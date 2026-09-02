using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
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
    internal const int MaxConsoleLines = 3000;
    private const string ConsoleTrimNotice = "[Earlier console output trimmed.]";
    private static readonly TimeSpan StartupSettingsTimeout = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan StartupFirstRunProbeTimeout = TimeSpan.FromSeconds(12);
    private static readonly TimeSpan StartupLauncherUpdateTimeout = TimeSpan.FromSeconds(25);
    private static readonly TimeSpan StartupVersionCheckTimeout = TimeSpan.FromSeconds(20);
    // The automatic sync runs a full system update, so it is bounded far above a status probe.
    private static readonly TimeSpan LauncherVersionSyncTimeout = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan ServerWebPageProbeTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan ServerWebPageStartupTimeout = TimeSpan.FromSeconds(150);
    private static readonly TimeSpan ServerWebPageStartupPollInterval = TimeSpan.FromSeconds(3);
    // How long a critical operation waits for an already-running passive status check to finish
    // before it gives up rather than risk running DiskPart against a distro something reopened.
    private static readonly TimeSpan PassiveStatusDrainTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan PassiveStatusDrainPollInterval = TimeSpan.FromMilliseconds(200);
    private const string DashboardAutoOpenFlagPath = "/home/dwemer/.dashboard_autoopen";
    /// <summary>Records the launcher build that last synchronized this distro.</summary>
    private const string LauncherSyncMarkerPath = "/home/dwemer/.launcher_synced_version";
    private const string DistroRepositoryUrl = "https://github.com/Dwemer-Dynamics/DwemerDistro-Core.git";
    private const string SystemReleaseManifestUrl =
        "https://raw.githubusercontent.com/Dwemer-Dynamics/DwemerDistro-Core/main/system-release.json";
    private const string InstalledSystemReleaseManifestPath = "/var/lib/dwemerdistro/system-release.json";
    private const string DashboardAutoOpenNeutralColor = "#C8C8C8";
    private const string DashboardAutoOpenSuccessColor = "#8FD694";
    private const string DashboardAutoOpenWarningColor = "#FFB641";
    private const string DashboardAutoOpenErrorColor = "#FF8A80";
    private const string ForceGitUpdatesNeutralColor = "#C8C8C8";
    // Force Updates rests on a warning colour whenever it is on, so the destructive state is
    // never reported in the same tone as an ordinary saved preference.
    private const string ForceGitUpdatesWarningColor = "#FFB641";
    private const string ForceGitUpdatesErrorColor = "#FF8A80";
    private const string CompactDistroNeutralColor = "#C8C8C8";
    private const string CompactDistroBusyColor = "#F4D8A6";
    private const string CompactDistroSuccessColor = "#8FD694";
    private const string CompactDistroWarningColor = "#FFB641";
    private const string CompactDistroErrorColor = "#FF8A80";
    private const string SystemStatusNeutralColor = "#C8C8C8";
    private const string SystemStatusBusyColor = "#F4D8A6";
    private const string SystemStatusSuccessColor = "#8FD694";
    private const string SystemStatusAttentionColor = "#FFB641";
    private const string SystemStatusErrorColor = "#FF8A80";
    /// <summary>Appended to a mod's version line, and only when the comparison confirmed it.</summary>
    internal const string UpdateAvailableStatusSuffix = "Update Available";

    /// <summary>The Settings label, reused by every message about the option.</summary>
    internal const string ForceGitUpdatesSettingName = "Force Updates";

    /// <summary>Restated on every mod update while the option is on.</summary>
    internal const string ForceGitUpdatesUpdateWarning =
        "Force Updates is ON: manual edits to Git-tracked files in these servers will be " +
        "permanently discarded. Databases, uploads, profiles, memories, voices, and other untracked " +
        "data are not deleted.";

    /// <summary>The Settings label, reused as the title of every Compact Distro message.</summary>
    internal const string CompactDistroSettingName = "Compact Distro";

    // The four stages the row reports while it runs, in order. Each one names the user-visible
    // effect rather than the tool that produces it, so the status line reads the same way as the
    // row description above it.
    internal const string CompactDistroCleaningCachesStatus =
        "Deleting installer caches the launcher can download again...";
    internal const string CompactDistroFreeingSpaceStatus =
        "Freeing the unused space inside the distro...";
    internal const string CompactDistroStoppingWslStatus =
        "Stopping the server and all running WSL distributions...";
    internal const string CompactDistroCompactingStatus =
        "Handing the freed space back to Windows. Approve the administrator prompt...";
    // Shown the moment the exclusive lock is taken, so the row never keeps showing an earlier
    // run's outcome while this one is already working.
    internal const string CompactDistroPreparingStatus = "Preparing Compact Distro...";

    /// <summary>The one line every exclusive distro operation uses when the shared gate refuses it.</summary>
    internal const string ExclusiveDistroOperationBusyMessage =
        "Another server, component, or system operation is already running.";
    private const string DistroStorageProbeCommand = "command -v ddistro_storage >/dev/null 2>&1";
    private const string DistroStorageCleanupCommand = "ddistro_storage safe-cleanup";

    // The top button says what it does; only a confirmed available update says so instead.
    internal const string SystemUpdateDefaultButtonText = "Update Distro";
    internal const string SystemUpdateAvailableButtonText = "Distro Update Available";

    // Segoe MDL2 Assets, the icon font the rail and caption buttons already use. The badge is a
    // shape, so the top button's signal never rests on colour alone.
    internal const string SystemUpdateAvailableGlyph = "\uE896";
    internal const string SystemUpdateFailedGlyph = "\uE7BA";
    internal const string SystemUpdateUnknownGlyph = "\uE9CE";
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
    git clone --depth 1 https://github.com/Dwemer-Dynamics/CHIM-MCP.git CHIM-MCP
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
npm cache clean --force || echo "CHIM-MCP installed, but the npm download cache could not be removed."

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
    private readonly LauncherReleaseNoticeService _launcherReleaseNoticeService;
    private readonly UpdatePreferencesService _updatePreferences;
    private readonly SemaphoreSlim _componentInstallGate = new(1, 1);

    private TcpProxyService? _tcpProxyService;
    private TcpProxyService? _lorkhanProxyService;
    private DiscoveryService? _discoveryService;
    private Process? _serverProcess;
    private string? _wslIp;
    private Window? _firstRunSetupWindow;

    private string _outputText = string.Empty;
    private int _outputGeneration;
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
    // Null whenever no run owns the button. The idle label is derived from the availability state
    // rather than stored, so a background check that confirms an update relabels the button without
    // anything else having to remember to.
    private string? _systemUpdateRunningButtonText;
    // Unknown until the system-version check reports. The launcher never claims that a distro it
    // has not inspected is current.
    private SystemUpdateAvailability _systemUpdateState = SystemUpdateAvailability.Unknown;
    private string? _installedSystemVersion;
    private string? _availableSystemVersion;
    private bool _isDistroUpdateInProgress;
    private bool _isComponentsOperationInProgress;
    private bool _isExclusiveDistroOperationInProgress;
    // Quickstart is modeless, so its open window and any still-finishing distro work claim the
    // shared gate used by exclusive distro operations. A counter lets an install remain protected
    // if the window closes before that install has finished.
    private int _quickstartDistroActivityCount;
    // Claim and check happen together under this gate, so neither window can slip an operation
    // in between another one's guard check and its flag being set.
    private readonly object _distroOperationGate = new();
    private bool _dashboardAutoOpenEnabled = true;
    private bool _lastSavedDashboardAutoOpenEnabled = true;
    private bool _isDashboardAutoOpenReady;
    private string _dashboardAutoOpenStatusText = "Checking saved preference...";
    private string _dashboardAutoOpenStatusColor = DashboardAutoOpenNeutralColor;
    // Off until a saved preference says otherwise, so an unreadable file can never force an update.
    private bool _forceGitUpdatesEnabled;
    private bool _lastSavedForceGitUpdatesEnabled;
    private string _forceGitUpdatesStatusText = string.Empty;
    private string _forceGitUpdatesStatusColor = ForceGitUpdatesNeutralColor;
    // Empty until the row has actually run something, so it collapses out of the layout at rest.
    private string _compactDistroStatusText = string.Empty;
    private string _compactDistroStatusColor = CompactDistroNeutralColor;
    private bool _canUpdateLauncher;
    private string _targetHerikaBranch = "Main";
    private string _targetStobeBranch = "Main";
    private string _targetDialecticBranch = "Main";
    private int _startAnimationDots;
    // Passive startup and retry checks can run together, so count every WSL-using task under the
    // same gate rather than relying on a single check/set flag.
    private int _passiveDistroActivityCount;
    private bool _isServerStatusRetryInProgress;
    private LauncherReleaseInfo? _pendingLauncherUpdate;
    private GameProfile _selectedGame;

    public MainWindowViewModel()
    {
        _dispatcher = Application.Current.Dispatcher;
        _wsl = new WslService(_processRunner);
        // The three server items back command CanExecute below, so they exist before any command.
        InitializeServerManagement();
        _launcherUpdateService = new LauncherUpdateService(_httpClient, _processRunner);
        _launcherReleaseNoticeService = new LauncherReleaseNoticeService();
        _updatePreferences = new UpdatePreferencesService();
        LoadForceGitUpdates();
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

        HerikaBranches = new ObservableCollection<string>(new[] { "Main", "Dev" });
        StobeBranches = new ObservableCollection<string>(new[] { "Main", "Dev" });
        DialecticBranches = new ObservableCollection<string>(new[] { "Main", "Dev" });
        GameProfiles = new ObservableCollection<GameProfile>(GameProfile.CreateCatalog());
        _selectedGame = GameProfiles[0];

        StartServerCommand = new AsyncRelayCommand(StartServerAsync, () => !IsCriticalMaintenanceInProgress && !IsServerRunning && !IsServerStarting);
        StopServerCommand = new AsyncRelayCommand(StopServerAsync, () => !IsCriticalMaintenanceInProgress && (IsServerRunning || IsServerStarting));
        ForceStopServerCommand = new AsyncRelayCommand(ForceStopServerAsync, () => !IsCriticalMaintenanceInProgress);
        // Update Distro is also the recovery action, so it stays available whether the distro
        // reports itself as current, out of date, or not at all.
        UpdateSystemCommand = new AsyncRelayCommand(UpdateSystemAsync, CanRunUpdateOperation);
        OpenServerFolderCommand = new RelayCommand(OpenServerFolder, CanAccessDistro);
        OpenFirstRunSetupCommand = new RelayCommand(OpenFirstRunSetupWindow, CanAccessDistro);
        SaveDashboardAutoOpenCommand = new AsyncRelayCommand(SaveDashboardAutoOpenAsync, () => _isDashboardAutoOpenReady && CanAccessDistro());
        ConfirmForceGitUpdatesCommand = new RelayCommand(ConfirmForceGitUpdates);
        // Webpage and rollback are meaningless for a product that is not installed.
        OpenChimCommand = new AsyncRelayCommand(() => OpenServerWebPageAsync("CHIM"), () => CanAccessDistro() && HerikaManager.CanUseInstalledFeatures);
        OpenStobeCommand = new AsyncRelayCommand(() => OpenServerWebPageAsync("STOBE"), () => CanAccessDistro() && StobeManager.CanUseInstalledFeatures);
        OpenDialecticCommand = new AsyncRelayCommand(() => OpenServerWebPageAsync("DIALECTIC"), () => CanAccessDistro() && DialecticManager.CanUseInstalledFeatures);
        // Nexus pages are plain external links: they never probe WSL, never start a server, and
        // stay usable whatever the local server is doing.
        OpenChimNexusCommand = new RelayCommand(() => OpenModNexusPage("CHIM"));
        OpenStobeNexusCommand = new RelayCommand(() => OpenModNexusPage("STOBE"));
        OpenDialecticNexusCommand = new RelayCommand(() => OpenModNexusPage("DIALECTIC"));
        OpenWikiCommand = new RelayCommand(() => _processRunner.OpenExternalUrl(LauncherConstants.WikiUrl));
        OpenDiscordCommand = new RelayCommand(() => _processRunner.OpenExternalUrl(LauncherConstants.DiscordUrl));

        OpenPiperVoicesFolderCommand = new RelayCommand(() => OpenFolder(@"\\wsl.localhost\DwemerAI4Skyrim3\home\dwemer\piper\voices"), CanAccessDistro);

        OpenTerminalCommand = new RelayCommand(() => RunCommandInNewWindow("wsl -d DwemerAI4Skyrim3 -u dwemer -- /usr/local/bin/terminal"), CanAccessDistro);
        ViewMemoryUsageCommand = new RelayCommand(() => RunCommandInNewWindow("wsl -d DwemerAI4Skyrim3 -- htop"), CanAccessDistro);
        ExportDistroCommand = new AsyncRelayCommand(ExportDistroAsync, CanRunExclusiveDistroOperation);
        ImportDistroCommand = new AsyncRelayCommand(ImportDistroAsync, CanRunExclusiveDistroOperation);
        OpenHerikaRollbackCommand = new RelayCommand(() => _ = OpenRollbackWindowAsync("herika"), () => CanAccessDistro() && HerikaManager.CanUseInstalledFeatures);
        OpenStobeRollbackCommand = new RelayCommand(() => _ = OpenRollbackWindowAsync("stobe"), () => CanAccessDistro() && StobeManager.CanUseInstalledFeatures);
        OpenDialecticRollbackCommand = new RelayCommand(() => _ = OpenRollbackWindowAsync("dialectic"), () => CanAccessDistro() && DialecticManager.CanUseInstalledFeatures);
        ViewXttsLogsCommand = new RelayCommand(() => RunCommandInNewWindow("wsl -d DwemerAI4Skyrim3 -u dwemer -- tail -n 100 -f /home/dwemer/xtts-api-server/log.txt"), CanAccessDistro);
        ViewChatterboxLogsCommand = new RelayCommand(() => RunCommandInNewWindow("wsl -d DwemerAI4Skyrim3 -u dwemer -- tail -n 100 -f /home/dwemer/chatterbox/log.txt"), CanAccessDistro);
        ViewPocketTtsLogsCommand = new RelayCommand(() => RunCommandInNewWindow("wsl -d DwemerAI4Skyrim3 -u dwemer -- bash -lc \"if [ -f /home/dwemer/audio.cpp/server.log ]; then tail -n 100 -f /home/dwemer/audio.cpp/server.log; else tail -n 100 -f /home/dwemer/pocket-tts/log.txt; fi\""), CanAccessDistro);
        ViewOmniVoiceLogsCommand = new RelayCommand(() => RunCommandInNewWindow("wsl -d DwemerAI4Skyrim3 -u dwemer -- tail -n 100 -f /home/dwemer/omnivoice-tts/logs/server.log"), CanAccessDistro);
        ViewMeloTtsLogsCommand = new RelayCommand(() => RunCommandInNewWindow("wsl -d DwemerAI4Skyrim3 -u dwemer -- tail -n 100 -f /home/dwemer/MeloTTS/melo/log.txt"), CanAccessDistro);
        ViewPiperLogsCommand = new RelayCommand(() => RunCommandInNewWindow("wsl -d DwemerAI4Skyrim3 -u dwemer -- tail -n 100 -f /home/dwemer/piper/log.txt"), CanAccessDistro);
        ViewLocalWhisperLogsCommand = new RelayCommand(() => RunCommandInNewWindow("wsl -d DwemerAI4Skyrim3 -u dwemer -- tail -n 100 -f /home/dwemer/remote-faster-whisper/log.txt"), CanAccessDistro);
        ViewParakeetLogsCommand = new RelayCommand(() => RunCommandInNewWindow("wsl -d DwemerAI4Skyrim3 -u dwemer -- tail -n 100 -f /home/dwemer/parakeet-api-server/log.txt"), CanAccessDistro);
        ViewApacheLogsCommand = new RelayCommand(() => RunCommandInNewWindow("wsl -d DwemerAI4Skyrim3 -u dwemer -- tail -n 100 -f /var/log/apache2/error.log"), CanAccessDistro);
        FixWslDnsCommand = new AsyncRelayCommand(FixWslDnsAsync, CanRunExclusiveDistroOperation);
        DistroDoctorCommand = new RelayCommand(OpenDistroDoctorWindow, CanAccessDistro);
        CompactDistroCommand = new AsyncRelayCommand(CompactDistroAsync, CanRunExclusiveDistroOperation);
        OpenCudaConfigCommand = new RelayCommand(() => _ = OpenCudaConfigWindowAsync(), CanAccessDistro);
        UpdateLauncherCommand = new AsyncRelayCommand(UpdateLauncherAsync, () => CanUpdateLauncher && !IsCriticalMaintenanceInProgress);
        CleanLogsCommand = new AsyncRelayCommand(CleanLogsAsync, CanAccessDistro);
        GenerateDiagnosticsCommand = new AsyncRelayCommand(GenerateDiagnosticsAsync, CanAccessDistro);
    }

    public string OutputText
    {
        get => _outputText;
        private set => SetProperty(ref _outputText, value);
    }

    internal int OutputGeneration => _outputGeneration;

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
                RaiseUpdateCommandStates();
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

    public string SystemUpdateButtonText =>
        _systemUpdateRunningButtonText ?? BuildSystemUpdateIdleButtonText(SystemUpdateState);

    /// <summary>
    /// A running label owns the button for as long as the run lasts. Passing null hands the button
    /// back to the availability state.
    /// </summary>
    private void SetSystemUpdateRunningButtonText(string? runningText)
    {
        if (string.Equals(_systemUpdateRunningButtonText, runningText, StringComparison.Ordinal))
        {
            return;
        }

        _systemUpdateRunningButtonText = runningText;
        OnPropertyChanged(nameof(SystemUpdateButtonText));
        OnPropertyChanged(nameof(SystemUpdateAccessibleName));
    }

    /// <summary>
    /// What the launcher currently knows about the shared system. Availability never depends on it:
    /// Update Distro is also the recovery action, so it stays live while the state is current or
    /// unknown.
    /// </summary>
    public SystemUpdateAvailability SystemUpdateState
    {
        get => _systemUpdateState;
        private set
        {
            if (SetProperty(ref _systemUpdateState, value))
            {
                RaiseSystemUpdateStatusChanged();
            }
        }
    }

    /// <summary>The compact line under the Components caption. Words first; colour only echoes them.</summary>
    public string SystemStatusText =>
        BuildSystemStatusText(SystemUpdateState, _installedSystemVersion, _availableSystemVersion);

    public string SystemStatusColor => BuildSystemStatusColor(SystemUpdateState);

    public string SystemStatusHelpText =>
        BuildSystemStatusText(SystemUpdateState, _installedSystemVersion, _availableSystemVersion) +
        " Update Distro updates DwemerDistro and shared components. Installed mods are not changed.";

    public string SystemUpdateBadgeGlyph => BuildSystemUpdateBadgeGlyph(SystemUpdateState);

    public string SystemUpdateBadgeColor => BuildSystemStatusColor(SystemUpdateState);

    /// <summary>
    /// The badge overlays the button's right gutter, which the button reserves symmetrically, so
    /// the label stays centred in the frame whether or not the badge is up. Showing it widens the
    /// frame by that gutter, but the surrounding slot reserves the widest state, so nothing else
    /// in the action row moves.
    /// </summary>
    public bool IsSystemUpdateBadgeVisible => BuildSystemUpdateBadgeGlyph(SystemUpdateState).Length > 0;

    public string SystemUpdateAccessibleName =>
        BuildSystemUpdateAccessibleName(SystemUpdateButtonText, SystemUpdateState);

    public string UpdateSystemHelpText => BuildUpdateSystemHelpText(
        CanRunUpdateOperation(), SystemUpdateState, _installedSystemVersion, _availableSystemVersion);

    /// <summary>
    /// Every state is spelled out, so someone who cannot see the status line or the badge colour
    /// still reads which one it is.
    /// </summary>
    internal static string BuildSystemStatusText(
        SystemUpdateAvailability state,
        string? installedVersion,
        string? availableVersion)
    {
        var installed = NormalizeSystemVersion(installedVersion);
        var available = NormalizeSystemVersion(availableVersion);

        return state switch
        {
            SystemUpdateAvailability.Checking => "System: checking for updates...",
            SystemUpdateAvailability.Updating => "System: updating now...",
            SystemUpdateAvailability.Current => installed is null
                ? "Distro is up to date."
                : $"Distro is up to date (version {installed}).",
            SystemUpdateAvailability.UpdateAvailable => (installed, available) switch
            {
                (not null, not null) => $"System: update available (installed {installed}, latest {available}).",
                (null, not null) => $"System: update available (latest {available}).",
                _ => "System: update available."
            },
            SystemUpdateAvailability.Failed => "System: last update failed. Run Update Distro to retry.",
            // Unknown is also the recovery state: a distro that cannot report its version is exactly
            // the one Update Distro exists to repair.
            _ => "System: version unknown. Update Distro also repairs a distro that cannot report it."
        };
    }

    internal static string BuildSystemStatusColor(SystemUpdateAvailability state)
    {
        return state switch
        {
            SystemUpdateAvailability.Checking => SystemStatusNeutralColor,
            SystemUpdateAvailability.Updating => SystemStatusBusyColor,
            SystemUpdateAvailability.Current => SystemStatusSuccessColor,
            SystemUpdateAvailability.UpdateAvailable => SystemStatusAttentionColor,
            SystemUpdateAvailability.Failed => SystemStatusErrorColor,
            _ => SystemStatusNeutralColor
        };
    }

    /// <summary>
    /// An empty glyph means no badge. Checking and Updating already say so in the button label and
    /// the status line, so they add nothing on top of the button.
    /// </summary>
    internal static string BuildSystemUpdateBadgeGlyph(SystemUpdateAvailability state)
    {
        return state switch
        {
            SystemUpdateAvailability.UpdateAvailable => SystemUpdateAvailableGlyph,
            SystemUpdateAvailability.Failed => SystemUpdateFailedGlyph,
            SystemUpdateAvailability.Unknown => SystemUpdateUnknownGlyph,
            _ => string.Empty
        };
    }

    /// <summary>
    /// The label the top button carries whenever no run owns it. Only the confirmed UpdateAvailable
    /// state advertises an update - the status colour never decides this - so checking, current,
    /// unknown and failed all keep the plain action label.
    /// </summary>
    internal static string BuildSystemUpdateIdleButtonText(SystemUpdateAvailability state)
    {
        return state == SystemUpdateAvailability.UpdateAvailable
            ? SystemUpdateAvailableButtonText
            : SystemUpdateDefaultButtonText;
    }

    internal static string BuildSystemUpdateAccessibleName(string? buttonText, SystemUpdateAvailability state)
    {
        var label = string.IsNullOrWhiteSpace(buttonText) ? SystemUpdateDefaultButtonText : buttonText.Trim();

        return state switch
        {
            SystemUpdateAvailability.Checking => $"{label}, checking for system updates",
            SystemUpdateAvailability.Updating => $"{label}, system update running",
            SystemUpdateAvailability.Current => $"{label}, system up to date",
            SystemUpdateAvailability.UpdateAvailable => $"{label}, system update available",
            SystemUpdateAvailability.Failed => $"{label}, last system update failed",
            _ => $"{label}, system version unknown"
        };
    }

    internal static string BuildUpdateSystemHelpText(
        bool canRunUpdateOperation,
        SystemUpdateAvailability state,
        string? installedVersion,
        string? availableVersion)
    {
        if (!canRunUpdateOperation)
        {
            return "Unavailable while another server, component, or system operation is running.";
        }

        return "Update DwemerDistro and shared components. Installed mods are not changed. " +
               BuildSystemStatusText(state, installedVersion, availableVersion);
    }

    internal static string? NormalizeSystemVersion(string? version)
    {
        var trimmed = version?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }

    /// <summary>
    /// Backend seam. The approved system-version check - a versioned distro manifest compared with
    /// the local successful-update marker - is the only thing that may report Checking, Current or
    /// UpdateAvailable. Nothing in the UI infers availability, and no Git SHA is consulted here.
    /// </summary>
    internal void ApplySystemUpdateCheckResult(
        SystemUpdateAvailability state,
        string? installedVersion,
        string? availableVersion)
    {
        RunOnUi(() =>
        {
            // A slow startup check must not overwrite the Updating state after a user starts the
            // recovery action. CompleteUpdateOperation queues a fresh check when the run finishes.
            if (IsDistroUpdateInProgress)
            {
                return;
            }

            _installedSystemVersion = NormalizeSystemVersion(installedVersion);
            _availableSystemVersion = NormalizeSystemVersion(availableVersion);
            _systemUpdateState = state;
            RaiseSystemUpdateStatusChanged();
        });
    }

    /// <summary>Moves the state without discarding versions a check has already reported.</summary>
    internal void SetSystemUpdateState(SystemUpdateAvailability state)
    {
        RunOnUi(() =>
        {
            _systemUpdateState = state;
            RaiseSystemUpdateStatusChanged();
        });
    }

    /// <summary>
    /// A finished update makes whatever the check advertised the version now on disk, so the line
    /// never reports the version it just replaced. The real check re-confirms this next time it runs.
    /// </summary>
    private void MarkSystemUpdateSucceeded()
    {
        RunOnUi(() =>
        {
            _installedSystemVersion = _availableSystemVersion ?? _installedSystemVersion;
            _availableSystemVersion = null;
            _systemUpdateState = SystemUpdateAvailability.Current;
            RaiseSystemUpdateStatusChanged();
        });
    }

    private void RaiseSystemUpdateStatusChanged()
    {
        OnPropertyChanged(nameof(SystemUpdateState));
        // The idle label is derived from the state, so it moves with it.
        OnPropertyChanged(nameof(SystemUpdateButtonText));
        OnPropertyChanged(nameof(SystemStatusText));
        OnPropertyChanged(nameof(SystemStatusColor));
        OnPropertyChanged(nameof(SystemStatusHelpText));
        OnPropertyChanged(nameof(SystemUpdateBadgeGlyph));
        OnPropertyChanged(nameof(SystemUpdateBadgeColor));
        OnPropertyChanged(nameof(IsSystemUpdateBadgeVisible));
        OnPropertyChanged(nameof(SystemUpdateAccessibleName));
        OnPropertyChanged(nameof(UpdateSystemHelpText));
    }

    public bool IsComponentInteractionEnabled =>
        !IsDistroUpdateInProgress &&
        !_isComponentsOperationInProgress &&
        !IsCriticalMaintenanceInProgress &&
        !ServerManagers.Any(manager => manager.IsBusy);

    public bool IsCriticalMaintenanceInProgress => _isExclusiveDistroOperationInProgress;

    /// <summary>
    /// True while Quickstart is open or still finishing distro work. It does not disable the rest
    /// of the launcher; it only holds off the exclusive distro operations.
    /// </summary>
    public bool IsQuickstartDistroActivityInProgress
    {
        get
        {
            lock (_distroOperationGate)
            {
                return _quickstartDistroActivityCount > 0;
            }
        }
    }

    public bool IsDistroUpdateInProgress
    {
        get => _isDistroUpdateInProgress;
        private set
        {
            if (SetProperty(ref _isDistroUpdateInProgress, value))
            {
                RaiseUpdateCommandStates();
                OnPropertyChanged(nameof(IsComponentInteractionEnabled));
                // Mod and system updates share the same WSL resources as the per-product actions,
                // so those actions have to stand down for as long as either update runs.
                RefreshServerUpdateConflictState();
            }
        }
    }

    public bool DashboardAutoOpenEnabled
    {
        get => _dashboardAutoOpenEnabled;
        set => SetProperty(ref _dashboardAutoOpenEnabled, value);
    }

    public string DashboardAutoOpenStatusText
    {
        get => _dashboardAutoOpenStatusText;
        private set => SetProperty(ref _dashboardAutoOpenStatusText, value);
    }

    public string DashboardAutoOpenStatusColor
    {
        get => _dashboardAutoOpenStatusColor;
        private set => SetProperty(ref _dashboardAutoOpenStatusColor, value);
    }

    /// <summary>
    /// Two-way for the Settings checkbox, which flips it before
    /// <see cref="ConfirmForceGitUpdatesCommand"/> runs; that command is what confirms, saves, or
    /// puts it back.
    /// </summary>
    public bool ForceGitUpdatesEnabled
    {
        get => _forceGitUpdatesEnabled;
        set => SetProperty(ref _forceGitUpdatesEnabled, value);
    }

    public string ForceGitUpdatesStatusText
    {
        get => _forceGitUpdatesStatusText;
        private set => SetProperty(ref _forceGitUpdatesStatusText, value);
    }

    public string ForceGitUpdatesStatusColor
    {
        get => _forceGitUpdatesStatusColor;
        private set => SetProperty(ref _forceGitUpdatesStatusColor, value);
    }

    /// <summary>
    /// The Compact Distro row's live status line. Empty means "nothing to report", which
    /// <see cref="HasCompactDistroStatus"/> turns into a collapsed, heightless line.
    /// </summary>
    public string CompactDistroStatusText
    {
        get => _compactDistroStatusText;
        private set
        {
            if (SetProperty(ref _compactDistroStatusText, value))
            {
                OnPropertyChanged(nameof(HasCompactDistroStatus));
            }
        }
    }

    public string CompactDistroStatusColor
    {
        get => _compactDistroStatusColor;
        private set => SetProperty(ref _compactDistroStatusColor, value);
    }

    public bool HasCompactDistroStatus => !string.IsNullOrWhiteSpace(CompactDistroStatusText);

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
    public ObservableCollection<GameProfile> GameProfiles { get; }

    public GameProfile SelectedGame
    {
        get => _selectedGame;
        set => SetProperty(ref _selectedGame, value);
    }

    public AsyncRelayCommand StartServerCommand { get; }
    public AsyncRelayCommand StopServerCommand { get; }
    public AsyncRelayCommand ForceStopServerCommand { get; }
    public AsyncRelayCommand UpdateSystemCommand { get; }
    public RelayCommand OpenServerFolderCommand { get; }
    public RelayCommand OpenFirstRunSetupCommand { get; }
    public AsyncRelayCommand SaveDashboardAutoOpenCommand { get; }
    public RelayCommand ConfirmForceGitUpdatesCommand { get; }
    public AsyncRelayCommand OpenChimCommand { get; }
    public AsyncRelayCommand OpenStobeCommand { get; }
    public AsyncRelayCommand OpenDialecticCommand { get; }
    public RelayCommand OpenChimNexusCommand { get; }
    public RelayCommand OpenStobeNexusCommand { get; }
    public RelayCommand OpenDialecticNexusCommand { get; }
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
    public RelayCommand DistroDoctorCommand { get; }
    public AsyncRelayCommand CompactDistroCommand { get; }
    public RelayCommand OpenCudaConfigCommand { get; }
    public AsyncRelayCommand UpdateLauncherCommand { get; }
    public AsyncRelayCommand CleanLogsCommand { get; }
    public AsyncRelayCommand GenerateDiagnosticsCommand { get; }

    public async Task InitializeAsync()
    {
        LauncherLogService.Startup("MainWindowViewModel initialization started.");
        StartProxyAndDiscovery();
        await RunStartupStepAsync("Load dashboard auto-open setting", LoadDashboardAutoOpenAsync, StartupSettingsTimeout).ConfigureAwait(true);
        QueueBackgroundTask("Installed server check", cancellationToken => RefreshServerManagementAsync(cancellationToken), StartupVersionCheckTimeout, accessesDistro: true);
        QueueBackgroundTask("Herika version check", cancellationToken => CheckForUpdatesAsync(cancellationToken), StartupVersionCheckTimeout, accessesDistro: true);
        QueueBackgroundTask("Stobe version check", cancellationToken => CheckStobeServerUpdatesAsync(cancellationToken), StartupVersionCheckTimeout, accessesDistro: true);
        QueueBackgroundTask("Dialectic version check", cancellationToken => CheckDialecticServerUpdatesAsync(cancellationToken), StartupVersionCheckTimeout, accessesDistro: true);
        QueueSystemUpdateCheck();
        QueueServerStatusRefresh();
        LauncherLogService.Startup("MainWindowViewModel initialization completed.");
    }

    public async Task ShutdownAsync()
    {
        LauncherLogService.Startup("Launcher shutdown started.");
        _startAnimationTimer.Stop();
        _serverStatusRetryTimer.Stop();
        await (_tcpProxyService?.StopAsync() ?? Task.CompletedTask).ConfigureAwait(false);
        await (_lorkhanProxyService?.StopAsync() ?? Task.CompletedTask).ConfigureAwait(false);
        await (_discoveryService?.StopAsync() ?? Task.CompletedTask).ConfigureAwait(false);
        _processRunner.TryKill(_serverProcess);
        LauncherLogService.Startup("Launcher shutdown completed.");
    }

    public async Task RunFirstRunSetupStartupCheckAsync()
    {
        LauncherLogService.Startup("First-time setup startup check started.");

        var shouldShowFirstRunSetup = false;
        try
        {
            using var probeCts = new CancellationTokenSource(StartupFirstRunProbeTimeout);
            shouldShowFirstRunSetup = await ShouldShowFirstRunSetupAsync(probeCts.Token).ConfigureAwait(false);
            if (!shouldShowFirstRunSetup)
            {
                LauncherLogService.Startup("First-time setup startup check completed: not needed.");
                QueueLauncherUpdateCheck();
                ShowPendingDedicatedTtsPortsNotice();
                // Queued last, and only on this branch: the automatic system update must never race
                // Quickstart's own install and update steps, or the release notice it would sit behind.
                QueueLauncherVersionSync();
                return;
            }
        }
        catch (OperationCanceledException)
        {
            LauncherLogService.Startup($"First-time setup startup check timed out after {StartupFirstRunProbeTimeout.TotalSeconds:0} seconds.");
            AppendLog("First-time setup check timed out. Open Settings > First-Time Setup if this is a fresh install." + Environment.NewLine, "yellow");
            QueueLauncherUpdateCheck();
            return;
        }
        catch (Exception ex)
        {
            LauncherLogService.Startup("First-time setup startup check failed.", ex);
            AppendLog($"First-time setup check failed: {ex.Message}{Environment.NewLine}", "yellow");
            QueueLauncherUpdateCheck();
            return;
        }

        SuppressDedicatedTtsPortsNoticeForFirstRunSetup();

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

    private void QueueLauncherUpdateCheck()
    {
        QueueBackgroundTask(
            "Launcher update check",
            cancellationToken => CheckLauncherUpdatesAsync(cancellationToken),
            StartupVersionCheckTimeout);
    }

    private void QueueSystemUpdateCheck()
    {
        QueueBackgroundTask(
            "System update check",
            cancellationToken => CheckSystemUpdatesAsync(cancellationToken),
            StartupVersionCheckTimeout,
            accessesDistro: true);
    }

    /// <summary>
    /// Runs the automatic launcher-version system sync on the shared background-task path, so a slow
    /// or failing update is logged rather than blocking startup.
    /// </summary>
    private void QueueLauncherVersionSync()
    {
        QueueBackgroundTask(
            "Launcher version sync",
            cancellationToken => SyncLauncherVersionAsync(cancellationToken),
            LauncherVersionSyncTimeout,
            accessesDistro: true);
    }

    private void ShowPendingDedicatedTtsPortsNotice()
    {
        var currentVersion = _launcherUpdateService.GetCurrentVersion();
        var notice = _launcherReleaseNoticeService.GetPendingDedicatedTtsPortsNotice(currentVersion);
        if (notice is null)
        {
            return;
        }

        RunOnUi(() =>
        {
            MessageBox.Show(
                notice.Message,
                notice.Title,
                MessageBoxButton.OK,
                MessageBoxImage.Information);

            if (!_launcherReleaseNoticeService.TryAcknowledge(notice, out var error))
            {
                LauncherLogService.Startup($"Could not acknowledge launcher release notice '{notice.Key}': {error}");
            }
        });
    }

    private void SuppressDedicatedTtsPortsNoticeForFirstRunSetup()
    {
        var currentVersion = _launcherUpdateService.GetCurrentVersion();
        var notice = _launcherReleaseNoticeService.GetPendingDedicatedTtsPortsNotice(currentVersion);
        if (notice is null)
        {
            return;
        }

        if (_launcherReleaseNoticeService.TryAcknowledge(notice, out var error))
        {
            LauncherLogService.Startup($"Suppressed launcher release notice '{notice.Key}' because first-time setup is required.");
        }
        else
        {
            LauncherLogService.Startup($"Could not suppress launcher release notice '{notice.Key}': {error}");
        }
    }

    public Task<bool> ShouldShowFirstRunSetupAsync(CancellationToken cancellationToken = default)
    {
        return FirstRunSetupViewModel.ShouldShowFirstRunSetupAsync(cancellationToken);
    }

    public async Task<bool> TryApplyLauncherUpdateBeforeFirstRunSetupAsync(CancellationToken cancellationToken = default)
    {
        LauncherReleaseInfo? update = null;
        try
        {
            SetLauncherUpdateState("Launcher update: checking before setup...", "White", false, "Checking...");

            var currentVersion = _launcherUpdateService.GetCurrentVersion().ToString(3);
            RunOnUi(() => LauncherVersionText = $"Launcher Version: {currentVersion}");

            update = await _launcherUpdateService.CheckForUpdatesAsync(cancellationToken).ConfigureAwait(false);
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
            }).ConfigureAwait(false);

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
            _pendingLauncherUpdate = update;
            SetLauncherUpdateState(
                "Launcher update before setup failed. Retry when ready.",
                "Yellow",
                true,
                "Retry Update");
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
        TimeSpan timeout,
        bool accessesDistro = false)
    {
        if (accessesDistro && !TryBeginPassiveDistroActivity())
        {
            LauncherLogService.Startup($"{name} deferred because critical distro maintenance is running.");
            return;
        }

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
            finally
            {
                if (accessesDistro)
                {
                    EndPassiveDistroActivity();
                }
            }
        });
    }

    private void StartProxyAndDiscovery()
    {
        try
        {
            _tcpProxyService = new TcpProxyService(LauncherConstants.SkyrimProxyPort, "CHIM", async cancellationToken =>
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
            _lorkhanProxyService = new TcpProxyService(LauncherConstants.LorkhanProxyPort, "LORKHAN", async cancellationToken =>
            {
                var ip = await GetWslIpAsync(forceRefresh: true, cancellationToken).ConfigureAwait(false);
                return ip is null ? null : new IPEndPoint(IPAddress.Parse(ip), LauncherConstants.LorkhanServerPort);
            }, text => AppendLog(text));
            _lorkhanProxyService.Start();
        }
        catch (Exception ex)
        {
            _lorkhanProxyService = null;
            LauncherLogService.Startup("LORKHAN TCP proxy failed to start.", ex);
            AppendLog($"LORKHAN TCP proxy failed to start: {ex.Message}{Environment.NewLine}", "yellow");
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

    internal static string? ResolveServerWebPageUrl(string? gameKey)
    {
        return gameKey switch
        {
            "CHIM" => LauncherConstants.ChimServerUiUrl,
            "STOBE" => LauncherConstants.StobeServerUiUrl,
            "DIALECTIC" => LauncherConstants.DialecticServerUiUrl,
            _ => null
        };
    }

    /// <summary>
    /// The public mod page for a product. Deliberately separate from
    /// <see cref="ResolveServerWebPageUrl"/>: this one is an external link with no local server,
    /// no WSL probe, and no running-state condition behind it.
    /// </summary>
    internal static string? ResolveNexusPageUrl(string? gameKey)
    {
        return gameKey switch
        {
            "CHIM" => LauncherConstants.ChimNexusUrl,
            "STOBE" => LauncherConstants.StobeNexusUrl,
            "DIALECTIC" => LauncherConstants.DialecticNexusUrl,
            _ => null
        };
    }

    /// <summary>
    /// Opens the mod's Nexus page in the default browser straight away. A browser that refuses to
    /// start is reported in the console; it must never take the launcher down with it.
    /// </summary>
    private void OpenModNexusPage(string gameKey)
    {
        var url = ResolveNexusPageUrl(gameKey);
        if (url is null)
        {
            AppendLog($"No Nexus page is configured for {gameKey}.{Environment.NewLine}", "yellow");
            return;
        }

        try
        {
            _processRunner.OpenExternalUrl(url);
        }
        catch (Exception ex)
        {
            AppendLog($"Could not open the {gameKey} Nexus page: {ex.Message}{Environment.NewLine}", "red");
        }
    }

    /// <summary>Compares the published system release with the last fully successful local update.</summary>
    private async Task CheckSystemUpdatesAsync(CancellationToken cancellationToken = default)
    {
        ApplySystemUpdateCheckResult(SystemUpdateAvailability.Checking, null, null);

        try
        {
            var availablePayload = await _httpClient
                .GetStringAsync(SystemReleaseManifestUrl, cancellationToken)
                .ConfigureAwait(false);
            var availableVersion = ParseSystemReleaseVersion(availablePayload);

            var installedResult = await _wsl.RunBashAsync(
                    BuildSystemReleaseMarkerReadCommand(),
                    loginShell: false,
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            var installedVersion = installedResult.Succeeded
                ? ParseSystemReleaseVersion(installedResult.StandardOutput)
                : null;

            ApplySystemUpdateCheckResult(
                ResolveSystemUpdateAvailability(installedVersion, availableVersion),
                installedVersion,
                availableVersion);
        }
        catch (OperationCanceledException)
        {
            ApplySystemUpdateCheckResult(SystemUpdateAvailability.Unknown, null, null);
            throw;
        }
        catch (Exception ex)
        {
            LauncherLogService.Startup("System update check failed.", ex);
            ApplySystemUpdateCheckResult(SystemUpdateAvailability.Unknown, null, null);
        }
    }

    internal static string? ParseSystemReleaseVersion(string? payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(payload);
            var root = document.RootElement;
            if (!root.TryGetProperty("schema_version", out var schemaVersion)
                || !schemaVersion.TryGetInt32(out var schema)
                || schema != 1
                || !root.TryGetProperty("version", out var versionElement))
            {
                return null;
            }

            var version = NormalizeSystemVersion(versionElement.GetString());
            var segments = version?.Split('.');
            return segments is { Length: 3 }
                   && segments.All(segment => segment.Length > 0 && segment.All(char.IsAsciiDigit))
                    ? version
                    : null;
        }
        catch (JsonException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    internal static SystemUpdateAvailability ResolveSystemUpdateAvailability(
        string? installedVersion,
        string? availableVersion)
    {
        var installed = NormalizeSystemVersion(installedVersion);
        var available = NormalizeSystemVersion(availableVersion);
        if (available is null)
        {
            return SystemUpdateAvailability.Unknown;
        }

        return string.Equals(installed, available, StringComparison.Ordinal)
            ? SystemUpdateAvailability.Current
            : SystemUpdateAvailability.UpdateAvailable;
    }

    internal static string BuildSystemReleaseMarkerReadCommand()
    {
        return $"cat {InstalledSystemReleaseManifestPath} 2>/dev/null || true";
    }

    internal static string BuildSystemReleaseMarkerWriteCommand()
    {
        return "printf '%s\\n' 'dwemer' | sudo -S install -D -m 0644 " +
               $"/home/dwemer/dwemerdistro/system-release.json {InstalledSystemReleaseManifestPath}";
    }

    // A page that answers at all is good enough to open; only a server-side failure
    // (or no answer) means the product is still coming up behind Apache.
    internal static bool IsServerWebPageResponseUsable(HttpStatusCode statusCode)
    {
        return (int)statusCode < 500;
    }

    internal static bool ShouldOfferServerStart(bool isServerRunning, bool isServerStarting, bool canStartServer)
    {
        return !isServerRunning && !isServerStarting && canStartServer;
    }

    private async Task OpenServerWebPageAsync(string gameKey)
    {
        var url = ResolveServerWebPageUrl(gameKey);
        if (url is null)
        {
            AppendLog($"No web page is configured for {gameKey}.{Environment.NewLine}", "yellow");
            return;
        }

        // Probe the page itself so a stale IsServerRunning flag never blocks a reachable page.
        if (await IsServerWebPageReachableAsync(url).ConfigureAwait(true))
        {
            _processRunner.OpenExternalUrl(url);
            return;
        }

        if (IsServerRunning)
        {
            MessageBox.Show(
                $"The server is running, but the {gameKey} Webpage is not responding.\n\n" +
                "Wait a few moments and try again.",
                $"{gameKey} Webpage",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        if (!ShouldOfferServerStart(IsServerRunning, IsServerStarting, StartServerCommand.CanExecute(null)))
        {
            MessageBox.Show(
                $"The server is already starting, but the {gameKey} Webpage is not responding yet.\n\n" +
                "Wait a few moments and try again.",
                $"{gameKey} Webpage",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        var confirmed = MessageBox.Show(
            $"The Dwemer Distro server is not running, so the {gameKey} Webpage is unavailable.\n\n" +
            "Start the server now?",
            $"{gameKey} Webpage",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (confirmed != MessageBoxResult.Yes)
        {
            return;
        }

        StartServerCommand.Execute(null);
        AppendLog($"Waiting up to {ServerWebPageStartupTimeout.TotalSeconds:0} seconds for the {gameKey} Webpage.{Environment.NewLine}");

        if (await WaitForServerWebPageAsync(url).ConfigureAwait(true))
        {
            _processRunner.OpenExternalUrl(url);
            return;
        }

        AppendLog($"The {gameKey} Webpage did not respond before the wait ended.{Environment.NewLine}", "yellow");
        MessageBox.Show(
            $"The server is still starting and the {gameKey} Webpage did not become available in time.\n\n" +
            "Try opening it again in a few minutes.",
            $"{gameKey} Webpage",
            MessageBoxButton.OK,
            MessageBoxImage.Warning);
    }

    // Bounded wait: polls on a fixed interval and gives up at ServerWebPageStartupTimeout.
    private async Task<bool> WaitForServerWebPageAsync(string url)
    {
        var elapsed = Stopwatch.StartNew();
        while (true)
        {
            if (await IsServerWebPageReachableAsync(url).ConfigureAwait(true))
            {
                return true;
            }

            if (elapsed.Elapsed + ServerWebPageStartupPollInterval >= ServerWebPageStartupTimeout)
            {
                return false;
            }

            if (elapsed.Elapsed >= ServerWebPageStartupPollInterval && !IsServerStarting && !IsServerRunning)
            {
                return false;
            }

            await Task.Delay(ServerWebPageStartupPollInterval).ConfigureAwait(true);
        }
    }

    private async Task<bool> IsServerWebPageReachableAsync(string url)
    {
        try
        {
            using var probeCts = new CancellationTokenSource(ServerWebPageProbeTimeout);
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            using var response = await _httpClient
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, probeCts.Token)
                .ConfigureAwait(false);
            return IsServerWebPageResponseUsable(response.StatusCode);
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or InvalidOperationException or UriFormatException)
        {
            return false;
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

    /// <summary>
    /// Keeps one operation lock across the shared system update and the mod that asked for it. Each
    /// mod's own Update button is the only caller: a single product update stays a single product
    /// update, so status is never re-read to pull other mods into the run.
    /// </summary>
    private async Task RunModUpdatesAsync(
        IReadOnlyList<(ServerManagerItemViewModel Product, ServerBranchChannel Branch)> productsToUpdate,
        bool forceGitUpdates)
    {
        if (!CanRunUpdateOperation())
        {
            return;
        }

        // Status can refresh while a confirmation dialog pumps the dispatcher. An empty selection is
        // still a valid run: the system stage is the whole point of the recovery path.
        var eligibleProducts = productsToUpdate
            .Where(update => ShouldUpdateProduct(update.Product.State))
            .ToArray();

        IsDistroUpdateInProgress = true;
        SetSystemUpdateRunningButtonText("Updating Distro...");
        SetSystemUpdateState(SystemUpdateAvailability.Updating);
        var systemSucceeded = false;
        var modsAttempted = false;

        try
        {
            AppendLog(
                eligibleProducts.Length > 0
                    ? $"Updating DwemerDistro and shared components before {string.Join(", ", eligibleProducts.Select(update => update.Product.DisplayName))}." +
                      Environment.NewLine
                    : "Updating DwemerDistro and shared components. The selected mod is no longer eligible, so only the system is updated." +
                      Environment.NewLine);

            var succeeded = await UpdateInstalledServersAsync(
                eligibleProducts,
                async () =>
                {
                    await FlushUpdateUiAsync().ConfigureAwait(true);
                    systemSucceeded = await RunSharedDistroUpdateAsync().ConfigureAwait(true);
                    return systemSucceeded;
                },
                (product, branch) =>
                {
                    modsAttempted = true;
                    SetSystemUpdateRunningButtonText(null);
                    return RunServerOperationAsync(product, ServerOperation.Update, branch, forceGitUpdates);
                }).ConfigureAwait(true);
            var completionMessage = "System and mod updates completed successfully.";
            if (!systemSucceeded)
            {
                completionMessage = "System update failed. No mods were updated. Check the log above.";
            }
            else if (!succeeded)
            {
                completionMessage = "At least one mod update failed. Check the log above.";
            }
            else if (!modsAttempted)
            {
                completionMessage =
                    "System and shared components updated successfully. The selected mod was no longer eligible, so no mod was changed.";
            }

            AppendLog(completionMessage + Environment.NewLine, succeeded ? "green" : "red");
            ReportSystemUpdateOutcome(systemSucceeded);
        }
        catch (Exception ex)
        {
            var message = systemSucceeded ? "Error during mod update" : "System update failed. No mods were updated";
            AppendLog($"{message}: {ex.Message}{Environment.NewLine}", "red");
            ReportSystemUpdateOutcome(systemSucceeded);
        }
        finally
        {
            CompleteUpdateOperation();
        }
    }

    internal void SetComponentsOperationInProgress(bool value)
    {
        if (_isComponentsOperationInProgress == value)
        {
            return;
        }

        _isComponentsOperationInProgress = value;
        RaiseUpdateCommandStates();
        RefreshServerUpdateConflictState();
        OnPropertyChanged(nameof(IsComponentInteractionEnabled));
    }

    /// <summary>
    /// Claims the exclusive distro slot in one atomic step. Every critical operation goes through
    /// this instead of checking and then setting, because Quickstart is modeless and can start an
    /// install on the same dispatcher between those two moments.
    /// </summary>
    private bool TryBeginExclusiveDistroOperation()
    {
        lock (_distroOperationGate)
        {
            if (!CanRunExclusiveDistroOperation())
            {
                return false;
            }

            _isExclusiveDistroOperationInProgress = true;
        }

        NotifyDistroOperationGateChanged();
        return true;
    }

    private void SetExclusiveDistroOperationInProgress(bool value)
    {
        lock (_distroOperationGate)
        {
            if (_isExclusiveDistroOperationInProgress == value)
            {
                return;
            }

            _isExclusiveDistroOperationInProgress = value;
        }

        NotifyDistroOperationGateChanged();
    }

    /// <summary>
    /// Registers the Quickstart window or one of its distro operations with the shared gate so
    /// Compact Distro, Export, Import, and Fix WSL DNS cannot start underneath it. Returns false
    /// when a critical operation already holds the gate, which is the caller's cue to do nothing.
    /// </summary>
    internal bool TryBeginQuickstartDistroActivity()
    {
        lock (_distroOperationGate)
        {
            if (_isExclusiveDistroOperationInProgress)
            {
                return false;
            }

            _quickstartDistroActivityCount++;
        }

        NotifyDistroOperationGateChanged();
        return true;
    }

    internal void EndQuickstartDistroActivity()
    {
        lock (_distroOperationGate)
        {
            if (_quickstartDistroActivityCount == 0)
            {
                return;
            }

            _quickstartDistroActivityCount--;
        }

        NotifyDistroOperationGateChanged();
    }

    /// <summary>
    /// Marshals to the UI thread because Quickstart releases the gate from whichever thread its
    /// install finished on, and every listener below touches command state.
    /// </summary>
    private void NotifyDistroOperationGateChanged()
    {
        RunOnUi(() =>
        {
            OnPropertyChanged(nameof(IsCriticalMaintenanceInProgress));
            OnPropertyChanged(nameof(IsQuickstartDistroActivityInProgress));
            OnPropertyChanged(nameof(IsComponentInteractionEnabled));
            RaiseUpdateCommandStates();
            RaiseServerCommandStates();
            RefreshServerUpdateConflictState();
            RaiseDistroAccessCommandStates();
        });
    }

    private void RaiseUpdateCommandStates()
    {
        UpdateSystemCommand?.RaiseCanExecuteChanged();
        CompactDistroCommand?.RaiseCanExecuteChanged();
        ExportDistroCommand?.RaiseCanExecuteChanged();
        ImportDistroCommand?.RaiseCanExecuteChanged();
        FixWslDnsCommand?.RaiseCanExecuteChanged();
        OnPropertyChanged(nameof(UpdateSystemHelpText));
    }

    private void RaiseDistroAccessCommandStates()
    {
        OpenServerFolderCommand?.RaiseCanExecuteChanged();
        OpenFirstRunSetupCommand?.RaiseCanExecuteChanged();
        SaveDashboardAutoOpenCommand?.RaiseCanExecuteChanged();
        OpenChimCommand?.RaiseCanExecuteChanged();
        OpenStobeCommand?.RaiseCanExecuteChanged();
        OpenDialecticCommand?.RaiseCanExecuteChanged();
        OpenPiperVoicesFolderCommand?.RaiseCanExecuteChanged();
        OpenTerminalCommand?.RaiseCanExecuteChanged();
        ViewMemoryUsageCommand?.RaiseCanExecuteChanged();
        OpenHerikaRollbackCommand?.RaiseCanExecuteChanged();
        OpenStobeRollbackCommand?.RaiseCanExecuteChanged();
        OpenDialecticRollbackCommand?.RaiseCanExecuteChanged();
        ViewXttsLogsCommand?.RaiseCanExecuteChanged();
        ViewChatterboxLogsCommand?.RaiseCanExecuteChanged();
        ViewPocketTtsLogsCommand?.RaiseCanExecuteChanged();
        ViewOmniVoiceLogsCommand?.RaiseCanExecuteChanged();
        ViewMeloTtsLogsCommand?.RaiseCanExecuteChanged();
        ViewPiperLogsCommand?.RaiseCanExecuteChanged();
        ViewLocalWhisperLogsCommand?.RaiseCanExecuteChanged();
        ViewParakeetLogsCommand?.RaiseCanExecuteChanged();
        ViewApacheLogsCommand?.RaiseCanExecuteChanged();
        DistroDoctorCommand?.RaiseCanExecuteChanged();
        OpenCudaConfigCommand?.RaiseCanExecuteChanged();
        UpdateLauncherCommand?.RaiseCanExecuteChanged();
        CleanLogsCommand?.RaiseCanExecuteChanged();
        GenerateDiagnosticsCommand?.RaiseCanExecuteChanged();
    }

    /// <summary>
    /// The one place a run's own result is turned into a state. A success reports Current because
    /// the shared system was just brought forward; a failure reports Failed so the line offers a
    /// retry instead of going quiet.
    /// </summary>
    private void ReportSystemUpdateOutcome(bool succeeded)
    {
        if (succeeded)
        {
            MarkSystemUpdateSucceeded();
            return;
        }

        SetSystemUpdateState(SystemUpdateAvailability.Failed);
    }

    private async Task UpdateSystemAsync()
    {
        await RunSystemUpdateAsync(requireConfirmation: true, sourceLabel: "System").ConfigureAwait(true);
    }

    /// <summary>
    /// Quickstart updates the shared core only. Application servers are installed and updated from
    /// the Choose Your Mods step and the Mods page, never as a side effect of the distro update.
    /// </summary>
    public Task<bool> UpdateDistroFromQuickstartAsync()
    {
        return RunSystemUpdateAsync(requireConfirmation: false, sourceLabel: "Quickstart");
    }

    /// <summary>
    /// Updates DwemerDistro and shared services without touching any application-server repository.
    /// Quickstart uses the same path without a second confirmation prompt.
    /// </summary>
    private async Task<bool> RunSystemUpdateAsync(
        bool requireConfirmation,
        string sourceLabel,
        CancellationToken cancellationToken = default)
    {
        if (!CanRunUpdateOperation())
        {
            AppendLog("Another server, component, or system operation is already running." + Environment.NewLine, "yellow");
            return false;
        }

        if (requireConfirmation &&
            MessageBox.Show(
                BuildSystemUpdateConfirmation(),
                "Update Distro",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            AppendLog("System update canceled." + Environment.NewLine);
            return false;
        }

        IsDistroUpdateInProgress = true;
        SetSystemUpdateRunningButtonText(sourceLabel.Equals("Quickstart", StringComparison.OrdinalIgnoreCase)
            ? "Quickstart Updating..."
            : "Updating Distro...");
        SetSystemUpdateState(SystemUpdateAvailability.Updating);

        try
        {
            AppendLog("Starting the DwemerDistro core and shared components update." + Environment.NewLine);
            await FlushUpdateUiAsync().ConfigureAwait(true);

            var succeeded = await RunSharedDistroUpdateAsync(cancellationToken).ConfigureAwait(true);
            AppendLog(
                succeeded
                    ? "System update completed successfully." + Environment.NewLine
                    : "System update may have encountered issues. Check the log above." + Environment.NewLine,
                succeeded ? "green" : "red");
            ReportSystemUpdateOutcome(succeeded);
            return succeeded;
        }
        catch (Exception ex)
        {
            AppendLog($"Error during system update: {ex.Message}{Environment.NewLine}", "red");
            ReportSystemUpdateOutcome(false);
            return false;
        }
        finally
        {
            CompleteUpdateOperation();
        }
    }

    // --- Automatic launcher-version system sync --------------------------------------------

    /// <summary>
    /// Runs the system-only update once after the launcher executable changes version, so a distro
    /// left behind by an older build is brought forward without the user having to find a button.
    /// No prompt is shown; the console carries the whole story. The marker is written only after a
    /// successful update, so a failed or blocked attempt simply retries on a later launch. Game
    /// server repositories are never touched: this reuses the same server-free system update.
    /// </summary>
    private async Task SyncLauncherVersionAsync(CancellationToken cancellationToken = default)
    {
        var currentVersion = SanitizeLauncherSyncVersion(LauncherConstants.LauncherVersion);
        if (currentVersion is null)
        {
            LauncherLogService.Startup("Launcher version sync skipped: the launcher version is not a plain version string.");
            return;
        }

        CommandResult marker;
        try
        {
            marker = await _wsl.RunBashAsync(
                    BuildLauncherSyncMarkerReadCommand(),
                    loginShell: false,
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LauncherLogService.Startup("Launcher version sync could not read its marker.", ex);
            return;
        }

        if (!marker.Succeeded)
        {
            // A distro that cannot answer has nothing to synchronize yet; first-time setup owns that case.
            LauncherLogService.Startup("Launcher version sync skipped: the distro did not answer.");
            return;
        }

        if (!ShouldSyncLauncherVersion(marker.StandardOutput, currentVersion))
        {
            LauncherLogService.Startup($"Launcher version sync skipped: {currentVersion} is already recorded.");
            return;
        }

        AppendLog(
            $"{Environment.NewLine}Launcher {currentVersion} has not updated this distro yet. " +
            $"Updating DwemerDistro and its shared components automatically. Installed mods are not changed.{Environment.NewLine}",
            "yellow");

        // Marshalled to the UI thread because the update drives button text, the operation lock, and
        // command states, exactly as the Components button does.
        var succeeded = await _dispatcher
            .InvokeAsync(() => RunSystemUpdateAsync(
                requireConfirmation: false,
                sourceLabel: "Launcher sync",
                cancellationToken))
            .Task
            .Unwrap()
            .ConfigureAwait(false);

        if (!succeeded)
        {
            AppendLog(
                "Automatic launcher update sync did not complete. It will run again on the next launch." + Environment.NewLine,
                "yellow");
            return;
        }

        CommandResult write;
        try
        {
            write = await _wsl.RunBashAsync(
                    BuildLauncherSyncMarkerWriteCommand(currentVersion),
                    loginShell: false,
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LauncherLogService.Startup("Launcher version sync could not record its marker.", ex);
            AppendLog(
                "Automatic launcher update sync finished, but it could not be recorded, so it runs again on the next launch." +
                Environment.NewLine,
                "yellow");
            return;
        }

        AppendLog(
            write.Succeeded
                ? $"Automatic launcher update sync finished. Launcher {currentVersion} will not repeat it." + Environment.NewLine
                : "Automatic launcher update sync finished, but it could not be recorded, so it runs again on the next launch." +
                  Environment.NewLine,
            write.Succeeded ? "green" : "yellow");
    }

    /// <summary>
    /// Only a plain dotted version may reach the marker command; anything else is refused rather than
    /// quoted into a shell.
    /// </summary>
    internal static string? SanitizeLauncherSyncVersion(string? version)
    {
        var trimmed = version?.Trim();
        if (string.IsNullOrEmpty(trimmed) || trimmed.Length > 32)
        {
            return null;
        }

        return trimmed.All(character => char.IsAsciiDigit(character) || character == '.') ? trimmed : null;
    }

    /// <summary>
    /// True when this launcher build has not recorded a successful sync yet. A missing, empty, or
    /// different marker all mean "run it once".
    /// </summary>
    internal static bool ShouldSyncLauncherVersion(string? recordedVersion, string currentVersion)
    {
        return !string.Equals(recordedVersion?.Trim(), currentVersion, StringComparison.Ordinal);
    }

    internal static string BuildLauncherSyncMarkerReadCommand()
    {
        return $"cat {LauncherSyncMarkerPath} 2>/dev/null || true";
    }

    internal static string BuildLauncherSyncMarkerWriteCommand(string version)
    {
        var safeVersion = SanitizeLauncherSyncVersion(version)
            ?? throw new ArgumentException("The launcher sync marker only accepts a plain version.", nameof(version));
        return $"mkdir -p /home/dwemer && printf '%s' '{safeVersion}' > {LauncherSyncMarkerPath}";
    }

    /// <summary>Prevents mod, component, and system operations from competing for the same WSL files.</summary>
    private bool CanRunUpdateOperation()
    {
        return CanRunUpdateOperation(
            IsDistroUpdateInProgress,
            _isComponentsOperationInProgress,
            _isExclusiveDistroOperationInProgress,
            ServerManagers.Select(manager => manager.IsBusy));
    }

    internal static bool CanRunUpdateOperation(
        bool isGlobalUpdateRunning,
        bool isComponentsOperationRunning,
        bool isExclusiveDistroOperationRunning,
        IEnumerable<bool> serverBusyStates)
    {
        return !isGlobalUpdateRunning &&
               !isComponentsOperationRunning &&
               !isExclusiveDistroOperationRunning &&
               !serverBusyStates.Any(isBusy => isBusy);
    }

    private bool CanRunExclusiveDistroOperation()
    {
        lock (_distroOperationGate)
        {
            return CanRunExclusiveDistroOperation(
                IsDistroUpdateInProgress,
                _isComponentsOperationInProgress,
                _isExclusiveDistroOperationInProgress,
                _quickstartDistroActivityCount > 0,
                _passiveDistroActivityCount > 0,
                IsServerStarting,
                ServerManagers.Select(manager => manager.IsBusy));
        }
    }

    internal static bool CanRunExclusiveDistroOperation(
        bool isGlobalUpdateRunning,
        bool isComponentsOperationRunning,
        bool isExclusiveDistroOperationRunning,
        bool isQuickstartDistroActivityRunning,
        bool isPassiveDistroActivityRunning,
        bool isServerStarting,
        IEnumerable<bool> serverBusyStates)
    {
        return !isServerStarting &&
               !isQuickstartDistroActivityRunning &&
               !isPassiveDistroActivityRunning &&
               CanRunUpdateOperation(
                   isGlobalUpdateRunning,
                   isComponentsOperationRunning,
                   isExclusiveDistroOperationRunning,
                   serverBusyStates);
    }

    /// <summary>
    /// Reports the shared gate refusing an operation the user already confirmed. The console is
    /// collapsed by default, so the answer has to be visible on its own.
    /// </summary>
    private void ReportExclusiveDistroOperationBusy(string title)
    {
        AppendLog(ExclusiveDistroOperationBusyMessage + Environment.NewLine, "yellow");
        RunOnUi(() => MessageBox.Show(
            ExclusiveDistroOperationBusyMessage + "\n\nWait for it to finish, then try again.",
            title,
            MessageBoxButton.OK,
            MessageBoxImage.Warning));
    }

    /// <summary>
    /// Waits for an already-running passive status check to finish. A passive check runs WSL
    /// commands, so one still in flight would reopen the distro between the stopped-state
    /// verification and DiskPart. New checks are already held off by the gate, so only the
    /// in-flight one has to drain, and the wait never blocks the UI thread it needs.
    /// </summary>
    private async Task<bool> WaitForPassiveDistroActivityIdleAsync()
    {
        if (!IsPassiveDistroActivityInProgress())
        {
            return true;
        }

        AppendLog("Waiting for the background status check to finish..." + Environment.NewLine);
        var deadline = DateTime.UtcNow + PassiveStatusDrainTimeout;
        while (IsPassiveDistroActivityInProgress())
        {
            if (DateTime.UtcNow >= deadline)
            {
                return false;
            }

            await Task.Delay(PassiveStatusDrainPollInterval).ConfigureAwait(false);
        }

        return true;
    }

    /// <summary>
    /// Atomically registers a passive refresh unless critical maintenance already owns the gate.
    /// This closes the check/set gap where a timer tick could start after compaction had begun.
    /// </summary>
    private bool TryBeginPassiveDistroActivity()
    {
        lock (_distroOperationGate)
        {
            if (_isExclusiveDistroOperationInProgress)
            {
                return false;
            }

            _passiveDistroActivityCount++;
        }

        NotifyDistroOperationGateChanged();
        return true;
    }

    private void EndPassiveDistroActivity()
    {
        lock (_distroOperationGate)
        {
            if (_passiveDistroActivityCount == 0)
            {
                return;
            }

            _passiveDistroActivityCount--;
        }

        NotifyDistroOperationGateChanged();
    }

    /// <summary>Registers the timer retry once while still counting it as passive distro work.</summary>
    private bool TryBeginServerStatusRetry()
    {
        lock (_distroOperationGate)
        {
            if (_isServerStatusRetryInProgress || _isExclusiveDistroOperationInProgress)
            {
                return false;
            }

            _isServerStatusRetryInProgress = true;
            _passiveDistroActivityCount++;
        }

        NotifyDistroOperationGateChanged();
        return true;
    }

    private void EndServerStatusRetry()
    {
        lock (_distroOperationGate)
        {
            if (!_isServerStatusRetryInProgress)
            {
                return;
            }

            _isServerStatusRetryInProgress = false;
            _passiveDistroActivityCount--;
        }

        NotifyDistroOperationGateChanged();
    }

    private bool IsPassiveDistroActivityInProgress()
    {
        lock (_distroOperationGate)
        {
            return _passiveDistroActivityCount > 0;
        }
    }

    private bool CanAccessDistro()
    {
        return !IsCriticalMaintenanceInProgress;
    }

    internal static string BuildModsUpdateConfirmation(
        IReadOnlyList<(ServerManagerItemViewModel Product, ServerBranchChannel Branch)> productsToUpdate,
        bool forceGitUpdates = false)
    {
        // If status changed after the button became available, describe the system-only recovery
        // accurately rather than promising a mod update that this empty selection cannot perform.
        if (productsToUpdate.Count == 0)
        {
            return "This will update DwemerDistro and its shared components only. " +
                   "The selected mod cannot currently be detected as installed, so it will not be updated.\n\n" +
                   "Missing mods are never installed.\n\n" +
                   "Are you sure?";
        }

        var branchLines = productsToUpdate
            .Select(update => $"{update.Product.DisplayName} target branch: {ServerManagementService.ToBranchChoice(update.Branch)}")
            .ToArray();

        // The setting is confirmed once in Settings but applies to every later update, so the
        // destructive part is restated here rather than relying on the user remembering it.
        var forceWarning = forceGitUpdates
            ? "\n\n" + ForceGitUpdatesUpdateWarning
            : string.Empty;

        return "This will update DwemerDistro and shared components first, then the selected installed mods below. " +
               "If the system update fails, no mods are updated.\n\n" +
               string.Join("\n", branchLines) +
               forceWarning +
               "\n\nAre you sure?";
    }

    /// <summary>
    /// The enable confirmation. It has to be exact about scope: forcing an update rewrites the files
    /// Git tracks, and leaves everything Git does not track - databases, uploads, profiles, memories
    /// and voices - untouched.
    /// </summary>
    internal static string BuildForceGitUpdatesConfirmation()
    {
        return $"{ForceGitUpdatesSettingName} is destructive.\n\n" +
               "While it is on, updating HerikaServer, StobeServer, or DialecticServer overwrites the files " +
               "those servers track in Git. Any edit you made to a tracked file by hand is permanently " +
               "discarded, with no undo.\n\n" +
               "Nothing untracked is removed: databases, uploads, profiles, memories, voices, and other " +
               "runtime data are left in place.\n\n" +
               "Leave this off unless an update keeps failing because an installed server has manual edits.\n\n" +
               $"Turn {ForceGitUpdatesSettingName} on?";
    }

    internal static string BuildSystemUpdateConfirmation()
    {
        return "This will update DwemerDistro and its shared components. Installed mods will not be changed.\n\n" +
               "Are you sure?";
    }

    private void CompleteUpdateOperation()
    {
        RunOnUi(() =>
        {
            IsDistroUpdateInProgress = false;
            SetSystemUpdateRunningButtonText(null);
        });
        QueueBackgroundTask("Installed server check", cancellationToken => RefreshServerManagementAsync(cancellationToken), StartupVersionCheckTimeout, accessesDistro: true);
        QueueBackgroundTask("Herika version check", cancellationToken => CheckForUpdatesAsync(cancellationToken), StartupVersionCheckTimeout, accessesDistro: true);
        QueueBackgroundTask("Stobe version check", cancellationToken => CheckStobeServerUpdatesAsync(cancellationToken), StartupVersionCheckTimeout, accessesDistro: true);
        QueueBackgroundTask("Dialectic version check", cancellationToken => CheckDialecticServerUpdatesAsync(cancellationToken), StartupVersionCheckTimeout, accessesDistro: true);
        QueueSystemUpdateCheck();
    }

    /// <summary>
    /// Pulls the distro scripts, runs update.sh, then runs update_gws with every application-server
    /// skip flag so only shared services are touched.
    /// </summary>
    private async Task<bool> RunSharedDistroUpdateAsync(CancellationToken cancellationToken = default)
    {
        var bashCommand = BuildSystemUpdateCommand();

        var sharedComponentsStarted = false;
        var result = await _wsl.RunBashAsync(bashCommand, line =>
        {
            if (line.Contains(SharedComponentsMarker, StringComparison.OrdinalIgnoreCase))
            {
                sharedComponentsStarted = true;
                AppendLog(Environment.NewLine + "Shared components update" + Environment.NewLine, "green");
                return;
            }

            AppendLog(line);
        }, loginShell: false, lineBuffered: true, cancellationToken: cancellationToken).ConfigureAwait(false);

        return result.Succeeded && sharedComponentsStarted;
    }

    /// <summary>Bootstraps the distro checkout when an empty-base installer ships source files without Git metadata.</summary>
    internal static string BuildSystemUpdateCommand()
    {
        return
            "export GIT_TERMINAL_PROMPT=0 && cd /home/dwemer/dwemerdistro && " +
            "if [ ! -d .git ]; then git init; fi && " +
            "if [ -f /home/dwemer/.gitconfig ] && ! command -v git-credential-manager >/dev/null 2>&1 && " +
            "git config --file /home/dwemer/.gitconfig --get-all credential.helper 2>/dev/null | grep -Fxq manager; then " +
            "git config --file /home/dwemer/.gitconfig --unset-all credential.helper '^manager$'; fi && " +
            $"if git remote get-url origin >/dev/null 2>&1; then git remote set-url origin {DistroRepositoryUrl}; " +
            $"else git remote add origin {DistroRepositoryUrl}; fi && " +
            "git -c credential.helper= fetch origin && git reset --hard origin/main && " +
            "chmod +x update.sh && echo 'dwemer' | sudo -S ./update.sh && " +
            $"echo '{SharedComponentsMarker}' && " + BuildSharedComponentsUpdateCommand() + " && " +
            BuildSystemReleaseMarkerWriteCommand();
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
        SetHerikaStatus("Checking...", "White", false);
        var currentBranch = await GetCurrentBranchAsync(cancellationToken).ConfigureAwait(false);
        var branchChoice = MapServerBranchToChoice(currentBranch, "aiagent");
        if (branchChoice is not null)
        {
            RunOnUi(() => TargetHerikaBranch = branchChoice);
        }

        var currentVersion = await ReadWslFileFirstLineAsync("/var/www/html/HerikaServer/.version.txt", cancellationToken).ConfigureAwait(false);
        var semanticVersion = await ReadWslFileFirstLineAsync("/var/www/html/HerikaServer/.version_number.txt", cancellationToken).ConfigureAwait(false);
        var gitVersion = currentBranch is null
            ? null
            : await GetTextOrNullAsync($"https://raw.githubusercontent.com/Dwemer-Dynamics/HerikaServer/{currentBranch}/.version.txt", cancellationToken).ConfigureAwait(false);

        if (!string.IsNullOrWhiteSpace(currentVersion) && !string.IsNullOrWhiteSpace(gitVersion))
        {
            // The only place a mod update is confirmed: the installed version is behind the branch
            // that is actually checked out. Every other arm below leaves the flag false.
            var updateAvailable = CompareVersions(currentVersion, gitVersion) < 0;
            SetHerikaStatus(
                BuildServerVersionStatusText(
                    "herika", currentBranch, FormatDateVersion(currentVersion), semanticVersion, updateAvailable),
                updateAvailable ? "Yellow" : "LimeGreen",
                updateAvailable);
        }
        else if (!string.IsNullOrWhiteSpace(currentVersion) || !string.IsNullOrWhiteSpace(semanticVersion))
        {
            SetHerikaStatus(
                BuildServerVersionStatusText(
                    "herika", currentBranch, FormatDateVersion(currentVersion), semanticVersion),
                "LimeGreen",
                false);
        }
        else
        {
            // Yellow here means "version unknown", not "behind" - so it must not turn a button green.
            SetHerikaStatus(BuildServerVersionStatusText("herika", currentBranch, null, null), "Yellow", false);
        }
    }

    private async Task CheckStobeServerUpdatesAsync(CancellationToken cancellationToken = default)
    {
        SetStobeStatus("Checking...", "White", false);
        var currentBranch = await GetStobeServerCurrentBranchAsync(cancellationToken).ConfigureAwait(false);
        var branchChoice = MapServerBranchToChoice(currentBranch, "stobe");
        if (branchChoice is not null)
        {
            RunOnUi(() => TargetStobeBranch = branchChoice);
        }

        var currentVersion = await ReadWslFileFirstLineAsync("/var/www/html/StobeServer/.version.txt", cancellationToken).ConfigureAwait(false);
        var semanticVersion =
            await ReadWslFileFirstLineAsync("/var/www/html/StobeServer/.version_number.txt", cancellationToken).ConfigureAwait(false) ??
            await ReadWslFileFirstLineAsync("/var/www/html/StobeServer/versionnumber.txt", cancellationToken).ConfigureAwait(false);
        var gitVersion = currentBranch is null
            ? null
            : await GetTextOrNullAsync($"https://raw.githubusercontent.com/Dwemer-Dynamics/StobeServer/{currentBranch}/.version.txt", cancellationToken).ConfigureAwait(false);

        if (!string.IsNullOrWhiteSpace(currentVersion) && !string.IsNullOrWhiteSpace(gitVersion))
        {
            var updateAvailable = CompareVersions(currentVersion, gitVersion) < 0;
            SetStobeStatus(
                BuildServerVersionStatusText(
                    "stobe", currentBranch, FormatDateVersion(currentVersion), semanticVersion, updateAvailable),
                updateAvailable ? "Yellow" : "LimeGreen",
                updateAvailable);
        }
        else if (!string.IsNullOrWhiteSpace(currentVersion) || !string.IsNullOrWhiteSpace(semanticVersion))
        {
            SetStobeStatus(
                BuildServerVersionStatusText(
                    "stobe", currentBranch, FormatDateVersion(currentVersion), semanticVersion),
                "LimeGreen",
                false);
        }
        else
        {
            SetStobeStatus(BuildServerVersionStatusText("stobe", currentBranch, null, null), "Yellow", false);
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

    private async Task LoadDashboardAutoOpenAsync(CancellationToken cancellationToken = default)
    {
        RunOnUi(() => SetDashboardAutoOpenStatus("Checking saved preference...", DashboardAutoOpenNeutralColor));

        CommandResult result;
        try
        {
            result = await _wsl.RunDistroAsUserAsync(
                "root",
                new[] { "bash", "-lc", $"mkdir -p /home/dwemer; if [ ! -f {DashboardAutoOpenFlagPath} ]; then echo 1 > {DashboardAutoOpenFlagPath}; fi; sed -n '1p' {DashboardAutoOpenFlagPath}" },
                cancellationToken: cancellationToken)
                .ConfigureAwait(false);
        }
        catch
        {
            ApplyDashboardAutoOpenLoadFallback();
            throw;
        }

        if (!result.Succeeded)
        {
            ApplyDashboardAutoOpenLoadFallback();
            throw new InvalidOperationException(
                string.IsNullOrWhiteSpace(result.StandardError)
                    ? "The dashboard auto-open preference could not be read."
                    : result.StandardError.Trim());
        }

        var enabled = result.StandardOutput.Trim() != "0";
        RunOnUi(() =>
        {
            _lastSavedDashboardAutoOpenEnabled = enabled;
            DashboardAutoOpenEnabled = enabled;
            _isDashboardAutoOpenReady = true;
            SetDashboardAutoOpenStatus(string.Empty, DashboardAutoOpenNeutralColor);
            SaveDashboardAutoOpenCommand.RaiseCanExecuteChanged();
        });
    }

    private void ApplyDashboardAutoOpenLoadFallback()
    {
        RunOnUi(() =>
        {
            _lastSavedDashboardAutoOpenEnabled = true;
            DashboardAutoOpenEnabled = true;
            _isDashboardAutoOpenReady = true;
            SetDashboardAutoOpenStatus(
                "Could not read the saved preference. Showing the default.",
                DashboardAutoOpenWarningColor);
            SaveDashboardAutoOpenCommand.RaiseCanExecuteChanged();
        });
    }

    private async Task SaveDashboardAutoOpenAsync()
    {
        var desired = DashboardAutoOpenEnabled;
        var value = desired ? "1" : "0";
        RunOnUi(() => SetDashboardAutoOpenStatus("Saving...", DashboardAutoOpenNeutralColor));

        CommandResult result;
        try
        {
            result = await _wsl.RunDistroAsUserAsync(
                "root",
                new[] { "bash", "-lc", $"mkdir -p /home/dwemer && echo {value} > {DashboardAutoOpenFlagPath}" })
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            RestoreDashboardAutoOpenAfterSaveFailure(ex.Message);
            return;
        }

        if (!result.Succeeded)
        {
            RestoreDashboardAutoOpenAfterSaveFailure(result.StandardError);
            return;
        }

        RunOnUi(() =>
        {
            _lastSavedDashboardAutoOpenEnabled = desired;
            SetDashboardAutoOpenStatus("Saved. Applies on the next server start.", DashboardAutoOpenSuccessColor);
        });
    }

    private void RestoreDashboardAutoOpenAfterSaveFailure(string? details)
    {
        RunOnUi(() =>
        {
            DashboardAutoOpenEnabled = _lastSavedDashboardAutoOpenEnabled;
            SetDashboardAutoOpenStatus("Could not save. Reverted to the last saved value.", DashboardAutoOpenErrorColor);
        });
        var reason = string.IsNullOrWhiteSpace(details) ? string.Empty : $": {details.Trim()}";
        AppendLog($"Failed to save Dwemer Dashboard auto-open setting{reason}{Environment.NewLine}", "red");
    }

    private void SetDashboardAutoOpenStatus(string text, string color)
    {
        DashboardAutoOpenStatusText = text;
        DashboardAutoOpenStatusColor = color;
    }

    /// <summary>
    /// Reads the saved Force Updates preference at startup. The service already fails closed, so
    /// a missing or malformed file simply leaves the option off and says so in the row.
    /// </summary>
    private void LoadForceGitUpdates()
    {
        var enabled = _updatePreferences.GetForceGitUpdates();
        _lastSavedForceGitUpdatesEnabled = enabled;
        ForceGitUpdatesEnabled = enabled;
        ApplyForceGitUpdatesSavedStatus(enabled);
    }

    /// <summary>
    /// The toggle rule, kept away from the dialog so it can be exercised without one: only turning
    /// the option on is destructive, so only that asks, and a cancelled prompt leaves it off.
    /// </summary>
    internal static bool ResolveForceGitUpdatesToggle(bool requestedEnabled, Func<bool> confirmEnable)
    {
        return requestedEnabled && confirmEnable();
    }

    /// <summary>
    /// Runs after the checkbox has already flipped itself, and is what puts it back: a cancelled
    /// enable reverts to unchecked and saves nothing, while any resolved state is persisted.
    /// </summary>
    private void ConfirmForceGitUpdates()
    {
        var desired = ResolveForceGitUpdatesToggle(ForceGitUpdatesEnabled, ConfirmForceGitUpdatesEnable);
        if (desired != ForceGitUpdatesEnabled)
        {
            ForceGitUpdatesEnabled = desired;
            ApplyForceGitUpdatesSavedStatus(_lastSavedForceGitUpdatesEnabled);
            AppendLog($"{ForceGitUpdatesSettingName} was left off.{Environment.NewLine}");
            return;
        }

        if (!_updatePreferences.TrySetForceGitUpdates(desired, out var error))
        {
            ForceGitUpdatesEnabled = _lastSavedForceGitUpdatesEnabled;
            SetForceGitUpdatesStatus(
                "Could not save. Reverted to the last saved value.",
                ForceGitUpdatesErrorColor);
            var reason = string.IsNullOrWhiteSpace(error) ? string.Empty : $": {error.Trim()}";
            AppendLog($"Failed to save the {ForceGitUpdatesSettingName} setting{reason}{Environment.NewLine}", "red");
            return;
        }

        _lastSavedForceGitUpdatesEnabled = desired;
        ApplyForceGitUpdatesSavedStatus(desired);
        AppendLog(
            desired
                ? $"{ForceGitUpdatesSettingName} is ON. Mod updates will discard manual edits to tracked files.{Environment.NewLine}"
                : $"{ForceGitUpdatesSettingName} is OFF.{Environment.NewLine}",
            desired ? "yellow" : "green");
    }

    private bool ConfirmForceGitUpdatesEnable()
    {
        return MessageBox.Show(
            BuildForceGitUpdatesConfirmation(),
            ForceGitUpdatesSettingName,
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning) == MessageBoxResult.Yes;
    }

    /// <summary>Reports the saved state, in a warning colour whenever the option is on.</summary>
    private void ApplyForceGitUpdatesSavedStatus(bool enabled)
    {
        SetForceGitUpdatesStatus(
            enabled
                ? "On. Mod updates discard manual edits to tracked files in the installed servers."
                : "Off. An update stops when an installed server has manual edits to tracked files.",
            enabled ? ForceGitUpdatesWarningColor : ForceGitUpdatesNeutralColor);
    }

    private void SetForceGitUpdatesStatus(string text, string color)
    {
        ForceGitUpdatesStatusText = text;
        ForceGitUpdatesStatusColor = color;
    }

    /// <summary>
    /// Marshals to the UI thread because the Compact Distro run awaits with
    /// <c>ConfigureAwait(false)</c> in places, so a stage can report from a pool thread.
    /// </summary>
    private void SetCompactDistroStatus(string text, string color)
    {
        RunOnUi(() =>
        {
            CompactDistroStatusText = text;
            CompactDistroStatusColor = color;
        });
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

        if (!TryBeginExclusiveDistroOperation())
        {
            ReportExclusiveDistroOperationBusy("Fix WSL DNS");
            return;
        }

        try
        {
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
        finally
        {
            SetExclusiveDistroOperationInProgress(false);
        }
    }

    private void OpenDistroDoctorWindow()
    {
        var window = new DistroDoctorWindow(new DistroDoctorViewModel(_wsl))
        {
            Owner = Application.Current.MainWindow
        };
        window.ShowDialog();
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

    private Task GenerateDiagnosticsAsync()
    {
        return GenerateDiagnosticsAsync(requireConfirmation: true, openOutputFolder: true);
    }

    // Creates the same support report for both the launcher UI and the headless CHIM command.
    internal async Task<string?> GenerateDiagnosticsAsync(bool requireConfirmation, bool openOutputFolder)
    {
        if (requireConfirmation)
        {
            var confirmed = MessageBox.Show(
                "The diagnostic file will include recent launcher output, service logs, LLM request/response logs, and local game plugin logs when available.\n\nContinue?",
                "Create Diagnostic File",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (confirmed != MessageBoxResult.Yes)
            {
                AppendLog("Diagnostic file creation canceled." + Environment.NewLine);
                return null;
            }
        }

        AppendLog("Generating diagnostic summary..." + Environment.NewLine);
        var lines = new List<string>
        {
            "DwemerDistro WPF Launcher Diagnostic Summary",
            $"Launcher Version: {LauncherConstants.LauncherVersion}",
            $"Generated: {DateTimeOffset.Now}",
            ""
        };

        await AddServerVersionDiagnosticsAsync(lines).ConfigureAwait(false);

        var diagnosticCommands = new List<(string Display, Func<Task<CommandResult>> Run)>
        {
            ("wsl -l -v", () => _wsl.RunWslAsync(new[] { "-l", "-v" }))
        };

        // Only probe repositories that exist. A git status against a missing optional server would
        // fill the report with errors that read like faults.
        foreach (var serverKey in new[] { "herika", "stobe", "dialectic" })
        {
            var config = GetRollbackServerConfig(serverKey);
            var manager = FindServerManagerByKey(config.Key);
            if (manager?.IsNotInstalled == true)
            {
                continue;
            }

            var repoPath = config.RepoPath;
            diagnosticCommands.Add((
                $"wsl -d {LauncherConstants.DistroName} -u {LauncherConstants.DistroUser} -- bash -lc \"cd {repoPath} && git status --short --branch\"",
                () => _wsl.RunBashAsync($"cd {repoPath} && git status --short --branch")));
        }

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

        var outputDir = DiagnosticReportPaths.OutputDirectory;
        Directory.CreateDirectory(outputDir);
        var outputPath = DiagnosticReportPaths.CreateTimestampedPath("diagnostics");
        await File.WriteAllLinesAsync(outputPath, lines).ConfigureAwait(false);
        AppendLog($"Diagnostic file created: {outputPath}{Environment.NewLine}", "green");
        if (openOutputFolder)
        {
            OpenFolder(outputDir);
        }

        return outputPath;
    }

    private async Task AddServerVersionDiagnosticsAsync(List<string> lines)
    {
        lines.Add("Installed Server Versions");
        lines.Add("Release metadata and exact Git commits for the currently installed servers.");
        lines.Add("");

        foreach (var serverKey in new[] { "herika", "stobe", "dialectic" })
        {
            var config = GetRollbackServerConfig(serverKey);
            lines.Add($"--- {config.DisplayName} ---");

            // An optional server that was never installed is a normal configuration, not a fault.
            // Say so plainly and skip the version/commit probes instead of logging them as failures.
            var manager = FindServerManagerByKey(config.Key);
            if (manager?.IsNotInstalled == true)
            {
                lines.Add("State: Not installed (optional server, nothing to report)");
                lines.Add("");
                continue;
            }

            if (manager?.NeedsRepair == true)
            {
                lines.Add("State: Needs repair (reported by ddistro_server status)");
            }

            try
            {
                var (dateVersion, dateVersionFile) = await ReadFirstServerVersionFileAsync(
                        config.RepoPath,
                        config.VersionTextFiles)
                    .ConfigureAwait(false);
                var (releaseVersion, releaseVersionFile) = await ReadFirstServerVersionFileAsync(
                        config.RepoPath,
                        config.VersionNumberFiles)
                    .ConfigureAwait(false);
                var commitResult = await _wsl.RunBashAsync(
                        $"git -C {EscapeForSingleQuotedBash(config.RepoPath)} rev-parse HEAD 2>/dev/null",
                        loginShell: false)
                    .ConfigureAwait(false);
                var gitCommit = commitResult.Succeeded && !string.IsNullOrWhiteSpace(commitResult.StandardOutput)
                    ? commitResult.StandardOutput.Trim()
                    : null;

                lines.Add($"Date Version: {dateVersion ?? "[missing or unavailable]"}{FormatVersionSource(dateVersionFile)}");
                lines.Add($"Release Version: {releaseVersion ?? "[missing or unavailable]"}{FormatVersionSource(releaseVersionFile)}");
                lines.Add($"Git Commit: {gitCommit ?? "[missing or unavailable]"}");
            }
            catch (Exception ex)
            {
                lines.Add($"[unavailable] {SanitizeDiagnosticText(ex.Message)}");
            }

            lines.Add("");
        }
    }

    private async Task<(string? Value, string? FileName)> ReadFirstServerVersionFileAsync(
        string repoPath,
        IEnumerable<string> fileNames)
    {
        foreach (var fileName in fileNames)
        {
            var value = await ReadWslFileFirstLineAsync($"{repoPath}/{fileName}").ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(value))
            {
                return (value, fileName);
            }
        }

        return (null, null);
    }

    private static string FormatVersionSource(string? fileName)
    {
        return string.IsNullOrWhiteSpace(fileName) ? string.Empty : $" ({fileName})";
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
        lines.Add("Log Diagnostics");
        lines.Add($"Each log section contains up to the last {MaxConsoleLines} lines.");
        lines.Add("Missing or unreadable logs are noted inline instead of failing diagnostic creation.");
        lines.Add("");

        AddLauncherSessionOutputDiagnostics(lines, MaxConsoleLines);
        await AddWslLogDiagnosticsAsync(lines, MaxConsoleLines).ConfigureAwait(false);
        AddLocalGameLogDiagnostics(lines, MaxConsoleLines);
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
            ("DialecticServer output_from_llm_fast", "/var/www/html/DialecticServer/log/output_from_llm_fast.log"),
            ("DialecticServer output_to_plugin", "/var/www/html/DialecticServer/log/output_to_plugin.log"),
            ("DialecticServer context_sent_to_llm", "/var/www/html/DialecticServer/log/context_sent_to_llm.log"),
            ("DialecticServer context_sent_to_llm_fast", "/var/www/html/DialecticServer/log/context_sent_to_llm_fast.log"),
            ("DialecticServer debugStream", "/var/www/html/DialecticServer/log/debugStream.log"),
            ("DialecticServer monitor", "/var/www/html/DialecticServer/log/monitor.log"),
            ("Apache error", "/var/log/apache2/error.log"),
            ("Apache vhost access", "/var/log/apache2/other_vhosts_access.log"),
            ("Dwemer Distro XTTS", "/home/dwemer/xtts-api-server/log.txt"),
            ("Chatterbox", "/home/dwemer/chatterbox/log.txt"),
            ("Pocket-TTS audio.cpp", "/home/dwemer/audio.cpp/server.log"),
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
                "port=$(tr -d '[:space:]' </home/dwemer/chatterbox/.dwemerdistro-port 2>/dev/null || echo 8020); " +
                "case \"$port\" in ''|*[!0-9]*) port=8020;; esac; " +
                "echo \"Configured port: $port\"; echo 'GET /provider_info'; curl -sS --max-time 5 http://127.0.0.1:$port/provider_info 2>&1 || true; " +
                "echo; echo 'GET /speakers_list_extended'; curl -sS --max-time 5 http://127.0.0.1:$port/speakers_list_extended 2>&1 || true; " +
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
            ("Dialectic Fallout New Vegas Plugin Log",
                BuildDialecticPluginLogCandidates()),
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

    private static string[] BuildDialecticPluginLogCandidates()
    {
        var candidates = new List<string>
        {
            @"%USERPROFILE%\Documents\My Games\FalloutNV\NVSE\dialectic.log",
            @"%USERPROFILE%\Documents\My Games\FalloutNV\dialectic.log",
            @"%ProgramFiles(x86)%\Steam\steamapps\common\Fallout New Vegas\dialectic.log",
            @"%ProgramFiles(x86)%\Steam\steamapps\common\Fallout New Vegas\Data\NVSE\Plugins\dialectic.log",
            @"%ProgramFiles%\Steam\steamapps\common\Fallout New Vegas\dialectic.log",
            @"%ProgramFiles%\Steam\steamapps\common\Fallout New Vegas\Data\NVSE\Plugins\dialectic.log",
            @"%ProgramFiles(x86)%\GOG Galaxy\Games\Fallout New Vegas\dialectic.log",
            @"%ProgramFiles(x86)%\GOG Galaxy\Games\Fallout New Vegas\Data\NVSE\Plugins\dialectic.log",
            @"%ProgramFiles%\GOG Galaxy\Games\Fallout New Vegas\dialectic.log",
            @"%ProgramFiles%\GOG Galaxy\Games\Fallout New Vegas\Data\NVSE\Plugins\dialectic.log",
            Path.GetFullPath("dialectic.log")
        };

        var globalInstances = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ModOrganizer");
        AddModOrganizerInstanceCandidates(candidates, globalInstances);

        foreach (var drive in DriveInfo.GetDrives().Where(drive => drive.DriveType == DriveType.Fixed))
        {
            AddModOrganizerInstanceCandidates(candidates, Path.Combine(drive.RootDirectory.FullName, "Modlists"));
        }

        foreach (var steamLibrary in GetSteamLibraryPaths())
        {
            var gameRoot = Path.Combine(steamLibrary, "steamapps", "common", "Fallout New Vegas");
            candidates.Add(Path.Combine(gameRoot, "dialectic.log"));
            candidates.Add(Path.Combine(gameRoot, "Data", "NVSE", "Plugins", "dialectic.log"));
        }

        return candidates
            .Select(Environment.ExpandEnvironmentVariables)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static void AddModOrganizerInstanceCandidates(List<string> candidates, string instancesRoot)
    {
        if (!Directory.Exists(instancesRoot))
        {
            return;
        }

        try
        {
            foreach (var instancePath in Directory.EnumerateDirectories(instancesRoot))
            {
                candidates.Add(Path.Combine(instancePath, "overwrite", "Root", "dialectic.log"));
                candidates.Add(Path.Combine(instancePath, "overwrite", "dialectic.log"));
                foreach (var modName in new[] { "Dialectic_dev", "Dialectic" })
                {
                    candidates.Add(Path.Combine(instancePath, "mods", modName, "dialectic.log"));
                    candidates.Add(Path.Combine(instancePath, "mods", modName, "NVSE", "Plugins", "dialectic.log"));
                    candidates.Add(Path.Combine(instancePath, "mods", modName, "Data", "NVSE", "Plugins", "dialectic.log"));
                }
            }
        }
        catch (UnauthorizedAccessException)
        {
            // Unreadable MO2 roots remain visible in the diagnostic candidate list through known paths.
        }
        catch (IOException)
        {
            // A disconnected or changing drive should not prevent diagnostic creation.
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

    private static string UsePostgresPeerAuthentication(string command)
    {
        return command
            .Replace("export PGPASSWORD=dwemer", "unset PGPASSWORD", StringComparison.Ordinal)
            .Replace("psql -h localhost -U dwemer", "psql -h /var/run/postgresql -U postgres", StringComparison.Ordinal);
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

        command = UsePostgresPeerAuthentication(command);
        lines.Add("$ wsl database schema diagnostics");
        try
        {
            var result = await _wsl.RunBashAsync(
                command, user: "postgres", loginShell: false).ConfigureAwait(false);
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

        command = UsePostgresPeerAuthentication(command);
        lines.Add("$ wsl connector diagnostics");
        try
        {
            var result = await _wsl.RunBashAsync(
                command, user: "postgres", loginShell: false).ConfigureAwait(false);
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

    private async Task CompactDistroAsync()
    {
        if (!CanRunExclusiveDistroOperation())
        {
            ReportExclusiveDistroOperationBusy(CompactDistroSettingName);
            return;
        }

        if (!await _wsl.DistroExistsAsync().ConfigureAwait(false))
        {
            SetCompactDistroStatus(
                $"{LauncherConstants.DistroName} is not installed, so there is nothing to compact.",
                CompactDistroWarningColor);
            MessageBox.Show(
                $"{LauncherConstants.DistroName} is not currently installed.",
                CompactDistroSettingName,
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        // Defaults to No because this stops every WSL distro on the machine.
        var confirmed = MessageBox.Show(
            BuildCompactDistroConfirmation(),
            CompactDistroSettingName,
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);

        if (confirmed != MessageBoxResult.Yes)
        {
            SetCompactDistroStatus("Canceled. Nothing was deleted or changed.", CompactDistroNeutralColor);
            AppendLog("Compact Distro canceled." + Environment.NewLine);
            return;
        }

        var serverStopWasAttempted = false;
        var serverWasStopped = false;
        if (!TryBeginExclusiveDistroOperation())
        {
            SetCompactDistroStatus(ExclusiveDistroOperationBusyMessage, CompactDistroWarningColor);
            ReportExclusiveDistroOperationBusy(CompactDistroSettingName);
            return;
        }

        // The row says it is working before the first WSL round trip, so an earlier run's terminal
        // status can never be mistaken for this one's result.
        SetCompactDistroStatus(CompactDistroPreparingStatus, CompactDistroBusyColor);
        try
        {
            var vhdxPath = NormalizeDistroVhdxPath(_wsl.GetDistroVhdxPath());
            if (vhdxPath is null || !File.Exists(vhdxPath))
            {
                SetCompactDistroStatus(
                    "The distro disk file could not be found. Nothing was deleted or stopped.",
                    CompactDistroErrorColor);
                MessageBox.Show(
                    "The launcher could not safely locate the distro's disk file. Nothing was deleted or stopped.\n\n" +
                    "Restart Windows and try again. If the problem continues, run Update Distro first.",
                    CompactDistroSettingName,
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                return;
            }

            var beforeSnapshot = TryGetFileProgressSnapshot(vhdxPath);
            AppendLog($"Detected distro VHDX: {vhdxPath}{Environment.NewLine}");
            if (beforeSnapshot is not null)
            {
                AppendLog($"Current VHDX size: {FormatByteSize(beforeSnapshot.Value.Length)}{Environment.NewLine}");
            }

            var cleanupProbe = await _wsl.RunBashAsync(DistroStorageProbeCommand, user: "root").ConfigureAwait(true);
            if (!cleanupProbe.Succeeded)
            {
                SetCompactDistroStatus("Update Distro first, then run Compact Distro again.", CompactDistroWarningColor);
                MessageBox.Show(
                    "This installation does not have the safe storage cleanup tool yet.\n\n" +
                    "Run Update Distro, then run Compact Distro again. Nothing was deleted or stopped.",
                    CompactDistroSettingName,
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            var usedBytesBeforeCleanup = await ReadDistroUsedBytesAsync().ConfigureAwait(true);
            SetCompactDistroStatus(CompactDistroCleaningCachesStatus, CompactDistroBusyColor);
            AppendLog("Removing reproducible installer caches..." + Environment.NewLine);
            var cleanupResult = await _wsl.RunBashAsync(
                    DistroStorageCleanupCommand,
                    text => AppendLog(text),
                    user: "root")
                .ConfigureAwait(true);
            if (!cleanupResult.Succeeded)
            {
                var installerActive = cleanupResult.ExitCode == 3;
                var cleanupError = GetCommandError(cleanupResult);
                AppendLog($"Safe storage cleanup stopped: {cleanupError}{Environment.NewLine}", "yellow");
                SetCompactDistroStatus(
                    installerActive
                        ? "A component installer is still running. Wait for it to finish and try again."
                        : "Safe cache cleanup could not finish. Nothing was stopped or compacted.",
                    installerActive ? CompactDistroWarningColor : CompactDistroErrorColor);
                MessageBox.Show(
                    installerActive
                        ? "A component installer is still running. Wait for it to finish, then run Compact Distro again.\n\nNothing was stopped or compacted."
                        : $"Safe cache cleanup could not finish.\n\n{cleanupError}\n\nNothing was stopped or compacted.",
                    CompactDistroSettingName,
                    MessageBoxButton.OK,
                    installerActive ? MessageBoxImage.Warning : MessageBoxImage.Error);
                return;
            }

            var usedBytesAfterCleanup = await ReadDistroUsedBytesAsync().ConfigureAwait(true);
            var cleanedBytes = CalculateReclaimedBytes(usedBytesBeforeCleanup, usedBytesAfterCleanup);

            SetCompactDistroStatus(CompactDistroFreeingSpaceStatus, CompactDistroBusyColor);
            AppendLog("Marking unused distro space as free..." + Environment.NewLine);
            var trimResult = await _wsl.RunBashAsync("fstrim -v /", text => AppendLog(text), user: "root").ConfigureAwait(true);
            if (!trimResult.Succeeded)
            {
                var trimError = GetCommandError(trimResult);
                AppendLog($"Filesystem trim failed: {trimError}{Environment.NewLine}", "red");
                SetCompactDistroStatus(
                    "Installer caches were removed, but the freed space could not be prepared for Windows. The server is still running if it was running before.",
                    CompactDistroWarningColor);
                MessageBox.Show(
                    $"Installer caches were removed, but the freed space could not be prepared for Windows.\n\n{trimError}\n\n" +
                    "The server and WSL were not stopped. Run Compact Distro again after resolving the error.",
                    CompactDistroSettingName,
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            AppendLog("Unused distro space is ready for Windows compaction." + Environment.NewLine, "green");
            SetCompactDistroStatus(CompactDistroStoppingWslStatus, CompactDistroBusyColor);
            serverStopWasAttempted = true;
            await PrepareDistroForSafeCompactionAsync().ConfigureAwait(true);
            serverWasStopped = true;

            var stoppedVhdxPath = NormalizeDistroVhdxPath(_wsl.GetDistroVhdxPath());
            if (stoppedVhdxPath is null ||
                !File.Exists(stoppedVhdxPath) ||
                !string.Equals(vhdxPath, stoppedVhdxPath, StringComparison.OrdinalIgnoreCase))
            {
                AppendLog("The registered distro disk path changed or disappeared after WSL shutdown. Windows compaction was skipped." + Environment.NewLine, "yellow");
                SetCompactDistroStatus(
                    "Installer caches were removed, but the distro disk could not be verified for Windows compaction. The server is stopped.",
                    CompactDistroWarningColor);
                MessageBox.Show(
                    "Installer caches were removed and the space was freed inside the distro, but the disk file could not be safely verified after WSL stopped. No Windows compaction was attempted.\n\n" +
                    "Start the server again when you're ready.",
                    CompactDistroSettingName,
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            // Nothing may reopen the distro between the stopped-state verification and DiskPart.
            // A passive status check that was already in flight when the gate closed is the one
            // thing that still could, so it is drained and the stopped state is confirmed again.
            if (!await WaitForPassiveDistroActivityIdleAsync().ConfigureAwait(true) ||
                await _wsl.DistroRunningAsync().ConfigureAwait(true))
            {
                AppendLog(
                    $"{LauncherConstants.DistroName} reopened after it was stopped. Windows compaction was skipped.{Environment.NewLine}",
                    "yellow");
                SetCompactDistroStatus(
                    "Installer caches were removed, but the distro reopened before Windows could compact it. Run Compact Distro again.",
                    CompactDistroWarningColor);
                MessageBox.Show(
                    "Installer caches were removed and the space was freed inside the distro, but the distro reopened before Windows compaction could start. No compaction was attempted.\n\n" +
                    $"Close any open \\\\wsl.localhost\\{LauncherConstants.DistroName} Explorer windows and run Compact Distro again.",
                    CompactDistroSettingName,
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            SetCompactDistroStatus(CompactDistroCompactingStatus, CompactDistroBusyColor);
            AppendLog("Running elevated Windows VHDX compact..." + Environment.NewLine);
            var compactResult = await CompactVhdxAsync(stoppedVhdxPath).ConfigureAwait(true);
            if (!compactResult.Succeeded)
            {
                var compactError = GetCommandError(compactResult);
                var elevationDeclined = compactResult.ExitCode == 1223;
                AppendLog($"Disk compaction failed: {compactError}{Environment.NewLine}", elevationDeclined ? "yellow" : "red");
                SetCompactDistroStatus(
                    elevationDeclined
                        ? "Stopped at the Windows administrator prompt. Space was freed inside the distro but not handed back to Windows. The server is stopped."
                        : "Space was freed inside the distro, but Windows could not reclaim it. The server is stopped.",
                    elevationDeclined ? CompactDistroWarningColor : CompactDistroErrorColor);
                MessageBox.Show(
                    (elevationDeclined
                        ? "The administrator prompt was not approved, so the freed space was not handed back to Windows."
                        : "Windows could not reclaim the freed space.") +
                    $"\n\nSpace was still freed inside the distro.\n\n{compactError}\n\nStart the server again when you're ready.",
                    CompactDistroSettingName,
                    MessageBoxButton.OK,
                    elevationDeclined ? MessageBoxImage.Warning : MessageBoxImage.Error);
                return;
            }

            var afterSnapshot = TryGetFileProgressSnapshot(stoppedVhdxPath);
            var summary = BuildCompactDistroSummary(beforeSnapshot, afterSnapshot, cleanedBytes);
            AppendLog(summary + Environment.NewLine, "green");

            SetCompactDistroStatus($"Done. {summary} The server is stopped.", CompactDistroSuccessColor);
            MessageBox.Show(
                $"Compact Distro finished.\n\n{summary}\n\nStart the server again when you're ready.",
                CompactDistroSettingName,
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            AppendLog($"Compact Distro error: {ex.Message}{Environment.NewLine}", "red");
            SetCompactDistroStatus(
                serverWasStopped
                    ? "Could not finish. The server is stopped. Open the console for details."
                    : serverStopWasAttempted
                        ? "Could not finish. The server may be stopped. Open the console for details."
                        : "Could not finish. The server and WSL were not stopped.",
                CompactDistroErrorColor);
            MessageBox.Show(
                $"Compact Distro could not finish.\n\n{ex.Message}\n\n" +
                (serverWasStopped
                    ? "The server is stopped. Start it again when you're ready."
                    : serverStopWasAttempted
                        ? "The server may be stopped. Check it before playing."
                        : "The server and WSL were not stopped."),
                CompactDistroSettingName,
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            SetExclusiveDistroOperationInProgress(false);
            // Passive checks were held off for the whole run. Restarting the timer lets an unknown
            // status be re-read on its own schedule instead of forcing the distro back open here.
            QueueServerStatusRefresh();
        }
    }

    internal static string BuildCompactDistroConfirmation()
    {
        return "Compact Distro frees up Windows disk space in three steps:\n\n" +
               "1. Delete installer caches the launcher can download again.\n" +
               "2. Release that space inside the distro.\n" +
               "3. Hand it back to Windows.\n\n" +
               "Your mods, installed servers and components, models, voices, databases, settings, logs, " +
               "and Hugging Face sign-in are kept.\n\n" +
               $"Before it starts, the {LauncherConstants.DistroName} server and every other running WSL " +
               "distribution are stopped, and Windows asks you to approve an administrator prompt. " +
               $"Close any open \\\\wsl.localhost\\{LauncherConstants.DistroName} Explorer windows first.\n\n" +
               "This can take a few minutes, and the server stays stopped when it finishes.\n\nContinue?";
    }

    private async Task PrepareDistroForSafeCompactionAsync()
    {
        // A passive status check that started before the gate closed runs WSL commands of its own,
        // so it has to finish before the stop sequence begins.
        if (!await WaitForPassiveDistroActivityIdleAsync().ConfigureAwait(true))
        {
            throw new InvalidOperationException(
                "A background status check is still running, so Windows compaction was not attempted.");
        }

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

        if (await _wsl.DistroRunningAsync().ConfigureAwait(false))
        {
            throw new InvalidOperationException(
                $"{LauncherConstants.DistroName} is still running, so Windows compaction was not attempted.");
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

        if (!TryBeginExclusiveDistroOperation())
        {
            ReportExclusiveDistroOperationBusy("Export Full Distro");
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
            SetExclusiveDistroOperationInProgress(false);
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

        if (!TryBeginExclusiveDistroOperation())
        {
            ReportExclusiveDistroOperationBusy("Import Full Distro");
            return;
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
            SetExclusiveDistroOperationInProgress(false);
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
        // A passive check runs WSL commands, which would reopen the distro a critical operation
        // just stopped. Callers re-queue once the gate reopens.
        if (IsCriticalMaintenanceInProgress)
        {
            return;
        }

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
        if (!NeedsServerStatusRefresh())
        {
            RunOnUi(() => _serverStatusRetryTimer.Stop());
            return;
        }

        if (!TryBeginServerStatusRetry())
        {
            if (IsCriticalMaintenanceInProgress)
            {
                RunOnUi(() => _serverStatusRetryTimer.Stop());
            }

            return;
        }

        try
        {
            using var timeoutCts = new CancellationTokenSource(StartupVersionCheckTimeout);
            await RefreshServerManagementAsync(timeoutCts.Token).ConfigureAwait(false);
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
            EndServerStatusRetry();
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
        vhdxPath = NormalizeDistroVhdxPath(vhdxPath)
            ?? throw new InvalidOperationException("The distro disk path is not safe to pass to Windows compaction.");
        if (!File.Exists(vhdxPath))
        {
            throw new FileNotFoundException("The distro disk file disappeared before Windows compaction.", vhdxPath);
        }

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

    private async Task<long?> ReadDistroUsedBytesAsync()
    {
        var result = await _wsl.RunBashAsync(
                "df -B1 --output=used / | tail -n 1",
                user: "root")
            .ConfigureAwait(false);
        return result.Succeeded ? ParseDistroUsedBytes(result.StandardOutput) : null;
    }

    internal static long? ParseDistroUsedBytes(string? output)
    {
        var value = output?
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .LastOrDefault();
        return long.TryParse(value, out var bytes) && bytes >= 0 ? bytes : null;
    }

    internal static long? CalculateReclaimedBytes(long? beforeBytes, long? afterBytes)
    {
        return beforeBytes is not null && afterBytes is not null && beforeBytes >= afterBytes
            ? beforeBytes.Value - afterBytes.Value
            : null;
    }

    internal static string? NormalizeDistroVhdxPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) ||
            path.Contains('"', StringComparison.Ordinal) ||
            path.Any(char.IsControl))
        {
            return null;
        }

        try
        {
            var fullPath = Path.GetFullPath(path);
            return Path.IsPathFullyQualified(fullPath) &&
                   string.Equals(Path.GetExtension(fullPath), ".vhdx", StringComparison.OrdinalIgnoreCase)
                ? fullPath
                : null;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    private static string BuildCompactDistroSummary(
        FileProgressSnapshot? beforeSnapshot,
        FileProgressSnapshot? afterSnapshot,
        long? cleanedBytes)
    {
        var cleanupSummary = cleanedBytes is > 0
            ? $"Removed {FormatByteSize(cleanedBytes.Value)} of reproducible caches. "
            : string.Empty;

        if (beforeSnapshot is not null && afterSnapshot is not null)
        {
            var reclaimedBytes = beforeSnapshot.Value.Length - afterSnapshot.Value.Length;
            return cleanupSummary + (reclaimedBytes > 0
                ? $"Returned {FormatByteSize(reclaimedBytes)} to Windows. The distro now uses {FormatByteSize(afterSnapshot.Value.Length)}, down from {FormatByteSize(beforeSnapshot.Value.Length)}."
                : $"Windows did not reduce the distro disk file further. It uses {FormatByteSize(afterSnapshot.Value.Length)}.");
        }

        if (afterSnapshot is not null)
        {
            return cleanupSummary + $"Returned unused space to Windows. The distro now uses {FormatByteSize(afterSnapshot.Value.Length)}.";
        }

        return cleanupSummary + "Returned unused space to Windows.";
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
        // An unanswered install-state probe matters as much as a missing version: without it the
        // Mods page cannot tell "not installed" from "not checked yet".
        return ServerManagers.Any(manager => !manager.IsStatusKnown) ||
               StatusNeedsRefresh(_herikaStatusText) ||
               StatusNeedsRefresh(_stobeStatusText) ||
               StatusNeedsRefresh(_dialecticStatusText);
    }

    private static bool StatusNeedsRefresh(string text)
    {
        return text.Contains("Checking...", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("[N/A]", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The one status line both the mod control menu and the 96px rail tile show. The
    /// "Update Available" suffix rides the existing " | " separator after the version fields, so
    /// the line only grows when the branch comparison actually confirmed a newer version - never
    /// for an unknown or missing one.
    /// </summary>
    internal static string BuildServerVersionStatusText(
        string serviceName,
        string? branch,
        string? dateVersion,
        string? semanticVersion,
        bool updateAvailable = false)
    {
        var source = string.IsNullOrWhiteSpace(branch) ? serviceName : branch.Trim();
        var text = $"{source} | {dateVersion ?? "N/A"} | {semanticVersion ?? "N/A"}";
        return updateAvailable ? $"{text} | {UpdateAvailableStatusSuffix}" : text;
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
                window.Closed += (_, _) =>
                {
                    _firstRunSetupWindow = null;
                    QueueBackgroundTask(
                        "QuickStart server status refresh",
                        cancellationToken => RefreshServerManagementAsync(cancellationToken),
                        StartupVersionCheckTimeout,
                        accessesDistro: true);
                };
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
        SetDialecticStatus("Checking...", "White", false);
        var currentBranch = await GetDialecticServerCurrentBranchAsync(cancellationToken).ConfigureAwait(false);
        var branchChoice = MapServerBranchToChoice(currentBranch, "dialectic");
        if (branchChoice is not null)
        {
            RunOnUi(() => TargetDialecticBranch = branchChoice);
        }

        var currentVersion = await ReadWslFileFirstLineAsync("/var/www/html/DialecticServer/.version.txt", cancellationToken).ConfigureAwait(false);
        var semanticVersion = await ReadWslFileFirstLineAsync("/var/www/html/DialecticServer/.version_number.txt", cancellationToken).ConfigureAwait(false);
        var gitVersion = currentBranch is null
            ? null
            : await GetTextOrNullAsync($"https://raw.githubusercontent.com/Dwemer-Dynamics/DialecticServer/{currentBranch}/.version.txt", cancellationToken).ConfigureAwait(false);

        if (!string.IsNullOrWhiteSpace(currentVersion) && !string.IsNullOrWhiteSpace(gitVersion))
        {
            var updateAvailable = CompareVersions(currentVersion, gitVersion) < 0;
            SetDialecticStatus(
                BuildServerVersionStatusText(
                    "dialectic", currentBranch, FormatDateVersion(currentVersion), semanticVersion, updateAvailable),
                updateAvailable ? "Yellow" : "LimeGreen",
                updateAvailable);
        }
        else if (!string.IsNullOrWhiteSpace(currentVersion) || !string.IsNullOrWhiteSpace(semanticVersion))
        {
            SetDialecticStatus(
                BuildServerVersionStatusText(
                    "dialectic", currentBranch, FormatDateVersion(currentVersion), semanticVersion),
                "LimeGreen",
                false);
        }
        else
        {
            SetDialecticStatus(BuildServerVersionStatusText("dialectic", currentBranch, null, null), "Yellow", false);
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

            QueueBackgroundTask("Herika version check", cancellationToken => CheckForUpdatesAsync(cancellationToken), StartupVersionCheckTimeout, accessesDistro: true);
            QueueBackgroundTask("Stobe version check", cancellationToken => CheckStobeServerUpdatesAsync(cancellationToken), StartupVersionCheckTimeout, accessesDistro: true);
            QueueBackgroundTask("Dialectic version check", cancellationToken => CheckDialecticServerUpdatesAsync(cancellationToken), StartupVersionCheckTimeout, accessesDistro: true);
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
                """
                if [ ! -x /usr/local/bin/install_cuda_dependencies ]; then
                    echo "The minimal CUDA installer is missing. Update DwemerDistro and retry."
                    exit 23
                fi
                /usr/local/bin/install_cuda_dependencies auto
                """,
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
            "audiocpp" => new(
                "audiocpp",
                "Pocket-TTS (GPU / audio.cpp)",
                """
                if [ ! -s /home/dwemer/.cache/huggingface/token ]; then
                    echo "Hugging Face token is required for Pocket-TTS. Save it in Quickstart and retry."
                    exit 22
                fi
                if [ ! -x /usr/local/bin/install_audiocpp_pockettts ]; then
                    echo "The audio.cpp Pocket-TTS installer is missing. Update DwemerDistro and retry."
                    exit 23
                fi
                BUILD_PARALLEL="${BUILD_PARALLEL:-2}" /usr/local/bin/install_audiocpp_pockettts auto
                ln -sfn /home/dwemer/audio.cpp/start-audiocpp-pockettts.sh /home/dwemer/audio.cpp/start.sh
                chown -h dwemer:dwemer /home/dwemer/audio.cpp/start.sh
                rm -f /home/dwemer/pocket-tts/start.sh
                """,
                "[ -x /home/dwemer/audio.cpp/build/bin/audiocpp_server ] && [ -e /home/dwemer/audio.cpp/start.sh ]",
                "root"),
            "pockettts" => new(
                "pockettts",
                "Pocket-TTS (CPU / Python)",
                """
                if [ ! -s /home/dwemer/.cache/huggingface/token ]; then
                    echo "Hugging Face token is required for Pocket-TTS. Save it in Quickstart and retry."
                    exit 22
                fi
                export PIP_NO_INPUT=1
                export PIP_DISABLE_PIP_VERSION_CHECK=1
                export PIP_NO_CACHE_DIR=1
                cd /home/dwemer
                POCKETTTS_FRESH=0
                if [ ! -d pocket-tts/venv ]; then
                    POCKETTTS_FRESH=1
                fi
                if [ ! -d pocket-tts/.git ]; then
                    rm -rf pocket-tts
                    git clone --depth 1 https://github.com/Dwemer-Dynamics/pocket-tts pocket-tts
                else
                    git -C pocket-tts pull --ff-only
                fi
                cd /home/dwemer/pocket-tts
                if [ ! -f .dwemerdistro-port ]; then
                    if [ "$POCKETTTS_FRESH" -eq 1 ]; then
                        printf '8024\n' > .dwemerdistro-port
                    else
                        printf '8020\n' > .dwemerdistro-port
                    fi
                fi
                if [ ! -d venv ]; then
                    python3 -m venv venv
                fi
                . venv/bin/activate
                python -m pip install --no-cache-dir --upgrade pip wheel setuptools
                list_cuda_only_packages() {
                    python -m pip list --format=freeze |
                        sed -n 's/^\(cuda-bindings\|cuda-pathfinder\|cuda-toolkit\|nvidia-[^=]*\|triton\)==.*$/\1/p'
                }
                remove_cuda_only_packages() {
                    local packages
                    mapfile -t packages < <(list_cuda_only_packages)
                    if [ "${#packages[@]}" -gt 0 ]; then
                        python -m pip uninstall -y "${packages[@]}"
                    fi
                }
                # A CUDA Torch wheel can carry the same public version as the CPU wheel, so
                # pip is allowed to keep it on --upgrade. Remove Torch together with its
                # CUDA-only packages first; sweeping those out from under an installed CUDA
                # Torch leaves the venv broken.
                if python -m pip list --format=freeze | grep -q '^torch==.*+cu' || [ -n "$(list_cuda_only_packages)" ]; then
                    python -m pip uninstall -y torch
                    remove_cuda_only_packages
                fi
                python -m pip install --no-cache-dir --upgrade torch --index-url https://download.pytorch.org/whl/cpu
                remove_cuda_only_packages
                python -m pip install --no-cache-dir -e .
                ln -sfn /home/dwemer/pocket-tts/start-cpu.sh /home/dwemer/pocket-tts/start.sh
                rm -f /home/dwemer/audio.cpp/start.sh
                """,
                "[ -x /home/dwemer/pocket-tts/venv/bin/python ] && [ -e /home/dwemer/pocket-tts/start.sh ]"),
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
                    git clone --depth 1 https://github.com/Dwemer-Dynamics/omnivoice-tts omnivoice-tts
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
                "/usr/local/bin/install_mimic3",
                "[ -x /home/dwemer/python-mimic3/bin/python ]"),
            "parakeet" => new(
                "parakeet",
                "Parakeet STT",
                """
                cd /home/dwemer
                if [ ! -d parakeet-api-server/.git ]; then
                    rm -rf parakeet-api-server
                    git clone --depth 1 https://github.com/Dwemer-Dynamics/parakeet-api-server parakeet-api-server
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
        export PIP_NO_CACHE_DIR=1
        export PIP_DISABLE_PIP_VERSION_CHECK=1
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

        if command -v ddistro_storage >/dev/null 2>&1; then
            echo
            echo "Removing reproducible installer downloads..."
            set +e
            if [ "$(id -u)" -eq 0 ]; then
                ddistro_storage safe-cleanup
            elif sudo -n true >/dev/null 2>&1; then
                sudo -n ddistro_storage safe-cleanup
            else
                printf '%s\n' dwemer | sudo -S -p '' ddistro_storage safe-cleanup
            fi
            cleanup_status=$?
            set -e
            if [ "$cleanup_status" -ne 0 ]; then
                echo "Storage cleanup was skipped; the component result is unchanged."
            fi
        else
            rm -rf /home/dwemer/.cache/pip /home/dwemer/.cache/uv /home/dwemer/.npm/_cacache
        fi

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

    private void SetHerikaStatus(string text, string color, bool updateAvailable)
    {
        RunOnUi(() =>
        {
            HerikaStatusText = text;
            HerikaStatusColor = color;
            ApplyVersionStatusToManager(ServerProduct.Herika, text, color, updateAvailable);
        });
    }

    private void SetStobeStatus(string text, string color, bool updateAvailable)
    {
        RunOnUi(() =>
        {
            StobeStatusText = text;
            StobeStatusColor = color;
            ApplyVersionStatusToManager(ServerProduct.Stobe, text, color, updateAvailable);
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
            AppendSanitizedLog(sanitized);
            return;
        }

        _ = _dispatcher.BeginInvoke(() => AppendSanitizedLog(sanitized), DispatcherPriority.Background);
    }

    private void AppendSanitizedLog(string sanitized)
    {
        var appended = OutputText + sanitized;
        var bounded = TrimConsoleOutput(appended);
        if (!string.Equals(appended, bounded, StringComparison.Ordinal))
        {
            _outputGeneration++;
        }

        OutputText = bounded;
    }

    // Keep long-running server sessions bounded while preserving the newest complete output lines.
    internal static string TrimConsoleOutput(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return value;
        }

        var lineCount = value[^1] == '\n' ? 0 : 1;
        foreach (var character in value)
        {
            if (character == '\n')
            {
                lineCount++;
            }
        }

        if (lineCount <= MaxConsoleLines)
        {
            return value;
        }

        var linesToSkip = lineCount - MaxConsoleLines;
        var retainedStart = 0;
        for (var skipped = 0; skipped < linesToSkip; skipped++)
        {
            var newlineIndex = value.IndexOf('\n', retainedStart);
            if (newlineIndex < 0)
            {
                return value;
            }

            retainedStart = newlineIndex + 1;
        }

        return ConsoleTrimNotice + Environment.NewLine + value[retainedStart..];
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
        ForceStopServerCommand.RaiseCanExecuteChanged();
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

    public static string ResolveServerBranchChoice(string? choice, string mainBranch)
    {
        return string.Equals(choice?.Trim(), "Dev", StringComparison.OrdinalIgnoreCase)
            ? "dev"
            : mainBranch;
    }

    public static string? MapServerBranchToChoice(string? branch, string mainBranch)
    {
        if (string.Equals(branch?.Trim(), mainBranch, StringComparison.OrdinalIgnoreCase))
        {
            return "Main";
        }

        return branch?.Trim().ToLowerInvariant() is "dev" or "unstable"
            ? "Dev"
            : null;
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

    private void SetDialecticStatus(string text, string color, bool updateAvailable)
    {
        RunOnUi(() =>
        {
            DialecticStatusText = text;
            DialecticStatusColor = color;
            ApplyVersionStatusToManager(ServerProduct.Dialectic, text, color, updateAvailable);
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

/// <summary>
/// What the launcher knows about the shared system, in the order a check moves through it. The
/// value only describes the system; it never gates Update Distro, which is also the recovery
/// action and stays available in every state.
/// </summary>
public enum SystemUpdateAvailability
{
    /// <summary>No answer yet, or a distro that cannot report its version. Also the recovery state.</summary>
    Unknown,
    Checking,
    Current,
    UpdateAvailable,
    Updating,
    Failed
}
