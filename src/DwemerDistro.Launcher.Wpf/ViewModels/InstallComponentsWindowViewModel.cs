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
    private readonly List<InstallComponentItemViewModel> _allItems = [];
    private readonly Dictionary<string, HuggingFaceModelAccessViewModel> _modelAccessItemsByKey;
    private string _huggingFaceTokenStatusText = "Checking";
    private string _huggingFaceTokenDetailText = "Checking Hugging Face token...";
    private string _huggingFaceTokenStatusBackground = "#555555";
    private string _huggingFaceTokenStatusForeground = "White";
    private static readonly TimeSpan HuggingFaceTokenReadTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan HuggingFaceTokenWriteTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan HuggingFaceTokenStatusTimeout = TimeSpan.FromSeconds(30);

    public InstallComponentsWindowViewModel(MainWindowViewModel mainWindowViewModel)
    {
        _wsl = new WslService(_processRunner);
        _huggingFaceToken = new HuggingFaceTokenService(_wsl);
        RefreshHuggingFaceTokenCommand = new AsyncRelayCommand(RefreshHuggingFaceTokenStatusAsync);
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
            BuildSections(mainWindowViewModel, _allItems));
    }

    public ObservableCollection<InstallComponentSectionViewModel> Sections { get; }

    public ObservableCollection<HuggingFaceModelAccessViewModel> HuggingFaceModelAccessItems { get; }

    public AsyncRelayCommand RefreshHuggingFaceTokenCommand { get; }

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

    public Task InitializeAsync()
    {
        return Task.WhenAll(RefreshInstalledStatesAsync(), RefreshHuggingFaceTokenStatusAsync());
    }

    public async Task RefreshHuggingFaceTokenStatusAsync()
    {
        SetHuggingFaceTokenCheckingState();

        try
        {
            using var timeout = new CancellationTokenSource(HuggingFaceTokenStatusTimeout);
            var status = await _huggingFaceToken.GetStatusAsync(timeout.Token).ConfigureAwait(true);
            ApplyHuggingFaceTokenStatus(status);
        }
        catch (OperationCanceledException)
        {
            ApplyHuggingFaceTokenStatus(HuggingFaceTokenStatus.Unknown("Hugging Face token check timed out. Click Refresh to try again."));
        }
        catch (Exception ex)
        {
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

    public async Task<string?> SaveHuggingFaceTokenAsync(string token)
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
    }

    public async Task<string?> ClearHuggingFaceTokenAsync()
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
    }

    public void SetHuggingFaceTokenReplacedState()
    {
        HuggingFaceTokenStatusText = "Replaced, not verified";
        HuggingFaceTokenDetailText = "Token was replaced in WSL. Click Refresh to verify Hugging Face and model access.";
        HuggingFaceTokenStatusBackground = "#4F3C7A";
        HuggingFaceTokenStatusForeground = "White";
        foreach (var model in HuggingFaceModelAccessItems)
        {
            model.SetUnknownState();
        }
    }

    public void SetHuggingFaceTokenClearedState()
    {
        HuggingFaceTokenStatusText = "Not configured";
        HuggingFaceTokenDetailText = "Token was cleared from WSL. Pocket-TTS needs a token and accepted model access for voice cloning.";
        HuggingFaceTokenStatusBackground = "#6A3A12";
        HuggingFaceTokenStatusForeground = "White";
        foreach (var model in HuggingFaceModelAccessItems)
        {
            model.SetUnknownState();
        }
    }

    private void SetHuggingFaceTokenCheckingState()
    {
        HuggingFaceTokenStatusText = "Checking";
        HuggingFaceTokenDetailText = "Checking /home/dwemer/.cache/huggingface/token...";
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
            HuggingFaceTokenStatusText = "Not configured";
            HuggingFaceTokenDetailText = "No token detected. Pocket-TTS needs a token and accepted model access for voice cloning.";
            HuggingFaceTokenStatusBackground = "#6A3A12";
            HuggingFaceTokenStatusForeground = "White";
            return;
        }

        if (status.IsValid == true)
        {
            var userSuffix = string.IsNullOrWhiteSpace(status.UserName) ? string.Empty : $": {status.UserName}";
            HuggingFaceTokenStatusText = $"Valid{userSuffix}";
            HuggingFaceTokenDetailText = $"Token detected at {status.TokenSource} and verified with Hugging Face.";
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
                ? $"Token detected at {status.TokenSource}, but validation did not complete."
                : $"Token detected at {status.TokenSource}, but validation did not complete: {status.Error}";
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

    private async Task RefreshInstalledStatesAsync()
    {
        foreach (var item in _allItems)
        {
            item.SetCheckingState();
        }

        var probeScript = BuildProbeScript();
        var result = await _wsl.RunBashAsync(probeScript, loginShell: false).ConfigureAwait(true);
        if (!result.Succeeded)
        {
            foreach (var item in _allItems)
            {
                item.SetUnknownState();
            }
            return;
        }

        Dictionary<string, InstallComponentProbeState>? installMap;
        try
        {
            installMap = JsonSerializer.Deserialize<Dictionary<string, InstallComponentProbeState>>(
                result.StandardOutput.Trim(),
                JsonOptions);
        }
        catch (JsonException)
        {
            installMap = null;
        }

        if (installMap is null)
        {
            foreach (var item in _allItems)
            {
                item.SetUnknownState();
            }
            return;
        }
        foreach (var item in _allItems)
        {
            if (installMap.TryGetValue(item.Key, out var state))
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
    }

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
        builder.AppendLine("def omnivoice_status():");
        builder.AppendLine("    base = Path('/home/dwemer/omnivoice-tts')");
        builder.AppendLine("    installed = (base / 'venv' / 'bin' / 'python').exists()");
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
        builder.AppendLine("status = {");

        foreach (var item in _allItems)
        {
            builder.Append("    ");
            builder.Append(JsonSerializer.Serialize(item.Key));
            if (string.Equals(item.Key, "omnivoice", StringComparison.OrdinalIgnoreCase))
            {
                builder.AppendLine(": omnivoice_status(),");
            }
            else
            {
                builder.Append(": make_status(bool(");
                builder.Append(item.InstallCheckExpression);
                builder.AppendLine(")),");
            }
        }

        builder.AppendLine("}");
        builder.AppendLine("print(json.dumps(status))");
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

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private static IEnumerable<InstallComponentSectionViewModel> BuildSections(
        MainWindowViewModel mainWindowViewModel,
        List<InstallComponentItemViewModel> allItems)
    {
        var sections = new List<InstallComponentSectionViewModel>();

        sections.Add(CreateSection(
            "Core",
            allItems,
            CreateItem(
                key: "cuda",
                title: "CUDA",
                description: "Install the NVIDIA CUDA stack used by GPU-backed services. Run this first on NVIDIA systems.",
                installCheckExpression: "shutil.which('nvcc') is not None or Path('/usr/bin/nvcc').exists() or Path('/usr/local/cuda/bin/nvcc').exists()",
                primaryCommand: mainWindowViewModel.InstallCudaCommand,
                supportsNvidiaCuda: true)));

        sections.Add(CreateSection(
            "Service Extensions",
            allItems,
            CreateItem(
                key: "minime",
                title: "Minime and TXT2VEC",
                description: "Installs the small helper LLM and vector service used to improve NPC responses and retrieval.",
                installCheckExpression: "Path('/home/dwemer/python-minime').exists()",
                primaryCommand: mainWindowViewModel.InstallMinimeT5Command,
                supportsNvidiaCuda: true,
                supportsAmdCpu: true)));

        sections.Add(CreateSection(
            "Text-to-Speech Engines",
            allItems,
            CreateItem(
                key: "pockettts",
                title: "Pocket-TTS",
                description: "Compact TTS that supports voice samples and can run in CPU or GPU mode depending on your configuration.",
                installCheckExpression: "Path('/home/dwemer/pocket-tts/venv').exists()",
                primaryCommand: mainWindowViewModel.InstallPocketTtsCommand,
                supportsNvidiaCuda: true,
                supportsAmdCpu: true),
            CreateItem(
                key: "chatterbox",
                title: "Chatterbox",
                description: "High-quality multilingual TTS with voice cloning. Shares port 8020 with XTTS and PocketTTS.",
                installCheckExpression: "Path('/home/dwemer/chatterbox/venv').exists()",
                primaryCommand: mainWindowViewModel.InstallChatterboxCommand,
                supportsNvidiaCuda: true,
                supportsAmdCpu: true),
            CreateItem(
                key: "omnivoice",
                title: "Multilingual OmniVoice TTS",
                description: "Optional local OmniVoice TTS for CHIM voice libraries. Uses a separate service on port 8021.",
                installCheckExpression: "Path('/home/dwemer/omnivoice-tts/venv').exists()",
                primaryCommand: mainWindowViewModel.InstallOmniVoiceCommand,
                supportsNvidiaCuda: true,
                secondaryActionText: "Configure",
                secondaryActionCommand: mainWindowViewModel.ConfigureOmniVoiceCommand,
                tertiaryActionText: "View Logs",
                tertiaryActionCommand: mainWindowViewModel.ViewOmniVoiceLogsCommand),
            CreateItem(
                key: "xtts",
                title: "Dwemer Distro XTTS",
                description: "High-quality XTTS deployment for cloned Skyrim voices. Requires the shared XTTS Python environment.",
                installCheckExpression: "Path('/home/dwemer/python-tts').exists()",
                primaryCommand: mainWindowViewModel.InstallXttsCommand,
                supportsNvidiaCuda: true),
            CreateItem(
                key: "melotts",
                title: "MeloTTS",
                description: "Fast TTS option for lightweight setups with low overhead and strong CPU support.",
                installCheckExpression: "Path('/home/dwemer/python-melotts').exists()",
                primaryCommand: mainWindowViewModel.InstallMeloTtsCommand,
                supportsNvidiaCuda: true,
                supportsAmdCpu: true),
            CreateItem(
                key: "pipertts",
                title: "Piper-TTS",
                description: "Fast local TTS with separate downloadable voice packs. Good when you want simple CPU speech output.",
                installCheckExpression: "Path('/home/dwemer/piper').exists()",
                primaryCommand: mainWindowViewModel.InstallPiperTtsCommand,
                supportsNvidiaCuda: true,
                supportsAmdCpu: true,
                secondaryActionText: "Open Piper Voice Folder",
                secondaryActionCommand: mainWindowViewModel.OpenPiperVoicesFolderCommand),
            CreateItem(
                key: "mimic3",
                title: "Mimic3",
                description: "Older local TTS service that is still useful when you want a simple, lightweight fallback.",
                installCheckExpression: "Path('/home/dwemer/mimic3').exists()",
                primaryCommand: mainWindowViewModel.InstallMimic3Command)));

        sections.Add(CreateSection(
            "Speech-to-Text Engines",
            allItems,
            CreateItem(
                key: "parakeet",
                title: "Parakeet STT",
                description: "Offline speech-to-text service with GPU and CPU modes for local transcription.",
                installCheckExpression: "Path('/home/dwemer/parakeet-api-server/venv').exists()",
                primaryCommand: mainWindowViewModel.InstallParakeetCommand,
                supportsNvidiaCuda: true,
                supportsAmdCpu: true),
            CreateItem(
                key: "localwhisper",
                title: "LocalWhisper",
                description: "Offline Whisper-based speech-to-text service for local microphone and transcription workflows.",
                installCheckExpression: "Path('/home/dwemer/python-stt').exists()",
                primaryCommand: mainWindowViewModel.InstallLocalWhisperCommand,
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
        string installCheckExpression,
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
            installCheckExpression,
            primaryCommand,
            supportsNvidiaCuda,
            supportsAmdCpu,
            secondaryActionText,
            secondaryActionCommand,
            tertiaryActionText,
            tertiaryActionCommand);
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
