using System.Collections.ObjectModel;
using System.Text;
using System.Text.Json;
using System.Windows.Input;
using DwemerDistro.Launcher.Wpf.Services;

namespace DwemerDistro.Launcher.Wpf.ViewModels;

public sealed class InstallComponentsWindowViewModel : ObservableObject
{
    private readonly ProcessRunner _processRunner = new();
    private readonly WslService _wsl;
    private readonly HuggingFaceTokenService _huggingFaceToken;
    private readonly MainWindowViewModel _mainWindowViewModel;
    private readonly List<InstallComponentItemViewModel> _allItems = [];
    private readonly Dictionary<string, HuggingFaceModelAccessViewModel> _modelAccessItemsByKey;
    private readonly SemaphoreSlim _componentProbeGate = new(1, 1);
    private CancellationTokenSource? _passiveChecks;
    private Task? _passiveCheckTask;
    private int _activeOperationCount;
    private string _huggingFaceTokenStatusText = "Checking";
    private string _huggingFaceTokenDetailText = "Checking Hugging Face token...";
    private string _huggingFaceTokenStatusBackground = "#555555";
    private string _huggingFaceTokenStatusForeground = "White";
    private string _configurableComponentsStatusText = "Checking installed components...";
    private static readonly TimeSpan HuggingFaceTokenReadTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan HuggingFaceTokenWriteTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan HuggingFaceTokenStatusTimeout = TimeSpan.FromSeconds(30);
    private static readonly IReadOnlyList<ConfigurableComponentDefinition> ConfigurableComponentDefinitions =
    [
        new("text2vec", "Text2Vec", "/home/dwemer/text2vec-api", "/home/dwemer/text2vec-api/conf.sh", "start"),
        new("localwhisper", "LocalWhisper", "/home/dwemer/remote-faster-whisper", "/home/dwemer/remote-faster-whisper/conf.sh", "whisper"),
        new("xtts", "Dwemer Distro XTTS", "/home/dwemer/xtts-api-server", "/home/dwemer/xtts-api-server/conf.sh", "start"),
        new("llamacpp", "llama.cpp", "/home/dwemer/llama-cpp", "/home/dwemer/llama-cpp/conf.sh", "start"),
        new("minime", "Minime and TXT2VEC", "/home/dwemer/minime-t5", "/home/dwemer/minime-t5/conf.sh", "start"),
        new("pipertts", "Piper-TTS", "/home/dwemer/piper", "/home/dwemer/piper/conf.sh", "start"),
        new("parakeet", "Parakeet STT", "/home/dwemer/parakeet-api-server", "/home/dwemer/parakeet-api-server/conf.sh", "start"),
        new("higgs", "Higgs TTS 3", "/home/dwemer/higgs-tts", "/home/dwemer/higgs-tts/conf.sh", "higgs"),
        new("chatterbox", "Chatterbox", "/home/dwemer/chatterbox", "/home/dwemer/chatterbox/conf.sh", "start"),
        new("audiocpp", "Pocket-TTS (GPU / audio.cpp)", "/home/dwemer/audio.cpp", "/home/dwemer/audio.cpp/conf.sh", "start"),
        new("pockettts", "Pocket-TTS (CPU / Python)", "/home/dwemer/pocket-tts", "/home/dwemer/pocket-tts/conf.sh", "start"),
        new("omnivoice", "Multilingual OmniVoice TTS", "/home/dwemer/omnivoice-tts", "/home/dwemer/omnivoice-tts/conf.sh", "omnivoice")
    ];
    private static readonly IReadOnlyDictionary<string, ConfigurableComponentDefinition> ConfigurableComponentsByKey =
        ConfigurableComponentDefinitions.ToDictionary(definition => definition.Key, StringComparer.OrdinalIgnoreCase);

    public InstallComponentsWindowViewModel(MainWindowViewModel mainWindowViewModel)
    {
        _mainWindowViewModel = mainWindowViewModel;
        _wsl = new WslService(_processRunner);
        _huggingFaceToken = new HuggingFaceTokenService(_wsl);
        RefreshHuggingFaceTokenCommand = new AsyncRelayCommand(RefreshHuggingFaceTokenStatusAsync);
        RefreshComponentsCommand = new AsyncRelayCommand(RefreshInstalledStatesAsync);
        ConfigurableComponents = [];
        HuggingFaceModelAccessItems = new ObservableCollection<HuggingFaceModelAccessViewModel>(
            HuggingFaceTokenService.RequiredModelAccess.Select(definition =>
                new HuggingFaceModelAccessViewModel(
                    definition.Key,
                    definition.DisplayName,
                    definition.RepositoryId,
                    definition.AccessUrl,
                    () => _processRunner.OpenExternalUrl(definition.AccessUrl))));
        _modelAccessItemsByKey = HuggingFaceModelAccessItems.ToDictionary(item => item.Key, StringComparer.OrdinalIgnoreCase);
        Sections = new ObservableCollection<InstallComponentSectionViewModel>(
            BuildSections(_allItems));
    }

    public ObservableCollection<InstallComponentSectionViewModel> Sections { get; }

    public ObservableCollection<HuggingFaceModelAccessViewModel> HuggingFaceModelAccessItems { get; }

    public ObservableCollection<ConfigurableComponentItemViewModel> ConfigurableComponents { get; }

    public AsyncRelayCommand RefreshHuggingFaceTokenCommand { get; }

    public AsyncRelayCommand RefreshComponentsCommand { get; }

    public string ConfigurableComponentsStatusText
    {
        get => _configurableComponentsStatusText;
        private set
        {
            if (SetProperty(ref _configurableComponentsStatusText, value))
            {
                OnPropertyChanged(nameof(HasConfigurableComponentsStatus));
            }
        }
    }

    public bool HasConfigurableComponentsStatus => !string.IsNullOrWhiteSpace(ConfigurableComponentsStatusText);

    public bool HasManagedHuggingFaceToken => HuggingFaceTokenService.HasManagedToken;

    public string HuggingFaceAccessTitle => HasManagedHuggingFaceToken
        ? "Hugging Face Access"
        : "Hugging Face Token";

    public string HuggingFaceReplaceButtonText => HasManagedHuggingFaceToken
        ? "Use Own Token"
        : "Replace Token";

    public string HuggingFaceAccessSummary => HasManagedHuggingFaceToken
        ? string.Empty
        : "Add a Hugging Face token for Pocket-TTS and Chatterbox voice model downloads.";

    public bool HasHuggingFaceAccessSummary => !string.IsNullOrWhiteSpace(HuggingFaceAccessSummary);

    public string HuggingFaceTokenStatusText
    {
        get => _huggingFaceTokenStatusText;
        private set => SetProperty(ref _huggingFaceTokenStatusText, value);
    }

    public string HuggingFaceTokenDetailText
    {
        get => _huggingFaceTokenDetailText;
        private set => SetProperty(ref _huggingFaceTokenDetailText, value);
    }

    public string HuggingFaceTokenStatusBackground
    {
        get => _huggingFaceTokenStatusBackground;
        private set => SetProperty(ref _huggingFaceTokenStatusBackground, value);
    }

    public string HuggingFaceTokenStatusForeground
    {
        get => _huggingFaceTokenStatusForeground;
        private set => SetProperty(ref _huggingFaceTokenStatusForeground, value);
    }

    public bool HasActiveOperation => _activeOperationCount > 0;

