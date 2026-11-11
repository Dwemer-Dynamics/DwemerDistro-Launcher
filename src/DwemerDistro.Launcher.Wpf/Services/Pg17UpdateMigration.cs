using System.Diagnostics;
using DwemerDistro.Launcher.Wpf.Models;

namespace DwemerDistro.Launcher.Wpf.Services;

/// <summary>What <c>ddistro_pg17_maintenance status</c> reports, always with exit code 0.</summary>
internal enum Pg17Status
{
    Needed,
    Current,
    Held,
    Unsupported
}

internal enum Pg17MigrationOutcome
{
    /// <summary>Nothing to do: already PostgreSQL 17, held on 15 by the user, unsupported, or no tool shipped.</summary>
    NotNeeded,
    Upgraded,
    /// <summary>A game was still running and the wait was stopped. Nothing was changed.</summary>
    Deferred,
    Failed
}

internal sealed record Pg17MigrationResult(Pg17MigrationOutcome Outcome, string Message)
{
    public bool AllowsUpdateToFinish => Outcome is Pg17MigrationOutcome.NotNeeded or Pg17MigrationOutcome.Upgraded;
}

/// <summary>
/// The unattended PostgreSQL 17 step of the normal distro update. It runs after core provisioning, never
/// asks anything, and waits while a supported game is open so a live save is never migrated underneath
/// it. The distro's maintenance tool owns the database work; its exit code is the only success signal.
/// Every dependency is a delegate so the orchestration runs against fakes or an isolated distro.
/// <paramref name="checkFreeSpaceAsync"/> throws <see cref="InvalidOperationException"/> when Windows lacks room
/// for the upgrade; it writes nothing.
/// </summary>
internal sealed class Pg17UpdateMigration(
    Func<string[], Action<string>, CancellationToken, Task<CommandResult>> runToolAsRoot,
    Func<IReadOnlyList<string>> findRunningGames,
    Func<CancellationToken, Task> checkFreeSpaceAsync,
    Func<TimeSpan, CancellationToken, Task> delayAsync,
    TimeSpan recheckInterval)
{
    internal const string ToolPath = "/usr/local/bin/ddistro_pg17_maintenance";
    internal static readonly TimeSpan DefaultRecheckInterval = TimeSpan.FromSeconds(30);

    /// <summary>Windows process names (no extension) of every game a DwemerDistro mod talks to.</summary>
    internal static readonly IReadOnlyDictionary<string, string> GameProcesses = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["SkyrimSE"] = "Skyrim Special Edition",
        ["SkyrimVR"] = "Skyrim VR",
        ["TESV"] = "Skyrim",
        ["Fallout4"] = "Fallout 4",
        ["Fallout4VR"] = "Fallout 4 VR",
        ["FalloutNV"] = "Fallout: New Vegas",
        ["Kenshi_x64"] = "Kenshi",
        ["Kenshi"] = "Kenshi",
        ["Morrowind"] = "Morrowind",
        ["openmw"] = "OpenMW",
        ["Bannerlord"] = "Mount & Blade II: Bannerlord"
    };

    /// <summary>Raised with the open games each time a recheck still finds one, and with an empty list once the wait ends.</summary>
    public event Action<IReadOnlyList<string>>? WaitingForGamesChanged;

    internal static string[] BuildStatusArguments() => [ToolPath, "status"];

    internal static string[] BuildAutoArguments() => [ToolPath, "auto"];

    /// <summary>
    /// An older maintenance tool still requires a Windows backup folder, which the launcher no longer passes.
    /// It refuses with this reason before touching the database.
    /// </summary>
    internal static bool IsBackupFolderRequiredRefusal(CommandResult result) =>
        !result.Succeeded
        && (result.StandardError.Contains(OlderToolBackupRefusal, StringComparison.Ordinal)
            || result.StandardOutput.Contains(OlderToolBackupRefusal, StringComparison.Ordinal));

    private const string OlderToolBackupRefusal = "Select a Windows backup directory";

    internal const string BackupFolderRequiredMessage =
        "This distro's database maintenance tool is from an older release that needs a backup folder, so the upgrade " +
        "did not start and the database is still on PostgreSQL 15. Run Update Distro to install the current tool, then try again.";

    /// <summary>The last non-empty output line must be exactly one of the four documented words.</summary>
    internal static Pg17Status? ParseStatus(string? output)
    {
        var line = (output ?? string.Empty)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .LastOrDefault();
        return line switch
        {
            "needed" => Pg17Status.Needed,
            "current" => Pg17Status.Current,
            "held" => Pg17Status.Held,
            "unsupported" => Pg17Status.Unsupported,
            _ => null
        };
    }

    internal static IReadOnlyList<string> FindRunningGames()
    {
        var running = new List<string>();
        foreach (var (processName, title) in GameProcesses)
        {
            var processes = Process.GetProcessesByName(processName);
            try
            {
                if (processes.Length > 0 && !running.Contains(title)) running.Add(title);
            }
            finally
            {
                foreach (var process in processes) process.Dispose();
            }
        }

        return running;
    }

    // Core provisioning can also start services and refresh grants. Wait before that phase
    // and check once more after the free-space check, immediately before starting cutover.
    internal async Task WaitForIdleAsync(Action<string> log, CancellationToken token)
    {
        var announced = false;
        try
        {
            for (var games = findRunningGames(); games.Count > 0; games = findRunningGames())
            {
                if (!announced) log($"Waiting for {string.Join(", ", games)} to close before updating the distro.\n");
                announced = true;
                WaitingForGamesChanged?.Invoke(games);
                await delayAsync(recheckInterval, token).ConfigureAwait(false);
            }
            token.ThrowIfCancellationRequested();
        }
        finally
        {
            if (announced) WaitingForGamesChanged?.Invoke([]);
        }
    }

    /// <summary>
    /// Runs status, waits for games, checks free space and runs <c>auto</c>. Cancelling
    /// <paramref name="waitToken"/> before <c>auto</c> starts returns <see cref="Pg17MigrationOutcome.Deferred"/>;
    /// once <c>auto</c> starts it always runs to its own end, because stopping wsl.exe does not stop root
    /// work inside the distro and the tool is idempotent on the next run.
    /// </summary>
    public async Task<Pg17MigrationResult> RunAsync(Action<string> log, CancellationToken waitToken)
    {
        var announcedGames = string.Empty;
        try
        {
            var probe = await runToolAsRoot(["test", "-x", ToolPath], _ => { }, waitToken).ConfigureAwait(false);
            // Only test's own "false" means the tool is absent. Any other failure (WSL could not run the
            // command, the update lock is held) must not let the update record a release over PostgreSQL 15.
            if (probe.ExitCode == 1)
            {
                return new(Pg17MigrationOutcome.NotNeeded,
                    "This distro release does not include the PostgreSQL 17 maintenance tool; the database is unchanged.");
            }

            if (!probe.Succeeded)
            {
                return new(Pg17MigrationOutcome.Failed,
                    $"the PostgreSQL 17 maintenance tool check failed (exit {probe.ExitCode}): {LastLine(probe)}");
            }

            while (true)
            {
                var status = await runToolAsRoot(BuildStatusArguments(), _ => { }, waitToken).ConfigureAwait(false);
                if (!status.Succeeded)
                {
                    return new(Pg17MigrationOutcome.Failed,
                        $"the PostgreSQL 17 status check failed (exit {status.ExitCode}): {LastLine(status)}");
                }

                switch (ParseStatus(status.StandardOutput))
                {
                    case Pg17Status.Current:
                        return new(Pg17MigrationOutcome.NotNeeded, "The database already runs PostgreSQL 17.");
                    case Pg17Status.Held:
                        return new(Pg17MigrationOutcome.NotNeeded,
                            "The database is held on PostgreSQL 15 after a rollback, so it was not upgraded.");
                    case Pg17Status.Unsupported:
                        return new(Pg17MigrationOutcome.NotNeeded,
                            "This database layout can't be upgraded automatically; it was left unchanged.");
                    case null:
                        return new(Pg17MigrationOutcome.Failed, "the PostgreSQL 17 status check gave an unrecognized answer");
                }

                var games = findRunningGames();
                if (games.Count == 0) break;

                var names = string.Join(", ", games);
                if (!string.Equals(names, announcedGames, StringComparison.Ordinal))
                {
                    announcedGames = names;
                    log($"The PostgreSQL 17 database upgrade is waiting for {names} to close. " +
                        "It continues on its own once the game is closed." + Environment.NewLine);
                }

                WaitingForGamesChanged?.Invoke(games);
                await delayAsync(recheckInterval, waitToken).ConfigureAwait(false);
            }

            if (announcedGames.Length > 0) WaitingForGamesChanged?.Invoke([]);
            await checkFreeSpaceAsync(waitToken).ConfigureAwait(false);
            await WaitForIdleAsync(log, waitToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (waitToken.IsCancellationRequested)
        {
            if (announcedGames.Length > 0) WaitingForGamesChanged?.Invoke([]);
            return new(Pg17MigrationOutcome.Deferred,
                "the PostgreSQL 17 database upgrade was postponed before it started; nothing was changed");
        }
        catch (InvalidOperationException ex)
        {
            if (announcedGames.Length > 0) WaitingForGamesChanged?.Invoke([]);
            return new(Pg17MigrationOutcome.Failed, $"the PostgreSQL 17 upgrade could not start: {ex.Message.TrimEnd('.')}");
        }

        var result = await runToolAsRoot(BuildAutoArguments(), log, CancellationToken.None).ConfigureAwait(false);
        return result.Succeeded
            ? new(Pg17MigrationOutcome.Upgraded, "The database was upgraded to PostgreSQL 17.")
            : IsBackupFolderRequiredRefusal(result)
                ? new(Pg17MigrationOutcome.Failed, "the distro's database maintenance tool is from an older release that needs a backup folder, so the " +
                    "PostgreSQL 17 upgrade did not start; the database is still on PostgreSQL 15")
                : new(Pg17MigrationOutcome.Failed, $"the PostgreSQL 17 upgrade failed (exit {result.ExitCode}): {LastLine(result)}");
    }

    private static string LastLine(CommandResult result)
    {
        var text = string.IsNullOrWhiteSpace(result.StandardError) ? result.StandardOutput : result.StandardError;
        var line = (text ?? string.Empty).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault();
        return string.IsNullOrEmpty(line) ? "no output" : DiagnosticEvidenceService.Sanitize(line);
    }
}
