using System.Text.Json;
using System.Text.RegularExpressions;

namespace DwemerDistro.Launcher.Wpf.Services;

/// <summary>
/// Typed front end for the distro-owned <c>/usr/local/bin/ddistro_custom_mod</c>.
///
/// The distro owns the custom mod registry and every operation; the launcher keeps no list of its
/// own. Commands go to wsl.exe <c>--exec</c> as an argument vector, so a repository URL or mod id
/// is never parsed by a shell, and both are validated here first as a second boundary.
/// </summary>
public sealed partial class CustomModService(WslService wsl)
{
    public const int SupportedSchemaVersion = 1;

    internal const string ManagerCommand = "/usr/local/bin/ddistro_custom_mod";

    /// <summary>
    /// Every dashboard and health link must stay under the local custom mod route: the shared
    /// CUSTOM_MODS_PORT (19000-19999), or 8081 as reported by older distros.
    /// </summary>
    [GeneratedRegex(@"^http://127\.0\.0\.1:(?:8081|19[0-9]{3})/custom-mods/")]
    private static partial Regex RouteBasePattern();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true
    };

    public async Task<CustomModStatusResult> GetStatusAsync(bool checkHealth, CancellationToken cancellationToken = default)
    {
        var result = await RunAsync(LauncherConstants.DistroUser, BuildStatusArguments(checkHealth), null, cancellationToken)
            .ConfigureAwait(false);
        if (!result.Succeeded)
        {
            return new CustomModStatusResult(null, DescribeFailure(result));
        }

        return TryParseStatus(result.StandardOutput, out var mods, out var error)
            ? new CustomModStatusResult(mods, null)
            : new CustomModStatusResult(null, error);
    }

    /// <summary>Downloads nothing permanent: the distro previews the manifest from a temporary clone.</summary>
    public async Task<CustomModPreviewResult> CheckAsync(string repositoryUrl, Action<string>? output, CancellationToken cancellationToken = default)
    {
        var result = await RunAsync(LauncherConstants.DistroUser, BuildCheckArguments(repositoryUrl), output, cancellationToken)
            .ConfigureAwait(false);
        if (!result.Succeeded)
        {
            return new CustomModPreviewResult(null, DescribeFailure(result));
        }

        return TryParsePreview(result.StandardOutput, out var preview, out var error)
            ? new CustomModPreviewResult(preview, null)
            : new CustomModPreviewResult(null, error);
    }

    public Task<Models.CommandResult> InstallAsync(string repositoryUrl, string expectedCommit, Action<string>? output, CancellationToken cancellationToken = default)
    {
        return RunAsync("root", BuildInstallArguments(repositoryUrl, expectedCommit), output, cancellationToken);
    }

    public Task<Models.CommandResult> UpdateAsync(string modId, Action<string>? output, CancellationToken cancellationToken = default)
    {
        return RunAsync("root", BuildIdArguments("update", modId), output, cancellationToken);
    }

    /// <summary>
    /// Explicit refresh of the author's offered branches (stored in the distro registry); with a
    /// branch, also previews that branch's exact commit for review before a switch.
    /// </summary>
    public async Task<CustomModBranchCheckResult> CheckBranchesAsync(string modId, string? branch, Action<string>? output, CancellationToken cancellationToken = default)
    {
        var result = await RunAsync("root", BuildBranchesArguments(modId, branch), output, cancellationToken)
            .ConfigureAwait(false);
        if (!result.Succeeded)
        {
            return new CustomModBranchCheckResult(null, DescribeFailure(result));
        }

        return TryParseBranchCheck(result.StandardOutput, out var check, out var error)
            ? new CustomModBranchCheckResult(check, null)
            : new CustomModBranchCheckResult(null, error);
    }

    public Task<Models.CommandResult> SwitchBranchAsync(string modId, string branch, string expectedCommit, Action<string>? output, CancellationToken cancellationToken = default)
    {
        return RunAsync("root", BuildSwitchArguments(modId, branch, expectedCommit), output, cancellationToken);
    }

    public Task<Models.CommandResult> BackupAsync(string modId, Action<string>? output, CancellationToken cancellationToken = default)
    {
        return RunAsync("root", BuildIdArguments("backup", modId), output, cancellationToken);
    }

    public Task<Models.CommandResult> UnregisterAsync(string modId, Action<string>? output, CancellationToken cancellationToken = default)
    {
        return RunAsync("root", BuildIdArguments("unregister", modId), output, cancellationToken);
    }

    private async Task<Models.CommandResult> RunAsync(
        string user,
        IReadOnlyList<string> arguments,
        Action<string>? output,
        CancellationToken cancellationToken)
    {
        // Mutations run as WSL's root user, like ddistro_server; status and check stay on dwemer.
        var operation = string.Join(" ", arguments.Skip(1));
        LauncherLogService.Operation($"START custom-mod {operation}");
        var result = await wsl.RunWslAsync(
            BuildWslArguments(user, arguments),
            line =>
            {
                LauncherLogService.Operation(line);
                output?.Invoke(line);
            },
            cancellationToken).ConfigureAwait(false);
        LauncherLogService.Operation($"END custom-mod {operation}; exit code {result.ExitCode}");
        return result;
    }

    // --- Command construction -------------------------------------------------------------

    /// <summary><c>--exec</c> runs the manager directly, without the distro's login shell.</summary>
    internal static string[] BuildWslArguments(string user, IReadOnlyList<string> arguments)
    {
        return ["-d", LauncherConstants.DistroName, "-u", user, "--exec", .. arguments];
    }

    internal static string[] BuildStatusArguments(bool checkHealth)
    {
        return checkHealth
            ? [ManagerCommand, "status", "--check-health", "--json"]
            : [ManagerCommand, "status", "--json"];
    }

    internal static string[] BuildCheckArguments(string repositoryUrl)
    {
        return [ManagerCommand, "check", RequireValidRepositoryUrl(repositoryUrl), "--json"];
    }

    internal static string[] BuildInstallArguments(string repositoryUrl, string expectedCommit)
    {
        if (!CommitPattern().IsMatch(expectedCommit ?? string.Empty))
        {
            throw new ArgumentException("A checked commit id is required.", nameof(expectedCommit));
        }

        return [ManagerCommand, "install", RequireValidRepositoryUrl(repositoryUrl), "--expect-commit", expectedCommit!];
    }

    internal static string[] BuildIdArguments(string verb, string modId)
    {
        if (verb is not ("update" or "backup" or "unregister"))
        {
            throw new ArgumentOutOfRangeException(nameof(verb), verb, "Unknown custom mod operation.");
        }

        if (!IsValidModId(modId))
        {
            throw new ArgumentException("Invalid custom mod id.", nameof(modId));
        }

        return [ManagerCommand, verb, modId];
    }

    internal static string[] BuildBranchesArguments(string modId, string? branch)
    {
        if (!IsValidModId(modId))
        {
            throw new ArgumentException("Invalid custom mod id.", nameof(modId));
        }

        if (branch is null)
        {
            return [ManagerCommand, "branches", modId, "--json"];
        }

        return IsValidBranchName(branch)
            ? [ManagerCommand, "branches", modId, "--branch", branch, "--json"]
            : throw new ArgumentException("Invalid branch name.", nameof(branch));
    }

    internal static string[] BuildSwitchArguments(string modId, string branch, string expectedCommit)
    {
        if (!IsValidModId(modId))
        {
            throw new ArgumentException("Invalid custom mod id.", nameof(modId));
        }

        if (!IsValidBranchName(branch))
        {
            throw new ArgumentException("Invalid branch name.", nameof(branch));
        }

        if (!CommitPattern().IsMatch(expectedCommit ?? string.Empty))
        {
            throw new ArgumentException("A reviewed commit id is required.", nameof(expectedCommit));
        }

        return [ManagerCommand, "switch", modId, "--branch", branch, "--expect-commit", expectedCommit!];
    }

    private static string RequireValidRepositoryUrl(string repositoryUrl)
    {
        var error = ValidateRepositoryUrl(repositoryUrl);
        return error is null ? repositoryUrl : throw new ArgumentException(error, nameof(repositoryUrl));
    }

    // --- Validation (mirrors the distro's rules; the distro validates again) ------------------

    /// <summary>Returns null for a plain public https://host/path URL, otherwise a short reason.</summary>
    public static string? ValidateRepositoryUrl(string? url)
    {
        if (string.IsNullOrEmpty(url))
        {
            return "Enter a repository URL.";
        }

        if (url.Length > 300)
        {
            return "The repository URL is too long.";
        }

        if (url.Any(c => c < 0x21 || c > 0x7E))
        {
            return "The repository URL contains spaces or unsupported characters.";
        }

        if (!url.StartsWith("https://", StringComparison.Ordinal))
        {
            return "Use a public https:// repository URL.";
        }

        if (url.IndexOfAny(['@', '?', '#', '%', '\\', ';']) >= 0)
        {
            return "Remove credentials, query text, or fragments from the URL.";
        }

        var match = RepositoryUrlPattern().Match(url);
        if (!match.Success)
        {
            return "Use a URL like https://github.com/owner/repository.";
        }

        var host = match.Groups["host"].Value;
        if (host.EndsWith(".localhost", StringComparison.Ordinal) ||
            host.EndsWith(".local", StringComparison.Ordinal) ||
            host.EndsWith(".internal", StringComparison.Ordinal) ||
            host.EndsWith(".lan", StringComparison.Ordinal) ||
            host.EndsWith(".home", StringComparison.Ordinal) ||
            char.IsDigit(host[^1]))
        {
            return "Local network repository hosts are not supported.";
        }

        var path = match.Groups["path"].Value + "/";
        return path.Contains("/../", StringComparison.Ordinal) || path.Contains("/./", StringComparison.Ordinal)
            ? "The repository path is not supported."
            : null;
    }

    /// <summary>
    /// Maps a validated https://github.com/owner/repo URL to public artwork addresses: the
    /// repository's social preview card, then the owner's avatar. Any other host returns false.
    /// </summary>
    public static bool TryGetGitHubArtworkUrls(string? repositoryUrl, out string[] artworkUrls)
    {
        artworkUrls = [];
        if (ValidateRepositoryUrl(repositoryUrl) is not null)
        {
            return false;
        }

        var match = GitHubRepositoryPattern().Match(repositoryUrl!);
        if (!match.Success)
        {
            return false;
        }

        var owner = match.Groups["owner"].Value;
        var repository = match.Groups["repo"].Value;
        if (repository.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
        {
            repository = repository[..^4];
        }

        if (repository.Length == 0 || repository.Trim('.').Length == 0)
        {
            return false;
        }

        artworkUrls =
        [
            $"https://opengraph.githubassets.com/1/{owner}/{repository}",
            $"https://avatars.githubusercontent.com/{owner}?s=460"
        ];
        return true;
    }

    public static bool IsValidModId(string? modId)
    {
        return !string.IsNullOrEmpty(modId) && ModIdPattern().IsMatch(modId) && !modId.Contains("--", StringComparison.Ordinal);
    }

    /// <summary>Mirrors the distro's branch name rule; anything else is never passed on or shown as offered.</summary>
    public static bool IsValidBranchName(string? branch)
    {
        return !string.IsNullOrEmpty(branch) && BranchPattern().IsMatch(branch) &&
               !branch.Contains("..", StringComparison.Ordinal) && !branch.Contains("//", StringComparison.Ordinal) &&
               !branch.EndsWith(".lock", StringComparison.Ordinal) && !branch.EndsWith('/') && !branch.EndsWith('.');
    }

    /// <summary>Accepts only the fixed local route for this mod id, never a manifest-supplied host.</summary>
    public static bool IsSafeModUrl(string? url, string modId)
    {
        if (string.IsNullOrEmpty(url) || !IsValidModId(modId))
        {
            return false;
        }

        var routeBase = RouteBasePattern().Match(url);
        var prefix = routeBase.Value + modId + "/";
        if (!routeBase.Success || !url.StartsWith(prefix, StringComparison.Ordinal))
        {
            return false;
        }

        var rest = url[prefix.Length..];
        return rest.Length == 0 || RelativeRoutePattern().IsMatch(rest);
    }

    /// <summary>An installed icon or banner: the fixed local route for this id, ending in a PNG/JPEG file.</summary>
    public static bool IsSafeModAssetUrl(string? url, string modId)
    {
        return IsSafeModUrl(url, modId) &&
               (url!.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ||
                url.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ||
                url.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase));
    }

    [GeneratedRegex(@"^https://(?<host>[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?(?:\.[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?)+)(?<path>(?:/[A-Za-z0-9._~-]+)+)/?\z")]
    private static partial Regex RepositoryUrlPattern();

    [GeneratedRegex(@"^https://github\.com/(?<owner>[A-Za-z0-9](?:[A-Za-z0-9-]{0,37}[A-Za-z0-9])?)/(?<repo>[A-Za-z0-9._-]{1,100})/?\z")]
    private static partial Regex GitHubRepositoryPattern();

    [GeneratedRegex(@"^[a-z][a-z0-9-]{1,30}[a-z0-9]\z")]
    private static partial Regex ModIdPattern();

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._/-]{0,99}\z")]
    private static partial Regex BranchPattern();

    [GeneratedRegex(@"^[0-9a-f]{40}\z")]
    private static partial Regex CommitPattern();

    [GeneratedRegex(@"^(?:[A-Za-z0-9_][A-Za-z0-9._-]*/)*(?:[A-Za-z0-9_][A-Za-z0-9._-]*)?\z")]
    private static partial Regex RelativeRoutePattern();

    // --- Parsing --------------------------------------------------------------------------

    internal static bool TryParseStatus(string? output, out IReadOnlyList<CustomModInfo>? mods, out string? error)
    {
        mods = null;
        if (!TryDeserialize<StatusDocument>(output, out var document, out error))
        {
            return false;
        }

        var list = new List<CustomModInfo>();
        foreach (var entry in document!.Mods ?? [])
        {
            if (!IsValidModId(entry.Id))
            {
                continue;
            }

            list.Add(new CustomModInfo(
                entry.Id!,
                Clean(entry.Name, 60) ?? entry.Id!,
                Clean(entry.Description, 300) ?? string.Empty,
                Clean(entry.Repository, 300) ?? string.Empty,
                Clean(entry.Branch, 100) ?? string.Empty,
                ShortCommit(entry.Commit),
                ParseState(entry.State),
                Clean(entry.Message, 300) ?? string.Empty,
                IsSafeModUrl(entry.DashboardUrl, entry.Id!) ? entry.DashboardUrl : null,
                IsSafeModUrl(entry.HealthUrl, entry.Id!) ? entry.HealthUrl : null,
                Clean(entry.Health, 20),
                Clean(entry.HealthMessage, 200),
                IsSafeModAssetUrl(entry.IconUrl, entry.Id!) ? entry.IconUrl : null,
                IsSafeModAssetUrl(entry.BannerUrl, entry.Id!) ? entry.BannerUrl : null)
            {
                DefaultBranch = IsValidBranchName(entry.Branches?.Default) ? entry.Branches!.Default! : string.Empty,
                OfferedBranches = CleanBranches(entry.Branches?.Allowed),
                BranchesCheckedAt = Clean(entry.BranchesCheckedAt, 40) ?? string.Empty
            });
        }

        mods = list;
        return true;
    }

    internal static bool TryParsePreview(string? output, out CustomModPreview? preview, out string? error)
    {
        preview = null;
        if (!TryDeserialize<PreviewDocument>(output, out var document, out error))
        {
            return false;
        }

        if (!IsValidModId(document!.Id) || !CommitPattern().IsMatch(document.Commit ?? string.Empty) ||
            ValidateRepositoryUrl(document.Repository) is not null)
        {
            error = "The repository check returned an incomplete result.";
            return false;
        }

        preview = new CustomModPreview(
            document.Id!,
            Clean(document.Name, 60) ?? document.Id!,
            Clean(document.Description, 300) ?? string.Empty,
            document.Repository!,
            Clean(document.Branch, 100) ?? string.Empty,
            document.Commit!,
            Clean(document.Folder, 200) ?? string.Empty,
            Clean(document.Database, 70) ?? string.Empty,
            document.Registered,
            document.Restorable && CommitPattern().IsMatch(document.InstalledCommit ?? string.Empty),
            document.Restorable && CommitPattern().IsMatch(document.InstalledCommit ?? string.Empty)
                ? document.InstalledCommit!
                : string.Empty);
        return true;
    }

    internal static bool TryParseBranchCheck(string? output, out CustomModBranchCheck? check, out string? error)
    {
        check = null;
        if (!TryDeserialize<BranchCheckDocument>(output, out var document, out error))
        {
            return false;
        }

        var hasTarget = !string.IsNullOrEmpty(document!.Branch);
        if (!IsValidModId(document.Id) ||
            (hasTarget && (!IsValidBranchName(document.Branch) || !CommitPattern().IsMatch(document.Commit ?? string.Empty))))
        {
            error = "The branch check returned an incomplete result.";
            return false;
        }

        check = new CustomModBranchCheck(
            document.Id!,
            IsValidBranchName(document.CurrentBranch) ? document.CurrentBranch! : string.Empty,
            CommitPattern().IsMatch(document.CurrentCommit ?? string.Empty) ? document.CurrentCommit! : string.Empty,
            IsValidBranchName(document.Branches?.Default) ? document.Branches!.Default! : string.Empty,
            CleanBranches(document.Branches?.Allowed),
            hasTarget ? document.Branch! : string.Empty,
            hasTarget ? document.Commit! : string.Empty);
        return true;
    }

    private static IReadOnlyList<string> CleanBranches(List<string>? branches)
    {
        return (branches ?? []).Where(IsValidBranchName).Distinct(StringComparer.Ordinal).Take(10).ToList();
    }

    private static bool TryDeserialize<T>(string? output, out T? document, out string? error)
        where T : VersionedDocument
    {
        document = null;
        error = null;
        var start = output?.IndexOf('{') ?? -1;
        var end = output?.LastIndexOf('}') ?? -1;
        if (start < 0 || end <= start)
        {
            error = "The custom mod manager did not return a result.";
            return false;
        }

        try
        {
            document = JsonSerializer.Deserialize<T>(output![start..(end + 1)], JsonOptions);
        }
        catch (JsonException)
        {
            error = "The custom mod manager result could not be read.";
            return false;
        }

        if (document is null || document.SchemaVersion != SupportedSchemaVersion)
        {
            error = "This launcher does not understand the distro's custom mod manager. Update the launcher.";
            document = null;
            return false;
        }

        return true;
    }

    internal static CustomModState ParseState(string? state)
    {
        return state switch
        {
            "ready" => CustomModState.Ready,
            "installing" or "updating" => CustomModState.Busy,
            _ => CustomModState.Failed
        };
    }

    private static string ShortCommit(string? commit)
    {
        return commit is { Length: >= 7 } && CommitPattern().IsMatch(commit) ? commit[..7] : string.Empty;
    }

    private static string? Clean(string? value, int maximum)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var text = new string(value.Where(c => !char.IsControl(c)).ToArray()).Trim();
        return text.Length > maximum ? text[..maximum] : text;
    }

    /// <summary>The last [FAIL] line is the manager's safe diagnostic; never echo raw stack output.</summary>
    internal static string DescribeFailure(Models.CommandResult result)
    {
        var text = (result.StandardError + "\n" + result.StandardOutput).Replace("\r", string.Empty);
        var failure = text.Split('\n')
            .Select(line => line.Trim())
            .LastOrDefault(line => line.StartsWith("[FAIL] ", StringComparison.Ordinal));
        if (failure is not null)
        {
            return Clean(failure["[FAIL] ".Length..], 300) ?? "The operation failed.";
        }

        if (text.Contains("No such file or directory", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("command not found", StringComparison.OrdinalIgnoreCase))
        {
            return "This distro does not support custom mods yet. Run Update Distro first.";
        }

        return $"The operation failed (exit code {result.ExitCode}).";
    }

    private abstract class VersionedDocument
    {
        public int SchemaVersion { get; set; }
    }

    private sealed class StatusDocument : VersionedDocument
    {
        public List<StatusEntry>? Mods { get; set; }
    }

    private sealed class StatusEntry
    {
        public string? Id { get; set; }
        public string? Name { get; set; }
        public string? Description { get; set; }
        public string? Repository { get; set; }
        public string? Branch { get; set; }
        public string? Commit { get; set; }
        public string? State { get; set; }
        public string? Message { get; set; }
        public string? DashboardUrl { get; set; }
        public string? HealthUrl { get; set; }
        public string? Health { get; set; }
        public string? HealthMessage { get; set; }
        public string? IconUrl { get; set; }
        public string? BannerUrl { get; set; }
        public BranchPolicyDocument? Branches { get; set; }
        public string? BranchesCheckedAt { get; set; }
    }

    private sealed class BranchPolicyDocument
    {
        public string? Default { get; set; }
        public List<string>? Allowed { get; set; }
    }

    private sealed class BranchCheckDocument : VersionedDocument
    {
        public string? Id { get; set; }
        public string? CurrentBranch { get; set; }
        public string? CurrentCommit { get; set; }
        public BranchPolicyDocument? Branches { get; set; }
        public string? Branch { get; set; }
        public string? Commit { get; set; }
    }

    private sealed class PreviewDocument : VersionedDocument
    {
        public string? Id { get; set; }
        public string? Name { get; set; }
        public string? Description { get; set; }
        public string? Repository { get; set; }
        public string? Branch { get; set; }
        public string? Commit { get; set; }
        public string? Folder { get; set; }
        public string? Database { get; set; }
        public bool Registered { get; set; }
        public bool Restorable { get; set; }
        public string? InstalledCommit { get; set; }
    }
}