    // Raised on the UI thread once the last install or configuration run finishes, so a page
    // that was held open for that work can be released.
    public event EventHandler? ActiveOperationsCompleted;

    // The page is only safe to drop when the user is somewhere else and nothing is running.
    // Callers must confirm the tab itself changed: nested selectors bubble SelectionChanged.
    public static bool ShouldUnloadComponentsPage(bool isComponentsTabSelected, bool hasActiveOperation)
    {
        return !isComponentsTabSelected && !hasActiveOperation;
    }

    // The page is built when Components opens and thrown away when the user leaves, so the
    // passive WSL probes are scoped to that mounted lifetime instead of the window's.
    public Task AttachAsync()
    {
        _passiveChecks?.Cancel();
        _passiveChecks = new CancellationTokenSource();
        _passiveCheckTask = InitializeAsync(_passiveChecks.Token);
        return _passiveCheckTask;
    }

    public async Task DetachAsync()
    {
        var source = _passiveChecks;
        var passiveCheckTask = _passiveCheckTask;
        _passiveChecks = null;
        _passiveCheckTask = null;
        source?.Cancel();

        try
        {
            if (passiveCheckTask is not null)
            {
                await passiveCheckTask.ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Leaving Components cancels its passive checks by design.
        }
        finally
        {
            source?.Dispose();
        }
    }

    private async Task TrackOperationAsync(Func<Task> operation)
    {
        await TrackOperationAsync(async () =>
        {
            await operation().ConfigureAwait(true);
            return true;
        }).ConfigureAwait(true);
    }

    private async Task<T> TrackOperationAsync<T>(Func<Task<T>> operation)
    {
        BeginActiveOperation();
        try
        {
            return await operation().ConfigureAwait(true);
        }
        finally
        {
            EndActiveOperation();
        }
    }

    private void BeginActiveOperation()
    {
        if (Interlocked.Increment(ref _activeOperationCount) == 1)
        {
            _mainWindowViewModel.SetComponentsOperationInProgress(true);
            OnPropertyChanged(nameof(HasActiveOperation));
        }
    }

    private void EndActiveOperation()
    {
        if (Interlocked.Decrement(ref _activeOperationCount) != 0)
        {
            return;
        }

        _mainWindowViewModel.SetComponentsOperationInProgress(false);
        OnPropertyChanged(nameof(HasActiveOperation));
        ActiveOperationsCompleted?.Invoke(this, EventArgs.Empty);
    }

    public Task RefreshHuggingFaceTokenStatusAsync()
    {
        return RefreshHuggingFaceTokenStatusAsync(PassiveCheckToken);
    }

    // Detached means no page is showing, so a passive check has nothing to report and must
    // not start more WSL work. Installs and configuration runs hold the page open, so they
    // always see the live token instead.
    private CancellationToken PassiveCheckToken => _passiveChecks?.Token ?? new CancellationToken(canceled: true);

    private Task InitializeAsync(CancellationToken cancellationToken)
    {
        return Task.WhenAll(
            RefreshInstalledStatesAsync(cancellationToken),
            RefreshHuggingFaceTokenStatusAsync(cancellationToken));
    }

    private async Task RefreshHuggingFaceTokenStatusAsync(CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return;
        }

        SetHuggingFaceTokenCheckingState();

        try
        {
            using var timeout = new CancellationTokenSource(HuggingFaceTokenStatusTimeout);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
            await _huggingFaceToken.EnsureManagedTokenAsync(linked.Token).ConfigureAwait(true);
            var status = await _huggingFaceToken.GetStatusAsync(linked.Token).ConfigureAwait(true);
            ApplyHuggingFaceTokenStatus(status);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The page unloaded mid-check. It checks again the next time it opens.
        }
        catch (OperationCanceledException)
        {
            ApplyHuggingFaceTokenStatus(HuggingFaceTokenStatus.Unknown("Hugging Face token check timed out. Click Refresh to try again."));
        }
        catch (Exception ex)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            ApplyHuggingFaceTokenStatus(HuggingFaceTokenStatus.Unknown($"Hugging Face token check failed: {ex.Message}"));
        }
    }

    public async Task<string?> ReadHuggingFaceTokenAsync()
    {
        try
        {
            using var timeout = new CancellationTokenSource(HuggingFaceTokenReadTimeout);
            return await _huggingFaceToken.ReadTokenAsync(timeout.Token).ConfigureAwait(true);
        }
        catch
        {
            return null;
        }
    }

    public Task<string?> SaveHuggingFaceTokenAsync(string token)
    {
        return TrackOperationAsync<string?>(async () =>
        {
            try
            {
                using var timeout = new CancellationTokenSource(HuggingFaceTokenWriteTimeout);
                var result = await _huggingFaceToken.SaveTokenAsync(token, timeout.Token).ConfigureAwait(true);
                return result.Succeeded ? null : HuggingFaceTokenService.BuildErrorText(result);
            }
            catch (OperationCanceledException)
            {
                return "Timed out while writing the Hugging Face token to WSL.";
            }
            catch (Exception ex)
            {
                return ex.Message;
            }
        });
    }

    public Task<string?> ClearHuggingFaceTokenAsync()
    {
        return TrackOperationAsync<string?>(async () =>
        {
            try
            {
                using var timeout = new CancellationTokenSource(HuggingFaceTokenWriteTimeout);
                var result = await _huggingFaceToken.ClearTokenAsync(timeout.Token).ConfigureAwait(true);
                return result.Succeeded ? null : HuggingFaceTokenService.BuildErrorText(result);
            }
            catch (OperationCanceledException)
            {
                return "Timed out while clearing the Hugging Face token from WSL.";
            }
            catch (Exception ex)
            {
                return ex.Message;
            }
        });
    }

    public void SetHuggingFaceTokenReplacedState()
    {
        HuggingFaceTokenStatusText = "Replaced, not verified";
        HuggingFaceTokenDetailText = "Your token was saved in WSL. Click Refresh to verify access.";
        HuggingFaceTokenStatusBackground = "#4F3C7A";
        HuggingFaceTokenStatusForeground = "White";
        foreach (var model in HuggingFaceModelAccessItems)
        {
            model.SetUnknownState();
        }
    }

    public void SetHuggingFaceTokenClearedState()
    {
        HuggingFaceTokenStatusText = HasManagedHuggingFaceToken ? "Built-in access available" : "Not configured";
        HuggingFaceTokenDetailText = HasManagedHuggingFaceToken
            ? "Your token was cleared. Click Refresh to restore built-in Hugging Face access."
            : "Token was cleared from WSL. Voice cloning downloads need Hugging Face access.";
        HuggingFaceTokenStatusBackground = HasManagedHuggingFaceToken ? "#4F3C7A" : "#6A3A12";
        HuggingFaceTokenStatusForeground = "White";
        foreach (var model in HuggingFaceModelAccessItems)
        {
            model.SetUnknownState();
        }
    }

    private void SetHuggingFaceTokenCheckingState()
    {
        HuggingFaceTokenStatusText = "Checking";
        HuggingFaceTokenDetailText = HasManagedHuggingFaceToken
            ? "Setting up built-in Hugging Face access..."
            : "Checking Hugging Face token status...";
        HuggingFaceTokenStatusBackground = "#555555";
        HuggingFaceTokenStatusForeground = "White";
        foreach (var model in HuggingFaceModelAccessItems)
        {
            model.SetCheckingState();
        }
    }

    private void ApplyHuggingFaceTokenStatus(HuggingFaceTokenStatus status)
    {
        ApplyHuggingFaceModelAccess(status.ModelAccess);

        if (!status.IsConfigured && string.IsNullOrWhiteSpace(status.Error))
        {
            HuggingFaceTokenStatusText = HasManagedHuggingFaceToken ? "Built-in access available" : "Not configured";
            HuggingFaceTokenDetailText = HasManagedHuggingFaceToken
                ? "Click Refresh to install the built-in Hugging Face access token."
                : "No token detected. Voice cloning downloads need Hugging Face access.";
            HuggingFaceTokenStatusBackground = HasManagedHuggingFaceToken ? "#4F3C7A" : "#6A3A12";
            HuggingFaceTokenStatusForeground = "White";
            return;
        }

        if (status.IsValid == true)
        {
            HuggingFaceTokenStatusText = HasManagedHuggingFaceToken ? "Ready" : "Valid";
            HuggingFaceTokenDetailText = HasManagedHuggingFaceToken
                ? "Built-in Hugging Face access is ready for voice model downloads."
                : "Token verified with Hugging Face.";
            HuggingFaceTokenStatusBackground = "#285A2D";
            HuggingFaceTokenStatusForeground = "White";
            return;
        }

        if (status.IsValid == false)
        {
            HuggingFaceTokenStatusText = "Invalid token";
            HuggingFaceTokenDetailText = status.Error ?? "Hugging Face rejected the configured token.";
            HuggingFaceTokenStatusBackground = "#7A2828";
            HuggingFaceTokenStatusForeground = "White";
            return;
        }

        if (status.IsConfigured)
        {
            HuggingFaceTokenStatusText = "Detected, not verified";
            HuggingFaceTokenDetailText = string.IsNullOrWhiteSpace(status.Error)
                ? "Hugging Face access is saved, but validation did not complete."
                : $"Hugging Face access is saved, but validation did not complete: {status.Error}";
            HuggingFaceTokenStatusBackground = "#4F3C7A";
            HuggingFaceTokenStatusForeground = "White";
            return;
        }

        HuggingFaceTokenStatusText = "Unknown";
        HuggingFaceTokenDetailText = status.Error ?? "Unable to check Hugging Face token status.";
        HuggingFaceTokenStatusBackground = "#4F3C7A";
        HuggingFaceTokenStatusForeground = "White";
    }

    private void ApplyHuggingFaceModelAccess(IReadOnlyList<HuggingFaceModelAccessStatus> modelAccess)
    {
        var updatedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var access in modelAccess)
        {
            if (_modelAccessItemsByKey.TryGetValue(access.Key, out var item))
            {
                item.ApplyStatus(access);
                updatedKeys.Add(access.Key);
            }
        }

        foreach (var item in HuggingFaceModelAccessItems)
        {
            if (!updatedKeys.Contains(item.Key))
            {
                item.SetUnknownState();
            }
        }
    }

    private Task RefreshInstalledStatesAsync()
    {
        return RefreshInstalledStatesAsync(PassiveCheckToken);
    }

    private async Task RefreshInstalledStatesAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _componentProbeGate.WaitAsync(cancellationToken).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        try
        {
            ConfigurableComponentsStatusText = "Checking installed components...";
            foreach (var item in _allItems)
            {
                item.SetCheckingState();
            }

            var probeScript = BuildProbeScript();
            var result = await _wsl.RunBashAsync(probeScript, loginShell: false, cancellationToken: cancellationToken).ConfigureAwait(true);
            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            if (!result.Succeeded)
            {
                SetComponentProbeUnavailable();
                return;
            }

            ComponentProbePayload? payload;
            try
            {
                payload = JsonSerializer.Deserialize<ComponentProbePayload>(
                    result.StandardOutput.Trim(),
                    JsonOptions);
            }
            catch (JsonException)
            {
                payload = null;
            }

            if (payload?.Install is null || payload.Configurable is null)
            {
                SetComponentProbeUnavailable();
                return;
            }

            foreach (var item in _allItems)
            {
                if (payload.Install.TryGetValue(item.Key, out var state))
                {
                    item.SetProbeState(
                        state.Installed,
                        state.StatusText ?? string.Empty,
                        state.StatusBackground ?? string.Empty,
                        state.DetailText ?? string.Empty);
                }
                else
                {
                    item.SetUnknownState();
                }
            }

            ApplyConfigurableComponents(payload.Configurable);
        }
        catch (OperationCanceledException)
        {
            // Leaving Components cancels the probe. The page re-probes when it opens again.
        }
        finally
        {
            _componentProbeGate.Release();
        }
    }

    private void SetComponentProbeUnavailable()
    {
        foreach (var item in _allItems)
        {
            item.SetUnknownState();
        }

        ConfigurableComponents.Clear();
        ConfigurableComponentsStatusText = "Unable to check installed components.";
    }

    private void ApplyConfigurableComponents(IReadOnlyList<ConfigurableComponentProbeState> states)
    {
        var statesByKey = states.ToDictionary(state => state.Key, StringComparer.OrdinalIgnoreCase);
        ConfigurableComponents.Clear();

        foreach (var definition in ConfigurableComponentDefinitions)
        {
            if (!statesByKey.TryGetValue(definition.Key, out var state) || !state.Available)
            {
                continue;
            }

            ConfigurableComponents.Add(new ConfigurableComponentItemViewModel(
                definition.Key,
                definition.Title,
                state.ConfigurationText ?? "Unknown",
                () => ConfigureComponentAsync(definition)));
        }

        ConfigurableComponentsStatusText = ConfigurableComponents.Count == 0
            ? "No configurable components are currently installed."
            : string.Empty;
    }

    private async Task ConfigureComponentAsync(ConfigurableComponentDefinition definition)
    {
        if (!ConfigurableComponentsByKey.ContainsKey(definition.Key))
        {
            throw new InvalidOperationException("The selected component is not in the trusted configuration catalog.");
        }

        var command =
            $"wsl.exe -d {LauncherConstants.DistroName} -u {LauncherConstants.DistroUser} -- bash -lc \"{definition.ScriptPath}\"";
        await TrackOperationAsync(async () =>
        {
            // A closed console or failed configuration exits non-zero; connectors stay as they were.
            var configured = false;
            try
            {
                configured = await _processRunner.RunInNewConsoleAndWaitAsync(command).ConfigureAwait(true) == 0;
            }
            finally
            {
                await RefreshInstalledStatesAsync().ConfigureAwait(true);
            }

            if (configured)
            {
                await SyncPocketTtsConnectorsAsync(definition.Key).ConfigureAwait(true);
            }
        }).ConfigureAwait(true);
    }

    /// <summary>
    /// After a Pocket-TTS install or configure, point managed game connectors at that backend.
    /// The Core helper writes nothing unless that backend is the only one enabled and it is running.
    /// </summary>
    private async Task SyncPocketTtsConnectorsAsync(string componentKey)
    {
        var backend = PocketTtsConnectorService.BackendForComponent(componentKey);
        var item = _allItems.FirstOrDefault(candidate => string.Equals(candidate.Key, componentKey, StringComparison.OrdinalIgnoreCase));
        if (backend is null || item is null)
        {
            return;
        }

        item.SetConnectorStatus("Checking game voice connectors...");
        var result = await _mainWindowViewModel.SyncPocketTtsConnectorsAsync(backend).ConfigureAwait(true);
        item.SetConnectorStatus(result.State == "not_enabled" ? $"{result.Message} Connectors were not changed." : result.Summary);
    }

    internal static string BuildMeloTtsProbeEntry()
    {
        return ": provider_status(Path('/home/dwemer/python-melotts/bin/python').exists(), Path('/home/dwemer/MeloTTS/start.sh').exists(), Path('/home/dwemer/MeloTTS'), 8084, 'melotts', 'MeloTTS'),";
    }

    /// <summary>
    /// The catalog's installed and startup states alone, as the same fixed probe code computes them for this page. It
    /// prints <c>{"catalog_version", "sha256", "items": [{"id", "installed", "installed_reason", "startup",
    /// "startup_reason"}]}</c>, comparable field by field with <c>ddistro inventory --json</c>'s <c>catalog</c> section.
    /// </summary>
    internal static string BuildCatalogProbeScript(ComponentCatalog catalog)
    {
        var builder = new StringBuilder();
        builder.AppendLine("python3 - <<'PY'");
        builder.AppendLine("from pathlib import Path");
        builder.AppendLine("import json");
        builder.Append(BuildCatalogProbeFunctions(catalog));
        builder.AppendLine("print(json.dumps(catalog_report()))");
        builder.AppendLine("PY");
        return builder.ToString();
    }

    /// <summary>
    /// Fixed Python that evaluates the catalog's installed rules with Core's semantics (bin/ddistro catalog_walk and
    /// inventory_marker), followed by the validated rules as plain literals. Nothing from the catalog becomes code: every
    /// rendered value is a validated id, path or version made only of safe characters.
    /// </summary>
    internal static string BuildCatalogProbeFunctions(ComponentCatalog catalog)
    {
        var builder = new StringBuilder(CatalogProbeFunctions);
        builder.AppendLine($"CATALOG_VERSION = {PythonText(catalog.CatalogVersion)}");
        builder.AppendLine($"CATALOG_SHA256 = {PythonText(catalog.Sha256)}");
        builder.AppendLine("CATALOG = {");
        foreach (var entry in catalog.Entries)
        {
            builder.AppendLine($"    {PythonText(entry.Id)}: ({PythonRule(entry.Installed)}, {(entry.Startup is null ? "None" : PythonRule(entry.Startup))}),");
        }

        builder.AppendLine("}");
        builder.AppendLine("CATALOG_STATE = {key: catalog_installed(rules[0]) for key, rules in CATALOG.items()}");
        builder.AppendLine();
        return builder.ToString();
    }

    private static string PythonRule(ComponentPredicate rule) =>
        $"({PythonText(rule.Mode)}, ({string.Join(" ", rule.Leaves.Select(leaf => $"({PythonText(leaf.Kind)}, {PythonText(leaf.Argument)}),"))}))";

    private static string PythonText(string value) =>
        value.Length > 0 && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '+' or '-' or '/' or '*')
            ? $"'{value}'"
            : throw new InvalidOperationException("A component catalog value is not a plain id, path or version.");

    private const string CatalogProbeFunctions = """
        import errno
        import os
        import pwd
        import stat

        CATALOG_LINK_HOPS = 8
        CATALOG_GLOB_SCAN = 256
        CATALOG_SEGMENT_CHARS = set('ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789._+-')
        CATALOG_WHICH_PATH = ('/usr/local/sbin', '/usr/local/bin', '/usr/sbin', '/usr/bin', '/sbin', '/bin', '/usr/local/cuda/bin')

        def catalog_segment(name):
            return 1 <= len(name) <= 64 and all(c in CATALOG_SEGMENT_CHARS for c in name)

        def catalog_uid(account):
            try:
                return pwd.getpwnam(account).pw_uid
            except KeyError:
                return None

        def catalog_zone(parts):
            if parts[:1] == ['usr'] or parts[:2] == ['etc', 'alternatives'] or len(parts) == 1 and parts[0] in ('bin', 'sbin', 'lib', 'lib32', 'lib64'):
                return ('system',)
            if parts[:2] == ['home', 'dwemer'] and len(parts) > 2:
                return ('home', parts[2])
            if parts[:3] == ['var', 'www', 'html'] and len(parts) > 3:
                return ('www', parts[3])
            return None

        def catalog_owners(parts):
            if parts[:2] == ['home', 'dwemer']:
                return {0, catalog_uid('dwemer')}
            if parts[:3] == ['var', 'www', 'html']:
                return {0, catalog_uid('dwemer'), catalog_uid('www-data')}
            return {0}

        def catalog_walk(path):
            pending, done, hops = path.strip('/').split('/'), [], 0
            try:
                info = os.lstat('/')
            except OSError:
                return 'unknown', 'unreadable'
            if info.st_uid != 0 or info.st_mode & 0o002:
                return 'unknown', 'unsafe'
            while pending:
                current = done + [pending.pop(0)]
                try:
                    info = os.lstat('/' + '/'.join(current))
                except OSError as error:
                    if error.errno in (errno.ENOENT, errno.ENOTDIR):
                        return 'absent', ''
                    return 'unknown', 'unreadable'
                if stat.S_ISLNK(info.st_mode):
                    hops += 1
                    if hops > CATALOG_LINK_HOPS:
                        return 'unknown', 'redirect_limit'
                    if info.st_uid not in catalog_owners(current):
                        return 'unknown', 'unsafe'
                    try:
                        target = os.readlink('/' + '/'.join(current))
                    except OSError:
                        return 'unknown', 'unreadable'
                    resolved = [] if target.startswith('/') else list(done)
                    for part in target.split('/'):
                        if part == '..':
                            if not resolved:
                                return 'unknown', 'unsafe_redirect'
                            resolved.pop()
                        elif part not in ('', '.'):
                            resolved.append(part)
                    zone = catalog_zone(current)
                    if zone is None or len(resolved) > 32 or not resolved or catalog_zone(resolved) not in (('system',), zone):
                        return 'unknown', 'unsafe_redirect'
                    pending, done = resolved + pending, []
                    continue
                if info.st_mode & 0o002 or info.st_uid not in catalog_owners(current):
                    return 'unknown', 'unsafe'
                if pending and not stat.S_ISDIR(info.st_mode):
                    return 'absent', ''
                done = current
            return 'present', info

        def catalog_leaf_path(kind, path):
            state, detail = catalog_walk(path)
            if state != 'present':
                return state, detail
            mode = detail.st_mode
            found = {'exists': True, 'file': stat.S_ISREG(mode), 'dir': stat.S_ISDIR(mode),
                     'program': stat.S_ISREG(mode) and bool(mode & 0o111)}[kind]
            return ('present', '') if found else ('absent', '')

        def catalog_combine(mode, results):
            deciding = 'absent' if mode == 'all' else 'present'
            if any(state == deciding for state, _ in results):
                return deciding, ''
            unknown = next((result for result in results if result[0] == 'unknown'), None)
            if unknown:
                return unknown
            return ('present' if mode == 'all' else 'absent'), ''

        def catalog_glob(path):
            parts = path.strip('/').split('/')
            index = next(i for i, part in enumerate(parts) if '*' in part)
            prefix, _, suffix = parts[index].partition('*')
            parent = '/' + '/'.join(parts[:index])
            state, detail = catalog_walk(parent)
            if state != 'present':
                return state, detail
            if not stat.S_ISDIR(detail.st_mode):
                return 'absent', ''
            names, truncated = [], False
            try:
                with os.scandir(parent) as scan:
                    for entry in scan:
                        if len(names) >= CATALOG_GLOB_SCAN:
                            truncated = True
                            break
                        names.append(entry.name)
            except OSError:
                return 'unknown', 'unreadable'
            results = [catalog_leaf_path('exists', '/'.join([parent, name] + parts[index + 1:]))
                       for name in sorted(names) if catalog_segment(name) and name not in ('.', '..')
                       and len(name) >= len(prefix) + len(suffix) and name.startswith(prefix) and name.endswith(suffix)]
            return catalog_combine('any', results + ([('unknown', 'scan_limit')] if truncated else []))

        def catalog_installed(predicate):
            mode, leaves = predicate
            results = []
            for kind, arg in leaves:
                if kind == 'which':
                    results.append(catalog_combine('any', [catalog_leaf_path('program', folder + '/' + arg) for folder in CATALOG_WHICH_PATH]))
                elif kind == 'glob':
                    results.append(catalog_glob(arg))
                else:
                    results.append(catalog_leaf_path(kind, arg))
            return catalog_combine(mode, results)

        def catalog_marker(path):
            parts = path.strip('/').split('/')
            for index in range(len(parts)):
                try:
                    info = os.lstat('/' + '/'.join(parts[:index + 1]))
                except OSError as error:
                    if error.errno in (errno.ENOENT, errno.ENOTDIR):
                        return 'absent'
                    return 'unreadable'
                if stat.S_ISLNK(info.st_mode):
                    return 'redirected'
                if index < len(parts) - 1 and not stat.S_ISDIR(info.st_mode):
                    return 'absent'
            return 'present' if stat.S_ISREG(info.st_mode) or stat.S_ISDIR(info.st_mode) else 'unreadable'

        def catalog_startup(predicate):
            if predicate is None:
                return 'n/a', ''
            mode, leaves = predicate
            states = [catalog_marker(path) for _, path in leaves]
            if (all if mode == 'all' else any)(state == 'present' for state in states):
                return 'present', ''
            state = next((s for s in states if s not in ('present', 'absent')), 'absent')
            return (state, '') if state == 'absent' else ('unknown', state)

        def installed_status(key, rich=None):
            state, reason = CATALOG_STATE[key]
            if state == 'unknown':
                return make_status(False, 'Unknown', 'Installed state could not be confirmed (' + reason + ').', UNKNOWN)
            if rich is not None:
                return rich(state == 'present')
            return make_status(state == 'present')

        def startup_exists(key):
            rule = CATALOG[key][1]
            if rule is None:
                return False
            mode, leaves = rule
            return (all if mode == 'all' else any)(Path(path).exists() for _, path in leaves)

        def catalog_report():
            items = []
            for key, rules in CATALOG.items():
                installed, installed_reason = CATALOG_STATE[key]
                startup, startup_reason = catalog_startup(rules[1])
                items.append({'id': key, 'installed': installed, 'installed_reason': installed_reason,
                              'startup': startup, 'startup_reason': startup_reason})
            return {'catalog_version': CATALOG_VERSION, 'sha256': CATALOG_SHA256, 'items': items}


        """;

    private string BuildProbeScript()
    {
        var builder = new StringBuilder();
        builder.AppendLine("python3 - <<'PY'");
        builder.AppendLine("from pathlib import Path");
        builder.AppendLine("import json");
        builder.AppendLine("import shutil");
        builder.AppendLine("import socket");
        builder.AppendLine("import urllib.request");
        builder.AppendLine();
        builder.AppendLine("GOOD = '#285A2D'");
        builder.AppendLine("WARN = '#6A3A12'");
        builder.AppendLine("BAD = '#7A2828'");
        builder.AppendLine("UNKNOWN = '#4F3C7A'");
        builder.AppendLine();
        builder.AppendLine("def make_status(installed, status_text=None, detail_text='', background=None):");
        builder.AppendLine("    if status_text is None:");
        builder.AppendLine("        status_text = 'Installed' if installed else 'Not installed'");
        builder.AppendLine("    if background is None:");
        builder.AppendLine("        background = GOOD if installed else WARN");
        builder.AppendLine("    return {");
        builder.AppendLine("        'installed': bool(installed),");
        builder.AppendLine("        'statusText': status_text,");
        builder.AppendLine("        'statusBackground': background,");
        builder.AppendLine("        'detailText': detail_text,");
        builder.AppendLine("    }");
        builder.AppendLine();
        builder.AppendLine("def is_port_open(port):");
        builder.AppendLine("    try:");
        builder.AppendLine("        with socket.create_connection(('127.0.0.1', port), timeout=0.5):");
        builder.AppendLine("            return True");
        builder.AppendLine("    except OSError:");
        builder.AppendLine("        return False");
        builder.AppendLine();
        builder.AppendLine("def read_saved_port(base, fallback):");
        builder.AppendLine("    try:");
        builder.AppendLine("        value = int((base / '.dwemerdistro-port').read_text(encoding='utf-8').strip())");
        builder.AppendLine("        return value if 1 <= value <= 65535 else fallback");
        builder.AppendLine("    except (OSError, TypeError, ValueError):");
        builder.AppendLine("        return fallback");
        builder.AppendLine();
        builder.AppendLine("def identify_provider(port):");
        builder.AppendLine("    try:");
        builder.AppendLine("        with urllib.request.urlopen(f'http://127.0.0.1:{port}/provider_info', timeout=2) as response:");
        builder.AppendLine("            data = json.loads(response.read().decode('utf-8', errors='replace'))");
        builder.AppendLine("        provider = str(data.get('provider') or '').strip().lower()");
        builder.AppendLine("        if provider in ('pocket_tts', 'pocket-tts'):");
        builder.AppendLine("            provider = 'pockettts'");
        builder.AppendLine("        if provider:");
        builder.AppendLine("            return provider");
        builder.AppendLine("    except Exception:");
        builder.AppendLine("        pass");
        builder.AppendLine("    try:");
        builder.AppendLine("        with urllib.request.urlopen(f'http://127.0.0.1:{port}/openapi.json', timeout=2) as response:");
        builder.AppendLine("            text = response.read().decode('utf-8', errors='replace').lower()");
        builder.AppendLine("        if 'chatterbox tts api' in text or '/sample/{file_name}' in text or '\"/speakers\"' in text:");
        builder.AppendLine("            return 'chatterbox'");
        builder.AppendLine("        if '/set_tts_settings' in text or '/tts_to_audio_form' in text:");
        builder.AppendLine("            return 'pockettts'");
        builder.AppendLine("        if '\"/languages\"' in text:");
        builder.AppendLine("            return 'xtts'");
        builder.AppendLine("    except Exception:");
        builder.AppendLine("        pass");
        builder.AppendLine("    return ''");
        builder.AppendLine();
        builder.AppendLine("def provider_status(installed, enabled, base, dedicated_port, expected_provider, label):");
        builder.AppendLine("    if not installed:");
        builder.AppendLine("        return make_status(False, 'Not installed', f'Dedicated local service port: 127.0.0.1:{dedicated_port}.', WARN)");
        builder.AppendLine("    configured_port = read_saved_port(base, 8020)");
        builder.AppendLine("    candidates = []");
        builder.AppendLine("    for port in (configured_port, dedicated_port, 8020):");
        builder.AppendLine("        if port not in candidates:");
        builder.AppendLine("            candidates.append(port)");
        builder.AppendLine("    for port in candidates:");
        builder.AppendLine("        actual_provider = identify_provider(port)");
        builder.AppendLine("        if actual_provider == expected_provider:");
        builder.AppendLine("            mode = 'legacy' if port == 8020 else 'dedicated'");
        builder.AppendLine("            return make_status(True, 'Healthy', f'{label} identified on {mode} port {port}.', GOOD)");
        builder.AppendLine("        if port == configured_port and is_port_open(port):");
        builder.AppendLine("            actual = actual_provider or 'unknown service'");
        builder.AppendLine("            return make_status(True, 'Port conflict', f'Expected {label} on configured port {port}, but found {actual}.', BAD)");
        builder.AppendLine("    if not enabled:");
        builder.AppendLine("        return make_status(True, 'Installed / disabled', f'{label} is installed; configured port {configured_port}.', WARN)");
        builder.AppendLine("    return make_status(True, 'Installed', f'{label} is enabled but not running on configured, dedicated, or legacy ports.', WARN)");
        builder.AppendLine();
        builder.AppendLine("def last_omnivoice_error(log_path):");
        builder.AppendLine("    try:");
        builder.AppendLine("        lines = log_path.read_text(encoding='utf-8', errors='replace').splitlines()");
        builder.AppendLine("    except OSError:");
        builder.AppendLine("        return ''");
        builder.AppendLine("    for raw in reversed(lines[-200:]):");
        builder.AppendLine("        line = ' '.join(raw.strip().split())");
        builder.AppendLine("        lower = line.lower()");
        builder.AppendLine("        if line and any(token in lower for token in ('error', 'traceback', 'failed', 'already in use')):");
        builder.AppendLine("            return line[:180]");
        builder.AppendLine("    return ''");
        builder.AppendLine();
        builder.AppendLine("def omnivoice_status(installed):");
        builder.AppendLine("    base = Path('/home/dwemer/omnivoice-tts')");
        builder.AppendLine("    log_error = last_omnivoice_error(base / 'logs' / 'server.log')");
        builder.AppendLine("    if not installed:");
        builder.AppendLine("        return make_status(False, 'Not installed', 'Separate optional service on 127.0.0.1:8021.', WARN)");
        builder.AppendLine("    try:");
        builder.AppendLine("        with urllib.request.urlopen('http://127.0.0.1:8021/health', timeout=2) as response:");
        builder.AppendLine("            health = json.loads(response.read().decode('utf-8', errors='replace'))");
        builder.AppendLine("        if response.status == 200 and health.get('status') == 'ok':");
        builder.AppendLine("            language = (health.get('active_language') or {}).get('id') or 'unknown'");
        builder.AppendLine("            voice_count = health.get('voice_count', 0)");
        builder.AppendLine("            cuda = 'CUDA yes' if health.get('cuda') else 'CUDA no'");
        builder.AppendLine("            gpu = health.get('gpu') or 'unknown GPU'");
        builder.AppendLine("            default_voice = health.get('default_voice') or 'no default'");
        builder.AppendLine("            detail = f'healthy; language {language}; {voice_count} voices; {cuda}; {gpu}; default {default_voice}'");
        builder.AppendLine("            return make_status(True, 'Healthy', detail, GOOD)");
        builder.AppendLine("    except Exception:");
        builder.AppendLine("        pass");
        builder.AppendLine("    if is_port_open(8021):");
        builder.AppendLine("        detail = 'port 8021 is in use but /health did not return OmniVoice.'");
        builder.AppendLine("        if log_error:");
        builder.AppendLine("            detail += ' Last log: ' + log_error");
        builder.AppendLine("        return make_status(True, 'Port conflict', detail, BAD)");
        builder.AppendLine("    detail = 'installed; not running on 8021.'");
        builder.AppendLine("    if log_error:");
        builder.AppendLine("        detail += ' Last log: ' + log_error");
        builder.AppendLine("    return make_status(True, 'Installed', detail, WARN)");
        builder.AppendLine();
        builder.AppendLine("def target_name(path):");
        builder.AppendLine("    if not path.exists() and not path.is_symlink():");
        builder.AppendLine("        return ''");
        builder.AppendLine("    if path.is_symlink():");
        builder.AppendLine("        return path.resolve(strict=False).name");
        builder.AppendLine("    return path.name");
        builder.AppendLine();
        builder.AppendLine("def start_configuration(base):");
        builder.AppendLine("    target = target_name(base / 'start.sh')");
        builder.AppendLine("    if not target:");
        builder.AppendLine("        return 'Disabled'");
        builder.AppendLine("    labels = {");
        builder.AppendLine("        'start.sh': 'Enabled',");
        builder.AppendLine("        'start-gpu.sh': 'GPU / CUDA',");
        builder.AppendLine("        'start-cpu.sh': 'CPU',");
        builder.AppendLine("        'start-piper-gpu.sh': 'GPU / CUDA',");
        builder.AppendLine("        'start-piper-cpu.sh': 'CPU',");
        builder.AppendLine("        'start-deepspeed.sh': 'DeepSpeed - GPU / CUDA',");
        builder.AppendLine("        'start-lowvram.sh': 'Low VRAM - GPU / CUDA',");
        builder.AppendLine("        'start-regular.sh': 'Regular - GPU / CUDA',");
        builder.AppendLine("        'start-audiocpp-pockettts.sh': 'Enabled',");
        builder.AppendLine("    }");
        builder.AppendLine("    return labels.get(target, 'Custom')");
        builder.AppendLine();
        builder.AppendLine("def whisper_configuration(base):");
        builder.AppendLine("    target = target_name(base / 'config.yaml')");
        builder.AppendLine("    if not target:");
        builder.AppendLine("        return 'Disabled'");
        builder.AppendLine("    labels = {");
        builder.AppendLine("        'config-Small-CPU-EN.yaml': 'Small EN - CPU',");
        builder.AppendLine("        'config-Small-GPU-EN.yaml': 'Small EN - GPU / CUDA',");
        builder.AppendLine("        'config-Small-GPU-ES.yaml': 'Small ES - GPU / CUDA',");
        builder.AppendLine("        'config-Tiny-GPU-EN.yaml': 'Tiny EN - GPU / CUDA',");
        builder.AppendLine("        'config-Medium-GPU-EN.yaml': 'Medium EN - GPU / CUDA',");
        builder.AppendLine("        'config-numbat-GPU-Skyrim-EN.yaml': 'Skyrim EN - GPU / CUDA',");
        builder.AppendLine("    }");
        builder.AppendLine("    return labels.get(target, 'Custom')");
        builder.AppendLine();
        builder.AppendLine("def omnivoice_configuration(base):");
        builder.AppendLine("    mode = start_configuration(base)");
        builder.AppendLine("    language = 'unknown'");
        builder.AppendLine("    try:");
        builder.AppendLine("        config = json.loads((base / 'config.json').read_text(encoding='utf-8'))");
        builder.AppendLine("        language = str(config.get('active_language') or 'unknown')");
        builder.AppendLine("    except (OSError, ValueError, TypeError):");
        builder.AppendLine("        pass");
        builder.AppendLine("    return f'{mode} - Language: {language}'");
        builder.AppendLine();
        builder.AppendLine("def current_configuration(base, kind):");
        builder.AppendLine("    if kind == 'higgs':");
        builder.AppendLine("        return 'Enabled at startup' if (base / '.enabled').exists() else 'Disabled at startup'");
        builder.AppendLine("    if kind == 'whisper':");
        builder.AppendLine("        return whisper_configuration(base)");
        builder.AppendLine("    if kind == 'omnivoice':");
        builder.AppendLine("        return omnivoice_configuration(base)");
        builder.AppendLine("    return start_configuration(base)");
        builder.AppendLine();
        builder.Append(BuildCatalogProbeFunctions(ComponentCatalog.Embedded));
        builder.AppendLine("status = {");

        foreach (var item in _allItems)
        {
            // An item the catalog does not know is left out, so it shows Unknown rather than a guess.
            if (ComponentCatalog.Embedded.Find(item.Key) is not { } entry)
            {
                continue;
            }

            builder.Append("    ");
            builder.Append(JsonSerializer.Serialize(item.Key));
            if (string.Equals(item.Key, "omnivoice", StringComparison.OrdinalIgnoreCase))
            {
                builder.AppendLine($": installed_status('{entry.Id}', omnivoice_status),");
            }
            else if (string.Equals(item.Key, "chatterbox", StringComparison.OrdinalIgnoreCase))
            {
                builder.AppendLine($": installed_status('{entry.Id}', lambda installed: provider_status(installed, startup_exists('{entry.Id}'), Path('/home/dwemer/chatterbox'), 8023, 'chatterbox', 'Chatterbox')),");
            }
            else if (string.Equals(item.Key, "pockettts", StringComparison.OrdinalIgnoreCase))
            {
                builder.AppendLine($": installed_status('{entry.Id}', lambda installed: provider_status(installed, startup_exists('{entry.Id}'), Path('/home/dwemer/pocket-tts'), 8024, 'pockettts', 'PocketTTS Python')),");
            }
            else if (string.Equals(item.Key, "melotts", StringComparison.OrdinalIgnoreCase))
            {
                builder.AppendLine(BuildMeloTtsProbeEntry());
            }
            else
            {
                builder.AppendLine($": installed_status('{entry.Id}'),");
            }
        }

        builder.AppendLine("}");
        builder.AppendLine("configurable = []");
        foreach (var definition in ConfigurableComponentDefinitions)
        {
            if (ComponentCatalog.Embedded.Find(definition.Key) is not { } entry)
            {
                continue;
            }

            builder.Append($"if CATALOG_STATE['{entry.Id}'][0] == 'present' and Path(");
            builder.Append(JsonSerializer.Serialize(definition.ScriptPath));
            builder.AppendLine(").is_file():");
            builder.Append("    configurable.append({'key': ");
            builder.Append(JsonSerializer.Serialize(definition.Key));
            builder.Append(", 'available': True, 'configurationText': current_configuration(Path(");
            builder.Append(JsonSerializer.Serialize(definition.BasePath));
            builder.Append("), ");
            builder.Append(JsonSerializer.Serialize(definition.ConfigurationKind));
            builder.AppendLine(")})");
        }

        builder.AppendLine("print(json.dumps({'install': status, 'configurable': configurable, 'catalog': catalog_report()}))");
        builder.AppendLine("PY");

        return builder.ToString();
    }

    private sealed class InstallComponentProbeState
    {
        public bool Installed { get; set; }

        public string? StatusText { get; set; }

        public string? StatusBackground { get; set; }

        public string? DetailText { get; set; }
    }

    private sealed class ComponentProbePayload
    {
        public Dictionary<string, InstallComponentProbeState>? Install { get; set; }

        public List<ConfigurableComponentProbeState>? Configurable { get; set; }
    }

    private sealed class ConfigurableComponentProbeState
    {
        public string Key { get; set; } = string.Empty;

        public bool Available { get; set; }

        public string? ConfigurationText { get; set; }
    }

    private sealed record ConfigurableComponentDefinition(
        string Key,
        string Title,
        string BasePath,
        string ScriptPath,
        string ConfigurationKind);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private ICommand CreateInstallCommand(string componentKey)
    {
        return new AsyncRelayCommand(() => TrackOperationAsync(async () =>
        {
            var installed = await _mainWindowViewModel.InstallComponentAsync(componentKey).ConfigureAwait(true);
            await RefreshInstalledStatesAsync().ConfigureAwait(true);
            if (installed)
            {
                await SyncPocketTtsConnectorsAsync(componentKey).ConfigureAwait(true);
            }
        }));
    }

    private IEnumerable<InstallComponentSectionViewModel> BuildSections(
        List<InstallComponentItemViewModel> allItems)
    {
        var sections = new List<InstallComponentSectionViewModel>();

        sections.Add(CreateSection(
            "Core",
            allItems,
            CreateItem(
                key: "cuda",
                title: "CUDA",
                description: "Install the NVIDIA CUDA stack used by GPU-backed services. Run this first on NVIDIA systems.\nPort: None",
                primaryCommand: CreateInstallCommand("cuda"),
                supportsNvidiaCuda: true)));

        sections.Add(CreateSection(
            "Local Language Models",
            allItems,
            CreateItem(
                key: "lmstudio",
                title: "LLM Studio",
                description: "LM Studio built into the distro. Run local models directly in the distro\nPort: 1234",
                primaryCommand: CreateInstallCommand("lmstudio"),
                supportsNvidiaCuda: true,
                supportsAmdCpu: true,
                secondaryActionText: "Open Manager",
                secondaryActionCommand: new AsyncRelayCommand(async () =>
                {
                    try { await new LmStudioService(_wsl).OpenManagerAsync(); }
                    catch (Exception error) { System.Windows.MessageBox.Show(error.Message, "LLM Studio"); }
                }))));

        sections.Add(CreateSection(
            "Service Extensions",
            allItems,
            CreateItem(
                key: "minime",
                title: "Minime and TXT2VEC",
                description: "Installs the small helper LLM and vector service used to improve NPC responses and retrieval.\nPort: 8082",
                primaryCommand: CreateInstallCommand("minime"),
                supportsNvidiaCuda: true,
                supportsAmdCpu: true),
            CreateItem(
                key: "chimmcp",
                title: "CHIM-MCP",
                description: "Optional MCP integration for CHIM tooling. Install this only when you want MCP support.\nPort: None",
                primaryCommand: CreateInstallCommand("chimmcp"))));

        sections.Add(CreateSection(
            "Text-to-Speech Engines",
            allItems,
            CreateItem(
                key: "audiocpp",
                title: "Pocket-TTS (GPU / audio.cpp)",
                description: "Recommended Pocket-TTS runtime for NVIDIA GPUs.\nPort: 8086",
                primaryCommand: CreateInstallCommand("audiocpp"),
                supportsNvidiaCuda: true),
            CreateItem(
                key: "pockettts",
                title: "Pocket-TTS (CPU / Python)",
                description: "Python Pocket-TTS runtime for AMD or CPU-only systems.\nPort: 8024",
                primaryCommand: CreateInstallCommand("pockettts"),
                supportsAmdCpu: true),
            CreateItem(
                key: "higgs",
                title: "Higgs TTS 3",
                description: "Very powerful TTS. Choose Higgs — Full or Higgs — Compact (Q8) during installation.\nCompact uses less memory. Its 5.1 GB download size is not its VRAM requirement.\nPort: 8025",
                primaryCommand: CreateInstallCommand("higgs"),
                supportsNvidiaCuda: true),
            CreateItem(
                key: "chatterbox",
                title: "Chatterbox",
                description: "High-quality multilingual TTS with voice cloning.\nPort: 8023",
                primaryCommand: CreateInstallCommand("chatterbox"),
                supportsNvidiaCuda: true,
                supportsAmdCpu: true),
            CreateItem(
                key: "omnivoice",
                title: "Multilingual OmniVoice TTS",
                description: "Optional local OmniVoice TTS for CHIM voice libraries.\nPort: 8021",
                primaryCommand: CreateInstallCommand("omnivoice"),
                supportsNvidiaCuda: true,
                secondaryActionText: "View Logs",
                secondaryActionCommand: _mainWindowViewModel.ViewOmniVoiceLogsCommand),
            CreateItem(
                key: "xtts",
                title: "Dwemer Distro XTTS",
                description: "High-quality XTTS deployment for cloned Skyrim voices. Requires the shared XTTS Python environment.\nPort: 8020",
                primaryCommand: CreateInstallCommand("xtts"),
                supportsNvidiaCuda: true),
            CreateItem(
                key: "pipertts",
                title: "Piper-TTS",
                description: "Fast local TTS with separate downloadable voice packs. Good when you want simple CPU speech output.\nPort: 5000",
                primaryCommand: CreateInstallCommand("pipertts"),
                supportsNvidiaCuda: true,
                supportsAmdCpu: true,
                secondaryActionText: "Open Piper Voice Folder",
                secondaryActionCommand: _mainWindowViewModel.OpenPiperVoicesFolderCommand)));

        sections.Add(CreateSection(
            "Speech-to-Text Engines",
            allItems,
            CreateItem(
                key: "parakeet",
                title: "Parakeet STT",
                description: "Offline speech-to-text service with GPU and CPU modes for local transcription.\nPort: 8022",
                primaryCommand: CreateInstallCommand("parakeet"),
                supportsNvidiaCuda: true,
                supportsAmdCpu: true),
            CreateItem(
                key: "localwhisper",
                title: "LocalWhisper",
                description: "Offline Whisper-based speech-to-text service for local microphone and transcription workflows.\nPort: 9876",
                primaryCommand: CreateInstallCommand("localwhisper"),
                supportsNvidiaCuda: true,
                supportsAmdCpu: true)));

        return sections;
    }

    private static InstallComponentSectionViewModel CreateSection(
        string title,
        List<InstallComponentItemViewModel> allItems,
        params InstallComponentItemViewModel[] items)
    {
        allItems.AddRange(items);
        return new InstallComponentSectionViewModel(title, items);
    }

    private static InstallComponentItemViewModel CreateItem(
        string key,
        string title,
        string description,
        ICommand primaryCommand,
        bool supportsNvidiaCuda = false,
        bool supportsAmdCpu = false,
        string? secondaryActionText = null,
        ICommand? secondaryActionCommand = null,
        string? tertiaryActionText = null,
        ICommand? tertiaryActionCommand = null)
    {
        return new InstallComponentItemViewModel(
            key,
            title,
            description,
            // Installed state comes from the shared component catalog, never from an expression on the item.
            string.Empty,
            primaryCommand,
            supportsNvidiaCuda,
            supportsAmdCpu,
            secondaryActionText,
            secondaryActionCommand,
            tertiaryActionText,
            tertiaryActionCommand);
    }
}

