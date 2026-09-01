using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using DwemerDistro.Launcher.Wpf.Models;
using DwemerDistro.Launcher.Wpf.Services;
using Application = System.Windows.Application;
using MessageBox = System.Windows.MessageBox;

namespace DwemerDistro.Launcher.Wpf.ViewModels;

public sealed class FirstRunSetupViewModel : ObservableObject
{
    private const int IntroStepIndex = 0;
    private const int UpdateDistroStepIndex = 1;
    // Choosing mods sits between the core distro update and the credential/component steps: the
    // manager needs the updated distro, and the later steps only target products that exist.
    private const int ChooseModsStepIndex = 2;
    private const int HuggingFaceStepIndex = 3;
    private const int SetupStepIndex = 4;
    private const int ReadyStepIndex = 5;
    private const string StatusChecking = "#555555";
    private const string StatusGood = "#285A2D";
    private const string StatusWarn = "#6A3A12";
    private const string StatusBad = "#7A2828";
    private const string StatusUnknown = "#4F3C7A";
    private const int MaxVisibleSetupLogChars = 20000;
    private const int SetupUiFlushDelayMilliseconds = 250;
    private static readonly object QuickstartInstallLogLock = new();
    private static readonly string QuickstartInstallLogPath =
        Path.Combine(AppContext.BaseDirectory, "Logs", "quickstart-install.log");

    private readonly MainWindowViewModel _mainWindowViewModel;
    private readonly Dispatcher _dispatcher;
    private readonly ProcessRunner _processRunner = new();
    private readonly WslService _wsl;
    private readonly HardwareDetectionService _hardwareDetection;
    private readonly DistroSetupService _distroSetup;
    private readonly OpenRouterCredentialSyncService _openRouterSync;
    private readonly HuggingFaceTokenService _huggingFaceToken;
    private readonly VoiceEngineService _voiceEngine;
    private readonly ServerManagementService _serverManagement;
    private readonly OnboardingStateService _onboardingState = new();
    private readonly object _visibleSetupLogBufferLock = new();
    private readonly StringBuilder _visibleSetupLogBuffer = new();
    private readonly object _setupInstallProgressLock = new();
    private bool _ownsQuickstartWindowDistroGate;

    private SetupPreset _selectedPreset;
    private DistroSetupStatus? _setupStatus;
    private OpenRouterSyncStatus? _openRouterStatus;
    private HuggingFaceTokenStatus? _huggingFaceStatus;
    private VoiceEngineStatus? _voiceEngineStatus;
    private int _currentStepIndex;
    private bool _isBusy;
    private bool _isInstallingSetup;
    private bool _showPresetOptions;
    private bool _showTechnicalDetails;
    private bool _skipHuggingFaceStep = HuggingFaceTokenService.HasManagedToken;
    private bool _quickstartDistroUpdated;
    private bool _isInstallingProducts;
    private string _productStatusText = "Checking installed mods...";
    private string _productStatusBackground = StatusChecking;
    private bool _isVisibleSetupLogFlushQueued;
    private bool _isSetupInstallProgressFlushQueued;
    private int _setupOutputGeneration;
    private string _busyText = "Working";
    private string _hardwareSummary = "Detecting hardware";
    private string _hardwareDetail = "Checking GPU and recommended setup path...";
    private string _setupStatusText = "Checking setup";
    private string _setupStatusBackground = StatusChecking;
    private string _setupLogText = string.Empty;
    private double _setupInstallProgress;
    private string _setupInstallProgressText = "Preparing setup...";
    private string _setupInstallDetailText = string.Empty;
    private string _openRouterKey = string.Empty;
    private string _openRouterStatusText = "Checking OpenRouter";
    private string _openRouterStatusDetail = "Paste your key to apply it to installed game profiles.";
    private string _openRouterStatusBackground = StatusChecking;
    private string _huggingFaceTokenValue = string.Empty;
    private string _huggingFaceStatusText = "Checking Hugging Face";
    private string _huggingFaceStatusDetail = "Checking token and required model access...";
    private string _huggingFaceStatusBackground = StatusChecking;
    private string _voiceStatusText = "Checking voice engine";
    private string _voiceStatusDetail = "The launcher will use the cloned voice engine detected in your install.";
    private string _voiceStatusBackground = StatusChecking;
    private string _readySummary = "Finish setup to start DwemerDistro.";
    private SetupInstallProgress? _pendingSetupInstallProgress;

    public FirstRunSetupViewModel(MainWindowViewModel mainWindowViewModel)
    {
        _mainWindowViewModel = mainWindowViewModel;
        _dispatcher = Application.Current.Dispatcher;
        _wsl = new WslService(_processRunner);
        _hardwareDetection = new HardwareDetectionService(_processRunner);
        _distroSetup = new DistroSetupService(_wsl);
        _openRouterSync = new OpenRouterCredentialSyncService(_wsl);
        _huggingFaceToken = new HuggingFaceTokenService(_wsl);
        _voiceEngine = new VoiceEngineService(_wsl);
        _serverManagement = new ServerManagementService(_wsl);
        _selectedPreset = _distroSetup.GetPreset(SetupPresetKey.AmdCpu);

        SetupComponents = [];
        PresetOptions = new ObservableCollection<PresetOptionViewModel>(
            _distroSetup.Presets.Select(preset => new PresetOptionViewModel(
                preset.Key,
                preset.Title,
                preset.HardwareLabel,
                preset.Description)));
        OpenRouterTargets = [];
        HuggingFaceModelAccessItems = new ObservableCollection<HuggingFaceQuickstartModelViewModel>(
            HuggingFaceTokenService.RequiredModelAccess.Select(model =>
                new HuggingFaceQuickstartModelViewModel(
                    model.Key,
                    model.DisplayName,
                    model.RepositoryId,
                    model.AccessUrl,
                    () => _processRunner.OpenExternalUrl(model.AccessUrl))));
        VoiceApplyTargets = [];
        ProductChoices = new ObservableCollection<QuickstartProductViewModel>(
            GameProfile.CreateCatalog()
                .Select(profile => (Profile: profile, Product: ServerManagementService.TryParseGameKey(profile.Key)))
                .Where(entry => entry.Product is not null)
                .Select(entry => new QuickstartProductViewModel(
                    entry.Profile,
                    entry.Product!.Value,
                    RetryProductInstallAsync,
                    SkipProductInstall)));

        foreach (var product in ProductChoices)
        {
            product.PropertyChanged += ProductChoice_PropertyChanged;
        }

        InstallSelectedProductsCommand = new AsyncRelayCommand(
            InstallSelectedProductsAsync,
            () => !IsBusy && CanRunDistroWork && HasSelectedProducts);
        RefreshProductsCommand = new AsyncRelayCommand(RefreshProductsAsync, () => !IsBusy);

        InstallRecommendedCommand = new AsyncRelayCommand(InstallRecommendedAsync, () => !IsBusy && CanRunDistroWork);
        ContinueCommand = new AsyncRelayCommand(ContinueAsync, CanContinue);
        SkipRecommendedSetupCommand = new AsyncRelayCommand(SkipRecommendedSetupAsync, () => !IsBusy && IsSetupIntroStep);
        BackCommand = new RelayCommand(Back, () => !IsBusy && CurrentStepIndex > 0);
        ToggleTechnicalDetailsCommand = new RelayCommand(() => ShowTechnicalDetails = !ShowTechnicalDetails, () => !IsBusy);
        TogglePresetOptionsCommand = new RelayCommand(() => ShowPresetOptions = !ShowPresetOptions, () => !IsBusy);
        UpdateDistroCommand = new AsyncRelayCommand(
            UpdateDistroAsync,
            () => !IsBusy && CanRunDistroWork && !_mainWindowViewModel.IsDistroUpdateInProgress);
        RefreshSetupCommand = new AsyncRelayCommand(RefreshSetupAsync, () => !IsBusy);
        SaveOpenRouterCommand = new AsyncRelayCommand(SaveOpenRouterAsync, () => !IsBusy && !string.IsNullOrWhiteSpace(OpenRouterKey));
        RefreshOpenRouterCommand = new AsyncRelayCommand(RefreshOpenRouterStatusAsync, () => !IsBusy);
        OpenOpenRouterKeysCommand = new RelayCommand(() => _processRunner.OpenExternalUrl("https://openrouter.ai/settings/keys"));
        SaveHuggingFaceCommand = new AsyncRelayCommand(SaveHuggingFaceAsync, () => !IsBusy && !string.IsNullOrWhiteSpace(HuggingFaceTokenValue));
        RefreshHuggingFaceCommand = new AsyncRelayCommand(RefreshHuggingFaceStatusAsync, () => !IsBusy);
        OpenHuggingFaceTokensCommand = new RelayCommand(() => _processRunner.OpenExternalUrl("https://huggingface.co/settings/tokens"));
        OpenHuggingFaceModelAccessCommand = new RelayCommand(OpenHuggingFaceModelAccessPages);
        RefreshReadyCommand = new AsyncRelayCommand(PrepareReadyAsync, () => !IsBusy);
        StartServerCommand = new AsyncRelayCommand(StartServerFromSetupAsync, () => !IsBusy && IsReadyStep);
        AdvancedSettingsCommand = new AsyncRelayCommand(CloseForAdvancedSettingsAsync, () => !IsBusy);

        ApplySelectedPresetToOptions();
        RebuildSetupComponentItems([]);
        _ownsQuickstartWindowDistroGate = _mainWindowViewModel.TryBeginQuickstartDistroActivity();
        if (!_ownsQuickstartWindowDistroGate)
        {
            throw new InvalidOperationException(
                "First-time setup cannot open while critical distro maintenance is running.");
        }

        _mainWindowViewModel.PropertyChanged += MainWindowViewModel_PropertyChanged;
    }

