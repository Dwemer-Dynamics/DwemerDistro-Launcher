using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Threading;
using DwemerDistro.Launcher.Wpf.Services;
using Application = System.Windows.Application;

namespace DwemerDistro.Launcher.Wpf.ViewModels;

public sealed class FirstRunSetupViewModel : ObservableObject
{
    private const string StatusChecking = "#555555";
    private const string StatusGood = "#285A2D";
    private const string StatusWarn = "#6A3A12";
    private const string StatusBad = "#7A2828";
    private const string StatusUnknown = "#4F3C7A";

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
    private bool _showPresetOptions;
    private bool _showTechnicalDetails;
    private string _busyText = "Working";
    private string _hardwareSummary = "Detecting hardware";
    private string _hardwareDetail = "Checking GPU and recommended setup path...";
    private string _setupStatusText = "Checking setup";
    private string _setupStatusBackground = StatusChecking;
    private string _setupLogText = string.Empty;
    private string _openRouterKey = string.Empty;
    private string _openRouterStatusText = "Checking OpenRouter";
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
        BackCommand = new RelayCommand(Back, () => !IsBusy && CurrentStepIndex > 0);
        ToggleTechnicalDetailsCommand = new RelayCommand(() => ShowTechnicalDetails = !ShowTechnicalDetails, () => !IsBusy);
        TogglePresetOptionsCommand = new RelayCommand(() => ShowPresetOptions = !ShowPresetOptions, () => !IsBusy);
        SelectNvidiaPowerfulCommand = new AsyncRelayCommand(() => SelectPresetAsync(SetupPresetKey.NvidiaPowerful), () => !IsBusy);
        SelectNvidiaStandardCommand = new AsyncRelayCommand(() => SelectPresetAsync(SetupPresetKey.NvidiaStandard), () => !IsBusy);
        SelectAmdCpuCommand = new AsyncRelayCommand(() => SelectPresetAsync(SetupPresetKey.AmdCpu), () => !IsBusy);
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

    public RelayCommand BackCommand { get; }

    public RelayCommand ToggleTechnicalDetailsCommand { get; }

    public RelayCommand TogglePresetOptionsCommand { get; }

    public AsyncRelayCommand SelectNvidiaPowerfulCommand { get; }

    public AsyncRelayCommand SelectNvidiaStandardCommand { get; }