public sealed class ConfigurableComponentItemViewModel : ObservableObject
{
    private readonly Func<Task> _configure;
    private string _configurationText;
    private bool _isConfiguring;

    public ConfigurableComponentItemViewModel(
        string key,
        string title,
        string configurationText,
        Func<Task> configure)
    {
        Key = key;
        Title = title;
        _configurationText = configurationText;
        _configure = configure;
        ConfigureCommand = new AsyncRelayCommand(ConfigureAsync, () => !IsConfiguring);
    }

    public string Key { get; }

    public string Title { get; }

    public AsyncRelayCommand ConfigureCommand { get; }

    public string ConfigurationText
    {
        get => _configurationText;
        private set => SetProperty(ref _configurationText, value);
    }

    public bool IsConfiguring
    {
        get => _isConfiguring;
        private set
        {
            if (SetProperty(ref _isConfiguring, value))
            {
                OnPropertyChanged(nameof(ConfigureButtonText));
                ConfigureCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string ConfigureButtonText => IsConfiguring ? "Configuring..." : "Configure";

    private async Task ConfigureAsync()
    {
        IsConfiguring = true;
        ConfigurationText = "Configuring...";
        try
        {
            await _configure().ConfigureAwait(true);
        }
        finally
        {
            IsConfiguring = false;
        }
    }
}

public sealed class HuggingFaceModelAccessViewModel : ObservableObject
{
    private string _statusText = "Checking";
    private string _statusBackground = "#555555";
    private string _statusForeground = "White";
    private string _detailText = string.Empty;
    private bool _hasAccessAction;

    public HuggingFaceModelAccessViewModel(
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
        DetailText = $"Checking {repositoryId}...";
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

    public string StatusForeground
    {
        get => _statusForeground;
        private set => SetProperty(ref _statusForeground, value);
    }

    public string DetailText
    {
        get => _detailText;
        private set => SetProperty(ref _detailText, value);
    }

    public bool HasAccessAction
    {
        get => _hasAccessAction;
        private set => SetProperty(ref _hasAccessAction, value);
    }

    public void SetCheckingState()
    {
        StatusText = "Checking";
        DetailText = $"Checking {RepositoryId}...";
        StatusBackground = "#555555";
        StatusForeground = "White";
        HasAccessAction = false;
    }

    public void SetUnknownState()
    {
        StatusText = "Unknown";
        DetailText = $"Unable to check {RepositoryId}.";
        StatusBackground = "#4F3C7A";
        StatusForeground = "White";
        HasAccessAction = true;
    }

    public void ApplyStatus(HuggingFaceModelAccessStatus status)
    {
        DetailText = string.IsNullOrWhiteSpace(status.Error)
            ? $"Checked {status.RepositoryId}."
            : $"{status.RepositoryId}: {status.Error}";

        switch (status.AccessStatus)
        {
            case "granted":
                StatusText = "Access granted";
                DetailText = $"Model download is reachable: {status.RepositoryId}.";
                StatusBackground = "#285A2D";
                StatusForeground = "White";
                HasAccessAction = false;
                break;
            case "token_required":
                StatusText = "Token required";
                StatusBackground = "#6A3A12";
                StatusForeground = "White";
                HasAccessAction = true;
                break;
            case "needs_approval":
                StatusText = "Needs approval";
                StatusBackground = "#6A3A12";
                StatusForeground = "White";
                HasAccessAction = true;
                break;
            case "invalid_token":
                StatusText = "Invalid token";
                StatusBackground = "#7A2828";
                StatusForeground = "White";
                HasAccessAction = false;
                break;
            case "not_found":
                StatusText = "Unavailable";
                StatusBackground = "#4F3C7A";
                StatusForeground = "White";
                HasAccessAction = true;
                break;
            default:
                StatusText = "Unable to verify";
                StatusBackground = "#4F3C7A";
                StatusForeground = "White";
                HasAccessAction = false;
                break;
        }
    }
}