    public event Action? RequestClose;

    public ObservableCollection<SetupComponentQuickstartViewModel> SetupComponents { get; }

    public ObservableCollection<PresetOptionViewModel> PresetOptions { get; }

    public ObservableCollection<CredentialTargetViewModel> OpenRouterTargets { get; }

    public ObservableCollection<HuggingFaceQuickstartModelViewModel> HuggingFaceModelAccessItems { get; }

    public ObservableCollection<CredentialTargetViewModel> VoiceApplyTargets { get; }

    /// <summary>The three optional application servers, in rail order.</summary>
    public ObservableCollection<QuickstartProductViewModel> ProductChoices { get; }

    public AsyncRelayCommand InstallSelectedProductsCommand { get; }

    public AsyncRelayCommand RefreshProductsCommand { get; }

    public AsyncRelayCommand InstallRecommendedCommand { get; }

    public AsyncRelayCommand ContinueCommand { get; }

    public AsyncRelayCommand SkipRecommendedSetupCommand { get; }

    public RelayCommand BackCommand { get; }

    public RelayCommand ToggleTechnicalDetailsCommand { get; }

    public RelayCommand TogglePresetOptionsCommand { get; }

    public AsyncRelayCommand UpdateDistroCommand { get; }

    public AsyncRelayCommand RefreshSetupCommand { get; }

    public AsyncRelayCommand SaveOpenRouterCommand { get; }

    public AsyncRelayCommand RefreshOpenRouterCommand { get; }

    public RelayCommand OpenOpenRouterKeysCommand { get; }

    public AsyncRelayCommand SaveHuggingFaceCommand { get; }

    public AsyncRelayCommand RefreshHuggingFaceCommand { get; }

    public RelayCommand OpenHuggingFaceTokensCommand { get; }

    public RelayCommand OpenHuggingFaceModelAccessCommand { get; }

    public AsyncRelayCommand RefreshReadyCommand { get; }

    public AsyncRelayCommand StartServerCommand { get; }

    public AsyncRelayCommand AdvancedSettingsCommand { get; }

    public int CurrentStepIndex
    {
        get => _currentStepIndex;
        private set
        {
            if (SetProperty(ref _currentStepIndex, value))
            {
                OnPropertyChanged(nameof(IsSetupIntroStep));
                OnPropertyChanged(nameof(IsUpdateDistroStep));
                OnPropertyChanged(nameof(IsChooseModsStep));
                OnPropertyChanged(nameof(IsSetupStep));
                OnPropertyChanged(nameof(IsHuggingFaceStep));
                OnPropertyChanged(nameof(IsReadyStep));
                OnPropertyChanged(nameof(ShowContinueButton));
                OnPropertyChanged(nameof(CurrentStepLabel));
                OnPropertyChanged(nameof(StepTitle));
                OnPropertyChanged(nameof(StepSubtitle));
                OnPropertyChanged(nameof(PrimaryContinueText));
                OnPropertyChanged(nameof(InstallRecommendedButtonText));
                OnPropertyChanged(nameof(IsSetupSelectionEnabled));
                OnPropertyChanged(nameof(IsProductSelectionEnabled));
                ShowTechnicalDetails = false;
                RaiseCommandStates();
            }
        }
    }

    public bool IsSetupIntroStep => CurrentStepIndex == IntroStepIndex;

    public bool IsUpdateDistroStep => CurrentStepIndex == UpdateDistroStepIndex;

    public bool IsChooseModsStep => CurrentStepIndex == ChooseModsStepIndex;

    public bool IsHuggingFaceStep => CurrentStepIndex == HuggingFaceStepIndex && !_skipHuggingFaceStep;

    public bool IsSetupStep => CurrentStepIndex == SetupStepIndex;

    public bool IsReadyStep => CurrentStepIndex == ReadyStepIndex;

    public bool ShowContinueButton => !IsReadyStep && !IsSetupIntroStep && !IsSetupStep;

