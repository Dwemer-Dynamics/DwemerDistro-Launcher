using System.ComponentModel;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using DwemerDistro.Launcher.Wpf.Services;
using MessageBox = System.Windows.MessageBox;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;

namespace DwemerDistro.Launcher.Wpf.ViewModels;

/// <summary>
/// The Mirthwood: Echoes panel. Echoes ships its own package installer and a Windows companion that
/// owns the Echoes services for as long as its window is open, so this panel only installs packages
/// and opens that companion. The generic server controls never start or stop Echoes.
/// </summary>
public sealed partial class MainWindowViewModel
{
    private const string EchoesTitle = MirthwoodEchoesService.ProductName;
    private const string EchoesNoPublishedReleaseText =
        "No published Mirthwood: Echoes package is available yet. Use Install from package… with a package you were given.";

    private MirthwoodEchoesService _echoesService = null!;
    private EchoesCompanion? _echoesCompanion;
    private bool _isEchoesBusy;
    private string _echoesStatusText = "Checking...";
    private string _echoesStatusColor = ServerManagerItemViewModel.CheckingColor;

    public RelayCommand OpenEchoesCommand { get; private set; } = null!;
    public AsyncRelayCommand InstallEchoesReleaseCommand { get; private set; } = null!;
    public AsyncRelayCommand InstallEchoesPackageCommand { get; private set; } = null!;
    public AsyncRelayCommand CheckEchoesPrerequisitesCommand { get; private set; } = null!;

    public string EchoesStatusText
    {
        get => _echoesStatusText;
        private set => SetProperty(ref _echoesStatusText, value);
    }

    public string EchoesStatusColor
    {
        get => _echoesStatusColor;
        private set => SetProperty(ref _echoesStatusColor, value);
    }

    public bool IsEchoesBusy
    {
        get => _isEchoesBusy;
        private set
        {
            if (SetProperty(ref _isEchoesBusy, value))
            {
                RaiseEchoesCommandStates();
            }
        }
    }

    public bool IsEchoesInstalled => _echoesCompanion is not null;

    public bool ShowEchoesInstallActions => !IsEchoesInstalled;

    public bool ShowEchoesInstalledActions => IsEchoesInstalled;

    private void InitializeMirthwoodEchoes()
    {
        _echoesService = new MirthwoodEchoesService(_processRunner, _wsl);
        OpenEchoesCommand = new RelayCommand(OpenEchoes, CanOpenEchoes);
        InstallEchoesReleaseCommand = new AsyncRelayCommand(() => InstallEchoesAsync(fromRelease: true), CanStartEchoesOperation);
        InstallEchoesPackageCommand = new AsyncRelayCommand(() => InstallEchoesAsync(fromRelease: false), CanStartEchoesOperation);
        CheckEchoesPrerequisitesCommand = new AsyncRelayCommand(CheckEchoesPrerequisitesAsync, CanStartEchoesOperation);
        RefreshEchoesCompanion(updateStatus: true);
    }

    /// <summary>
    /// The full distro write gate: no upgrade, distro update, component or server install, custom mod or
    /// assistant file change, and no exclusive maintenance. Opening counts too, because the companion
    /// starts the Echoes service and PostgreSQL inside the distro.
    /// </summary>
    private bool IsEchoesDistroGateOpen() => IsComponentInteractionEnabled && !IsLiveFileMutationInProgress;

    private bool CanStartEchoesOperation() => !IsEchoesBusy && IsEchoesDistroGateOpen();

    private bool CanOpenEchoes() => !IsEchoesBusy && IsEchoesInstalled && IsEchoesDistroGateOpen();

    /// <summary>Selecting the Echoes panel rescans the companion folder, so installs or removals made elsewhere show.</summary>
    private void OnEchoesSelected()
    {
        if (_echoesService is null || IsEchoesBusy)
        {
            return;
        }

        RefreshEchoesCompanion(updateStatus: true);
    }

    private void RaiseEchoesCommandStates()
    {
        OpenEchoesCommand?.RaiseCanExecuteChanged();
        InstallEchoesReleaseCommand?.RaiseCanExecuteChanged();
        InstallEchoesPackageCommand?.RaiseCanExecuteChanged();
        CheckEchoesPrerequisitesCommand?.RaiseCanExecuteChanged();
    }

    private void SetEchoesStatus(string text, string color)
    {
        RunOnUi(() =>
        {
            EchoesStatusText = text;
            EchoesStatusColor = color;
        });
    }

