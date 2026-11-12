using System.IO;
using DwemerDistro.Launcher.Wpf.Models;
using DwemerDistro.Launcher.Wpf.Services;

namespace DwemerDistro.Launcher.Wpf.ViewModels;

/// <summary>
/// The Debian Bookworm to Trixie step of the distro update, through <see cref="TrixieUpdateMigration"/>. Rollout is off
/// unless <c>"TrixieMaintenanceLab": true</c> is in update preferences; with it off and no journal the launcher does
/// exactly what it did before. A journal that says an upgrade is unfinished blocks every distro writer here, whatever
/// the rollout, until the step resolves it.
/// </summary>
/// <summary>
/// What the startup notice may say about a blocked Trixie step. <see cref="PastGameplayBoundary"/> is the journal's
/// irrevocable boundary; <see cref="ProvenCopyPath"/> is set only before it, for a copy that booted holding Core's fence.
/// </summary>
internal sealed record SystemUpgradeNoticeFacts(
    TrixieMaintenanceOutcome Outcome, bool JournalUnreadable, bool PastGameplayBoundary, string? ProvenCopyPath)
{
    internal static SystemUpgradeNoticeFacts From(TrixieMaintenanceResult result, (TrixieJournal? Journal, bool Unreadable) read)
    {
        var entry = read.Journal;
        var boundary = entry is { GameplayWriteBoundary: true } or { Phase: TrixieJournalPhase.Releasing or TrixieJournalPhase.Completed };
        // Once Core's cancel lifted its fence, writers may have run since the copy was taken: it is no longer named either.
        var copy = entry is { CopyVerified: true, FenceLifted: false, BackupSha256: not null, BackupPath: { Length: > 0 } path } && !boundary ? path : null;
        return new(result.Outcome, read.Unreadable, boundary, copy);
    }
}

public sealed partial class MainWindowViewModel
{
    private readonly TrixieJournalStore _systemUpgradeJournal = new(TrixieJournalStore.DefaultPath);
    private volatile bool _systemUpgradeBlocksWrites;
    // The passive distro leases the running update itself holds: the startup sync runs inside one.
    private int _systemUpdateOwnPassiveLeases;

    internal const string SystemUpgradeBlockedMessage =
        "DwemerDistro's Debian Trixie system upgrade is unfinished, so the server, mods and components stay stopped to protect " +
        "the recovery copy. Run Update Distro in Settings to finish or check it.";

    /// <summary>Where the offline recovery copies are written; Import Distro Backup opens here when it exists.</summary>
    internal static string SystemUpgradeBackupRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DwemerDistro", "SystemUpgradeBackups");

    private bool IsSystemUpgradeRolloutEnabled() => _updatePreferences.GetTrixieMaintenanceLabOptIn();

    /// <summary>Re-reads the journal (a file and HKCU, never WSL) and refreshes every gate that depends on it.</summary>
    private void RefreshSystemUpgradeGuard()
    {
        var blocked = TrixieJournalStore.BlocksWrites(_systemUpgradeJournal.Read(), _wsl.GetDistroRegistrationId());
        if (_systemUpgradeBlocksWrites == blocked) return;
        _systemUpgradeBlocksWrites = blocked;
        LauncherLogService.Startup(blocked ? "Distro writers blocked: the system upgrade journal is unfinished." : "Distro writers allowed: the system upgrade journal is settled.");
        NotifyDistroOperationGateChanged();
    }

    /// <summary>Windows that change files or settings inside the distro: closed while a Trixie upgrade is unfinished.</summary>
    private bool CanWriteDistro() => !_systemUpgradeBlocksWrites && CanAccessDistro();

    private bool RefuseWhileSystemUpgradeUnfinished()
    {
        if (!_systemUpgradeBlocksWrites) return false;
        AppendLog(SystemUpgradeBlockedMessage + Environment.NewLine, "yellow");
        return true;
    }

