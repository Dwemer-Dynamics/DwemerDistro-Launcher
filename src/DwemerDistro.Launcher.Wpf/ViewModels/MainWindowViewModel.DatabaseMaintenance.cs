using System.Windows;
using DwemerDistro.Launcher.Wpf.Models;
using DwemerDistro.Launcher.Wpf.Services;
using MessageBox = System.Windows.MessageBox;

namespace DwemerDistro.Launcher.Wpf.ViewModels;

/// <summary>
/// PostgreSQL 17 maintenance. Upgrade and rollback here are explicit and user-started; the normal distro
/// update runs the same upgrade unattended through <see cref="Pg17UpdateMigration"/>, sharing the
/// free-space check below. The distro's own maintenance tool does the work and its exit code is the
/// only success signal.
/// </summary>
public sealed partial class MainWindowViewModel
{
    internal const string DatabaseMaintenanceSettingName = "Database Maintenance";
    internal const string DatabaseMaintenanceToolPath = Pg17UpdateMigration.ToolPath;
    internal const string DatabaseMaintenanceUpgradeVerb = "upgrade";
    internal const string DatabaseMaintenanceRollbackVerb = "rollback";

    private string _databaseMaintenanceStatusText = string.Empty;
    private string _databaseMaintenanceStatusColor = CompactDistroNeutralColor;
    private bool _isDatabaseUpgradeWaiting;
    private CancellationTokenSource? _databaseUpgradeWaitCts;
    // The WSL registration whose PostgreSQL 15 data an earlier launcher build deleted, saved in update preferences
    // so Roll back stays off without probing WSL. A reinstall or import registers a new id; a real upgrade clears it.
    private string? _pg15DeletedRegistrationId;

    public AsyncRelayCommand UpgradeDatabaseCommand { get; private set; } = null!;
    public AsyncRelayCommand RollbackDatabaseCommand { get; private set; } = null!;

    /// <summary>Ends the update's wait for open games. The upgrade is postponed and the update is not recorded as finished.</summary>
    public RelayCommand StopDatabaseUpgradeWaitCommand { get; private set; } = null!;

    /// <summary>True while a distro update holds its PostgreSQL 17 upgrade until open games are closed.</summary>
    public bool IsDatabaseUpgradeWaiting
    {
        get => _isDatabaseUpgradeWaiting;
        private set
        {
            if (SetProperty(ref _isDatabaseUpgradeWaiting, value))
            {
                StopDatabaseUpgradeWaitCommand?.RaiseCanExecuteChanged();
            }
        }
    }

    public string DatabaseMaintenanceStatusText
    {
        get => _databaseMaintenanceStatusText;
        private set
        {
            if (SetProperty(ref _databaseMaintenanceStatusText, value))
            {
                OnPropertyChanged(nameof(HasDatabaseMaintenanceStatus));
            }
        }
    }

    public string DatabaseMaintenanceStatusColor
    {
        get => _databaseMaintenanceStatusColor;
        private set => SetProperty(ref _databaseMaintenanceStatusColor, value);
    }

    public bool HasDatabaseMaintenanceStatus => !string.IsNullOrWhiteSpace(DatabaseMaintenanceStatusText);

    private void InitializeDatabaseMaintenanceCommands()
    {
        _pg15DeletedRegistrationId = _updatePreferences.GetPg15DeletedRegistrationId();
        UpgradeDatabaseCommand = new AsyncRelayCommand(
            () => RunDatabaseMaintenanceAsync(DatabaseMaintenanceUpgradeVerb),
            () => !_systemUpgradeBlocksWrites && CanStartExclusiveDistroOperation());
        RollbackDatabaseCommand = new AsyncRelayCommand(
            () => RunDatabaseMaintenanceAsync(DatabaseMaintenanceRollbackVerb),
            () => !_systemUpgradeBlocksWrites && CanStartExclusiveDistroOperation()
                  && IsDatabaseRollbackAvailable(_pg15DeletedRegistrationId, _wsl.GetDistroRegistrationId()));
        StopDatabaseUpgradeWaitCommand = new RelayCommand(StopDatabaseUpgradeWait, () => IsDatabaseUpgradeWaiting);
    }

    private void RaiseDatabaseMaintenanceCommandStates()
    {
        UpgradeDatabaseCommand?.RaiseCanExecuteChanged();
        RollbackDatabaseCommand?.RaiseCanExecuteChanged();
    }

    /// <summary>Rollback stays offered unless the PostgreSQL 15 data of this same registration was deleted.</summary>
    internal static bool IsDatabaseRollbackAvailable(string? deletedRegistrationId, string? currentRegistrationId) =>
        deletedRegistrationId is null
        || !string.Equals(deletedRegistrationId, currentRegistrationId ?? string.Empty, StringComparison.OrdinalIgnoreCase);