    /// <summary>Reads the companion folder only; never starts WSL.</summary>
    private void RefreshEchoesCompanion(bool updateStatus)
    {
        var scan = _echoesService.ScanCompanions();
        _echoesCompanion = scan.Match;
        OnPropertyChanged(nameof(IsEchoesInstalled));
        OnPropertyChanged(nameof(ShowEchoesInstallActions));
        OnPropertyChanged(nameof(ShowEchoesInstalledActions));
        RaiseEchoesCommandStates();
        if (!updateStatus)
        {
            return;
        }

        if (scan.Match is { } companion)
        {
            var version = companion.Version is null ? string.Empty : $" {companion.Version}";
            SetEchoesStatus($"Installed{version}", "LimeGreen");
        }
        else if (scan.OtherDistro is { } other)
        {
            SetEchoesStatus($"Installed for {other} only. Install here to use it with {_wsl.DistroName}.",
                ServerManagerItemViewModel.NeedsRepairColor);
        }
        else
        {
            SetEchoesStatus("Not installed", ServerManagerItemViewModel.NotInstalledColor);
        }
    }

    private bool BeginEchoesOperation()
    {
        if (!CanStartEchoesOperation())
        {
            return false;
        }

        IsEchoesBusy = true;
        // Echoes changes the shared distro (system user, database, systemd unit), so it holds the same
        // gate as a component install: updates, server operations and exclusive maintenance wait for it.
        SetComponentsOperationInProgress(true);
        return true;
    }

    private void EndEchoesOperation()
    {
        SetComponentsOperationInProgress(false);
        IsEchoesBusy = false;
    }