    public string CurrentStepLabel => $"Step {GetDisplayStepNumber()} of {GetTotalStepCount()}";

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                OnPropertyChanged(nameof(IsSetupSelectionEnabled));
                OnPropertyChanged(nameof(IsProductSelectionEnabled));
                RaiseCommandStates();
            }
        }
    }

    /// <summary>
    /// False while the launcher holds the shared distro gate for Compact Distro, Export, Import,
    /// or Fix WSL DNS. Only the install and update actions read it, so every other step of
    /// Quickstart, including moving between steps, stays usable.
    /// </summary>
    public bool CanRunDistroWork => !_mainWindowViewModel.IsCriticalMaintenanceInProgress;

    public bool IsSetupSelectionEnabled => !IsBusy && IsSetupStep;

    public bool IsProductSelectionEnabled => !IsBusy && IsChooseModsStep && !_isInstallingProducts;

    public bool IsInstallingProducts
    {
        get => _isInstallingProducts;
        private set
        {
            if (SetProperty(ref _isInstallingProducts, value))
            {
                OnPropertyChanged(nameof(IsProductSelectionEnabled));
            }
        }
    }

    public bool HasSelectedProducts => ProductChoices.Any(product => product.IsSelected);

    /// <summary>
    /// Nothing selected is a valid choice. Once a user selects a mod, Quickstart must not advance
    /// until that choice either installed successfully or the user explicitly skipped its failure.
    /// </summary>
    public bool CanLeaveProductSelection => CanAdvanceFromProductSelection(ProductChoices);

    internal static bool CanAdvanceFromProductSelection(
        IReadOnlyList<QuickstartProductViewModel> products) =>
        !products.Any(product => product.IsSelected) || products
            .Where(product => product.IsSelected)
            .All(product => product.InstallState is
                QuickstartProductInstallState.Installed or QuickstartProductInstallState.Skipped);

    public string ProductStatusText
    {
        get => _productStatusText;
        private set => SetProperty(ref _productStatusText, value);
    }

    public string ProductStatusBackground
    {
        get => _productStatusBackground;
        private set => SetProperty(ref _productStatusBackground, value);
    }

    public string InstallProductsButtonText =>
        HasSelectedProducts ? "Install Selected Mods" : "No Mods Selected";

    public bool IsInstallingSetup
    {
        get => _isInstallingSetup;
        private set => SetProperty(ref _isInstallingSetup, value);
    }

    public bool ShowPresetOptions
    {
        get => _showPresetOptions;
        private set => SetProperty(ref _showPresetOptions, value);
    }

    public bool ShowTechnicalDetails
    {
        get => _showTechnicalDetails;
        private set
        {
            if (SetProperty(ref _showTechnicalDetails, value))
            {
                OnPropertyChanged(nameof(DetailsButtonText));
            }
        }
    }

    public string DetailsButtonText => ShowTechnicalDetails ? "Hide Details" : "Details";

    public string BusyText
    {
        get => _busyText;
        private set => SetProperty(ref _busyText, value);
    }

    public string StepTitle => CurrentStepIndex switch
    {
        IntroStepIndex => "Quick Setup",
        UpdateDistroStepIndex => "Update Distro",
        ChooseModsStepIndex => "Choose Your Mods",
        HuggingFaceStepIndex => "Connect Hugging Face",
        SetupStepIndex => "Components",
        _ => "Setup Complete"
    };

    public string StepSubtitle => CurrentStepIndex switch
    {
        IntroStepIndex => "The launcher picked the recommended setup for this machine.",
        UpdateDistroStepIndex => "Pull the latest distro scripts first.",
        ChooseModsStepIndex => "Pick the mods you want. You can add or remove any of them later from the Mods page.",
        HuggingFaceStepIndex => "The installers use Hugging Face to download cloned voice models.",
        SetupStepIndex => "Install the required voice and speech components.",
        _ => "Start the server, switch on the game, and talk."
    };

    public string PrimaryContinueText => CurrentStepIndex switch
    {
        IntroStepIndex => "Continue",
        UpdateDistroStepIndex => "Next",
        ChooseModsStepIndex => "Next",
        HuggingFaceStepIndex => "Continue to Install",
        SetupStepIndex => "Continue to Start Server",
        _ => "Ready"
    };

    public string InstallRecommendedButtonText =>
        _setupStatus?.AllRequiredInstalled == true ? "Continue to Start Server" : "Install";

    public string SelectedPresetTitle => _selectedPreset.Title;

    public string SelectedPresetHardware => _selectedPreset.HardwareLabel;

    public string SelectedPresetDescription => _selectedPreset.Description;

    public string SelectedVoiceEngine => _selectedPreset.VoiceEngineName;

    public bool ShowAmdCpuModeNote =>
        _selectedPreset.Key == SetupPresetKey.AmdCpu &&
        HardwareSummary.Contains("AMD GPU", StringComparison.OrdinalIgnoreCase);

    public string HardwareSummary
    {
        get => _hardwareSummary;
        private set
        {
            if (SetProperty(ref _hardwareSummary, value))
            {
                OnPropertyChanged(nameof(ShowAmdCpuModeNote));
            }
        }
    }

    public string HardwareDetail
    {
        get => _hardwareDetail;
        private set => SetProperty(ref _hardwareDetail, value);
    }

    public string SetupStatusText
    {
        get => _setupStatusText;
        private set => SetProperty(ref _setupStatusText, value);
    }

    public string SetupStatusBackground
    {
        get => _setupStatusBackground;
        private set => SetProperty(ref _setupStatusBackground, value);
    }

    public string SetupLogText
    {
        get => _setupLogText;
        private set => SetProperty(ref _setupLogText, value);
    }

    public double SetupInstallProgress
    {
        get => _setupInstallProgress;
        private set => SetProperty(ref _setupInstallProgress, value);
    }

    public string SetupInstallProgressText
    {
        get => _setupInstallProgressText;
        private set => SetProperty(ref _setupInstallProgressText, value);
    }

    public string SetupInstallDetailText
    {
        get => _setupInstallDetailText;
        private set
        {
            if (SetProperty(ref _setupInstallDetailText, value))
            {
                OnPropertyChanged(nameof(HasSetupInstallDetail));
            }
        }
    }

    public bool HasSetupInstallDetail => !string.IsNullOrWhiteSpace(SetupInstallDetailText);

    public string OpenRouterKey
    {
        get => _openRouterKey;
        set
        {
            if (SetProperty(ref _openRouterKey, value))
            {
                SaveOpenRouterCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string OpenRouterStatusText
    {
        get => _openRouterStatusText;
        private set => SetProperty(ref _openRouterStatusText, value);
    }

    public string OpenRouterStatusDetail
    {
        get => _openRouterStatusDetail;
        private set => SetProperty(ref _openRouterStatusDetail, value);
    }

    public string OpenRouterStatusBackground
    {
        get => _openRouterStatusBackground;
        private set => SetProperty(ref _openRouterStatusBackground, value);
    }

    public string HuggingFaceTokenValue
    {
        get => _huggingFaceTokenValue;
        set
        {
            if (SetProperty(ref _huggingFaceTokenValue, value))
            {
                SaveHuggingFaceCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string HuggingFaceStatusText
    {
        get => _huggingFaceStatusText;
        private set => SetProperty(ref _huggingFaceStatusText, value);
    }

    public string HuggingFaceStatusDetail
    {
        get => _huggingFaceStatusDetail;
        private set => SetProperty(ref _huggingFaceStatusDetail, value);
    }

    public string HuggingFaceStatusBackground
    {
        get => _huggingFaceStatusBackground;
        private set => SetProperty(ref _huggingFaceStatusBackground, value);
    }

    public string VoiceStatusText
    {
        get => _voiceStatusText;
        private set => SetProperty(ref _voiceStatusText, value);
    }

    public string VoiceStatusDetail
    {
        get => _voiceStatusDetail;
        private set => SetProperty(ref _voiceStatusDetail, value);
    }

    public string VoiceStatusBackground
    {
        get => _voiceStatusBackground;
        private set => SetProperty(ref _voiceStatusBackground, value);
    }

    public string ReadySummary
    {
        get => _readySummary;
        private set => SetProperty(ref _readySummary, value);
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await RunBusyAsync("Checking your setup", async () =>
        {
            var hardware = await _hardwareDetection.DetectAsync(cancellationToken).ConfigureAwait(true);
            HardwareSummary = hardware.Summary;
            HardwareDetail = hardware.Detail;
            ApplyPreset(hardware.RecommendedPreset);
            await RefreshSetupCoreAsync(cancellationToken).ConfigureAwait(true);
            await RefreshProductsCoreAsync(cancellationToken).ConfigureAwait(true);
            await _huggingFaceToken.EnsureManagedTokenAsync(cancellationToken).ConfigureAwait(true);
            await RefreshHuggingFaceStatusCoreAsync(cancellationToken).ConfigureAwait(true);
        }).ConfigureAwait(true);
    }

    public static async Task<bool> ShouldShowFirstRunSetupAsync(
        CancellationToken cancellationToken = default,
        OnboardingStateService? onboardingState = null)
    {
        onboardingState ??= new OnboardingStateService();
        var state = await onboardingState.LoadAsync(cancellationToken).ConfigureAwait(false);
        if (state.Completed || state.Skipped)
        {
            return false;
        }

        var processRunner = new ProcessRunner();
        var wsl = new WslService(processRunner);
        var hardwareDetection = new HardwareDetectionService(processRunner);
        var setup = new DistroSetupService(wsl);
        var voiceEngine = new VoiceEngineService(wsl);

        var hardware = await hardwareDetection.DetectAsync(cancellationToken).ConfigureAwait(false);
        var preset = setup.GetPreset(hardware.RecommendedPreset);
        var setupStatus = await setup.ProbeAsync(preset, cancellationToken).ConfigureAwait(false);

        if (!setupStatus.DistroExists || !setupStatus.AllRequiredInstalled)
        {
            return true;
        }

        var voiceStatus = await voiceEngine.GetStatusAsync(preset, cancellationToken).ConfigureAwait(false);
        return !voiceStatus.HasUsableEngine;
    }

    private async Task InstallRecommendedAsync()
    {
        if (_setupStatus?.AllRequiredInstalled == true)
        {
            CurrentStepIndex = ReadyStepIndex;
            await PrepareReadyAsync().ConfigureAwait(true);
            return;
        }

        var setupComplete = false;
        await RunDistroBusyAsync($"Installing {_selectedPreset.Title}", async () =>
        {
            ResetSetupOutputBuffers();
            SetupLogText = string.Empty;
            ResetQuickstartInstallLog();
            SetupInstallProgress = 0;
            SetupInstallProgressText = $"Preparing {_selectedPreset.Title}...";
            SetupInstallDetailText = string.Empty;
            IsInstallingSetup = true;
            AppendSetupLog($"Quickstart install log: {QuickstartInstallLogPath}{Environment.NewLine}");
            AppendSetupLog("Console output is summarized here to keep the launcher responsive. The full apt and pip output is saved in the quickstart install log." + Environment.NewLine);
            AppendSetupLog($"Recommended path: {_selectedPreset.Title}{Environment.NewLine}");
            try
            {
                var status = await _distroSetup.InstallPresetAsync(
                        _selectedPreset,
                        AppendSetupLog,
                        ApplySetupInstallProgress,
                        skipPreparation: _quickstartDistroUpdated)
                    .ConfigureAwait(true);
                ApplySetupStatus(status);
                await RefreshHuggingFaceStatusCoreAsync().ConfigureAwait(true);
                await RefreshVoiceStatusCoreAsync().ConfigureAwait(true);
                setupComplete = status.AllRequiredInstalled;
            }
            finally
            {
                IsInstallingSetup = false;
            }
        }).ConfigureAwait(true);

        if (setupComplete)
        {
            CurrentStepIndex = ReadyStepIndex;
            await PrepareReadyAsync().ConfigureAwait(true);
        }
    }

    private async Task UpdateDistroAsync()
    {
        await RunDistroBusyAsync("Updating distro", async () =>
        {
            ResetSetupOutputBuffers();
            SetupLogText = string.Empty;
            ResetQuickstartInstallLog();
            SetupInstallProgress = 0;
            SetupInstallProgressText = "Updating DwemerDistro...";
            SetupInstallDetailText = string.Empty;
            IsInstallingSetup = true;
            AppendSetupLog($"Quickstart update log: {QuickstartInstallLogPath}{Environment.NewLine}");
            AppendSetupLog("Starting DwemerDistro update. Detailed output is shown in the main launcher log." + Environment.NewLine);
            try
            {
                var updated = await _mainWindowViewModel.UpdateDistroFromQuickstartAsync().ConfigureAwait(true);
                _quickstartDistroUpdated = updated;
                SetupInstallProgress = updated ? 100 : 0;
                SetupInstallProgressText = updated ? "Distro update complete." : "Distro update needs attention.";
                AppendSetupLog(updated
                    ? "Distro update completed. Refreshed quickstart checks." + Environment.NewLine
                    : "Distro update reported issues. Check the main launcher log." + Environment.NewLine);
                await RefreshSetupCoreAsync().ConfigureAwait(true);
                await RefreshProductsCoreAsync().ConfigureAwait(true);
            }
            finally
            {
                IsInstallingSetup = false;
            }
        }).ConfigureAwait(true);
    }

    private async Task ContinueAsync()
    {
        if (!CanContinue())
        {
            return;
        }

        if (CurrentStepIndex == SetupStepIndex)
        {
            CurrentStepIndex = ReadyStepIndex;
            await PrepareReadyAsync().ConfigureAwait(true);
            return;
        }

        CurrentStepIndex = GetNextStepIndex(CurrentStepIndex);
    }

    private async Task SkipRecommendedSetupAsync()
    {
        if (IsBusy)
        {
            return;
        }

        try
        {
            await _onboardingState
                .MarkSkippedAsync(_selectedPreset.Key, GetSelectedProductKeys(), GetProductInstallResults())
                .ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            LauncherLogService.Startup("Quickstart could not save the skip preference.", ex);
            MessageBox.Show(
                $"Quick Setup could not be disabled.\n\n{ex.Message}",
                "DwemerDistro Quickstart",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        RequestClose?.Invoke();
    }

    private void Back()
    {
        if (CurrentStepIndex <= 0 || IsBusy)
        {
            return;
        }

        CurrentStepIndex = GetPreviousStepIndex(CurrentStepIndex);
    }

    private async Task RefreshSetupAsync()
    {
        await RunBusyAsync("Checking setup", () => RefreshSetupCoreAsync()).ConfigureAwait(true);
    }

    private async Task SaveOpenRouterAsync()
    {
        await RunBusyAsync("Saving OpenRouter key", async () =>
        {
            OpenRouterStatusText = "Applying key";
            OpenRouterStatusDetail = "Applying key to installed game profiles...";
            OpenRouterStatusBackground = StatusChecking;
            var status = await _openRouterSync.SaveKeyAsync(OpenRouterKey).ConfigureAwait(true);
            ApplyOpenRouterStatus(status);
            if (status.AnyUpdated || status.AllAvailableTargetsConfigured)
            {
                OpenRouterKey = string.Empty;
            }
        }).ConfigureAwait(true);
    }

    private async Task RefreshOpenRouterStatusAsync()
    {
        await RunBusyAsync("Checking OpenRouter", () => RefreshOpenRouterStatusCoreAsync()).ConfigureAwait(true);
    }

    private async Task SaveHuggingFaceAsync()
    {
        await RunBusyAsync("Saving Hugging Face token", async () =>
        {
            var result = await _huggingFaceToken.SaveTokenAsync(HuggingFaceTokenValue).ConfigureAwait(true);
            HuggingFaceTokenValue = string.Empty;
            if (!result.Succeeded)
            {
                HuggingFaceStatusText = "Save failed";
                HuggingFaceStatusDetail = HuggingFaceTokenService.BuildErrorText(result);
                HuggingFaceStatusBackground = StatusBad;
                return;
            }

            await RefreshHuggingFaceStatusCoreAsync().ConfigureAwait(true);
        }).ConfigureAwait(true);
    }

    private async Task RefreshHuggingFaceStatusAsync()
    {
        await RunBusyAsync("Checking Hugging Face", () => RefreshHuggingFaceStatusCoreAsync()).ConfigureAwait(true);
    }

    private void OpenHuggingFaceModelAccessPages()
    {
        foreach (var model in HuggingFaceTokenService.RequiredModelAccess)
        {
            _processRunner.OpenExternalUrl(model.AccessUrl);
        }
    }

    private async Task PrepareReadyAsync()
    {
        await RunBusyAsync("Applying voice engine", async () =>
        {
            await RefreshVoiceStatusCoreAsync().ConfigureAwait(true);
            VoiceApplyTargets.Clear();

            if (_voiceEngineStatus?.HasUsableEngine != true)
            {
                ReadySummary = "Components are not ready yet. Go back and install the recommended setup.";
                return;
            }

            var targets = await _voiceEngine.ApplyVoiceEngineAsync(_voiceEngineStatus.EngineKey).ConfigureAwait(true);
            ApplyVoiceTargetStatuses(targets);

            ReadySummary = "Ready to start DwemerDistro.";
        }).ConfigureAwait(true);
    }

    private async Task StartServerFromSetupAsync()
    {
        try
        {
            await MarkReadyAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            LauncherLogService.Startup("Quickstart could not mark onboarding complete before starting server.", ex);
        }

        RequestClose?.Invoke();
        _mainWindowViewModel.StartServerCommand.Execute(null);
    }

    private async Task CloseForAdvancedSettingsAsync()
    {
        if (IsReadyStep && CanContinue())
        {
            await MarkReadyAsync().ConfigureAwait(true);
        }

        RequestClose?.Invoke();
    }

    private async Task RefreshSetupCoreAsync(CancellationToken cancellationToken = default)
    {
        SetupStatusText = "Checking setup";
        SetupStatusBackground = StatusChecking;
        var status = await _distroSetup.ProbeAsync(_selectedPreset, cancellationToken).ConfigureAwait(true);
        ApplySetupStatus(status);
    }

    private async Task RefreshOpenRouterStatusCoreAsync(CancellationToken cancellationToken = default)
    {
        OpenRouterStatusText = "Checking OpenRouter";
        OpenRouterStatusDetail = "Checking installed game databases...";
        OpenRouterStatusBackground = StatusChecking;
        var status = await _openRouterSync.GetStatusAsync(cancellationToken).ConfigureAwait(true);
        ApplyOpenRouterStatus(status);
    }

    private async Task RefreshHuggingFaceStatusCoreAsync(CancellationToken cancellationToken = default)
    {
        HuggingFaceStatusText = "Checking";
        HuggingFaceStatusDetail = "Checking token and required model access...";
        HuggingFaceStatusBackground = StatusChecking;
        foreach (var item in HuggingFaceModelAccessItems)
        {
            item.SetCheckingState();
        }

        await _huggingFaceToken.EnsureManagedTokenAsync(cancellationToken).ConfigureAwait(true);
        var status = await _huggingFaceToken.GetStatusAsync(cancellationToken).ConfigureAwait(true);
        ApplyHuggingFaceStatus(status);
    }

    private async Task RefreshVoiceStatusCoreAsync(CancellationToken cancellationToken = default)
    {
        VoiceStatusText = "Checking voice engine";
        VoiceStatusBackground = StatusChecking;
        var status = await _voiceEngine.GetStatusAsync(_selectedPreset, cancellationToken).ConfigureAwait(true);
        ApplyVoiceStatus(status);
    }

    private void ApplyPreset(SetupPresetKey key)
    {
        _selectedPreset = _distroSetup.GetPreset(key);
        OnPropertyChanged(nameof(SelectedPresetTitle));
        OnPropertyChanged(nameof(SelectedPresetHardware));
        OnPropertyChanged(nameof(SelectedPresetDescription));
        OnPropertyChanged(nameof(SelectedVoiceEngine));
        OnPropertyChanged(nameof(ShowAmdCpuModeNote));
        ApplySelectedPresetToOptions();
        RebuildSetupComponentItems(_setupStatus?.Components ?? []);
    }

    private void ApplySelectedPresetToOptions()
    {
        foreach (var option in PresetOptions)
        {
            option.IsSelected = option.Key == _selectedPreset.Key;
        }

    }

    private void ApplySetupStatus(DistroSetupStatus status)
    {
        _setupStatus = status;
        SetupStatusText = !status.DistroExists
            ? "Distro missing"
            : status.AllRequiredInstalled
                ? "Ready"
                : "Needs install";
        SetupStatusBackground = !status.DistroExists
            ? StatusBad
            : status.AllRequiredInstalled
                ? StatusGood
                : StatusWarn;
        OnPropertyChanged(nameof(InstallRecommendedButtonText));
        RebuildSetupComponentItems(status.Components);
        RaiseCommandStates();
    }

    private void RebuildSetupComponentItems(IReadOnlyList<SetupComponentState> states)
    {
        var statesByKey = states.ToDictionary(state => state.Key, StringComparer.OrdinalIgnoreCase);
        SetupComponents.Clear();

        foreach (var key in _selectedPreset.ComponentKeys)
        {
            var component = _distroSetup.GetComponent(key);
            if (statesByKey.TryGetValue(key, out var state))
            {
                SetupComponents.Add(new SetupComponentQuickstartViewModel(
                    state.Title,
                    state.Description,
                    state.IsInstalled,
                    state.IsInstalled ? "Done" : "Install",
                    state.IsInstalled ? StatusGood : StatusWarn,
                    state.Error));
            }
            else
            {
                SetupComponents.Add(new SetupComponentQuickstartViewModel(
                    component.Title,
                    component.Description,
                    false,
                    "Checking",
                    StatusChecking,
                    null));
            }
        }
    }

    private void ApplyOpenRouterStatus(OpenRouterSyncStatus status)
    {
        _openRouterStatus = status;
        OpenRouterTargets.Clear();

        // A product that is not installed has no database to hold a key, so it is not a credential
        // target at all - listing it as "needs key" would read as a problem the user must fix.
        var installedTargets = status.Targets.Where(IsInstalledCredentialTarget).ToArray();

        foreach (var target in installedTargets)
        {
            OpenRouterTargets.Add(new CredentialTargetViewModel(
                target.TargetName,
                target.IsSkipped ? "Skipped" : target.StatusText,
                target.IsConfigured ? StatusGood : target.IsSkipped ? StatusUnknown : StatusWarn,
                target.Error));
        }

        if (status.HasError)
        {
            OpenRouterStatusText = "Unable to check OpenRouter";
            OpenRouterStatusDetail = status.Error ?? "OpenRouter status could not be checked.";
            OpenRouterStatusBackground = StatusUnknown;
        }
        else if (status.AllAvailableTargetsConfigured)
        {
            OpenRouterStatusText = status.AnyUpdated ? "OpenRouter key applied" : "OpenRouter key configured";
            OpenRouterStatusDetail = BuildOpenRouterStatusDetail(status);
            OpenRouterStatusBackground = StatusGood;
        }
        else
        {
            OpenRouterStatusText = status.AnyUpdated ? "OpenRouter partially applied" : "OpenRouter key needed";
            OpenRouterStatusDetail = BuildOpenRouterStatusDetail(status);
            OpenRouterStatusBackground = StatusWarn;
        }

        RaiseCommandStates();
    }

    private void ApplyHuggingFaceStatus(HuggingFaceTokenStatus status)
    {
        _huggingFaceStatus = status;
        var accessByKey = status.ModelAccess.ToDictionary(model => model.Key, StringComparer.OrdinalIgnoreCase);

        foreach (var item in HuggingFaceModelAccessItems)
        {
            if (accessByKey.TryGetValue(item.Key, out var access))
            {
                item.ApplyStatus(access);
            }
            else
            {
                item.SetUnknownState();
            }
        }

        if (!status.IsConfigured && string.IsNullOrWhiteSpace(status.Error))
        {
            HuggingFaceStatusText = "Token needed";
            HuggingFaceStatusDetail = "Do this first. The voice installer uses Hugging Face to download the Pocket-TTS model.";
            HuggingFaceStatusBackground = StatusWarn;
        }
        else if (IsHuggingFaceReady(status))
        {
            var userSuffix = string.IsNullOrWhiteSpace(status.UserName) ? string.Empty : $" as {status.UserName}";
            HuggingFaceStatusText = $"Ready{userSuffix}";
            HuggingFaceStatusDetail = "Token is valid and the required cloned-voice model is reachable.";
            HuggingFaceStatusBackground = StatusGood;
        }
        else if (status.IsValid == false)
        {
            HuggingFaceStatusText = "Invalid token";
            HuggingFaceStatusDetail = status.Error ?? "Hugging Face rejected this token.";
            HuggingFaceStatusBackground = StatusBad;
        }
        else if (status.IsConfigured)
        {
            HuggingFaceStatusText = "Needs model access";
            HuggingFaceStatusDetail = "Accept the required cloned-voice model access on Hugging Face, then refresh.";
            HuggingFaceStatusBackground = StatusWarn;
        }
        else
        {
            HuggingFaceStatusText = "Unable to verify";
            HuggingFaceStatusDetail = status.Error ?? "Hugging Face status could not be checked.";
            HuggingFaceStatusBackground = StatusUnknown;
        }

        ApplyHuggingFaceStepState(IsHuggingFaceReady(status));
        RaiseCommandStates();
    }

    private void ApplyVoiceStatus(VoiceEngineStatus status)
    {
        _voiceEngineStatus = status;
        VoiceStatusText = status.HasUsableEngine ? "Ready" : "Voice engine needed";
        VoiceStatusDetail = status.DetailText;
        VoiceStatusBackground = status.HasUsableEngine ? StatusGood : StatusWarn;
        RaiseCommandStates();
    }

    private void ApplyVoiceTargetStatuses(IReadOnlyList<VoiceEngineApplyTargetStatus> targets)
    {
        VoiceApplyTargets.Clear();
        foreach (var target in targets)
        {
            VoiceApplyTargets.Add(new CredentialTargetViewModel(
                target.TargetName,
                target.StatusText,
                target.Applied ? StatusGood : target.Skipped ? StatusUnknown : StatusWarn,
                target.Error));
        }
    }

    private async Task MarkReadyAsync()
    {
        var voiceEngineKey = _voiceEngineStatus?.EngineKey ?? _selectedPreset.VoiceEngineKey;
        await _onboardingState.MarkCompletedAsync(
                _selectedPreset.Key,
                voiceEngineKey,
                false,
                IsHuggingFaceReady(_huggingFaceStatus),
                GetSelectedProductKeys(),
                GetProductInstallResults())
            .ConfigureAwait(true);
    }

    private bool CanContinue()
    {
        if (IsBusy)
        {
            return false;
        }

        return CurrentStepIndex switch
        {
            IntroStepIndex => true,
            UpdateDistroStepIndex => true,
            // Continuing with nothing selected is deliberate. A selected mod must be installed or
            // explicitly skipped so a checked box cannot be silently ignored.
            ChooseModsStepIndex => !_isInstallingProducts && CanLeaveProductSelection,
            HuggingFaceStepIndex => IsHuggingFaceReady(_huggingFaceStatus),
            SetupStepIndex => _setupStatus?.AllRequiredInstalled == true,
            ReadyStepIndex => _voiceEngineStatus?.HasUsableEngine == true,
            _ => false
        };
    }

    private void ApplyHuggingFaceStepState(bool shouldSkip)
    {
        var skipStep = HuggingFaceTokenService.HasManagedToken || shouldSkip;
        if (_skipHuggingFaceStep == skipStep)
        {
            return;
        }

        _skipHuggingFaceStep = skipStep;
        OnPropertyChanged(nameof(IsHuggingFaceStep));
        OnPropertyChanged(nameof(CurrentStepLabel));
        OnPropertyChanged(nameof(StepSubtitle));
        OnPropertyChanged(nameof(PrimaryContinueText));

        if (_skipHuggingFaceStep && CurrentStepIndex == HuggingFaceStepIndex)
        {
            CurrentStepIndex = GetNextStepIndex(CurrentStepIndex);
        }
    }

    private int GetNextStepIndex(int currentStepIndex)
    {
        return _skipHuggingFaceStep && currentStepIndex == ChooseModsStepIndex
            ? SetupStepIndex
            : Math.Min(ReadyStepIndex, currentStepIndex + 1);
    }

    private int GetPreviousStepIndex(int currentStepIndex)
    {
        return _skipHuggingFaceStep && currentStepIndex == SetupStepIndex
            ? ChooseModsStepIndex
            : Math.Max(0, currentStepIndex - 1);
    }

    private int GetTotalStepCount()
    {
        return _skipHuggingFaceStep ? 5 : 6;
    }

    private int GetDisplayStepNumber()
    {
        return _skipHuggingFaceStep && CurrentStepIndex > HuggingFaceStepIndex
            ? CurrentStepIndex
            : CurrentStepIndex + 1;
    }

    /// <summary>
    /// Runs a Quickstart step that mutates the distro. Quickstart is modeless, so the step is
    /// registered with the launcher's shared gate for its whole duration: Compact Distro, Export,
    /// Import, and Fix WSL DNS cannot start underneath it, and it refuses to start while one of
    /// those is already running.
    /// </summary>
    private async Task RunDistroBusyAsync(string busyText, Func<Task> action)
    {
        if (IsBusy)
        {
            return;
        }

        if (!_mainWindowViewModel.TryBeginQuickstartDistroActivity())
        {
            ReportDistroMaintenanceBusy();
            return;
        }

        try
        {
            await RunBusyAsync(busyText, action).ConfigureAwait(true);
        }
        finally
        {
            _mainWindowViewModel.EndQuickstartDistroActivity();
        }
    }

    /// <summary>Quickstart has no console of its own, so the refusal has to be a dialog.</summary>
    private void ReportDistroMaintenanceBusy()
    {
        MessageBox.Show(
            MainWindowViewModel.ExclusiveDistroOperationBusyMessage +
            "\n\nWait for it to finish, then try again.",
            "DwemerDistro Quickstart",
            MessageBoxButton.OK,
            MessageBoxImage.Warning);
    }

    /// <summary>
    /// Keeps the install and update buttons in step with the launcher's shared gate while this
    /// modeless window stays open.
    /// </summary>
    private void MainWindowViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(MainWindowViewModel.IsCriticalMaintenanceInProgress) &&
            e.PropertyName != nameof(MainWindowViewModel.IsDistroUpdateInProgress))
        {
            return;
        }

        if (_dispatcher.CheckAccess())
        {
            RefreshDistroWorkAvailability();
            return;
        }

        if (!_dispatcher.HasShutdownStarted)
        {
            _ = _dispatcher.BeginInvoke((Action)RefreshDistroWorkAvailability);
        }
    }

    private void RefreshDistroWorkAvailability()
    {
        OnPropertyChanged(nameof(CanRunDistroWork));
        RaiseCommandStates();
    }

    /// <summary>Releases the launcher-level subscription and window gate when Quickstart closes.</summary>
    public void Detach()
    {
        _mainWindowViewModel.PropertyChanged -= MainWindowViewModel_PropertyChanged;
        if (!_ownsQuickstartWindowDistroGate)
        {
            return;
        }

        _ownsQuickstartWindowDistroGate = false;
        _mainWindowViewModel.EndQuickstartDistroActivity();
    }

    private async Task RunBusyAsync(string busyText, Func<Task> action)
    {
        if (IsBusy)
        {
            return;
        }

        BusyText = busyText;
        IsBusy = true;
        try
        {
            await action().ConfigureAwait(true);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void AppendSetupLog(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        AppendQuickstartInstallLog(text);
        var visibleText = BuildVisibleSetupLogAppend(text);
        if (string.IsNullOrEmpty(visibleText))
        {
            return;
        }

        QueueVisibleSetupLog(visibleText, _setupOutputGeneration);
    }

    private void QueueVisibleSetupLog(string text, int generation)
    {
        lock (_visibleSetupLogBufferLock)
        {
            if (generation != _setupOutputGeneration)
            {
                return;
            }

            _visibleSetupLogBuffer.Append(text);
            if (_isVisibleSetupLogFlushQueued)
            {
                return;
            }

            _isVisibleSetupLogFlushQueued = true;
        }

        _ = FlushVisibleSetupLogLaterAsync(generation);
    }

    private async Task FlushVisibleSetupLogLaterAsync(int generation)
    {
        await Task.Delay(SetupUiFlushDelayMilliseconds).ConfigureAwait(false);
        if (!_dispatcher.HasShutdownStarted)
        {
            _ = _dispatcher.BeginInvoke((Action)(() => FlushVisibleSetupLog(generation)), DispatcherPriority.ContextIdle);
        }
    }

    private void FlushVisibleSetupLog(int generation)
    {
        if (generation != _setupOutputGeneration)
        {
            return;
        }

        string text;
        lock (_visibleSetupLogBufferLock)
        {
            if (generation != _setupOutputGeneration)
            {
                _visibleSetupLogBuffer.Clear();
                _isVisibleSetupLogFlushQueued = false;
                return;
            }

            text = _visibleSetupLogBuffer.ToString();
            _visibleSetupLogBuffer.Clear();
            _isVisibleSetupLogFlushQueued = false;
        }

        if (!string.IsNullOrEmpty(text))
        {
            SetupLogText = BuildVisibleSetupLog(SetupLogText, text);
        }
    }

    private static void ResetQuickstartInstallLog()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(QuickstartInstallLogPath)!);
            lock (QuickstartInstallLogLock)
            {
                File.WriteAllText(
                    QuickstartInstallLogPath,
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] Quickstart install log started.{Environment.NewLine}");
            }
        }
        catch
        {
            // The visible setup log is still available if the persistent log cannot be written.
        }
    }

    private static void AppendQuickstartInstallLog(string text)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(QuickstartInstallLogPath)!);
            lock (QuickstartInstallLogLock)
            {
                File.AppendAllText(QuickstartInstallLogPath, text);
            }
        }
        catch
        {
            // Logging must never break first-time setup.
        }
    }

    private void ApplySetupInstallProgress(SetupInstallProgress progress)
    {
        if (progress.IsInstalled == true && !_dispatcher.HasShutdownStarted)
        {
            _ = _dispatcher.BeginInvoke(
                (Action)(() => MarkSetupComponentInstalled(progress.ComponentTitle)),
                DispatcherPriority.Background);
        }

        var generation = _setupOutputGeneration;
        lock (_setupInstallProgressLock)
        {
            _pendingSetupInstallProgress = progress;
            if (_isSetupInstallProgressFlushQueued)
            {
                return;
            }

            _isSetupInstallProgressFlushQueued = true;
        }

        _ = FlushSetupInstallProgressLaterAsync(generation);
    }

    private void MarkSetupComponentInstalled(string componentTitle)
    {
        var component = SetupComponents.FirstOrDefault(item =>
            string.Equals(item.Title, componentTitle, StringComparison.OrdinalIgnoreCase));
        component?.MarkInstalled();
    }

    private async Task FlushSetupInstallProgressLaterAsync(int generation)
    {
        await Task.Delay(SetupUiFlushDelayMilliseconds).ConfigureAwait(false);
        if (!_dispatcher.HasShutdownStarted)
        {
            _ = _dispatcher.BeginInvoke((Action)(() => FlushSetupInstallProgress(generation)), DispatcherPriority.Background);
        }
    }

    private void FlushSetupInstallProgress(int generation)
    {
        if (generation != _setupOutputGeneration)
        {
            return;
        }

        SetupInstallProgress? progress;
        lock (_setupInstallProgressLock)
        {
            progress = _pendingSetupInstallProgress;
            _pendingSetupInstallProgress = null;
            _isSetupInstallProgressFlushQueued = false;
        }

        if (progress is null)
        {
            return;
        }

        SetupInstallProgress = progress.Percentage;
        SetupInstallProgressText = $"{progress.StatusText} ({progress.CompletedComponents} of {progress.TotalComponents})";
        SetupInstallDetailText = progress.DetailText ?? string.Empty;
    }

    private void ResetSetupOutputBuffers()
    {
        _setupOutputGeneration++;

        lock (_visibleSetupLogBufferLock)
        {
            _visibleSetupLogBuffer.Clear();
            _isVisibleSetupLogFlushQueued = false;
        }

        lock (_setupInstallProgressLock)
        {
            _pendingSetupInstallProgress = null;
            _isSetupInstallProgressFlushQueued = false;
        }
    }

    private static string BuildVisibleSetupLog(string current, string append)
    {
        var combined = current + append;
        if (combined.Length <= MaxVisibleSetupLogChars)
        {
            return combined;
        }

        var visibleTail = combined[^MaxVisibleSetupLogChars..];
        var lineBreakIndex = visibleTail.IndexOf('\n');
        if (lineBreakIndex >= 0 && lineBreakIndex + 1 < visibleTail.Length)
        {
            visibleTail = visibleTail[(lineBreakIndex + 1)..];
        }

        return "[Older output is still saved in the full quickstart install log.]" +
               Environment.NewLine +
               visibleTail;
    }

    private static string BuildVisibleSetupLogAppend(string text)
    {
        var builder = new StringBuilder();
        var normalized = text.Replace("\r", "\n");
        foreach (var rawLine in normalized.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || IsVerboseInstallerLine(line))
            {
                continue;
            }

            builder.AppendLine(line);
        }

        return builder.ToString();
    }

    private static bool IsVerboseInstallerLine(string line)
    {
        string[] verbosePrefixes =
        [
            "(Reading database",
            "Selecting previously unselected package",
            "Preparing to unpack",
            "Unpacking ",
            "Setting up ",
            "Processing triggers for",
            "Reading package lists",
            "Building dependency tree",
            "Reading state information",
            "Hit:",
            "Get:",
            "Ign:",
            "Fetched ",
            "The following ",
            "The following additional packages",
            "Suggested packages:",
            "Recommended packages:",
            "After this operation",
            "Need to get",
            "Requirement already satisfied:",
            "Collecting ",
            "Downloading ",
            "Installing collected packages:"
        ];

        return verbosePrefixes.Any(prefix => line.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    }

    private void ProductChoice_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (nameof(QuickstartProductViewModel.IsSelected)
            or nameof(QuickstartProductViewModel.IsInstalled)))
        {
            return;
        }

        OnPropertyChanged(nameof(HasSelectedProducts));
        OnPropertyChanged(nameof(CanLeaveProductSelection));
        OnPropertyChanged(nameof(InstallProductsButtonText));
        InstallSelectedProductsCommand.RaiseCanExecuteChanged();
        ContinueCommand.RaiseCanExecuteChanged();
    }

    private async Task RefreshProductsAsync()
    {
        await RunBusyAsync("Checking installed mods", () => RefreshProductsCoreAsync()).ConfigureAwait(true);
    }

    /// <summary>
    /// Reads the server manager status so already-installed products show as Installed and stay
    /// locked. A failed probe, a missing entry, or a state this build does not recognise leaves the
    /// row locked as well: Quickstart only installs what the manager confirms is not installed.
    /// </summary>
    private async Task RefreshProductsCoreAsync(CancellationToken cancellationToken = default)
    {
        ProductStatusText = "Checking installed mods";
        ProductStatusBackground = StatusChecking;

        var result = await _serverManagement.GetStatusAsync(cancellationToken).ConfigureAwait(true);
        if (!result.IsSuccess)
        {
            foreach (var product in ProductChoices)
            {
                product.ApplyStatus(ServerInstallState.Unknown);
            }

            ProductStatusText = "Mod status unavailable";
            ProductStatusBackground = StatusUnknown;
            AppendSetupLog(
                $"Could not read installed mods: {result.Error}{Environment.NewLine}" +
                $"Refresh installed mods before selecting anything to install.{Environment.NewLine}");
            RaiseProductSelectionState();
            return;
        }

        foreach (var product in ProductChoices)
        {
            product.ApplyStatus(ResolveReportedState(result, product.Product));
        }

        UpdateProductSelectionSummary();
    }

    /// <summary>
    /// The state the manager reports for a product. A failed read, an absent entry, and a value this
    /// build cannot parse all resolve to Unknown rather than being treated as "not installed".
    /// </summary>
    internal static ServerInstallState ResolveReportedState(ServerStatusResult? result, ServerProduct product)
    {
        if (result?.IsSuccess != true)
        {
            return ServerInstallState.Unknown;
        }

        return result.Snapshot!.Find(product)?.State switch
        {
            ServerInstallState.NotInstalled => ServerInstallState.NotInstalled,
            ServerInstallState.Installed => ServerInstallState.Installed,
            ServerInstallState.NeedsRepair => ServerInstallState.NeedsRepair,
            _ => ServerInstallState.Unknown
        };
    }

    /// <summary>
    /// Installs the ticked products one at a time on the production branch. A failure stops that
    /// product only: earlier successes stay installed and the row offers Retry or Skip.
    /// </summary>
    private async Task InstallSelectedProductsAsync()
    {
        var selected = ProductChoices.Where(product => product.IsSelected).ToArray();
        if (selected.Length == 0)
        {
            return;
        }

        await RunDistroBusyAsync("Installing mods", async () =>
        {
            IsInstallingProducts = true;
            ResetSetupOutputBuffers();
            SetupLogText = string.Empty;
            SetupInstallProgress = 0;
            SetupInstallDetailText = string.Empty;
            try
            {
                for (var index = 0; index < selected.Length; index++)
                {
                    var product = selected[index];
                    product.ResetInstallState();
                    SetupInstallProgressText = $"Installing {product.Title} ({index + 1} of {selected.Length})...";
                    SetupInstallProgress = index * 100d / selected.Length;
                    await InstallProductCoreAsync(product).ConfigureAwait(true);
                }

                SetupInstallProgress = 100;
                SetupInstallProgressText = BuildProductInstallSummary(selected);
                UpdateProductSelectionSummary();
            }
            finally
            {
                IsInstallingProducts = false;
            }
        }).ConfigureAwait(true);
    }

    /// <summary>Refreshes the Choose Your Mods summary and command state after status or install changes.</summary>
    private void UpdateProductSelectionSummary()
    {
        var installedCount = ProductChoices.Count(product => product.IsInstalled);
        ProductStatusText = installedCount == 0
            ? "No mods installed yet"
            : $"{installedCount} of {ProductChoices.Count} installed";
        ProductStatusBackground = installedCount == 0 ? StatusWarn : StatusGood;
        if (ProductChoices.Any(product => product.ReportedState == ServerInstallState.Unknown))
        {
            ProductStatusText = "Mod status unavailable";
            ProductStatusBackground = StatusUnknown;
        }

        RaiseProductSelectionState();
    }

    /// <summary>
    /// Re-reads the selection-derived state after a status refresh may have dropped ticks, without
    /// overwriting a status summary the caller already set.
    /// </summary>
    private void RaiseProductSelectionState()
    {
        OnPropertyChanged(nameof(HasSelectedProducts));
        OnPropertyChanged(nameof(CanLeaveProductSelection));
        OnPropertyChanged(nameof(InstallProductsButtonText));
        RaiseCommandStates();
    }

    internal static string BuildProductInstallSummary(IReadOnlyList<QuickstartProductViewModel> attempted)
    {
        var installed = attempted.Count(product => product.InstallState == QuickstartProductInstallState.Installed);
        var failed = attempted.Count(product => product.InstallState == QuickstartProductInstallState.Failed);
        if (failed == 0)
        {
            return $"Installed {installed} of {attempted.Count} mods.";
        }

        return $"Installed {installed} of {attempted.Count} mods. {failed} need attention - retry or skip.";
    }

    /// <summary>
    /// Runs one install on the production branch. Quickstart never offers the development branch: a
    /// first-run user should land on the branch the mod ships to players.
    /// </summary>
    private Task InstallProductCoreAsync(QuickstartProductViewModel product)
    {
        return InstallProductGuardedAsync(
            product,
            token => _serverManagement.GetStatusAsync(token),
            () => _serverManagement.InstallAsync(product.Product, ServerBranchChannel.Main, AppendSetupLog),
            AppendSetupLog);
    }

    /// <summary>
    /// The single install path behind both the batch run and Retry. The manager status is re-read
    /// immediately before the install command, on this explicit user action only, and the install is
    /// never issued unless the fresh answer is an explicit not-installed. A row blocked here stays
    /// Failed so Retry can pick it up once the status recovers.
    /// </summary>
    internal static async Task InstallProductGuardedAsync(
        QuickstartProductViewModel product,
        Func<CancellationToken, Task<ServerStatusResult>> readStatus,
        Func<Task<CommandResult>> install,
        Action<string> log,
        CancellationToken cancellationToken = default)
    {
        product.SetInstallState(QuickstartProductInstallState.Installing);
        log($"{Environment.NewLine}Checking the current install status for {product.Title}...{Environment.NewLine}");

        ServerStatusResult? status;
        try
        {
            status = await readStatus(cancellationToken).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            status = ServerStatusResult.Failed(ex.Message);
        }

        var state = ResolveReportedState(status, product.Product);
        product.ApplyStatus(state);

        if (state != ServerInstallState.NotInstalled)
        {
            var blocked = DescribeBlockedInstall(product.Title, state);
            if (state == ServerInstallState.Installed)
            {
                // Already present: record it as installed rather than a failure so onboarding can
                // still move on, and leave removal to the Mods page.
                product.SetInstallState(QuickstartProductInstallState.Installed, blocked);
            }
            else
            {
                product.SetInstallState(QuickstartProductInstallState.Failed, blocked);
            }

            log($"{blocked}{Environment.NewLine}");
            return;
        }

        log($"Installing {product.Title} on the Main branch...{Environment.NewLine}");

        try
        {
            var result = await install().ConfigureAwait(true);

            if (result.Succeeded)
            {
                product.SetInstallState(QuickstartProductInstallState.Installed);
                log($"{product.Title} installed.{Environment.NewLine}");
                return;
            }

            var error = (result.StandardError + result.StandardOutput).Trim();
            product.SetInstallState(
                QuickstartProductInstallState.Failed,
                string.IsNullOrWhiteSpace(error) ? $"Exit code {result.ExitCode}." : TakeLastLines(error, 3));
            log($"{product.Title} install failed.{Environment.NewLine}");
        }
        catch (Exception ex)
        {
            product.SetInstallState(QuickstartProductInstallState.Failed, ex.Message);
            log($"{product.Title} install failed: {ex.Message}{Environment.NewLine}");
        }
    }

    /// <summary>The guidance shown on a row whose fresh status blocked the install.</summary>
    internal static string DescribeBlockedInstall(string title, ServerInstallState state)
    {
        return state switch
        {
            ServerInstallState.Installed =>
                $"{title} is already installed. Manage or remove it from the Mods page.",
            ServerInstallState.NeedsRepair =>
                $"{title} needs repair, so Quickstart did not install it. Repair or reinstall it from the Mods page.",
            _ =>
                $"Could not confirm whether {title} is installed, so nothing was installed. Refresh installed mods and try again, or manage it from the Mods page."
        };
    }

    private async Task RetryProductInstallAsync(QuickstartProductViewModel product)
    {
        if (IsBusy)
        {
            return;
        }

        await RunDistroBusyAsync($"Installing {product.Title}", async () =>
        {
            IsInstallingProducts = true;
            try
            {
                SetupInstallProgressText = $"Retrying {product.Title}...";
                await InstallProductCoreAsync(product).ConfigureAwait(true);
                SetupInstallProgressText = product.InstallState == QuickstartProductInstallState.Installed
                    ? $"{product.Title} installed."
                    : $"{product.Title} still needs attention.";
            }
            finally
            {
                IsInstallingProducts = false;
            }
        }).ConfigureAwait(true);
    }

    /// <summary>Leaves a failed product uninstalled and lets the user move on.</summary>
    private void SkipProductInstall(QuickstartProductViewModel product)
    {
        product.SetInstallState(QuickstartProductInstallState.Skipped, product.ResultDetail);
        AppendSetupLog($"Skipped {product.Title}. Install it later from the Mods page.{Environment.NewLine}");
        RaiseCommandStates();
    }

    private static string TakeLastLines(string text, int maxLines)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n', StringSplitOptions.RemoveEmptyEntries);
        return string.Join(Environment.NewLine, lines.Skip(Math.Max(0, lines.Length - maxLines)));
    }

    private IReadOnlyList<string> GetSelectedProductKeys()
    {
        return ProductChoices
            .Where(product => product.IsSelected || product.InstallState != QuickstartProductInstallState.Pending)
            .Select(product => ServerManagementService.ToProductToken(product.Product))
            .ToArray();
    }

    private IReadOnlyDictionary<string, string> GetProductInstallResults()
    {
        return ProductChoices.ToDictionary(
            product => ServerManagementService.ToProductToken(product.Product),
            product => product.ToInstallResultKey(),
            StringComparer.OrdinalIgnoreCase);
    }

    private void RaiseCommandStates()
    {
        InstallRecommendedCommand.RaiseCanExecuteChanged();
        InstallSelectedProductsCommand.RaiseCanExecuteChanged();
        RefreshProductsCommand.RaiseCanExecuteChanged();
        ContinueCommand.RaiseCanExecuteChanged();
        SkipRecommendedSetupCommand.RaiseCanExecuteChanged();
        BackCommand.RaiseCanExecuteChanged();
        ToggleTechnicalDetailsCommand.RaiseCanExecuteChanged();
        TogglePresetOptionsCommand.RaiseCanExecuteChanged();
        RefreshSetupCommand.RaiseCanExecuteChanged();
        SaveOpenRouterCommand.RaiseCanExecuteChanged();
        RefreshOpenRouterCommand.RaiseCanExecuteChanged();
        SaveHuggingFaceCommand.RaiseCanExecuteChanged();
        RefreshHuggingFaceCommand.RaiseCanExecuteChanged();
        RefreshReadyCommand.RaiseCanExecuteChanged();
        StartServerCommand.RaiseCanExecuteChanged();
        AdvancedSettingsCommand.RaiseCanExecuteChanged();
        UpdateDistroCommand.RaiseCanExecuteChanged();
    }

    /// <summary>
    /// True when the credential target belongs to a product the manager reports as installed. Targets
    /// are matched on database name, which is what the manager reports for each product.
    /// </summary>
    private bool IsInstalledCredentialTarget(OpenRouterTargetStatus target)
    {
        var match = ProductChoices.FirstOrDefault(product =>
            string.Equals(
                ServerManagementService.ToProductToken(product.Product),
                target.DatabaseName,
                StringComparison.OrdinalIgnoreCase) ||
            string.Equals(product.Key, ExtractTargetKey(target.TargetName), StringComparison.OrdinalIgnoreCase));

        // An unrecognised target (CHIM's "dwemer" database, or a future product) is kept: dropping it
        // would silently stop applying the key.
        return match is null || match.IsInstalled;
    }

    private static string ExtractTargetKey(string targetName)
    {
        var separator = targetName.IndexOf('/');
        return (separator < 0 ? targetName : targetName[..separator]).Trim();
    }

    private static string BuildOpenRouterStatusDetail(OpenRouterSyncStatus status)
    {
        if (status.HasError)
        {
            return status.Error ?? "OpenRouter status could not be checked.";
        }

        if (status.Targets.Count == 0)
        {
            return "No installed game database responded. Run Update Distro, then apply the key again.";
        }

        var parts = new List<string>();
        var updated = status.Targets
            .Where(target => target.WasUpdated)
            .Select(target => target.TargetName)
            .ToArray();
        var configured = status.Targets
            .Where(target => target.IsConfigured && !target.WasUpdated)
            .Select(target => target.TargetName)
            .ToArray();
        var skipped = status.Targets
            .Where(target => target.IsSkipped)
            .Select(target => $"{target.TargetName}: {target.StatusText}")
            .ToArray();
        var failed = status.Targets
            .Where(target => !target.IsConfigured && !target.IsSkipped)
            .Select(target => string.IsNullOrWhiteSpace(target.Error)
                ? $"{target.TargetName}: {target.StatusText}"
                : $"{target.TargetName}: {target.StatusText} - {target.Error}")
            .ToArray();

        if (updated.Length > 0)
        {
            parts.Add("Saved to " + string.Join(", ", updated) + ".");
        }

        if (configured.Length > 0)
        {
            parts.Add("Already configured in " + string.Join(", ", configured) + ".");
        }

        if (failed.Length > 0)
        {
            parts.Add("Needs attention: " + string.Join("; ", failed) + ".");
        }

        if (skipped.Length > 0)
        {
            parts.Add("Skipped unavailable targets: " + string.Join("; ", skipped) + ".");
        }

        return parts.Count == 0
            ? "Paste your key and apply it to the installed game profiles."
            : string.Join(" ", parts);
    }

    private static bool IsHuggingFaceReady(HuggingFaceTokenStatus? status)
    {
        return status?.IsConfigured == true &&
               status.IsValid == true &&
               status.ModelAccess.Count > 0 &&
               status.ModelAccess.All(model => string.Equals(model.AccessStatus, "granted", StringComparison.OrdinalIgnoreCase));
    }
}

