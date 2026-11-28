using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace DwemerDistro.Launcher.Wpf.Services;

/// <summary>An installed Windows companion generation whose distro.txt names this launcher's distro.</summary>
public sealed record EchoesCompanion(string Directory, string Executable, string? Version);

/// <summary>Result of scanning the companion folder. OtherDistro is set only when no generation matches.</summary>
public sealed record EchoesCompanionScan(EchoesCompanion? Match, string? OtherDistro);

/// <summary>A validated Mirthwood: Echoes server package folder (the one Update-Server.ps1 installs from).</summary>
public sealed record EchoesPackage(string Root, string Version);

/// <summary>A published (never draft) GitHub release carrying the server package asset.</summary>
public sealed record EchoesRelease(string Tag, string Version, string AssetName, string DownloadUrl, long Size, string? Sha256);

/// <summary>
/// Installs Mirthwood: Echoes through its own package installer and opens its Windows companion.
/// The companion owns the Echoes services: it starts them while its window is open and stops them on
/// minimise or close, so nothing here ever starts or stops <c>mirthwood-echoes.service</c> itself.
/// Release discovery reads only the public latest-release endpoint, so a draft release is never seen
/// and no credentials are involved.
/// </summary>
public sealed class MirthwoodEchoesService(ProcessRunner processRunner, WslService wsl)
{
    public const string ProductName = "Mirthwood: Echoes";
    public const string ModId = "mirthwood-echoes";
    public const int SteamAppId = 2272900;
    internal const string GameExecutable = "Mirthwood.exe";
    internal const string CompanionExecutable = "Mirthwood.Echoes.Play.exe";
    internal const string CompanionProcessName = "Mirthwood.Echoes.Play";
    internal const string LatestReleaseApiUrl = "https://api.github.com/repos/Dwemer-Dynamics/mirthwood-echoes/releases/latest";
    internal const string ReleaseDownloadPrefix = "https://github.com/Dwemer-Dynamics/mirthwood-echoes/releases/download/";
    internal const string DotNetDownloadUrl = "https://dotnet.microsoft.com/download/dotnet/10.0";
    internal const string WebView2DownloadUrl = "https://developer.microsoft.com/microsoft-edge/webview2/";
    private const long DownloadLimit = 2L * 1024 * 1024 * 1024;
    private const long ExtractLimit = 6L * 1024 * 1024 * 1024;
    private const int EntryLimit = 50000;
    private const int MetadataLimit = 4 * 1024 * 1024;
    private const string WebView2ClientKey = @"Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}";

