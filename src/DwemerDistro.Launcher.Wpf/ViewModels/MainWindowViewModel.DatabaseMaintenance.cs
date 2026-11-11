using System.Windows;
using DwemerDistro.Launcher.Wpf.Services;
using MessageBox = System.Windows.MessageBox;

namespace DwemerDistro.Launcher.Wpf.ViewModels;

/// <summary>
/// Explicit, user-started PostgreSQL 17 maintenance. Nothing here runs at startup or as part of an
/// update; the distro's own maintenance tool does the work and its exit code is the only success signal.
/// </summary>
public sealed partial class MainWindowViewModel
{
    internal const string DatabaseMaintenanceSettingName = "Database Maintenance";
    internal const string DatabaseMaintenanceToolPath = "/usr/local/bin/ddistro_pg17_maintenance";
    internal const string DatabaseMaintenanceUpgradeVerb = "upgrade";
    internal const string DatabaseMaintenanceRollbackVerb = "rollback";

    private string _databaseMaintenanceStatusText = string.Empty;
    private string _databaseMaintenanceStatusColor = CompactDistroNeutralColor;

    public AsyncRelayCommand UpgradeDatabaseCommand { get; private set; } = null!;
    public AsyncRelayCommand RollbackDatabaseCommand { get; private set; } = null!;

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
                var size = await _wsl.RunDistroAsUserAsync("root", ["du", "-sb", "/var/lib/postgresql/15/main"]);
                var disk = NormalizeDistroVhdxPath(_wsl.GetDistroVhdxPath());
                if (!size.Succeeded || !long.TryParse(size.StandardOutput.Split('\t')[0], out var bytes)
                    || bytes <= 0 || string.IsNullOrEmpty(disk))
                    throw new InvalidOperationException("Could not determine database size and WSL backing volume.");
                var backingDrive = new System.IO.DriveInfo(System.IO.Path.GetPathRoot(disk)!);
                var backupDrive = new System.IO.DriveInfo(System.IO.Path.GetPathRoot(picker.SelectedPath)!);
                var sameVolume = string.Equals(backingDrive.Name, backupDrive.Name, StringComparison.OrdinalIgnoreCase);
                if (backingDrive.AvailableFreeSpace < checked(bytes * (sameVolume ? 6 : 4))
                    || backupDrive.AvailableFreeSpace < checked(bytes * 11 / 10))
                    throw new InvalidOperationException("Insufficient Windows disk space for staging and backup.");
                var privateFolder = new System.IO.DirectoryInfo(System.IO.Path.Combine(picker.SelectedPath, $"Dwemer-PG15-{Guid.NewGuid():N}"));
                var acl = new System.Security.AccessControl.DirectorySecurity();
                acl.SetAccessRuleProtection(true, false);
                var owner = System.Security.Principal.WindowsIdentity.GetCurrent().User
                    ?? throw new InvalidOperationException("Could not identify the current Windows account.");
                acl.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(owner,
                    System.Security.AccessControl.FileSystemRights.FullControl,
                    System.Security.AccessControl.InheritanceFlags.ContainerInherit | System.Security.AccessControl.InheritanceFlags.ObjectInherit,
                    System.Security.AccessControl.PropagationFlags.None, System.Security.AccessControl.AccessControlType.Allow));
                System.IO.FileSystemAclExtensions.Create(privateFolder, acl);
                var path = await _wsl.RunWslAsync(["-d", LauncherConstants.DistroName, "--exec", "wslpath", "-a", "-u", privateFolder.FullName]);
                if (!path.Succeeded || !path.StandardOutput.Trim().StartsWith("/mnt/", StringComparison.Ordinal))
                    throw new InvalidOperationException("Choose a Windows backup folder outside the distro.");
                arguments = [.. arguments, path.StandardOutput.Trim()];
            }
            AppendLog($"Starting {actionName.ToLowerInvariant()}...{Environment.NewLine}");
            var result = await _wsl.RunDatabaseMaintenanceAsync(LauncherConstants.DistroName, arguments, text => AppendLog(text))
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

    private void SetDatabaseMaintenanceStatus(string text, string color)
    {
        RunOnUi(() =>
        {
            DatabaseMaintenanceStatusText = text;
            DatabaseMaintenanceStatusColor = color;
        });
    }
}