    public AsyncRelayCommand SelectAmdCpuCommand { get; }

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
                OnPropertyChanged(nameof(IsSetupStep));
                OnPropertyChanged(nameof(IsOpenRouterStep));
                OnPropertyChanged(nameof(IsHuggingFaceStep));
                OnPropertyChanged(nameof(IsReadyStep));
                OnPropertyChanged(nameof(ShowContinueButton));
                OnPropertyChanged(nameof(CurrentStepLabel));
                OnPropertyChanged(nameof(StepTitle));
                OnPropertyChanged(nameof(StepSubtitle));
                OnPropertyChanged(nameof(PrimaryContinueText));
                OnPropertyChanged(nameof(InstallRecommendedButtonText));
                ShowTechnicalDetails = false;
                RaiseCommandStates();
            }
        }
    }

    public bool IsHuggingFaceStep => CurrentStepIndex == 0;

    public bool IsSetupStep => CurrentStepIndex == 1;

    public bool IsOpenRouterStep => CurrentStepIndex == 2;

    public bool IsReadyStep => CurrentStepIndex == 3;

    public bool ShowContinueButton => !IsReadyStep;

    public string CurrentStepLabel => $"Step {CurrentStepIndex + 1} of 4";

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                RaiseCommandStates();
            }
        }
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
        0 => "Connect Hugging Face",
        1 => "Install the recommended setup",
        2 => "Paste your OpenRouter key",
        _ => "Ready to play"
    };

    public string StepSubtitle => CurrentStepIndex switch
    {
        0 => "The installers use Hugging Face to download cloned voice models.",
        1 => "The launcher picks the right path from your hardware and installs only what first-time users need.",
        2 => "One key is applied to every installed Dwemer game profile.",
        _ => "The launcher detected your voice engine and applied it to the installed game profiles."
    };

    public string PrimaryContinueText => CurrentStepIndex switch
    {
        0 => "Continue to Install",
        1 => "Continue to OpenRouter",
        2 => "Continue to Ready",
        _ => "Ready"
    };

    public string InstallRecommendedButtonText =>
        _setupStatus?.AllRequiredInstalled == true ? "Continue to OpenRouter" : "Install Recommended Setup";

    public string SelectedPresetTitle => _selectedPreset.Title;

    public string SelectedPresetHardware => _selectedPreset.HardwareLabel;

    public string SelectedPresetDescription => _selectedPreset.Description;

    public string SelectedVoiceEngine => _selectedPreset.VoiceEngineName;

    public bool ShowNvidiaPowerfulSwitch => _selectedPreset.Key != SetupPresetKey.NvidiaPowerful;

    public bool ShowNvidiaStandardSwitch => _selectedPreset.Key != SetupPresetKey.NvidiaStandard;

    public bool ShowAmdCpuSwitch => _selectedPreset.Key != SetupPresetKey.AmdCpu;

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

    public async Task InitializeAsync()
    {
        await RunBusyAsync("Checking your setup", async () =>
        {
            var hardware = await _hardwareDetection.DetectAsync().ConfigureAwait(true);
            HardwareSummary = hardware.Summary;
            HardwareDetail = hardware.Detail;
            ApplyPreset(hardware.RecommendedPreset);
            await RefreshSetupCoreAsync().ConfigureAwait(true);
            await RefreshOpenRouterStatusCoreAsync().ConfigureAwait(true);
            await RefreshHuggingFaceStatusCoreAsync().ConfigureAwait(true);
        }).ConfigureAwait(true);
    }

    public static async Task<bool> ShouldShowFirstRunSetupAsync(CancellationToken cancellationToken = default)
    {
        var processRunner = new ProcessRunner();
        var wsl = new WslService(processRunner);
        var hardwareDetection = new HardwareDetectionService(processRunner);
        var setup = new DistroSetupService(wsl);
        var openRouter = new OpenRouterCredentialSyncService(wsl);
        var huggingFace = new HuggingFaceTokenService(wsl);
        var voiceEngine = new VoiceEngineService(wsl);
        var stateService = new OnboardingStateService();

        var state = await stateService.LoadAsync(cancellationToken).ConfigureAwait(false);
        var hardware = await hardwareDetection.DetectAsync(cancellationToken).ConfigureAwait(false);
        var preset = setup.GetPreset(hardware.RecommendedPreset);
        var setupStatus = await setup.ProbeAsync(preset, cancellationToken).ConfigureAwait(false);
        var voiceStatus = await voiceEngine.GetStatusAsync(preset, cancellationToken).ConfigureAwait(false);

        if (!setupStatus.DistroExists || !setupStatus.AllRequiredInstalled || !voiceStatus.HasUsableEngine)
        {
            return true;
        }

        var openRouterStatus = await openRouter.GetStatusAsync(cancellationToken).ConfigureAwait(false);
        var huggingFaceStatus = await huggingFace.GetStatusAsync(cancellationToken).ConfigureAwait(false);

        return !state.Completed ||
               !openRouterStatus.AllAvailableTargetsConfigured ||
               !IsHuggingFaceReady(huggingFaceStatus);
    }

    private async Task InstallRecommendedAsync()
    {
        if (_setupStatus?.AllRequiredInstalled == true)
        {
            CurrentStepIndex = 2;
            return;
        }

        await RunBusyAsync($"Installing {_selectedPreset.Title}", async () =>
        {
            SetupLogText = string.Empty;
            AppendSetupLog($"Recommended path: {_selectedPreset.Title}{Environment.NewLine}");
            var status = await _distroSetup.InstallPresetAsync(_selectedPreset, AppendSetupLog).ConfigureAwait(true);
            ApplySetupStatus(status);
            await RefreshVoiceStatusCoreAsync().ConfigureAwait(true);
        }).ConfigureAwait(true);
    }

    private async Task ContinueAsync()
    {
        if (!CanContinue())
        {
            return;
        }

        if (CurrentStepIndex == 2)
        {
            CurrentStepIndex = 3;
            await PrepareReadyAsync().ConfigureAwait(true);
            return;
        }

        CurrentStepIndex++;
    }

    private void Back()
    {
        if (CurrentStepIndex <= 0 || IsBusy)
        {
            return;
        }

        CurrentStepIndex--;
    }

    private async Task SelectPresetAsync(SetupPresetKey key)
    {
        await RunBusyAsync("Checking selected setup", async () =>
        {
            ApplyPreset(key);
            ShowPresetOptions = false;
            await RefreshSetupCoreAsync().ConfigureAwait(true);
        }).ConfigureAwait(true);
    }

    private async Task RefreshSetupAsync()
    {
        await RunBusyAsync("Checking setup", RefreshSetupCoreAsync).ConfigureAwait(true);
    }

    private async Task SaveOpenRouterAsync()
    {
        await RunBusyAsync("Saving OpenRouter key", async () =>
        {
            OpenRouterStatusText = "Applying key";
            OpenRouterStatusBackground = StatusChecking;
            var status = await _openRouterSync.SaveKeyAsync(OpenRouterKey).ConfigureAwait(true);
            OpenRouterKey = string.Empty;
            ApplyOpenRouterStatus(status);
        }).ConfigureAwait(true);
    }

    private async Task RefreshOpenRouterStatusAsync()
    {
        await RunBusyAsync("Checking OpenRouter", RefreshOpenRouterStatusCoreAsync).ConfigureAwait(true);
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
        await RunBusyAsync("Checking Hugging Face", RefreshHuggingFaceStatusCoreAsync).ConfigureAwait(true);
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
                ReadySummary = "A cloned voice engine was not detected. Return to the install step or use Advanced Settings.";
                return;
            }

            var targets = await _voiceEngine.ApplyVoiceEngineAsync(_voiceEngineStatus.EngineKey).ConfigureAwait(true);
            ApplyVoiceTargetStatuses(targets);

            ReadySummary = $"{_voiceEngineStatus.DisplayName} is selected. OpenRouter and Hugging Face are configured. Start the server, switch on the game, and talk.";
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

    private async Task RefreshSetupCoreAsync()
    {
        SetupStatusText = "Checking setup";
        SetupStatusBackground = StatusChecking;
        var status = await _distroSetup.ProbeAsync(_selectedPreset).ConfigureAwait(true);
        ApplySetupStatus(status);
    }

    private async Task RefreshOpenRouterStatusCoreAsync()
    {
        OpenRouterStatusText = "Checking OpenRouter";
        OpenRouterStatusBackground = StatusChecking;
        var status = await _openRouterSync.GetStatusAsync().ConfigureAwait(true);
        ApplyOpenRouterStatus(status);
    }

    private async Task RefreshHuggingFaceStatusCoreAsync()
    {
        HuggingFaceStatusText = "Checking";
        HuggingFaceStatusDetail = "Checking token and required model access...";
        HuggingFaceStatusBackground = StatusChecking;
        foreach (var item in HuggingFaceModelAccessItems)
        {
            item.SetCheckingState();
        }

        var status = await _huggingFaceToken.GetStatusAsync().ConfigureAwait(true);
        ApplyHuggingFaceStatus(status);
    }

    private async Task RefreshVoiceStatusCoreAsync()
    {
        VoiceStatusText = "Checking voice engine";
        VoiceStatusBackground = StatusChecking;
        var status = await _voiceEngine.GetStatusAsync(_selectedPreset).ConfigureAwait(true);
        ApplyVoiceStatus(status);
    }

    private void ApplyPreset(SetupPresetKey key)
    {
        _selectedPreset = _distroSetup.GetPreset(key);
        OnPropertyChanged(nameof(SelectedPresetTitle));
        OnPropertyChanged(nameof(SelectedPresetHardware));
        OnPropertyChanged(nameof(SelectedPresetDescription));
        OnPropertyChanged(nameof(SelectedVoiceEngine));
        OnPropertyChanged(nameof(ShowNvidiaPowerfulSwitch));
        OnPropertyChanged(nameof(ShowNvidiaStandardSwitch));
        OnPropertyChanged(nameof(ShowAmdCpuSwitch));
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
        SetupStatusText = status.Summary;
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
                    state.IsInstalled ? "Installed" : "Needed",
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
            OpenRouterStatusBackground = StatusUnknown;
        }
        else if (status.AllAvailableTargetsConfigured)
        {
            OpenRouterStatusText = status.AnyUpdated ? "OpenRouter key applied" : "OpenRouter key configured";
            OpenRouterStatusBackground = StatusGood;
        }
        else
        {
            OpenRouterStatusText = "OpenRouter key needed";
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
            HuggingFaceStatusDetail = "Do this first. The voice installers use Hugging Face to download Pocket-TTS and Chatterbox models.";
            HuggingFaceStatusBackground = StatusWarn;
        }
        else if (IsHuggingFaceReady(status))
        {
            var userSuffix = string.IsNullOrWhiteSpace(status.UserName) ? string.Empty : $" as {status.UserName}";
            HuggingFaceStatusText = $"Ready{userSuffix}";
            HuggingFaceStatusDetail = "Token is valid and the required cloned-voice models are reachable.";
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
            HuggingFaceStatusDetail = "Open the voice model pages, accept access, then refresh.";
            HuggingFaceStatusBackground = StatusWarn;
        }
        else
        {
            HuggingFaceStatusText = "Unable to verify";
            HuggingFaceStatusDetail = status.Error ?? "Hugging Face status could not be checked.";
            HuggingFaceStatusBackground = StatusUnknown;
        }

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
                _openRouterStatus?.AllAvailableTargetsConfigured == true,
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
            0 => IsHuggingFaceReady(_huggingFaceStatus),
            1 => _setupStatus?.AllRequiredInstalled == true,
            2 => _openRouterStatus?.AllAvailableTargetsConfigured == true,
            3 => _voiceEngineStatus?.HasUsableEngine == true,
            _ => false
        };
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

        if (_dispatcher.CheckAccess())
        {
            SetupLogText += text;
            return;
        }

        _ = _dispatcher.BeginInvoke(() => SetupLogText += text, DispatcherPriority.Background);
    }

    private void RaiseCommandStates()
    {
        InstallRecommendedCommand.RaiseCanExecuteChanged();
        ContinueCommand.RaiseCanExecuteChanged();
        BackCommand.RaiseCanExecuteChanged();
        ToggleTechnicalDetailsCommand.RaiseCanExecuteChanged();
        TogglePresetOptionsCommand.RaiseCanExecuteChanged();
        SelectNvidiaPowerfulCommand.RaiseCanExecuteChanged();
        SelectNvidiaStandardCommand.RaiseCanExecuteChanged();
        SelectAmdCpuCommand.RaiseCanExecuteChanged();
        RefreshSetupCommand.RaiseCanExecuteChanged();
        SaveOpenRouterCommand.RaiseCanExecuteChanged();
        RefreshOpenRouterCommand.RaiseCanExecuteChanged();
        SaveHuggingFaceCommand.RaiseCanExecuteChanged();
        RefreshHuggingFaceCommand.RaiseCanExecuteChanged();
        RefreshReadyCommand.RaiseCanExecuteChanged();
        StartServerCommand.RaiseCanExecuteChanged();
        AdvancedSettingsCommand.RaiseCanExecuteChanged();
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
