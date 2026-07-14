using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Threading;
using DwemerDistro.Launcher.Wpf.Models;
using DwemerDistro.Launcher.Wpf.Services;
using DwemerDistro.Launcher.Wpf.Views;
using Application = System.Windows.Application;
using MessageBox = System.Windows.MessageBox;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;
using SaveFileDialog = Microsoft.Win32.SaveFileDialog;
using Forms = System.Windows.Forms;

namespace DwemerDistro.Launcher.Wpf.ViewModels;

public sealed partial class MainWindowViewModel : ObservableObject
{
    private static readonly TimeSpan StartupSettingsTimeout = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan StartupFirstRunProbeTimeout = TimeSpan.FromSeconds(12);
    private static readonly TimeSpan StartupLauncherUpdateTimeout = TimeSpan.FromSeconds(25);
    private static readonly TimeSpan StartupVersionCheckTimeout = TimeSpan.FromSeconds(20);
    private const string ChimMcpInstallScript = """
set -e
export PATH=/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin:$PATH
cd /home/dwemer

node_path=$(command -v node 2>/dev/null || true)
npm_path=$(command -v npm 2>/dev/null || true)
if [ -z "$node_path" ] || [ -z "$npm_path" ] || [[ "$node_path" == /mnt/c/* ]] || [[ "$node_path" == *.exe ]] || [[ "$npm_path" == /mnt/c/* ]] || [[ "$npm_path" == *.cmd ]]; then
    echo "Installing Linux nodejs/npm for CHIM-MCP..."
    echo dwemer | sudo -S apt-get update
    echo dwemer | sudo -S apt-get -y install nodejs npm
    export PATH=/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin:$PATH
fi

if [ ! -d CHIM-MCP/.git ]; then
    rm -rf CHIM-MCP
    git clone https://github.com/Dwemer-Dynamics/CHIM-MCP.git CHIM-MCP
else
    git -C CHIM-MCP reset --hard HEAD
    git -C CHIM-MCP pull --ff-only
fi

cd CHIM-MCP
if [ -f package-lock.json ]; then
    npm ci
else
    npm install
fi

npm run build
test -f dist/index.js
echo 1 > /home/dwemer/.mcp_enabled
echo "CHIM-MCP installed and enabled."
""";

    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _startAnimationTimer;
    private readonly DispatcherTimer _serverStatusRetryTimer;
    private readonly ProcessRunner _processRunner = new();
    private readonly WslService _wsl;
    private readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromSeconds(15) };
    private readonly LauncherUpdateService _launcherUpdateService;
    private readonly SemaphoreSlim _componentInstallGate = new(1, 1);

    private TcpProxyService? _tcpProxyService;
    private DiscoveryService? _discoveryService;
    private Process? _serverProcess;
    private string? _wslIp;
    private Window? _firstRunSetupWindow;

    private string _outputText = string.Empty;
    private bool _isServerRunning;
    private bool _isServerStarting;
    private string _startButtonText = "Start Server";
    private string _herikaStatusText = "Checking...";
    private string _herikaStatusColor = "White";
    private string _stobeStatusText = "Checking...";
    private string _stobeStatusColor = "White";
    private string _dialecticStatusText = "Checking...";
    private string _dialecticStatusColor = "White";
    private string _launcherVersionText = $"Launcher Version: {LauncherConstants.LauncherVersion}";
    private string _launcherUpdateStatusText = "Launcher update: checking...";
    private string _launcherUpdateStatusColor = "White";
    private string _launcherUpdateButtonText = "Check Update";
    private string _distroUpdateButtonText = "Update";
    private bool _isDistroUpdateInProgress;
    private bool _mcpEnabled = true;
    private bool _includeHerikaServerUpdate = true;
    private bool _includeStobeServerUpdate = true;
    private bool _includeDialecticServerUpdate = true;
    private bool _canUpdateLauncher;
    private string _targetHerikaBranch = "aiagent";
    private string _targetStobeBranch = "stobe";
    private string _targetDialecticBranch = "dialectic";
    private int _startAnimationDots;
    private bool _isServerStatusRefreshInProgress;
    private LauncherReleaseInfo? _pendingLauncherUpdate;

    public MainWindowViewModel()
    {
        _dispatcher = Application.Current.Dispatcher;
        _wsl = new WslService(_processRunner);
        _launcherUpdateService = new LauncherUpdateService(_httpClient, _processRunner);
        _startAnimationTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(500)
        };
        _startAnimationTimer.Tick += (_, _) => UpdateStartAnimation();
        _serverStatusRetryTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(15)
        };
        _serverStatusRetryTimer.Tick += async (_, _) => await RetryServerStatusChecksAsync().ConfigureAwait(true);

        HerikaBranches = new ObservableCollection<string>(new[] { "aiagent", "dev", "unstable" });
        StobeBranches = new ObservableCollection<string>(new[] { "stobe", "dev", "unstable" });
        DialecticBranches = new ObservableCollection<string>(new[] { "dialectic", "dev", "unstable" });

        StartServerCommand = new AsyncRelayCommand(StartServerAsync, () => !IsServerRunning && !IsServerStarting);
        StopServerCommand = new AsyncRelayCommand(StopServerAsync, () => IsServerRunning || IsServerStarting);
        ForceStopServerCommand = new AsyncRelayCommand(ForceStopServerAsync);
        UpdateAllCommand = new AsyncRelayCommand(UpdateAllAsync, () => !IsDistroUpdateInProgress);
        OpenServerFolderCommand = new RelayCommand(OpenServerFolder);
        OpenFirstRunSetupCommand = new RelayCommand(OpenFirstRunSetupWindow);
        InstallComponentsCommand = new RelayCommand(OpenInstallComponentsWindow);
        OpenDebuggingCommand = new RelayCommand(OpenDebuggingWindow);
        SaveMcpEnabledCommand = new AsyncRelayCommand(SaveMcpEnabledAsync);
        SaveUpdateIncludeCommand = new AsyncRelayCommand(SaveUpdateIncludeSettingsAsync);
        OpenChimCommand = new RelayCommand(() => _processRunner.OpenExternalUrl(LauncherConstants.ChimNexusUrl));
        OpenStobeCommand = new RelayCommand(() => _processRunner.OpenExternalUrl(LauncherConstants.StobeNexusUrl));
        OpenDialecticCommand = new RelayCommand(() => _processRunner.OpenExternalUrl(LauncherConstants.DialecticServerUiUrl));
        OpenWikiCommand = new RelayCommand(() => _processRunner.OpenExternalUrl(LauncherConstants.WikiUrl));
        OpenDiscordCommand = new RelayCommand(() => _processRunner.OpenExternalUrl(LauncherConstants.DiscordUrl));

        OpenPiperVoicesFolderCommand = new RelayCommand(() => OpenFolder(@"\\wsl.localhost\DwemerAI4Skyrim3\home\dwemer\piper\voices"));

        OpenTerminalCommand = new RelayCommand(() => RunCommandInNewWindow("wsl -d DwemerAI4Skyrim3 -u dwemer -- /usr/local/bin/terminal"));
        ViewMemoryUsageCommand = new RelayCommand(() => RunCommandInNewWindow("wsl -d DwemerAI4Skyrim3 -- htop"));
        ExportDistroCommand = new AsyncRelayCommand(ExportDistroAsync);
        ImportDistroCommand = new AsyncRelayCommand(ImportDistroAsync);
        OpenHerikaRollbackCommand = new RelayCommand(() => _ = OpenRollbackWindowAsync("herika"));
        OpenStobeRollbackCommand = new RelayCommand(() => _ = OpenRollbackWindowAsync("stobe"));
        OpenDialecticRollbackCommand = new RelayCommand(() => _ = OpenRollbackWindowAsync("dialectic"));
        ViewXttsLogsCommand = new RelayCommand(() => RunCommandInNewWindow("wsl -d DwemerAI4Skyrim3 -u dwemer -- tail -n 100 -f /home/dwemer/xtts-api-server/log.txt"));
        ViewChatterboxLogsCommand = new RelayCommand(() => RunCommandInNewWindow("wsl -d DwemerAI4Skyrim3 -u dwemer -- tail -n 100 -f /home/dwemer/chatterbox/log.txt"));
        ViewPocketTtsLogsCommand = new RelayCommand(() => RunCommandInNewWindow("wsl -d DwemerAI4Skyrim3 -u dwemer -- tail -n 100 -f /home/dwemer/pocket-tts/log.txt"));
        ViewOmniVoiceLogsCommand = new RelayCommand(() => RunCommandInNewWindow("wsl -d DwemerAI4Skyrim3 -u dwemer -- tail -n 100 -f /home/dwemer/omnivoice-tts/logs/server.log"));
        ViewMeloTtsLogsCommand = new RelayCommand(() => RunCommandInNewWindow("wsl -d DwemerAI4Skyrim3 -u dwemer -- tail -n 100 -f /home/dwemer/MeloTTS/melo/log.txt"));
        ViewPiperLogsCommand = new RelayCommand(() => RunCommandInNewWindow("wsl -d DwemerAI4Skyrim3 -u dwemer -- tail -n 100 -f /home/dwemer/piper/log.txt"));
        ViewLocalWhisperLogsCommand = new RelayCommand(() => RunCommandInNewWindow("wsl -d DwemerAI4Skyrim3 -u dwemer -- tail -n 100 -f /home/dwemer/remote-faster-whisper/log.txt"));
        ViewParakeetLogsCommand = new RelayCommand(() => RunCommandInNewWindow("wsl -d DwemerAI4Skyrim3 -u dwemer -- tail -n 100 -f /home/dwemer/parakeet-api-server/log.txt"));
        ViewApacheLogsCommand = new RelayCommand(() => RunCommandInNewWindow("wsl -d DwemerAI4Skyrim3 -u dwemer -- tail -n 100 -f /var/log/apache2/error.log"));
        FixWslDnsCommand = new AsyncRelayCommand(FixWslDnsAsync);
        DistroDoctorCommand = new AsyncRelayCommand(RunDistroDoctorAsync);
        ReclaimDistroDiskSpaceCommand = new AsyncRelayCommand(ReclaimDistroDiskSpaceAsync);
        OpenCudaConfigCommand = new RelayCommand(() => _ = OpenCudaConfigWindowAsync());
        UpdateLauncherCommand = new AsyncRelayCommand(UpdateLauncherAsync, () => CanUpdateLauncher);
        CleanLogsCommand = new AsyncRelayCommand(CleanLogsAsync);
        GenerateDiagnosticsCommand = new AsyncRelayCommand(GenerateDiagnosticsAsync);
    }

    public string OutputText
    {
        get => _outputText;
        private set => SetProperty(ref _outputText, value);
    }

    public bool IsServerRunning
    {
        get => _isServerRunning;
        private set
        {
            if (SetProperty(ref _isServerRunning, value))
            {
                RaiseServerCommandStates();
            }
        }
    }

    public bool IsServerStarting
    {
        get => _isServerStarting;
        private set
        {
            if (SetProperty(ref _isServerStarting, value))
            {
                RaiseServerCommandStates();
            }
        }
    }

    public string StartButtonText
    {
        get => _startButtonText;
        private set => SetProperty(ref _startButtonText, value);
    }

    public string HerikaStatusText
    {
        get => _herikaStatusText;
        private set => SetProperty(ref _herikaStatusText, value);
    }

    public string HerikaStatusColor
    {
        get => _herikaStatusColor;
        private set => SetProperty(ref _herikaStatusColor, value);
    }

    public string StobeStatusText
    {
        get => _stobeStatusText;
        private set => SetProperty(ref _stobeStatusText, value);
    }

    public string StobeStatusColor
    {
        get => _stobeStatusColor;
        private set => SetProperty(ref _stobeStatusColor, value);
    }

    public string DialecticStatusText
    {
        get => _dialecticStatusText;
        private set => SetProperty(ref _dialecticStatusText, value);
    }

    public string DialecticStatusColor
    {
        get => _dialecticStatusColor;
        private set => SetProperty(ref _dialecticStatusColor, value);
    }

    public string LauncherVersionText
    {
        get => _launcherVersionText;
        private set => SetProperty(ref _launcherVersionText, value);
    }

    public string LauncherUpdateStatusText
    {
        get => _launcherUpdateStatusText;
        private set => SetProperty(ref _launcherUpdateStatusText, value);
    }

    public string LauncherUpdateStatusColor
    {
        get => _launcherUpdateStatusColor;
        private set => SetProperty(ref _launcherUpdateStatusColor, value);
    }

    public string LauncherUpdateButtonText
    {
        get => _launcherUpdateButtonText;
        private set => SetProperty(ref _launcherUpdateButtonText, value);
    }

    public string DistroUpdateButtonText
    {
        get => _distroUpdateButtonText;
        private set => SetProperty(ref _distroUpdateButtonText, value);
    }

    public bool IsDistroUpdateInProgress
    {
        get => _isDistroUpdateInProgress;
        private set
        {
            if (SetProperty(ref _isDistroUpdateInProgress, value))
            {
                UpdateAllCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool McpEnabled
    {
        get => _mcpEnabled;
        set => SetProperty(ref _mcpEnabled, value);
    }

    public bool IncludeHerikaServerUpdate
    {
        get => _includeHerikaServerUpdate;
        set => SetProperty(ref _includeHerikaServerUpdate, value);
    }

    public bool IncludeStobeServerUpdate
    {
        get => _includeStobeServerUpdate;
        set => SetProperty(ref _includeStobeServerUpdate, value);
    }

    public bool IncludeDialecticServerUpdate
    {
        get => _includeDialecticServerUpdate;
        set => SetProperty(ref _includeDialecticServerUpdate, value);
    }

    public bool CanUpdateLauncher
    {
        get => _canUpdateLauncher;
        private set
        {
            if (SetProperty(ref _canUpdateLauncher, value))
            {
                UpdateLauncherCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string TargetHerikaBranch
    {
        get => _targetHerikaBranch;
        set => SetProperty(ref _targetHerikaBranch, value);
    }

    public string TargetStobeBranch
    {
        get => _targetStobeBranch;
        set => SetProperty(ref _targetStobeBranch, value);
    }

    public string TargetDialecticBranch
    {
        get => _targetDialecticBranch;
        set => SetProperty(ref _targetDialecticBranch, value);
    }

    public ObservableCollection<string> HerikaBranches { get; }
    public ObservableCollection<string> StobeBranches { get; }
    public ObservableCollection<string> DialecticBranches { get; }

    public AsyncRelayCommand StartServerCommand { get; }
    public AsyncRelayCommand StopServerCommand { get; }
    public AsyncRelayCommand ForceStopServerCommand { get; }
    public AsyncRelayCommand UpdateAllCommand { get; }
    public RelayCommand OpenServerFolderCommand { get; }
    public RelayCommand OpenFirstRunSetupCommand { get; }
    public RelayCommand InstallComponentsCommand { get; }
    public RelayCommand OpenDebuggingCommand { get; }
    public AsyncRelayCommand SaveMcpEnabledCommand { get; }
    public AsyncRelayCommand SaveUpdateIncludeCommand { get; }
    public RelayCommand OpenChimCommand { get; }
    public RelayCommand OpenStobeCommand { get; }
    public RelayCommand OpenDialecticCommand { get; }
    public RelayCommand OpenWikiCommand { get; }
    public RelayCommand OpenDiscordCommand { get; }
    public RelayCommand OpenPiperVoicesFolderCommand { get; }
    public RelayCommand OpenTerminalCommand { get; }
    public RelayCommand ViewMemoryUsageCommand { get; }
    public AsyncRelayCommand ExportDistroCommand { get; }
    public AsyncRelayCommand ImportDistroCommand { get; }
    public RelayCommand OpenHerikaRollbackCommand { get; }
    public RelayCommand OpenStobeRollbackCommand { get; }
    public RelayCommand OpenDialecticRollbackCommand { get; }
    public RelayCommand ViewXttsLogsCommand { get; }
    public RelayCommand ViewChatterboxLogsCommand { get; }
    public RelayCommand ViewPocketTtsLogsCommand { get; }
    public RelayCommand ViewOmniVoiceLogsCommand { get; }
    public RelayCommand ViewMeloTtsLogsCommand { get; }
    public RelayCommand ViewPiperLogsCommand { get; }
    public RelayCommand ViewLocalWhisperLogsCommand { get; }
    public RelayCommand ViewParakeetLogsCommand { get; }
    public RelayCommand ViewApacheLogsCommand { get; }
    public AsyncRelayCommand FixWslDnsCommand { get; }
    public AsyncRelayCommand DistroDoctorCommand { get; }
    public AsyncRelayCommand ReclaimDistroDiskSpaceCommand { get; }
    public RelayCommand OpenCudaConfigCommand { get; }
    public AsyncRelayCommand UpdateLauncherCommand { get; }
    public AsyncRelayCommand CleanLogsCommand { get; }
    public AsyncRelayCommand GenerateDiagnosticsCommand { get; }

    public async Task InitializeAsync()
    {
        LauncherLogService.Startup("MainWindowViewModel initialization started.");
        StartProxyAndDiscovery();
        await RunStartupStepAsync("Load MCP setting", LoadMcpEnabledAsync, StartupSettingsTimeout).ConfigureAwait(true);
        await RunStartupStepAsync("Load update include settings", LoadUpdateIncludeSettingsAsync, StartupSettingsTimeout).ConfigureAwait(true);
        QueueBackgroundTask("Herika version check", cancellationToken => CheckForUpdatesAsync(cancellationToken), StartupVersionCheckTimeout);
        QueueBackgroundTask("Stobe version check", cancellationToken => CheckStobeServerUpdatesAsync(cancellationToken), StartupVersionCheckTimeout);
        QueueBackgroundTask("Dialectic version check", cancellationToken => CheckDialecticServerUpdatesAsync(cancellationToken), StartupVersionCheckTimeout);
        QueueBackgroundTask("Launcher update check", cancellationToken => CheckLauncherUpdatesAsync(cancellationToken), StartupVersionCheckTimeout);
        QueueServerStatusRefresh();
        LauncherLogService.Startup("MainWindowViewModel initialization completed.");
    }

    public async Task ShutdownAsync()
    {
        LauncherLogService.Startup("Launcher shutdown started.");
        _startAnimationTimer.Stop();
        _serverStatusRetryTimer.Stop();
        await (_tcpProxyService?.StopAsync() ?? Task.CompletedTask).ConfigureAwait(false);
        await (_discoveryService?.StopAsync() ?? Task.CompletedTask).ConfigureAwait(false);
        _processRunner.TryKill(_serverProcess);
        LauncherLogService.Startup("Launcher shutdown completed.");
    }

    public async Task RunFirstRunSetupStartupCheckAsync()
    {
        LauncherLogService.Startup("First-time setup startup check started.");

        try
        {
            using var probeCts = new CancellationTokenSource(StartupFirstRunProbeTimeout);
            if (!await ShouldShowFirstRunSetupAsync(probeCts.Token).ConfigureAwait(false))
            {
                LauncherLogService.Startup("First-time setup startup check completed: not needed.");
                return;
            }
        }
        catch (OperationCanceledException)
        {
            LauncherLogService.Startup($"First-time setup startup check timed out after {StartupFirstRunProbeTimeout.TotalSeconds:0} seconds.");
            AppendLog("First-time setup check timed out. Open Debugging > First-Time Setup if this is a fresh install." + Environment.NewLine, "yellow");
            return;
        }
        catch (Exception ex)
        {
            LauncherLogService.Startup("First-time setup startup check failed.", ex);
            AppendLog($"First-time setup check failed: {ex.Message}{Environment.NewLine}", "yellow");
            return;
        }

        try
        {
            using var updateCts = new CancellationTokenSource(StartupLauncherUpdateTimeout);
            if (await TryApplyLauncherUpdateBeforeFirstRunSetupAsync(updateCts.Token).ConfigureAwait(false))
            {
                LauncherLogService.Startup("Launcher update started before first-time setup.");
                return;
            }
        }
        catch (OperationCanceledException)
        {
            LauncherLogService.Startup($"Launcher update check before first-time setup timed out after {StartupLauncherUpdateTimeout.TotalSeconds:0} seconds.");
            AppendLog("Launcher update check timed out. Continuing first-time setup." + Environment.NewLine, "yellow");
        }
        catch (Exception ex)
        {
            LauncherLogService.Startup("Launcher update check before first-time setup failed.", ex);
            AppendLog($"Launcher update check before setup failed: {ex.Message}{Environment.NewLine}", "yellow");
        }

        LauncherLogService.Startup("Opening first-time setup from startup check.");
        OpenFirstRunSetupWindow();
    }

    public Task<bool> ShouldShowFirstRunSetupAsync(CancellationToken cancellationToken = default)
    {
        return FirstRunSetupViewModel.ShouldShowFirstRunSetupAsync(cancellationToken);
    }

    public async Task<bool> TryApplyLauncherUpdateBeforeFirstRunSetupAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            SetLauncherUpdateState("Launcher update: checking before setup...", "White", false, "Checking...");

            var currentVersion = _launcherUpdateService.GetCurrentVersion().ToString(3);
            RunOnUi(() => LauncherVersionText = $"Launcher Version: {currentVersion}");

            var update = await _launcherUpdateService.CheckForUpdatesAsync(cancellationToken).ConfigureAwait(false);
            _pendingLauncherUpdate = update;
            if (update is null)
            {
                SetLauncherUpdateState(
                    $"Launcher update: up to date [{currentVersion}]",
                    "LimeGreen",
                    false,
                    "Up To Date");
                return false;
            }

            var targetVersion = update.Version.ToString(3);
            AppendLog($"Launcher update {targetVersion} found. Updating before first-time setup...{Environment.NewLine}", "yellow");
            SetLauncherUpdateState(
                $"Launcher update required before setup [{currentVersion} -> {targetVersion}]",
                "Red",
                false,
                "Updating...");

            var packagePath = await _launcherUpdateService.DownloadUpdatePackageAsync(update, progress =>
            {
                var statusText = $"Downloading launcher update before setup... {progress}%";
                SetLauncherUpdateState(statusText, "White", false, $"Update {progress}%");
            }, cancellationToken).ConfigureAwait(false);

            AppendLog("Launcher update downloaded. Closing launcher to apply update before first-time setup..." + Environment.NewLine, "green");
            _launcherUpdateService.StartUpdaterAndExit(packagePath);
            RunOnUi(() =>
            {
                LauncherUpdateButtonText = "Applying...";
                Application.Current.Shutdown();
            });
            return true;
        }
        catch (Exception ex)
        {
            _pendingLauncherUpdate = null;
            SetLauncherUpdateState(
                "Launcher update before setup failed. Continuing setup.",
                "Yellow",
                false,
                "Check Again");
            AppendLog($"Launcher update before first-time setup failed: {ex.Message}{Environment.NewLine}", "yellow");
            return false;
        }
    }

    private async Task RunStartupStepAsync(
        string name,
        Func<CancellationToken, Task> action,
        TimeSpan timeout)
    {
        LauncherLogService.Startup($"{name} started.");
        using var timeoutCts = new CancellationTokenSource(timeout);

        try
        {
            await action(timeoutCts.Token).ConfigureAwait(true);
            LauncherLogService.Startup($"{name} completed.");
        }
        catch (OperationCanceledException)
        {
            LauncherLogService.Startup($"{name} timed out after {timeout.TotalSeconds:0} seconds.");
            AppendLog($"{name} timed out. Continuing launcher startup.{Environment.NewLine}", "yellow");
        }
        catch (Exception ex)
        {
            LauncherLogService.Startup($"{name} failed.", ex);
            AppendLog($"{name} failed: {ex.Message}{Environment.NewLine}", "yellow");
        }
    }

    private void QueueBackgroundTask(
        string name,
        Func<CancellationToken, Task> action,
        TimeSpan timeout)
    {
        _ = Task.Run(async () =>
        {
            using var timeoutCts = new CancellationTokenSource(timeout);
            try
            {
                await action(timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                LauncherLogService.Startup($"{name} timed out after {timeout.TotalSeconds:0} seconds.");
            }
            catch (Exception ex)
            {
                LauncherLogService.Startup($"{name} failed.", ex);
                AppendLog($"{name} failed: {ex.Message}{Environment.NewLine}", "yellow");
            }
        });
    }

    private void StartProxyAndDiscovery()
    {
        try
        {
            _tcpProxyService = new TcpProxyService(async cancellationToken =>
            {
                var ip = await GetWslIpAsync(forceRefresh: true, cancellationToken).ConfigureAwait(false);
                return ip is null ? null : new IPEndPoint(IPAddress.Parse(ip), LauncherConstants.SkyrimServerPort);
            }, text => AppendLog(text));
            _tcpProxyService.Start();
        }
        catch (Exception ex)
        {
            _tcpProxyService = null;
            LauncherLogService.Startup("TCP proxy failed to start.", ex);
            AppendLog($"TCP proxy failed to start: {ex.Message}{Environment.NewLine}", "yellow");
        }

        try
        {
            _discoveryService = new DiscoveryService(
                cancellationToken => GetWslIpAsync(forceRefresh: false, cancellationToken),
                text => AppendLog(text));
            _discoveryService.Start();
        }
        catch (Exception ex)
        {
            _discoveryService = null;
            LauncherLogService.Startup("Discovery service failed to start.", ex);
            AppendLog($"Discovery service failed to start: {ex.Message}{Environment.NewLine}", "yellow");
        }
    }

    private async Task StartServerAsync()
    {
        if (IsServerRunning || IsServerStarting)
        {
            MessageBox.Show("The server is already running or starting.", "Server Status");
            return;
        }

        _wslIp = null;
        IsServerStarting = true;
        StartButtonText = "Server is Starting";
        StartStartAnimation();

        try
        {
            _serverProcess = _processRunner.StartHiddenProcess(
                "wsl.exe",
                new[] { "-d", LauncherConstants.DistroName, "--", "/etc/start_env" },
                line =>
                {
                    AppendLog(line);
                    if (line.Contains("AIAgent.ini Network Settings:", StringComparison.OrdinalIgnoreCase))
                    {
                        RunOnUi(() =>
                        {
                            StopStartAnimation();
                            IsServerRunning = true;
                            IsServerStarting = false;
                            StartButtonText = "Server is Running";
                        });
                        AppendLog("Server is ready." + Environment.NewLine);
                        _ = Task.Run(() => GetWslIpAsync(forceRefresh: true));
                    }
                },
                redirectInput: true);

            AppendLog("DwemerDistro is starting up." + Environment.NewLine);
            await _serverProcess.WaitForExitAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            AppendLog($"An error occurred: {ex.Message}{Environment.NewLine}", "red");
        }
        finally
        {
            if (!IsServerRunning)
            {
                StopStartAnimation();
                IsServerStarting = false;
                StartButtonText = "Start Server";
            }
        }
    }

    private async Task StopServerAsync()
    {
        if (!IsServerRunning && !IsServerStarting)
        {
            MessageBox.Show("The server is not currently running.", "Server Status");
            return;
        }

        try
        {
            if (_serverProcess is { HasExited: false })
            {
                await TryStopTrackedServerProcessAsync(
                        TimeSpan.FromSeconds(5),
                        killOnTimeout: true,
                        timeoutMessage: "DwemerDistro server process did not exit within 5 seconds.")
                    .ConfigureAwait(false);
            }
            else
            {
                AppendLog("DwemerDistro process not running or already stopped." + Environment.NewLine);
            }

            await _wsl.RunWslAsync(new[] { "-t", LauncherConstants.DistroName }).ConfigureAwait(false);
            AppendLog("DwemerDistro terminated." + Environment.NewLine);
        }
        catch (Exception ex)
        {
            AppendLog($"An error occurred during stop: {ex.Message}{Environment.NewLine}", "red");
        }
        finally
        {
            RunOnUi(() =>
            {
                StopStartAnimation();
                IsServerRunning = false;
                IsServerStarting = false;
                StartButtonText = "Start Server";
            });
        }
    }

    private async Task ForceStopServerAsync()
    {
        try
        {
            await _wsl.RunWslAsync(new[] { "-t", LauncherConstants.DistroName }).ConfigureAwait(false);
            AppendLog("DwemerDistro force terminated command sent." + Environment.NewLine);
            _processRunner.TryKill(_serverProcess);
        }
        catch (Exception ex)
        {
            AppendLog($"An error occurred during force stop: {ex.Message}{Environment.NewLine}", "red");
        }
        finally
        {
            RunOnUi(() =>
            {
                StopStartAnimation();
                IsServerRunning = false;
                IsServerStarting = false;
                StartButtonText = "Start Server";
            });
        }
    }

    private async Task UpdateAllAsync()
    {
        await RunDistroUpdateAsync(requireConfirmation: true, sourceLabel: "Distro Updates").ConfigureAwait(true);
    }

    public Task<bool> UpdateDistroFromQuickstartAsync()
    {
        return RunDistroUpdateAsync(requireConfirmation: false, sourceLabel: "Quickstart");
    }

    private async Task<bool> RunDistroUpdateAsync(bool requireConfirmation, string sourceLabel)
    {
        if (IsDistroUpdateInProgress)
        {
            AppendLog("Distro update is already running." + Environment.NewLine, "yellow");
            return false;
        }

        var includeHerika = IncludeHerikaServerUpdate;
        var includeStobe = IncludeStobeServerUpdate;
        var includeDialectic = IncludeDialecticServerUpdate;
        var targetHerika = NormalizeBranch(TargetHerikaBranch, "aiagent", "aiagent", "dev", "unstable");
        var targetStobe = NormalizeBranch(TargetStobeBranch, "stobe", "stobe", "dev", "unstable");
        var targetDialectic = NormalizeBranch(TargetDialecticBranch, "dialectic", "dialectic", "dev", "unstable");

        var confirmText = includeHerika || includeStobe || includeDialectic
            ? "This will update the Dwemer Distro and selected server components.\n\n" +
              (includeHerika ? $"HerikaServer target branch: {targetHerika}\n" : "HerikaServer update: disabled\n") +
              (includeStobe ? $"StobeServer target branch: {targetStobe}\n" : "StobeServer update: disabled\n") +
              (includeDialectic ? $"DialecticServer target branch: {targetDialectic}\n" : "DialecticServer update: disabled\n") +
              "\nAre you sure?"
            : "This will update Dwemer Distro only.\n\nAll server updates are disabled in the Distro Updates section.\n\nAre you sure?";

        if (requireConfirmation &&
            MessageBox.Show(confirmText, "Update System", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            AppendLog("Update canceled." + Environment.NewLine);
            return false;
        }

        IsDistroUpdateInProgress = true;
        DistroUpdateButtonText = sourceLabel.Equals("Quickstart", StringComparison.OrdinalIgnoreCase)
            ? "Quickstart Updating..."
            : "Updating...";

        try
        {
            SetHerikaStatus("Updating...", "White");
            if (includeStobe)
            {
                SetStobeStatus("Updating...", "White");
            }
            if (includeDialectic)
            {
                SetDialecticStatus("Updating...", "White");
            }

            AppendLog("Starting system update for the selected server components..." + Environment.NewLine);
            AppendLog("Preparing update steps..." + Environment.NewLine);
            await FlushUpdateUiAsync().ConfigureAwait(true);

            if (includeHerika)
            {
                AppendLog(Environment.NewLine + "STEP 1: Prepare HerikaServer branch" + Environment.NewLine, "green");
                if (!await SwitchHerikaServerBranchAsync(targetHerika).ConfigureAwait(false))
                {
                    return false;
                }
            }

            if (includeStobe)
            {
                AppendLog(Environment.NewLine + (includeHerika ? "STEP 2: Prepare StobeServer branch" : "STEP 1: Prepare StobeServer branch") + Environment.NewLine, "green");
                if (!await SwitchStobeServerBranchAsync(targetStobe).ConfigureAwait(false))
                {
                    return false;
                }
            }

            if (includeDialectic)
            {
                AppendLog(Environment.NewLine + "Prepare DialecticServer branch" + Environment.NewLine, "green");
                if (!await SwitchDialecticServerBranchAsync(targetDialectic).ConfigureAwait(false))
                {
                    return false;
                }
            }

            AppendLog(Environment.NewLine + (includeHerika || includeStobe || includeDialectic
                ? "STEP 3: Run DwemerDistro core update and component update"
                : "STEP 1: Run DwemerDistro core update") + Environment.NewLine, "green");
            AppendLog("Executing update script..." + Environment.NewLine);
            await FlushUpdateUiAsync().ConfigureAwait(true);

            var serverUpdateRequested = includeHerika || includeStobe || includeDialectic;
            var gwsFlags = new List<string>();
            if (!includeHerika)
            {
                gwsFlags.Add("--skip-herika");
            }
            if (!includeStobe)
            {
                gwsFlags.Add("--skip-stobe");
            }
            if (!includeDialectic)
            {
                gwsFlags.Add("--skip-dialectic");
            }

            var gwsCommand = "/usr/local/bin/update_gws";
            if (gwsFlags.Count > 0)
            {
                gwsCommand += " " + string.Join(" ", gwsFlags);
            }

            var bashCommand = serverUpdateRequested
                ? "cd /home/dwemer/dwemerdistro && git fetch origin && git reset --hard origin/main && " +
                  "chmod +x update.sh && echo 'dwemer' | sudo -S ./update.sh && " +
                  "echo '=====MARKER:BEGIN_SERVER_UPDATE=====' && " + gwsCommand
                : "cd /home/dwemer/dwemerdistro && git fetch origin && git reset --hard origin/main && " +
                  "chmod +x update.sh && echo 'dwemer' | sudo -S ./update.sh";

            var distroUpdateComplete = false;
            var serverUpdateStarted = false;
            var serverUpdateComplete = false;
            var branchErrorDetected = false;

            var result = await _wsl.RunBashAsync(bashCommand, line =>
            {
                if (serverUpdateRequested && line.Contains("=====MARKER:BEGIN_SERVER_UPDATE=====", StringComparison.OrdinalIgnoreCase))
                {
                    distroUpdateComplete = true;
                    serverUpdateStarted = true;
                    AppendLog(Environment.NewLine + "STEP 4: Dwemer Distro Server & Components Update" + Environment.NewLine, "green");
                    return;
                }

                AppendLog(line);
                var lowered = line.ToLowerInvariant();
                if (serverUpdateRequested &&
                    (lowered.Contains("you are not currently on a branch") ||
                     lowered.Contains("please specify which branch you want to merge with")))
                {
                    branchErrorDetected = true;
                }

                if (serverUpdateRequested && serverUpdateStarted && (line.Contains("Successfully") || line.Contains("Completed")))
                {
                    serverUpdateComplete = true;
                }
            }, loginShell: false, lineBuffered: true).ConfigureAwait(false);

            if (!serverUpdateRequested)
            {
                distroUpdateComplete = result.Succeeded;
            }

            var updateSucceeded = result.Succeeded && distroUpdateComplete && (!serverUpdateRequested || !branchErrorDetected);
            if (updateSucceeded)
            {
                var statusParts = new List<string>();
                if (includeHerika)
                {
                    statusParts.Add($"HerikaServer: {await GetCurrentBranchAsync().ConfigureAwait(false) ?? "unknown"}");
                }
                if (includeStobe)
                {
                    statusParts.Add($"StobeServer: {await GetStobeServerCurrentBranchAsync().ConfigureAwait(false) ?? "unknown"}");
                }
                if (includeDialectic)
                {
                    statusParts.Add($"DialecticServer: {await GetDialecticServerCurrentBranchAsync().ConfigureAwait(false) ?? "unknown"}");
                }

                if (serverUpdateRequested && serverUpdateComplete)
                {
                    AppendLog($"System update completed successfully! {string.Join(" | ", statusParts)}{Environment.NewLine}", "green");
                }
                else if (serverUpdateRequested)
                {
                    AppendLog($"Update completed. {string.Join(" | ", statusParts)}{Environment.NewLine}", "green");
                }
                else
                {
                    AppendLog("Distro update completed successfully. Server updates were skipped." + Environment.NewLine, "green");
                }
            }
            else
            {
                AppendLog("Update may have encountered issues. Check logs above." + Environment.NewLine, "red");
            }

            return updateSucceeded;
        }
        catch (Exception ex)
        {
            AppendLog($"Error during update: {ex.Message}{Environment.NewLine}", "red");
            return false;
        }
        finally
        {
            RunOnUi(() =>
            {
                IsDistroUpdateInProgress = false;
                DistroUpdateButtonText = "Update";
            });
            QueueBackgroundTask("Herika version check", cancellationToken => CheckForUpdatesAsync(cancellationToken), StartupVersionCheckTimeout);
            QueueBackgroundTask("Stobe version check", cancellationToken => CheckStobeServerUpdatesAsync(cancellationToken), StartupVersionCheckTimeout);
            QueueBackgroundTask("Dialectic version check", cancellationToken => CheckDialecticServerUpdatesAsync(cancellationToken), StartupVersionCheckTimeout);
        }
    }

    private async Task<bool> SwitchHerikaServerBranchAsync(string targetBranch)
    {
        if (targetBranch is not ("aiagent" or "dev" or "unstable"))
        {
            AppendLog($"Invalid branch selection: '{targetBranch}'. Expected aiagent, dev, or unstable.{Environment.NewLine}", "red");
            return false;
        }

        var currentBranch = await GetCurrentBranchAsync().ConfigureAwait(false);
        if (currentBranch == targetBranch)
        {
            AppendLog($"HerikaServer already on branch '{targetBranch}'." + Environment.NewLine);
            return true;
        }

        AppendLog($"Switching HerikaServer branch to '{targetBranch}'..." + Environment.NewLine);
        var result = await _wsl.RunBashAsync(
            "cd /var/www/html/HerikaServer && " +
            "git stash save 'Auto-stash before switching branch' && " +
            "git fetch origin && " +
            $"git checkout -B {targetBranch} origin/{targetBranch}",
            line => AppendLog(line),
            loginShell: false,
            lineBuffered: true).ConfigureAwait(false);

        if (!result.Succeeded)
        {
            AppendLog($"Failed to switch HerikaServer branch to '{targetBranch}'." + Environment.NewLine, "red");
            AppendLog((result.StandardError + result.StandardOutput).Trim() + Environment.NewLine, "red");
            return false;
        }

        AppendLog($"Successfully switched HerikaServer to '{targetBranch}'." + Environment.NewLine, "green");
        return true;
    }

    private async Task<bool> SwitchStobeServerBranchAsync(string targetBranch)
    {
        if (targetBranch is not ("stobe" or "dev" or "unstable"))
        {
            AppendLog($"Invalid StobeServer branch selection: '{targetBranch}'. Expected stobe, dev, or unstable.{Environment.NewLine}", "red");
            return false;
        }

        if (!await EnsureStobeServerRepoExistsAsync(targetBranch).ConfigureAwait(false))
        {
            return false;
        }

        var currentBranch = await GetStobeServerCurrentBranchAsync().ConfigureAwait(false);
        if (currentBranch == targetBranch)
        {
            AppendLog($"StobeServer already on branch '{targetBranch}'." + Environment.NewLine);
            return true;
        }

        AppendLog($"Switching StobeServer branch to '{targetBranch}'..." + Environment.NewLine);
        var result = await _wsl.RunBashAsync(
            "cd /var/www/html/StobeServer && " +
            "git stash save 'Auto-stash before switching branch' && " +
            "git fetch origin && " +
            $"git checkout -B {targetBranch} origin/{targetBranch}",
            line => AppendLog(line),
            loginShell: false,
            lineBuffered: true).ConfigureAwait(false);

        if (!result.Succeeded)
        {
            AppendLog($"Failed to switch StobeServer branch to '{targetBranch}'." + Environment.NewLine, "red");
            AppendLog((result.StandardError + result.StandardOutput).Trim() + Environment.NewLine, "red");
            return false;
        }

        AppendLog($"Successfully switched StobeServer to '{targetBranch}'." + Environment.NewLine, "green");
        return true;
    }

    private async Task<bool> EnsureStobeServerRepoExistsAsync(string targetBranch)
    {
        AppendLog("Checking StobeServer repository state..." + Environment.NewLine);
        var result = await _wsl.RunBashAsync(
            "base_dir=/var/www/html; repo_path=/var/www/html/StobeServer; state=EXISTS; " +
            "mkdir -p \"$base_dir\" || { echo ERROR:BASE_DIR_CREATE_FAILED >&2; exit 1; }; " +
            "if [ ! -d \"$repo_path/.git\" ]; then " +
            "state=CLONED; " +
            "for legacy_path in /var/www/html/stobeserver /var/www/html/stoberser; do " +
            "if [ -d \"$legacy_path/.git\" ]; then rm -rf \"$repo_path\" && mv \"$legacy_path\" \"$repo_path\" && state=MIGRATED:${legacy_path}; break; fi; " +
            "done; " +
            "if [ ! -d \"$repo_path/.git\" ]; then rm -rf \"$repo_path\" && " +
            $"git clone -b {targetBranch} https://github.com/Dwemer-Dynamics/StobeServer.git \"$repo_path\" 1>&2 && state=CLONED:{targetBranch}; " +
            "fi; fi; " +
            "mkdir -p \"$repo_path/log\"; : > \"$repo_path/log/stobe_import.log\"; : > \"$repo_path/log/stobeserver.log\"; echo \"$state\"",
            line => AppendLog(line),
            loginShell: false,
            lineBuffered: true).ConfigureAwait(false);

        if (!result.Succeeded)
        {
            AppendLog("Failed to prepare StobeServer repository for branch switch." + Environment.NewLine, "red");
            AppendLog((result.StandardError + result.StandardOutput).Trim() + Environment.NewLine, "red");
            return false;
        }

        var output = result.StandardOutput.Trim();
        if (output.StartsWith("CLONED:", StringComparison.OrdinalIgnoreCase))
        {
            AppendLog($"StobeServer was missing and has been cloned on branch '{output.Split(':').Last()}'." + Environment.NewLine, "yellow");
        }
        else if (output.StartsWith("MIGRATED:", StringComparison.OrdinalIgnoreCase))
        {
            AppendLog($"Recovered StobeServer from legacy path '{output.Split(':').Last()}' and migrated to /var/www/html/StobeServer." + Environment.NewLine, "yellow");
        }

        return true;
    }

    private async Task<bool> SwitchDialecticServerBranchAsync(string targetBranch)
    {
        if (targetBranch is not ("dialectic" or "dev" or "unstable"))
        {
            AppendLog($"Invalid DialecticServer branch selection: '{targetBranch}'. Expected dialectic, dev, or unstable.{Environment.NewLine}", "red");
            return false;
        }

        if (!await EnsureDialecticServerRepoExistsAsync(targetBranch).ConfigureAwait(false))
        {
            return false;
        }

        var currentBranch = await GetDialecticServerCurrentBranchAsync().ConfigureAwait(false);
        if (currentBranch == targetBranch)
        {
            AppendLog($"DialecticServer already on branch '{targetBranch}'." + Environment.NewLine);
            return true;
        }

        AppendLog($"Switching DialecticServer branch to '{targetBranch}'..." + Environment.NewLine);
        var result = await _wsl.RunBashAsync(
            "cd /var/www/html/DialecticServer && " +
            "git stash push -u -m 'Auto-stash before switching branch' >/dev/null 2>&1 || true; " +
            "git fetch origin && " +
            $"git checkout -B {targetBranch} origin/{targetBranch}",
            line => AppendLog(line),
            loginShell: false,
            lineBuffered: true).ConfigureAwait(false);

        if (!result.Succeeded)
        {
            AppendLog($"Failed to switch DialecticServer branch to '{targetBranch}'." + Environment.NewLine, "red");
            AppendLog((result.StandardError + result.StandardOutput).Trim() + Environment.NewLine, "red");
            return false;
        }

        AppendLog($"Successfully switched DialecticServer to '{targetBranch}'." + Environment.NewLine, "green");
        return true;
    }

    private async Task<bool> EnsureDialecticServerRepoExistsAsync(string targetBranch)
    {
        AppendLog("Checking DialecticServer repository state..." + Environment.NewLine);
        var result = await _wsl.RunBashAsync(
            "set -e; repo_path=/var/www/html/DialecticServer; backup_path=''; state=EXISTS; mkdir -p /var/www/html; " +
            "if [ -d \"$repo_path\" ] && [ ! -d \"$repo_path/.git\" ]; then " +
            "backup_path=\"${repo_path}.pre-git-$(date -u +%Y%m%d%H%M%S)\"; mv \"$repo_path\" \"$backup_path\"; state=MIGRATED:$backup_path; fi; " +
            "if [ ! -d \"$repo_path/.git\" ]; then " +
            $"if ! git clone -b {targetBranch} https://github.com/Dwemer-Dynamics/DialecticServer.git \"$repo_path\" 1>&2; then " +
            "rm -rf \"$repo_path\"; if [ -n \"$backup_path\" ]; then mv \"$backup_path\" \"$repo_path\"; fi; exit 1; fi; " +
            "if [ -z \"$backup_path\" ]; then state=CLONED; fi; fi; " +
            "if [ -n \"$backup_path\" ] && [ -d \"$backup_path\" ]; then " +
            "for relative_path in conf/conf.php conf/character_map.json; do " +
            "if [ -e \"$backup_path/$relative_path\" ]; then mkdir -p \"$repo_path/$(dirname \"$relative_path\")\"; cp -a \"$backup_path/$relative_path\" \"$repo_path/$relative_path\"; fi; done; " +
            "for relative_path in uploads data/voices soundcache log; do if [ -d \"$backup_path/$relative_path\" ]; then mkdir -p \"$repo_path/$relative_path\"; cp -a \"$backup_path/$relative_path/.\" \"$repo_path/$relative_path/\"; fi; done; " +
            "find \"$backup_path/conf\" -maxdepth 1 -type f -name 'conf_*.php' -exec cp -a {} \"$repo_path/conf/\" \\; 2>/dev/null || true; fi; " +
            "echo \"$state\"",
            line => AppendLog(line),
            loginShell: false,
            lineBuffered: true).ConfigureAwait(false);

        if (!result.Succeeded)
        {
            AppendLog("Failed to prepare DialecticServer repository." + Environment.NewLine, "red");
            AppendLog((result.StandardError + result.StandardOutput).Trim() + Environment.NewLine, "red");
            return false;
        }

        if (result.StandardOutput.Contains("MIGRATED:", StringComparison.OrdinalIgnoreCase))
        {
            AppendLog("Migrated the existing DialecticServer deployment into a managed Git checkout while preserving runtime data." + Environment.NewLine, "yellow");
        }
        else if (result.StandardOutput.Contains("CLONED", StringComparison.OrdinalIgnoreCase))
        {
            AppendLog($"Installed DialecticServer on branch '{targetBranch}'." + Environment.NewLine, "green");
        }

        return true;
    }

    private async Task<string?> GetCurrentBranchAsync(CancellationToken cancellationToken = default)
    {
        var result = await _wsl.RunBashAsync(
                "cd /var/www/html/HerikaServer && git rev-parse --abbrev-ref HEAD",
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        return result.Succeeded ? result.StandardOutput.Trim() : null;
    }

    private async Task<string?> GetStobeServerCurrentBranchAsync(CancellationToken cancellationToken = default)
    {
        var result = await _wsl.RunBashAsync(
                "cd /var/www/html/StobeServer && git rev-parse --abbrev-ref HEAD",
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        return result.Succeeded ? result.StandardOutput.Trim() : null;
    }

    private async Task<string?> GetDialecticServerCurrentBranchAsync(CancellationToken cancellationToken = default)
    {
        var result = await _wsl.RunBashAsync(
                "cd /var/www/html/DialecticServer && git rev-parse --abbrev-ref HEAD",
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        return result.Succeeded ? result.StandardOutput.Trim() : null;
    }

    private async Task CheckForUpdatesAsync(CancellationToken cancellationToken = default)
    {
        SetHerikaStatus("Checking...", "White");
        var currentBranch = await GetCurrentBranchAsync(cancellationToken).ConfigureAwait(false);
        if (currentBranch is "aiagent" or "dev" or "unstable")
        {
            RunOnUi(() => TargetHerikaBranch = currentBranch);
        }

        var currentVersion = await ReadWslFileFirstLineAsync("/var/www/html/HerikaServer/.version.txt", cancellationToken).ConfigureAwait(false);
        var semanticVersion = await ReadWslFileFirstLineAsync("/var/www/html/HerikaServer/.version_number.txt", cancellationToken).ConfigureAwait(false);
        var gitVersion = currentBranch is null
            ? null
            : await GetTextOrNullAsync($"https://raw.githubusercontent.com/abeiro/HerikaServer/{currentBranch}/.version.txt", cancellationToken).ConfigureAwait(false);

        var statusText = BuildServerVersionStatusText(
            "herika",
            currentBranch,
            FormatDateVersion(currentVersion),
            semanticVersion);

        if (!string.IsNullOrWhiteSpace(currentVersion) && !string.IsNullOrWhiteSpace(gitVersion))
        {
            var comparison = CompareVersions(currentVersion, gitVersion);
            SetHerikaStatus(statusText, comparison < 0 ? "Red" : "LimeGreen");
        }
        else if (!string.IsNullOrWhiteSpace(currentVersion) || !string.IsNullOrWhiteSpace(semanticVersion))
        {
            SetHerikaStatus(statusText, "LimeGreen");
        }
        else
        {
            SetHerikaStatus(BuildServerVersionStatusText("herika", currentBranch, null, null), "Yellow");
        }
    }

    private async Task CheckStobeServerUpdatesAsync(CancellationToken cancellationToken = default)
    {
        SetStobeStatus("Checking...", "White");
        var currentBranch = await GetStobeServerCurrentBranchAsync(cancellationToken).ConfigureAwait(false);
        if (currentBranch is "stobe" or "dev" or "unstable")
        {
            RunOnUi(() => TargetStobeBranch = currentBranch);
        }

        var currentVersion = await ReadWslFileFirstLineAsync("/var/www/html/StobeServer/.version.txt", cancellationToken).ConfigureAwait(false);
        var semanticVersion =
            await ReadWslFileFirstLineAsync("/var/www/html/StobeServer/.version_number.txt", cancellationToken).ConfigureAwait(false) ??
            await ReadWslFileFirstLineAsync("/var/www/html/StobeServer/versionnumber.txt", cancellationToken).ConfigureAwait(false);
        var gitVersion = currentBranch is null
            ? null
            : await GetTextOrNullAsync($"https://raw.githubusercontent.com/Dwemer-Dynamics/StobeServer/{currentBranch}/.version.txt", cancellationToken).ConfigureAwait(false);

        var statusText = BuildServerVersionStatusText(
            "stobe",
            currentBranch,
            FormatDateVersion(currentVersion),
            semanticVersion);

        if (!string.IsNullOrWhiteSpace(currentVersion) && !string.IsNullOrWhiteSpace(gitVersion))
        {
            var comparison = CompareVersions(currentVersion, gitVersion);
            SetStobeStatus(statusText, comparison < 0 ? "Red" : "LimeGreen");
        }
        else if (!string.IsNullOrWhiteSpace(currentVersion) || !string.IsNullOrWhiteSpace(semanticVersion))
        {
            SetStobeStatus(statusText, "LimeGreen");
        }
        else
        {
            SetStobeStatus(BuildServerVersionStatusText("stobe", currentBranch, null, null), "Yellow");
        }
    }

    private async Task CheckLauncherUpdatesAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            SetLauncherUpdateState("Launcher update: checking...", "White", false, "Checking...");

            var currentVersion = _launcherUpdateService.GetCurrentVersion().ToString(3);
            RunOnUi(() => LauncherVersionText = $"Launcher Version: {currentVersion}");

            var update = await _launcherUpdateService.CheckForUpdatesAsync(cancellationToken).ConfigureAwait(false);
            _pendingLauncherUpdate = update;

            if (update is null)
            {
                SetLauncherUpdateState(
                    $"Launcher update: up to date [{currentVersion}]",
                    "LimeGreen",
                    false,
                    "Up To Date");
                return;
            }

            var targetVersion = update.Version.ToString(3);
            SetLauncherUpdateState(
                $"Launcher update available [{currentVersion} -> {targetVersion}]",
                "Red",
                true,
                "Update Launcher");
        }
        catch (Exception ex)
        {
            _pendingLauncherUpdate = null;
            SetLauncherUpdateState(
                "Launcher update check failed. See log.",
                "Yellow",
                false,
                "Check Again");
            AppendLog($"Launcher update check failed: {ex.Message}{Environment.NewLine}", "yellow");
        }
    }

    private async Task UpdateLauncherAsync()
    {
        try
        {
            if (_pendingLauncherUpdate is null)
            {
                await CheckLauncherUpdatesAsync().ConfigureAwait(false);
                if (_pendingLauncherUpdate is null)
                {
                    return;
                }
            }

            CanUpdateLauncher = false;
            RunOnUi(() => LauncherUpdateButtonText = "Downloading...");
            AppendLog("Downloading launcher update..." + Environment.NewLine);

            var packagePath = await _launcherUpdateService.DownloadUpdatePackageAsync(_pendingLauncherUpdate, progress =>
            {
                var statusText = $"Downloading launcher update... {progress}%";
                SetLauncherUpdateState(statusText, "White", false, $"Update {progress}%");
            }).ConfigureAwait(false);

            AppendLog("Launcher update downloaded. Closing launcher to apply update..." + Environment.NewLine, "green");
            _launcherUpdateService.StartUpdaterAndExit(packagePath);
            RunOnUi(() =>
            {
                LauncherUpdateButtonText = "Applying...";
                Application.Current.Shutdown();
            });
        }
        catch (Exception ex)
        {
            SetLauncherUpdateState(
                "Launcher update failed. See log.",
                "Red",
                true,
                "Retry Update");
            AppendLog($"Launcher update failed: {ex.Message}{Environment.NewLine}", "red");
        }
    }

    private async Task LoadMcpEnabledAsync(CancellationToken cancellationToken = default)
    {
        var result = await _wsl.RunDistroAsync(
                new[] { "bash", "-lc", "if [ -f /home/dwemer/.mcp_enabled ]; then cat /home/dwemer/.mcp_enabled; else echo 1 > /home/dwemer/.mcp_enabled; echo 1; fi" },
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        RunOnUi(() => McpEnabled = result.Succeeded ? result.StandardOutput.Trim() == "1" : true);
    }

    private async Task SaveMcpEnabledAsync()
    {
        var value = McpEnabled ? "1" : "0";
        var result = await _wsl.RunDistroAsync(new[] { "bash", "-lc", $"echo {value} > /home/dwemer/.mcp_enabled" })
            .ConfigureAwait(false);
        if (result.Succeeded)
        {
            AppendLog(McpEnabled
                    ? "MCP service enabled. Restart server to apply." + Environment.NewLine
                    : "MCP service disabled. Restart server to apply." + Environment.NewLine,
                McpEnabled ? "green" : "red");
        }
        else
        {
            AppendLog($"Failed to save MCP setting: {result.StandardError}{Environment.NewLine}", "red");
        }
    }

    private async Task LoadUpdateIncludeSettingsAsync(CancellationToken cancellationToken = default)
    {
        var result = await _wsl.RunDistroAsUserAsync(
            "root",
            new[] { "bash", "-lc", "mkdir -p /home/dwemer; if [ ! -f /home/dwemer/.update_include_herika ]; then echo 1 > /home/dwemer/.update_include_herika; fi; if [ ! -f /home/dwemer/.update_include_stobe ]; then echo 1 > /home/dwemer/.update_include_stobe; fi; if [ ! -f /home/dwemer/.update_include_dialectic ]; then echo 1 > /home/dwemer/.update_include_dialectic; fi; sed -n '1p' /home/dwemer/.update_include_herika; sed -n '1p' /home/dwemer/.update_include_stobe; sed -n '1p' /home/dwemer/.update_include_dialectic" },
            cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        if (!result.Succeeded)
        {
            RunOnUi(() =>
            {
                IncludeHerikaServerUpdate = true;
                IncludeStobeServerUpdate = true;
                IncludeDialecticServerUpdate = true;
            });
            return;
        }

        var lines = result.StandardOutput.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        RunOnUi(() =>
        {
            IncludeHerikaServerUpdate = lines.ElementAtOrDefault(0) != "0";
            IncludeStobeServerUpdate = lines.ElementAtOrDefault(1) != "0";
            IncludeDialecticServerUpdate = lines.ElementAtOrDefault(2) != "0";
        });
    }

    private async Task SaveUpdateIncludeSettingsAsync()
    {
        var herika = IncludeHerikaServerUpdate ? "1" : "0";
        var stobe = IncludeStobeServerUpdate ? "1" : "0";
        var dialectic = IncludeDialecticServerUpdate ? "1" : "0";
        var result = await _wsl.RunDistroAsUserAsync(
            "root",
            new[] { "bash", "-lc", $"echo {herika} > /home/dwemer/.update_include_herika && echo {stobe} > /home/dwemer/.update_include_stobe && echo {dialectic} > /home/dwemer/.update_include_dialectic" })
            .ConfigureAwait(false);

        if (!result.Succeeded)
        {
            AppendLog($"Failed to save update include settings: {result.StandardError}{Environment.NewLine}", "red");
        }
    }

    private async Task FixWslDnsAsync()
    {
        if (MessageBox.Show(
                "This will update WSL DNS settings, restart WSL, and test github.com resolution.\n\nContinue?",
                "Fix WSL DNS",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            AppendLog("WSL DNS repair canceled." + Environment.NewLine);
            return;
        }

        AppendLog("Starting WSL DNS repair..." + Environment.NewLine);
        var dnsFixCommand =
            "echo 'dwemer' | sudo -S sh -c 'printf \"[network]\\ngenerateResolvConf = false\\n\" > /etc/wsl.conf' && " +
            "echo 'dwemer' | sudo -S rm -f /etc/resolv.conf && " +
            "echo 'dwemer' | sudo -S sh -c 'printf \"nameserver 1.1.1.1\\nnameserver 8.8.8.8\\n\" > /etc/resolv.conf' && " +
            "echo 'dwemer' | sudo -S chmod 644 /etc/resolv.conf && echo 'DNS_FIX_APPLIED'";

        var result = await _wsl.RunBashAsync(dnsFixCommand, text => AppendLog(text)).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            AppendLog("WSL DNS repair failed." + Environment.NewLine, "red");
            return;
        }

        AppendLog("Restarting WSL to apply DNS settings..." + Environment.NewLine);
        await _wsl.RunWslAsync(new[] { "--shutdown" }).ConfigureAwait(false);
        var verify = await _wsl.RunBashAsync("getent hosts github.com | head -n 1").ConfigureAwait(false);
        if (verify.Succeeded && !string.IsNullOrWhiteSpace(verify.StandardOutput))
        {
            AppendLog("WSL DNS repair completed successfully." + Environment.NewLine, "green");
            AppendLog($"github.com resolves to: {verify.StandardOutput.Trim()}{Environment.NewLine}", "green");
        }
        else
        {
            AppendLog("WSL DNS settings updated, but github.com still does not resolve." + Environment.NewLine, "yellow");
        }
    }

    private async Task RunDistroDoctorAsync()
    {
        if (!await _wsl.DistroExistsAsync().ConfigureAwait(true))
        {
            MessageBox.Show(
                $"{LauncherConstants.DistroName} is not currently installed.",
                "Distro Doctor",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        var choice = MessageBox.Show(
            "Distro Doctor checks common DwemerDistro runtime issues: permissions, Apache, PostgreSQL, required tools, CHIM Background Life service, service ports, disk space, and recent server logs.\n\nChoose Yes to check and repair safe issues.\nChoose No to check only.\nChoose Cancel to do nothing.",
            "Distro Doctor",
            MessageBoxButton.YesNoCancel,
            MessageBoxImage.Question);

        if (choice == MessageBoxResult.Cancel)
        {
            AppendLog("Distro Doctor canceled." + Environment.NewLine);
            return;
        }

        var doctorMode = choice == MessageBoxResult.Yes ? "--repair" : "--check";
        var actionLabel = choice == MessageBoxResult.Yes ? "check and repair" : "check only";

        AppendLog($"Starting Distro Doctor ({actionLabel})..." + Environment.NewLine);
        var command =
            "if [ -x /usr/local/bin/ddistro_doctor ]; then " +
            $"/usr/local/bin/ddistro_doctor {doctorMode}; " +
            "elif [ -f /home/dwemer/dwemerdistro/bin/ddistro_doctor ]; then " +
            $"bash /home/dwemer/dwemerdistro/bin/ddistro_doctor {doctorMode}; " +
            "else " +
            "echo 'ddistro_doctor is not installed. Falling back to permission helper only.'; " +
            "if [ -x /usr/local/bin/fix_ddistro_permissions ]; then " +
            $"/usr/local/bin/fix_ddistro_permissions {doctorMode}; " +
            "elif [ -f /home/dwemer/dwemerdistro/bin/fix_ddistro_permissions ]; then " +
            $"bash /home/dwemer/dwemerdistro/bin/fix_ddistro_permissions {doctorMode}; " +
            "else echo 'Neither ddistro_doctor nor fix_ddistro_permissions is installed. Run Update System first.'; exit 127; fi; " +
            "fi";

        var result = await _wsl.RunBashAsync(command, text => AppendLog(text), user: "root", loginShell: false, lineBuffered: true).ConfigureAwait(true);
        if (result.Succeeded)
        {
            AppendLog("Distro Doctor completed successfully." + Environment.NewLine, "green");
            MessageBox.Show(
                "Distro Doctor completed successfully. Review the launcher log for warnings and details.",
                "Distro Doctor",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        var error = GetCommandError(result);
        AppendLog($"Distro Doctor failed: {error}{Environment.NewLine}", "red");
        MessageBox.Show(
            $"Distro Doctor failed.\n\n{error}",
            "Distro Doctor",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
    }

    private async Task CleanLogsAsync()
    {
        var command =
            "for file in " +
            "/var/log/apache2/error.log " +
            "/var/log/apache2/other_vhosts_access.log " +
            "/var/www/html/HerikaServer/log/debugStream.log " +
            "/var/www/html/HerikaServer/log/context_sent_to_llm.log " +
            "/var/www/html/HerikaServer/log/output_from_llm.log " +
            "/var/www/html/HerikaServer/log/output_to_plugin.log " +
            "/var/www/html/HerikaServer/log/minai.log " +
            "/var/www/html/HerikaServer/log/chim.log " +
            "/var/www/html/HerikaServer/log/vision.log; do " +
            "if [ -f \"$file\" ]; then mv \"$file\" \"${file}.bak\"; fi; done; echo LOGS_CLEANED";
        var result = await _wsl.RunBashAsync(command, text => AppendLog(text)).ConfigureAwait(false);
        AppendLog(result.Succeeded ? "Logs cleaned." + Environment.NewLine : "Failed to clean logs." + Environment.NewLine, result.Succeeded ? "green" : "red");
    }

    private async Task GenerateDiagnosticsAsync()
    {
        var confirmed = MessageBox.Show(
            "The diagnostic file will include recent launcher output, service logs, LLM request/response logs, and local game plugin logs when available.\n\nContinue?",
            "Create Diagnostic File",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (confirmed != MessageBoxResult.Yes)
        {
            AppendLog("Diagnostic file creation canceled." + Environment.NewLine);
            return;
        }

        AppendLog("Generating diagnostic summary..." + Environment.NewLine);
        var lines = new List<string>
        {
            "DwemerDistro WPF Launcher Diagnostic Summary",
            $"Launcher Version: {LauncherConstants.LauncherVersion}",
            $"Generated: {DateTimeOffset.Now}",
            ""
        };

        var diagnosticCommands = new (string Display, Func<Task<CommandResult>> Run)[]
        {
            ("wsl -l -v", () => _wsl.RunWslAsync(new[] { "-l", "-v" })),
            ($"wsl -d {LauncherConstants.DistroName} -u {LauncherConstants.DistroUser} -- bash -lc \"cd /var/www/html/HerikaServer && git status --short --branch\"",
                () => _wsl.RunBashAsync("cd /var/www/html/HerikaServer && git status --short --branch")),
            ($"wsl -d {LauncherConstants.DistroName} -u {LauncherConstants.DistroUser} -- bash -lc \"cd /var/www/html/StobeServer && git status --short --branch\"",
                () => _wsl.RunBashAsync("cd /var/www/html/StobeServer && git status --short --branch")),
            ($"wsl -d {LauncherConstants.DistroName} -u {LauncherConstants.DistroUser} -- bash -lc \"cd /var/www/html/DialecticServer && git status --short --branch\"",
                () => _wsl.RunBashAsync("cd /var/www/html/DialecticServer && git status --short --branch"))
        };

        foreach (var (display, run) in diagnosticCommands)
        {
            lines.Add("$ " + display);
            try
            {
                var result = await run().ConfigureAwait(false);
                lines.Add(result.StandardOutput);
                if (!string.IsNullOrWhiteSpace(result.StandardError))
                {
                    lines.Add(result.StandardError);
                }
            }
            catch (Exception ex)
            {
                lines.Add(ex.ToString());
            }
            lines.Add("");
        }

        await AddPermissionDiagnosticsAsync(lines).ConfigureAwait(false);
        await AddLogDiagnosticsAsync(lines).ConfigureAwait(false);
        await AddDatabaseSchemaDiagnosticsAsync(lines).ConfigureAwait(false);
        await AddConnectorDiagnosticsAsync(lines).ConfigureAwait(false);

        var outputDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "DwemerDistro-Diagnostics");
        Directory.CreateDirectory(outputDir);
        var outputPath = Path.Combine(outputDir, $"diagnostics-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
        await File.WriteAllLinesAsync(outputPath, lines).ConfigureAwait(false);
        AppendLog($"Diagnostic file created: {outputPath}{Environment.NewLine}", "green");
        OpenFolder(outputDir);
    }

    private async Task AddPermissionDiagnosticsAsync(List<string> lines)
    {
        lines.Add("Distro Doctor Diagnostics");
        lines.Add("Read-only checks for common DwemerDistro runtime, service, port, permission, and log issues.");
        lines.Add("");
        lines.Add("$ ddistro_doctor --check");

        var command =
            "if [ -x /usr/local/bin/ddistro_doctor ]; then " +
            "/usr/local/bin/ddistro_doctor --check; " +
            "elif [ -f /home/dwemer/dwemerdistro/bin/ddistro_doctor ]; then " +
            "bash /home/dwemer/dwemerdistro/bin/ddistro_doctor --check; " +
            "elif [ -x /usr/local/bin/fix_ddistro_permissions ]; then " +
            "echo '[missing] ddistro_doctor is not installed. Falling back to permission checks only.'; " +
            "/usr/local/bin/fix_ddistro_permissions --check; " +
            "elif [ -f /home/dwemer/dwemerdistro/bin/fix_ddistro_permissions ]; then " +
            "echo '[missing] ddistro_doctor is not installed. Falling back to permission checks only.'; " +
            "bash /home/dwemer/dwemerdistro/bin/fix_ddistro_permissions --check; " +
            "else echo '[missing] ddistro_doctor and fix_ddistro_permissions are not installed.'; exit 127; fi";

        try
        {
            var result = await _wsl.RunBashAsync(command, user: "root", loginShell: false).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(result.StandardOutput))
            {
                lines.Add(SanitizeDiagnosticText(result.StandardOutput.TrimEnd()));
            }

            if (!string.IsNullOrWhiteSpace(result.StandardError))
            {
                lines.Add("[stderr]");
                lines.Add(SanitizeDiagnosticText(result.StandardError.TrimEnd()));
            }

            if (!result.Succeeded)
            {
                lines.Add($"[exit code {result.ExitCode}]");
            }
        }
        catch (Exception ex)
        {
            lines.Add(ex.ToString());
        }

        lines.Add("");
    }

    private async Task AddLogDiagnosticsAsync(List<string> lines)
    {
        const int maxLogLines = 3000;

        lines.Add("Log Diagnostics");
        lines.Add($"Each log section contains up to the last {maxLogLines} lines.");
        lines.Add("Missing or unreadable logs are noted inline instead of failing diagnostic creation.");
        lines.Add("");

        AddLauncherSessionOutputDiagnostics(lines, maxLogLines);
        await AddWslLogDiagnosticsAsync(lines, maxLogLines).ConfigureAwait(false);
        AddLocalGameLogDiagnostics(lines, maxLogLines);
    }

    private void AddLauncherSessionOutputDiagnostics(List<string> lines, int maxLogLines)
    {
        lines.Add($"--- Start of Launcher Session Output (last {maxLogLines} lines) ---");
        if (string.IsNullOrWhiteSpace(OutputText))
        {
            lines.Add("[empty]");
        }
        else
        {
            lines.Add(SanitizeDiagnosticText(TakeLastLines(OutputText, maxLogLines)));
        }

        lines.Add("--- End of Launcher Session Output ---");
        lines.Add("");
    }

    private async Task AddWslLogDiagnosticsAsync(List<string> lines, int maxLogLines)
    {
        var logFiles = new (string Name, string Path)[]
        {
            ("HerikaServer output_from_llm", "/var/www/html/HerikaServer/log/output_from_llm.log"),
            ("HerikaServer chim", "/var/www/html/HerikaServer/log/chim.log"),
            ("HerikaServer output_to_plugin", "/var/www/html/HerikaServer/log/output_to_plugin.log"),
            ("HerikaServer context_sent_to_llm", "/var/www/html/HerikaServer/log/context_sent_to_llm.log"),
            ("HerikaServer debugStream", "/var/www/html/HerikaServer/log/debugStream.log"),
            ("HerikaServer minai", "/var/www/html/HerikaServer/log/minai.log"),
            ("HerikaServer vision", "/var/www/html/HerikaServer/log/vision.log"),
            ("StobeServer stobe", "/var/www/html/StobeServer/log/stobe.log"),
            ("StobeServer stobeserver", "/var/www/html/StobeServer/log/stobeserver.log"),
            ("StobeServer stobe_import", "/var/www/html/StobeServer/log/stobe_import.log"),
            ("StobeServer output_from_llm", "/var/www/html/StobeServer/log/output_from_llm.log"),
            ("StobeServer context_sent_to_llm", "/var/www/html/StobeServer/log/context_sent_to_llm.log"),
            ("DialecticServer dialectic", "/var/www/html/DialecticServer/log/dialectic.log"),
            ("DialecticServer service", "/var/www/html/DialecticServer/log/service.log"),
            ("DialecticServer manager", "/var/www/html/DialecticServer/log/manager.log"),
            ("DialecticServer output_from_llm", "/var/www/html/DialecticServer/log/output_from_llm.log"),
            ("DialecticServer context_sent_to_llm", "/var/www/html/DialecticServer/log/context_sent_to_llm.log"),
            ("Apache error", "/var/log/apache2/error.log"),
            ("Apache vhost access", "/var/log/apache2/other_vhosts_access.log"),
            ("Dwemer Distro XTTS", "/home/dwemer/xtts-api-server/log.txt"),
            ("Chatterbox", "/home/dwemer/chatterbox/log.txt"),
            ("Pocket-TTS", "/home/dwemer/pocket-tts/log.txt"),
            ("OmniVoice", "/home/dwemer/omnivoice-tts/logs/server.log"),
            ("Minime and TXT2VEC", "/home/dwemer/minime-t5/log.txt"),
            ("MeloTTS", "/home/dwemer/MeloTTS/melo/log.txt"),
            ("Piper", "/home/dwemer/piper/log.txt"),
            ("Mimic3", "/home/dwemer/mimic3/log.txt"),
            ("LocalWhisper", "/home/dwemer/remote-faster-whisper/log.txt"),
            ("Parakeet", "/home/dwemer/parakeet-api-server/log.txt")
        };

        foreach (var (name, path) in logFiles)
        {
            lines.Add($"--- Start of {name} ({path}) ---");
            var escapedPath = EscapeForSingleQuotedBash(path);
            var command =
                $"if [ -f {escapedPath} ]; then tail -n {maxLogLines} {escapedPath}; else echo '[missing] {path}'; fi";

            try
            {
                var result = await _wsl.RunBashAsync(command, user: "root", loginShell: false).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(result.StandardOutput))
                {
                    lines.Add(SanitizeDiagnosticText(result.StandardOutput.TrimEnd()));
                }

                if (!string.IsNullOrWhiteSpace(result.StandardError))
                {
                    lines.Add("[stderr]");
                    lines.Add(SanitizeDiagnosticText(result.StandardError.TrimEnd()));
                }

                if (!result.Succeeded)
                {
                    lines.Add($"[exit code {result.ExitCode}]");
                }
            }
            catch (Exception ex)
            {
                lines.Add(ex.ToString());
            }

            lines.Add($"--- End of {name} ---");
            lines.Add("");
        }

        var probes = new (string Name, string Command)[]
        {
            (
                "Chatterbox voice directories",
                "echo '/home/dwemer/chatterbox/voices'; " +
                "ls -la /home/dwemer/chatterbox/voices 2>&1 || true; " +
                "echo; echo '/var/www/html/HerikaServer/data/voices'; " +
                "ls -la /var/www/html/HerikaServer/data/voices 2>&1 || true"
            ),
            (
                "Chatterbox API voice inventory",
                "if command -v curl >/dev/null 2>&1; then " +
                "echo 'GET /health'; curl -sS --max-time 5 http://127.0.0.1:8020/health 2>&1 || true; " +
                "echo; echo 'GET /speakers_list_extended'; curl -sS --max-time 5 http://127.0.0.1:8020/speakers_list_extended 2>&1 || true; " +
                "else echo '[missing] curl'; fi"
            ),
            (
                "OmniVoice component status",
                "echo '/home/dwemer/omnivoice-tts'; " +
                "ls -la /home/dwemer/omnivoice-tts 2>&1 || true; " +
                "echo; echo 'voices'; find /home/dwemer/omnivoice-tts/voices -maxdepth 2 -type f 2>/dev/null | head -100 || true; " +
                "echo; echo 'doctor'; if [ -x /home/dwemer/omnivoice-tts/venv/bin/python ]; then cd /home/dwemer/omnivoice-tts && venv/bin/python omnivoice_cli.py doctor 2>&1 || true; else echo '[missing] OmniVoice venv'; fi"
            ),
            (
                "OmniVoice API inventory",
                "if command -v curl >/dev/null 2>&1; then " +
                "echo 'GET /health'; curl -sS --max-time 5 http://127.0.0.1:8021/health 2>&1 || true; " +
                "echo; echo 'GET /active_language'; curl -sS --max-time 5 http://127.0.0.1:8021/active_language 2>&1 || true; " +
                "echo; echo 'GET /speakers_list_extended'; curl -sS --max-time 5 http://127.0.0.1:8021/speakers_list_extended 2>&1 || true; " +
                "else echo '[missing] curl'; fi"
            )
        };

        foreach (var (name, command) in probes)
        {
            lines.Add($"--- Start of {name} ---");

            try
            {
                var result = await _wsl.RunBashAsync(command, user: "root", loginShell: false).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(result.StandardOutput))
                {
                    lines.Add(SanitizeDiagnosticText(result.StandardOutput.TrimEnd()));
                }

                if (!string.IsNullOrWhiteSpace(result.StandardError))
                {
                    lines.Add("[stderr]");
                    lines.Add(SanitizeDiagnosticText(result.StandardError.TrimEnd()));
                }

                if (!result.Succeeded)
                {
                    lines.Add($"[exit code {result.ExitCode}]");
                }
            }
            catch (Exception ex)
            {
                lines.Add(ex.ToString());
            }

            lines.Add($"--- End of {name} ---");
            lines.Add("");
        }
    }

    private static void AddLocalGameLogDiagnostics(List<string> lines, int maxLogLines)
    {
        var localLogGroups = new List<(string Name, string[] Paths)>
        {
            ("AIAgent.log",
            [
                @"%USERPROFILE%\Documents\My Games\Skyrim Special Edition\SKSE\AIAgent.log",
                @"%USERPROFILE%\Documents\My Games\Skyrim\SKSE\AIAgent.log",
                @"%USERPROFILE%\Documents\My Games\Skyrim.INI\SKSE\AIAgent.log",
                @"%USERPROFILE%\Documents\My Games\Skyrim Special Edition\SKSE\Plugins\AIAgent.log",
                @"%USERPROFILE%\Documents\My Games\Skyrim\SKSE\Plugins\AIAgent.log",
                @"%USERPROFILE%\Documents\My Games\Skyrim.INI\SKSE\Plugins\AIAgent.log"
            ]),
            ("Papyrus.0.log",
            [
                @"%USERPROFILE%\Documents\My Games\Skyrim Special Edition\Logs\Script\Papyrus.0.log"
            ]),
            ("STOBE Mod Log",
                BuildStobeModLogCandidates())
        };

        foreach (var (name, paths) in localLogGroups)
        {
            var selectedTemplate = paths.FirstOrDefault(path => File.Exists(Environment.ExpandEnvironmentVariables(path)));
            if (selectedTemplate is null)
            {
                lines.Add($"--- Start of {name} ---");
                lines.Add("[missing]");
                lines.Add("Attempted paths:");
                lines.AddRange(paths);
                lines.Add($"--- End of {name} ---");
                lines.Add("");
                continue;
            }

            var selectedPath = Environment.ExpandEnvironmentVariables(selectedTemplate);
            lines.Add($"--- Start of {selectedTemplate} ---");
            lines.Add($"# Resolved path: {selectedPath}");
            try
            {
                lines.Add(SanitizeDiagnosticText(ReadTextFileTail(selectedPath, maxLogLines)));
            }
            catch (Exception ex)
            {
                lines.Add(ex.ToString());
            }

            lines.Add($"--- End of {selectedTemplate} ---");
            lines.Add("");
        }
    }

    private static string[] BuildStobeModLogCandidates()
    {
        var candidates = new List<string>
        {
            @"%ProgramFiles(x86)%\Steam\steamapps\common\Kenshi\mods\Stobe\Stobe.log",
            @"%ProgramFiles%\Steam\steamapps\common\Kenshi\mods\Stobe\Stobe.log",
            Path.GetFullPath(@"Kenshi\mods\Stobe\Stobe.log")
        };

        foreach (var steamLibrary in GetSteamLibraryPaths())
        {
            candidates.Add(Path.Combine(steamLibrary, "steamapps", "common", "Kenshi", "mods", "Stobe", "Stobe.log"));
        }

        foreach (var drive in DriveInfo.GetDrives().Where(drive => drive.DriveType == DriveType.Fixed))
        {
            var root = drive.RootDirectory.FullName;
            candidates.Add(Path.Combine(root, "SteamLibrary", "steamapps", "common", "Kenshi", "mods", "Stobe", "Stobe.log"));
            candidates.Add(Path.Combine(root, "Games", "SteamLibrary", "steamapps", "common", "Kenshi", "mods", "Stobe", "Stobe.log"));
            candidates.Add(Path.Combine(root, "Program Files (x86)", "Steam", "steamapps", "common", "Kenshi", "mods", "Stobe", "Stobe.log"));
        }

        return candidates
            .Select(Environment.ExpandEnvironmentVariables)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static IEnumerable<string> GetSteamLibraryPaths()
    {
        foreach (var steamRoot in new[]
                 {
                     Environment.ExpandEnvironmentVariables(@"%ProgramFiles(x86)%\Steam"),
                     Environment.ExpandEnvironmentVariables(@"%ProgramFiles%\Steam")
                 })
        {
            if (string.IsNullOrWhiteSpace(steamRoot))
            {
                continue;
            }

            if (Directory.Exists(steamRoot))
            {
                yield return steamRoot;
            }

            var libraryFoldersPath = Path.Combine(steamRoot, "steamapps", "libraryfolders.vdf");
            if (!File.Exists(libraryFoldersPath))
            {
                continue;
            }

            string[] lines;
            try
            {
                lines = File.ReadAllLines(libraryFoldersPath);
            }
            catch
            {
                continue;
            }

            foreach (var line in lines)
            {
                var match = Regex.Match(line, "\"path\"\\s+\"(?<path>[^\"]+)\"");
                if (match.Success)
                {
                    yield return match.Groups["path"].Value.Replace(@"\\", @"\");
                }
            }
        }
    }

    private static string ReadTextFileTail(string path, int maxLines)
    {
        var tail = new Queue<string>();
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);

        while (reader.ReadLine() is { } line)
        {
            tail.Enqueue(line);
            if (tail.Count > maxLines)
            {
                tail.Dequeue();
            }
        }

        return string.Join(Environment.NewLine, tail);
    }

    private static string TakeLastLines(string text, int maxLines)
    {
        var tail = new Queue<string>();
        foreach (var line in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            tail.Enqueue(line);
            if (tail.Count > maxLines)
            {
                tail.Dequeue();
            }
        }

        return string.Join(Environment.NewLine, tail);
    }

    private static string SanitizeDiagnosticText(string text)
    {
        return Regex.Replace(text ?? string.Empty, @"hf_[A-Za-z0-9_-]{20,}", "hf_[redacted]");
    }

    private async Task AddDatabaseSchemaDiagnosticsAsync(List<string> lines)
    {
        lines.Add("Database Schema Diagnostics");
        lines.Add("These checks are read-only and cover HerikaServer, StobeServer, and DialecticServer database state.");
        lines.Add("");

        var command = """
set +e
export PGPASSWORD=dwemer
echo "== PostgreSQL connection =="
psql -h localhost -U dwemer -d dwemer -X -v ON_ERROR_STOP=1 -P pager=off -c "SELECT current_database() AS database, current_user AS user, version() AS postgres_version;"
echo
echo "== Installed extensions =="
psql -h localhost -U dwemer -d dwemer -X -v ON_ERROR_STOP=1 -P pager=off -c "SELECT extname, extversion FROM pg_extension ORDER BY extname;"
echo
echo "== database_versioning live rows =="
psql -h localhost -U dwemer -d dwemer -X -v ON_ERROR_STOP=1 -P pager=off -c "SELECT tablename, version FROM public.database_versioning ORDER BY tablename;"
echo
echo "== public tables =="
psql -h localhost -U dwemer -d dwemer -X -v ON_ERROR_STOP=1 -P pager=off -c "SELECT table_name FROM information_schema.tables WHERE table_schema = 'public' AND table_type = 'BASE TABLE' ORDER BY table_name;"
echo
echo "== public columns =="
psql -h localhost -U dwemer -d dwemer -X -v ON_ERROR_STOP=1 -P pager=off -c "SELECT table_name, ordinal_position, column_name, data_type, is_nullable, column_default FROM information_schema.columns WHERE table_schema = 'public' ORDER BY table_name, ordinal_position;"
echo
echo "== public constraints =="
psql -h localhost -U dwemer -d dwemer -X -v ON_ERROR_STOP=1 -P pager=off -c "SELECT conrelid::regclass::text AS table_name, conname, contype, pg_get_constraintdef(oid) AS definition FROM pg_constraint WHERE connamespace = 'public'::regnamespace ORDER BY table_name, conname;"
echo
echo "== public indexes =="
psql -h localhost -U dwemer -d dwemer -X -v ON_ERROR_STOP=1 -P pager=off -c "SELECT tablename, indexname, indexdef FROM pg_indexes WHERE schemaname = 'public' ORDER BY tablename, indexname;"
echo
echo "== HerikaServer expected db update versions from debug/db_updates.php =="
if [ -f /var/www/html/HerikaServer/debug/db_updates.php ]; then
  grep -oE 'updateVersion\("[^"]+",[[:space:]]*[0-9]+' /var/www/html/HerikaServer/debug/db_updates.php | sed -E 's/updateVersion\("([^"]+)",[[:space:]]*([0-9]+)/\1|\2/' | sort -t '|' -k1,1 -k2,2nr | sort -t '|' -k1,1 -u
else
  echo "HerikaServer update file missing"
fi
echo
echo "== StobeServer expected db update versions from debug/db_updates.php =="
if [ -f /var/www/html/StobeServer/debug/db_updates.php ]; then
  grep -oE "applyPatch\('[^']+', *[0-9]+" /var/www/html/StobeServer/debug/db_updates.php | sed -E "s/applyPatch\('([^']+)', *([0-9]+)/\1|\2/" | sort -t '|' -k1,1 -k2,2nr | sort -t '|' -k1,1 -u
else
  echo "StobeServer update file missing"
fi
echo
echo "== DialecticServer database state =="
if psql -h localhost -U dwemer -d dialectic -X -At -v ON_ERROR_STOP=1 -P pager=off -c "SELECT 1;" >/dev/null 2>&1; then
  psql -h localhost -U dwemer -d dialectic -X -v ON_ERROR_STOP=1 -P pager=off -c "SELECT tablename, version FROM public.database_versioning ORDER BY tablename;"
  psql -h localhost -U dwemer -d dialectic -X -v ON_ERROR_STOP=1 -P pager=off -c "SELECT table_schema, COUNT(*) AS table_count FROM information_schema.tables WHERE table_schema IN ('public', 'dialectic_meta') AND table_type = 'BASE TABLE' GROUP BY table_schema ORDER BY table_schema;"
else
  echo "Dialectic database unavailable"
fi
echo
echo "== Common Oghma integrity checks =="
psql -h localhost -U dwemer -d dwemer -X -v ON_ERROR_STOP=1 -P pager=off -c "SELECT EXISTS (SELECT 1 FROM information_schema.tables WHERE table_schema = 'public' AND table_name = 'oghma') AS oghma_table_exists;"
if psql -h localhost -U dwemer -d dwemer -X -At -v ON_ERROR_STOP=1 -P pager=off -c "SELECT to_regclass('public.oghma') IS NOT NULL;" | grep -q t; then
  psql -h localhost -U dwemer -d dwemer -X -v ON_ERROR_STOP=1 -P pager=off -c "SELECT c.conname, c.contype, pg_get_constraintdef(c.oid) AS definition FROM pg_constraint c JOIN pg_class t ON t.oid = c.conrelid JOIN pg_namespace n ON n.oid = t.relnamespace WHERE n.nspname = 'public' AND t.relname = 'oghma' ORDER BY c.contype, c.conname;"
  psql -h localhost -U dwemer -d dwemer -X -v ON_ERROR_STOP=1 -P pager=off -c "SELECT COUNT(*) AS rows, COUNT(*) FILTER (WHERE topic IS NULL OR BTRIM(topic::text) = '') AS blank_topics FROM public.oghma;"
  psql -h localhost -U dwemer -d dwemer -X -v ON_ERROR_STOP=1 -P pager=off -c "SELECT COUNT(*) AS duplicate_normalized_topics FROM (SELECT LOWER(BTRIM(topic::text)) AS normalized_topic FROM public.oghma GROUP BY LOWER(BTRIM(topic::text)) HAVING COUNT(*) > 1) dup;"
else
  echo "Oghma table missing; detailed Oghma checks skipped."
fi
""";

        lines.Add("$ wsl database schema diagnostics");
        try
        {
            var result = await _wsl.RunBashAsync(command).ConfigureAwait(false);
            lines.Add(result.StandardOutput);
            if (!string.IsNullOrWhiteSpace(result.StandardError))
            {
                lines.Add(result.StandardError);
            }

            if (!result.Succeeded)
            {
                lines.Add($"Database schema diagnostics exited with code {result.ExitCode}.");
            }
        }
        catch (Exception ex)
        {
            lines.Add(ex.ToString());
        }

        lines.Add("");
    }

    private async Task AddConnectorDiagnosticsAsync(List<string> lines)
    {
        lines.Add("Connector Diagnostics");
        lines.Add("These checks are read-only and show active/profile-linked connector IDs plus non-secret connector fields.");
        lines.Add("Secret-bearing columns such as api_key, metadata, and config are not dumped.");
        lines.Add("");

        var command = """
set +e
export PGPASSWORD=dwemer
echo "== HerikaServer connectors =="
echo "-- Herika LLM profile connectors --"
if psql -h localhost -U dwemer -d dwemer -X -At -v ON_ERROR_STOP=1 -P pager=off -c "SELECT to_regclass('public.core_profiles') IS NOT NULL AND to_regclass('public.core_llm_connector') IS NOT NULL AND NOT EXISTS (SELECT 1 FROM (VALUES ('core_profiles','id'),('core_profiles','label'),('core_profiles','default_npc'),('core_profiles','default_narrator'),('core_profiles','llm_primary_id'),('core_profiles','llm_secondary_id'),('core_profiles','llm_tertiary_id'),('core_profiles','llm_quaternary_id'),('core_profiles','diary_connector_id'),('core_profiles','llm_formatter_id'),('core_profiles','llm_fallback_id'),('core_llm_connector','id'),('core_llm_connector','label'),('core_llm_connector','driver'),('core_llm_connector','provider'),('core_llm_connector','service'),('core_llm_connector','model'),('core_llm_connector','url'),('core_llm_connector','max_tokens'),('core_llm_connector','temperature')) AS required(table_name, column_name) WHERE NOT EXISTS (SELECT 1 FROM information_schema.columns c WHERE c.table_schema = 'public' AND c.table_name = required.table_name AND c.column_name = required.column_name));" | grep -qx t; then
  psql -h localhost -U dwemer -d dwemer -X -v ON_ERROR_STOP=1 -P pager=off -c "WITH profile_scope AS (SELECT p.*, CASE WHEN COALESCE(NULLIF(p.default_npc::text, ''), '0') IN ('1','true','TRUE','t','yes','on') OR COALESCE(NULLIF(p.default_narrator::text, ''), '0') IN ('1','true','TRUE','t','yes','on') THEN 0 ELSE 1 END AS profile_priority FROM public.core_profiles p ORDER BY profile_priority, p.id LIMIT 20), slots AS (SELECT p.id AS profile_id, p.label AS profile_label, p.default_npc, p.default_narrator, p.profile_priority, s.slot_order, s.slot_name, s.connector_id FROM profile_scope p CROSS JOIN LATERAL (VALUES (1,'primary',p.llm_primary_id), (2,'secondary',p.llm_secondary_id), (3,'tertiary',p.llm_tertiary_id), (4,'quaternary',p.llm_quaternary_id), (5,'diary',p.diary_connector_id), (6,'formatter',p.llm_formatter_id), (7,'fallback',p.llm_fallback_id)) AS s(slot_order, slot_name, connector_id)) SELECT s.profile_id, s.profile_label, s.default_npc, s.default_narrator, s.slot_name, s.connector_id, c.label AS connector_label, c.driver, c.provider, c.service, c.model, c.url, c.max_tokens, c.temperature FROM slots s LEFT JOIN public.core_llm_connector c ON c.id = s.connector_id ORDER BY s.profile_priority, s.profile_id, s.slot_order;"
else
  echo "[missing] Herika LLM profile connector columns are not present in this database layout."
fi
echo
echo "-- Herika TTS profile connectors --"
if psql -h localhost -U dwemer -d dwemer -X -At -v ON_ERROR_STOP=1 -P pager=off -c "SELECT to_regclass('public.core_profiles') IS NOT NULL AND to_regclass('public.core_tts_connector') IS NOT NULL AND NOT EXISTS (SELECT 1 FROM (VALUES ('core_profiles','id'),('core_profiles','label'),('core_profiles','default_npc'),('core_profiles','default_narrator'),('core_profiles','tts_connector_id'),('core_tts_connector','id'),('core_tts_connector','label'),('core_tts_connector','driver'),('core_tts_connector','url')) AS required(table_name, column_name) WHERE NOT EXISTS (SELECT 1 FROM information_schema.columns c WHERE c.table_schema = 'public' AND c.table_name = required.table_name AND c.column_name = required.column_name));" | grep -qx t; then
  psql -h localhost -U dwemer -d dwemer -X -v ON_ERROR_STOP=1 -P pager=off -c "WITH profile_scope AS (SELECT p.*, CASE WHEN COALESCE(NULLIF(p.default_npc::text, ''), '0') IN ('1','true','TRUE','t','yes','on') OR COALESCE(NULLIF(p.default_narrator::text, ''), '0') IN ('1','true','TRUE','t','yes','on') THEN 0 ELSE 1 END AS profile_priority FROM public.core_profiles p ORDER BY profile_priority, p.id LIMIT 20) SELECT p.id AS profile_id, p.label AS profile_label, p.default_npc, p.default_narrator, p.tts_connector_id, c.label AS connector_label, c.driver, c.url FROM profile_scope p LEFT JOIN public.core_tts_connector c ON c.id = p.tts_connector_id ORDER BY p.profile_priority, p.id;"
else
  echo "[missing] Herika TTS profile connector columns are not present in this database layout."
fi
echo
echo "-- Herika STT global connector --"
if psql -h localhost -U dwemer -d dwemer -X -At -v ON_ERROR_STOP=1 -P pager=off -c "SELECT to_regclass('public.general_settings') IS NOT NULL AND to_regclass('public.core_stt_connector') IS NOT NULL AND NOT EXISTS (SELECT 1 FROM (VALUES ('general_settings','id'),('general_settings','value'),('core_stt_connector','id'),('core_stt_connector','label'),('core_stt_connector','driver'),('core_stt_connector','url')) AS required(table_name, column_name) WHERE NOT EXISTS (SELECT 1 FROM information_schema.columns c WHERE c.table_schema = 'public' AND c.table_name = required.table_name AND c.column_name = required.column_name));" | grep -qx t; then
  psql -h localhost -U dwemer -d dwemer -X -v ON_ERROR_STOP=1 -P pager=off -c "SELECT expected.id AS setting_id, gs.value AS setting_value, c.id AS connector_id, c.label AS connector_label, c.driver, c.url FROM (SELECT 'GLOBAL_STT_CONNECTOR_ID'::text AS id) expected LEFT JOIN public.general_settings gs ON gs.id = expected.id LEFT JOIN public.core_stt_connector c ON c.id = CASE WHEN gs.value ~ '^[0-9]+$' THEN gs.value::integer ELSE NULL END;"
else
  echo "[missing] Herika STT global connector columns are not present in this database layout."
fi
echo
echo "-- Herika ITT global connector --"
if psql -h localhost -U dwemer -d dwemer -X -At -v ON_ERROR_STOP=1 -P pager=off -c "SELECT to_regclass('public.general_settings') IS NOT NULL AND to_regclass('public.core_itt_connector') IS NOT NULL AND NOT EXISTS (SELECT 1 FROM (VALUES ('general_settings','id'),('general_settings','value'),('core_itt_connector','id'),('core_itt_connector','label'),('core_itt_connector','driver'),('core_itt_connector','url')) AS required(table_name, column_name) WHERE NOT EXISTS (SELECT 1 FROM information_schema.columns c WHERE c.table_schema = 'public' AND c.table_name = required.table_name AND c.column_name = required.column_name));" | grep -qx t; then
  psql -h localhost -U dwemer -d dwemer -X -v ON_ERROR_STOP=1 -P pager=off -c "SELECT expected.id AS setting_id, gs.value AS setting_value, c.id AS connector_id, c.label AS connector_label, c.driver, c.url FROM (SELECT 'GLOBAL_ITT_CONNECTOR_ID'::text AS id) expected LEFT JOIN public.general_settings gs ON gs.id = expected.id LEFT JOIN public.core_itt_connector c ON c.id = CASE WHEN gs.value ~ '^[0-9]+$' THEN gs.value::integer ELSE NULL END;"
else
  echo "[missing] Herika ITT global connector columns are not present in this database layout."
fi
echo
echo "-- Herika ITT profile connectors --"
if psql -h localhost -U dwemer -d dwemer -X -At -v ON_ERROR_STOP=1 -P pager=off -c "SELECT to_regclass('public.core_profiles') IS NOT NULL AND to_regclass('public.core_itt_connector') IS NOT NULL AND NOT EXISTS (SELECT 1 FROM (VALUES ('core_profiles','id'),('core_profiles','label'),('core_profiles','default_npc'),('core_profiles','default_narrator'),('core_profiles','itt_connector_id'),('core_itt_connector','id'),('core_itt_connector','label'),('core_itt_connector','driver'),('core_itt_connector','url')) AS required(table_name, column_name) WHERE NOT EXISTS (SELECT 1 FROM information_schema.columns c WHERE c.table_schema = 'public' AND c.table_name = required.table_name AND c.column_name = required.column_name));" | grep -qx t; then
  psql -h localhost -U dwemer -d dwemer -X -v ON_ERROR_STOP=1 -P pager=off -c "WITH profile_scope AS (SELECT p.*, CASE WHEN COALESCE(NULLIF(p.default_npc::text, ''), '0') IN ('1','true','TRUE','t','yes','on') OR COALESCE(NULLIF(p.default_narrator::text, ''), '0') IN ('1','true','TRUE','t','yes','on') THEN 0 ELSE 1 END AS profile_priority FROM public.core_profiles p ORDER BY profile_priority, p.id LIMIT 20) SELECT p.id AS profile_id, p.label AS profile_label, p.default_npc, p.default_narrator, p.itt_connector_id, c.label AS connector_label, c.driver, c.url FROM profile_scope p LEFT JOIN public.core_itt_connector c ON c.id = p.itt_connector_id ORDER BY p.profile_priority, p.id;"
else
  echo "[missing] Herika ITT profile connector columns are not present in this database layout."
fi
echo
echo "== StobeServer connectors =="
echo "-- Stobe database connection --"
psql -h localhost -U dwemer -d stobe -X -v ON_ERROR_STOP=1 -P pager=off -c "SELECT current_database() AS database, current_user AS user;"
echo
echo "-- Stobe LLM profile connectors --"
if psql -h localhost -U dwemer -d stobe -X -At -v ON_ERROR_STOP=1 -P pager=off -c "SELECT to_regclass('public.core_profiles') IS NOT NULL AND to_regclass('public.core_llm_connector') IS NOT NULL AND NOT EXISTS (SELECT 1 FROM (VALUES ('core_profiles','id'),('core_profiles','label'),('core_profiles','is_default_npc'),('core_profiles','is_player_faction_profile'),('core_profiles','response_connector'),('core_profiles','diary_connector'),('core_profiles','autochat_connector'),('core_profiles','middleterm_connector'),('core_profiles','backgroundlife_connector'),('core_profiles','dynamic_connector'),('core_profiles','relationship_connector'),('core_llm_connector','id'),('core_llm_connector','name'),('core_llm_connector','connector_type'),('core_llm_connector','model'),('core_llm_connector','base_url'),('core_llm_connector','max_tokens'),('core_llm_connector','temperature'),('core_llm_connector','is_default')) AS required(table_name, column_name) WHERE NOT EXISTS (SELECT 1 FROM information_schema.columns c WHERE c.table_schema = 'public' AND c.table_name = required.table_name AND c.column_name = required.column_name));" | grep -qx t; then
  psql -h localhost -U dwemer -d stobe -X -v ON_ERROR_STOP=1 -P pager=off -c "WITH profile_scope AS (SELECT p.*, CASE WHEN COALESCE(p.is_default_npc::text, 'false') IN ('true','t','1') OR COALESCE(p.is_player_faction_profile::text, 'false') IN ('true','t','1') THEN 0 ELSE 1 END AS profile_priority FROM public.core_profiles p ORDER BY profile_priority, p.id LIMIT 20), slots AS (SELECT p.id AS profile_id, p.label AS profile_label, p.is_default_npc, p.is_player_faction_profile, p.profile_priority, s.slot_order, s.slot_name, s.connector_id FROM profile_scope p CROSS JOIN LATERAL (VALUES (1,'response',p.response_connector), (2,'diary',p.diary_connector), (3,'autochat',p.autochat_connector), (4,'middleterm',p.middleterm_connector), (5,'backgroundlife',p.backgroundlife_connector), (6,'dynamic',p.dynamic_connector), (7,'relationship',p.relationship_connector)) AS s(slot_order, slot_name, connector_id)) SELECT s.profile_id, s.profile_label, s.is_default_npc, s.is_player_faction_profile, s.slot_name, s.connector_id, c.name AS connector_name, c.connector_type, c.model, c.base_url, c.max_tokens, c.temperature, c.is_default AS connector_is_default FROM slots s LEFT JOIN public.core_llm_connector c ON c.id = s.connector_id ORDER BY s.profile_priority, s.profile_id, s.slot_order;"
else
  echo "[missing] Stobe LLM profile connector columns are not present in this database layout."
fi
echo
echo "-- Stobe TTS profile connectors --"
if psql -h localhost -U dwemer -d stobe -X -At -v ON_ERROR_STOP=1 -P pager=off -c "SELECT to_regclass('public.core_profiles') IS NOT NULL AND to_regclass('public.core_tts_connector') IS NOT NULL AND NOT EXISTS (SELECT 1 FROM (VALUES ('core_profiles','id'),('core_profiles','label'),('core_profiles','is_default_npc'),('core_profiles','is_player_faction_profile'),('core_profiles','tts_connector_id'),('core_tts_connector','id'),('core_tts_connector','name'),('core_tts_connector','connector_type'),('core_tts_connector','base_url'),('core_tts_connector','is_default')) AS required(table_name, column_name) WHERE NOT EXISTS (SELECT 1 FROM information_schema.columns c WHERE c.table_schema = 'public' AND c.table_name = required.table_name AND c.column_name = required.column_name));" | grep -qx t; then
  psql -h localhost -U dwemer -d stobe -X -v ON_ERROR_STOP=1 -P pager=off -c "WITH profile_scope AS (SELECT p.*, CASE WHEN COALESCE(p.is_default_npc::text, 'false') IN ('true','t','1') OR COALESCE(p.is_player_faction_profile::text, 'false') IN ('true','t','1') THEN 0 ELSE 1 END AS profile_priority FROM public.core_profiles p ORDER BY profile_priority, p.id LIMIT 20) SELECT p.id AS profile_id, p.label AS profile_label, p.is_default_npc, p.is_player_faction_profile, p.tts_connector_id, c.name AS connector_name, c.connector_type, c.base_url, c.is_default AS connector_is_default FROM profile_scope p LEFT JOIN public.core_tts_connector c ON c.id = p.tts_connector_id ORDER BY p.profile_priority, p.id;"
else
  echo "[missing] Stobe TTS profile connector columns are not present in this database layout."
fi
echo
echo "-- Stobe STT connector --"
if psql -h localhost -U dwemer -d stobe -X -At -v ON_ERROR_STOP=1 -P pager=off -c "SELECT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = 'public' AND table_name = 'core_profiles' AND column_name = 'stt_connector_id');" | grep -qx t; then
  echo "[present] Stobe profile STT connector column exists, but this launcher does not yet know the matching connector table shape."
else
  echo "[not configured] StobeServer schema has no profile STT connector column."
fi
echo
echo "-- Stobe ITT connector --"
if psql -h localhost -U dwemer -d stobe -X -At -v ON_ERROR_STOP=1 -P pager=off -c "SELECT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = 'public' AND table_name = 'core_profiles' AND column_name = 'itt_connector_id');" | grep -qx t; then
  echo "[present] Stobe profile ITT connector column exists, but this launcher does not yet know the matching connector table shape."
else
  echo "[not configured] StobeServer schema has no profile ITT connector column."
fi
""";

        lines.Add("$ wsl connector diagnostics");
        try
        {
            var result = await _wsl.RunBashAsync(command).ConfigureAwait(false);
            lines.Add(SanitizeDiagnosticText(result.StandardOutput));
            if (!string.IsNullOrWhiteSpace(result.StandardError))
            {
                lines.Add(SanitizeDiagnosticText(result.StandardError));
            }

            if (!result.Succeeded)
            {
                lines.Add($"Connector diagnostics exited with code {result.ExitCode}.");
            }
        }
        catch (Exception ex)
        {
            lines.Add(ex.ToString());
        }

        lines.Add("");
    }

    private async Task ReclaimDistroDiskSpaceAsync()
    {
        if (!await _wsl.DistroExistsAsync().ConfigureAwait(false))
        {
            MessageBox.Show(
                $"{LauncherConstants.DistroName} is not currently installed.",
                "Reclaim Distro Disk Space",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        var confirmed = MessageBox.Show(
            $"This will run fstrim inside {LauncherConstants.DistroName}, request a clean server stop, flush filesystem buffers, shut down WSL, and attempt to compact ext4.vhdx using an elevated Windows prompt.\n\n" +
            $"Close any open \\\\wsl.localhost\\{LauncherConstants.DistroName} Explorer windows first.\n\n" +
            "This will also stop any other running WSL distros.\n\n" +
            "This may take a few minutes.\n\nContinue?",
            "Reclaim Distro Disk Space",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (confirmed != MessageBoxResult.Yes)
        {
            AppendLog("Distro disk reclaim canceled." + Environment.NewLine);
            return;
        }

        try
        {
            var vhdxPath = _wsl.GetDistroVhdxPath();
            var beforeSnapshot = vhdxPath is not null ? TryGetFileProgressSnapshot(vhdxPath) : null;

            if (!string.IsNullOrWhiteSpace(vhdxPath))
            {
                AppendLog($"Detected distro VHDX: {vhdxPath}{Environment.NewLine}");
            }

            if (beforeSnapshot is not null)
            {
                AppendLog($"Current VHDX size: {FormatByteSize(beforeSnapshot.Value.Length)}{Environment.NewLine}");
            }

            AppendLog("Running fstrim inside Dwemer Distro..." + Environment.NewLine);
            var trimResult = await _wsl.RunBashAsync("fstrim -av", text => AppendLog(text), user: "root").ConfigureAwait(true);
            if (trimResult.Succeeded)
            {
                AppendLog("Filesystem trim completed." + Environment.NewLine, "green");
            }
            else
            {
                AppendLog($"Filesystem trim note: {GetCommandError(trimResult)}{Environment.NewLine}", "yellow");
            }

            await PrepareDistroForSafeCompactionAsync().ConfigureAwait(true);

            if (string.IsNullOrWhiteSpace(vhdxPath))
            {
                AppendLog("Could not locate ext4.vhdx automatically. Immediate Windows compaction was skipped." + Environment.NewLine, "yellow");
                MessageBox.Show(
                    "Filesystem trim and WSL shutdown completed, but the launcher could not locate ext4.vhdx for an immediate compact pass.\n\n" +
                    "Start the server again when you're ready.",
                    "Disk Reclaim Partial",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            AppendLog("Running elevated Windows VHDX compact..." + Environment.NewLine);
            var compactResult = await CompactVhdxAsync(vhdxPath).ConfigureAwait(true);
            if (!compactResult.Succeeded)
            {
                var compactError = GetCommandError(compactResult);
                var compactTag = compactResult.ExitCode == 1223 ? "yellow" : "red";
                AppendLog($"Disk compaction failed: {compactError}{Environment.NewLine}", compactTag);
                if (!string.IsNullOrWhiteSpace(compactResult.StandardOutput))
                {
                    AppendLog(compactResult.StandardOutput.Trim() + Environment.NewLine, compactTag);
                }

                MessageBox.Show(
                    $"Disk compaction did not complete.\n\n{compactError}\n\nStart the server again when you're ready.",
                    compactResult.ExitCode == 1223 ? "Disk Reclaim Canceled" : "Disk Reclaim Failed",
                    MessageBoxButton.OK,
                    compactResult.ExitCode == 1223 ? MessageBoxImage.Warning : MessageBoxImage.Error);
                return;
            }

            var afterSnapshot = TryGetFileProgressSnapshot(vhdxPath);
            var summary = BuildDiskReclaimSummary(vhdxPath, beforeSnapshot, afterSnapshot);
            AppendLog(summary + Environment.NewLine, "green");

            MessageBox.Show(
                $"Distro disk reclaim completed.\n\n{summary}\n\nStart the server again when you're ready.",
                "Disk Reclaim Complete",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            AppendLog($"Disk reclaim error: {ex.Message}{Environment.NewLine}", "red");
            MessageBox.Show(
                $"Failed to reclaim distro disk space.\n\n{ex.Message}",
                "Disk Reclaim Failed",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private async Task PrepareDistroForSafeCompactionAsync()
    {
        AppendLog("Stopping Dwemer Distro for disk maintenance..." + Environment.NewLine);

        if (_serverProcess is { HasExited: false })
        {
            AppendLog("Requesting Dwemer Distro server process to stop cleanly..." + Environment.NewLine);
        }

        await TryStopTrackedServerProcessAsync(
                TimeSpan.FromSeconds(10),
                killOnTimeout: false,
                timeoutMessage: "DwemerDistro server process did not exit within 10 seconds. Continuing with WSL shutdown.")
            .ConfigureAwait(true);

        AppendLog("Flushing filesystem buffers..." + Environment.NewLine);
        var syncResult = await _wsl.RunDistroAsUserAsync("root", new[] { "sync" }).ConfigureAwait(true);
        if (!syncResult.Succeeded)
        {
            AppendLog($"Filesystem sync note: {GetCommandError(syncResult)}{Environment.NewLine}", "yellow");
        }

        AppendLog("Shutting down WSL..." + Environment.NewLine);
        var shutdownResult = await _wsl.ShutdownAsync(text => AppendLog(text)).ConfigureAwait(true);
        if (!shutdownResult.Succeeded)
        {
            AppendLog($"WSL shutdown note: {GetCommandError(shutdownResult)}{Environment.NewLine}", "yellow");
        }

        if (await _wsl.DistroRunningAsync().ConfigureAwait(false))
        {
            AppendLog("WSL shutdown did not stop Dwemer Distro cleanly. Falling back to terminate." + Environment.NewLine, "yellow");
            var terminateResult = await _wsl.TerminateDistroAsync().ConfigureAwait(false);
            if (!terminateResult.Succeeded)
            {
                var note = GetCommandError(terminateResult);
                if (!string.IsNullOrWhiteSpace(note))
                {
                    AppendLog($"WSL stop note: {note}{Environment.NewLine}", "yellow");
                }
            }
        }

        _serverProcess = null;
        RunOnUi(() =>
        {
            StopStartAnimation();
            IsServerRunning = false;
            IsServerStarting = false;
            StartButtonText = "Start Server";
        });
    }

    private async Task ExportDistroAsync()
    {
        if (!await _wsl.DistroExistsAsync().ConfigureAwait(false))
        {
            MessageBox.Show(
                $"{LauncherConstants.DistroName} is not currently installed.",
                "Export Full Distro",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        var archivePath = GetExportArchivePath();
        if (string.IsNullOrWhiteSpace(archivePath))
        {
            AppendLog("Full distro export canceled." + Environment.NewLine);
            return;
        }

        var confirmed = MessageBox.Show(
            $"This will stop {LauncherConstants.DistroName} and export the full distro to:\n\n{archivePath}\n\n" +
            $"Close any open \\\\wsl.localhost\\{LauncherConstants.DistroName} Explorer windows first.\n\nContinue?",
            "Export Full Distro",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (confirmed != MessageBoxResult.Yes)
        {
            AppendLog("Full distro export canceled." + Environment.NewLine);
            return;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(archivePath)!);

            AppendLog($"Preparing full distro export: {archivePath}{Environment.NewLine}");
            await StopDistroForMaintenanceAsync().ConfigureAwait(true);

            AppendLog("Running WSL export. This can take several minutes..." + Environment.NewLine);
            var result = await RunArchiveOperationWithProgressAsync(
                    callback => _wsl.ExportDistroAsync(archivePath, callback),
                    archivePath,
                    "Export progress")
                .ConfigureAwait(true);
            if (!result.Succeeded)
            {
                var error = GetCommandError(result);
                AppendLog($"Full distro export failed: {error}{Environment.NewLine}", "red");
                MessageBox.Show(
                    $"Full distro export failed.\n\n{error}",
                    "Export Failed",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                return;
            }

            AppendLog($"Full distro export completed: {archivePath}{Environment.NewLine}", "green");
            MessageBox.Show(
                $"Full distro export completed.\n\nArchive:\n{archivePath}",
                "Export Complete",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            AppendLog($"Full distro export error: {ex.Message}{Environment.NewLine}", "red");
            MessageBox.Show(
                $"Full distro export failed.\n\n{ex.Message}",
                "Export Failed",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            QueueServerStatusRefresh(immediate: true);
        }
    }

    private async Task ImportDistroAsync()
    {
        var archivePath = GetImportArchivePath();
        if (string.IsNullOrWhiteSpace(archivePath))
        {
            AppendLog("Full distro import canceled." + Environment.NewLine);
            return;
        }

        var installPath = GetImportInstallPath(archivePath);
        if (string.IsNullOrWhiteSpace(installPath))
        {
            AppendLog("Full distro import canceled." + Environment.NewLine);
            return;
        }

        AppendLog($"Selected import location: {installPath}{Environment.NewLine}");

        if (!File.Exists(archivePath))
        {
            MessageBox.Show(
                $"The selected archive was not found:\n\n{archivePath}",
                "Import Full Distro",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            return;
        }

        if (Directory.Exists(installPath) && Directory.EnumerateFileSystemEntries(installPath).Any())
        {
            var continueNonEmpty = MessageBox.Show(
                $"The selected install folder is not empty:\n\n{installPath}\n\n" +
                "WSL can import into an existing folder, but this is safest with a dedicated distro folder.\n\nContinue anyway?",
                "Import Full Distro",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (continueNonEmpty != MessageBoxResult.Yes)
            {
                AppendLog("Full distro import canceled." + Environment.NewLine);
                return;
            }
        }

        var distroExists = await _wsl.DistroExistsAsync().ConfigureAwait(false);
        string? backupPath = null;

        if (distroExists)
        {
            var replaceDecision = MessageBox.Show(
                $"{LauncherConstants.DistroName} is already installed.\n\n" +
                $"Selected archive:\n{archivePath}\n\n" +
                $"Selected install folder:\n{installPath}\n\n" +
                "Yes: create a backup export first\n" +
                "No: replace it without making a backup\n" +
                "Cancel: abort import",
                "Import Full Distro",
                MessageBoxButton.YesNoCancel,
                MessageBoxImage.Warning);

            if (replaceDecision == MessageBoxResult.Cancel)
            {
                AppendLog("Full distro import canceled." + Environment.NewLine);
                return;
            }

            if (replaceDecision == MessageBoxResult.Yes)
            {
                backupPath = GetPreImportBackupPath();
                if (string.IsNullOrWhiteSpace(backupPath))
                {
                    AppendLog("Full distro import canceled." + Environment.NewLine);
                    return;
                }

                if (string.Equals(
                        Path.GetFullPath(backupPath),
                        Path.GetFullPath(archivePath),
                        StringComparison.OrdinalIgnoreCase))
                {
                    MessageBox.Show(
                        "The pre-import backup path cannot be the same file as the archive you are importing.",
                        "Import Full Distro",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                    return;
                }
            }
            else
            {
                var destructiveConfirm = MessageBox.Show(
                    $"This will unregister the current {LauncherConstants.DistroName} distro and replace it.\n\n" +
                    $"Selected archive:\n{archivePath}\n\n" +
                    $"Selected install folder:\n{installPath}\n\nContinue?",
                    "Confirm Distro Replace",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning);

                if (destructiveConfirm != MessageBoxResult.Yes)
                {
                    AppendLog("Full distro import canceled." + Environment.NewLine);
                    return;
                }
            }
        }
        else
        {
            var importConfirm = MessageBox.Show(
                $"Import {LauncherConstants.DistroName}.\n\n" +
                $"Selected archive:\n{archivePath}\n\n" +
                $"Selected install folder:\n{installPath}\n\nContinue?",
                "Import Full Distro",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (importConfirm != MessageBoxResult.Yes)
            {
                AppendLog("Full distro import canceled." + Environment.NewLine);
                return;
            }
        }

        try
        {
            Directory.CreateDirectory(installPath);
            await StopDistroForMaintenanceAsync().ConfigureAwait(true);

            if (!string.IsNullOrWhiteSpace(backupPath))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(backupPath)!);
                AppendLog($"Creating pre-import backup: {backupPath}{Environment.NewLine}");
                AppendLog("Running WSL export for backup. This can take several minutes..." + Environment.NewLine);

                var backupResult = await RunArchiveOperationWithProgressAsync(
                        callback => _wsl.ExportDistroAsync(backupPath, callback),
                        backupPath,
                        "Backup export progress")
                    .ConfigureAwait(true);
                if (!backupResult.Succeeded)
                {
                    var backupError = GetCommandError(backupResult);
                    AppendLog($"Pre-import backup failed: {backupError}{Environment.NewLine}", "red");
                    MessageBox.Show(
                        $"Pre-import backup failed.\n\n{backupError}",
                        "Import Failed",
                        MessageBoxButton.OK,
                        MessageBoxImage.Error);
                    return;
                }

                AppendLog($"Pre-import backup completed: {backupPath}{Environment.NewLine}", "green");
            }

            if (distroExists)
            {
                AppendLog($"Unregistering existing {LauncherConstants.DistroName} distro..." + Environment.NewLine);
                var unregisterResult = await _wsl.UnregisterDistroAsync(text => AppendLog(text)).ConfigureAwait(true);
                if (!unregisterResult.Succeeded)
                {
                    var unregisterError = GetCommandError(unregisterResult);
                    AppendLog($"Failed to unregister existing distro: {unregisterError}{Environment.NewLine}", "red");
                    MessageBox.Show(
                        $"Failed to unregister the existing distro.\n\n{unregisterError}",
                        "Import Failed",
                        MessageBoxButton.OK,
                        MessageBoxImage.Error);
                    return;
                }
            }

            AppendLog($"Importing full distro from: {archivePath}{Environment.NewLine}");
            AppendLog("Running WSL import. This can take several minutes..." + Environment.NewLine);
            var importResult = await RunPathOperationWithProgressAsync(
                    callback => _wsl.ImportDistroAsync(installPath, archivePath, callback),
                    installPath,
                    "Import progress",
                    "waiting for install files...")
                .ConfigureAwait(true);
            if (!importResult.Succeeded)
            {
                var importError = GetCommandError(importResult);
                AppendLog($"Full distro import failed: {importError}{Environment.NewLine}", "red");
                MessageBox.Show(
                    $"Full distro import failed.\n\n{importError}" +
                    (!string.IsNullOrWhiteSpace(backupPath) ? $"\n\nBackup archive:\n{backupPath}" : string.Empty),
                    "Import Failed",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                return;
            }

            _wslIp = null;
            AppendLog($"Full distro import completed. Install location: {installPath}{Environment.NewLine}", "green");
            MessageBox.Show(
                $"Full distro import completed.\n\nInstall location:\n{installPath}" +
                (!string.IsNullOrWhiteSpace(backupPath) ? $"\n\nBackup archive:\n{backupPath}" : string.Empty) +
                "\n\nStart the server again when you're ready.",
                "Import Complete",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            AppendLog($"Full distro import error: {ex.Message}{Environment.NewLine}", "red");
            MessageBox.Show(
                $"Full distro import failed.\n\n{ex.Message}",
                "Import Failed",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            QueueServerStatusRefresh(immediate: true);
        }
    }

    public async Task RequestRollbackAsync(string serverKey, string displayName, RollbackTarget? selectedTarget, Window rollbackWindow)
    {
        if (selectedTarget is null)
        {
            MessageBox.Show("Please select a rollback target first.", "No Selection", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var confirmed = MessageBox.Show(
            $"Rollback {displayName} to:\n\n{selectedTarget.Label}\n\n" +
            "Warning: Rolling back to much older versions can cause data/config incompatibility\n" +
            "and may result in data loss if migrations or files are not backward compatible.\n\n" +
            "Any local changes will be auto-stashed first.\n" +
            "Continue?",
            "Confirm Rollback",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (confirmed != MessageBoxResult.Yes)
        {
            return;
        }

        await RollbackServerAsync(selectedTarget, rollbackWindow, serverKey).ConfigureAwait(true);
    }

    public async Task SaveCudaSettingAsync(string gpuValue, Window window)
    {
        var normalizedGpu = gpuValue is "0" or "1" or "2" or "3" ? gpuValue : "all";
        var displayName = normalizedGpu == "all" ? "All GPUs" : $"GPU {normalizedGpu}";

        var configContent = normalizedGpu == "all"
            ? "#!/bin/bash\n" +
              "# CUDA Device Configuration\n" +
              "# This file is auto-generated by Dwemer Distro Launcher and will NOT be overwritten by updates\n" +
              "# Users can configure their GPU selection in the Dwemer Distro Launcher UI\n\n" +
              "# Set which GPU device to use (0 = first GPU, 1 = second GPU, etc.)\n" +
              "# Leave empty or unset to use all available GPUs\n" +
              "# export CUDA_VISIBLE_DEVICES=1\n"
            : "#!/bin/bash\n" +
              "# CUDA Device Configuration\n" +
              "# This file is auto-generated by Dwemer Distro Launcher and will NOT be overwritten by updates\n" +
              "# Users can configure their GPU selection in the Dwemer Distro Launcher UI\n\n" +
              "# Set which GPU device to use (0 = first GPU, 1 = second GPU, etc.)\n" +
              $"# Currently set to: GPU {normalizedGpu}\n" +
              $"export CUDA_VISIBLE_DEVICES={normalizedGpu}\n";

        var bashCommand =
            $"printf %s {EscapeForSingleQuotedBash(configContent)} > /home/dwemer/.cuda_config && chmod +x /home/dwemer/.cuda_config";
        var result = await _wsl.RunDistroAsync(new[] { "bash", "-c", bashCommand }).ConfigureAwait(true);

        if (!result.Succeeded)
        {
            MessageBox.Show(
                $"Failed to save GPU setting:\n{(result.StandardError + result.StandardOutput).Trim()}",
                "Error",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            return;
        }

        AppendLog($"CUDA GPU setting changed to: {displayName}{Environment.NewLine}");

        if (IsServerRunning || IsServerStarting)
        {
            MessageBox.Show(
                $"CUDA GPU set to: {displayName}\n\nRestart the server for changes to take effect.",
                "Restart Required",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        else
        {
            MessageBox.Show($"CUDA GPU set to: {displayName}", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        RunOnUi(window.Close);
    }

    private async Task<string?> GetWslIpAsync(bool forceRefresh, CancellationToken cancellationToken = default)
    {
        if (_wslIp is not null && !forceRefresh)
        {
            return _wslIp;
        }

        try
        {
            var newIp = await _wsl.GetWslIpAsync(cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(newIp) && newIp != _wslIp)
            {
                _wslIp = newIp;
                AppendLog($"DwemerDistro WSL IP: {_wslIp}{Environment.NewLine}");
            }

            return _wslIp;
        }
        catch (Exception ex)
        {
            _wslIp = null;
            AppendLog($"Error checking WSL IP: {ex.Message}{Environment.NewLine}", "red");
            return null;
        }
    }

    private async Task<string?> ReadWslFileFirstLineAsync(string path, CancellationToken cancellationToken = default)
    {
        var result = await _wsl.RunBashAsync(
                $"sed -n '1p' {path} 2>/dev/null",
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        return result.Succeeded && !string.IsNullOrWhiteSpace(result.StandardOutput)
            ? result.StandardOutput.Trim()
            : null;
    }

    private async Task StopDistroForMaintenanceAsync()
    {
        _processRunner.TryKill(_serverProcess);
        _serverProcess = null;

        var terminateResult = await _wsl.TerminateDistroAsync().ConfigureAwait(false);
        if (!terminateResult.Succeeded)
        {
            var note = GetCommandError(terminateResult);
            if (!string.IsNullOrWhiteSpace(note))
            {
                AppendLog($"WSL stop note: {note}{Environment.NewLine}", "yellow");
            }
        }

        RunOnUi(() =>
        {
            StopStartAnimation();
            IsServerRunning = false;
            IsServerStarting = false;
            StartButtonText = "Start Server";
        });
    }

    private async Task<bool> TryStopTrackedServerProcessAsync(
        TimeSpan timeout,
        bool killOnTimeout,
        string timeoutMessage)
    {
        if (_serverProcess is not { HasExited: false } process)
        {
            return true;
        }

        try
        {
            await process.StandardInput.WriteLineAsync().ConfigureAwait(false);
            await process.StandardInput.FlushAsync().ConfigureAwait(false);

            using var timeoutCts = new CancellationTokenSource(timeout);
            try
            {
                await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
                _serverProcess = null;
                return true;
            }
            catch (OperationCanceledException)
            {
                if (killOnTimeout)
                {
                    _processRunner.TryKill(process);
                    _serverProcess = null;
                    AppendLog("DwemerDistro process killed after timeout." + Environment.NewLine, "yellow");
                }
                else
                {
                    AppendLog(timeoutMessage + Environment.NewLine, "yellow");
                }

                return false;
            }
        }
        catch (Exception ex)
        {
            AppendLog($"Server shutdown note: {ex.Message}{Environment.NewLine}", "yellow");
            return false;
        }
    }

    private void QueueServerStatusRefresh(bool immediate = false)
    {
        RunOnUi(() =>
        {
            if (!_serverStatusRetryTimer.IsEnabled)
            {
                _serverStatusRetryTimer.Start();
            }
        });

        if (immediate)
        {
            _ = Task.Run(RetryServerStatusChecksAsync);
        }
    }

    private async Task RetryServerStatusChecksAsync()
    {
        if (_isServerStatusRefreshInProgress)
        {
            return;
        }

        if (!NeedsServerStatusRefresh())
        {
            RunOnUi(() => _serverStatusRetryTimer.Stop());
            return;
        }

        _isServerStatusRefreshInProgress = true;
        try
        {
            using var timeoutCts = new CancellationTokenSource(StartupVersionCheckTimeout);
            await CheckForUpdatesAsync(timeoutCts.Token).ConfigureAwait(false);
            await CheckStobeServerUpdatesAsync(timeoutCts.Token).ConfigureAwait(false);
            await CheckDialecticServerUpdatesAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            LauncherLogService.Startup($"Version status refresh timed out after {StartupVersionCheckTimeout.TotalSeconds:0} seconds.");
            AppendLog("Version status refresh timed out. It will retry later." + Environment.NewLine, "yellow");
        }
        catch (Exception ex)
        {
            AppendLog($"Version status refresh failed: {ex.Message}{Environment.NewLine}", "yellow");
        }
        finally
        {
            _isServerStatusRefreshInProgress = false;
        }

        if (!NeedsServerStatusRefresh())
        {
            RunOnUi(() => _serverStatusRetryTimer.Stop());
        }
    }

    private async Task<CommandResult> RunArchiveOperationWithProgressAsync(
        Func<Action<string>, Task<CommandResult>> operation,
        string archivePath,
        string progressLabel,
        TimeSpan? pollInterval = null)
    {
        return await RunPathOperationWithProgressAsync(
                operation,
                archivePath,
                progressLabel,
                "waiting for archive file...",
                pollInterval)
            .ConfigureAwait(false);
    }

    private async Task<CommandResult> RunPathOperationWithProgressAsync(
        Func<Action<string>, Task<CommandResult>> operation,
        string progressPath,
        string progressLabel,
        string waitingMessage,
        TimeSpan? pollInterval = null)
    {
        var interval = pollInterval ?? TimeSpan.FromSeconds(5);
        using var monitorCts = new CancellationTokenSource();
        var monitorTask = MonitorPathProgressAsync(progressPath, progressLabel, waitingMessage, interval, monitorCts.Token);

        try
        {
            return await operation(text => AppendLog(text)).ConfigureAwait(false);
        }
        finally
        {
            monitorCts.Cancel();
            try
            {
                await monitorTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected when the archive operation completes and stops the monitor.
            }
        }
    }

    private async Task MonitorPathProgressAsync(
        string progressPath,
        string progressLabel,
        string waitingMessage,
        TimeSpan interval,
        CancellationToken cancellationToken)
    {
        long? previousLength = null;
        DateTime? previousWriteUtc = null;
        AppendLog($"{progressLabel}: {waitingMessage}{Environment.NewLine}");

        while (true)
        {
            await Task.Delay(interval, cancellationToken).ConfigureAwait(false);

            var snapshot = TryGetPathProgressSnapshot(progressPath);
            if (snapshot is null)
            {
                AppendLog($"{progressLabel}: {waitingMessage}{Environment.NewLine}");
                continue;
            }

            if (previousLength is null ||
                snapshot.Value.Length != previousLength.Value ||
                snapshot.Value.LastWriteUtc != previousWriteUtc)
            {
                var deltaText = previousLength is null
                    ? string.Empty
                    : $" ({FormatSignedByteDelta(snapshot.Value.Length - previousLength.Value)})";
                AppendLog($"{progressLabel}: {FormatByteSize(snapshot.Value.Length)}{deltaText}{Environment.NewLine}");
            }
            else
            {
                AppendLog($"{progressLabel}: still running at {FormatByteSize(snapshot.Value.Length)}{Environment.NewLine}");
            }

            previousLength = snapshot.Value.Length;
            previousWriteUtc = snapshot.Value.LastWriteUtc;
        }
    }

    private string? GetExportArchivePath()
    {
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        var dialog = new SaveFileDialog
        {
            Title = "Export Full Dwemer Distro",
            Filter = "Tar Archive (*.tar)|*.tar|All Files (*.*)|*.*",
            DefaultExt = ".tar",
            AddExtension = true,
            OverwritePrompt = true,
            InitialDirectory = desktop,
            FileName = $"{LauncherConstants.DistroName}-{DateTime.Now:yyyyMMdd-HHmmss}.tar"
        };

        return dialog.ShowDialog() == true ? Path.GetFullPath(dialog.FileName) : null;
    }

    private string? GetPreImportBackupPath()
    {
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        var dialog = new SaveFileDialog
        {
            Title = "Choose Pre-Import Backup Export",
            Filter = "Tar Archive (*.tar)|*.tar|All Files (*.*)|*.*",
            DefaultExt = ".tar",
            AddExtension = true,
            OverwritePrompt = true,
            InitialDirectory = desktop,
            FileName = $"{LauncherConstants.DistroName}-preimport-{DateTime.Now:yyyyMMdd-HHmmss}.tar"
        };

        return dialog.ShowDialog() == true ? Path.GetFullPath(dialog.FileName) : null;
    }

    private string? GetImportArchivePath()
    {
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        var dialog = new OpenFileDialog
        {
            Title = "Import Full Dwemer Distro",
            Filter = "Tar Archive (*.tar)|*.tar|All Files (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false,
            InitialDirectory = desktop
        };

        return dialog.ShowDialog() == true ? Path.GetFullPath(dialog.FileName) : null;
    }

    private string? GetImportInstallPath(string archivePath)
    {
        var initialDirectory = Path.GetDirectoryName(Path.GetFullPath(archivePath)) ??
                               Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);

        using var dialog = new Forms.FolderBrowserDialog
        {
            Description = "Choose the folder where WSL should store the imported Dwemer distro.",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = true,
            InitialDirectory = initialDirectory
        };

        return dialog.ShowDialog() == Forms.DialogResult.OK
            ? Path.GetFullPath(dialog.SelectedPath)
            : null;
    }

    private async Task<CommandResult> CompactVhdxAsync(string vhdxPath)
    {
        var scriptPath = Path.Combine(Path.GetTempPath(), $"dwemerdistro-compact-{Guid.NewGuid():N}.ps1");
        var logPath = Path.Combine(Path.GetTempPath(), $"dwemerdistro-compact-{Guid.NewGuid():N}.log");
        var script = """
                     param(
                         [Parameter(Mandatory = $true)][string]$VhdxPath,
                         [Parameter(Mandatory = $true)][string]$LogPath
                     )

                     $diskpartScript = Join-Path $env:TEMP ('dwemerdistro-diskpart-' + [guid]::NewGuid().ToString('N') + '.txt')
                     try {
                         @(
                             ('select vdisk file="{0}"' -f $VhdxPath)
                             'attach vdisk readonly'
                             'compact vdisk'
                             'detach vdisk'
                             'exit'
                         ) | Set-Content -LiteralPath $diskpartScript -Encoding Ascii
                         $output = & diskpart.exe /s $diskpartScript 2>&1 | Out-String
                         Set-Content -LiteralPath $LogPath -Value $output -Encoding UTF8
                         if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
                         if ($output -match 'Virtual Disk Service error') { exit 1 }
                         exit 0
                     }
                     catch {
                         ($_ | Out-String) | Set-Content -LiteralPath $LogPath -Encoding UTF8
                         exit 1
                     }
                     finally {
                         Remove-Item -LiteralPath $diskpartScript -ErrorAction SilentlyContinue
                     }
                     """;

        await File.WriteAllTextAsync(scriptPath, script).ConfigureAwait(false);

        try
        {
            var result = await _processRunner.RunElevatedAsync(
                    "powershell.exe",
                    new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", scriptPath, "-VhdxPath", vhdxPath, "-LogPath", logPath })
                .ConfigureAwait(false);

            var log = File.Exists(logPath)
                ? await File.ReadAllTextAsync(logPath).ConfigureAwait(false)
                : string.Empty;
            return new CommandResult(result.ExitCode, log, string.Empty);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return new CommandResult(1223, string.Empty, "The Windows UAC prompt was canceled.");
        }
        finally
        {
            try
            {
                if (File.Exists(scriptPath))
                {
                    File.Delete(scriptPath);
                }

                if (File.Exists(logPath))
                {
                    File.Delete(logPath);
                }
            }
            catch
            {
                // Best-effort temp cleanup.
            }
        }
    }

    private static string BuildDiskReclaimSummary(
        string vhdxPath,
        FileProgressSnapshot? beforeSnapshot,
        FileProgressSnapshot? afterSnapshot)
    {
        if (beforeSnapshot is not null && afterSnapshot is not null)
        {
            var reclaimedBytes = beforeSnapshot.Value.Length - afterSnapshot.Value.Length;
            return reclaimedBytes > 0
                ? $"Reclaimed {FormatByteSize(reclaimedBytes)} from {vhdxPath}.{Environment.NewLine}Before: {FormatByteSize(beforeSnapshot.Value.Length)} | After: {FormatByteSize(afterSnapshot.Value.Length)}"
                : $"Compaction completed for {vhdxPath}.{Environment.NewLine}Size is now {FormatByteSize(afterSnapshot.Value.Length)}.";
        }

        if (afterSnapshot is not null)
        {
            return $"Compaction completed for {vhdxPath}.{Environment.NewLine}Current size: {FormatByteSize(afterSnapshot.Value.Length)}.";
        }

        return $"Compaction completed for {vhdxPath}.";
    }

    private static string GetCommandError(CommandResult result)
    {
        var error = string.IsNullOrWhiteSpace(result.StandardError)
            ? result.StandardOutput
            : result.StandardError;
        var trimmed = error?.Trim();
        return string.IsNullOrWhiteSpace(trimmed)
            ? $"WSL command failed with exit code {result.ExitCode}."
            : trimmed;
    }

    private bool NeedsServerStatusRefresh()
    {
        return StatusNeedsRefresh(_herikaStatusText) ||
               StatusNeedsRefresh(_stobeStatusText) ||
               StatusNeedsRefresh(_dialecticStatusText);
    }

    private static bool StatusNeedsRefresh(string text)
    {
        return text.Contains("Checking...", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("[N/A]", StringComparison.OrdinalIgnoreCase);
    }

    private static string BuildServerVersionStatusText(
        string serviceName,
        string? branch,
        string? dateVersion,
        string? semanticVersion)
    {
        var source = string.IsNullOrWhiteSpace(branch) ? serviceName : branch.Trim();
        return $"{source} | {dateVersion ?? "N/A"} | {semanticVersion ?? "N/A"}";
    }

    private static FileProgressSnapshot? TryGetFileProgressSnapshot(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            var fileInfo = new FileInfo(path);
            fileInfo.Refresh();
            return fileInfo.Exists
                ? new FileProgressSnapshot(fileInfo.Length, fileInfo.LastWriteTimeUtc)
                : null;
        }
        catch
        {
            return null;
        }
    }

    private static FileProgressSnapshot? TryGetPathProgressSnapshot(string path)
    {
        var fileSnapshot = TryGetFileProgressSnapshot(path);
        if (fileSnapshot is not null)
        {
            return fileSnapshot;
        }

        try
        {
            if (!Directory.Exists(path))
            {
                return null;
            }

            var vhdxPath = Path.Combine(path, "ext4.vhdx");
            var vhdxSnapshot = TryGetFileProgressSnapshot(vhdxPath);
            if (vhdxSnapshot is not null)
            {
                return vhdxSnapshot;
            }

            var directoryInfo = new DirectoryInfo(path);
            directoryInfo.Refresh();
            if (!directoryInfo.Exists)
            {
                return null;
            }

            long totalLength = 0;
            DateTime latestWriteUtc = directoryInfo.LastWriteTimeUtc;

            foreach (var file in directoryInfo.EnumerateFiles("*", SearchOption.AllDirectories))
            {
                totalLength += file.Length;
                if (file.LastWriteTimeUtc > latestWriteUtc)
                {
                    latestWriteUtc = file.LastWriteTimeUtc;
                }
            }

            return new FileProgressSnapshot(totalLength, latestWriteUtc);
        }
        catch
        {
            return null;
        }
    }

    private static string FormatSignedByteDelta(long bytes)
    {
        if (bytes > 0)
        {
            return $"+{FormatByteSize(bytes)}";
        }

        if (bytes < 0)
        {
            return $"-{FormatByteSize(Math.Abs(bytes))}";
        }

        return "0 B";
    }

    private static string FormatByteSize(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double value = bytes;
        var unitIndex = 0;

        while (value >= 1024 && unitIndex < units.Length - 1)
        {
            value /= 1024;
            unitIndex++;
        }

        return unitIndex == 0
            ? $"{bytes} {units[unitIndex]}"
            : $"{value:0.0} {units[unitIndex]}";
    }

    private async Task<string?> GetTextOrNullAsync(string url, CancellationToken cancellationToken = default)
    {
        try
        {
            return (await _httpClient.GetStringAsync(url, cancellationToken).ConfigureAwait(false)).Trim();
        }
        catch
        {
            return null;
        }
    }

    private void OpenServerFolder()
    {
        OpenFolder(@"\\wsl.localhost\DwemerAI4Skyrim3\var\www\html");
    }

    private void OpenFolder(string path)
    {
        try
        {
            _processRunner.OpenFolder(path);
        }
        catch (Exception ex)
        {
            AppendLog($"Error opening folder: {ex.Message}{Environment.NewLine}", "red");
        }
    }

    private void OpenInstallComponentsWindow()
    {
        var window = new InstallComponentsWindow(this)
        {
            Owner = Application.Current.MainWindow
        };
        window.Show();
    }

    public void OpenFirstRunSetupWindow()
    {
        RunOnUi(() =>
        {
            if (_firstRunSetupWindow is not null)
            {
                if (_firstRunSetupWindow.WindowState == WindowState.Minimized)
                {
                    _firstRunSetupWindow.WindowState = WindowState.Normal;
                }

                _firstRunSetupWindow.Activate();
                _firstRunSetupWindow.Focus();
                return;
            }

            try
            {
                AppendLog("Opening first-time setup..." + Environment.NewLine);
                var window = new FirstRunSetupWindow(this)
                {
                    Owner = Application.Current.MainWindow
                };
                _firstRunSetupWindow = window;
                window.Closed += (_, _) => _firstRunSetupWindow = null;
                window.Show();
                window.Activate();
            }
            catch (Exception ex)
            {
                _firstRunSetupWindow = null;
                AppendLog($"Failed to open first-time setup: {ex.Message}{Environment.NewLine}", "red");
                MessageBox.Show(
                    $"First-time setup could not be opened.\n\n{ex.Message}",
                    "First-Time Setup",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        });
    }

    private void OpenDebuggingWindow()
    {
        var window = new DebuggingWindow
        {
            Owner = Application.Current.MainWindow,
            DataContext = this
        };
        window.Show();
    }

    private async Task OpenRollbackWindowAsync(string serverKey)
    {
        try
        {
            var config = GetRollbackServerConfig(serverKey);
            var (currentBranch, currentSha) = await GetServerHeadInfoAsync(config.Key).ConfigureAwait(false);
            var rollbackTargets = await GetRollbackTargetsAsync(config.Key).ConfigureAwait(false);

            if (rollbackTargets.Count == 0)
            {
                MessageBox.Show(
                    $"No rollback targets were found in {config.DisplayName}.\n\nConfirm git history is available and try again.",
                    "Rollback Unavailable",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                return;
            }

            RunOnUi(() =>
            {
                var window = new RollbackWindow(this, config.Key, config.DisplayName, currentBranch, currentSha, rollbackTargets)
                {
                    Owner = Application.Current.MainWindow
                };
                window.ShowDialog();
            });
        }
        catch (Exception ex)
        {
            AppendLog($"Rollback menu error: {ex.Message}{Environment.NewLine}", "red");
            MessageBox.Show(
                $"Failed to load rollback options.\n\n{ex.Message}",
                "Rollback Error",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private async Task OpenCudaConfigWindowAsync()
    {
        try
        {
            var currentGpu = await GetCurrentGpuSettingAsync().ConfigureAwait(false);
            RunOnUi(() =>
            {
                var window = new CudaConfigWindow(this, currentGpu)
                {
                    Owner = Application.Current.MainWindow
                };
                window.ShowDialog();
            });
        }
        catch (Exception ex)
        {
            AppendLog($"CUDA configuration error: {ex.Message}{Environment.NewLine}", "red");
            MessageBox.Show(
                $"Failed to load CUDA GPU configuration.\n\n{ex.Message}",
                "CUDA Configuration Error",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    public async Task InstallComponentAsync(string componentKey)
    {
        var definition = GetComponentInstallDefinition(componentKey);
        if (!await _componentInstallGate.WaitAsync(0).ConfigureAwait(true))
        {
            MessageBox.Show(
                "Another component installer is already running. Finish or close it before starting another install.",
                "Component Install In Progress",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        try
        {
            var wrapperScript = BuildComponentInstallWrapper(definition);
            AppendLog($"Starting {definition.DisplayName} installer. Log: {definition.LogPath}{Environment.NewLine}");

            var exitCode = await _processRunner.RunWslScriptInNewConsoleAndWaitAsync(
                    LauncherConstants.DistroName,
                    definition.RunAsUser,
                    wrapperScript)
                .ConfigureAwait(true);
            AppendLog(
                $"{definition.DisplayName} installer exited with code {exitCode}.{Environment.NewLine}",
                exitCode == 0 ? "green" : "red");
        }
        catch (Exception ex)
        {
            AppendLog($"{definition.DisplayName} installer could not be started: {ex.Message}{Environment.NewLine}", "red");
            MessageBox.Show(
                $"{definition.DisplayName} could not be installed.\n\n{ex.Message}",
                "Component Install Failed",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            _componentInstallGate.Release();
        }
    }

    private async Task CheckDialecticServerUpdatesAsync(CancellationToken cancellationToken = default)
    {
        SetDialecticStatus("Checking...", "White");
        var currentBranch = await GetDialecticServerCurrentBranchAsync(cancellationToken).ConfigureAwait(false);
        if (currentBranch is "dialectic" or "dev" or "unstable")
        {
            RunOnUi(() => TargetDialecticBranch = currentBranch);
        }

        var currentVersion = await ReadWslFileFirstLineAsync("/var/www/html/DialecticServer/.version.txt", cancellationToken).ConfigureAwait(false);
        var semanticVersion = await ReadWslFileFirstLineAsync("/var/www/html/DialecticServer/.version_number.txt", cancellationToken).ConfigureAwait(false);
        var gitVersion = currentBranch is null
            ? null
            : await GetTextOrNullAsync($"https://raw.githubusercontent.com/Dwemer-Dynamics/DialecticServer/{currentBranch}/.version.txt", cancellationToken).ConfigureAwait(false);

        var statusText = BuildServerVersionStatusText(
            "dialectic",
            currentBranch,
            FormatDateVersion(currentVersion),
            semanticVersion);

        if (!string.IsNullOrWhiteSpace(currentVersion) && !string.IsNullOrWhiteSpace(gitVersion))
        {
            SetDialecticStatus(statusText, CompareVersions(currentVersion, gitVersion) < 0 ? "Red" : "LimeGreen");
        }
        else if (!string.IsNullOrWhiteSpace(currentVersion) || !string.IsNullOrWhiteSpace(semanticVersion))
        {
            SetDialecticStatus(statusText, "LimeGreen");
        }
        else
        {
            SetDialecticStatus(BuildServerVersionStatusText("dialectic", currentBranch, null, null), "Yellow");
        }
    }

    private void RunCommandInNewWindow(string command)
    {
        try
        {
            AppendLog($"Executing command: {command}{Environment.NewLine}");
            _processRunner.RunInNewConsole(command);
        }
        catch (Exception ex)
        {
            AppendLog($"Unexpected error while running command: {ex.Message}{Environment.NewLine}", "red");
        }
    }

    private async Task<(string? Branch, string? Sha)> GetServerHeadInfoAsync(string serverKey)
    {
        var config = GetRollbackServerConfig(serverKey);
        var result = await _wsl.RunBashAsync(
                $"cd {config.RepoPath} && git rev-parse --abbrev-ref HEAD && git rev-parse --short HEAD")
            .ConfigureAwait(false);

        if (!result.Succeeded)
        {
            return (null, null);
        }

        var lines = result.StandardOutput
            .Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return lines.Length >= 2 ? (lines[0], lines[1]) : (null, null);
    }

    private async Task<List<RollbackTarget>> GetRollbackTargetsAsync(string serverKey)
    {
        var config = GetRollbackServerConfig(serverKey);
        var versionHistoryFiles = config.VersionNumberFiles.Concat(config.VersionTextFiles).ToArray();
        var historyFilesArg = string.Join(" ", versionHistoryFiles);

        var historyResult = await _wsl.RunBashAsync(
                $"cd {config.RepoPath} && git fetch --all --tags --quiet && git log --date=short --pretty=format:'%H\t%h\t%cd' -n 40 -- {historyFilesArg}")
            .ConfigureAwait(false);

        if (!historyResult.Succeeded)
        {
            return [];
        }

        var targets = new List<RollbackTarget>();
        var seenVersions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var line in historyResult.StandardOutput.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = line.Split('\t');
            if (parts.Length < 3)
            {
                continue;
            }

            var fullSha = parts[0];
            var shaShort = parts[1];
            var commitDate = parts[2];
            var versionNumber = await GetCommitFileFirstLineAsync(config.RepoPath, fullSha, config.VersionNumberFiles).ConfigureAwait(false);
            var versionText = await GetCommitFileFirstLineAsync(config.RepoPath, fullSha, config.VersionTextFiles).ConfigureAwait(false);

            if (string.IsNullOrWhiteSpace(versionNumber))
            {
                continue;
            }

            var versionKey = $"{versionNumber}|{versionText}";
            if (!seenVersions.Add(versionKey))
            {
                continue;
            }

            targets.Add(new RollbackTarget
            {
                Ref = fullSha,
                ShaShort = shaShort,
                Date = commitDate,
                VersionNumber = versionNumber,
                VersionText = versionText,
                Label = $"Version {versionNumber} - {commitDate}"
            });
        }

        return targets;
    }

    private async Task<string> GetCommitFileFirstLineAsync(string repoPath, string commitSha, IEnumerable<string> fileCandidates)
    {
        foreach (var fileName in fileCandidates)
        {
            var result = await _wsl.RunBashAsync(
                    $"cd {repoPath} && git show {commitSha}:{fileName} 2>/dev/null | sed -n '1p'")
                .ConfigureAwait(false);

            if (!result.Succeeded)
            {
                continue;
            }

            var line = result.StandardOutput.Trim();
            if (!string.IsNullOrWhiteSpace(line))
            {
                return line;
            }
        }

        return string.Empty;
    }

    private async Task RollbackServerAsync(RollbackTarget target, Window rollbackWindow, string serverKey)
    {
        var config = GetRollbackServerConfig(serverKey);
        if (string.IsNullOrWhiteSpace(target.Ref))
        {
            AppendLog("Rollback failed: invalid target reference." + Environment.NewLine, "red");
            return;
        }

        try
        {
            AppendLog($"Starting {config.DisplayName} rollback..." + Environment.NewLine);
            AppendLog($"Target: {target.Label}{Environment.NewLine}");
            AppendLog("Warning: DB/config compatibility can vary across versions." + Environment.NewLine, "yellow");

            var stashMessage = $"Auto-stash before rollback {DateTime.Now:yyyy-MM-dd HH:mm:ss}";
            var verifyRef = $"{target.Ref}^{{commit}}";
            var bashCommand =
                "set -e; " +
                $"cd {config.RepoPath}; " +
                "git rev-parse --is-inside-work-tree >/dev/null; " +
                "git fetch --all --tags; " +
                $"git rev-parse --verify {EscapeForSingleQuotedBash(verifyRef)} >/dev/null; " +
                $"git stash push -u -m {EscapeForSingleQuotedBash(stashMessage)} >/dev/null 2>&1 || true; " +
                $"git checkout --detach {EscapeForSingleQuotedBash(target.Ref)}; " +
                "echo ROLLBACK_HEAD:$(git rev-parse --short HEAD)";

            var result = await _wsl.RunBashAsync(bashCommand, text => AppendLog(text)).ConfigureAwait(true);
            if (!result.Succeeded)
            {
                AppendLog("Rollback failed. Review output above for details." + Environment.NewLine, "red");
                return;
            }

            var rolledBackSha = result.StandardOutput
                .Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .FirstOrDefault(line => line.StartsWith("ROLLBACK_HEAD:", StringComparison.Ordinal))
                ?.Split(':', 2)
                .ElementAtOrDefault(1)
                ?.Trim();

            AppendLog($"{config.DisplayName} rollback completed successfully. HEAD: {rolledBackSha ?? "unknown"}{Environment.NewLine}", "green");
            RunOnUi(rollbackWindow.Close);

            QueueBackgroundTask("Herika version check", cancellationToken => CheckForUpdatesAsync(cancellationToken), StartupVersionCheckTimeout);
            QueueBackgroundTask("Stobe version check", cancellationToken => CheckStobeServerUpdatesAsync(cancellationToken), StartupVersionCheckTimeout);
            QueueBackgroundTask("Dialectic version check", cancellationToken => CheckDialecticServerUpdatesAsync(cancellationToken), StartupVersionCheckTimeout);
        }
        catch (Exception ex)
        {
            AppendLog($"Rollback error: {ex.Message}{Environment.NewLine}", "red");
        }
    }

    private async Task<string> GetCurrentGpuSettingAsync()
    {
        var result = await _wsl.RunDistroAsync(new[] { "cat", "/home/dwemer/.cuda_config" }).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            return "all";
        }

        foreach (var rawLine in result.StandardOutput.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries))
        {
            var line = rawLine.Trim();
            if (!line.StartsWith("export CUDA_VISIBLE_DEVICES=", StringComparison.Ordinal) ||
                line.StartsWith("#", StringComparison.Ordinal))
            {
                continue;
            }

            var gpuValue = line.Split('=', 2).ElementAtOrDefault(1)?.Trim();
            return gpuValue is "0" or "1" or "2" or "3" ? gpuValue : "all";
        }

        return "all";
    }

    private static RollbackServerConfig GetRollbackServerConfig(string serverKey)
    {
        return serverKey.Trim().ToLowerInvariant() switch
        {
            "stobe" or "stobeserver" => new RollbackServerConfig(
                "stobe",
                "StobeServer",
                "/var/www/html/StobeServer",
                [".version_number.txt", "versionnumber.txt"],
                [".version.txt", "version.txt"]),
            "dialectic" or "dialecticserver" => new RollbackServerConfig(
                "dialectic",
                "DialecticServer",
                "/var/www/html/DialecticServer",
                [".version_number.txt"],
                [".version.txt"]),
            _ => new RollbackServerConfig(
                "herika",
                "HerikaServer",
                "/var/www/html/HerikaServer",
                [".version_number.txt"],
                [".version.txt"])
        };
    }

    private static string EscapeForSingleQuotedBash(string value)
    {
        return $"'{value.Replace("'", "'\"'\"'")}'";
    }

    private static ComponentInstallDefinition GetComponentInstallDefinition(string componentKey)
    {
        return componentKey.Trim().ToLowerInvariant() switch
        {
            "cuda" => new(
                "cuda",
                "CUDA",
                "/usr/local/bin/install_full_packages",
                "command -v nvcc >/dev/null 2>&1 || [ -x /usr/bin/nvcc ] || [ -x /usr/local/cuda/bin/nvcc ]",
                "root"),
            "minime" => new(
                "minime",
                "Minime and TXT2VEC",
                "/home/dwemer/minime-t5/ddistro_install.sh",
                "[ -x /home/dwemer/python-minime/bin/python ]"),
            "chimmcp" => new(
                "chimmcp",
                "CHIM-MCP",
                ChimMcpInstallScript,
                "[ -f /home/dwemer/CHIM-MCP/dist/index.js ]"),
            "pockettts" => new(
                "pockettts",
                "Pocket-TTS",
                """
                cd /home/dwemer
                if [ ! -d pocket-tts/.git ]; then
                    rm -rf pocket-tts
                    git clone https://github.com/Dwemer-Dynamics/pocket-tts pocket-tts
                fi
                /home/dwemer/pocket-tts/ddistro_install.sh
                """,
                "[ -x /home/dwemer/pocket-tts/venv/bin/python ]"),
            "chatterbox" => new(
                "chatterbox",
                "Chatterbox",
                "/home/dwemer/chatterbox/ddistro_install.sh",
                "[ -x /home/dwemer/chatterbox/venv/bin/python ]"),
            "omnivoice" => new(
                "omnivoice",
                "Multilingual OmniVoice TTS",
                """
                cd /home/dwemer
                if [ ! -d omnivoice-tts/.git ]; then
                    rm -rf omnivoice-tts
                    git clone https://github.com/Dwemer-Dynamics/omnivoice-tts omnivoice-tts
                fi
                /home/dwemer/omnivoice-tts/ddistro_install.sh
                """,
                "[ -x /home/dwemer/omnivoice-tts/venv/bin/python ]"),
            "xtts" => new(
                "xtts",
                "Dwemer Distro XTTS",
                "/home/dwemer/xtts-api-server/ddistro_install.sh",
                "[ -x /home/dwemer/python-tts/bin/python ]"),
            "melotts" => new(
                "melotts",
                "MeloTTS",
                "/home/dwemer/MeloTTS/ddistro_install.sh",
                "[ -x /home/dwemer/python-melotts/bin/python ]"),
            "pipertts" => new(
                "pipertts",
                "Piper-TTS",
                "/home/dwemer/piper/ddistro_install.sh",
                "find /home/dwemer/python-piper/lib -path '*/site-packages/piper/const.py' -print -quit 2>/dev/null | grep -q ."),
            "mimic3" => new(
                "mimic3",
                "Mimic3",
                "/home/dwemer/mimic3/ddistro_install.sh",
                "[ -x /home/dwemer/python-mimic3/bin/python ]"),
            "parakeet" => new(
                "parakeet",
                "Parakeet STT",
                """
                cd /home/dwemer
                if [ ! -d parakeet-api-server/.git ]; then
                    rm -rf parakeet-api-server
                    git clone https://github.com/Dwemer-Dynamics/parakeet-api-server parakeet-api-server
                fi
                /home/dwemer/parakeet-api-server/ddistro_install.sh
                """,
                "[ -x /home/dwemer/parakeet-api-server/venv/bin/python ]"),
            "localwhisper" => new(
                "localwhisper",
                "LocalWhisper",
                "/home/dwemer/remote-faster-whisper/ddistro_install.sh",
                "[ -x /home/dwemer/python-stt/bin/python ]"),
            _ => throw new ArgumentOutOfRangeException(nameof(componentKey), componentKey, "Unknown component installer.")
        };
    }

    private static string BuildComponentInstallWrapper(ComponentInstallDefinition definition)
    {
        var payload = $$"""
        set +e
        (
            set -e
        {{definition.InstallScript}}
        )
        install_status=$?
        set -e

        set +e
        bash -lc {{EscapeForSingleQuotedBash(definition.VerificationScript)}}
        verification_status=$?
        set -e

        if [ "$verification_status" -ne 0 ]; then
            echo
            echo "Verification failed: the expected component files were not found."
            exit 1
        fi

        if [ "$install_status" -eq 0 ] || [ "$install_status" -eq 130 ]; then
            exit 0
        fi

        exit "$install_status"
        """;
        var encodedPayload = Convert.ToBase64String(Encoding.UTF8.GetBytes(payload.Replace("\r\n", "\n")));

        return $$"""
        set -uo pipefail
        component_name={{EscapeForSingleQuotedBash(definition.DisplayName)}}
        log_file={{EscapeForSingleQuotedBash(definition.LogPath)}}
        log_dir=$(dirname "$log_file")

        if [ "$(id -u)" -eq 0 ]; then
            install -d -o dwemer -g dwemer -m 0775 "$log_dir"
        else
            mkdir -p "$log_dir"
        fi

        payload_file=$(mktemp)
        cleanup() {
            rm -f "$payload_file"
        }
        trap cleanup EXIT
        echo {{encodedPayload}} | base64 -d > "$payload_file"
        chmod 700 "$payload_file"

        echo "=== $component_name installer ==="
        echo "Install log: $log_file"
        echo

        set +e
        if command -v script >/dev/null 2>&1; then
            script -q -e -c "bash $payload_file" "$log_file"
            status=$?
        else
            bash "$payload_file" 2>&1 | tee "$log_file"
            status=${PIPESTATUS[0]}
        fi
        set -e

        echo
        if [ "$status" -eq 0 ]; then
            echo "$component_name installation completed and was verified."
        else
            echo "$component_name installation failed with exit code $status."
        fi
        echo "Install log: $log_file"
        echo

        if [ "${DWEMER_COMPONENT_INSTALL_NO_PAUSE:-0}" != "1" ]; then
            read -r -p "Press Enter to close this window..." _
        fi
        exit "$status"
        """;
    }

    private async Task FlushUpdateUiAsync()
    {
        await _dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
        await Task.Delay(75).ConfigureAwait(true);
    }

    private void SetHerikaStatus(string text, string color)
    {
        RunOnUi(() =>
        {
            HerikaStatusText = text;
            HerikaStatusColor = color;
        });
    }

    private void SetStobeStatus(string text, string color)
    {
        RunOnUi(() =>
        {
            StobeStatusText = text;
            StobeStatusColor = color;
        });
    }

    private void SetLauncherUpdateState(string text, string color, bool canUpdate, string buttonText)
    {
        RunOnUi(() =>
        {
            LauncherUpdateStatusText = text;
            LauncherUpdateStatusColor = color;
            LauncherUpdateButtonText = buttonText;
            CanUpdateLauncher = canUpdate;
        });
    }

    private void AppendLog(string text, string? tag = null)
    {
        var sanitized = SanitizeLogText(text);
        if (string.IsNullOrEmpty(sanitized))
        {
            return;
        }

        if (_dispatcher.CheckAccess())
        {
            OutputText += sanitized;
            return;
        }

        _ = _dispatcher.BeginInvoke(() => OutputText += sanitized, DispatcherPriority.Background);
    }

    private void RunOnUi(Action action)
    {
        if (_dispatcher.CheckAccess())
        {
            action();
        }
        else
        {
            _dispatcher.Invoke(action);
        }
    }

    private void RaiseServerCommandStates()
    {
        StartServerCommand.RaiseCanExecuteChanged();
        StopServerCommand.RaiseCanExecuteChanged();
    }

    private void StartStartAnimation()
    {
        RunOnUi(() =>
        {
            _startAnimationDots = 0;
            if (!_startAnimationTimer.IsEnabled)
            {
                _startAnimationTimer.Start();
            }
            UpdateStartAnimation();
        });
    }

    private void StopStartAnimation()
    {
        RunOnUi(() =>
        {
            _startAnimationTimer.Stop();
            _startAnimationDots = 0;
        });
    }

    private void UpdateStartAnimation()
    {
        if (!IsServerStarting)
        {
            return;
        }

        var dots = new string('.', _startAnimationDots);
        StartButtonText = $"Server is Starting {dots}".TrimEnd();
        _startAnimationDots = (_startAnimationDots % 3) + 1;
    }

    private static string NormalizeBranch(string value, string fallback, params string[] allowed)
    {
        var normalized = (value ?? string.Empty).Trim().ToLowerInvariant();
        return allowed.Contains(normalized) ? normalized : fallback;
    }

    private static string? FormatDateVersion(string? version)
    {
        if (string.IsNullOrWhiteSpace(version) || version.Length < 8)
        {
            return version;
        }

        return $"{version[4..6]}-{version[6..8]}-{version[0..4]}";
    }

    private int CompareVersions(string v1, string v2)
    {
        if (v1 == v2)
        {
            return 0;
        }

        try
        {
            if (long.TryParse(v1, out var n1) && long.TryParse(v2, out var n2))
            {
                return n1.CompareTo(n2);
            }

            var p1 = v1.Split('.').Select(int.Parse).ToList();
            var p2 = v2.Split('.').Select(int.Parse).ToList();
            var length = Math.Max(p1.Count, p2.Count);
            while (p1.Count < length)
            {
                p1.Add(0);
            }
            while (p2.Count < length)
            {
                p2.Add(0);
            }

            for (var i = 0; i < length; i++)
            {
                var comparison = p1[i].CompareTo(p2[i]);
                if (comparison != 0)
                {
                    return comparison;
                }
            }
        }
        catch (Exception ex)
        {
            AppendLog($"[DEBUG] Version parsing error: {ex.Message}. Using string comparison.{Environment.NewLine}");
            return string.Compare(v1, v2, StringComparison.Ordinal);
        }

        return 0;
    }

    private static string SanitizeLogText(string text)
    {
        var withoutAnsi = RemoveAnsiEscapeSequences(text);
        var normalized = withoutAnsi.Replace("\r\n", "\n");
        var lines = normalized.Split('\n');
        var kept = new List<string>(lines.Length);

        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (trimmed.Length > 0 && IsDecorativeSeparatorLine(trimmed))
            {
                continue;
            }

            kept.Add(line);
        }

        var joined = string.Join(Environment.NewLine, kept);
        return string.IsNullOrWhiteSpace(joined) ? string.Empty : joined;
    }

    private static bool IsDecorativeSeparatorLine(string value)
    {
        if (value.Length < 20)
        {
            return false;
        }

        return value.All(ch =>
            ch == '_' ||
            ch == '\u00AF' ||
            ch == '-' ||
            ch == '=' ||
            ch == ' ');
    }

    private static string RemoveAnsiEscapeSequences(string text)
    {
        return AnsiRegex().Replace(text, string.Empty);
    }

    [GeneratedRegex(@"\x1B(?:[@-Z\\-_]|\[[0-?]*[ -/]*[@-~])")]
    private static partial Regex AnsiRegex();

    private sealed record ComponentInstallDefinition(
        string Key,
        string DisplayName,
        string InstallScript,
        string VerificationScript,
        string RunAsUser = LauncherConstants.DistroUser)
    {
        public string LogPath => $"/home/dwemer/.dwemerdistro/logs/components/{Key}.log";
    }

    private void SetDialecticStatus(string text, string color)
    {
        RunOnUi(() =>
        {
            DialecticStatusText = text;
            DialecticStatusColor = color;
        });
    }

    private sealed record RollbackServerConfig(
        string Key,
        string DisplayName,
        string RepoPath,
        string[] VersionNumberFiles,
        string[] VersionTextFiles);

    private readonly record struct FileProgressSnapshot(long Length, DateTime LastWriteUtc);
}
