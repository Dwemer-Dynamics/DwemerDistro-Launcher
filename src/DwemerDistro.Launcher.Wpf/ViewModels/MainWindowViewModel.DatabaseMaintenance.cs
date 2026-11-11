using System.Windows;
using DwemerDistro.Launcher.Wpf.Models;
using DwemerDistro.Launcher.Wpf.Services;
using MessageBox = System.Windows.MessageBox;

namespace DwemerDistro.Launcher.Wpf.ViewModels;

/// <summary>
/// PostgreSQL 17 maintenance. Upgrade and rollback here are explicit and user-started; the normal distro
/// update runs the same upgrade unattended through <see cref="Pg17UpdateMigration"/>, sharing the backup
/// and free-space checks below. The distro's own maintenance tool does the work and its exit code is the
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
        UpgradeDatabaseCommand = new AsyncRelayCommand(
            () => RunDatabaseMaintenanceAsync(DatabaseMaintenanceUpgradeVerb), CanStartExclusiveDistroOperation);
        RollbackDatabaseCommand = new AsyncRelayCommand(
            () => RunDatabaseMaintenanceAsync(DatabaseMaintenanceRollbackVerb), CanStartExclusiveDistroOperation);
        StopDatabaseUpgradeWaitCommand = new RelayCommand(StopDatabaseUpgradeWait, () => IsDatabaseUpgradeWaiting);
    }

    private void RaiseDatabaseMaintenanceCommandStates()
    {
        UpgradeDatabaseCommand?.RaiseCanExecuteChanged();
        RollbackDatabaseCommand?.RaiseCanExecuteChanged();
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
               "If you might need to roll back, do it before starting the server or playing.\n\n" +
               "Choose a private Windows folder for the verified backup of your databases and login credentials.\n\nContinue?";
    }

    internal static string BuildDatabaseRollbackConfirmation()
    {
        return "Roll the DwemerDistro database back from the PostgreSQL 17 upgrade?\n\n" +
               "Only use this before anything has been written to the upgraded database, that is, before starting " +
               "the server or playing after the upgrade. Anything written to PostgreSQL 17 since the upgrade is not " +
               "carried back.\n\n" +
               "If you have new saves, keep PostgreSQL 17 running. Rollback checks for changes and refuses to discard them.\n\nContinue?";
    }

    private async Task RunDatabaseMaintenanceAsync(string verb)
    {
        var isUpgrade = verb == DatabaseMaintenanceUpgradeVerb;
        var actionName = isUpgrade ? "Database upgrade" : "Database rollback";
        var arguments = BuildDatabaseMaintenanceArguments(verb);

        if (!CanStartExclusiveDistroOperation())
        {
            ReportExclusiveDistroOperationBusy(DatabaseMaintenanceSettingName);
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

        // Defaults to No: both directions rewrite the database cluster.
        var confirmed = MessageBox.Show(
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

        SetDatabaseMaintenanceStatus(
            isUpgrade ? "Upgrading the database..." : "Rolling the database back...",
            CompactDistroBusyColor);
        var endedSafely = true;
        try
        {
            var probe = await _wsl.RunDistroAsUserAsync(
                    "root", ["test", "-x", DatabaseMaintenanceToolPath])
                .ConfigureAwait(true);
            if (!probe.Succeeded)
            {
                AppendLog($"{actionName} unavailable: {DatabaseMaintenanceToolPath} was not found.{Environment.NewLine}", "red");
                SetDatabaseMaintenanceStatus(
                    "Failed. This distro does not have the database maintenance tool. Run Update Distro first. Nothing was changed.",
                    CompactDistroErrorColor);
                MessageBox.Show(
                    "This installation does not have the database maintenance tool yet.\n\n" +
                    "Run Update Distro, then try again. Nothing was changed.",
                    DatabaseMaintenanceSettingName,
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                return;
            }

            if (isUpgrade)
            {
                using var picker = new System.Windows.Forms.FolderBrowserDialog
                {
                    Description = "Choose a private Windows folder for the PostgreSQL 15 backup",
                    UseDescriptionForTitle = true
                };
                if (picker.ShowDialog() != System.Windows.Forms.DialogResult.OK)
                {
                    SetDatabaseMaintenanceStatus("Upgrade cancelled.", CompactDistroNeutralColor);
                    return;
                }
                arguments = [.. arguments, await PrepareDatabaseBackupAsync(_wsl, picker.SelectedPath).ConfigureAwait(true)];
            }
            AppendLog($"Starting {actionName.ToLowerInvariant()}...{Environment.NewLine}");
            var result = await _wsl.RunDatabaseMaintenanceAsync(arguments, text => AppendLog(text))
                .ConfigureAwait(true);
            if (!result.Succeeded)
            {
                var error = GetCommandError(result);
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

            AppendLog($"{actionName} finished.{Environment.NewLine}", "green");
            SetDatabaseMaintenanceStatus(
                isUpgrade
                    ? "Done. The database was upgraded to PostgreSQL 17."
                    : "Done. The database was rolled back.",
                CompactDistroSuccessColor);
            MessageBox.Show(
                isUpgrade
                    ? "The database was upgraded to PostgreSQL 17."
                    : "The database was rolled back.",
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
    /// The normal distro update: core provisioning and shared components, then the unattended PostgreSQL 17
    /// step, and only then the release marker, so an update never records success before the database is
    /// migrated. Each scripted phase gets its own <paramref name="phaseTimeout"/>, so time spent waiting for
    /// a game to close is not charged to the release step. Takes a <see cref="WslService"/> rather than the
    /// view model's, so a test can run the same sequence against an isolated distro.
    /// </summary>
    internal static async Task<SharedDistroUpdateResult> RunSharedDistroUpdatePipelineAsync(
        WslService wsl,
        Pg17UpdateMigration migration,
        Action<string> output,
        Action<string> migrationLog,
        TimeSpan phaseTimeout,
        CancellationToken cancellationToken,
        CancellationToken waitToken)
    {
        try
        {
            await migration.WaitForIdleAsync(migrationLog, waitToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (waitToken.IsCancellationRequested)
        {
            return new(new CommandResult(1, "", ""), new(Pg17MigrationOutcome.Deferred, "Update postponed before provisioning."));
        }
        CommandResult core;
        using (var phase = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            phase.CancelAfter(phaseTimeout);
            core = await DistroUpdateRunner.RunAsync(wsl, BuildSystemUpdateCommand(), output, phase.Token).ConfigureAwait(false);
        }

        if (!core.Succeeded)
        {
            return new(core, null);
        }

        output(UpdateStageMarkerLine(UpdateStageDatabase));
        var database = await migration.RunAsync(migrationLog, waitToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (!database.AllowsUpdateToFinish)
        {
            return new(core, database);
        }

        using (var phase = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            phase.CancelAfter(phaseTimeout);
            var release = await DistroUpdateRunner.RunAsync(wsl, BuildSystemReleaseCommand(), output, phase.Token).ConfigureAwait(false);
            return new(release, database);
        }
    }

    internal sealed record SharedDistroUpdateResult(CommandResult Command, Pg17MigrationResult? Database)
    {
        public bool Succeeded => Command.Succeeded && Database is { AllowsUpdateToFinish: true };
    }

    private Pg17UpdateMigration CreateUpdateMigration()
    {
        var wsl = _wsl;
        var migration = new Pg17UpdateMigration(
            (arguments, output, token) => wsl.RunDatabaseMaintenanceAsync(arguments, output, token),
            Pg17UpdateMigration.FindRunningGames,
            token => PrepareDatabaseBackupAsync(wsl, Pg17UpdateMigration.DefaultBackupRoot, token),
            Task.Delay,
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

    internal static string BuildDatabaseUpgradeWaitingStatus(IReadOnlyList<string> games) =>
        $"Waiting for {string.Join(", ", games)} to close before upgrading to PostgreSQL 17. " +
        "The distro update finishes on its own once the game is closed. Nothing has been changed yet.";

    /// <summary>The Database Maintenance row's line once the update's database step ends; null leaves the row as it was.</summary>
    internal static (string Text, string Color)? BuildUpdateDatabaseStatus(Pg17MigrationResult result, bool waited) => result.Outcome switch
    {
        Pg17MigrationOutcome.Upgraded => (
            "Done. The distro update upgraded the database to PostgreSQL 17. A verified backup of the previous database is in " +
            Pg17UpdateMigration.DefaultBackupRoot + ".", CompactDistroSuccessColor),
        Pg17MigrationOutcome.Deferred => (
            "Upgrade postponed. Nothing was changed. Update Distro finishes the upgrade the next time it runs with no game open.",
            CompactDistroNeutralColor),
        Pg17MigrationOutcome.Failed => (
            "The distro update could not upgrade the database, so the update was not finished. Open the console for details.",
            CompactDistroErrorColor),
        _ => waited ? (result.Message, CompactDistroNeutralColor) : null
    };

    internal static string DescribeDatabaseUpdateFailure(Pg17MigrationResult result) =>
        $" during the PostgreSQL 17 database upgrade: {result.Message.TrimEnd('.')}." +
        (result.Outcome == Pg17MigrationOutcome.Deferred
            ? " Close the game and run Update Distro again."
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
    /// Checks Windows free space for staging and the backup, creates a backup folder only the current
    /// Windows account can open under <paramref name="parentFolder"/>, and returns its distro path.
    /// Both the manual upgrade and the unattended update upgrade use this.
    /// </summary>
    internal static async Task<string> PrepareDatabaseBackupAsync(
        WslService wsl, string parentFolder, CancellationToken cancellationToken = default)
    {
        var size = await wsl.RunDistroAsUserAsync("root", ["du", "-sb", "/var/lib/postgresql/15/main"],
            cancellationToken: cancellationToken).ConfigureAwait(false);
        var disk = NormalizeDistroVhdxPath(wsl.GetDistroVhdxPath());
        if (!size.Succeeded || !long.TryParse(size.StandardOutput.Split('	')[0], out var bytes)
            || bytes <= 0 || string.IsNullOrEmpty(disk))
            throw new InvalidOperationException("Could not determine database size and WSL backing volume.");
        System.IO.Directory.CreateDirectory(parentFolder);
        var backingDrive = new System.IO.DriveInfo(System.IO.Path.GetPathRoot(disk)!);
        var backupDrive = new System.IO.DriveInfo(System.IO.Path.GetPathRoot(System.IO.Path.GetFullPath(parentFolder))!);
        var sameVolume = string.Equals(backingDrive.Name, backupDrive.Name, StringComparison.OrdinalIgnoreCase);
        if (backingDrive.AvailableFreeSpace < checked(bytes * (sameVolume ? 6 : 4))
            || backupDrive.AvailableFreeSpace < checked(bytes * 11 / 10))
            throw new InvalidOperationException("Insufficient Windows disk space for staging and backup.");
        var privateFolder = new System.IO.DirectoryInfo(System.IO.Path.Combine(parentFolder, $"Dwemer-PG15-{Guid.NewGuid():N}"));
        var acl = new System.Security.AccessControl.DirectorySecurity();
        acl.SetAccessRuleProtection(true, false);
        var owner = System.Security.Principal.WindowsIdentity.GetCurrent().User
            ?? throw new InvalidOperationException("Could not identify the current Windows account.");
        acl.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(owner,
            System.Security.AccessControl.FileSystemRights.FullControl,
            System.Security.AccessControl.InheritanceFlags.ContainerInherit | System.Security.AccessControl.InheritanceFlags.ObjectInherit,
            System.Security.AccessControl.PropagationFlags.None, System.Security.AccessControl.AccessControlType.Allow));
        System.IO.FileSystemAclExtensions.Create(privateFolder, acl);
        var path = await wsl.RunWslAsync(["-d", wsl.DistroName, "--exec", "wslpath", "-a", "-u", privateFolder.FullName],
            cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!path.Succeeded || !path.StandardOutput.Trim().StartsWith("/mnt/", StringComparison.Ordinal))
            throw new InvalidOperationException("Choose a Windows backup folder outside the distro.");
        return path.StandardOutput.Trim();
    }

    private void SetDatabaseMaintenanceStatus(string text, string color)
    {
        RunOnUi(() =>
        {
            DatabaseMaintenanceStatusText = text;
            DatabaseMaintenanceStatusColor = color;
        });
    }
}
