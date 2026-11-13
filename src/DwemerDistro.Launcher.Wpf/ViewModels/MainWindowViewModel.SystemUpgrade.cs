using System.IO;
using DwemerDistro.Launcher.Wpf.Models;
using DwemerDistro.Launcher.Wpf.Services;

namespace DwemerDistro.Launcher.Wpf.ViewModels;

/// <summary>
/// The Debian Bookworm to Trixie step of the distro update, through <see cref="TrixieUpdateMigration"/>. Rollout is off
/// unless <c>"TrixieMaintenanceLab": true</c> is in update preferences; with it off and no journal the launcher does
/// exactly what it did before. A fresh upgrade starts only after the user confirms Upgrade DwemerDistro in the top
/// bar; a reopen, Quickstart or a mod update never begins one, and only resumes an upgrade already under way. A
/// journal that says an upgrade is unfinished blocks every distro writer here, whatever the rollout, until the step
/// resolves it.
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
        "the recovery copy. Reopen the launcher to finish or check it.";

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
        RunOnUi(RaiseDebianUpgradeAvailabilityChanged);
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
        // A fresh start never waits for a game: it is refused and the user confirms again once the game is closed.
        return new TrixieUpdateMigration(host, _systemUpgradeJournal, IsSystemUpgradeRolloutEnabled, deadline);
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
    /// and never after the generic updater. It only resumes or checks an upgrade already under way; it never starts one.
    /// Null when another operation held the gate and nothing ran.
    /// </summary>
    private async Task<TrixieMaintenanceResult?> RunSystemUpgradeStepAsync(PausableDeadline deadline)
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
            }, allowFreshStart: false, waitCts.Token, updateCts.Token));
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
        _lastSystemUpgradeResult = result;
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
            "The server, mods and components stay stopped until the launcher finishes or verifies it; reopen the launcher to retry.", "red"),
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
                $"Don't import or restore a backup over this distro: it may hold progress newer than any backup. Reopen the launcher to check it again; if it still isn't finished, {help}.",
                IsWarning: false);
        if (facts.PastGameplayBoundary)
            return new(title, "DwemerDistro's Debian Trixie upgrade needs a final check",
                "The upgraded distro passed its check and the launcher began releasing its services, so it may already hold new game progress. " +
                "Its release could not be confirmed." + stopped,
                details,
                $"Reopen the launcher to finish it. Don't restore the Bookworm recovery copy: it is older than this distro and would discard progress made since the upgrade. If it still can't finish, {help}.",
                IsWarning: false);
        var copy = facts.ProvenCopyPath is { } path
            ? $" A checked recovery copy from before the upgrade is at {path}; Core's fence kept game progress from being saved after it was made."
            : " This attempt has no checked recovery copy.";
        return new(title, heading,
            "The launcher stopped the upgrade's automatic recovery because it can't prove the distro's state, and it will not move, import or remove anything more on its own." +
            copy + stopped,
            details,
            facts.ProvenCopyPath is null
                ? $"Reopen the launcher to check it again. Don't import an older backup over this distro; {help}."
                : $"Reopen the launcher to check it again. If it still needs recovery, {help} before restoring that copy.",
            IsWarning: false);
    }

    internal static string DescribeSystemUpgradeFailure(TrixieMaintenanceResult result) =>
        $" during the Debian Trixie system upgrade: {result.Message.TrimEnd('.')}. Full output: Logs\\launcher-operations.log.";

    /// <summary>
    /// A matching release says nothing about the OS: with rollout on, a Bookworm distro that can be offered the Debian 13
    /// upgrade still has an update available, even when both release versions are equal.
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
    // --- The Debian 13 offer ---------------------------------------------------------------------------------------

    private TrixieUpgradeOffer? _debianUpgradeOffer;
    private int _debianUpgradeNoticeRaised;
    private TrixieMaintenanceResult? _lastSystemUpgradeResult;
    // "Checking..." during the fresh recheck, "Upgrading..." during the approved run; null when idle.
    private string? _debianUpgradeRunningText;

    /// <summary>The modeless notice that a Debian 13 upgrade can be started; separate from the failure notice, which outranks it.</summary>
    internal event EventHandler<StartupDistroUpdateNotice>? DebianUpgradeNoticeRaised;

    /// <summary>A fresh Debian 13 upgrade can be offered: rollout on, nothing under way, and the read-only checks passed.</summary>
    public bool IsDebianUpgradeAvailable => _debianUpgradeOffer is { Available: true } && !_systemUpgradeBlocksWrites;

    /// <summary>The top-bar button stays up through its own recheck and run, even once the journal hides the offer.</summary>
    public bool IsDebianUpgradeBarVisible => IsDebianUpgradeAvailable || _debianUpgradeRunningText is not null;

    /// <summary>The top-bar button's label: Upgrade DwemerDistro, or what its own recheck or run is doing.</summary>
    public string DebianUpgradeTopBarText => _debianUpgradeRunningText ?? DebianUpgradeButtonText;

    /// <summary>The top-bar button's tooltip and help text.</summary>
    public string DebianUpgradeAvailableText => _debianUpgradeOffer is { HasRoom: false }
        ? $"Upgrade DwemerDistro to Debian 13. There isn't enough free disk space for it yet; {DebianUpgradeButtonText} shows what it needs."
        : $"Upgrade DwemerDistro to Debian 13. {DebianUpgradeButtonText} rechecks and asks before anything changes.";

    /// <summary>
    /// Refreshes the offer with <see cref="TrixieUpdateMigration.PreviewAsync"/>, which changes nothing. With rollout off,
    /// or a completed or unfinished journal, it makes no WSL call. The first offer of a launch raises one modeless notice.
    /// </summary>
    private async Task RefreshDebianUpgradeOfferAsync(CancellationToken cancellationToken)
    {
        TrixieUpgradeOffer offer;
        try
        {
            offer = IsSystemUpgradePossiblyPending() && !_systemUpgradeBlocksWrites
                ? await CreateSystemUpgradeMigration(null).PreviewAsync(cancellationToken).ConfigureAwait(false)
                : TrixieUpgradeOffer.None("rollout off, or the journal is completed or unfinished");
        }
        catch (Exception ex) when (ex is OperationCanceledException or InvalidOperationException or IOException)
        {
            // A slow or failed preview only means nothing is offered yet; the release check still reports.
            offer = TrixieUpgradeOffer.None("the read-only check did not finish (" + ex.GetType().Name + ")");
        }

        if (!offer.Available && IsSystemUpgradeRolloutEnabled())
            LauncherLogService.Startup($"Debian 13 upgrade not offered: {offer.Reason}.");
        SetDebianUpgradeOffer(offer);
        RunOnUi(() =>
        {
            if (!IsDebianUpgradeAvailable || IsDistroUpdateInProgress || _shutdownCts.IsCancellationRequested
                || Interlocked.Exchange(ref _debianUpgradeNoticeRaised, 1) != 0)
                return;
            LauncherLogService.Startup("Debian 13 upgrade offered.");
            DebianUpgradeNoticeRaised?.Invoke(this, BuildDebianUpgradeAvailableNotice(offer));
        });
    }

    private void SetDebianUpgradeOffer(TrixieUpgradeOffer offer) => RunOnUi(() =>
    {
        _debianUpgradeOffer = offer;
        OnPropertyChanged(nameof(DebianUpgradeAvailableText));
        RaiseDebianUpgradeAvailabilityChanged();
    });

    /// <summary>The offer shows or hides the top-bar Upgrade DwemerDistro button and gates its command.</summary>
    private void RaiseDebianUpgradeAvailabilityChanged()
    {
        OnPropertyChanged(nameof(IsDebianUpgradeAvailable));
        OnPropertyChanged(nameof(IsDebianUpgradeBarVisible));
        UpgradeDwemerDistroCommand?.RaiseCanExecuteChanged();
    }

    private void SetDebianUpgradeRunningText(string? text)
    {
        _debianUpgradeRunningText = text;
        OnPropertyChanged(nameof(DebianUpgradeTopBarText));
        OnPropertyChanged(nameof(IsDebianUpgradeBarVisible));
        UpgradeDwemerDistroCommand?.RaiseCanExecuteChanged();
    }

    /// <summary>Modeless and only navigates: its Upgrade DwemerDistro opens the same recheck and review as the top-bar button.</summary>
    internal static StartupDistroUpdateNotice BuildDebianUpgradeAvailableNotice(TrixieUpgradeOffer offer) => new(
        DebianUpgradeButtonText,
        "Upgrade available",
        "Roughly 10-20 minutes. You can review the upgrade before starting." +
        (offer.HasRoom ? string.Empty : " Free up disk space before starting."),
        null,
        "Close this window to keep playing.",
        IsWarning: true,
        PrimaryActionText: DebianUpgradeButtonText);

    /// <summary>
    /// The top bar's Upgrade DwemerDistro: a fresh read-only recheck, then the confirmation, where Later closes it. A stale
    /// offer says so and hides the button; it never falls back to a routine update.
    /// </summary>
    private async Task UpgradeDwemerDistroAsync()
    {
        if (await ConfirmDebianUpgradeAsync().ConfigureAwait(true))
            await RunApprovedDebianUpgradeAsync().ConfigureAwait(true);
    }

    /// <summary>True only for Upgrade DwemerDistro in the dialog, which accepts it only with no game or server running at the click.</summary>
    private async Task<bool> ConfirmDebianUpgradeAsync()
    {
        if (_debianUpgradeRunningText is not null) return false;
        if (!IsSystemUpgradePossiblyPending() || _systemUpgradeBlocksWrites)
        {
            SetDebianUpgradeOffer(TrixieUpgradeOffer.None("rollout off, or the journal is completed or unfinished"));
            ShowDebianUpgradeUnavailable("it is no longer offered for this distro");
            return false;
        }

        if (!TryBeginPassiveDistroActivity())
        {
            ShowDebianUpgradeUnavailable("another DwemerDistro operation is running. Try again when it finishes");
            return false;
        }

        TrixieUpgradeOffer offer;
        SetDebianUpgradeRunningText("Checking...");
        try
        {
            // Fresh figures for the dialog; the cached offer may be hours old.
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_shutdownCts.Token);
            timeout.CancelAfter(StartupVersionCheckTimeout);
            offer = await CreateSystemUpgradeMigration(null).PreviewAsync(timeout.Token).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is OperationCanceledException or InvalidOperationException or IOException)
        {
            LauncherLogService.Startup("The Debian 13 upgrade check did not finish.", ex);
            if (!_shutdownCts.IsCancellationRequested)
                ShowDebianUpgradeUnavailable("the launcher could not finish checking it. Try again in a moment");
            return false;
        }
        finally
        {
            SetDebianUpgradeRunningText(null);
            EndPassiveDistroActivity();
        }

        SetDebianUpgradeOffer(offer);
        if (!offer.Available)
        {
            LauncherLogService.Startup($"Debian 13 upgrade not offered: {offer.Reason}.");
            ShowDebianUpgradeUnavailable("it is no longer offered for this distro");
            return false;
        }

        var dialog = new Views.DebianUpgradeConfirmationWindow(offer, Pg17UpdateMigration.FindRunningGames,
            () => IsServerRunning || IsServerStarting);
        if (System.Windows.Application.Current?.MainWindow is { IsVisible: true } owner) dialog.Owner = owner;
        else dialog.WindowStartupLocation = System.Windows.WindowStartupLocation.CenterScreen;
        var start = dialog.ShowDialog() == true;
        AppendLog((start
            ? "Upgrade DwemerDistro confirmed."
            : "Upgrade DwemerDistro postponed. Debian 12 is kept and nothing in the system was changed.") + Environment.NewLine);
        return start;
    }

    /// <summary>A stale or unreadable offer is said plainly; nothing runs in its place.</summary>
    private static void ShowDebianUpgradeUnavailable(string reason) => System.Windows.MessageBox.Show(
        $"{DebianUpgradeButtonText} can't start: {reason}. Nothing was changed.",
        DebianUpgradeButtonText, System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);

    /// <summary>The update run with the upgrade approved for this one run. The user asked for it, so the outcome is a dialog.</summary>
    private async Task RunApprovedDebianUpgradeAsync()
    {
        _lastSystemUpgradeResult = null;
        SystemUpdateRunResult run;
        SetDebianUpgradeRunningText("Upgrading...");
        try
        {
            run = await RunSystemUpdateCoreAsync(requireConfirmation: false, sourceLabel: "System", migrationEntry: true,
                startSystemUpgrade: true).ConfigureAwait(true);
        }
        finally
        {
            SetDebianUpgradeRunningText(null);
        }

        if (run == SystemUpdateRunResult.NotRun || _shutdownCts.IsCancellationRequested) return;
        var (text, image) = BuildApprovedDebianUpgradeReport(_lastSystemUpgradeResult);
        System.Windows.MessageBox.Show(text, DebianUpgradeButtonText, System.Windows.MessageBoxButton.OK, image);
    }

    internal static (string Text, System.Windows.MessageBoxImage Image) BuildApprovedDebianUpgradeReport(TrixieMaintenanceResult? result) => result switch
    {
        null => ("The distro update did not finish, so Upgrade DwemerDistro did not start and Debian 12 is unchanged. " +
                 "The console says why. You can choose Upgrade DwemerDistro again later.", System.Windows.MessageBoxImage.Warning),
        { Outcome: TrixieMaintenanceOutcome.Completed } => ("Upgrade DwemerDistro finished: DwemerDistro now runs Debian 13. You can start the server and play again.",
            System.Windows.MessageBoxImage.Information),
        { LeavesWritesBlocked: true } => ((BuildSystemUpgradeConsoleLine(result)?.Text ?? result.Message) +
            " Reopen the launcher to finish or check it.", System.Windows.MessageBoxImage.Error),
        { Outcome: TrixieMaintenanceOutcome.Restored } => (BuildSystemUpgradeConsoleLine(result)?.Text ?? result.Message,
            System.Windows.MessageBoxImage.Warning),
        _ => (Capitalize(result.Message.TrimEnd('.')) + ".", System.Windows.MessageBoxImage.Warning)
    };

    private static string Capitalize(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];
}