    internal TrixieUpdateMigration CreateSystemUpgradeMigration(PausableDeadline? deadline)
    {
        var wsl = _wsl;
        var processRunner = _processRunner;
        // The recovery copy is its own registration; every call names the distro it acts on.
        WslService For(string name) => string.Equals(name, wsl.DistroName, StringComparison.OrdinalIgnoreCase) ? wsl : new WslService(processRunner, name);
        var host = new TrixieMaintenanceHost
        {
            RunCoreAsync = (arguments, output, token) => wsl.RunDatabaseMaintenanceAsync(arguments, output, token),
            RunAsRootAsync = (name, arguments, token) => wsl.RunWslAsync(["-d", name, "-u", "root", "--exec", .. arguments], cancellationToken: token),
            // Core's read-only status, as root under the update lock and without start_env, so proving PG17 on a cold
            // distro never starts PostgreSQL or opens gameplay writes. Only "current" passes; why not is logged.
            IsPg17CurrentAsync = async token =>
            {
                var status = await wsl.RunDatabaseMaintenanceAsync(Pg17UpdateMigration.BuildStatusArguments(), _ => { }, token).ConfigureAwait(false);
                var parsed = status.Succeeded ? Pg17UpdateMigration.ParseStatus(status.StandardOutput) : null;
                if (parsed != Pg17Status.Current)
                    LauncherLogService.Operation(DescribePg17GateRefusal(status, parsed));
                return parsed == Pg17Status.Current;
            },
            FindRunningGames = Pg17UpdateMigration.FindRunningGames,
            IsServerRunning = () => IsServerRunning || IsServerStarting,
            DelayAsync = PauseDeadlineDuringDelay(Task.Delay, deadline),
            ReadRegistration = name => ReadSystemUpgradeRegistration(wsl, name),
            ReadVolumeFreeSpace = ReadBackingVolumeFreeSpace,
            EnterCutoverAsync = EnterSystemUpgradeCutoverAsync,
            TerminateAsync = async (name, token) =>
            {
                var distro = For(name);
                await distro.TerminateDistroAsync(cancellationToken: token).ConfigureAwait(false);
                return !await distro.DistroRunningAsync(token).ConfigureAwait(false);
            },
            IsRunningAsync = (name, token) => For(name).DistroRunningAsync(token),
            ExportAsync = (path, _, token) => RunArchiveOperationWithProgressAsync(
                callback => wsl.ExportDistroAsync(path, callback, token), path, "Recovery copy progress"),
            // WSL 2 explicitly: the copy's disk must be a VHDX that a restore can swap in.
            ImportAsync = (name, folder, archive, output, token) =>
                wsl.RunWslAsync(["--import", name, folder, archive, "--version", "2"], output, token),
            VerifyArchiveAsync = TrixieUpdateMigration.VerifyTarArchiveAsync,
            BackupDirectory = SystemUpgradeBackupRoot
        };
        var migration = new TrixieUpdateMigration(host, _systemUpgradeJournal, IsSystemUpgradeRolloutEnabled, deadline,
            Pg17UpdateMigration.DefaultRecheckInterval);
        // The same Stop waiting control as the database step: both are postponed before anything changes.
        migration.WaitingForGamesChanged += games =>
        {
            RunOnUi(() => IsDatabaseUpgradeWaiting = games.Count > 0);
            if (games.Count > 0)
                SetDatabaseMaintenanceStatus(BuildSystemUpgradeWaitingStatus(games), CompactDistroWarningColor);
        };
        return migration;
    }

    /// <summary>
    /// Why the PostgreSQL 17 gate held the Trixie step back. A Core whose status needs a running PostgreSQL 17 fails
    /// on a cold distro rather than answering; that is Core's to prove, and the launcher never starts the server for it.
    /// </summary>
    internal static string DescribePg17GateRefusal(CommandResult status, Pg17Status? parsed)
    {
        if (status.Succeeded)
            return $"Debian Trixie step: PostgreSQL 17 status is '{parsed?.ToString() ?? "unrecognized"}', not current; the upgrade waits.";
        var text = string.IsNullOrWhiteSpace(status.StandardError) ? status.StandardOutput : status.StandardError;
        var line = (text ?? string.Empty).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault();
        return $"Debian Trixie step: the PostgreSQL 17 status check failed (exit {status.ExitCode}): " +
               (string.IsNullOrEmpty(line) ? "no output" : DiagnosticEvidenceService.Sanitize(line)) +
               ". PostgreSQL was not started for it; the upgrade waits.";
    }