    private static readonly Regex GenerationPattern = new("^[0-9A-F]{24}$", RegexOptions.CultureInvariant);
    private static readonly Regex VersionPattern = new(@"^\d+\.\d+\.\d+$", RegexOptions.CultureInvariant);
    private static readonly Regex TagPattern = new(@"^v?(\d+\.\d+\.\d+)$", RegexOptions.CultureInvariant);
    private static readonly Regex ReservedName = new("^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(\\..*)?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>Files Update-Server.ps1 and install-service.sh require, relative to the package root.</summary>
    internal static readonly string[] RequiredPackageFiles =
    [
        "Update-Server.ps1",
        "install-service.sh",
        "upgrade-identity.py",
        "app/Mirthwood.Echoes.Server.dll",
        "app/web-control.py",
        "app/bundle/manifest.json",
        "launcher/" + CompanionExecutable,
        "launcher/mirthwood-echoes.ico"
    ];

    // Read-only: reports what install-service.sh would refuse, plus the runtime it needs, and changes nothing.
    // A raw literal keeps the source file's line endings; a CRLF checkout (core.autocrlf) would hand
    // /bin/sh "do\r" and the probe would never reach its sentinel, so the script is normalised to LF.
    internal static readonly string LinuxPrerequisiteScript = """
        [ -d /run/systemd/system ] || echo "MISSING systemd"
        for tool in dotnet psql curl python3 flock sudo visudo systemd-run runuser useradd sha256sum; do
            command -v "$tool" >/dev/null 2>&1 || echo "MISSING $tool"
        done
        if [ -x /usr/bin/dotnet ]; then
            env DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 /usr/bin/dotnet --list-runtimes 2>/dev/null | grep -q '^Microsoft\.AspNetCore\.App 10\.' || echo "MISSING aspnetcore-10"
        else
            echo "MISSING usr-bin-dotnet"
        fi
        [ -d /run/systemd/system ] && { systemctl cat postgresql.service >/dev/null 2>&1 || echo "MISSING postgresql-service"; }
        echo "ECHOES_PREREQUISITES_CHECKED"
        """.ReplaceLineEndings("\n");

    /// <summary>A cold distro start can take a while; a hung one must not hold the panel busy forever.</summary>
    internal static readonly TimeSpan LinuxPrerequisiteTimeout = TimeSpan.FromMinutes(2);
    private const int DiagnosticLimit = 300;

    public static string DefaultPlayRoot =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MirthwoodEchoes", "Play");

    public static string StagingRoot =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DwemerDistro", "MirthwoodEchoes");

    // --- Companion -------------------------------------------------------------------------

    /// <summary>
    /// Finds the newest installer-created generation for <paramref name="distro"/>. Each install rewrites
    /// distro.txt, so its timestamp marks the generation the desktop shortcut was last pointed at.
    /// </summary>
    internal static EchoesCompanionScan ScanCompanions(string distro, string playRoot)
    {
        if (!Directory.Exists(playRoot) || IsReparsePoint(playRoot))
        {
            return new EchoesCompanionScan(null, null);
        }

        EchoesCompanion? best = null;
        var bestStamp = DateTime.MinValue;
        string? other = null;
        IEnumerable<string> directories;
        try
        {
            directories = Directory.EnumerateDirectories(playRoot).ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new EchoesCompanionScan(null, null);
        }

        foreach (var directory in directories)
        {
            var executable = Path.Combine(directory, CompanionExecutable);
            var marker = Path.Combine(directory, "distro.txt");
            try
            {
                if (!GenerationPattern.IsMatch(Path.GetFileName(directory)) || IsReparsePoint(directory) ||
                    !File.Exists(executable) || !File.Exists(marker) || new FileInfo(marker).Length > 256)
                {
                    continue;
                }

                var owner = File.ReadAllText(marker).Trim();
                if (!string.Equals(owner, distro, StringComparison.OrdinalIgnoreCase))
                {
                    other ??= DisplayName(owner);
                    continue;
                }

                var stamp = File.GetLastWriteTimeUtc(marker);
                if (best is null || stamp > bestStamp)
                {
                    best = new EchoesCompanion(directory, executable, ReadVersion(executable));
                    bestStamp = stamp;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }

        return new EchoesCompanionScan(best, best is null ? other : null);
    }

    public EchoesCompanionScan ScanCompanions() => ScanCompanions(wsl.DistroName, DefaultPlayRoot);

    public static bool IsCompanionRunning() => RunningCompanionDistros().Count > 0;

    /// <summary>
    /// The distro each open companion serves, read from the distro.txt beside its executable; null when
    /// that cannot be read. The companion is single-instance, so an open window for another distro
    /// would swallow a new start.
    /// </summary>
    public static IReadOnlyList<string?> RunningCompanionDistros()
    {
        var distros = new List<string?>();
        foreach (var process in Process.GetProcessesByName(CompanionProcessName))
        {
            using (process)
            {
                try
                {
                    var executable = process.MainModule?.FileName;
                    var marker = executable is null ? null : Path.Combine(Path.GetDirectoryName(executable)!, "distro.txt");
                    distros.Add(marker is not null && File.Exists(marker) && new FileInfo(marker).Length <= 256
                        ? File.ReadAllText(marker).Trim()
                        : null);
                }
                catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException or UnauthorizedAccessException)
                {
                    distros.Add(null);
                }
            }
        }

        return distros;
    }

    /// <summary>
    /// Starts the companion. It is single-instance: a second start only brings the open window forward,
    /// so the services it owns are never started twice.
    /// </summary>
    public static void LaunchCompanion(EchoesCompanion companion)
    {
        var startInfo = new ProcessStartInfo(companion.Executable)
        {
            UseShellExecute = false,
            WorkingDirectory = companion.Directory
        };
        using var _ = Process.Start(startInfo) ?? throw new InvalidOperationException("Windows did not start the Mirthwood: Echoes companion.");
    }

    private static string? ReadVersion(string executable)
    {
        var version = FileVersionInfo.GetVersionInfo(executable).ProductVersion?.Split('+')[0].Trim();
        return version is not null && VersionPattern.IsMatch(version) ? version : null;
    }

    // --- Windows prerequisites --------------------------------------------------------------

    /// <summary>Single Steam library folder that holds Mirthwood, or null when none or several qualify.</summary>
    public static string? FindGameDirectory()
    {
        if (Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) is not string steam ||
            string.IsNullOrWhiteSpace(steam))
        {
            return null;
        }

        var libraries = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Path.GetFullPath(steam) };
        var folders = Path.Combine(steam, "steamapps", "libraryfolders.vdf");
        try
        {
            if (File.Exists(folders) && new FileInfo(folders).Length < MetadataLimit)
            {
                foreach (Match match in Regex.Matches(File.ReadAllText(folders), "\"path\"\\s+\"([^\"]+)\""))
                {
                    libraries.Add(Path.GetFullPath(match.Groups[1].Value.Replace("\\\\", "\\")));
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
        }

        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var library in libraries)
        {
            try
            {
                var installDirectory = "Mirthwood";
                var manifest = Path.Combine(library, "steamapps", $"appmanifest_{SteamAppId}.acf");
                if (File.Exists(manifest) && new FileInfo(manifest).Length < MetadataLimit)
                {
                    var match = Regex.Match(File.ReadAllText(manifest), "\"installdir\"\\s+\"([^\"\\\\/]+)\"");
                    if (match.Success)
                    {
                        installDirectory = match.Groups[1].Value;
                    }
                }

                var candidate = Path.Combine(library, "steamapps", "common", installDirectory);
                if (IsValidGameDirectory(candidate))
                {
                    found.Add(Path.GetFullPath(candidate));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
            }
        }

        return found.Count == 1 ? found.First() : null;
    }

    /// <summary>The installer converts paths to /mnt/&lt;drive&gt;, so only local drive-letter folders qualify.</summary>
    internal static bool IsLocalDrivePath(string path) =>
        Regex.IsMatch(path, @"^[A-Za-z]:\\", RegexOptions.CultureInvariant) && Path.IsPathFullyQualified(path);

    internal static bool IsValidGameDirectory(string? path) =>
        !string.IsNullOrWhiteSpace(path) && IsLocalDrivePath(path) && File.Exists(Path.Combine(path, GameExecutable));

    /// <summary>Missing Windows runtimes the companion needs. Each entry is a complete, actionable sentence.</summary>
    public static IReadOnlyList<string> CheckWindowsPrerequisites()
    {
        var missing = new List<string>();
        if (!HasWindowsDesktopRuntime10())
        {
            missing.Add($"The .NET 10 Desktop Runtime (x64) is not installed on Windows. Install it from {DotNetDownloadUrl}.");
        }

        if (!HasWebView2Runtime())
        {
            missing.Add($"The Microsoft Edge WebView2 Runtime is not installed. Install the Evergreen runtime from {WebView2DownloadUrl}.");
        }

        return missing;
    }

    private static bool HasWindowsDesktopRuntime10()
    {
        var root = Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\dotnet\Setup\InstalledVersions\x64", "InstallLocation", null) as string;
        if (string.IsNullOrWhiteSpace(root))
        {
            root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet");
        }

        try
        {
            var framework = Path.Combine(root, "shared", "Microsoft.WindowsDesktop.App");
            return Directory.Exists(framework) &&
                   Directory.EnumerateDirectories(framework, "10.*").Any(path => File.Exists(Path.Combine(path, "Microsoft.WindowsDesktop.App.deps.json")));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

    private static bool HasWebView2Runtime()
    {
        foreach (var key in new[]
                 {
                     @"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\" + WebView2ClientKey,
                     @"HKEY_LOCAL_MACHINE\SOFTWARE\" + WebView2ClientKey,
                     @"HKEY_CURRENT_USER\Software\" + WebView2ClientKey
                 })
        {
            if (Registry.GetValue(key, "pv", null) is string version && !string.IsNullOrWhiteSpace(version) && version != "0.0.0.0")
            {
                return true;
            }
        }

        return false;
    }

    // --- Distro prerequisites ---------------------------------------------------------------

    /// <summary>Runs the read-only distro probe. The probe runs as root because the installer does.</summary>
    public async Task<IReadOnlyList<string>> CheckLinuxPrerequisitesAsync(CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(LinuxPrerequisiteTimeout);
        Models.CommandResult result;
        try
        {
            result = await wsl.RunWslAsync(
                    ["-d", wsl.DistroName, "-u", "root", "--exec", "/bin/sh", "-c", LinuxPrerequisiteScript],
                    null,
                    timeout.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return [$"The {wsl.DistroName} distro did not answer the prerequisite check within {LinuxPrerequisiteTimeout.TotalMinutes:0} minutes. " +
                    "Make sure DwemerDistro starts, then check again."];
        }

        return ParseLinuxPrerequisites(result, wsl.DistroName);
    }

    /// <summary>
    /// A failed probe never passes, even when its sentinel was printed: the exit code decides first. The
    /// probe prints only tool names, so its output carries no secrets; the excerpt is still bounded.
    /// </summary>
    internal static IReadOnlyList<string> ParseLinuxPrerequisites(Models.CommandResult result, string distro)
    {
        if (!result.Succeeded)
        {
            var detail = DescribeProbeFailure(result.StandardError + "\n" + result.StandardOutput);
            return [$"The {distro} distro could not be checked (exit code {result.ExitCode}{(detail.Length == 0 ? string.Empty : ": " + detail)}). " +
                    "Make sure DwemerDistro is installed and starts, then check again."];
        }

        return ParseLinuxPrerequisites(result.StandardOutput, distro);
    }

    /// <summary>The last few meaningful output lines on one line, without NUL or control characters, bounded.</summary>
    internal static string DescribeProbeFailure(string? output)
    {
        var lines = (output ?? string.Empty)
            .Split('\n')
            .Select(line => new string(line.Where(character => !char.IsControl(character)).ToArray()).Trim())
            .Where(line => line.Length > 0 && line != "ECHOES_PREREQUISITES_CHECKED")
            .TakeLast(3);
        var text = string.Join(" | ", lines);
        return text.Length <= DiagnosticLimit ? text : text[..DiagnosticLimit] + "…";
    }

    internal static IReadOnlyList<string> ParseLinuxPrerequisites(string? output, string distro)
    {
        var lines = (output ?? string.Empty).Split('\n').Select(line => line.Trim()).ToArray();
        if (!lines.Contains("ECHOES_PREREQUISITES_CHECKED"))
        {
            return [$"The {distro} distro could not be checked. Make sure DwemerDistro is installed and starts, then check again."];
        }

        var missing = lines
            .Where(line => line.StartsWith("MISSING ", StringComparison.Ordinal))
            .Select(line => line["MISSING ".Length..])
            .ToHashSet(StringComparer.Ordinal);
        var messages = new List<string>();
        if (missing.Contains("systemd"))
        {
            messages.Add($"systemd is not running in {distro}. Mirthwood: Echoes runs its server as a systemd service.");
        }

        if (missing.Contains("dotnet") || missing.Contains("usr-bin-dotnet") || missing.Contains("aspnetcore-10"))
        {
            messages.Add($"The .NET 10 ASP.NET Core Runtime is not installed at /usr/bin/dotnet in {distro}. " +
                         "DwemerDistro does not install it yet: install Microsoft's aspnetcore-runtime-10.0 package for Debian in the distro, then check again.");
        }

        if (missing.Contains("psql") || missing.Contains("postgresql-service"))
        {
            messages.Add($"PostgreSQL is not available in {distro}. Run Update Distro in Settings, then check again.");
        }

        foreach (var tool in missing.Where(token => token is not ("systemd" or "dotnet" or "usr-bin-dotnet" or "aspnetcore-10" or "psql" or "postgresql-service")).Order(StringComparer.Ordinal))
        {
            messages.Add($"{distro} is missing the installer tool '{DisplayName(tool)}'. Run Update Distro in Settings, then check again.");
        }

        return messages;
    }

    // --- Published release ------------------------------------------------------------------

    /// <summary>
    /// The latest published release with a server package, or null when the repository has none. GitHub's
    /// latest endpoint never returns drafts or prereleases; both are re-checked anyway.
    /// </summary>
    public static async Task<EchoesRelease?> GetLatestReleaseAsync(HttpClient http, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, LatestReleaseApiUrl);
        request.Headers.UserAgent.ParseAdd("DwemerDistro-Launcher/" + LauncherConstants.LauncherVersion);
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
        {
            throw new HttpRequestException("GitHub is limiting requests. Try again later.");
        }

        response.EnsureSuccessStatusCode();
        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        if (bytes.Length > MetadataLimit)
        {
            throw new InvalidDataException("The release description is too large.");
        }

        return ParseRelease(bytes);
    }

    internal static EchoesRelease? ParseRelease(byte[] json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.TryGetProperty("draft", out var draft) && draft.ValueKind != JsonValueKind.False ||
            root.TryGetProperty("prerelease", out var prerelease) && prerelease.ValueKind != JsonValueKind.False)
        {
            return null;
        }

        var tag = root.TryGetProperty("tag_name", out var tagValue) && tagValue.ValueKind == JsonValueKind.String ? tagValue.GetString()! : string.Empty;
        var tagMatch = TagPattern.Match(tag);
        if (!tagMatch.Success)
        {
            throw new InvalidDataException("The latest Mirthwood: Echoes release has an unsupported tag.");
        }

        var version = tagMatch.Groups[1].Value;
        var expectedName = $"Mirthwood-Echoes-Server-{version}.zip";
        if (!root.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var asset in assets.EnumerateArray())
        {
            if (!asset.TryGetProperty("name", out var name) || name.ValueKind != JsonValueKind.String || name.GetString() != expectedName)
            {
                continue;
            }

            var url = asset.TryGetProperty("browser_download_url", out var urlValue) && urlValue.ValueKind == JsonValueKind.String ? urlValue.GetString() : null;
            if (url != ReleaseDownloadPrefix + tag + "/" + expectedName)
            {
                throw new InvalidDataException("The Mirthwood: Echoes release asset has an unexpected download address.");
            }

            var size = asset.TryGetProperty("size", out var sizeValue) && sizeValue.TryGetInt64(out var parsedSize) ? parsedSize : 0;
            if (size <= 0 || size > DownloadLimit)
            {
                throw new InvalidDataException("The Mirthwood: Echoes release asset has an invalid size.");
            }

            string? sha256 = null;
            if (asset.TryGetProperty("digest", out var digest) && digest.ValueKind == JsonValueKind.String &&
                digest.GetString() is { } digestText && digestText.StartsWith("sha256:", StringComparison.Ordinal))
            {
                sha256 = digestText["sha256:".Length..].ToLowerInvariant();
                if (!Regex.IsMatch(sha256, "^[a-f0-9]{64}$", RegexOptions.CultureInvariant))
                {
                    throw new InvalidDataException("The Mirthwood: Echoes release asset has an invalid checksum.");
                }
            }

            return new EchoesRelease(tag, version, expectedName, url, size, sha256);
        }

        return null;
    }

    /// <summary>Downloads the release asset from GitHub hosts only, checking its size and published digest.</summary>
    public static async Task<string> DownloadReleaseAsync(HttpClient http, EchoesRelease release, string directory, CancellationToken cancellationToken = default)
    {
        var path = Path.Combine(directory, release.AssetName);
        var address = release.DownloadUrl;
        for (var redirect = 0; redirect < 6; redirect++)
        {
            var uri = new Uri(address);
            if (uri.Scheme != Uri.UriSchemeHttps || !uri.IsDefaultPort || uri.UserInfo.Length != 0 ||
                uri.Host is not ("github.com" or "objects.githubusercontent.com" or "release-assets.githubusercontent.com"))
            {
                throw new InvalidDataException("The Mirthwood: Echoes package must download from GitHub.");
            }

            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.UserAgent.ParseAdd("DwemerDistro-Launcher/" + LauncherConstants.LauncherVersion);
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if ((int)response.StatusCode is >= 300 and < 400 && response.Headers.Location is { } location)
            {
                address = new Uri(uri, location).AbsoluteUri;
                continue;
            }

            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is { } length && length != release.Size)
            {
                throw new InvalidDataException("The Mirthwood: Echoes package size differs from its release.");
            }

            await using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous))
            await using (var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
            {
                var buffer = new byte[65536];
                long total = 0;
                int read;
                while ((read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) != 0)
                {
                    total += read;
                    if (total > release.Size)
                    {
                        throw new InvalidDataException("The Mirthwood: Echoes package is larger than its release.");
                    }

                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                }

                if (total != release.Size)
                {
                    throw new InvalidDataException("The Mirthwood: Echoes package download was incomplete.");
                }
            }

            if (release.Sha256 is not null)
            {
                await using var verify = File.OpenRead(path);
                var hash = Convert.ToHexString(await SHA256.HashDataAsync(verify, cancellationToken).ConfigureAwait(false)).ToLowerInvariant();
                if (hash != release.Sha256)
                {
                    throw new InvalidDataException("The Mirthwood: Echoes package failed its SHA-256 check.");
                }
            }

            return path;
        }

        throw new HttpRequestException("Too many GitHub redirects.");
    }

    // --- Package validation -----------------------------------------------------------------

    /// <summary>Creates a fresh, empty staging folder owned by this operation.</summary>
    public static string CreateStagingDirectory()
    {
        var path = Path.Combine(StagingRoot, "stage-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>Removes only a staging folder this service created; never follows a link.</summary>
    public static void DeleteStagingDirectory(string? path)
    {
        if (path is null || !Directory.Exists(path) || IsReparsePoint(path) ||
            !string.Equals(Path.GetDirectoryName(Path.GetFullPath(path)), Path.GetFullPath(StagingRoot), StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFileName(path).StartsWith("stage-", StringComparison.Ordinal))
        {
            return;
        }

        try
        {
            Directory.Delete(path, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LauncherLogService.Operation($"Mirthwood: Echoes staging folder was left for later cleanup: {ex.Message}");
        }
    }

    /// <summary>
    /// Extracts a package ZIP into an empty folder, refusing absolute, parent, device, link and duplicate
    /// entries, then validates the extracted layout and product identity.
    /// </summary>
    public static EchoesPackage ExtractPackage(string zipPath, string destination, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(destination);
        if (Directory.EnumerateFileSystemEntries(destination).Any())
        {
            throw new IOException("The package staging folder is not empty.");
        }

        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(destination)) + Path.DirectorySeparatorChar;
        using var archive = ZipFile.OpenRead(zipPath);
        if (archive.Entries.Count is 0 or > EntryLimit)
        {
            throw new InvalidDataException("The file is not a Mirthwood: Echoes server package.");
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        var buffer = new byte[65536];
        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var name = entry.FullName;
            if (!IsSafeEntryName(name) || !seen.Add(name.TrimEnd('/')))
            {
                throw new InvalidDataException("The package contains an unsafe or duplicate path and was not installed.");
            }

            var fileType = (entry.ExternalAttributes >> 16) & 0xF000;
            if (fileType is not (0 or 0x8000 or 0x4000))
            {
                throw new InvalidDataException("The package contains a link or special file and was not installed.");
            }

            var target = Path.GetFullPath(Path.Combine(root, name.Replace('/', Path.DirectorySeparatorChar)));
            if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("The package contains a path outside its folder and was not installed.");
            }

            if (name.EndsWith('/'))
            {
                Directory.CreateDirectory(target);
                continue;
            }

            total += entry.Length;
            if (total > ExtractLimit)
            {
                throw new InvalidDataException("The package is larger than expected and was not installed.");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            using var data = entry.Open();
            long written = 0;
            int read;
            while ((read = data.Read(buffer, 0, buffer.Length)) != 0)
            {
                written += read;
                if (written > entry.Length)
                {
                    throw new InvalidDataException("A package file is larger than its archive entry.");
                }

                output.Write(buffer, 0, read);
            }

            if (written != entry.Length)
            {
                throw new InvalidDataException("A package file is truncated.");
            }
        }

        return ValidatePackage(ResolvePackageRoot(destination));
    }

    /// <summary>The package may sit at the archive root or inside one top-level folder.</summary>
    internal static string ResolvePackageRoot(string extracted)
    {
        if (File.Exists(Path.Combine(extracted, "app", "bundle", "manifest.json")))
        {
            return extracted;
        }

        var directories = Directory.GetDirectories(extracted);
        if (directories.Length == 1 && Directory.GetFiles(extracted).Length == 0 &&
            File.Exists(Path.Combine(directories[0], "app", "bundle", "manifest.json")))
        {
            return directories[0];
        }

        throw new InvalidDataException("The file does not have the Mirthwood: Echoes server package layout.");
    }

    internal static bool IsSafeEntryName(string name)
    {
        if (name.Length is 0 or > 400 || name.StartsWith('/') || name.Contains('\\') || name.Contains(':') ||
            name.Any(char.IsControl))
        {
            return false;
        }

        var segments = name.EndsWith('/') ? name[..^1].Split('/') : name.Split('/');
        return segments.All(segment =>
            segment.Length is > 0 and <= 200 &&
            segment is not ("." or "..") &&
            !segment.EndsWith('.') && !segment.EndsWith(' ') &&
            segment.IndexOfAny(['<', '>', '"', '|', '?', '*']) < 0 &&
            !ReservedName.IsMatch(segment));
    }

    /// <summary>Checks the files the installer requires and the manifest's exact product identity.</summary>
    public static EchoesPackage ValidatePackage(string root)
    {
        root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        if (!IsLocalDrivePath(root + Path.DirectorySeparatorChar))
        {
            throw new InvalidDataException("Choose a Mirthwood: Echoes package on a local drive (for example C:\\).");
        }

        if (!Directory.Exists(root) || IsReparsePoint(root))
        {
            throw new InvalidDataException("The Mirthwood: Echoes package folder was not found.");
        }

        foreach (var relative in RequiredPackageFiles)
        {
            var path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(path) || IsReparsePoint(path))
            {
                throw new InvalidDataException($"The Mirthwood: Echoes package is incomplete: {relative} is missing.");
            }
        }

        var manifestPath = Path.Combine(root, "app", "bundle", "manifest.json");
        if (new FileInfo(manifestPath).Length > MetadataLimit)
        {
            throw new InvalidDataException("The Mirthwood: Echoes package manifest is too large.");
        }

        using var manifest = JsonDocument.Parse(File.ReadAllBytes(manifestPath));
        var id = ReadString(manifest.RootElement, "id");
        var name = ReadString(manifest.RootElement, "name");
        var version = ReadString(manifest.RootElement, "version");
        if (id != ModId || name != ProductName)
        {
            throw new InvalidDataException("This package does not identify itself as Mirthwood: Echoes.");
        }

        if (version is null || !VersionPattern.IsMatch(version))
        {
            throw new InvalidDataException("The Mirthwood: Echoes package has an invalid version.");
        }

        return new EchoesPackage(root, version);
    }

    // --- Installer --------------------------------------------------------------------------

    /// <summary>
    /// The package's own updater, always with this launcher's distro (its default names another distro)
    /// and the Steam folder chosen here. Saves keep the updater's default location.
    /// </summary>
    internal static string[] BuildInstallerArguments(EchoesPackage package, string distro, string gameDirectory) =>
    [
        "-NoLogo", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass",
        "-File", Path.Combine(package.Root, "Update-Server.ps1"),
        "-Package", package.Root,
        "-Distro", distro,
        "-GameDir", gameDirectory
    ];

    public Task<Models.CommandResult> RunInstallerAsync(
        EchoesPackage package,
        string gameDirectory,
        Action<string>? output = null,
        CancellationToken cancellationToken = default)
    {
        var powershell = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
        return processRunner.RunHiddenAsync(powershell, BuildInstallerArguments(package, wsl.DistroName, gameDirectory), output, cancellationToken);
    }

    // --- Helpers ----------------------------------------------------------------------------

    private static string? ReadString(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool IsReparsePoint(string path) =>
        (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;

    private static string DisplayName(string value)
    {
        var visible = new string(value.Where(character => !char.IsControl(character)).ToArray()).Trim();
        return visible.Length <= 64 ? visible : visible[..64];
    }
}