public enum CustomModState
{
    Ready,
    Busy,
    Failed
}

public sealed record CustomModInfo(
    string Id,
    string Name,
    string Description,
    string Repository,
    string Branch,
    string ShortCommit,
    CustomModState State,
    string Message,
    string? DashboardUrl,
    string? HealthUrl,
    string? Health,
    string? HealthMessage,
    string? IconUrl,
    string? BannerUrl)
{
    /// <summary>The installed icon, loaded by the view model; null shows the name alone.</summary>
    public System.Windows.Media.ImageSource? IconImage { get; init; }

    /// <summary>The author's last checked policy from the distro registry; never fetched on paint.</summary>
    public string DefaultBranch { get; init; } = string.Empty;

    public IReadOnlyList<string> OfferedBranches { get; init; } = [];

    public string BranchesCheckedAt { get; init; } = string.Empty;

    /// <summary>False when the author stopped offering the installed branch; Update still keeps it.</summary>
    public bool IsBranchOffered => OfferedBranches.Count == 0 || OfferedBranches.Contains(Branch, StringComparer.Ordinal);
}

public sealed record CustomModPreview(
    string Id,
    string Name,
    string Description,
    string Repository,
    string Branch,
    string Commit,
    string Folder,
    string Database,
    bool Registered,
    bool Restorable,
    string InstalledCommit);

/// <summary>A refreshed branch policy and, when a target was given, its exact commit to review.</summary>
public sealed record CustomModBranchCheck(
    string Id,
    string CurrentBranch,
    string CurrentCommit,
    string DefaultBranch,
    IReadOnlyList<string> OfferedBranches,
    string Branch,
    string Commit);

public sealed record CustomModBranchCheckResult(CustomModBranchCheck? Check, string? Error);

public sealed record CustomModStatusResult(IReadOnlyList<CustomModInfo>? Mods, string? Error);

public sealed record CustomModPreviewResult(CustomModPreview? Preview, string? Error);
