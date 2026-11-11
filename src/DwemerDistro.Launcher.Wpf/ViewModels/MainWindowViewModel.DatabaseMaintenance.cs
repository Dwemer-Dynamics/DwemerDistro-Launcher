using System.Text.Json;
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
    internal const string DatabaseCleanupPreviewVerb = "cleanup-preview";
    internal const string DatabaseCleanupVerb = "cleanup-pg15";

    private string _databaseMaintenanceStatusText = string.Empty;
    private string _databaseMaintenanceStatusColor = CompactDistroNeutralColor;
    private bool _isDatabaseUpgradeWaiting;
    private CancellationTokenSource? _databaseUpgradeWaitCts;
    // The WSL registration whose PostgreSQL 15 data was deleted, saved in update preferences so Roll back stays
    // off after a restart without probing WSL. A reinstall or import registers a new id; a real upgrade clears it.
    private string? _pg15DeletedRegistrationId;

    public AsyncRelayCommand UpgradeDatabaseCommand { get; private set; } = null!;
    public AsyncRelayCommand RollbackDatabaseCommand { get; private set; } = null!;
    public AsyncRelayCommand DeletePg15DataCommand { get; private set; } = null!;

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
            () => RunDatabaseMaintenanceAsync(DatabaseMaintenanceUpgradeVerb), CanStartExclusiveDistroOperation);
        RollbackDatabaseCommand = new AsyncRelayCommand(
            () => RunDatabaseMaintenanceAsync(DatabaseMaintenanceRollbackVerb),
            () => CanStartExclusiveDistroOperation()
                  && IsDatabaseRollbackAvailable(_pg15DeletedRegistrationId, _wsl.GetDistroRegistrationId()));
        DeletePg15DataCommand = new AsyncRelayCommand(DeletePg15DataAsync, CanStartExclusiveDistroOperation);
        StopDatabaseUpgradeWaitCommand = new RelayCommand(StopDatabaseUpgradeWait, () => IsDatabaseUpgradeWaiting);
    }

    private void RaiseDatabaseMaintenanceCommandStates()
    {
        UpgradeDatabaseCommand?.RaiseCanExecuteChanged();
        RollbackDatabaseCommand?.RaiseCanExecuteChanged();
        DeletePg15DataCommand?.RaiseCanExecuteChanged();
    }

    /// <summary>Rollback stays offered unless the PostgreSQL 15 data of this same registration was deleted.</summary>
    internal static bool IsDatabaseRollbackAvailable(string? deletedRegistrationId, string? currentRegistrationId) =>
        deletedRegistrationId is null
        || !string.Equals(deletedRegistrationId, currentRegistrationId ?? string.Empty, StringComparison.OrdinalIgnoreCase);

    /// <summary>Null after a real upgrade, which leaves fresh PostgreSQL 15 data behind, so rollback is the tool's call again.</summary>
    private void SetPg15DeletedRegistration(string? registrationId)
    {
        _pg15DeletedRegistrationId = registrationId;
        if (!_updatePreferences.TrySetPg15DeletedRegistrationId(registrationId, out var error))
        {
            AppendLog($"Could not save the PostgreSQL 15 cleanup state: {error}{Environment.NewLine}", "yellow");
        }

        RaiseDatabaseMaintenanceCommandStates();
    }

    internal static string[] BuildDatabaseMaintenanceArguments(string verb) => verb switch
    {
        DatabaseMaintenanceUpgradeVerb or DatabaseMaintenanceRollbackVerb
            or DatabaseCleanupPreviewVerb or DatabaseCleanupVerb => [DatabaseMaintenanceToolPath, verb],
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

    internal sealed record Pg15CleanupPreview(bool Eligible, long Bytes, string Reason);

    internal enum Pg15CleanupOutcome
    {
        PreviewFailed,
        NotEligible,
        Canceled,
        Failed,
        Deleted
    }

    /// <summary><see cref="Detail"/> is the tool's reason or error; <see cref="FreedBytes"/> is null when the tool did not report it.</summary>
    internal sealed record Pg15CleanupResult(Pg15CleanupOutcome Outcome, string Detail, long? FreedBytes = null, int ExitCode = 0);

    /// <summary><c>cleanup-preview</c>: the last non-empty line must be {"eligible":bool,"bytes":int,"reason":string}.</summary>
    internal static Pg15CleanupPreview? ParseCleanupPreview(string? output)
    {
        using var document = ReadLastJsonObject(output);
        if (document is null) return null;
        var root = document.RootElement;
        return root.TryGetProperty("eligible", out var eligible) && eligible.ValueKind is JsonValueKind.True or JsonValueKind.False
               && root.TryGetProperty("bytes", out var bytes) && bytes.ValueKind == JsonValueKind.Number
               && bytes.TryGetInt64(out var count) && count >= 0
               && root.TryGetProperty("reason", out var reason) && reason.ValueKind == JsonValueKind.String
            ? new Pg15CleanupPreview(eligible.GetBoolean(), count, reason.GetString()!)
            : null;
    }

    /// <summary><c>cleanup-pg15</c> on success: the last non-empty line must be {"freed_bytes":int}.</summary>
    internal static long? ParseCleanupFreedBytes(string? output)
    {
        using var document = ReadLastJsonObject(output);
        return document is not null
               && document.RootElement.TryGetProperty("freed_bytes", out var freed) && freed.ValueKind == JsonValueKind.Number
               && freed.TryGetInt64(out var count) && count >= 0
            ? count
            : null;
    }

    private static JsonDocument? ReadLastJsonObject(string? output)
    {
        var line = (output ?? string.Empty)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .LastOrDefault();
        if (line is null || !line.StartsWith('{')) return null;
        try
        {
            var document = JsonDocument.Parse(line);
            if (document.RootElement.ValueKind == JsonValueKind.Object) return document;
            document.Dispose();
        }
        catch (JsonException)
        {
        }

        return null;
    }

    internal static string DescribeExactBytes(long bytes) =>
        $"{FormatByteSize(bytes)} ({bytes.ToString("N0", System.Globalization.CultureInfo.InvariantCulture)} bytes)";

    internal static string BuildPg15CleanupConfirmation(long bytes)
    {
        return "Delete the old PostgreSQL 15 data inside DwemerDistro?\n\n" +
               $"This frees {DescribeExactBytes(bytes)} inside the distro.\n\n" +
               "One-click rollback to PostgreSQL 15 will no longer be possible, and Roll back is turned off.\n\n" +
               "Your Windows backup from the upgrade is kept. Nothing outside the distro is deleted.\n\n" +
               "The space is freed inside the WSL virtual disk, which is not necessarily Windows free space. " +
               "Compact Distro hands freed space back to Windows.\n\nContinue?";
    }

    internal static string BuildPg15CleanupDoneStatus(long? freedBytes) =>
        (freedBytes is { } freed
            ? $"Done. Deleted the old PostgreSQL 15 data and freed {DescribeExactBytes(freed)} inside the distro."
            : "Done. Deleted the old PostgreSQL 15 data. The tool did not report how much space was freed.") +
        " One-click rollback is no longer available. Your Windows backup was kept.";

    /// <summary>
    /// Previews, asks, and only then deletes. Nothing is deleted unless the preview is well-formed and eligible
    /// and <paramref name="confirm"/>, shown the previewed byte count, returns true. The tool's exit code is the
    /// only success signal for the deletion.
    /// </summary>
    internal static async Task<Pg15CleanupResult> RunPg15CleanupAsync(
        Func<string[], Action<string>, Task<CommandResult>> runToolAsRoot,
        Func<long, bool> confirm,
        Action<string> output)
    {
        var previewResult = await runToolAsRoot(BuildDatabaseMaintenanceArguments(DatabaseCleanupPreviewVerb), _ => { })
            .ConfigureAwait(true);
        if (!previewResult.Succeeded)
        {
            return new(Pg15CleanupOutcome.PreviewFailed, DescribeDatabaseToolFailure(previewResult), ExitCode: previewResult.ExitCode);
        }

        if (ParseCleanupPreview(previewResult.StandardOutput) is not { } preview)
        {
            return new(Pg15CleanupOutcome.PreviewFailed, "The database maintenance tool's cleanup preview could not be read.");
        }

        if (!preview.Eligible)
        {
            var reason = string.IsNullOrWhiteSpace(preview.Reason) ? "There is no old PostgreSQL 15 data to delete." : preview.Reason.Trim();
            return new(Pg15CleanupOutcome.NotEligible, reason);
        }

        if (!confirm(preview.Bytes))
        {
            return new(Pg15CleanupOutcome.Canceled, "Canceled. Nothing was changed.");
        }

        var result = await runToolAsRoot(BuildDatabaseMaintenanceArguments(DatabaseCleanupVerb), output).ConfigureAwait(true);
        return result.Succeeded
            ? new(Pg15CleanupOutcome.Deleted, string.Empty, ParseCleanupFreedBytes(result.StandardOutput))
            : new(Pg15CleanupOutcome.Failed, DescribeDatabaseToolFailure(result), ExitCode: result.ExitCode);
    }

    /// <summary>
    /// Explicit only, never automatic. The preview, the question and the deletion all happen inside one exclusive
    /// slot, so what is shown is what is removed.
    /// </summary>
    private async Task DeletePg15DataAsync()
    {
        const string actionName = "PostgreSQL 15 cleanup";
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

        SetDatabaseMaintenanceStatus("Checking for old PostgreSQL 15 data...", CompactDistroBusyColor);
        var endedSafely = true;
        try
        {
            if (!await EnsureDatabaseToolAsync(actionName).ConfigureAwait(true))
            {
                return;
            }

            var registrationId = _wsl.GetDistroRegistrationId();
            var wsl = _wsl;
            var cleanup = await RunPg15CleanupAsync(
                    (arguments, output) => wsl.RunDatabaseMaintenanceAsync(arguments, output),
                    bytes =>
                    {
                        // Defaults to No: the deletion cannot be undone from the launcher.
                        if (MessageBox.Show(
                                BuildPg15CleanupConfirmation(bytes),
                                "Confirm Delete Old PostgreSQL 15 Data",
                                MessageBoxButton.YesNo,
                                MessageBoxImage.Warning,
                                MessageBoxResult.No) != MessageBoxResult.Yes)
                        {
                            return false;
                        }

                        SetDatabaseMaintenanceStatus("Deleting the old PostgreSQL 15 data...", CompactDistroBusyColor);
                        AppendLog($"Starting {actionName.ToLowerInvariant()}...{Environment.NewLine}");
                        return true;
                    },
                    text => AppendLog(text))
                .ConfigureAwait(true);

            switch (cleanup.Outcome)
            {
                case Pg15CleanupOutcome.Deleted:
                    SetPg15DeletedRegistration(registrationId ?? string.Empty);
                    var done = BuildPg15CleanupDoneStatus(cleanup.FreedBytes);
                    AppendLog($"{actionName} finished.{Environment.NewLine}", "green");
                    SetDatabaseMaintenanceStatus(done, CompactDistroSuccessColor);
                    MessageBox.Show(done, DatabaseMaintenanceSettingName, MessageBoxButton.OK, MessageBoxImage.Information);
                    break;
                case Pg15CleanupOutcome.Canceled:
                    SetDatabaseMaintenanceStatus(cleanup.Detail, CompactDistroNeutralColor);
                    AppendLog($"{actionName} canceled.{Environment.NewLine}");
                    break;
                case Pg15CleanupOutcome.NotEligible:
                    AppendLog($"{actionName} not available: {cleanup.Detail}{Environment.NewLine}");
                    SetDatabaseMaintenanceStatus($"Nothing to delete. {cleanup.Detail}", CompactDistroNeutralColor);
                    MessageBox.Show($"{cleanup.Detail}\n\nNothing was changed.",
                        DatabaseMaintenanceSettingName, MessageBoxButton.OK, MessageBoxImage.Information);
                    break;
                case Pg15CleanupOutcome.PreviewFailed:
                    AppendLog($"{actionName} preview failed: {cleanup.Detail}{Environment.NewLine}", "red");
                    SetDatabaseMaintenanceStatus(
                        "Could not check for old PostgreSQL 15 data. Nothing was changed. Open the console for details.",
                        CompactDistroErrorColor);
                    MessageBox.Show($"Could not check for old PostgreSQL 15 data.\n\n{cleanup.Detail}\n\nNothing was changed.",
                        DatabaseMaintenanceSettingName, MessageBoxButton.OK, MessageBoxImage.Error);
                    break;
                default:
                    AppendLog($"{actionName} failed: {cleanup.Detail}{Environment.NewLine}", "red");
                    SetDatabaseMaintenanceStatus(
                        $"{actionName} failed (exit code {cleanup.ExitCode}). Open the console for details.",
                        CompactDistroErrorColor);
                    MessageBox.Show($"{actionName} failed.\n\n{cleanup.Detail}",
                        DatabaseMaintenanceSettingName, MessageBoxButton.OK, MessageBoxImage.Error);
                    break;
            }
        }
        catch (Exception ex)
        {
            endedSafely = false;
            AppendLog($"{actionName} error: {ex.Message}{Environment.NewLine}", "red");
            SetDatabaseMaintenanceStatus($"{actionName} could not finish. Open the console for details.", CompactDistroErrorColor);
            MessageBox.Show($"{actionName} could not finish.\n\n{ex.Message}",
                DatabaseMaintenanceSettingName, MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            SetExclusiveDistroOperationInProgress(false);
            QueueServerStatusRefresh();
            ResumeAssistantAfterMaintenance(endedSafely);
        }
    }

    private static string DescribeDatabaseToolFailure(CommandResult result) =>
        result.ExitCode == DistroUpdateRunner.OtherUpdateRunningExitCode
            ? "A distro update is still running. Nothing was changed; try again once it finishes."
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
            "Failed. This distro does not have the database maintenance tool. Run Update Distro first. Nothing was changed.",
            CompactDistroErrorColor);
        MessageBox.Show(
            "This installation does not have the database maintenance tool yet.\n\n" +
            "Run Update Distro, then try again. Nothing was changed.",
            DatabaseMaintenanceSettingName,
            MessageBoxButton.OK,
            MessageBoxImage.Error);
        return false;
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
            if (!await EnsureDatabaseToolAsync(actionName).ConfigureAwait(true))
            {
                return;
            }

            // Only an upgrade from PostgreSQL 15 re-enables Roll back; a zero exit on an already current
            // database does not bring deleted data back.
            var restoresRollback = false;
            if (isUpgrade && !IsDatabaseRollbackAvailable(_pg15DeletedRegistrationId, _wsl.GetDistroRegistrationId()))
            {
                var status = await _wsl.RunDatabaseMaintenanceAsync(Pg17UpdateMigration.BuildStatusArguments(), _ => { })
                    .ConfigureAwait(true);
                restoresRollback = status.Succeeded && Pg17UpdateMigration.ParseStatus(status.StandardOutput) == Pg17Status.Needed;
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
                SetPg15DeletedRegistration(null);
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

    private Pg17UpdateMigration CreateUpdateMigration(PausableDeadline? deadline)
    {
        var wsl = _wsl;
        var migration = new Pg17UpdateMigration(
            (arguments, output, token) => wsl.RunDatabaseMaintenanceAsync(arguments, output, token),
            Pg17UpdateMigration.FindRunningGames,
            token => PrepareDatabaseBackupAsync(wsl, Pg17UpdateMigration.DefaultBackupRoot, token),
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
    /// Only the delay is excused: the status checks, backup and provisioning around it still count against it.
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