    /// <summary>After a real upgrade, which leaves fresh PostgreSQL 15 data behind, so rollback is the tool's call again.</summary>
    private void ClearPg15DeletedRegistration()
    {
        _pg15DeletedRegistrationId = null;
        if (!_updatePreferences.TrySetPg15DeletedRegistrationId(null, out var error))
        {
            AppendLog($"Could not save the PostgreSQL 15 cleanup state: {error}{Environment.NewLine}", "yellow");
        }

        RaiseDatabaseMaintenanceCommandStates();
    }

    internal static string[] BuildDatabaseMaintenanceArguments(string verb) => verb switch
    {
        DatabaseMaintenanceUpgradeVerb or DatabaseMaintenanceRollbackVerb => [DatabaseMaintenanceToolPath, verb],
        _ => throw new ArgumentOutOfRangeException(nameof(verb), verb, "Unknown database maintenance action.")
    };

    internal static string BuildDatabaseUpgradeConfirmation()
    {
        return "Upgrade the DwemerDistro database to PostgreSQL 17?\n\n" +
               "This runs the distro's database maintenance tool as root. Don't play or use the server while it runs.\n\n" +
               "Rollback to the previous database is only possible before anything is written to the upgraded database. " +
               "If you might need to roll back, do it before starting the server or playing.\n\nContinue?";
    }

    internal static string BuildDatabaseRollbackConfirmation()
    {
        return "Roll the DwemerDistro database back from the PostgreSQL 17 upgrade?\n\n" +
               "Only use this before anything has been written to the upgraded database, that is, before starting " +
               "the server or playing after the upgrade. Anything written to PostgreSQL 17 since the upgrade is not " +
               "carried back.\n\n" +
               "If you have new saves, keep PostgreSQL 17 running. Rollback checks for changes and refuses to discard them.\n\nContinue?";
    }

    private static string DescribeDatabaseToolFailure(CommandResult result) =>
        result.ExitCode == DistroUpdateRunner.OtherUpdateRunningExitCode
            ? "A distro update is still running. Nothing was changed; try again once it finishes."
            : Pg17UpdateMigration.IsBackupFolderRequiredRefusal(result)
                ? Pg17UpdateMigration.BackupFolderRequiredMessage
                : GetCommandError(result);

    private async Task<bool> EnsureDatabaseToolAsync(string actionName)
    {
        var probe = await _wsl.RunDistroAsUserAsync("root", ["test", "-x", DatabaseMaintenanceToolPath])
            .ConfigureAwait(true);
        if (probe.Succeeded)
        {
            return true;
        }

        AppendLog($"{actionName} unavailable: {DatabaseMaintenanceToolPath} was not found.{Environment.NewLine}", "red");
        SetDatabaseMaintenanceStatus(
            "Failed. This distro does not have the database maintenance tool. Run Update Distro in Settings > First-Time Setup first. Nothing was changed.",
            CompactDistroErrorColor);
        MessageBox.Show(
            "This installation does not have the database maintenance tool yet.\n\n" +
            "Run Update Distro in Settings > First-Time Setup, then try again. Nothing was changed.",
            DatabaseMaintenanceSettingName,
            MessageBoxButton.OK,
            MessageBoxImage.Error);
        return false;
    }

