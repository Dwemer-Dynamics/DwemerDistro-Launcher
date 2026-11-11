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
    internal const string DatabaseCleanupConfirmPreviewOption = "--confirm-preview";

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
            or DatabaseCleanupPreviewVerb => [DatabaseMaintenanceToolPath, verb],
        _ => throw new ArgumentOutOfRangeException(nameof(verb), verb, "Unknown database maintenance action.")
    };

    /// <summary><c>cleanup-pg15</c> only ever runs bound to the token of the preview the user was shown.</summary>
    internal static string[] BuildPg15CleanupArguments(string previewToken) => IsCleanupPreviewToken(previewToken)
        ? [DatabaseMaintenanceToolPath, DatabaseCleanupVerb, DatabaseCleanupConfirmPreviewOption, previewToken]
        : throw new ArgumentException("The cleanup preview token must be 64 lowercase hex characters.", nameof(previewToken));

    internal static bool IsCleanupPreviewToken(string? token) =>
        token is { Length: 64 } && token.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

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

    /// <summary><see cref="Token"/> is null unless the tool sent a well-formed one; an older tool sends none.</summary>
    internal sealed record Pg15CleanupPreview(bool Eligible, long Bytes, string Reason, string? Token = null);

    internal enum Pg15CleanupOutcome
    {
        PreviewFailed,
        RegistrationUnknown,
        RegistrationChanged,
        NotEligible,
        Canceled,
        Failed,
        Deleted
    }

    /// <summary>
    /// <see cref="Detail"/> is the tool's reason or error; <see cref="FreedBytes"/> is null when the tool did not report it.
    /// <see cref="RegistrationId"/> is the previewed WSL registration GUID, set only when its data was deleted.
    /// </summary>
    internal sealed record Pg15CleanupResult(
        Pg15CleanupOutcome Outcome, string Detail, long? FreedBytes = null, int ExitCode = 0, string? RegistrationId = null);

    internal const string Pg15CleanupRegistrationUnknownMessage =
        "The launcher could not identify this DwemerDistro installation in WSL, so it cannot keep Roll back turned off " +
        "after deleting. Nothing was deleted.";

    internal const string Pg15CleanupRegistrationChangedMessage =
        "DwemerDistro was reinstalled or re-imported after the preview, so the preview no longer describes it. " +
        "Nothing was deleted. Choose Delete PG15 data again for a fresh preview.";

    internal const string Pg15CleanupTokenMissingMessage =
        "The database maintenance tool's cleanup preview did not include a valid confirmation token, so the launcher " +
        "cannot prove the deletion matches the preview. Nothing was deleted. Update DwemerDistro, then choose " +
        "Delete PG15 data again for a fresh preview.";

    /// <summary>
    /// <c>cleanup-preview</c>: the last non-empty line must be {"eligible":bool,"bytes":int,"reason":string}, plus
    /// "token" (64 lowercase hex characters) when eligible. A missing or malformed token leaves <see cref="Pg15CleanupPreview.Token"/> null.
    /// </summary>
    internal static Pg15CleanupPreview? ParseCleanupPreview(string? output)
    {
        using var document = ReadLastJsonObject(output);
        if (document is null) return null;
        var root = document.RootElement;
        return root.TryGetProperty("eligible", out var eligible) && eligible.ValueKind is JsonValueKind.True or JsonValueKind.False
               && root.TryGetProperty("bytes", out var bytes) && bytes.ValueKind == JsonValueKind.Number
               && bytes.TryGetInt64(out var count) && count >= 0
               && root.TryGetProperty("reason", out var reason) && reason.ValueKind == JsonValueKind.String
            ? new Pg15CleanupPreview(eligible.GetBoolean(), count, reason.GetString()!,
                root.TryGetProperty("token", out var token) && token.ValueKind == JsonValueKind.String
                && IsCleanupPreviewToken(token.GetString()) ? token.GetString() : null)
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
    /// Previews, asks, and only then deletes. Nothing is deleted unless the preview is well-formed, eligible and
    /// tokened and <paramref name="confirm"/>, shown the previewed byte count, returns true. The deletion is passed
    /// exactly that token, so the tool refuses if what it would delete is no longer what was previewed. The answer is bound to the
    /// WSL registration GUID read before the preview: an unknown one is refused, and one that differs once the
    /// user has answered means the preview is stale. The tool's exit code is the only success signal for the deletion.
    /// </summary>
    internal static async Task<Pg15CleanupResult> RunPg15CleanupAsync(
        Func<string[], Action<string>, Task<CommandResult>> runToolAsRoot,
        Func<long, bool> confirm,
        Action<string> output,
        Func<string?> readRegistrationId)
    {
        var registrationId = readRegistrationId();
        if (!Guid.TryParse(registrationId, out _))
        {
            return new(Pg15CleanupOutcome.RegistrationUnknown, Pg15CleanupRegistrationUnknownMessage);
        }

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

        if (preview.Token is not { } previewToken)
        {
            return new(Pg15CleanupOutcome.PreviewFailed, Pg15CleanupTokenMissingMessage);
        }

        if (!confirm(preview.Bytes))
        {
            return new(Pg15CleanupOutcome.Canceled, "Canceled. Nothing was changed.");
        }

        if (!string.Equals(readRegistrationId(), registrationId, StringComparison.OrdinalIgnoreCase))
        {
            return new(Pg15CleanupOutcome.RegistrationChanged, Pg15CleanupRegistrationChangedMessage);
        }

        var result = await runToolAsRoot(BuildPg15CleanupArguments(previewToken), output).ConfigureAwait(true);
        return result.Succeeded
            ? new(Pg15CleanupOutcome.Deleted, string.Empty, ParseCleanupFreedBytes(result.StandardOutput), RegistrationId: registrationId)
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
                    text => AppendLog(text),
                    wsl.GetDistroRegistrationId)
                .ConfigureAwait(true);

            switch (cleanup.Outcome)
            {
                case Pg15CleanupOutcome.Deleted:
                    SetPg15DeletedRegistration(cleanup.RegistrationId);
                    var done = BuildPg15CleanupDoneStatus(cleanup.FreedBytes);
                    AppendLog($"{actionName} finished.{Environment.NewLine}", "green");
                    SetDatabaseMaintenanceStatus(done, CompactDistroSuccessColor);
                    MessageBox.Show(done, DatabaseMaintenanceSettingName, MessageBoxButton.OK, MessageBoxImage.Information);
                    break;
                case Pg15CleanupOutcome.Canceled:
                    SetDatabaseMaintenanceStatus(cleanup.Detail, CompactDistroNeutralColor);
                    AppendLog($"{actionName} canceled.{Environment.NewLine}");
                    break;
                case Pg15CleanupOutcome.RegistrationUnknown or Pg15CleanupOutcome.RegistrationChanged:
                    AppendLog($"{actionName} refused: {cleanup.Detail}{Environment.NewLine}", "yellow");
                    SetDatabaseMaintenanceStatus(cleanup.Detail, CompactDistroWarningColor);
                    MessageBox.Show(cleanup.Detail, DatabaseMaintenanceSettingName, MessageBoxButton.OK, MessageBoxImage.Warning);
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
        DatabaseBackupFolder? backup = null;
        try
        {
            if (!await EnsureDatabaseToolAsync(actionName).ConfigureAwait(true))
            {
                return;
            }

            var restoresRollback = false;
            if (isUpgrade)
            {
                // Status first: a database already on PostgreSQL 17 needs no backup folder, and its PostgreSQL 15
                // data may already be deleted, so there is nothing to size or copy.
                var status = await _wsl.RunDatabaseMaintenanceAsync(Pg17UpdateMigration.BuildStatusArguments(), _ => { })
                    .ConfigureAwait(true);
                if (DescribeManualUpgradeNotStarted(status) is { } notStarted)
                {
                    AppendLog($"{actionName} not started: {notStarted.Text}{Environment.NewLine}");
                    SetDatabaseMaintenanceStatus(notStarted.Text, notStarted.Color);
                    MessageBox.Show(notStarted.Text, DatabaseMaintenanceSettingName, MessageBoxButton.OK, notStarted.Icon);
                    return;
                }

                // Only an upgrade from PostgreSQL 15 re-enables Roll back; it leaves fresh PostgreSQL 15 data behind.
                restoresRollback = !IsDatabaseRollbackAvailable(_pg15DeletedRegistrationId, _wsl.GetDistroRegistrationId());
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

                try
                {
                    backup = await PrepareDatabaseBackupAsync(_wsl, picker.SelectedPath).ConfigureAwait(true);
                }
                catch (InvalidOperationException ex)
                {
                    var notStarted = $"Upgrade not started. {ex.Message} Nothing was changed.";
                    AppendLog($"{actionName} not started: {ex.Message}{Environment.NewLine}", "yellow");
                    SetDatabaseMaintenanceStatus(notStarted, CompactDistroWarningColor);
                    MessageBox.Show(notStarted, DatabaseMaintenanceSettingName, MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                arguments = [.. arguments, backup.DistroPath];
            }

            // Again right before the tool runs: the questions and the folder picker can stay open any length of time.
            if (RefuseDatabaseMaintenanceWhileGameOpen(actionName))
            {
                if (backup is not null) TryRemoveUnusedBackupFolder(backup.WindowsPath);
                return;
            }

            SetDatabaseMaintenanceStatus(
                isUpgrade ? "Upgrading the database..." : "Rolling the database back...",
                CompactDistroBusyColor);
            AppendLog($"Starting {actionName.ToLowerInvariant()}...{Environment.NewLine}");
            var result = await _wsl.RunDatabaseMaintenanceAsync(arguments, text => AppendLog(text))
                .ConfigureAwait(true);
            if (!result.Succeeded)
            {
                // The update lock refused the tool before it started, so its backup folder is still empty.
                if (backup is not null && result.ExitCode == DistroUpdateRunner.OtherUpdateRunningExitCode)
                {
                    TryRemoveUnusedBackupFolder(backup.WindowsPath);
                }

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
    /// What the manual upgrade reports from the tool's <c>status</c> instead of asking for a backup folder;
    /// null when the database still needs the upgrade or is held on PostgreSQL 15.
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
            Pg17Status.Needed or Pg17Status.Held => null,
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
    /// </summary>
    internal static async Task<SharedDistroUpdateResult> RunSharedDistroUpdatePipelineAsync(
        WslService wsl,
        Pg17UpdateMigration migration,
        Action<string> output,
        Action<string> migrationLog,
        TimeSpan phaseTimeout,
        CancellationToken cancellationToken,
        CancellationToken waitToken,
        Action<Pg17MigrationResult>? databaseFinished = null)
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
        databaseFinished?.Invoke(database);
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
        DatabaseBackupFolder? backup = null;
        var migration = new Pg17UpdateMigration(
            (arguments, output, token) => wsl.RunDatabaseMaintenanceAsync(arguments, output, token),
            Pg17UpdateMigration.FindRunningGames,
            async token => (backup = await PrepareDatabaseBackupAsync(wsl, Pg17UpdateMigration.DefaultBackupRoot, token)
                .ConfigureAwait(false)).DistroPath,
            PauseDeadlineDuringDelay(Task.Delay, deadline),
            Pg17UpdateMigration.DefaultRecheckInterval,
            () =>
            {
                if (backup is not null) TryRemoveUnusedBackupFolder(backup.WindowsPath);
            });
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

    /// <summary>
    /// The row's line when the update stops with an exception. Once the database step has ended, its own result
    /// stands, because <c>auto</c> runs to its end and a timeout or shutdown after it does not undo it.
    /// </summary>
    internal static (string Text, string Color)? BuildInterruptedUpdateDatabaseStatus(Pg17MigrationResult? result, bool waited) =>
        result is not null
            ? BuildUpdateDatabaseStatus(result, waited)
            : waited
                ? ("Upgrade postponed. Nothing was changed. Update Distro finishes the upgrade the next time it runs with no game open.",
                    CompactDistroNeutralColor)
                : null;

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

    /// <summary>A backup folder this launcher just created, by its Windows path and its distro path.</summary>
    internal sealed record DatabaseBackupFolder(string WindowsPath, string DistroPath);

    internal const string DatabaseBackupFolderPrefix = "Dwemer-PG15-";

    /// <summary>Null when <paramref name="parentFolder"/> can hold a backup; otherwise why it can't.</summary>
    internal static string? DescribeUnsupportedBackupFolder(string parentFolder) =>
        System.IO.Path.GetPathRoot(System.IO.Path.GetFullPath(parentFolder)) is { } root && root.StartsWith(@"\\", StringComparison.Ordinal)
            ? @"Network folders (paths starting with \\) can't hold the database backup. Choose a folder on a local Windows drive, such as C:."
            : null;

    /// <summary>
    /// Checks Windows free space for staging and the backup, creates a backup folder only the current
    /// Windows account can open under <paramref name="parentFolder"/>, and returns it. The folder is removed
    /// again, if still empty, when the distro cannot see it. Both the manual upgrade and the unattended update
    /// upgrade use this.
    /// </summary>
    internal static async Task<DatabaseBackupFolder> PrepareDatabaseBackupAsync(
        WslService wsl, string parentFolder, CancellationToken cancellationToken = default)
    {
        if (DescribeUnsupportedBackupFolder(parentFolder) is { } unsupported)
            throw new InvalidOperationException(unsupported);
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
        var privateFolder = new System.IO.DirectoryInfo(System.IO.Path.Combine(parentFolder, $"{DatabaseBackupFolderPrefix}{Guid.NewGuid():N}"));
        // Creating a folder that already exists succeeds silently, so a fresh name must be free first;
        // only then is a later removal limited to what this created.
        if (privateFolder.Exists)
            throw new InvalidOperationException("The new backup folder name is already in use. Try again.");
        var acl = new System.Security.AccessControl.DirectorySecurity();
        acl.SetAccessRuleProtection(true, false);
        var owner = System.Security.Principal.WindowsIdentity.GetCurrent().User
            ?? throw new InvalidOperationException("Could not identify the current Windows account.");
        acl.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(owner,
            System.Security.AccessControl.FileSystemRights.FullControl,
            System.Security.AccessControl.InheritanceFlags.ContainerInherit | System.Security.AccessControl.InheritanceFlags.ObjectInherit,
            System.Security.AccessControl.PropagationFlags.None, System.Security.AccessControl.AccessControlType.Allow));
        System.IO.FileSystemAclExtensions.Create(privateFolder, acl);
        try
        {
            var path = await wsl.RunWslAsync(["-d", wsl.DistroName, "--exec", "wslpath", "-a", "-u", privateFolder.FullName],
                cancellationToken: cancellationToken).ConfigureAwait(false);
            if (!path.Succeeded || !path.StandardOutput.Trim().StartsWith("/mnt/", StringComparison.Ordinal))
                throw new InvalidOperationException("Choose a Windows backup folder outside the distro.");
            // wslpath maps any drive letter, mounted or not. A network, subst or removable drive WSL did not mount
            // would put the backup in the distro's own disk under /mnt; only the folder just created proves the mapping.
            var visible = await wsl.RunWslAsync(["-d", wsl.DistroName, "-u", "root", "--exec", "test", "-d", path.StandardOutput.Trim()],
                cancellationToken: cancellationToken).ConfigureAwait(false);
            if (!visible.Succeeded)
                throw new InvalidOperationException("The distro cannot see the backup folder. Choose a folder on a local Windows drive.");
            return new(privateFolder.FullName, path.StandardOutput.Trim());
        }
        catch
        {
            TryRemoveUnusedBackupFolder(privateFolder.FullName);
            throw;
        }
    }

    /// <summary>
    /// Removes a backup folder from <see cref="PrepareDatabaseBackupAsync"/> that nothing was written to. Never
    /// recursive: a folder holding anything, a backup included, stays, and so does any folder not named like ours.
    /// </summary>
    internal static bool TryRemoveUnusedBackupFolder(string windowsPath)
    {
        if (!System.IO.Path.GetFileName(System.IO.Path.TrimEndingDirectorySeparator(windowsPath))
                .StartsWith(DatabaseBackupFolderPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        try
        {
            if (System.IO.Directory.EnumerateFileSystemEntries(windowsPath).Any()) return false;
            System.IO.Directory.Delete(windowsPath, recursive: false);
            return true;
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        {
            return false;
        }
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