public sealed class PresetOptionViewModel : ObservableObject
{
    private bool _isSelected;

    public PresetOptionViewModel(SetupPresetKey key, string title, string hardwareLabel, string description)
    {
        Key = key;
        Title = title;
        HardwareLabel = hardwareLabel;
        Description = description;
    }

    public SetupPresetKey Key { get; }

    public string Title { get; }

    public string HardwareLabel { get; }

    public string Description { get; }

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (SetProperty(ref _isSelected, value))
            {
                OnPropertyChanged(nameof(StatusText));
                OnPropertyChanged(nameof(StatusBackground));
            }
        }
    }

    public string StatusText => IsSelected ? "Selected" : "Available";

    public string StatusBackground => IsSelected ? "#285A2D" : "#555555";
}

public sealed class SetupComponentQuickstartViewModel(
    string title,
    string description,
    bool isInstalled,
    string statusText,
    string statusBackground,
    string? detailText) : ObservableObject
{
    private bool _isInstalled = isInstalled;
    private string _statusText = statusText;
    private string _statusBackground = statusBackground;

    public string Title { get; } = title;

    public string Description { get; } = description;

    public bool IsInstalled
    {
        get => _isInstalled;
        private set
        {
            if (SetProperty(ref _isInstalled, value))
            {
                OnPropertyChanged(nameof(StatusIconData));
                OnPropertyChanged(nameof(StatusIconColor));
                OnPropertyChanged(nameof(StatusToolTip));
            }
        }
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public string StatusBackground
    {
        get => _statusBackground;
        private set => SetProperty(ref _statusBackground, value);
    }

    public string StatusIconData => IsInstalled
        ? "M 4 10 L 8 14 L 16 6"
        : "M 5 5 L 15 15 M 15 5 L 5 15";

    public string StatusIconColor => IsInstalled ? "#61C46A" : "#E14A4A";

    public string StatusToolTip => IsInstalled ? "Installed" : "Not installed";

    public string? DetailText { get; } = detailText;

    public bool HasDetail => !string.IsNullOrWhiteSpace(DetailText);

    public void MarkInstalled()
    {
        IsInstalled = true;
        StatusText = "Done";
        StatusBackground = "#285A2D";
    }
}

public sealed class CredentialTargetViewModel(
    string title,
    string statusText,
    string statusBackground,
    string? detailText)
{
    public string Title { get; } = title;

    public string StatusText { get; } = statusText;

    public string StatusBackground { get; } = statusBackground;

    public string? DetailText { get; } = detailText;

    public bool HasDetail => !string.IsNullOrWhiteSpace(DetailText);
}

public sealed class HuggingFaceQuickstartModelViewModel : ObservableObject
{
    private string _statusText = "Checking";
    private string _statusBackground = "#555555";
    private string _detailText;

    public HuggingFaceQuickstartModelViewModel(
        string key,
        string title,
        string repositoryId,
        string accessUrl,
        Action openAccessPage)
    {
        Key = key;
        Title = title;
        RepositoryId = repositoryId;
        AccessUrl = accessUrl;
        _detailText = $"Checking {repositoryId}...";
        OpenAccessPageCommand = new RelayCommand(openAccessPage);
    }

    public string Key { get; }

    public string Title { get; }

    public string RepositoryId { get; }

    public string AccessUrl { get; }

    public RelayCommand OpenAccessPageCommand { get; }

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public string StatusBackground
    {
        get => _statusBackground;
        private set => SetProperty(ref _statusBackground, value);
    }

    public string DetailText
    {
        get => _detailText;
        private set => SetProperty(ref _detailText, value);
    }

    public void SetCheckingState()
    {
        StatusText = "Checking";
        StatusBackground = "#555555";
        DetailText = $"Checking {RepositoryId}...";
    }

    public void SetUnknownState()
    {
        StatusText = "Unable to verify";
        StatusBackground = "#4F3C7A";
        DetailText = $"Open {RepositoryId}, accept access, then refresh.";
    }

    public void ApplyStatus(HuggingFaceModelAccessStatus status)
    {
        switch (status.AccessStatus)
        {
            case "granted":
                StatusText = "Access granted";
                StatusBackground = "#285A2D";
                DetailText = $"{status.RepositoryId} is reachable.";
                break;
            case "needs_approval":
                StatusText = "Accept access";
                StatusBackground = "#6A3A12";
                DetailText = "Open the access page, accept the terms, then refresh.";
                break;
            case "token_required":
                StatusText = "Token required";
                StatusBackground = "#6A3A12";
                DetailText = "Paste a Hugging Face token before checking this model.";
                break;
            case "invalid_token":
                StatusText = "Invalid token";
                StatusBackground = "#7A2828";
                DetailText = status.Error ?? "Hugging Face rejected the token.";
                break;
            default:
                StatusText = "Unable to verify";
                StatusBackground = "#4F3C7A";
                DetailText = status.Error ?? $"Open {status.RepositoryId}, accept access, then refresh.";
                break;
        }
    }
}
