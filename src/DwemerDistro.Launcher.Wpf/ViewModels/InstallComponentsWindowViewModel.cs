using System.Collections.ObjectModel;
using System.Text;
using System.Text.Json;
using System.Windows.Input;
using DwemerDistro.Launcher.Wpf.Services;

namespace DwemerDistro.Launcher.Wpf.ViewModels;

public sealed class InstallComponentsWindowViewModel : ObservableObject
{
    private readonly WslService _wsl = new(new ProcessRunner());
    private readonly HuggingFaceTokenService _huggingFaceToken;
    private readonly List<InstallComponentItemViewModel> _allItems = [];
    private string _huggingFaceTokenStatusText = "Checking";
    private string _huggingFaceTokenDetailText = "Checking Hugging Face token...";
    private string _huggingFaceTokenStatusBackground = "#555555";
    private string _huggingFaceTokenStatusForeground = "White";

    public InstallComponentsWindowViewModel(MainWindowViewModel mainWindowViewModel)
    {
        _huggingFaceToken = new HuggingFaceTokenService(_wsl);
        RefreshHuggingFaceTokenCommand = new AsyncRelayCommand(RefreshHuggingFaceTokenStatusAsync);
        Sections = new ObservableCollection<InstallComponentSectionViewModel>(
            BuildSections(mainWindowViewModel, _allItems));
    }

    public ObservableCollection<InstallComponentSectionViewModel> Sections { get; }

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

        var status = await _huggingFaceToken.GetStatusAsync().ConfigureAwait(true);
        ApplyHuggingFaceTokenStatus(status);
    }

    public Task<string?> ReadHuggingFaceTokenAsync()
    {
        return _huggingFaceToken.ReadTokenAsync();
    }

    public async Task<string?> SaveHuggingFaceTokenAsync(string token)
    {
        var result = await _huggingFaceToken.SaveTokenAsync(token).ConfigureAwait(true);
        return result.Succeeded ? null : HuggingFaceTokenService.BuildErrorText(result);
    }

    public async Task<string?> ClearHuggingFaceTokenAsync()
    {
        var result = await _huggingFaceToken.ClearTokenAsync().ConfigureAwait(true);
        return result.Succeeded ? null : HuggingFaceTokenService.BuildErrorText(result);
    }

    private void SetHuggingFaceTokenCheckingState()
    {
        HuggingFaceTokenStatusText = "Checking";
        HuggingFaceTokenDetailText = "Checking /home/dwemer/.cache/huggingface/token...";
        HuggingFaceTokenStatusBackground = "#555555";
        HuggingFaceTokenStatusForeground = "White";
    }

    private void ApplyHuggingFaceTokenStatus(HuggingFaceTokenStatus status)
    {
        if (!status.IsConfigured && string.IsNullOrWhiteSpace(status.Error))
        {
            HuggingFaceTokenStatusText = "Not configured";
            HuggingFaceTokenDetailText = "No token detected. Chatterbox and Pocket-TTS may ask for one during model downloads.";
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

        Dictionary<string, bool>? installMap;
        try
        {
            installMap = JsonSerializer.Deserialize<Dictionary<string, bool>>(result.StandardOutput.Trim());
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
            if (installMap.TryGetValue(item.Key, out var isInstalled))
            {
                item.SetInstalledState(isInstalled);
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
        builder.AppendLine();
        builder.AppendLine("status = {");

        foreach (var item in _allItems)
        {
            builder.Append("    ");
            builder.Append(JsonSerializer.Serialize(item.Key));
            builder.Append(": bool(");
            builder.Append(item.InstallCheckExpression);
            builder.AppendLine("),");
        }

        builder.AppendLine("}");
        builder.AppendLine("print(json.dumps(status))");
        builder.AppendLine("PY");

        return builder.ToString();
    }

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
        ICommand? secondaryActionCommand = null)
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
            secondaryActionCommand);
    }
}