    internal static string BuildSystemUpgradeWaitingStatus(IReadOnlyList<string> games) =>
        $"Waiting for {string.Join(", ", games)} to close before upgrading the distro to Debian Trixie. " +
        "The distro update finishes on its own once the game is closed. Nothing has been changed yet.";

    /// <summary>
    /// The named WSL 2 registration from HKCU, never WSL: GUID, BasePath and the VhdFileName WSL recorded, plus the
    /// disk's file identity (null while the disk is not in place). Null when it is absent or its values are not plain.
    /// </summary>
    internal static TrixieRegistration? ReadSystemUpgradeRegistration(WslService wsl, string name)
    {
        if (wsl.ReadRegistration(name) is not { } fields) return null;
        var basePath = NormalizeDistroFolderPath(fields.BasePath);
        if (string.IsNullOrEmpty(basePath) || !IsPlainVhdFileName(fields.VhdFileName)) return null;
        return new TrixieRegistration(name, fields.Id, basePath, fields.VhdFileName,
            TrixieWindowsFiles.FileId(Path.Combine(basePath, fields.VhdFileName)));
    }

    internal static bool IsPlainVhdFileName(string? name) =>
        !string.IsNullOrWhiteSpace(name) && name == Path.GetFileName(name) && name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0
        && name.EndsWith(".vhdx", StringComparison.OrdinalIgnoreCase) && name.Trim() == name;

