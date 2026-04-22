using System.Collections.ObjectModel;
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
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _startAnimationTimer;
    private readonly DispatcherTimer _serverStatusRetryTimer;
    private readonly ProcessRunner _processRunner = new();
    private readonly WslService _wsl;
    private readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromSeconds(15) };
    private readonly LauncherUpdateService _launcherUpdateService;

    private TcpProxyService? _tcpProxyService;
    private DiscoveryService? _discoveryService;
    private Process? _serverProcess;
    private string? _wslIp;

    private string _outputText = string.Empty;
    private bool _isServerRunning;
    private bool _isServerStarting;
    private string _startButtonText = "Start Server";
    private string _herikaStatusText = "HerikaServer: ...";
    private string _herikaStatusColor = "White";
    private string _stobeStatusText = "StobeServer: ...";
    private string _stobeStatusColor = "White";
    private string _nexusVersionText = "CHIM Nexus: ... | STOBE Nexus: ...";
    private string _launcherVersionText = $"Launcher Version: {LauncherConstants.LauncherVersion}";
    private string _launcherUpdateStatusText = "Launcher update: checking...";
    private string _launcherUpdateStatusColor = "White";
    private string _launcherUpdateButtonText = "Check Launcher Update";
    private bool _mcpEnabled = true;
    private bool _includeHerikaServerUpdate = true;
    private bool _includeStobeServerUpdate = true;
    private bool _canUpdateLauncher;
    private string _targetHerikaBranch = "aiagent";
    private string _targetStobeBranch = "stobe";
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

        HerikaBranches = new ObservableCollection<string>(new[] { "aiagent", "dev" });
        StobeBranches = new ObservableCollection<string>(new[] { "stobe", "dev" });

        StartServerCommand = new AsyncRelayCommand(StartServerAsync, () => !IsServerRunning && !IsServerStarting);
        StopServerCommand = new AsyncRelayCommand(StopServerAsync, () => IsServerRunning || IsServerStarting);
        ForceStopServerCommand = new AsyncRelayCommand(ForceStopServerAsync);
        UpdateAllCommand = new AsyncRelayCommand(UpdateAllAsync);
        OpenServerFolderCommand = new RelayCommand(OpenServerFolder);
        InstallComponentsCommand = new RelayCommand(OpenInstallComponentsWindow);
        ConfigureInstalledComponentsCommand = new RelayCommand(() => RunCommandInNewWindow("wsl -d DwemerAI4Skyrim3 -u dwemer -- /usr/local/bin/conf_services"));
        OpenDebuggingCommand = new RelayCommand(OpenDebuggingWindow);
        SaveMcpEnabledCommand = new AsyncRelayCommand(SaveMcpEnabledAsync);
        SaveUpdateIncludeCommand = new AsyncRelayCommand(SaveUpdateIncludeSettingsAsync);
        OpenChimCommand = new RelayCommand(() => _processRunner.OpenExternalUrl(LauncherConstants.ChimNexusUrl));
        OpenStobeCommand = new RelayCommand(() => _processRunner.OpenExternalUrl(LauncherConstants.StobeNexusUrl));
        OpenWikiCommand = new RelayCommand(() => _processRunner.OpenExternalUrl(LauncherConstants.WikiUrl));
        OpenDiscordCommand = new RelayCommand(() => _processRunner.OpenExternalUrl(LauncherConstants.DiscordUrl));

        InstallCudaCommand = new RelayCommand(() => RunCommandInNewWindow("wsl -d DwemerAI4Skyrim3 -- /usr/local/bin/install_full_packages"));
        InstallXttsCommand = new RelayCommand(() => RunCommandInNewWindow("wsl -d DwemerAI4Skyrim3 -u dwemer -- /home/dwemer/xtts-api-server/ddistro_install.sh"));
        InstallChatterboxCommand = new RelayCommand(() => RunCommandInNewWindow("wsl -d DwemerAI4Skyrim3 -u dwemer -- /home/dwemer/chatterbox/ddistro_install.sh"));
        InstallMeloTtsCommand = new RelayCommand(() => RunCommandInNewWindow("wsl -d DwemerAI4Skyrim3 -u dwemer -- /home/dwemer/MeloTTS/ddistro_install.sh"));
        InstallMinimeT5Command = new RelayCommand(() => RunCommandInNewWindow("wsl -d DwemerAI4Skyrim3 -u dwemer -- /home/dwemer/minime-t5/ddistro_install.sh"));
        InstallMimic3Command = new RelayCommand(() => RunCommandInNewWindow("wsl -d DwemerAI4Skyrim3 -u dwemer -- /home/dwemer/mimic3/ddistro_install.sh"));
        InstallPiperTtsCommand = new RelayCommand(() => RunCommandInNewWindow("wsl -d DwemerAI4Skyrim3 -u dwemer -- /home/dwemer/piper/ddistro_install.sh"));
        InstallLocalWhisperCommand = new RelayCommand(() => RunCommandInNewWindow("wsl -d DwemerAI4Skyrim3 -u dwemer -- /home/dwemer/remote-faster-whisper/ddistro_install.sh"));
        InstallParakeetCommand = new RelayCommand(() => RunCommandInNewWindow("wsl -d DwemerAI4Skyrim3 -u dwemer -- /home/dwemer/parakeet-api-server/ddistro_install.sh"));
        InstallPocketTtsCommand = new RelayCommand(() => RunCommandInNewWindow("wsl -d DwemerAI4Skyrim3 -u dwemer -- /home/dwemer/pocket-tts/ddistro_install.sh"));
        OpenPiperVoicesFolderCommand = new RelayCommand(() => OpenFolder(@"\\wsl.localhost\DwemerAI4Skyrim3\home\dwemer\piper\voices"));

        OpenTerminalCommand = new RelayCommand(() => RunCommandInNewWindow("wsl -d DwemerAI4Skyrim3 -u dwemer -- /usr/local/bin/terminal"));
        ViewMemoryUsageCommand = new RelayCommand(() => RunCommandInNewWindow("wsl -d DwemerAI4Skyrim3 -- htop"));
        ExportDistroCommand = new AsyncRelayCommand(ExportDistroAsync);
        ImportDistroCommand = new AsyncRelayCommand(ImportDistroAsync);
        OpenHerikaRollbackCommand = new RelayCommand(() => _ = OpenRollbackWindowAsync("herika"));
        OpenStobeRollbackCommand = new RelayCommand(() => _ = OpenRollbackWindowAsync("stobe"));
        ViewXttsLogsCommand = new RelayCommand(() => RunCommandInNewWindow("wsl -d DwemerAI4Skyrim3 -u dwemer -- tail -n 100 -f /home/dwemer/xtts-api-server/log.txt"));
        ViewChatterboxLogsCommand = new RelayCommand(() => RunCommandInNewWindow("wsl -d DwemerAI4Skyrim3 -u dwemer -- tail -n 100 -f /home/dwemer/chatterbox/log.txt"));
        ViewPocketTtsLogsCommand = new RelayCommand(() => RunCommandInNewWindow("wsl -d DwemerAI4Skyrim3 -u dwemer -- tail -n 100 -f /home/dwemer/pocket-tts/log.txt"));
        ViewMeloTtsLogsCommand = new RelayCommand(() => RunCommandInNewWindow("wsl -d DwemerAI4Skyrim3 -u dwemer -- tail -n 100 -f /home/dwemer/MeloTTS/melo/log.txt"));
        ViewPiperLogsCommand = new RelayCommand(() => RunCommandInNewWindow("wsl -d DwemerAI4Skyrim3 -u dwemer -- tail -n 100 -f /home/dwemer/piper/log.txt"));
        ViewLocalWhisperLogsCommand = new RelayCommand(() => RunCommandInNewWindow("wsl -d DwemerAI4Skyrim3 -u dwemer -- tail -n 100 -f /home/dwemer/remote-faster-whisper/log.txt"));
        ViewParakeetLogsCommand = new RelayCommand(() => RunCommandInNewWindow("wsl -d DwemerAI4Skyrim3 -u dwemer -- tail -n 100 -f /home/dwemer/parakeet-api-server/log.txt"));
        ViewApacheLogsCommand = new RelayCommand(() => RunCommandInNewWindow("wsl -d DwemerAI4Skyrim3 -u dwemer -- tail -n 100 -f /var/log/apache2/error.log"));
        FixWslDnsCommand = new AsyncRelayCommand(FixWslDnsAsync);
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

    public string NexusVersionText
    {
        get => _nexusVersionText;
        private set => SetProperty(ref _nexusVersionText, value);
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

    public ObservableCollection<string> HerikaBranches { get; }
    public ObservableCollection<string> StobeBranches { get; }

    public AsyncRelayCommand StartServerCommand { get; }
    public AsyncRelayCommand StopServerCommand { get; }
    public AsyncRelayCommand ForceStopServerCommand { get; }
    public AsyncRelayCommand UpdateAllCommand { get; }
    public RelayCommand OpenServerFolderCommand { get; }
    public RelayCommand InstallComponentsCommand { get; }
    public RelayCommand ConfigureInstalledComponentsCommand { get; }
    public RelayCommand OpenDebuggingCommand { get; }
    public AsyncRelayCommand SaveMcpEnabledCommand { get; }
    public AsyncRelayCommand SaveUpdateIncludeCommand { get; }
    public RelayCommand OpenChimCommand { get; }
    public RelayCommand OpenStobeCommand { get; }
    public RelayCommand OpenWikiCommand { get; }
    public RelayCommand OpenDiscordCommand { get; }
    public RelayCommand InstallCudaCommand { get; }
    public RelayCommand InstallXttsCommand { get; }
    public RelayCommand InstallChatterboxCommand { get; }
    public RelayCommand InstallMeloTtsCommand { get; }
    public RelayCommand InstallMinimeT5Command { get; }
    public RelayCommand InstallMimic3Command { get; }
    public RelayCommand InstallPiperTtsCommand { get; }
    public RelayCommand InstallLocalWhisperCommand { get; }
    public RelayCommand InstallParakeetCommand { get; }
    public RelayCommand InstallPocketTtsCommand { get; }
    public RelayCommand OpenPiperVoicesFolderCommand { get; }
    public RelayCommand OpenTerminalCommand { get; }
    public RelayCommand ViewMemoryUsageCommand { get; }
    public AsyncRelayCommand ExportDistroCommand { get; }
    public AsyncRelayCommand ImportDistroCommand { get; }
    public RelayCommand OpenHerikaRollbackCommand { get; }
    public RelayCommand OpenStobeRollbackCommand { get; }
    public RelayCommand ViewXttsLogsCommand { get; }
    public RelayCommand ViewChatterboxLogsCommand { get; }
    public RelayCommand ViewPocketTtsLogsCommand { get; }
    public RelayCommand ViewMeloTtsLogsCommand { get; }
    public RelayCommand ViewPiperLogsCommand { get; }
    public RelayCommand ViewLocalWhisperLogsCommand { get; }
    public RelayCommand ViewParakeetLogsCommand { get; }
    public RelayCommand ViewApacheLogsCommand { get; }
    public AsyncRelayCommand FixWslDnsCommand { get; }
    public RelayCommand OpenCudaConfigCommand { get; }
    public AsyncRelayCommand UpdateLauncherCommand { get; }
    public AsyncRelayCommand CleanLogsCommand { get; }
    public AsyncRelayCommand GenerateDiagnosticsCommand { get; }

    public async Task InitializeAsync()
    {
        StartProxyAndDiscovery();
        await LoadMcpEnabledAsync().ConfigureAwait(true);
        await LoadUpdateIncludeSettingsAsync().ConfigureAwait(true);
        _ = Task.Run(CheckForUpdatesAsync);
        _ = Task.Run(CheckStobeServerUpdatesAsync);
        _ = Task.Run(CheckNexusVersionsAsync);
        _ = Task.Run(CheckLauncherUpdatesAsync);
        QueueServerStatusRefresh();
    }

    public async Task ShutdownAsync()
    {
        _startAnimationTimer.Stop();
        _serverStatusRetryTimer.Stop();
        await (_tcpProxyService?.StopAsync() ?? Task.CompletedTask).ConfigureAwait(false);
        await (_discoveryService?.StopAsync() ?? Task.CompletedTask).ConfigureAwait(false);
        _processRunner.TryKill(_serverProcess);
    }

    private void StartProxyAndDiscovery()
    {
        _tcpProxyService = new TcpProxyService(async cancellationToken =>
        {
            var ip = await GetWslIpAsync(forceRefresh: true, cancellationToken).ConfigureAwait(false);
            return ip is null ? null : new IPEndPoint(IPAddress.Parse(ip), LauncherConstants.SkyrimServerPort);
        }, text => AppendLog(text));
        _tcpProxyService.Start();

        _discoveryService = new DiscoveryService(
            cancellationToken => GetWslIpAsync(forceRefresh: false, cancellationToken),
            text => AppendLog(text));
        _discoveryService.Start();
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
                await _serverProcess.StandardInput.WriteLineAsync().ConfigureAwait(false);
                await _serverProcess.StandardInput.FlushAsync().ConfigureAwait(false);
                using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                try
                {
                    await _serverProcess.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    _processRunner.TryKill(_serverProcess);
                    AppendLog("DwemerDistro process killed after timeout." + Environment.NewLine, "yellow");
                }
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
        var includeHerika = IncludeHerikaServerUpdate;
        var includeStobe = IncludeStobeServerUpdate;
        var targetHerika = NormalizeBranch(TargetHerikaBranch, "aiagent", "aiagent", "dev");
        var targetStobe = NormalizeBranch(TargetStobeBranch, "stobe", "stobe", "dev");

        var confirmText = includeHerika || includeStobe
            ? "This will update the Dwemer Distro and selected server components.\n\n" +
              (includeHerika ? $"HerikaServer target branch: {targetHerika}\n" : "HerikaServer update: disabled\n") +
              (includeStobe ? $"StobeServer target branch: {targetStobe}\n" : "StobeServer update: disabled\n") +
              "\nAre you sure?"
            : "This will update Dwemer Distro only.\n\nHerikaServer and StobeServer updates are disabled in the Updater section.\n\nAre you sure?";

        if (MessageBox.Show(confirmText, "Update System", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            AppendLog("Update canceled." + Environment.NewLine);
            return;
        }

        try
        {
            SetHerikaStatus("Updating...", "White");
            if (includeStobe)
            {
                SetStobeStatus("Updating...", "White");
            }

            AppendLog(includeHerika && includeStobe
                ? "Starting full system update..." + Environment.NewLine
                : includeHerika
                    ? "Starting core system update + HerikaServer update..." + Environment.NewLine
                    : includeStobe
                        ? "Starting core system update + StobeServer update..." + Environment.NewLine
                        : "Starting core system update (HerikaServer and StobeServer skipped)..." + Environment.NewLine);
            AppendLog("Preparing update steps..." + Environment.NewLine);
            await FlushUpdateUiAsync().ConfigureAwait(true);

            if (includeHerika)
            {
                AppendLog(Environment.NewLine + "STEP 1: Prepare HerikaServer branch" + Environment.NewLine, "green");
                if (!await SwitchHerikaServerBranchAsync(targetHerika).ConfigureAwait(false))
                {
                    return;
                }
            }

            if (includeStobe)
            {
                AppendLog(Environment.NewLine + (includeHerika ? "STEP 2: Prepare StobeServer branch" : "STEP 1: Prepare StobeServer branch") + Environment.NewLine, "green");
                if (!await SwitchStobeServerBranchAsync(targetStobe).ConfigureAwait(false))
                {
                    return;
                }
            }

            AppendLog(Environment.NewLine + (includeHerika || includeStobe
                ? "STEP 3: Run DwemerDistro core update and component update"
                : "STEP 1: Run DwemerDistro core update") + Environment.NewLine, "green");
            AppendLog("Executing update script..." + Environment.NewLine);
            await FlushUpdateUiAsync().ConfigureAwait(true);

            var serverUpdateRequested = includeHerika || includeStobe;
            var gwsFlags = new List<string>();
            if (!includeHerika)
            {
                gwsFlags.Add("--skip-herika");
            }
            if (!includeStobe)
            {
                gwsFlags.Add("--skip-stobe");
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

            if (result.Succeeded && distroUpdateComplete && (!serverUpdateRequested || !branchErrorDetected))
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
                    AppendLog("Distro update completed successfully. HerikaServer and StobeServer updates were skipped." + Environment.NewLine, "green");
                }
            }
            else
            {
                AppendLog("Update may have encountered issues. Check logs above." + Environment.NewLine, "red");
            }
        }
        catch (Exception ex)
        {
            AppendLog($"Error during update: {ex.Message}{Environment.NewLine}", "red");
        }
        finally
        {
            _ = Task.Run(CheckForUpdatesAsync);
            _ = Task.Run(CheckStobeServerUpdatesAsync);
            _ = Task.Run(CheckNexusVersionsAsync);
        }
    }

    private async Task<bool> SwitchHerikaServerBranchAsync(string targetBranch)
    {
        if (targetBranch is not ("aiagent" or "dev"))
        {
            AppendLog($"Invalid branch selection: '{targetBranch}'. Expected aiagent or dev.{Environment.NewLine}", "red");
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
        if (targetBranch is not ("stobe" or "dev"))
        {
            AppendLog($"Invalid StobeServer branch selection: '{targetBranch}'. Expected stobe or dev.{Environment.NewLine}", "red");
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

    private async Task<string?> GetCurrentBranchAsync()
    {
        var result = await _wsl.RunBashAsync("cd /var/www/html/HerikaServer && git rev-parse --abbrev-ref HEAD")
            .ConfigureAwait(false);
        return result.Succeeded ? result.StandardOutput.Trim() : null;
    }

    private async Task<string?> GetStobeServerCurrentBranchAsync()
    {
        var result = await _wsl.RunBashAsync("cd /var/www/html/StobeServer && git rev-parse --abbrev-ref HEAD")
            .ConfigureAwait(false);
        return result.Succeeded ? result.StandardOutput.Trim() : null;
    }

    private async Task CheckForUpdatesAsync()
    {
        SetHerikaStatus(BuildServerStatusText("HerikaServer", null, "Checking..."), "White");
        var currentBranch = await GetCurrentBranchAsync().ConfigureAwait(false);
        if (currentBranch is "aiagent" or "dev")
        {
            RunOnUi(() => TargetHerikaBranch = currentBranch);
        }

        var currentVersion = await ReadWslFileFirstLineAsync("/var/www/html/HerikaServer/.version.txt").ConfigureAwait(false);
        var semanticVersion = await ReadWslFileFirstLineAsync("/var/www/html/HerikaServer/.version_number.txt").ConfigureAwait(false);
        var gitVersion = currentBranch is null
            ? null
            : await GetTextOrNullAsync($"https://raw.githubusercontent.com/abeiro/HerikaServer/{currentBranch}/.version.txt").ConfigureAwait(false);

        var versionDisplay = $"{FormatDateVersion(currentVersion) ?? "N/A"} | {semanticVersion ?? "N/A"}";
        var statusText = BuildServerStatusText("HerikaServer", currentBranch, $"[{versionDisplay}]");

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
            SetHerikaStatus(BuildServerStatusText("HerikaServer", currentBranch, "[N/A]"), "Yellow");
        }
    }

    private async Task CheckStobeServerUpdatesAsync()
    {
        SetStobeStatus(BuildServerStatusText("StobeServer", null, "Checking..."), "White");
        var currentBranch = await GetStobeServerCurrentBranchAsync().ConfigureAwait(false);
        if (currentBranch is "stobe" or "dev")
        {
            RunOnUi(() => TargetStobeBranch = currentBranch);
        }

        var currentVersion = await ReadWslFileFirstLineAsync("/var/www/html/StobeServer/.version.txt").ConfigureAwait(false);
        var semanticVersion =
            await ReadWslFileFirstLineAsync("/var/www/html/StobeServer/.version_number.txt").ConfigureAwait(false) ??
            await ReadWslFileFirstLineAsync("/var/www/html/StobeServer/versionnumber.txt").ConfigureAwait(false);
        var gitVersion = currentBranch is null
            ? null
            : await GetTextOrNullAsync($"https://raw.githubusercontent.com/Dwemer-Dynamics/StobeServer/{currentBranch}/.version.txt").ConfigureAwait(false);

        var versionDisplay = $"{FormatDateVersion(currentVersion) ?? "N/A"} | {semanticVersion ?? "N/A"}";
        var statusText = BuildServerStatusText("StobeServer", currentBranch, $"[{versionDisplay}]");

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
            SetStobeStatus(BuildServerStatusText("StobeServer", currentBranch, "[N/A]"), "Yellow");
        }
    }

    private async Task CheckNexusVersionsAsync()
    {
        var chimVersion = await GetNexusVersionAsync(LauncherConstants.ChimNexusUrl).ConfigureAwait(false) ?? "N/A";
        var stobeVersion = await GetNexusVersionAsync(LauncherConstants.StobeNexusUrl).ConfigureAwait(false) ?? "N/A";
        RunOnUi(() => NexusVersionText = $"CHIM Nexus: {chimVersion} | STOBE Nexus: {stobeVersion}");
    }

    private async Task CheckLauncherUpdatesAsync()
    {
        try
        {
            SetLauncherUpdateState("Launcher update: checking...", "White", false, "Check Launcher Update");

            var currentVersion = _launcherUpdateService.GetCurrentVersion().ToString(3);
            RunOnUi(() => LauncherVersionText = $"Launcher Version: {currentVersion}");

            var update = await _launcherUpdateService.CheckForUpdatesAsync().ConfigureAwait(false);
            _pendingLauncherUpdate = update;

            if (update is null)
            {
                SetLauncherUpdateState(
                    $"Launcher update: up to date [{currentVersion}]",
                    "LimeGreen",
                    false,
                    "Launcher Up To Date");
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
                "Check Launcher Update");
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
            RunOnUi(() => LauncherUpdateButtonText = "Downloading Launcher Update...");
            AppendLog("Downloading launcher update..." + Environment.NewLine);

            var packagePath = await _launcherUpdateService.DownloadUpdatePackageAsync(_pendingLauncherUpdate, progress =>
            {
                var text = $"Downloading launcher update... {progress}%";
                SetLauncherUpdateState(text, "White", false, text);
            }).ConfigureAwait(false);

            AppendLog("Launcher update downloaded. Closing launcher to apply update..." + Environment.NewLine, "green");
            _launcherUpdateService.StartUpdaterAndExit(packagePath);
            RunOnUi(() =>
            {
                LauncherUpdateButtonText = "Applying Launcher Update...";
                Application.Current.Shutdown();
            });
        }
        catch (Exception ex)
        {
            SetLauncherUpdateState(
                "Launcher update failed. See log.",
                "Red",
                true,
                "Retry Launcher Update");
            AppendLog($"Launcher update failed: {ex.Message}{Environment.NewLine}", "red");
        }
    }

    private async Task<string?> GetNexusVersionAsync(string url)
    {
        try
        {
            var html = await _httpClient.GetStringAsync(url).ConfigureAwait(false);
            var match = NexusVersionRegex().Match(html);
            return match.Success ? match.Groups[1].Value : null;
        }
        catch
        {
            return null;
        }
    }

    private async Task LoadMcpEnabledAsync()
    {
        var result = await _wsl.RunDistroAsync(new[] { "bash", "-lc", "if [ -f /home/dwemer/.mcp_enabled ]; then cat /home/dwemer/.mcp_enabled; else echo 1 > /home/dwemer/.mcp_enabled; echo 1; fi" })
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

    private async Task LoadUpdateIncludeSettingsAsync()
    {
        var result = await _wsl.RunDistroAsUserAsync(
            "root",
            new[] { "bash", "-lc", "mkdir -p /home/dwemer; if [ ! -f /home/dwemer/.update_include_herika ]; then echo 1 > /home/dwemer/.update_include_herika; fi; if [ ! -f /home/dwemer/.update_include_stobe ]; then echo 1 > /home/dwemer/.update_include_stobe; fi; sed -n '1p' /home/dwemer/.update_include_herika; sed -n '1p' /home/dwemer/.update_include_stobe" })
            .ConfigureAwait(false);

        if (!result.Succeeded)
        {
            RunOnUi(() =>
            {
                IncludeHerikaServerUpdate = true;
                IncludeStobeServerUpdate = true;
            });
            return;
        }

        var lines = result.StandardOutput.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        RunOnUi(() =>
        {
            IncludeHerikaServerUpdate = lines.ElementAtOrDefault(0) != "0";
            IncludeStobeServerUpdate = lines.ElementAtOrDefault(1) != "0";
        });
    }

    private async Task SaveUpdateIncludeSettingsAsync()
    {
        var herika = IncludeHerikaServerUpdate ? "1" : "0";
        var stobe = IncludeStobeServerUpdate ? "1" : "0";
        var result = await _wsl.RunDistroAsUserAsync(
            "root",
            new[] { "bash", "-lc", $"echo {herika} > /home/dwemer/.update_include_herika && echo {stobe} > /home/dwemer/.update_include_stobe" })
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
        AppendLog("Generating diagnostic summary..." + Environment.NewLine);
        var lines = new List<string>
        {
            "DwemerDistro WPF Launcher Diagnostic Summary",
            $"Launcher Version: {LauncherConstants.LauncherVersion}",
            $"Generated: {DateTimeOffset.Now}",
            ""
        };

        foreach (var command in new[]
                 {
                     "wsl -l -v",
                     "wsl -d DwemerAI4Skyrim3 -u dwemer -- bash -lc \"cd /var/www/html/HerikaServer && git status --short --branch\"",
                     "wsl -d DwemerAI4Skyrim3 -u dwemer -- bash -lc \"cd /var/www/html/StobeServer && git status --short --branch\""
                 })
        {
            lines.Add("$ " + command);
            try
            {
                var result = await _processRunner.RunHiddenAsync("cmd.exe", new[] { "/c", command }).ConfigureAwait(false);
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

        var outputDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "DwemerDistro-Diagnostics");
        Directory.CreateDirectory(outputDir);
        var outputPath = Path.Combine(outputDir, $"diagnostics-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
        await File.WriteAllLinesAsync(outputPath, lines).ConfigureAwait(false);
        AppendLog($"Diagnostic file created: {outputPath}{Environment.NewLine}", "green");
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

    private async Task<string?> ReadWslFileFirstLineAsync(string path)
    {
        var result = await _wsl.RunBashAsync($"sed -n '1p' {path} 2>/dev/null").ConfigureAwait(false);
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
            await CheckForUpdatesAsync().ConfigureAwait(false);
            await CheckStobeServerUpdatesAsync().ConfigureAwait(false);
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
        return StatusNeedsRefresh(_herikaStatusText) || StatusNeedsRefresh(_stobeStatusText);
    }

    private static bool StatusNeedsRefresh(string text)
    {
        return text.Contains("Checking...", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("[N/A]", StringComparison.OrdinalIgnoreCase);
    }

    private static string BuildServerStatusText(string serverName, string? branch, string detail)
    {
        var header = string.IsNullOrWhiteSpace(branch)
            ? serverName
            : $"{serverName} ({branch.Trim()})";
        return $"{header}{Environment.NewLine}{detail}";
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

    private async Task<string?> GetTextOrNullAsync(string url)
    {
        try
        {
            return (await _httpClient.GetStringAsync(url).ConfigureAwait(false)).Trim();
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
        var window = new InstallComponentsWindow
        {
            Owner = Application.Current.MainWindow,
            DataContext = this
        };
        window.Show();
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

            _ = Task.Run(CheckForUpdatesAsync);
            _ = Task.Run(CheckStobeServerUpdatesAsync);
            _ = Task.Run(CheckNexusVersionsAsync);
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
            ch == '¯' ||
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

    [GeneratedRegex(@"(?:Version|version)[^<]{0,120}</[^>]+>\s*<[^>]+[^>]*>(\d+\.\d+\.\d+)")]
    private static partial Regex NexusVersionRegex();

    private sealed record RollbackServerConfig(
        string Key,
        string DisplayName,
        string RepoPath,
        string[] VersionNumberFiles,
        string[] VersionTextFiles);

    private readonly record struct FileProgressSnapshot(long Length, DateTime LastWriteUtc);
}