    private async Task RunDatabaseMaintenanceAsync(string verb)
    {
        var isUpgrade = verb == DatabaseMaintenanceUpgradeVerb;
        var cleanupOnly = false;
        var actionName = isUpgrade ? "Database upgrade" : "Database rollback";
        var arguments = BuildDatabaseMaintenanceArguments(verb);

        if (!CanStartExclusiveDistroOperation())
        {
            ReportExclusiveDistroOperationBusy(DatabaseMaintenanceSettingName);
            return;
        }

        if (RefuseWhileSystemUpgradeUnfinished())
        {
            SetDatabaseMaintenanceStatus(SystemUpgradeBlockedMessage, CompactDistroWarningColor);
            return;
        }

        if (!await _wsl.DistroExistsAsync().ConfigureAwait(true))
        {
            SetDatabaseMaintenanceStatus(
                $"{LauncherConstants.DistroName} is not installed. Nothing was changed.",
                CompactDistroWarningColor);
            MessageBox.Show(
                $"{LauncherConstants.DistroName} is not currently installed.",
                DatabaseMaintenanceSettingName,
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        if (RefuseDatabaseMaintenanceWhileGameOpen(actionName))
        {
            return;
        }

        if (!await PauseAssistantForMaintenanceAsync(DatabaseMaintenanceSettingName).ConfigureAwait(true))
        {
            SetDatabaseMaintenanceStatus(AssistantBusyForMaintenanceMessage, CompactDistroWarningColor);
            return;
        }

        if (!TryBeginExclusiveDistroOperation())
        {
            ResumeAssistantAfterMaintenance(reconnect: true);
            SetDatabaseMaintenanceStatus(ExclusiveDistroOperationBusyMessage, CompactDistroWarningColor);
            ReportExclusiveDistroOperationBusy(DatabaseMaintenanceSettingName);
            return;
        }

        SetDatabaseMaintenanceStatus("Checking the database...", CompactDistroBusyColor);
        var endedSafely = true;
        try
        {
            if (!await EnsureDatabaseToolAsync(actionName).ConfigureAwait(true))
            {
                return;
            }

            var restoresRollback = false;
            if (isUpgrade)
            {
                // Status first: a database already on PostgreSQL 17 needs no free-space check, and its PostgreSQL 15
                // data may already be deleted, so there is nothing to size.
                var status = await _wsl.RunDatabaseMaintenanceAsync(Pg17UpdateMigration.BuildStatusArguments(), _ => { })
                    .ConfigureAwait(true);
                if (DescribeManualUpgradeNotStarted(status) is { } notStarted)
                {
                    AppendLog($"{actionName} not started: {notStarted.Text}{Environment.NewLine}");
                    SetDatabaseMaintenanceStatus(notStarted.Text, notStarted.Color);
                    MessageBox.Show(notStarted.Text, DatabaseMaintenanceSettingName, MessageBoxButton.OK, notStarted.Icon);
                    return;
                }

                cleanupOnly = IsDatabaseStagingCleanupOnly(status);
                if (cleanupOnly)
                {
                    // PostgreSQL 17 already serves, so this removes only the staging copies through Core's
                    // cleanup-staging, which refuses rather than upgrades if the state changed since status.
                    actionName = "Database cleanup";
                    arguments = Pg17UpdateMigration.BuildCleanupStagingArguments();
                }
                else
                {
                    // Only an upgrade from PostgreSQL 15 re-enables Roll back; it leaves fresh PostgreSQL 15 data behind.
                    restoresRollback = !IsDatabaseRollbackAvailable(_pg15DeletedRegistrationId, _wsl.GetDistroRegistrationId());
                }
            }

            // Defaults to No: both directions rewrite the database cluster. The staging cleanup rewrites nothing that serves.
            var confirmed = cleanupOnly ? MessageBoxResult.Yes : MessageBox.Show(
                isUpgrade ? BuildDatabaseUpgradeConfirmation() : BuildDatabaseRollbackConfirmation(),
                isUpgrade ? "Confirm Database Upgrade" : "Confirm Database Rollback",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.No);
            if (confirmed != MessageBoxResult.Yes)
            {
                SetDatabaseMaintenanceStatus("Canceled. Nothing was changed.", CompactDistroNeutralColor);
                AppendLog($"{actionName} canceled.{Environment.NewLine}");
                return;
            }

            if (isUpgrade && !cleanupOnly)
            {
                try
                {
                    await CheckDatabaseUpgradeSpaceAsync(_wsl).ConfigureAwait(true);
                }
                catch (InvalidOperationException ex)
                {
                    var notStarted = $"Upgrade not started. {ex.Message} Nothing was changed.";
                    AppendLog($"{actionName} not started: {ex.Message}{Environment.NewLine}", "yellow");
                    SetDatabaseMaintenanceStatus(notStarted, CompactDistroWarningColor);
                    MessageBox.Show(notStarted, DatabaseMaintenanceSettingName, MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
            }

            // Again right before the tool runs: the question can stay open any length of time.
            if (RefuseDatabaseMaintenanceWhileGameOpen(actionName))
            {
                return;
            }

            SetDatabaseMaintenanceStatus(
                cleanupOnly ? "Removing the upgrade's staging copies..."
                    : isUpgrade ? "Upgrading the database..." : "Rolling the database back...",
                CompactDistroBusyColor);
            AppendLog($"Starting {actionName.ToLowerInvariant()}...{Environment.NewLine}");
            var result = await _wsl.RunDatabaseMaintenanceAsync(arguments, text => AppendLog(text))
                .ConfigureAwait(true);
            if (!result.Succeeded)
            {
                var error = DescribeDatabaseToolFailure(result);
                AppendLog($"{actionName} failed: {error}{Environment.NewLine}", "red");
                SetDatabaseMaintenanceStatus(
                    $"{actionName} failed (exit code {result.ExitCode}). Open the console for details.",
                    CompactDistroErrorColor);
                MessageBox.Show(
                    $"{actionName} failed.\n\n{error}",
                    DatabaseMaintenanceSettingName,
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                return;
            }

            if (restoresRollback)
            {
                ClearPg15DeletedRegistration();
            }

            var done = cleanupOnly
                ? "PostgreSQL 17 was already serving. The upgrade's leftover staging copies were removed."
                : isUpgrade
                    ? "The database was upgraded to PostgreSQL 17."
                    : "The database was rolled back.";
            AppendLog($"{actionName} finished.{Environment.NewLine}", "green");
            SetDatabaseMaintenanceStatus($"Done. {done}", CompactDistroSuccessColor);
            MessageBox.Show(
                done,
                DatabaseMaintenanceSettingName,
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            endedSafely = false;
            AppendLog($"{actionName} error: {ex.Message}{Environment.NewLine}", "red");
            SetDatabaseMaintenanceStatus(
                $"{actionName} could not finish. Open the console for details.",
                CompactDistroErrorColor);
            MessageBox.Show(
                $"{actionName} could not finish.\n\n{ex.Message}",
                DatabaseMaintenanceSettingName,
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            SetExclusiveDistroOperationInProgress(false);
            QueueServerStatusRefresh();
            ResumeAssistantAfterMaintenance(endedSafely);
        }
    }

    /// <summary>
    /// Manual upgrade and rollback never wait for a game the way Update Distro does; they refuse on the same
    /// game check, before asking anything and again right before the tool runs.
    /// </summary>
    private bool RefuseDatabaseMaintenanceWhileGameOpen(string actionName)
    {
        var games = Pg17UpdateMigration.FindRunningGames();
        if (games.Count == 0)
        {
            return false;
        }

        var message = BuildDatabaseMaintenanceGameOpenMessage(actionName, games);
        AppendLog(message + Environment.NewLine, "yellow");
        SetDatabaseMaintenanceStatus(message, CompactDistroWarningColor);
        MessageBox.Show(message, DatabaseMaintenanceSettingName, MessageBoxButton.OK, MessageBoxImage.Warning);
        return true;
    }

    internal static string BuildDatabaseMaintenanceGameOpenMessage(string actionName, IReadOnlyList<string> games) =>
        $"{actionName} not started because {string.Join(", ", games)} is open. Close the game, then try again. Nothing was changed.";

    /// <summary>
    /// True when the manual upgrade's <c>status</c> leaves only the staging cleanup: it runs <c>cleanup-staging</c>
    /// with no free-space check or confirmation, never <c>upgrade</c>.
    /// </summary>
    internal static bool IsDatabaseStagingCleanupOnly(CommandResult status) =>
        status.Succeeded && Pg17UpdateMigration.ParseStatus(status.StandardOutput) == Pg17Status.CleanupNeeded;

    /// <summary>
    /// What the manual upgrade reports from the tool's <c>status</c> instead of asking to upgrade;
    /// null when the database still needs the upgrade or its staging cleanup, or is held on PostgreSQL 15.
    /// </summary>
    internal static (string Text, string Color, MessageBoxImage Icon)? DescribeManualUpgradeNotStarted(CommandResult status)
    {
        if (!status.Succeeded)
        {
            var error = DescribeDatabaseToolFailure(status).Trim();
            return ($"Could not check the database version. {error}" +
                    (error.Contains("Nothing was changed", StringComparison.Ordinal) ? string.Empty : " Nothing was changed."),
                CompactDistroErrorColor, MessageBoxImage.Error);
        }

        return Pg17UpdateMigration.ParseStatus(status.StandardOutput) switch
        {
            Pg17Status.Needed or Pg17Status.Held or Pg17Status.CleanupNeeded => null,
            Pg17Status.Current => ("Nothing to do. The database already runs PostgreSQL 17.",
                CompactDistroSuccessColor, MessageBoxImage.Information),
            Pg17Status.Unsupported => ("This database layout can't be upgraded by the launcher. Nothing was changed.",
                CompactDistroWarningColor, MessageBoxImage.Warning),
            _ => ("Could not check the database version: the maintenance tool gave an unrecognized answer. Nothing was changed.",
                CompactDistroErrorColor, MessageBoxImage.Error)
        };
    }

    /// <summary>
    /// The normal distro update: core provisioning and shared components, then the unattended PostgreSQL 17
    /// step, and only then the release marker, so an update never records success before the database is
    /// migrated. Each scripted phase gets its own <paramref name="phaseTimeout"/>, so time spent waiting for
    /// a game to close is not charged to the release step. Takes a <see cref="WslService"/> rather than the
    /// view model's, so a test can run the same sequence against an isolated distro. <paramref name="databaseFinished"/>
    /// gets the database step's result as soon as it ends, before a cancellation can still stop the update.
    /// With <paramref name="systemUpgrade"/>, an interrupted Debian Trixie upgrade is resolved before anything else
    /// writes, never after the generic updater, and the upgrade itself follows the verified PostgreSQL 17 step.
    /// While <see cref="TrixieUpdateMigration.KeepsLocalChangesAsync"/> says the update belongs to the migration, the checkout
    /// is guarded instead of reset; a <paramref name="migrationEntry"/> (Update Distro and the reopen sync) also passes Core's
    /// preserve flag to update.sh and update_gws. Only <paramref name="startSystemUpgrade"/>, set solely after the user chose
    /// Upgrade DwemerDistro, may start a fresh upgrade, and not when a game was open at any point of this run: the user is
    /// asked to confirm again instead. Every other run only finishes an upgrade already under way.
    /// </summary>
    internal static Task<SharedDistroUpdateResult> RunSharedDistroUpdatePipelineAsync(
        WslService wsl,
        Pg17UpdateMigration migration,
        Action<string> output,
        Action<string> migrationLog,
        TimeSpan phaseTimeout,
        CancellationToken cancellationToken,
        CancellationToken waitToken,
        Action<Pg17MigrationResult>? databaseFinished = null,
        TrixieUpdateMigration? systemUpgrade = null,
        Action<TrixieMaintenanceResult>? systemUpgradeFinished = null,
        bool migrationEntry = false,
        bool startSystemUpgrade = false) =>
        RunSharedDistroUpdatePipelineAsync((command, lines, token) => DistroUpdateRunner.RunAsync(wsl, command, lines, token),
            migration, output, migrationLog, phaseTimeout, cancellationToken, waitToken, databaseFinished, systemUpgrade,
            systemUpgradeFinished, migrationEntry, startSystemUpgrade);

    /// <summary>The same pipeline with the scripted phases run by <paramref name="runUpdate"/>, so a test sees each command.</summary>
    internal static async Task<SharedDistroUpdateResult> RunSharedDistroUpdatePipelineAsync(
        Func<string, Action<string>, CancellationToken, Task<CommandResult>> runUpdate,
        Pg17UpdateMigration migration,
        Action<string> output,
        Action<string> migrationLog,
        TimeSpan phaseTimeout,
        CancellationToken cancellationToken,
        CancellationToken waitToken,
        Action<Pg17MigrationResult>? databaseFinished = null,
        TrixieUpdateMigration? systemUpgrade = null,
        Action<TrixieMaintenanceResult>? systemUpgradeFinished = null,
        bool migrationEntry = false,
        bool startSystemUpgrade = false)
    {
        // The user's Start applies to the moment they confirmed with no game open; a game seen since voids it.
        var gameSeen = false;
        void OnGames(IReadOnlyList<string> games) => gameSeen |= games.Count > 0;
        migration.WaitingForGamesChanged += OnGames;
        try
        {
            return await RunSharedDistroUpdatePipelineCoreAsync(runUpdate, migration, output, migrationLog, phaseTimeout,
                cancellationToken, waitToken, databaseFinished, systemUpgrade, systemUpgradeFinished, migrationEntry,
                startSystemUpgrade, () => gameSeen).ConfigureAwait(false);
        }
        finally
        {
            migration.WaitingForGamesChanged -= OnGames;
        }
    }

    private static async Task<SharedDistroUpdateResult> RunSharedDistroUpdatePipelineCoreAsync(
        Func<string, Action<string>, CancellationToken, Task<CommandResult>> runUpdate,
        Pg17UpdateMigration migration,
        Action<string> output,
        Action<string> migrationLog,
        TimeSpan phaseTimeout,
        CancellationToken cancellationToken,
        CancellationToken waitToken,
        Action<Pg17MigrationResult>? databaseFinished,
        TrixieUpdateMigration? systemUpgrade,
        Action<TrixieMaintenanceResult>? systemUpgradeFinished,
        bool migrationEntry,
        bool startSystemUpgrade,
        Func<bool> gameSeen)
    {
        try
        {
            await migration.WaitForIdleAsync(migrationLog, waitToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (waitToken.IsCancellationRequested)
        {
            return new(new CommandResult(1, "", ""), new(Pg17MigrationOutcome.Deferred, "Update postponed before provisioning."));
        }

        if (systemUpgrade is not null)
        {
            var recovered = await systemUpgrade.RunAsync(migrationLog, allowFreshStart: false, waitToken, cancellationToken).ConfigureAwait(false);
            if (recovered.LeavesWritesBlocked)
            {
                systemUpgradeFinished?.Invoke(recovered);
                return new(new CommandResult(1, "", ""), null, recovered);
            }
        }
        CommandResult core;
        bool keepLocal;
        using (var phase = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            phase.CancelAfter(phaseTimeout);
            keepLocal = systemUpgrade is not null && await systemUpgrade.KeepsLocalChangesAsync(phase.Token).ConfigureAwait(false);
            core = await runUpdate(BuildSystemUpdateCommand(keepLocal, keepLocal && migrationEntry), output, phase.Token).ConfigureAwait(false);
        }

        if (!core.Succeeded)
        {
            return new(core, null, KeptLocalChanges: keepLocal);
        }

        output(UpdateStageMarkerLine(UpdateStageDatabase));
        var database = await migration.RunAsync(migrationLog, waitToken).ConfigureAwait(false);
        databaseFinished?.Invoke(database);
        cancellationToken.ThrowIfCancellationRequested();
        if (!database.AllowsUpdateToFinish)
        {
            return new(core, database, KeptLocalChanges: keepLocal);
        }

        TrixieMaintenanceResult? system = null;
        if (systemUpgrade is not null)
        {
            // A fresh upgrade starts only on the user's confirmed Start, after an update that kept local changes; any other
            // run only finishes or checks one, so a reopen or a routine Update Distro never begins it.
            var approved = startSystemUpgrade && keepLocal && migrationEntry && systemUpgrade.RolloutEnabled;
            var voided = approved && gameSeen();
            if (approved && !voided) output(UpdateStageMarkerLine(UpdateStageOperatingSystem));
            system = await systemUpgrade.RunAsync(migrationLog, allowFreshStart: approved && !voided, waitToken, cancellationToken).ConfigureAwait(false);
            if (voided && system.Outcome == TrixieMaintenanceOutcome.NotNeeded)
                system = new(TrixieMaintenanceOutcome.Deferred, DebianUpgradeVoidedByGameMessage);
            systemUpgradeFinished?.Invoke(system);
            // An unfinished upgrade must not be written to, not even by the release marker.
            if (system.LeavesWritesBlocked) return new(core, database, system, keepLocal);
            cancellationToken.ThrowIfCancellationRequested();
        }

        using (var phase = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            phase.CancelAfter(phaseTimeout);
            var release = await runUpdate(BuildSystemReleaseCommand(), output, phase.Token).ConfigureAwait(false);
            return new(release, database, system, keepLocal);
        }
    }

    internal const string DebianUpgradeVoidedByGameMessage =
        "a game was open during the distro update, so Upgrade DwemerDistro did not start the Debian 13 upgrade; nothing was changed. " +
        "Close the game, then choose Upgrade DwemerDistro again";

    /// <param name="System">The Debian Trixie step; a deferred or refused one still lets the release be recorded.</param>
    /// <param name="KeptLocalChanges">The core update ran with the checkout guard, as part of the migration.</param>
    internal sealed record SharedDistroUpdateResult(
        CommandResult Command, Pg17MigrationResult? Database, TrixieMaintenanceResult? System = null, bool KeptLocalChanges = false)
    {
        public bool Succeeded => Command.Succeeded && Database is { AllowsUpdateToFinish: true } && System is null or { AllowsUpdateToFinish: true };
    }

    private Pg17UpdateMigration CreateUpdateMigration(PausableDeadline? deadline)
    {
        var wsl = _wsl;
        var migration = new Pg17UpdateMigration(
            (arguments, output, token) => wsl.RunDatabaseMaintenanceAsync(arguments, output, token),
            Pg17UpdateMigration.FindRunningGames,
            token => CheckDatabaseUpgradeSpaceAsync(wsl, token),
            PauseDeadlineDuringDelay(Task.Delay, deadline),
            Pg17UpdateMigration.DefaultRecheckInterval);
        var hintShown = false;
        migration.WaitingForGamesChanged += games =>
        {
            RunOnUi(() => IsDatabaseUpgradeWaiting = games.Count > 0);
            if (games.Count == 0)
            {
                return;
            }

            SetDatabaseMaintenanceStatus(BuildDatabaseUpgradeWaitingStatus(games), CompactDistroWarningColor);
            if (!hintShown)
            {
                hintShown = true;
                AppendLog("To postpone it instead, choose Stop waiting under Settings, Maintenance and Backups, Database Maintenance." +
                          Environment.NewLine, "yellow");
            }
        };
        return migration;
    }

    /// <summary>
    /// Stops <paramref name="deadline"/> for each idle recheck delay while the update waits for a game to close.
    /// Only the delay is excused: the status checks, free-space check and provisioning around it still count against it.
    /// </summary>
    internal static Func<TimeSpan, CancellationToken, Task> PauseDeadlineDuringDelay(
        Func<TimeSpan, CancellationToken, Task> delayAsync, PausableDeadline? deadline)
    {
        if (deadline is null) return delayAsync;
        return async (interval, token) =>
        {
            deadline.Pause();
            try
            {
                await delayAsync(interval, token).ConfigureAwait(false);
            }
            finally
            {
                deadline.Resume();
            }
        };
    }

    internal static string BuildDatabaseUpgradeWaitingStatus(IReadOnlyList<string> games) =>
        $"Waiting for {string.Join(", ", games)} to close before upgrading to PostgreSQL 17. " +
        "The distro update finishes on its own once the game is closed. Nothing has been changed yet.";

    /// <summary>The Database Maintenance row's line once the update's database step ends; null leaves the row as it was.</summary>
    internal static (string Text, string Color)? BuildUpdateDatabaseStatus(Pg17MigrationResult result, bool waited) => result.Outcome switch
    {
        Pg17MigrationOutcome.Upgraded => (
            "Done. The distro update upgraded the database to PostgreSQL 17.", CompactDistroSuccessColor),
        Pg17MigrationOutcome.CleanedUp => (
            "Done. PostgreSQL 17 was already serving; the distro update removed the upgrade's leftover staging copies.",
            CompactDistroSuccessColor),
        Pg17MigrationOutcome.Failed when result.CleanupOnly => (
            "PostgreSQL 17 is serving, but the distro update could not remove the upgrade's staging copies, so the update " +
            "was not finished. Open the console for details.",
            CompactDistroErrorColor),
        Pg17MigrationOutcome.Deferred => (
            "Upgrade postponed. Nothing was changed. To finish it, close any open game and run Update Distro in Settings > First-Time Setup.",
            CompactDistroNeutralColor),
        Pg17MigrationOutcome.Failed => (
            "The distro update could not upgrade the database, so the update was not finished. Open the console for details.",
            CompactDistroErrorColor),
        _ => waited ? (result.Message, CompactDistroNeutralColor) : null
    };

    /// <summary>
    /// The row's line when the update stops with an exception. Once the database step has ended, its own result
    /// stands, because <c>auto</c> runs to its end and a timeout or shutdown after it does not undo it.
    /// </summary>
    internal static (string Text, string Color)? BuildInterruptedUpdateDatabaseStatus(Pg17MigrationResult? result, bool waited) =>
        result is not null
            ? BuildUpdateDatabaseStatus(result, waited)
            : waited
                ? ("Upgrade postponed. Nothing was changed. To finish it, close any open game and run Update Distro in Settings > First-Time Setup.",
                    CompactDistroNeutralColor)
                : null;

    internal static string DescribeDatabaseUpdateFailure(Pg17MigrationResult result) =>
        $" during the PostgreSQL 17 database upgrade: {result.Message.TrimEnd('.')}." +
        (result.Outcome == Pg17MigrationOutcome.Deferred
            ? " Close the game, then run Update Distro in Settings > First-Time Setup again."
            : " Full output: Logs\\launcher-operations.log.");

    private CancellationTokenSource BeginDatabaseUpgradeWait(CancellationToken updateToken)
    {
        var waitCts = CancellationTokenSource.CreateLinkedTokenSource(updateToken);
        Volatile.Write(ref _databaseUpgradeWaitCts, waitCts);
        return waitCts;
    }

    private void EndDatabaseUpgradeWait(CancellationTokenSource waitCts)
    {
        Interlocked.CompareExchange(ref _databaseUpgradeWaitCts, null, waitCts);
        RunOnUi(() => IsDatabaseUpgradeWaiting = false);
    }

    private void StopDatabaseUpgradeWait()
    {
        if (Volatile.Read(ref _databaseUpgradeWaitCts) is not { } waitCts)
        {
            return;
        }

        try
        {
            waitCts.Cancel();
            AppendLog("Stopped waiting for the game to close. The PostgreSQL 17 upgrade is postponed." + Environment.NewLine, "yellow");
        }
        catch (ObjectDisposedException)
        {
            // The update already moved past its wait.
        }
    }

    /// <summary>
    /// Where earlier launcher builds saved PostgreSQL 15 backups. The launcher no longer writes or creates it;
    /// Import Distro Backup opens it when it exists.
    /// </summary>
    internal static string DatabaseBackupRoot => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DwemerDistro", "DatabaseBackups");

    internal const string DatabaseBackupFolderPrefix = "Dwemer-PG15-";

    /// <summary>
    /// True for the database-only <c>Dwemer-PG15-&lt;32 hex&gt;.tar</c> backup or its <c>.recovery.json</c>
    /// companion. Neither is a full distro export, so a full distro import must refuse them.
    /// </summary>
    internal static bool IsDatabaseBackupArtifact(string path)
    {
        var name = System.IO.Path.GetFileName(path);
        string[] suffixes = [".tar", ".tar.recovery.json", ".recovery.json"];
        foreach (var suffix in suffixes)
        {
            if (name.Length != DatabaseBackupFolderPrefix.Length + 32 + suffix.Length
                || !name.StartsWith(DatabaseBackupFolderPrefix, StringComparison.OrdinalIgnoreCase)
                || !name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (name.AsSpan(DatabaseBackupFolderPrefix.Length, 32).ContainsAnyExcept("0123456789abcdefABCDEF"))
                continue;
            return true;
        }

        return false;
    }

    /// <summary>
    /// The upgrade stages working copies of the PostgreSQL 15 data inside the distro, and the WSL virtual disk grows
    /// into the Windows drive that holds it, so that drive needs room for four copies.
    /// </summary>
    internal static bool HasRoomForDatabaseUpgrade(long donorBytes, long backingVolumeFreeBytes) =>
        backingVolumeFreeBytes >= checked(donorBytes * 4);

    /// <summary>
    /// Sizes the PostgreSQL 15 data and checks the free space of the Windows drive holding the distro's virtual disk.
    /// Reads only; throws <see cref="InvalidOperationException"/> when the size is unknown or the drive is too full.
    /// Both the manual upgrade and the unattended update upgrade use this.
    /// </summary>
    internal static async Task CheckDatabaseUpgradeSpaceAsync(WslService wsl, CancellationToken cancellationToken = default)
    {
        var size = await wsl.RunDistroAsUserAsync("root", ["du", "-sb", "/var/lib/postgresql/15/main"],
            cancellationToken: cancellationToken).ConfigureAwait(false);
        var disk = NormalizeDistroVhdxPath(wsl.GetDistroVhdxPath());
        if (!size.Succeeded || !long.TryParse(size.StandardOutput.Split('\t')[0], out var bytes)
            || bytes <= 0 || string.IsNullOrEmpty(disk) || ReadBackingVolumeFreeSpace(disk) is not { } backing)
            throw new InvalidOperationException("Could not determine database size and WSL backing volume.");
        if (!HasRoomForDatabaseUpgrade(bytes, backing.FreeBytes))
            throw new InvalidOperationException(
                $"Not enough free space on {backing.Volume}, which holds the distro's virtual disk: the upgrade needs " +
                $"{FormatByteSize(checked(bytes * 4))} free and {FormatByteSize(backing.FreeBytes)} is available.");
    }

    /// <summary>
    /// The Windows volume that holds <paramref name="path"/> and the free space the current user may use on it. WSL
    /// registers the disk as <c>\\?\C:\...</c>, which <see cref="System.IO.DriveInfo"/> rejects, and a folder can be
    /// a mounted volume or a junction to one, so the drive letter in the path is not always the volume written to.
    /// Windows resolves both. Null when Windows cannot name the volume or read its free space.
    /// </summary>
    internal static (string Volume, long FreeBytes)? ReadBackingVolumeFreeSpace(string path)
    {
        var buffer = new char[32768];
        if (!GetVolumePathNameW(path, buffer, buffer.Length)) return null;
        var volume = new string(buffer, 0, Array.IndexOf(buffer, '\0'));
        if (!GetDiskFreeSpaceExW(volume, out var available, out _, out _) || available > long.MaxValue) return null;
        // "\\?\D:\" reads as "D:\"; a volume without a drive letter keeps its full name.
        if (volume.StartsWith(@"\\?\", StringComparison.Ordinal) && volume.Length == 7 && volume[5] == ':')
            volume = volume[4..];
        return (volume, (long)available);
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool GetVolumePathNameW(string fileName, [System.Runtime.InteropServices.Out] char[] volumePathName, int bufferLength);

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool GetDiskFreeSpaceExW(string directoryName, out ulong freeBytesAvailable, out ulong totalBytes, out ulong totalFreeBytes);

    private void SetDatabaseMaintenanceStatus(string text, string color)
    {
        RunOnUi(() =>
        {
            DatabaseMaintenanceStatusText = text;
            DatabaseMaintenanceStatusColor = color;
        });
    }
}
