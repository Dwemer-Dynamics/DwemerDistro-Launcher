using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace DwemerDistro.Launcher.Wpf.Services;

public sealed record CommunityRepository(string Name, string Repository, string Description);

public sealed record CommunityRelease(JsonObject Document, string DescriptorHash)
{
    public JsonObject Manifest => (JsonObject)Document["manifest"]!;
    public string Repository => CommunityModService.Text(Document, "repository");
    public string Commit => CommunityModService.Text(Document, "commit");
    public string Version => CommunityModService.Text(Document, "version");
    public string Id => CommunityModService.Text(Manifest, "id");
    public string Name => CommunityModService.Text(Manifest, "name");
    /// <summary>The manifest's optional author or team name as plain text; empty when absent or invalid.</summary>
    public string Author => CommunityModService.DisplayText(Manifest["author"], 80);
    public string GameName => CommunityModService.DisplayText(Game["name"], 200);
    public JsonObject Game => (JsonObject)Manifest["game"]!;
    public JsonObject Install => (JsonObject)Manifest["distribution"]!["client"]!["install"]!;
}

/// <summary>Public discovery stays on Windows; installation uses pinned packages and Core's server registry.</summary>
public sealed class CommunityModService : IDisposable
{
    private const int MetadataLimit = 4 * 1024 * 1024;
    private const int ReceiptLimit = 2 * MetadataLimit + 65536;
    private const long PackageLimit = 512L * 1024 * 1024;
    private readonly WslService _wsl;
    private readonly HttpClient _http;
    private readonly string _stateRoot;
    private readonly string _manager;
    private readonly Func<string, IEnumerable<string>, CancellationToken, Task<Models.CommandResult>>? _coreOverride;
    private static readonly Regex RepositoryPattern = new(@"^https://github\.com/([A-Za-z0-9_.-]+)/([A-Za-z0-9_.-]+?)(?:\.git)?/?$", RegexOptions.CultureInvariant);
    private static readonly Regex HashPattern = new("^[a-f0-9]{64}$", RegexOptions.CultureInvariant);