    private void OpenEchoes()
    {
        // RelayCommand.Execute does not re-check CanExecute, so the gate is enforced here as well.
        if (!CanOpenEchoes())
        {
            return;
        }

        RefreshEchoesCompanion(updateStatus: false);
        if (_echoesCompanion is not { } companion)
        {
            RefreshEchoesCompanion(updateStatus: true);
            MessageBox.Show($"{EchoesTitle} is not installed for {_wsl.DistroName}. Install it first.", EchoesTitle,
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var missing = MirthwoodEchoesService.CheckWindowsPrerequisites();
        if (missing.Count > 0)
        {
            ReportEchoesPrerequisites(missing, updateStatus: false);
            return;
        }

        try
        {
            var open = MirthwoodEchoesService.RunningCompanionDistros();
            // The companion is single-instance, so a start would only raise a window that may serve another
            // distro. Any owner that cannot be read, or differs, blocks the launch.
            if (FindEchoesOwnerConflict(open, _wsl.DistroName) is { } conflict)
            {
                var message = conflict == EchoesOwnerConflict.Unknown
                    ? $"A {EchoesTitle} window is already open, but the launcher could not tell which WSL distro it serves. " +
                      $"Close that window, then open {EchoesTitle} again."
                    : $"A {EchoesTitle} window for another WSL distro is already open. Close it, then open {EchoesTitle} again.";
                SetEchoesStatus("Another companion window is open. Close it, then try again.", ServerManagerItemViewModel.NeedsRepairColor);
                AppendLog(message + Environment.NewLine, "yellow");
                MessageBox.Show(message, EchoesTitle, MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var alreadyOpen = open.Count > 0;
            MirthwoodEchoesService.LaunchCompanion(companion);
            // Started is not ready: the companion still has to start its server.
            SetEchoesStatus(alreadyOpen ? "Bringing the companion window forward..." : "Opening the companion window...",
                ServerManagerItemViewModel.CheckingColor);
            AppendLog(alreadyOpen
                ? $"{EchoesTitle} is already open; bringing its window forward.{Environment.NewLine}"
                : $"Opening {EchoesTitle}. Its window starts and stops the Echoes server.{Environment.NewLine}");
        }
        catch (Exception ex) when (ex is Win32Exception or IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            AppendLog($"{EchoesTitle} could not be opened: {ex.Message}{Environment.NewLine}", "red");
            MessageBox.Show($"{EchoesTitle} could not be opened.\n\n{ex.Message}", EchoesTitle, MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    internal enum EchoesOwnerConflict { OtherDistro, Unknown }

    /// <summary>Fails closed: an unreadable or blank owner counts as a conflict, as does another distro.</summary>
    internal static EchoesOwnerConflict? FindEchoesOwnerConflict(IReadOnlyList<string?> runningOwners, string distro)
    {
        if (runningOwners.Any(string.IsNullOrWhiteSpace))
        {
            return EchoesOwnerConflict.Unknown;
        }

        return runningOwners.Any(owner => !string.Equals(owner, distro, StringComparison.OrdinalIgnoreCase))
            ? EchoesOwnerConflict.OtherDistro
            : null;
    }

    private async Task CheckEchoesPrerequisitesAsync()
    {
        if (!BeginEchoesOperation())
        {
            return;
        }

        try
        {
            var result = await CheckEchoesInstallPrerequisitesAsync().ConfigureAwait(true);
            if (result is not null)
            {
                RefreshEchoesCompanion(updateStatus: true);
                AppendLog($"{EchoesTitle} prerequisites are ready. Mirthwood: {result}{Environment.NewLine}", "green");
                MessageBox.Show($"Everything {EchoesTitle} needs is ready.\n\nMirthwood: {result}", EchoesTitle,
                    MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }
        finally
        {
            EndEchoesOperation();
        }
    }

    /// <summary>Returns the Mirthwood folder when every prerequisite is met; otherwise reports what is missing.</summary>
    private async Task<string?> CheckEchoesInstallPrerequisitesAsync()
    {
        SetEchoesStatus("Checking prerequisites...", ServerManagerItemViewModel.BusyColor);
        var missing = new List<string>();
        var gameDirectory = MirthwoodEchoesService.FindGameDirectory();
        if (gameDirectory is null)
        {
            missing.Add("Steam could not locate exactly one Mirthwood installation on a local drive. Install Mirthwood through Steam, then check again.");
        }

        missing.AddRange(MirthwoodEchoesService.CheckWindowsPrerequisites());
        try
        {
            if (!await _wsl.DistroExistsAsync(_shutdownCts.Token).ConfigureAwait(true))
            {
                missing.Add($"The {_wsl.DistroName} distro is not installed. Run DwemerDistroInstaller.exe first.");
            }
            else
            {
                missing.AddRange(await _echoesService.CheckLinuxPrerequisitesAsync(_shutdownCts.Token).ConfigureAwait(true));
            }
        }
        catch (Exception ex) when (ex is Win32Exception or IOException or InvalidOperationException)
        {
            missing.Add($"The {_wsl.DistroName} distro could not be checked: {ex.Message}");
        }

        if (missing.Count == 0)
        {
            return gameDirectory;
        }

        ReportEchoesPrerequisites(missing);
        return null;
    }

    private void ReportEchoesPrerequisites(IReadOnlyList<string> missing, bool updateStatus = true)
    {
        if (updateStatus)
        {
            SetEchoesStatus("Missing prerequisites. See the console for details.", ServerManagerItemViewModel.ErrorColor);
        }

        AppendLog($"{EchoesTitle} needs:{Environment.NewLine}", "yellow");
        foreach (var item in missing)
        {
            AppendLog($"  - {item}{Environment.NewLine}", "yellow");
        }

        RunOnUi(() => MessageBox.Show(
            $"{EchoesTitle} cannot be installed or opened yet:\n\n• " + string.Join("\n• ", missing),
            EchoesTitle,
            MessageBoxButton.OK,
            MessageBoxImage.Warning));
    }

    private static string? PickEchoesPackage()
    {
        var dialog = new OpenFileDialog
        {
            Title = $"Choose a {EchoesTitle} server package",
            Filter = "Mirthwood: Echoes package (*.zip;Update-Server.ps1)|*.zip;Update-Server.ps1",
            CheckFileExists = true,
            Multiselect = false
        };

        return dialog.ShowDialog() == true ? Path.GetFullPath(dialog.FileName) : null;
    }

    private bool ConfirmEchoesInstall(string version, string gameDirectory)
    {
        var verb = IsEchoesInstalled ? "update" : "install";
        var runningNote = MirthwoodEchoesService.IsCompanionRunning()
            ? "\n\nThe companion window is open. Finish your session first; reopen it afterwards if it does not follow the update."
            : string.Empty;
        return MessageBox.Show(
            $"This will {verb} {EchoesTitle} {version} using its own installer:\n\n" +
            $"• the Echoes server, its PostgreSQL database and its systemd service inside {_wsl.DistroName}\n" +
            "• the Windows companion and the Mirthwood: Echoes desktop shortcut\n" +
            $"• the game module in {gameDirectory} (managed by the Echoes server)\n\n" +
            "Saves, provider settings and other mods are kept." + runningNote + "\n\nContinue?",
            EchoesTitle,
            MessageBoxButton.YesNo,
            MessageBoxImage.Question) == MessageBoxResult.Yes;
    }

    private async Task InstallEchoesAsync(bool fromRelease)
    {
        string? selected = null;
        if (!fromRelease)
        {
            selected = PickEchoesPackage();
            if (selected is null)
            {
                return;
            }
        }

        if (!BeginEchoesOperation())
        {
            return;
        }

        string? staging = null;
        var reported = false;
        try
        {
            var gameDirectory = await CheckEchoesInstallPrerequisitesAsync().ConfigureAwait(true);
            if (gameDirectory is null)
            {
                reported = true;
                return;
            }

            EchoesPackage package;
            if (fromRelease)
            {
                SetEchoesStatus("Looking for a published release...", ServerManagerItemViewModel.BusyColor);
                var release = await MirthwoodEchoesService.GetLatestReleaseAsync(_httpClient, _shutdownCts.Token).ConfigureAwait(true);
                if (release is null)
                {
                    SetEchoesStatus("No published release yet. Use Install from package…", ServerManagerItemViewModel.NotInstalledColor);
                    AppendLog(EchoesNoPublishedReleaseText + Environment.NewLine, "yellow");
                    MessageBox.Show(EchoesNoPublishedReleaseText, EchoesTitle, MessageBoxButton.OK, MessageBoxImage.Information);
                    reported = true;
                    return;
                }

                if (!ConfirmEchoesInstall(release.Version, gameDirectory))
                {
                    return;
                }

                staging = MirthwoodEchoesService.CreateStagingDirectory();
                SetEchoesStatus($"Downloading {release.Version}...", ServerManagerItemViewModel.BusyColor);
                AppendLog($"Downloading {release.AssetName}...{Environment.NewLine}");
                // The release download can take minutes; the shared 15-second client would cut it off.
                using var downloadClient = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromMinutes(30) };
                var archive = await MirthwoodEchoesService.DownloadReleaseAsync(downloadClient, release, staging, _shutdownCts.Token).ConfigureAwait(true);
                SetEchoesStatus("Checking the package...", ServerManagerItemViewModel.BusyColor);
                var stagingFolder = Path.Combine(staging, "package");
                package = await Task.Run(() => MirthwoodEchoesService.ExtractPackage(archive, stagingFolder, _shutdownCts.Token)).ConfigureAwait(true);
                if (package.Version != release.Version)
                {
                    throw new InvalidDataException("The downloaded package version differs from its release.");
                }
            }
            else
            {
                SetEchoesStatus("Checking the package...", ServerManagerItemViewModel.BusyColor);
                if (selected!.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                {
                    staging = MirthwoodEchoesService.CreateStagingDirectory();
                    var stagingFolder = Path.Combine(staging, "package");
                    package = await Task.Run(() => MirthwoodEchoesService.ExtractPackage(selected, stagingFolder, _shutdownCts.Token)).ConfigureAwait(true);
                }
                else
                {
                    package = MirthwoodEchoesService.ValidatePackage(Path.GetDirectoryName(selected)!);
                }

                if (!ConfirmEchoesInstall(package.Version, gameDirectory))
                {
                    return;
                }
            }

            SetEchoesStatus($"Installing {package.Version}...", ServerManagerItemViewModel.BusyColor);
            AppendLog($"Installing {EchoesTitle} {package.Version} into {_wsl.DistroName}...{Environment.NewLine}");
            LauncherLogService.Operation($"START Mirthwood: Echoes installer {package.Version}; distro {_wsl.DistroName}");
            var result = await _echoesService.RunInstallerAsync(package, gameDirectory, line => AppendLog(line), _shutdownCts.Token)
                .ConfigureAwait(true);
            LauncherLogService.Operation($"END Mirthwood: Echoes installer; exit code {result.ExitCode}");
            if (!result.Succeeded)
            {
                throw new IOException(DescribeEchoesInstallerFailure(result));
            }

            RefreshEchoesCompanion(updateStatus: true);
            if (!IsEchoesInstalled)
            {
                throw new IOException($"The installer finished, but no companion for {_wsl.DistroName} was found.");
            }

            reported = true;
            AppendLog($"{EchoesTitle} {package.Version} is installed. Open it from here or the desktop shortcut.{Environment.NewLine}", "green");
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or HttpRequestException or JsonException
                                       or UnauthorizedAccessException or InvalidOperationException or Win32Exception
                                       or TaskCanceledException)
        {
            reported = true;
            var message = ex is TaskCanceledException ? "The operation timed out or was cancelled." : ex.Message;
            SetEchoesStatus("Install failed. See the console for details.", ServerManagerItemViewModel.ErrorColor);
            AppendLog($"{EchoesTitle} install failed: {message}{Environment.NewLine}", "red");
            MessageBox.Show($"{EchoesTitle} was not installed.\n\n{message}", EchoesTitle, MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            MirthwoodEchoesService.DeleteStagingDirectory(staging);
            EndEchoesOperation();
            if (!reported)
            {
                RefreshEchoesCompanion(updateStatus: true);
            }
        }
    }

    internal static string DescribeEchoesInstallerFailure(Models.CommandResult result)
    {
        var lines = (result.StandardError + "\n" + result.StandardOutput)
            .Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.Length > 0)
            .ToArray();
        var detail = lines.FirstOrDefault(line => line.StartsWith("Missing WSL prerequisite", StringComparison.Ordinal)
                                                  || line.StartsWith("Expected .NET runtime", StringComparison.Ordinal))
                     ?? lines.LastOrDefault();
        return detail is null
            ? $"The Echoes installer exited with code {result.ExitCode}."
            : $"The Echoes installer exited with code {result.ExitCode}: {detail}";
    }
}
