using System.Text.Json;
using DwemerDistro.Launcher.Wpf.Models;

namespace DwemerDistro.Launcher.Wpf.Services;

/// <summary>
/// Typed front end for <c>/usr/local/bin/ddistro_server</c>.
///
/// Every command is assembled from an allowlist: the product and branch tokens come from
/// <see cref="ServerProduct"/> and <see cref="ServerBranchChannel"/> switch expressions, never from
    /// user text, and the arguments go to <see cref="WslService.RunDistroAsUserAsync"/> as a real
/// argument vector rather than a bash string. That keeps a hostile branch name or product key from
/// reaching a shell. Progress is streamed line by line so long installs stay legible in the console.
/// </summary>
public sealed class ServerManagementService(WslService wsl)
{
    /// <summary>The only status schema this launcher build understands.</summary>
    public const int SupportedSchemaVersion = 1;

    internal const string ManagerCommand = "/usr/local/bin/ddistro_server";

    // The distro emits snake_case field names (schema_version, repository_state, database_present,
    // production_branch, development_branch), so the naming policy has to match or every one of them
    // silently reads back as its default.
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true
    };

    public async Task<ServerStatusResult> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        Models.CommandResult result;
        try
        {
            result = await wsl.RunDistroAsUserAsync(
                    LauncherConstants.DistroUser,
                    BuildStatusArguments(),
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return ServerStatusResult.Failed(ex.Message);
        }

        if (!result.Succeeded)
        {
            return ServerStatusResult.Failed(DescribeFailure(result));
        }

        return TryParseStatus(result.StandardOutput, out var snapshot, out var error)
            ? ServerStatusResult.Succeeded(snapshot!)
            : ServerStatusResult.Failed(error!);
    }

    public Task<Models.CommandResult> InstallAsync(
        ServerProduct product,
        ServerBranchChannel branch,
        Action<string>? output = null,
        CancellationToken cancellationToken = default)
    {
        return RunManagerAsync(BuildInstallArguments(product, branch), output, cancellationToken);
    }

    /// <summary>
    /// Updates an existing install. The manager never installs a missing product, and callers must
    /// still gate on <see cref="ServerStatus.IsUsable"/> so a not-installed product is never asked.
    /// </summary>
    /// <param name="forceGitUpdates">
    /// Passes <c>--force</c>, which lets the manager update over a dirty worktree by discarding
    /// manual edits to tracked files. Only the launcher's confirmed Force Updates setting turns
    /// this on, and only for update: install and repair never force.
    /// </param>
    public Task<Models.CommandResult> UpdateAsync(
        ServerProduct product,
        ServerBranchChannel branch,
        bool forceGitUpdates = false,
        Action<string>? output = null,
        CancellationToken cancellationToken = default)
    {
        return RunManagerAsync(BuildUpdateArguments(product, branch, forceGitUpdates), output, cancellationToken);
    }

    public Task<Models.CommandResult> RepairAsync(
        ServerProduct product,
        ServerBranchChannel branch,
        Action<string>? output = null,
        CancellationToken cancellationToken = default)
    {
        return RunManagerAsync(BuildRepairArguments(product, branch), output, cancellationToken);
    }

    public Task<Models.CommandResult> UninstallAsync(
        ServerProduct product,
        Action<string>? output = null,
        CancellationToken cancellationToken = default)
    {
        return RunManagerAsync(BuildUninstallArguments(product), output, cancellationToken);
    }

    private Task<Models.CommandResult> RunManagerAsync(
        IReadOnlyList<string> arguments,
        Action<string>? output,
        CancellationToken cancellationToken)
    {
        // Lifecycle mutations own system files, Apache configuration, and PostgreSQL databases.
        // WSL's explicit root user avoids interactive sudo prompts and keeps credentials out of
        // launcher input while the read-only status probe continues under the application account.
        return wsl.RunDistroAsUserAsync("root", arguments, output, cancellationToken);
    }

    // --- Command construction -------------------------------------------------------------
    // stdbuf keeps the manager's progress line-buffered through the redirected pipe; without it a
    // long install would arrive in one block at the end.

    internal static string[] BuildStatusArguments()
    {
        return [ManagerCommand, "status", "all", "--json"];
    }

    internal static string[] BuildInstallArguments(ServerProduct product, ServerBranchChannel branch)
    {
        return BuildBranchOperationArguments("install", product, branch);
    }

    /// <summary>
    /// <c>--force</c> is appended last and only for update, so the manager's argument order stays the
    /// same as every other operation and a default update can never carry the destructive flag.
    /// </summary>
    internal static string[] BuildUpdateArguments(
        ServerProduct product,
        ServerBranchChannel branch,
        bool forceGitUpdates = false)
    {
        var arguments = BuildBranchOperationArguments("update", product, branch);
        return forceGitUpdates ? [.. arguments, "--force"] : arguments;
    }

    internal static string[] BuildRepairArguments(ServerProduct product, ServerBranchChannel branch)
    {
        return BuildBranchOperationArguments("repair", product, branch);
    }

    internal static string[] BuildUninstallArguments(ServerProduct product)
    {
        return
        [
            "stdbuf", "-oL", "-eL",
            ManagerCommand, "uninstall", ToProductToken(product), "--confirm", GetPurgeToken(product)
        ];
    }

    private static string[] BuildBranchOperationArguments(
        string verb,
        ServerProduct product,
        ServerBranchChannel branch)
    {
        return
        [
            "stdbuf", "-oL", "-eL",
            ManagerCommand, verb, ToProductToken(product), "--branch", ToBranchToken(branch)
        ];
    }

    /// <summary>Allowlisted product token. An out-of-range enum value throws instead of forwarding.</summary>
    public static string ToProductToken(ServerProduct product)
    {
        return product switch
        {
            ServerProduct.Herika => "herika",
            ServerProduct.Stobe => "stobe",
            ServerProduct.Dialectic => "dialectic",
            _ => throw new ArgumentOutOfRangeException(nameof(product), product, "Unknown server product.")
        };
    }

    /// <summary>Allowlisted branch token. Only the manager's two channels are representable.</summary>
    public static string ToBranchToken(ServerBranchChannel branch)
    {
        return branch switch
        {
            ServerBranchChannel.Main => "main",
            ServerBranchChannel.Dev => "dev",
            _ => throw new ArgumentOutOfRangeException(nameof(branch), branch, "Unknown branch channel.")
        };
    }

    /// <summary>
    /// The exact token the uninstall dialog makes the user type, and the only value ever passed to
    /// <c>--confirm</c>.
    /// </summary>
    public static string GetPurgeToken(ServerProduct product)
    {
        return product switch
        {
            ServerProduct.Herika => "PURGE-HERIKA",
            ServerProduct.Stobe => "PURGE-STOBE",
            ServerProduct.Dialectic => "PURGE-DIALECTIC",
            _ => throw new ArgumentOutOfRangeException(nameof(product), product, "Unknown server product.")
        };
    }

    /// <summary>Maps a launcher branch choice ("Main"/"Dev") onto the manager's channel.</summary>
    public static ServerBranchChannel ParseBranchChannel(string? choice)
    {
        return string.Equals(choice?.Trim(), "Dev", StringComparison.OrdinalIgnoreCase)
            ? ServerBranchChannel.Dev
            : ServerBranchChannel.Main;
    }

    public static string ToBranchChoice(ServerBranchChannel branch)
    {
        return branch == ServerBranchChannel.Dev ? "Dev" : "Main";
    }

    /// <summary>The display name used in the Mods page, the console, and the uninstall dialog.</summary>
    public static string GetDisplayName(ServerProduct product)
    {
        return product switch
        {
            ServerProduct.Herika => "HerikaServer",
            ServerProduct.Stobe => "StobeServer",
            ServerProduct.Dialectic => "DialecticServer",
            _ => throw new ArgumentOutOfRangeException(nameof(product), product, "Unknown server product.")
        };
    }

    /// <summary>Maps a rail/game key ("CHIM", "STOBE", "DIALECTIC") onto a product.</summary>
    public static ServerProduct? TryParseGameKey(string? gameKey)
    {
        return gameKey?.Trim().ToUpperInvariant() switch
        {
            "CHIM" => ServerProduct.Herika,
            "STOBE" => ServerProduct.Stobe,
            "DIALECTIC" => ServerProduct.Dialectic,
            _ => null
        };
    }

    // --- Status parsing -------------------------------------------------------------------

    /// <summary>
    /// Parses the versioned status document. An unexpected schema version is a hard failure: a
    /// newer distro may rename states, and guessing would show the wrong install state.
    /// </summary>
    internal static bool TryParseStatus(
        string? json,
        out ServerStatusSnapshot? snapshot,
        out string? error)
    {
        snapshot = null;
        error = null;

        var payload = ExtractJsonObject(json);
        if (payload is null)
        {
            error = "The server manager did not return a status document.";
            return false;
        }

        StatusDocument? document;
        try
        {
            document = JsonSerializer.Deserialize<StatusDocument>(payload, JsonOptions);
        }
        catch (JsonException ex)
        {
            error = $"The server manager status could not be read: {ex.Message}";
            return false;
        }

        if (document is null)
        {
            error = "The server manager returned an empty status document.";
            return false;
        }

        if (document.SchemaVersion != SupportedSchemaVersion)
        {
            error = $"Unsupported server status schema version {document.SchemaVersion}. " +
                    $"This launcher understands version {SupportedSchemaVersion}. Update the launcher.";
            return false;
        }

        var servers = new List<ServerStatus>();
        foreach (var entry in document.Servers ?? [])
        {
            var product = ParseProduct(entry.Product);
            if (product is null)
            {
                // A product this build does not know is ignored rather than fatal, so an older
                // launcher keeps managing the three products it does understand.
                continue;
            }

            servers.Add(new ServerStatus(
                product.Value,
                ParseInstallState(entry.State),
                ParseRepositoryState(entry.RepositoryState),
                entry.DatabasePresent,
                NullIfBlank(entry.Root),
                NullIfBlank(entry.Database),
                NullIfBlank(entry.Branch),
                NullIfBlank(entry.Version),
                NullIfBlank(entry.ProductionBranch),
                NullIfBlank(entry.DevelopmentBranch),
                entry.Port));
        }

        snapshot = new ServerStatusSnapshot(document.SchemaVersion, servers);
        return true;
    }

    /// <summary>
    /// The manager may print progress before the JSON payload, so take the outermost object rather
    /// than assuming stdout is pure JSON.
    /// </summary>
    private static string? ExtractJsonObject(string? output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return null;
        }

        var start = output.IndexOf('{');
        var end = output.LastIndexOf('}');
        return start >= 0 && end > start ? output[start..(end + 1)] : null;
    }

    internal static ServerProduct? ParseProduct(string? value)
    {
        return value?.Trim().ToLowerInvariant() switch
        {
            "herika" => ServerProduct.Herika,
            "stobe" => ServerProduct.Stobe,
            "dialectic" => ServerProduct.Dialectic,
            _ => null
        };
    }

    internal static ServerInstallState ParseInstallState(string? value)
    {
        return value?.Trim().ToLowerInvariant() switch
        {
            "not-installed" => ServerInstallState.NotInstalled,
            "installed" => ServerInstallState.Installed,
            "needs-repair" => ServerInstallState.NeedsRepair,
            _ => ServerInstallState.Unknown
        };
    }

    internal static ServerRepositoryState ParseRepositoryState(string? value)
    {
        return value?.Trim().ToLowerInvariant() switch
        {
            "absent" => ServerRepositoryState.Absent,
            "legacy" => ServerRepositoryState.Legacy,
            "managed" => ServerRepositoryState.Managed,
            _ => ServerRepositoryState.Unknown
        };
    }

    private static string? NullIfBlank(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static string DescribeFailure(Models.CommandResult result)
    {
        var text = (result.StandardError + result.StandardOutput).Trim();
        if (text.Contains("command not found", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("No such file or directory", StringComparison.OrdinalIgnoreCase))
        {
            return "The distro does not provide ddistro_server yet. Run Update Distro to install the server manager.";
        }

        return string.IsNullOrWhiteSpace(text) ? $"Exit code {result.ExitCode}." : text;
    }

    private sealed class StatusDocument
    {
        public int SchemaVersion { get; set; }

        public List<StatusEntry>? Servers { get; set; }
    }

    private sealed class StatusEntry
    {
        public string? Product { get; set; }

        public string? State { get; set; }

        public string? RepositoryState { get; set; }

        public bool? DatabasePresent { get; set; }

        public string? Root { get; set; }

        public string? Database { get; set; }

        public string? Branch { get; set; }

        public string? Version { get; set; }

        public string? ProductionBranch { get; set; }

        public string? DevelopmentBranch { get; set; }

        public int? Port { get; set; }
    }
}

/// <summary>Status probe outcome. A failure keeps the previous UI state instead of clearing it.</summary>
public sealed record ServerStatusResult(ServerStatusSnapshot? Snapshot, string? Error)
{
    public bool IsSuccess => Snapshot is not null;

    public static ServerStatusResult Succeeded(ServerStatusSnapshot snapshot)
    {
        return new ServerStatusResult(snapshot, null);
    }

    public static ServerStatusResult Failed(string error)
    {
        return new ServerStatusResult(null, error);
    }
}