    public CommunityModService(WslService wsl) : this(wsl,
        new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromMinutes(5) },
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DwemerDistro", "CommunityMods"),
        "/usr/local/bin/ddistro_custom_mod")
    { }

    internal CommunityModService(WslService wsl, HttpClient http, string stateRoot, string manager)
    {
        _wsl = wsl;
        _http = http;
        _stateRoot = stateRoot;
        _manager = manager;
        if (!_http.DefaultRequestHeaders.UserAgent.Any())
            _http.DefaultRequestHeaders.UserAgent.ParseAdd("DwemerDistro-CommunityMods/1.0");
    }

    internal CommunityModService(WslService wsl, HttpClient http, string stateRoot, string manager,
        Func<string, IEnumerable<string>, CancellationToken, Task<Models.CommandResult>> core)
        : this(wsl, http, stateRoot, manager) => _coreOverride = core;

    internal static string Text(JsonObject obj, string key) => obj[key]?.GetValue<string>() ?? string.Empty;
    public void Dispose() => _http.Dispose();

    /// <summary>Optional manifest text for display only: trimmed, bounded and never markup; empty when absent or unsafe.</summary>
    internal static string DisplayText(JsonNode? node, int maximum)
    {
        string? text;
        // A parsed descriptor can hold an escaped lone surrogate, which JsonElement refuses to read as a string.
        try { if (node is not JsonValue value || !value.TryGetValue<string>(out text)) return string.Empty; }
        catch (InvalidOperationException) { return string.Empty; }
        // Like Core and the publisher, reject Cc/Cf/Cs/Zl/Zp anywhere in the original text (lone surrogates
        // fail to decode); a literal U+FFFD is ordinary text. Only the length limit applies to the trimmed text.
        for (var rest = text.AsSpan(); !rest.IsEmpty;)
        {
            if (Rune.DecodeFromUtf16(rest, out var rune, out var used) != System.Buffers.OperationStatus.Done ||
                Rune.GetUnicodeCategory(rune) is UnicodeCategory.Control or UnicodeCategory.Format
                    or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator)
                return string.Empty;
            rest = rest[used..];
        }
        var trimmed = text.Trim();
        return trimmed.EnumerateRunes().Count() > maximum ? string.Empty : trimmed;
    }

    /// <summary>"By" the manifest author, else the public repository owner; empty for a local source.</summary>
    internal static string Credit(string? author, string? repository)
    {
        var name = string.IsNullOrWhiteSpace(author) ? RepositoryOwner(repository) : author.Trim();
        return name.Length == 0 ? string.Empty : "By " + name;
    }

    internal static string RepositoryOwner(string? repository)
    {
        if (!Uri.TryCreate(repository, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps) return string.Empty;
        var owner = uri.AbsolutePath.Trim('/').Split('/')[0];
        return Regex.IsMatch(owner, "^[A-Za-z0-9_.-]{1,100}$", RegexOptions.CultureInvariant) ? owner : string.Empty;
    }

    internal static string Relative(string value)
    {
        if (value.Length is 0 or > 200 || value.Contains('\\') || value.Split('/').Any(p =>
                !Regex.IsMatch(p, "^[A-Za-z0-9_][A-Za-z0-9._-]*$", RegexOptions.CultureInvariant) || p.EndsWith('.') ||
                Regex.IsMatch(p.Split('.')[0], "^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)))
            throw new InvalidDataException("Package paths must stay inside their component folder.");
        return value;
    }

    private static string RepositoryKey(string repository)
    {
        var match = RepositoryPattern.Match(repository);
        if (!match.Success) throw new InvalidDataException("Enter a public GitHub repository URL.");
        return match.Groups[1].Value + "/" + match.Groups[2].Value;
    }

    private async Task<Stream> OpenAsync(string address, CancellationToken cancellationToken, long maximum = PackageLimit)
    {
        for (var redirect = 0; redirect < 6; redirect++)
        {
            var uri = new Uri(address);
            if (uri.Scheme != "https" || !uri.IsDefaultPort || uri.UserInfo.Length != 0 ||
                uri.Host is not ("api.github.com" or "github.com" or "raw.githubusercontent.com" or "release-assets.githubusercontent.com" or "objects.githubusercontent.com"))
                throw new InvalidDataException("Downloads must come from GitHub release or source hosts.");
            using var response = await _http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if ((int)response.StatusCode is >= 300 and < 400 && response.Headers.Location is { } location)
            {
                address = new Uri(uri, location).AbsoluteUri;
                continue;
            }
            if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
                throw new HttpRequestException("GitHub is limiting requests. Use the cached list or try again later.");
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength > maximum) throw new InvalidDataException("Download is larger than expected.");
            Directory.CreateDirectory(_stateRoot);
            // Large ZIPs spill to disk; never buffer a client archive in launcher memory.
            var memory = new FileStream(Path.Combine(_stateRoot, "download-" + Guid.NewGuid().ToString("N")),
                FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 65536, FileOptions.Asynchronous | FileOptions.DeleteOnClose);
            try
            {
                await using var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                var buffer = new byte[65536];
                int read;
                while ((read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) != 0)
                {
                    if (memory.Length + read > maximum) { memory.Dispose(); throw new InvalidDataException("Download is larger than expected."); }
                    await memory.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                }
                memory.Position = 0;
                return memory;
            }
            catch { await memory.DisposeAsync().ConfigureAwait(false); throw; }
        }
        throw new HttpRequestException("Too many GitHub redirects.");
    }

    private async Task<byte[]> ReadAsync(string address, int maximum, CancellationToken cancellationToken)
    {
        await using var stream = await OpenAsync(address, cancellationToken, maximum).ConfigureAwait(false);
        if (stream.Length > maximum) throw new InvalidDataException("GitHub metadata or artwork is too large.");
        using var output = new MemoryStream();
        await stream.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
        return output.ToArray();
    }

    public async Task<(IReadOnlyList<CommunityRepository> Items, string Notice)> BrowseAsync(string search, CancellationToken cancellationToken = default)
    {
        search = search.Trim();
        if (search.Length > 80 || search.Any(c => !char.IsLetterOrDigit(c) && c is not (' ' or '-' or '_')))
            throw new InvalidDataException("Search using a game or mod name (letters, numbers and spaces).");
        var cacheRoot = Path.Combine(_stateRoot, "catalog");
        Directory.CreateDirectory(cacheRoot);
        var cache = Path.Combine(cacheRoot, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(search))).ToLowerInvariant() + ".json");
        try
        {
            var query = "topic:kagrenac-mod archived:false fork:true" + (search.Length == 0 ? "" : " " + search + " in:name,description");
            var bytes = await ReadAsync("https://api.github.com/search/repositories?per_page=30&q=" + Uri.EscapeDataString(query), MetadataLimit, cancellationToken).ConfigureAwait(false);
            var json = JsonNode.Parse(bytes)!.AsObject();
            var items = json["items"]!.AsArray().Select(n => n!.AsObject()).Select(n =>
                new CommunityRepository(Text(n, "full_name"), Text(n, "html_url"), Text(n, "description"))).ToList();
            foreach (var item in items) RepositoryKey(item.Repository);
            await File.WriteAllBytesAsync(cache, JsonSerializer.SerializeToUtf8Bytes(items), cancellationToken).ConfigureAwait(false);
            foreach (var obsolete in Directory.EnumerateFiles(cacheRoot, "*.json").Where(p => Regex.IsMatch(Path.GetFileName(p), "^[a-f0-9]{64}\\.json$"))
                         .OrderByDescending(File.GetLastWriteTimeUtc).Skip(32))
                File.Delete(obsolete);
            var notice = json["incomplete_results"]?.GetValue<bool>() == true || json["total_count"]!.GetValue<int>() > 30
                ? "Showing up to 30 repositories. Narrow your search for more results." : "Community listings are published by their authors.";
            return (items, notice);
        }
        catch (Exception error) when (error is HttpRequestException or IOException or JsonException)
        {
            if (!File.Exists(cache) || new FileInfo(cache).Length > MetadataLimit) throw;
            var items = JsonSerializer.Deserialize<List<CommunityRepository>>(await File.ReadAllBytesAsync(cache, cancellationToken).ConfigureAwait(false)) ?? [];
            return (items, "GitHub unavailable. Showing cached results from " + File.GetLastWriteTime(cache).ToString("g") + ".");
        }
    }

    public async Task<CommunityRelease> CheckAsync(string repository, CancellationToken cancellationToken = default, string? tag = null)
    {
        var key = RepositoryKey(repository);
        if (tag is not null && !Regex.IsMatch(tag, "^[A-Za-z0-9][A-Za-z0-9._-]{0,79}$")) throw new InvalidDataException("Unsupported release tag.");
        var metadata = JsonNode.Parse(await ReadAsync("https://api.github.com/repos/" + key + "/releases/" + (tag is null ? "latest" : "tags/" + tag), MetadataLimit, cancellationToken).ConfigureAwait(false))!.AsObject();
        if (metadata["draft"]!.GetValue<bool>() || metadata["prerelease"]!.GetValue<bool>())
            throw new InvalidDataException("Choose a published stable release.");
        var version = Text(metadata, "tag_name");
        if (!Regex.IsMatch(version, "^[A-Za-z0-9][A-Za-z0-9._-]{0,79}$")) throw new InvalidDataException("Unsupported release tag.");
        if (tag is not null && tag != version) throw new InvalidDataException("GitHub returned a different release tag.");
        var asset = metadata["assets"]!.AsArray().FirstOrDefault(n => n?["name"]?.GetValue<string>() == "kagrenac-release.json")?.AsObject()
            ?? throw new InvalidDataException("This repository has no installable KAGRENAC release yet.");
        var expected = "https://github.com/" + key + "/releases/download/" + version + "/kagrenac-release.json";
        if (Text(asset, "browser_download_url") != expected) throw new InvalidDataException("Unexpected release descriptor URL.");
        var bytes = await ReadAsync(expected, MetadataLimit, cancellationToken).ConfigureAwait(false);
        var release = ParseRelease(bytes);
        if (!RepositoryKey(release.Repository).Equals(key, StringComparison.OrdinalIgnoreCase) || release.Version != version)
            throw new InvalidDataException("Release identity differs from the repository.");
        var receipt = Path.Combine(_stateRoot, release.Id + ".json");
        if (tag is null && File.Exists(receipt) && new FileInfo(receipt).Length <= ReceiptLimit)
        {
            var previous = JsonNode.Parse(await File.ReadAllTextAsync(receipt, cancellationToken).ConfigureAwait(false))!.AsObject();
            if (previous["pending"]?.GetValue<bool>() == true && Text(previous, "repository") == release.Repository && Text(previous, "version") != release.Version)
                return await CheckAsync(repository, cancellationToken, Text(previous, "version")).ConfigureAwait(false);
        }
        return release;
    }

    internal static CommunityRelease ParseRelease(byte[] bytes)
    {
        if (bytes.Length > MetadataLimit) throw new InvalidDataException("Release descriptor exceeds 4 MiB.");
        var doc = JsonNode.Parse(bytes)?.AsObject() ?? throw new InvalidDataException("Missing release descriptor.");
        var release = new CommunityRelease(doc, Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
        RepositoryKey(release.Repository);
        if (doc["schema_version"]?.GetValue<int>() != 1 || release.Manifest["schema_version"]?.GetValue<int>() != 2 ||
            !CustomModService.IsValidModId(release.Id) || !Regex.IsMatch(release.Commit, "^[a-f0-9]{40}$") ||
            !Regex.IsMatch(release.Version, "^[A-Za-z0-9][A-Za-z0-9._-]{0,79}$"))
            throw new InvalidDataException("Unsupported release format or identity.");
        Relative(Text(release.Game, "executable"));
        if (Text(release.Install, "profile") != "game-directory") throw new InvalidDataException("Update the launcher for this install profile.");
        Relative(Text(release.Install, "path"));
        Relative(Text(release.Install, "config_path"));
        foreach (var kind in new[] { "server", "client" })
        {
            var package = doc["packages"]![kind]!.AsObject();
            var asset = Relative(Text(package, "asset"));
            if (asset.Contains('/') || !asset.EndsWith(".zip", StringComparison.Ordinal) || !HashPattern.IsMatch(Text(package, "sha256")) ||
                package["size"]!.GetValue<long>() is <= 0 or > PackageLimit) throw new InvalidDataException("Invalid release asset.");
            _ = Files(package);
        }
        var privatePath = Text(release.Install, "config_path");
        if (Files(doc["packages"]!["client"]!.AsObject()).Keys.Any(p => p.Equals(privatePath, StringComparison.OrdinalIgnoreCase) ||
                p.StartsWith(privatePath + "/", StringComparison.OrdinalIgnoreCase) || privatePath.StartsWith(p + "/", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("Client package includes its private pairing configuration.");
        return release;
    }

    private static Dictionary<string, JsonObject> Files(JsonObject package)
    {
        var result = new Dictionary<string, JsonObject>(StringComparer.OrdinalIgnoreCase);
        var files = package["files"]!.AsArray();
        if (files.Count is 0 or > 20000) throw new InvalidDataException("Invalid package file count.");
        long total = 0;
        foreach (var value in files)
        {
            var item = value!.AsObject();
            var path = Relative(Text(item, "path"));
            var size = item["size"]!.GetValue<long>();
            if (size < 0 || !HashPattern.IsMatch(Text(item, "sha256")) || !result.TryAdd(path, item))
                throw new InvalidDataException("Invalid or duplicate package file receipt.");
            total = checked(total + size);
        }
        if (total > PackageLimit || result.Keys.Any(p => p.Split('/').Select((_, i) => string.Join('/', p.Split('/').Take(i))).Skip(1).Any(result.ContainsKey)))
            throw new InvalidDataException("Package is too large or has file/directory collisions.");
        return result;
    }

    public async Task<byte[]?> ArtworkAsync(CommunityRelease release, string kind, CancellationToken cancellationToken = default)
    {
        var path = release.Manifest["assets"]?[kind]?.GetValue<string>();
        if (path is null) return null;
        Relative(path);
        if (!path.EndsWith(".png", StringComparison.OrdinalIgnoreCase) && !path.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) && !path.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase)) return null;
        var root = Text(release.Manifest["distribution"]!["server"]!.AsObject(), "root").TrimEnd('/');
        if (root.Length != 0 && root != ".") path = Relative(root) + "/" + path;
        return await ReadAsync("https://raw.githubusercontent.com/" + RepositoryKey(release.Repository) + "/" + release.Commit + "/" + path, MetadataLimit, cancellationToken).ConfigureAwait(false);
    }

    public static string? DetectGame(CommunityRelease release)
    {
        if (release.Game["steam_app_id"] is not JsonValue app || !app.TryGetValue<int>(out var appId) || appId <= 0) return null;
        var steam = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) as string;
        if (steam is null) return null;
        var libraries = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { steam };
        var folders = Path.Combine(steam, "steamapps", "libraryfolders.vdf");
        if (File.Exists(folders) && new FileInfo(folders).Length < MetadataLimit)
            foreach (Match match in Regex.Matches(File.ReadAllText(folders), "\"path\"\\s+\"([^\"]+)\"")) libraries.Add(match.Groups[1].Value.Replace("\\\\", "\\"));
        var found = new List<string>();
        foreach (var library in libraries)
        {
            var manifest = Path.Combine(library, "steamapps", "appmanifest_" + appId + ".acf");
            if (!File.Exists(manifest) || new FileInfo(manifest).Length > MetadataLimit) continue;
            var match = Regex.Match(File.ReadAllText(manifest), "\"installdir\"\\s+\"([^\"]+)\"");
            if (!match.Success || match.Groups[1].Value.IndexOfAny(['/', '\\']) >= 0) continue;
            var path = Path.Combine(library, "steamapps", "common", match.Groups[1].Value);
            if (File.Exists(Path.Combine(path, Text(release.Game, "executable")))) found.Add(path);
        }
        return found.Count == 1 ? found[0] : null;
    }

    private static string Target(string root, string relative)
    {
        Relative(relative);
        root = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
        var target = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!target.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Install path escaped the game directory.");
        for (var cursor = target; cursor.Length >= root.Length; cursor = Path.GetDirectoryName(cursor) ?? string.Empty)
            if ((File.Exists(cursor) || Directory.Exists(cursor)) && (File.GetAttributes(cursor) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Install path contains a link or junction. Select its real directory.");
        return target;
    }

    private static void VerifyOwned(string root, Dictionary<string, JsonObject> old, Dictionary<string, JsonObject> next, bool pending)
    {
        foreach (var path in old.Keys.Union(next.Keys, StringComparer.OrdinalIgnoreCase))
        {
            var target = Target(root, path);
            if (!File.Exists(target))
            {
                if (Directory.Exists(target) || (old.ContainsKey(path) && !pending)) throw new IOException("A packaged file is missing or replaced: " + path);
                continue;
            }
            using var stream = File.OpenRead(target);
            var hash = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
            if (!(old.TryGetValue(path, out var before) && Text(before, "sha256") == hash) &&
                !(pending && next.TryGetValue(path, out var after) && Text(after, "sha256") == hash))
                throw new IOException("An edited or unowned file would be overwritten: " + path);
        }
    }

    private async Task<JsonObject> StageClientAsync(CommunityRelease release, string directory, CancellationToken cancellationToken)
    {
        var package = release.Document["packages"]!["client"]!.AsObject();
        var url = "https://github.com/" + RepositoryKey(release.Repository) + "/releases/download/" + release.Version + "/" + Text(package, "asset");
        await using var input = await OpenAsync(url, cancellationToken).ConfigureAwait(false);
        if (input.Length != package["size"]!.GetValue<long>() || Convert.ToHexString(await SHA256.HashDataAsync(input, cancellationToken).ConfigureAwait(false)).ToLowerInvariant() != Text(package, "sha256"))
            throw new InvalidDataException("Client archive failed its release SHA-256 check.");
        input.Position = 0;
        using var archive = new ZipArchive(input, ZipArchiveMode.Read);
        var files = Files(package);
        if (archive.Entries.Count != files.Count || archive.Entries.Select(e => e.FullName).Distinct(StringComparer.OrdinalIgnoreCase).Count() != files.Count)
            throw new InvalidDataException("Client archive entries differ from the release receipts.");
        foreach (var entry in archive.Entries)
        {
            var fileType = (entry.ExternalAttributes >> 16) & 0xf000;
            if (!files.TryGetValue(entry.FullName, out var item) || entry.Length != item["size"]!.GetValue<long>() || fileType is not (0 or 0x8000))
                throw new InvalidDataException("Client archive contains a link, special file or unexpected entry.");
            var path = Target(directory, entry.FullName);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            await using (var data = entry.Open())
            {
                var buffer = new byte[65536];
                long count = 0;
                int read;
                while ((read = await data.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) != 0)
                {
                    count += read;
                    if (count > entry.Length) throw new InvalidDataException("Unpacked client file exceeds its declared size.");
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                }
                if (count != entry.Length) throw new InvalidDataException("Client file is truncated.");
            }
            await using var verify = File.OpenRead(path);
            if (Convert.ToHexString(await SHA256.HashDataAsync(verify, cancellationToken).ConfigureAwait(false)).ToLowerInvariant() != Text(item, "sha256"))
                throw new InvalidDataException("A client file failed its SHA-256 check.");
        }
        return package;
    }

    private async Task<Models.CommandResult> CoreAsync(string user, IEnumerable<string> arguments, CancellationToken cancellationToken) =>
        await (_coreOverride is null
            ? _wsl.RunWslAsync(new[] { "-d", LauncherConstants.DistroName, "-u", user, "--exec", _manager }.Concat(arguments), null, cancellationToken)
            : _coreOverride(user, arguments, cancellationToken)).ConfigureAwait(false);

    // Core remains the authority for server ownership and readiness; no Windows client receipt is created.
    private async Task<JsonObject?> ServerEntryAsync(CommunityRelease release, CancellationToken cancellationToken)
    {
        RepositoryKey(release.Repository);
        if (!CustomModService.IsValidModId(release.Id)) throw new InvalidDataException("Invalid mod id.");
        var result = await CoreAsync(LauncherConstants.DistroUser, ["status", "--json"], cancellationToken).ConfigureAwait(false);
        if (!result.Succeeded) throw new IOException(CustomModService.DescribeFailure(result));
        var status = JsonNode.Parse(result.StandardOutput)?.AsObject();
        if (status?["schema_version"]?.GetValue<int>() != 1 || status["mods"] is not JsonArray mods)
            throw new InvalidDataException("Could not read installed servers. Update Distro and try again.");
        var entry = mods.FirstOrDefault(n => n?["id"]?.GetValue<string>() == release.Id)?.AsObject();
        if (entry is not null && (Text(entry, "repository") != release.Repository || Text(entry, "install_mode") != "release"))
            throw new IOException("This mod id belongs to another repository or a legacy server. Nothing was changed.");
        return entry;
    }

    private static bool ServerMatchesRelease(JsonObject? entry, CommunityRelease release) =>
        entry is not null && Text(entry, "state") == "ready" && Text(entry, "commit") == release.Commit &&
        Text(entry, "server_folder") == Text(release.Manifest, "server_folder") && entry["pending_release"] is null &&
        entry["release"] is JsonObject installed && Text(installed, "tag") == release.Version &&
        Text(installed, "descriptor_sha256") == release.DescriptorHash;

    public async Task<bool> IsServerInstalledAsync(CommunityRelease release, CancellationToken cancellationToken = default) =>
        ServerMatchesRelease(await ServerEntryAsync(release, cancellationToken).ConfigureAwait(false), release);

    public async Task InstallServerAsync(CommunityRelease release, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        RepositoryKey(release.Repository);
        if (!CustomModService.IsValidModId(release.Id)) throw new InvalidDataException("Invalid mod id.");
        Directory.CreateDirectory(_stateRoot);
        // Share the existing lock with combined installs so their client phase cannot overlap a server update.
        using var operationLock = new FileStream(Path.Combine(_stateRoot, release.Id + ".lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        progress?.Report("Checking server release…");
        var check = await CoreAsync(LauncherConstants.DistroUser,
            ["release-check", release.Repository, "--tag", release.Version, "--json"], cancellationToken).ConfigureAwait(false);
        if (!check.Succeeded) throw new IOException(CustomModService.DescribeFailure(check));
        var preview = JsonNode.Parse(check.StandardOutput)?.AsObject();
        if (preview?["release_hash"]?.GetValue<string>() != release.DescriptorHash ||
            preview?["release"]?["commit"]?.GetValue<string>() != release.Commit)
            throw new IOException("Release changed after preview. Check it again.");
        var entry = await ServerEntryAsync(release, cancellationToken).ConfigureAwait(false);
        if (ServerMatchesRelease(entry, release)) return;
        var update = entry is not null && Text(entry, "state") != "unregistered";
        progress?.Report("Installing server…");
        var install = await CoreAsync("root", [update ? "release-update" : "release-install", update ? release.Id : release.Repository,
            "--tag", release.Version, "--expect-commit", release.Commit, "--expect-release", release.DescriptorHash], cancellationToken).ConfigureAwait(false);
        if (!install.Succeeded) throw new IOException(CustomModService.DescribeFailure(install));
        if (!await IsServerInstalledAsync(release, cancellationToken).ConfigureAwait(false))
            throw new IOException("The server is not ready yet. Retry installation.");
    }

    public static string ClientReleaseUrl(CommunityRelease release)
    {
        var repository = RepositoryKey(release.Repository);
        if (!Regex.IsMatch(release.Version, "^[A-Za-z0-9][A-Za-z0-9._-]{0,79}$")) throw new InvalidDataException("Unsupported release tag.");
        var asset = Relative(Text(release.Document["packages"]!["client"]!.AsObject(), "asset"));
        if (asset.Contains('/') || !asset.EndsWith(".zip", StringComparison.Ordinal)) throw new InvalidDataException("Invalid client release asset.");
        return "https://github.com/" + repository + "/releases/download/" + release.Version + "/" + asset;
    }

    private static void SaveReceipt(string path, JsonObject document)
    {
        var temp = path + ".new";
        File.WriteAllText(temp, document.ToJsonString());
        File.Move(temp, path, true);
    }

    public async Task<string> InstallAsync(CommunityRelease release, string gameDirectory, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        gameDirectory = Path.GetFullPath(gameDirectory);
        var executable = Target(gameDirectory, Text(release.Game, "executable"));
        if (!File.Exists(executable)) throw new IOException("The selected folder does not contain the declared game executable.");
        var processName = Path.GetFileNameWithoutExtension(executable);
        var processes = Process.GetProcessesByName(processName);
        try { if (processes.Length != 0) throw new IOException("Close the game before installing or updating its mod."); }
        finally { foreach (var process in processes) process.Dispose(); }
        if (Text(release.Game, "loader_path") is { Length: > 0 } loader && !File.Exists(Target(gameDirectory, loader)))
            throw new IOException("Install the required game loader first: " + Text(release.Game, "loader"));
        if (Text(release.Game, "executable_sha256") is { Length: > 0 } pin)
        {
            using var stream = File.OpenRead(executable);
            if (Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant() != pin) throw new IOException("The game executable does not match the author's supported build.");
        }
        Directory.CreateDirectory(_stateRoot);
        using var operationLock = new FileStream(Path.Combine(_stateRoot, release.Id + ".lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var receiptPath = Path.Combine(_stateRoot, release.Id + ".json");
        if (File.Exists(receiptPath) && new FileInfo(receiptPath).Length > ReceiptLimit) throw new IOException("Client receipt is too large.");
        var oldReceipt = File.Exists(receiptPath) ? JsonNode.Parse(await File.ReadAllTextAsync(receiptPath, cancellationToken).ConfigureAwait(false))!.AsObject() : null;
        if (oldReceipt?["removing"]?.GetValue<bool>() == true) throw new IOException("Finish removing this installation before reinstalling it.");
        var destination = Target(gameDirectory, Text(release.Install, "path"));
        if (oldReceipt is not null && (Text(oldReceipt, "repository") != release.Repository || Text(oldReceipt, "destination") != destination ||
                Text(oldReceipt, "config_path") != Text(release.Install, "config_path")))
            throw new IOException("This mod id already belongs to another repository, folder or pairing contract.");
        var pending = oldReceipt?["pending"]?.GetValue<bool>() == true;
        if (pending && Text(oldReceipt!, "descriptor_hash") != release.DescriptorHash)
            throw new IOException("Retry the interrupted release before selecting another version.");
        var old = oldReceipt?["installed_package"] is JsonObject previous ? Files(previous) : new Dictionary<string, JsonObject>(StringComparer.OrdinalIgnoreCase);
        var next = Files(release.Document["packages"]!["client"]!.AsObject());
        VerifyOwned(destination, old, next, pending);
        var configPath = Target(destination, Text(release.Install, "config_path"));
        if (File.Exists(configPath) && oldReceipt is null) throw new IOException("An unowned private client config already exists; nothing is adopted.");
        var stage = Path.Combine(_stateRoot, "stage-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stage);
        try
        {
            progress?.Report("Downloading and checking the client package…");
            var package = await StageClientAsync(release, stage, cancellationToken).ConfigureAwait(false);
            progress?.Report("Checking the matching server release…");
            var check = await CoreAsync(LauncherConstants.DistroUser, ["release-check", release.Repository, "--tag", release.Version, "--json"], cancellationToken).ConfigureAwait(false);
            if (!check.Succeeded) throw new IOException(CustomModService.DescribeFailure(check));
            var checkedServer = JsonNode.Parse(check.StandardOutput)!.AsObject();
            if (Text(checkedServer, "release_hash") != release.DescriptorHash) throw new IOException("Release changed after preview; check it again.");
            var status = await CoreAsync(LauncherConstants.DistroUser, ["status", "--json"], cancellationToken).ConfigureAwait(false);
            if (!status.Succeeded) throw new IOException(CustomModService.DescribeFailure(status));
            var existing = JsonNode.Parse(status.StandardOutput)!["mods"]!.AsArray().FirstOrDefault(n => n?["id"]?.GetValue<string>() == release.Id)?.AsObject();
            if (existing is not null && (Text(existing, "repository") != release.Repository || Text(existing, "install_mode") != "release"))
                throw new IOException("This mod id is already claimed by another repository or a legacy branch install. Nothing is adopted.");
            var installed = existing is not null;
            var record = new JsonObject
            {
                ["repository"] = release.Repository,
                ["destination"] = destination,
                ["config_path"] = Text(release.Install, "config_path"),
                ["descriptor_hash"] = release.DescriptorHash,
                ["version"] = release.Version,
                ["commit"] = release.Commit,
                ["pending"] = true,
                ["game_executable"] = executable,
                ["pending_package"] = package.DeepClone(),
                ["installed_package"] = oldReceipt?["installed_package"]?.DeepClone(),
                ["server_ready"] = false
            };
            SaveReceipt(receiptPath, record);
            progress?.Report("Installing the matching server release…");
            var coreArgs = new[] { installed ? "release-update" : "release-install", installed ? release.Id : release.Repository,
                "--tag", release.Version, "--expect-commit", release.Commit, "--expect-release", release.DescriptorHash };
            var install = await CoreAsync("root", coreArgs, cancellationToken).ConfigureAwait(false);
            if (!install.Succeeded) throw new IOException(CustomModService.DescribeFailure(install));
            record["server_ready"] = true;
            SaveReceipt(receiptPath, record);
            progress?.Report("Installing client files and pairing privately…");
            VerifyOwned(destination, old, next, pending);
            foreach (var obsolete in old.Keys.Except(next.Keys, StringComparer.OrdinalIgnoreCase))
            {
                var path = Target(destination, obsolete);
                if (File.Exists(path)) File.Delete(path);
            }
            foreach (var (path, item) in next)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var target = Target(destination, path);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                var temp = target + ".release-" + Guid.NewGuid().ToString("N");
                File.Copy(Target(stage, path), temp, false);
                File.Move(temp, target, true);
            }
            // Pairing output contains a secret. It is never passed to the launcher console/logger.
            var pairing = await CoreAsync("root", ["release-pair", release.Id], cancellationToken).ConfigureAwait(false);
            if (!pairing.Succeeded) throw new IOException("Server ready; private client pairing failed. Retry this release.");
            var paired = JsonNode.Parse(pairing.StandardOutput)!.AsObject();
            if (Text(paired, "token").Length < 16 || !CustomModService.IsSafeModUrl(Text(paired, "server_url").TrimEnd('/') + "/",
                    release.Id, Text(release.Manifest, "server_folder")))
                throw new IOException("Server returned incomplete pairing data.");
            if (File.Exists(configPath))
            {
                var config = JsonNode.Parse(await File.ReadAllTextAsync(configPath, cancellationToken).ConfigureAwait(false))!.AsObject();
                if (Text(config, "token") != Text(paired, "token") || Text(config, "server_url") != Text(paired, "server_url"))
                    throw new IOException("Existing private pairing differs. It was preserved; review it before re-pairing.");
            }
            else
            {
                Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
                var privateTemp = configPath + ".pair-" + Guid.NewGuid().ToString("N");
                var security = new FileSecurity();
                security.SetAccessRuleProtection(true, false);
                foreach (var sid in new[] { WindowsIdentity.GetCurrent().User!, new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null) })
                    security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl, AccessControlType.Allow));
                using (var empty = FileSystemAclExtensions.Create(new FileInfo(privateTemp), FileMode.CreateNew,
                    FileSystemRights.Write, FileShare.None, 4096, FileOptions.None, security))
                {
                    await empty.WriteAsync(Encoding.UTF8.GetBytes(paired.ToJsonString()), cancellationToken).ConfigureAwait(false);
                    empty.Flush(true);
                }
                File.Move(privateTemp, configPath, false);
            }
            record["pending"] = false;
            record["installed_package"] = package.DeepClone();
            record.Remove("pending_package");
            SaveReceipt(receiptPath, record);
            return "Installed and paired. " + Text(release.Game, "enable_in_game") + " Provider setup and in-game acceptance are separate checks.";
        }
        finally
        {
            // Only this operation's random staging folder is removed, never game or runtime data.
            if (Directory.Exists(stage) && Path.GetDirectoryName(stage) == _stateRoot && !new DirectoryInfo(stage).Attributes.HasFlag(FileAttributes.ReparsePoint))
                Directory.Delete(stage, true);
        }
    }

    public async Task<string> RemoveAsync(string modId, string repository, CancellationToken cancellationToken = default)
    {
        if (!CustomModService.IsValidModId(modId)) throw new InvalidDataException("Invalid mod id.");
        RepositoryKey(repository);
        Directory.CreateDirectory(_stateRoot);
        using var operationLock = new FileStream(Path.Combine(_stateRoot, modId + ".lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var path = Path.Combine(_stateRoot, modId + ".json");
        JsonObject? record = null;
        if (File.Exists(path))
        {
            if (new FileInfo(path).Length > ReceiptLimit) throw new IOException("Client receipt is too large.");
            record = JsonNode.Parse(await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false))!.AsObject();
            if (Text(record, "repository") != repository) throw new IOException("This receipt belongs to another repository.");
            var processes = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(Text(record, "game_executable")));
            try { if (processes.Length != 0) throw new IOException("Close the game before removing client files."); }
            finally { foreach (var process in processes) process.Dispose(); }
            var old = record["installed_package"] is JsonObject installed ? Files(installed) : new Dictionary<string, JsonObject>(StringComparer.OrdinalIgnoreCase);
            var next = record["pending_package"] is JsonObject pending ? Files(pending) : new Dictionary<string, JsonObject>(StringComparer.OrdinalIgnoreCase);
            var destination = Text(record, "destination");
            VerifyOwned(destination, old, next, record["pending"]?.GetValue<bool>() == true || record["removing"]?.GetValue<bool>() == true);
            record["removing"] = true;
            SaveReceipt(path, record);
            foreach (var owned in old.Keys.Union(next.Keys, StringComparer.OrdinalIgnoreCase))
            {
                var target = Target(destination, owned);
                if (File.Exists(target)) File.Delete(target);
            }
        }
        var status = await CoreAsync(LauncherConstants.DistroUser, ["status", "--json"], cancellationToken).ConfigureAwait(false);
        if (!status.Succeeded) throw new IOException(CustomModService.DescribeFailure(status));
        var server = JsonNode.Parse(status.StandardOutput)!["mods"]!.AsArray().FirstOrDefault(n => n?["id"]?.GetValue<string>() == modId)?.AsObject();
        if (server is not null)
        {
            if (Text(server, "repository") != repository) throw new IOException("The server id belongs to another repository.");
            var removed = await CoreAsync("root", ["unregister", modId], cancellationToken).ConfigureAwait(false);
            if (!removed.Succeeded) throw new IOException(CustomModService.DescribeFailure(removed));
        }
        if (record is not null)
        {
            record["removing"] = false;
            record["pending"] = false;
            record["server_ready"] = false;
            record["installed_package"] = null;
            record.Remove("pending_package");
            SaveReceipt(path, record);
        }
        return "Owned game client files removed; server unregistered. Private configuration, server files, database and unknown files were kept.";
    }

    public string ClientState(string modId, string repository)
    {
        if (!CustomModService.IsValidModId(modId)) return "Client receipt unavailable";
        var path = Path.Combine(_stateRoot, modId + ".json");
        if (!File.Exists(path)) return "Client not installed on this PC";
        try
        {
            if (new FileInfo(path).Length > ReceiptLimit) return "Client receipt unavailable";
            var record = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
            if (Text(record, "repository") != repository) return "Client belongs to another repository";
            if (record["removing"]?.GetValue<bool>() == true) return "Client removal pending";
            if (record["pending"]?.GetValue<bool>() == true) return "Client installation pending";
            return record["installed_package"] is null ? "Client removed; private data kept" : "Client installation recorded";
        }
        catch (Exception error) when (error is IOException or JsonException or InvalidOperationException) { return "Client receipt unavailable"; }
    }
}
