using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DwemerDistro.Launcher.Wpf.Models;

namespace DwemerDistro.Launcher.Wpf.Services;

public sealed class PluginPackageBrokerService : IAsyncDisposable
{
    private const string ApiUrl = "http://127.0.0.1:8083/HerikaServer/ui/api/plugin_packages.php";
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly WslService _wsl;
    private readonly Action<string> _output;
    private readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromMinutes(3) };
    private readonly CancellationTokenSource _stop = new();
    private readonly HashSet<string> _deferredForMo2 = new(StringComparer.Ordinal);
    private Task? _worker;
    private string? _brokerToken;

    public PluginPackageBrokerService(WslService wsl, Action<string> output)
    {
        _wsl = wsl;
        _output = output;
    }

    public void Start()
    {
        _worker ??= Task.Run(() => RunAsync(_stop.Token));
    }

    public async Task StopAsync()
    {
        _stop.Cancel();
        if (_worker is null)
        {
            return;
        }
        try
        {
            await _worker.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
    }

    public async Task<int> RunOnceAsync(CancellationToken cancellationToken = default)
    {
        _brokerToken ??= await GetBrokerTokenAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(_brokerToken))
        {
            return 0;
        }
        var pending = await GetPendingJobsAsync(cancellationToken).ConfigureAwait(false);
        foreach (var job in pending)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await ProcessJobAsync(job, cancellationToken).ConfigureAwait(false);
        }
        return pending.Count;
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _stop.Dispose();
        _httpClient.Dispose();
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await RunOnceAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (HttpRequestException)
            {
                // HerikaServer can be stopped while the launcher remains open.
            }
            catch (Exception error)
            {
                LauncherLogService.Startup("Unified plugin broker poll failed.", error);
            }

            await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<string?> GetBrokerTokenAsync(CancellationToken cancellationToken)
    {
        const string tokenPath = "/var/www/html/HerikaServer/data/plugin_packages/broker_token";
        var result = await _wsl.RunDistroAsUserAsync("root", ["cat", tokenPath], cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            using var initializeRequest = new HttpRequestMessage(HttpMethod.Get, ApiUrl + "?action=pending");
            initializeRequest.Headers.Add("X-Dwemer-Plugin-Token", "initialize");
            try
            {
                using var ignored = await _httpClient.SendAsync(initializeRequest, cancellationToken).ConfigureAwait(false);
            }
            catch (HttpRequestException)
            {
                return null;
            }
            result = await _wsl.RunDistroAsUserAsync("root", ["cat", tokenPath], cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        if (!result.Succeeded)
        {
            return null;
        }
        var token = result.StandardOutput.Trim();
        return token.Length == 64 && token.All(Uri.IsHexDigit) ? token : null;
    }

    private async Task<IReadOnlyList<PendingPackageJob>> GetPendingJobsAsync(CancellationToken cancellationToken)
    {
        using var request = BrokerRequest(HttpMethod.Get, ApiUrl + "?action=pending");
        using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
        {
            _brokerToken = null;
            return [];
        }
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false));
        var jobs = new List<PendingPackageJob>();
        if (!document.RootElement.TryGetProperty("jobs", out var jobArray) || jobArray.ValueKind != JsonValueKind.Array)
        {
            return jobs;
        }
        foreach (var element in jobArray.EnumerateArray())
        {
            var id = element.GetProperty("id").GetString();
            if (!string.IsNullOrWhiteSpace(id))
            {
                jobs.Add(new PendingPackageJob(id));
            }
        }
        return jobs;
    }

    private async Task ProcessJobAsync(PendingPackageJob pending, CancellationToken cancellationToken)
    {
        if (Mo2IsRunning() && !string.Equals(Environment.GetEnvironmentVariable("DWEMER_PLUGIN_BROKER_ALLOW_MO2_RUNNING"), "1", StringComparison.Ordinal))
        {
            if (_deferredForMo2.Add(pending.Id))
            {
                _output($"Unified plugin package is ready. Close Mod Organizer 2 to install it safely.{Environment.NewLine}");
            }
            return;
        }

        ClaimedPackageJob? claimed = null;
        GameInstallTransaction? transaction = null;
        try
        {
            claimed = await ClaimJobAsync(pending.Id, cancellationToken).ConfigureAwait(false);
            _output($"Installing unified plugin package {claimed.PackageName} {claimed.Version}...{Environment.NewLine}");

            var packagePath = await DownloadPackageAsync(claimed.Id, cancellationToken).ConfigureAwait(false);
            transaction = InstallGameComponent(packagePath, claimed, cancellationToken);
            var result = transaction.Result;
            var completed = await ReportCompletionAsync(claimed, true, result, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(completed.Status, "completed", StringComparison.Ordinal))
            {
                transaction.Rollback();
                await TryReportRollbackAsync(claimed.Id, result, completed.Error, cancellationToken).ConfigureAwait(false);
                claimed = null;
                throw new InvalidOperationException(completed.Error ?? "HerikaServer could not activate the server component.");
            }

            transaction.Commit();
            _output($"Installed {claimed.PackageName} {claimed.Version} into MO2 profile '{result.Profile}'.{Environment.NewLine}");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            transaction?.Rollback();
            throw;
        }
        catch (Exception error)
        {
            transaction?.Rollback();
            LauncherLogService.Startup($"Unified plugin job {pending.Id} failed.", error);
            _output($"Unified plugin install failed: {error.Message}{Environment.NewLine}");
            if (claimed is not null)
            {
                await TryReportFailureAsync(claimed, error, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task<ClaimedPackageJob> ClaimJobAsync(string jobId, CancellationToken cancellationToken)
    {
        using var request = BrokerRequest(HttpMethod.Post, ApiUrl + "?action=claim");
        request.Content = JsonContent.Create(new { job_id = jobId });
        using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false));
        var job = document.RootElement.GetProperty("job");
        return new ClaimedPackageJob(
            job.GetProperty("id").GetString()!,
            job.GetProperty("claim_token").GetString()!,
            job.GetProperty("package_id").GetString()!,
            job.GetProperty("package_name").GetString()!,
            job.GetProperty("version").GetString()!);
    }

    private async Task<string> DownloadPackageAsync(string jobId, CancellationToken cancellationToken)
    {
        var downloadRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DwemerDistro",
            "PluginPackages");
        Directory.CreateDirectory(downloadRoot);
        var destination = Path.Combine(downloadRoot, jobId + ".dwpkg");

        using var request = BrokerRequest(HttpMethod.Get, ApiUrl + "?action=download&job_id=" + Uri.EscapeDataString(jobId));
        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true);
        await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
        return destination;
    }

    private GameInstallTransaction InstallGameComponent(string packagePath, ClaimedPackageJob claimed, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var archive = ZipFile.OpenRead(packagePath);
        var package = ValidatePackage(archive, claimed);
        var settings = ResolveSettings(package.GameVariants.Keys);
        var payloadPrefix = package.GameVariants[settings.GameVariant].TrimEnd('/') + "/";
        var modsRoot = Path.Combine(settings.Mo2Root!, "mods");
        var profilesRoot = Path.Combine(settings.Mo2Root!, "profiles");
        var profileRoot = Path.Combine(profilesRoot, settings.Profile!);
        var modListPath = Path.Combine(profileRoot, "modlist.txt");
        if (!Directory.Exists(modsRoot) || !Directory.Exists(profileRoot))
        {
            throw new InvalidOperationException("The configured Mod Organizer 2 root or profile no longer exists.");
        }

        var transactionRoot = Path.Combine(modsRoot, ".dwemer-plugin-transactions", claimed.Id);
        var stageRoot = Path.Combine(transactionRoot, "stage");
        var backupRoot = Path.Combine(transactionRoot, "backup");
        var targetRoot = Path.Combine(modsRoot, package.ModName);
        var modListBackup = Path.Combine(transactionRoot, "modlist.txt.backup");
        RemoveDirectory(transactionRoot);
        Directory.CreateDirectory(stageRoot);

        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var normalized = NormalizeArchivePath(entry.FullName);
            if (!normalized.StartsWith(payloadPrefix, StringComparison.Ordinal) || normalized.Length == payloadPrefix.Length)
            {
                continue;
            }
            var relative = normalized[payloadPrefix.Length..];
            var destination = SafeDestination(stageRoot, relative);
            if (entry.FullName.EndsWith("/", StringComparison.Ordinal))
            {
                Directory.CreateDirectory(destination);
                continue;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            entry.ExtractToFile(destination, overwrite: true);
        }
        if (!Directory.EnumerateFiles(stageRoot, "*", SearchOption.AllDirectories).Any())
        {
            throw new InvalidOperationException("The selected game variant has no files.");
        }

        PreserveMutablePaths(targetRoot, stageRoot, package.MutablePaths);
        var marker = new
        {
            schema_version = 1,
            package_id = claimed.PackageId,
            version = claimed.Version,
            job_id = claimed.Id,
            installed_at = DateTimeOffset.UtcNow
        };
        File.WriteAllText(Path.Combine(stageRoot, ".dwemer-plugin.json"), JsonSerializer.Serialize(marker, JsonOptions));

        Directory.CreateDirectory(transactionRoot);
        if (Directory.Exists(targetRoot))
        {
            Directory.Move(targetRoot, backupRoot);
        }
        try
        {
            Directory.Move(stageRoot, targetRoot);
            File.Copy(modListPath, modListBackup, overwrite: true);
            EnableMod(modListPath, package.ModName);
        }
        catch
        {
            if (Directory.Exists(targetRoot)) RemoveDirectory(targetRoot);
            if (Directory.Exists(backupRoot)) Directory.Move(backupRoot, targetRoot);
            if (File.Exists(modListBackup)) File.Copy(modListBackup, modListPath, overwrite: true);
            throw;
        }

        var result = new GameInstallResult(
            settings.Mo2Root!,
            settings.Profile!,
            settings.GameVariant,
            package.ModName,
            targetRoot,
            BuildFileLedger(targetRoot));
        return new GameInstallTransaction(result, targetRoot, backupRoot, modListPath, modListBackup, transactionRoot);
    }

    private static ValidatedGamePackage ValidatePackage(ZipArchive archive, ClaimedPackageJob claimed)
    {
        var entries = new Dictionary<string, ZipArchiveEntry>(StringComparer.Ordinal);
        long totalBytes = 0;
        foreach (var entry in archive.Entries)
        {
            var path = NormalizeArchivePath(entry.FullName);
            totalBytes += entry.Length;
            if (archive.Entries.Count > 5000 || totalBytes > 1073741824L)
            {
                throw new InvalidDataException("Package exceeds launcher safety limits.");
            }
            if (((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000)
            {
                throw new InvalidDataException($"Package entry '{path}' is a symbolic link.");
            }
            if (!entries.TryAdd(path, entry))
            {
                throw new InvalidDataException($"Package contains duplicate path '{path}'.");
            }
        }
        if (!entries.TryGetValue("manifest.json", out var manifestEntry) || !entries.TryGetValue("checksums.sha256", out var checksumsEntry))
        {
            throw new InvalidDataException("Package is missing manifest.json or checksums.sha256.");
        }

        using var manifestDocument = JsonDocument.Parse(ReadEntry(manifestEntry));
        var manifest = manifestDocument.RootElement;
        if (manifest.GetProperty("schema_version").GetInt32() != 3 ||
            !string.Equals(manifest.GetProperty("package_id").GetString(), claimed.PackageId, StringComparison.Ordinal) ||
            !string.Equals(manifest.GetProperty("version").GetString(), claimed.Version, StringComparison.Ordinal))
        {
            throw new InvalidDataException("Downloaded package identity does not match the claimed job.");
        }

        VerifyChecksums(entries, ReadEntry(checksumsEntry));
        var game = manifest.GetProperty("components").GetProperty("game");
        var modName = game.GetProperty("mod_name").GetString() ?? string.Empty;
        ValidateModName(modName);
        var variants = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var variant in game.GetProperty("variants").EnumerateObject())
        {
            if (variant.Name is not ("skyrim-se" or "skyrim-vr"))
            {
                throw new InvalidDataException($"Unsupported game variant '{variant.Name}'.");
            }
            var prefix = NormalizeArchivePath(variant.Value.GetString()!);
            if (!prefix.StartsWith("game/", StringComparison.Ordinal))
            {
                throw new InvalidDataException("Game payload paths must be below game/.");
            }
            variants[variant.Name] = prefix;
        }
        var mutable = game.TryGetProperty("mutable_paths", out var mutableElement)
            ? mutableElement.EnumerateArray().Select(value => NormalizeArchivePath(value.GetString()!)).ToArray()
            : [];
        return new ValidatedGamePackage(modName, variants, mutable);
    }

    private static void ValidateModName(string modName)
    {
        if (string.IsNullOrWhiteSpace(modName) || modName != modName.Trim() || modName is "." or ".." || modName.EndsWith('.'))
        {
            throw new InvalidDataException("Game component mod_name is not a safe MO2 folder name.");
        }
        if (modName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            throw new InvalidDataException("Game component mod_name contains invalid filename characters.");
        }
        var deviceName = modName.Split('.', 2)[0];
        if (deviceName.Equals("CON", StringComparison.OrdinalIgnoreCase) ||
            deviceName.Equals("PRN", StringComparison.OrdinalIgnoreCase) ||
            deviceName.Equals("AUX", StringComparison.OrdinalIgnoreCase) ||
            deviceName.Equals("NUL", StringComparison.OrdinalIgnoreCase) ||
            (deviceName.Length == 4 && (deviceName.StartsWith("COM", StringComparison.OrdinalIgnoreCase) || deviceName.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) && deviceName[3] is >= '1' and <= '9'))
        {
            throw new InvalidDataException("Game component mod_name is reserved by Windows.");
        }
    }

    private static void VerifyChecksums(IReadOnlyDictionary<string, ZipArchiveEntry> entries, string checksumFile)
    {
        var expected = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var rawLine in checksumFile.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var separator = rawLine.IndexOf("  ", StringComparison.Ordinal);
            if (separator != 64)
            {
                throw new InvalidDataException("checksums.sha256 contains an invalid line.");
            }
            var hash = rawLine[..64].ToLowerInvariant();
            var path = NormalizeArchivePath(rawLine[(separator + 2)..].TrimStart('*'));
            if (hash.Length != 64 || !hash.All(Uri.IsHexDigit) || path == "checksums.sha256" || !expected.TryAdd(path, hash))
            {
                throw new InvalidDataException("checksums.sha256 contains an invalid entry.");
            }
        }
        foreach (var (path, entry) in entries)
        {
            if (entry.FullName.EndsWith("/", StringComparison.Ordinal) || path == "checksums.sha256") continue;
            if (!expected.Remove(path, out var expectedHash))
            {
                throw new InvalidDataException($"Package file '{path}' is not covered by checksums.sha256.");
            }
            using var stream = entry.Open();
            var actual = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
            if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(actual), Encoding.ASCII.GetBytes(expectedHash)))
            {
                throw new InvalidDataException($"Checksum mismatch for '{path}'.");
            }
        }
        if (expected.Count > 0)
        {
            throw new InvalidDataException($"Checksum references missing file '{expected.Keys.First()}'.");
        }
    }

    private PluginBrokerSettings ResolveSettings(IEnumerable<string> supportedVariants)
    {
        var supported = supportedVariants.ToHashSet(StringComparer.Ordinal);
        var settingsPath = SettingsPath();
        PluginBrokerSettings settings;
        try
        {
            settings = File.Exists(settingsPath)
                ? JsonSerializer.Deserialize<PluginBrokerSettings>(File.ReadAllText(settingsPath), JsonOptions) ?? new PluginBrokerSettings()
                : new PluginBrokerSettings();
        }
        catch (JsonException)
        {
            settings = new PluginBrokerSettings();
        }

        settings.Mo2Root = Environment.GetEnvironmentVariable("DWEMER_MO2_ROOT") ?? settings.Mo2Root;
        settings.Profile = Environment.GetEnvironmentVariable("DWEMER_MO2_PROFILE") ?? settings.Profile;
        settings.GameVariant = Environment.GetEnvironmentVariable("DWEMER_GAME_VARIANT") ?? settings.GameVariant;
        if (!supported.Contains(settings.GameVariant))
        {
            settings.GameVariant = supported.Count == 1 ? supported.Single() : throw new InvalidOperationException("Select a supported game variant in plugin-broker.json.");
        }
        if (string.IsNullOrWhiteSpace(settings.Mo2Root) || !Directory.Exists(Path.Combine(settings.Mo2Root, "mods")))
        {
            settings.Mo2Root = DiscoverMo2Root(settings.GameVariant);
        }
        if (string.IsNullOrWhiteSpace(settings.Mo2Root))
        {
            throw new InvalidOperationException("Could not find a Mod Organizer 2 instance. Set mo2Root in plugin-broker.json.");
        }
        if (string.IsNullOrWhiteSpace(settings.Profile) || !Directory.Exists(Path.Combine(settings.Mo2Root, "profiles", settings.Profile)))
        {
            settings.Profile = DiscoverProfile(settings.Mo2Root);
        }
        if (string.IsNullOrWhiteSpace(settings.Profile))
        {
            throw new InvalidOperationException("Could not select a Mod Organizer 2 profile. Set profile in plugin-broker.json.");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);
        File.WriteAllText(settingsPath, JsonSerializer.Serialize(settings, JsonOptions));
        return settings;
    }

    private static string? DiscoverMo2Root(string variant)
    {
        var preferred = variant == "skyrim-vr" ? @"C:\Modlists\Skyrim VR" : @"C:\Modlists\Skyrim AE";
        if (Directory.Exists(Path.Combine(preferred, "mods"))) return preferred;
        var candidates = new List<string>();
        foreach (var drive in DriveInfo.GetDrives().Where(drive => drive.IsReady && drive.DriveType == DriveType.Fixed))
        {
            var modlists = Path.Combine(drive.RootDirectory.FullName, "Modlists");
            if (!Directory.Exists(modlists)) continue;
            try
            {
                candidates.AddRange(Directory.EnumerateDirectories(modlists).Where(path => Directory.Exists(Path.Combine(path, "mods"))));
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
        return candidates.Distinct(StringComparer.OrdinalIgnoreCase).Count() == 1 ? candidates[0] : null;
    }

    private static bool Mo2IsRunning()
    {
        return Process.GetProcessesByName("ModOrganizer").Length > 0 || Process.GetProcessesByName("ModOrganizer2").Length > 0;
    }

    private static string? DiscoverProfile(string mo2Root)
    {
        var profilesRoot = Path.Combine(mo2Root, "profiles");
        var profiles = Directory.Exists(profilesRoot)
            ? Directory.EnumerateDirectories(profilesRoot)
                .Where(path => File.Exists(Path.Combine(path, "modlist.txt")))
                .Select(Path.GetFileName)
                .Where(name => !string.IsNullOrWhiteSpace(name) && !name.Contains("backup", StringComparison.OrdinalIgnoreCase))
                .ToArray()
            : [];
        return profiles.Length == 1 ? profiles[0] : null;
    }

    private static void EnableMod(string modListPath, string modName)
    {
        var encoding = DetectTextEncoding(modListPath);
        var lines = File.ReadAllLines(modListPath, encoding).ToList();
        var match = lines.FindIndex(line => line.Length > 1 && (line[0] == '+' || line[0] == '-') && string.Equals(line[1..], modName, StringComparison.OrdinalIgnoreCase));
        if (match >= 0)
        {
            lines[match] = "+" + modName;
        }
        else
        {
            lines.Add("+" + modName);
        }
        var temporary = modListPath + ".dwemer.tmp";
        File.WriteAllLines(temporary, lines, encoding);
        File.Move(temporary, modListPath, overwrite: true);
    }

    private static Encoding DetectTextEncoding(string path)
    {
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF) return new UTF8Encoding(true);
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE) return Encoding.Unicode;
        return new UTF8Encoding(false);
    }

    private static void PreserveMutablePaths(string oldRoot, string newRoot, IEnumerable<string> mutablePaths)
    {
        if (!Directory.Exists(oldRoot)) return;
        foreach (var mutable in mutablePaths)
        {
            var oldPath = SafeDestination(oldRoot, mutable);
            var newPath = SafeDestination(newRoot, mutable);
            if (File.Exists(oldPath))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(newPath)!);
                File.Copy(oldPath, newPath, overwrite: true);
            }
            else if (Directory.Exists(oldPath))
            {
                CopyDirectory(oldPath, newPath);
            }
        }
    }

    private async Task<JobCompletion> ReportCompletionAsync(ClaimedPackageJob job, bool success, GameInstallResult result, CancellationToken cancellationToken)
    {
        using var request = BrokerRequest(HttpMethod.Post, ApiUrl + "?action=complete");
        request.Content = JsonContent.Create(new
        {
            job_id = job.Id,
            claim_token = job.ClaimToken,
            success,
            result = new
            {
                mo2_root = result.Mo2Root,
                profile = result.Profile,
                game_variant = result.GameVariant,
                mod_name = result.ModName,
                install_path = result.InstallPath,
                files = result.Files
            }
        });
        using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false));
        var responseJob = document.RootElement.GetProperty("job");
        return new JobCompletion(
            responseJob.GetProperty("status").GetString()!,
            responseJob.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.String ? error.GetString() : null);
    }

    private async Task TryReportFailureAsync(ClaimedPackageJob job, Exception error, CancellationToken cancellationToken)
    {
        try
        {
            using var request = BrokerRequest(HttpMethod.Post, ApiUrl + "?action=complete");
            request.Content = JsonContent.Create(new
            {
                job_id = job.Id,
                claim_token = job.ClaimToken,
                success = false,
                result = new { error = error.Message }
            });
            using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception reportError)
        {
            LauncherLogService.Startup($"Could not report unified plugin job {job.Id} failure.", reportError);
        }
    }

    private async Task TryReportRollbackAsync(string jobId, GameInstallResult result, string? reason, CancellationToken cancellationToken)
    {
        try
        {
            using var request = BrokerRequest(HttpMethod.Post, ApiUrl + "?action=rollback");
            request.Content = JsonContent.Create(new { job_id = jobId, result = new { rolled_back = true, reason, mod_name = result.ModName } });
            using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception reportError)
        {
            LauncherLogService.Startup($"Could not report unified plugin job {jobId} rollback.", reportError);
        }
    }

    private HttpRequestMessage BrokerRequest(HttpMethod method, string url)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Add("X-Dwemer-Plugin-Token", _brokerToken);
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("DwemerDistro", LauncherConstants.LauncherVersion));
        return request;
    }

    private static string SettingsPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DwemerDistro",
        "plugin-broker.json");

    private static string NormalizeArchivePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Contains('\0') || path.Contains('\\') || path.StartsWith('/') || (path.Length > 1 && path[1] == ':'))
        {
            throw new InvalidDataException("Package contains an invalid archive path.");
        }
        var normalized = path.TrimEnd('/');
        if (normalized.Split('/').Any(segment => string.IsNullOrEmpty(segment) || segment is "." or ".."))
        {
            throw new InvalidDataException($"Package path '{path}' is unsafe.");
        }
        return normalized;
    }

    private static string SafeDestination(string root, string relative)
    {
        var normalized = NormalizeArchivePath(relative);
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var destination = Path.GetFullPath(Path.Combine(root, normalized.Replace('/', Path.DirectorySeparatorChar)));
        if (!destination.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"Package path '{relative}' leaves the install root.");
        }
        return destination;
    }

    private static string ReadEntry(ZipArchiveEntry entry)
    {
        using var reader = new StreamReader(entry.Open(), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    private static IReadOnlyDictionary<string, string> BuildFileLedger(string root)
    {
        return Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                path => Path.GetRelativePath(root, path).Replace('\\', '/'),
                path => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant(),
                StringComparer.Ordinal);
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source))
        {
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), overwrite: true);
        }
        foreach (var directory in Directory.EnumerateDirectories(source))
        {
            CopyDirectory(directory, Path.Combine(destination, Path.GetFileName(directory)));
        }
    }

    private static void RemoveDirectory(string path)
    {
        if (!Directory.Exists(path)) return;
        foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(path, recursive: true);
    }

    private sealed record PendingPackageJob(string Id);
    private sealed record ClaimedPackageJob(string Id, string ClaimToken, string PackageId, string PackageName, string Version);
    private sealed record ValidatedGamePackage(string ModName, IReadOnlyDictionary<string, string> GameVariants, IReadOnlyList<string> MutablePaths);
    private sealed record JobCompletion(string Status, string? Error);

    private sealed record GameInstallResult(
        string Mo2Root,
        string Profile,
        string GameVariant,
        string ModName,
        string InstallPath,
        IReadOnlyDictionary<string, string> Files);

    private sealed class GameInstallTransaction(
        GameInstallResult result,
        string targetRoot,
        string backupRoot,
        string modListPath,
        string modListBackup,
        string transactionRoot)
    {
        private bool _finished;
        public GameInstallResult Result { get; } = result;

        public void Commit()
        {
            if (_finished) return;
            _finished = true;
            if (Directory.Exists(backupRoot))
            {
                var retained = Path.Combine(Path.GetDirectoryName(transactionRoot)!, "backups", Path.GetFileName(transactionRoot));
                Directory.CreateDirectory(Path.GetDirectoryName(retained)!);
                RemoveDirectory(retained);
                Directory.Move(backupRoot, retained);
            }
            RemoveDirectory(transactionRoot);
        }

        public void Rollback()
        {
            if (_finished) return;
            _finished = true;
            if (Directory.Exists(targetRoot)) RemoveDirectory(targetRoot);
            if (Directory.Exists(backupRoot)) Directory.Move(backupRoot, targetRoot);
            if (File.Exists(modListBackup)) File.Copy(modListBackup, modListPath, overwrite: true);
            RemoveDirectory(transactionRoot);
        }
    }
}