    /// <summary>The registration's BasePath as a plain fully-qualified folder; WSL may store it as <c>\\?\C:\...</c>.</summary>
    internal static string? NormalizeDistroFolderPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Contains('"', StringComparison.Ordinal) || path.Any(char.IsControl)) return null;
        var trimmed = path.StartsWith(@"\\?\", StringComparison.Ordinal) && !path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)
            ? path[4..]
            : path;
        try
        {
            return Path.IsPathFullyQualified(trimmed) ? Path.TrimEndingDirectorySeparator(Path.GetFullPath(trimmed)) : null;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    /// <summary>
    /// Claims the exclusive distro slot from inside the running update, so no status check, belt refresh or assistant
    /// session starts the distro during the recovery copy or upgrade. The update's own passive leases are excused.
    /// </summary>
    private async Task<IDisposable?> EnterSystemUpgradeCutoverAsync(CancellationToken token)
    {
        lock (_distroOperationGate)
        {
            if (_isExclusiveDistroOperationInProgress) return null;
            _isExclusiveDistroOperationInProgress = true;
        }

        NotifyDistroOperationGateChanged();
        var release = new CutoverRelease(this);
        if (!await PauseAssistantForMaintenanceAsync("Distro update").ConfigureAwait(false))
        {
            release.Dispose();
            return null;
        }

        release.AssistantPaused = true;
        var drainUntil = DateTime.UtcNow + PassiveStatusDrainTimeout;
        while (true)
        {
            int passive;
            lock (_distroOperationGate) passive = _passiveDistroActivityCount;
            if (passive <= Volatile.Read(ref _systemUpdateOwnPassiveLeases)) return release;
            if (DateTime.UtcNow >= drainUntil || token.IsCancellationRequested)
            {
                release.Dispose();
                return null;
            }

            await Task.Delay(PassiveStatusDrainPollInterval, CancellationToken.None).ConfigureAwait(false);
        }
    }

    private sealed class CutoverRelease(MainWindowViewModel owner) : IDisposable
    {
        private int _disposed;
        public bool AssistantPaused { get; set; }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            // Before the slot opens, so nothing that writes is offered over an upgrade that just failed.
            owner.RefreshSystemUpgradeGuard();
            owner.SetExclusiveDistroOperationInProgress(false);
            if (AssistantPaused) owner.ResumeAssistantAfterMaintenance(reconnect: !owner._systemUpgradeBlocksWrites);
        }
    }

    /// <summary>
    /// The Trixie step alone, under the same update gate as Update Distro: crash recovery at startup, before any writer
    /// and never after the generic updater, and the OS check on a launch whose release is already recorded.
    /// Null when another operation held the gate and nothing ran.
    /// </summary>
    private async Task<TrixieMaintenanceResult?> RunSystemUpgradeStepAsync(PausableDeadline deadline, bool allowFreshStart)
    {
        if (!CanRunSystemUpdateOperation()) return null;
        IsDistroUpdateInProgress = true;
        SetSystemUpdateRunningButtonText("Updating Distro...");
        SetSystemUpdateState(SystemUpdateAvailability.Updating);
        using var updateCts = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token, _shutdownCts.Token);
        using var waitCts = BeginDatabaseUpgradeWait(updateCts.Token);
        TrixieMaintenanceResult? result = null;
        try
        {
            var migration = CreateSystemUpgradeMigration(deadline);
            var task = Task.Run(() => migration.RunAsync(text =>
            {
                LauncherLogService.Operation(text);
                AppendLog(text);
            }, allowFreshStart, waitCts.Token, updateCts.Token));
            _distroUpdateTask = task;
            result = await task.ConfigureAwait(true);
            ReportSystemUpgradeResult(result);
            return result;
        }
        finally
        {
            EndDatabaseUpgradeWait(waitCts);
            RefreshSystemUpgradeGuard();
            ReportSystemUpdateOutcome(result is { AllowsUpdateToFinish: true });
            CompleteUpdateOperation();
        }
    }

    private void ReportSystemUpgradeResult(TrixieMaintenanceResult result)
    {
        if (result.Outcome == TrixieMaintenanceOutcome.NotActive) return;
        LauncherLogService.Operation($"Debian Trixie step: {result.Outcome}; writes blocked={result.LeavesWritesBlocked}; {result.Message}");
        if (BuildSystemUpgradeConsoleLine(result) is { } line) AppendLog(line.Text + Environment.NewLine, line.Color);
        if (result.Outcome is TrixieMaintenanceOutcome.Deferred or TrixieMaintenanceOutcome.RecoveryRequired or TrixieMaintenanceOutcome.Completed
            or TrixieMaintenanceOutcome.Restored)
            SetDatabaseMaintenanceStatus(BuildSystemUpgradeConsoleLine(result)!.Value.Text,
                result.LeavesWritesBlocked ? CompactDistroErrorColor : CompactDistroNeutralColor);
    }

    /// <summary>What the console says about the step; null for the silent outcomes, so an inactive rollout prints nothing.</summary>
    internal static (string Text, string Color)? BuildSystemUpgradeConsoleLine(TrixieMaintenanceResult result) => result.Outcome switch
    {
        TrixieMaintenanceOutcome.NotActive or TrixieMaintenanceOutcome.NotNeeded => null,
        TrixieMaintenanceOutcome.Completed => (result.Message, "green"),
        TrixieMaintenanceOutcome.Restored => (result.Message.TrimEnd('.') + ".", "yellow"),
        TrixieMaintenanceOutcome.RecoveryRequired => ($"Debian Trixie upgrade needs recovery: {result.Message.TrimEnd('.')}. " +
            "The server, mods and components stay stopped until Update Distro finishes or verifies it.", "red"),
        _ when result.LeavesWritesBlocked => ($"Debian Trixie upgrade unfinished: {result.Message.TrimEnd('.')}.", "red"),
        _ => ($"Debian Trixie upgrade not done this time: {result.Message.TrimEnd('.')}. It is tried again on a later launch.", "yellow")
    };

    /// <summary>The journal as the step left it, read once for the startup notice. Null without a result.</summary>
    private SystemUpgradeNoticeFacts? ReadSystemUpgradeNoticeFacts(TrixieMaintenanceResult? result) =>
        result is null ? null : SystemUpgradeNoticeFacts.From(result, _systemUpgradeJournal.Read());

    /// <summary>
    /// The startup notice for a step that left distro writes blocked. A deferral is retried on its own and raises no
    /// dialog; the console and the server's own refusal already explain it. Past the journal's gameplay-write boundary,
    /// or when that boundary can't be read, no restore is ever suggested: the recovery copy is older than progress the
    /// upgraded distro may have accepted. Before it, a checked copy is named, and only with a request to get help first.
    /// </summary>
    internal static StartupDistroUpdateNotice? BuildSystemUpgradeStartupNotice(string title, string? failure, SystemUpgradeNoticeFacts? facts)
    {
        const string heading = "DwemerDistro's Debian Trixie upgrade isn't finished";
        const string stopped = " The server, mods and components stay stopped.";
        const string help = "use Generate Distro Debugging Logs on the Logs tab and ask for help";
        var details = BuildStartupNoticeDetails(failure);
        if (facts is { Outcome: TrixieMaintenanceOutcome.Deferred, JournalUnreadable: false }) return null;
        if (facts is null or { JournalUnreadable: true })
            return new(title, heading,
                (facts is null
                    ? "The launcher could not confirm that the Debian Trixie upgrade finished."
                    : "The launcher can't read its record of the Debian Trixie upgrade, so it can't tell how far an upgrade got. It changed nothing.") + stopped,
                details,
                $"Don't import or restore a backup over this distro: it may hold progress newer than any backup. Use Update Distro in Settings to check it again; if it still isn't finished, {help}.",
                IsWarning: false);
        if (facts.PastGameplayBoundary)
            return new(title, "DwemerDistro's Debian Trixie upgrade needs a final check",
                "The upgraded distro passed its check and the launcher began releasing its services, so it may already hold new game progress. " +
                "Its release could not be confirmed." + stopped,
                details,
                $"Use Update Distro in Settings to finish it. Don't restore the Bookworm recovery copy: it is older than this distro and would discard progress made since the upgrade. If it still can't finish, {help}.",
                IsWarning: false);
        var copy = facts.ProvenCopyPath is { } path
            ? $" A checked recovery copy from before the upgrade is at {path}; Core's fence kept game progress from being saved after it was made."
            : " This attempt has no checked recovery copy.";
        return new(title, heading,
            "The launcher stopped the upgrade's automatic recovery because it can't prove the distro's state, and it will not move, import or remove anything more on its own." +
            copy + stopped,
            details,
            facts.ProvenCopyPath is null
                ? $"Use Update Distro in Settings to check it again. Don't import an older backup over this distro; {help}."
                : $"Use Update Distro in Settings to check it again. If it still needs recovery, {help} before restoring that copy.",
            IsWarning: false);
    }

    internal static string DescribeSystemUpgradeFailure(TrixieMaintenanceResult result) =>
        $" during the Debian Trixie system upgrade: {result.Message.TrimEnd('.')}. Full output: Logs\\launcher-operations.log.";

    /// <summary>The codename read for the update badge, only when the Trixie tool is installed; empty otherwise.</summary>
    internal static string BuildSystemCodenameReadCommand() => TrixieUpdateMigration.CodenameReadScript;

    /// <summary>
    /// A matching release says nothing about the OS: with rollout on, a Bookworm distro that has the Trixie tool still
    /// has an update available, even when both release versions are equal.
    /// </summary>
    internal static SystemUpdateAvailability ResolveSystemUpdateAvailability(
        string? installedVersion, string? availableVersion, string? installedCodename, bool rolloutEnabled)
    {
        var release = ResolveSystemUpdateAvailability(installedVersion, availableVersion);
        return release == SystemUpdateAvailability.Current && rolloutEnabled
               && string.Equals(installedCodename?.Trim(), TrixieUpdateMigration.SourceCodename, StringComparison.Ordinal)
            ? SystemUpdateAvailability.UpdateAvailable
            : release;
    }
}
