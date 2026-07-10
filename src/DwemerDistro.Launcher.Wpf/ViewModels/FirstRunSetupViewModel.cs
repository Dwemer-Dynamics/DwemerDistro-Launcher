using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using DwemerDistro.Launcher.Wpf.Services;
using Application = System.Windows.Application;

namespace DwemerDistro.Launcher.Wpf.ViewModels;

public sealed class FirstRunSetupViewModel : ObservableObject
{
    private const int IntroStepIndex = 0;
    private const int UpdateDistroStepIndex = 1;
    private const int HuggingFaceStepIndex = 2;
    private const int SetupStepIndex = 3;
    private const int ReadyStepIndex = 4;
    private const string StatusChecking = "#555555";
    private const string StatusGood = "#285A2D";
    private const string StatusWarn = "#6A3A12";
    private const string StatusBad = "#7A2828";
    private const string StatusUnknown = "#4F3C7A";
    private const int MaxVisibleSetupLogChars = 60000;
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
    private readonly OnboardingStateService _onboardingState = new();

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

        InstallRecommendedCommand = new AsyncRelayCommand(InstallRecommendedAsync, () => !IsBusy);
        ContinueCommand = new AsyncRelayCommand(ContinueAsync, CanContinue);
        SkipRecommendedSetupCommand = new AsyncRelayCommand(SkipRecommendedSetupAsync, () => !IsBusy && IsSetupIntroStep);
        BackCommand = new RelayCommand(Back, () => !IsBusy && CurrentStepIndex > 0);
        ToggleTechnicalDetailsCommand = new RelayCommand(() => ShowTechnicalDetails = !ShowTechnicalDetails, () => !IsBusy);
        TogglePresetOptionsCommand = new RelayCommand(() => ShowPresetOptions = !ShowPresetOptions, () => !IsBusy);
        UpdateDistroCommand = new AsyncRelayCommand(UpdateDistroAsync, () => !IsBusy && !_mainWindowViewModel.IsDistroUpdateInProgress);
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
    }

    public event Action? RequestClose;

    public ObservableCollection<SetupComponentQuickstartViewModel> SetupComponents { get; }

    public ObservableCollection<PresetOptionViewModel> PresetOptions { get; }

    public ObservableCollection<CredentialTargetViewModel> OpenRouterTargets { get; }

    public ObservableCollection<HuggingFaceQuickstartModelViewModel> HuggingFaceModelAccessItems { get; }

    public ObservableCollection<CredentialTargetViewModel> VoiceApplyTargets { get; }

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
                ShowTechnicalDetails = false;
                RaiseCommandStates();
            }
        }
    }

    public bool IsSetupIntroStep => CurrentStepIndex == IntroStepIndex;

    public bool IsUpdateDistroStep => CurrentStepIndex == UpdateDistroStepIndex;

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
                RaiseCommandStates();
            }
        }
    }

    public bool IsSetupSelectionEnabled => !IsBusy && IsSetupStep;

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
        HuggingFaceStepIndex => "Connect Hugging Face",
        SetupStepIndex => "Install Components",
        _ => "Setup Complete"
    };

    public string StepSubtitle => CurrentStepIndex switch
    {
        IntroStepIndex => "The launcher picked the recommended setup for this machine.",
        UpdateDistroStepIndex => "Pull the latest distro scripts first.",
        HuggingFaceStepIndex => "The installers use Hugging Face to download cloned voice models.",
        SetupStepIndex => "Install the required voice and speech components.",
        _ => "Start the server, switch on the game, and talk."
    };

    public string PrimaryContinueText => CurrentStepIndex switch
    {
        IntroStepIndex => "Continue",
        UpdateDistroStepIndex => "Next",
        HuggingFaceStepIndex => "Continue to Install",
        SetupStepIndex => "Continue to Start Server",
        _ => "Ready"
    };

    public string InstallRecommendedButtonText =>
        _setupStatus?.AllRequiredInstalled == true ? "Continue to Start Server" : "Install Recommended Setup";

    public string SelectedPresetTitle => _selectedPreset.Title;

    public string SelectedPresetHardware => _selectedPreset.HardwareLabel;

    public string SelectedPresetDescription => _selectedPreset.Description;

    public string SelectedVoiceEngine => _selectedPreset.VoiceEngineName;

    public string HardwareSummary
    {
        get => _hardwareSummary;
        private set => SetProperty(ref _hardwareSummary, value);
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
            await _huggingFaceToken.EnsureManagedTokenAsync(cancellationToken).ConfigureAwait(true);
            await RefreshHuggingFaceStatusCoreAsync(cancellationToken).ConfigureAwait(true);
        }).ConfigureAwait(true);
    }

    public static async Task<bool> ShouldShowFirstRunSetupAsync(CancellationToken cancellationToken = default)
    {
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
        await RunBusyAsync($"Installing {_selectedPreset.Title}", async () =>
        {
            SetupLogText = string.Empty;
            ResetQuickstartInstallLog();
            SetupInstallProgress = 0;
            SetupInstallProgressText = $"Preparing {_selectedPreset.Title}...";
            SetupInstallDetailText = string.Empty;
            IsInstallingSetup = true;
            AppendSetupLog($"Quickstart install log: {QuickstartInstallLogPath}{Environment.NewLine}");
            AppendSetupLog($"Recommended path: {_selectedPreset.Title}{Environment.NewLine}");
            try
            {
                var status = await _distroSetup.InstallPresetAsync(
                        _selectedPreset,
                        AppendSetupLog,
                        ApplySetupInstallProgress)
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
        await RunBusyAsync("Updating distro", async () =>
        {
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
                SetupInstallProgress = updated ? 100 : 0;
                SetupInstallProgressText = updated ? "Distro update complete." : "Distro update needs attention.";
                AppendSetupLog(updated
                    ? "Distro update completed. Refreshed quickstart checks." + Environment.NewLine
                    : "Distro update reported issues. Check the main launcher log." + Environment.NewLine);
                await RefreshSetupCoreAsync().ConfigureAwait(true);
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

        CurrentStepIndex = ReadyStepIndex;
        await PrepareReadyAsync().ConfigureAwait(true);
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

            ReadySummary = $"{_voiceEngineStatus.DisplayName} is ready.";
        }).ConfigureAwait(true);
    }

    private async Task StartServerFromSetupAsync()
    {
        await MarkReadyAsync().ConfigureAwait(true);
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
                    state.IsInstalled ? "Done" : "Install",
                    state.IsInstalled ? StatusGood : StatusWarn,
                    state.Error));
            }
            else
            {
                SetupComponents.Add(new SetupComponentQuickstartViewModel(
                    component.Title,
                    component.Description,
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

        foreach (var target in status.Targets)
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
        VoiceStatusText = status.HasUsableEngine ? $"{status.DisplayName} detected" : "Voice engine needed";
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
                IsHuggingFaceReady(_huggingFaceStatus))
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
        return _skipHuggingFaceStep && currentStepIndex == UpdateDistroStepIndex
            ? SetupStepIndex
            : Math.Min(ReadyStepIndex, currentStepIndex + 1);
    }

    private int GetPreviousStepIndex(int currentStepIndex)
    {
        return _skipHuggingFaceStep && currentStepIndex == SetupStepIndex
            ? UpdateDistroStepIndex
            : Math.Max(0, currentStepIndex - 1);
    }

    private int GetTotalStepCount()
    {
        return _skipHuggingFaceStep ? 4 : 5;
    }

    private int GetDisplayStepNumber()
    {
        return _skipHuggingFaceStep && CurrentStepIndex > HuggingFaceStepIndex
            ? CurrentStepIndex
            : CurrentStepIndex + 1;
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

        void AppendVisible()
        {
            SetupLogText = BuildVisibleSetupLog(SetupLogText, text);
        }

        if (_dispatcher.CheckAccess())
        {
            AppendVisible();
            return;
        }

        _ = _dispatcher.BeginInvoke((Action)AppendVisible, DispatcherPriority.Background);
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
        void Apply()
        {
            SetupInstallProgress = progress.Percentage;
            SetupInstallProgressText = $"{progress.StatusText} ({progress.CompletedComponents} of {progress.TotalComponents})";
            SetupInstallDetailText = progress.DetailText ?? string.Empty;
        }

        if (_dispatcher.CheckAccess())
        {
            Apply();
            return;
        }

        _ = _dispatcher.BeginInvoke((Action)Apply, DispatcherPriority.Background);
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

    private void RaiseCommandStates()
    {
        InstallRecommendedCommand.RaiseCanExecuteChanged();
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
    string statusText,
    string statusBackground,
    string? detailText)
{
    public string Title { get; } = title;

    public string Description { get; } = description;

    public string StatusText { get; } = statusText;

    public string StatusBackground { get; } = statusBackground;

    public string? DetailText { get; } = detailText;

    public bool HasDetail => !string.IsNullOrWhiteSpace(DetailText);
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
