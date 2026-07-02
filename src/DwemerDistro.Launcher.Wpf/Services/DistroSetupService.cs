using System.Text.Json;

namespace DwemerDistro.Launcher.Wpf.Services;

public sealed class DistroSetupService(WslService wsl)
{
    private static readonly SetupComponent[] Components =
    [
        new(
            "cuda",
            "CUDA",
            "NVIDIA CUDA runtime",
            "shutil.which('nvcc') is not None or Path('/usr/bin/nvcc').exists() or Path('/usr/local/cuda/bin/nvcc').exists()",
            ["-d", LauncherConstants.DistroName, "--", "/usr/local/bin/install_full_packages"]),
        new(
            "chatterbox",
            "Chatterbox",
            "Cloned voice engine for powerful NVIDIA systems",
            "Path('/home/dwemer/chatterbox/venv').exists()",
            ["-d", LauncherConstants.DistroName, "-u", LauncherConstants.DistroUser, "--", "/home/dwemer/chatterbox/ddistro_install.sh"]),
        new(
            "pockettts",
            "Pocket-TTS",
            "Cloned voice engine for standard NVIDIA, AMD, and CPU systems",
            "Path('/home/dwemer/pocket-tts/venv').exists()",
            ["-d", LauncherConstants.DistroName, "-u", LauncherConstants.DistroUser, "--", "/home/dwemer/pocket-tts/ddistro_install.sh"]),
        new(
            "minime",
            "Minime and TXT2VEC",
            "Local helper model and vector service",
            "Path('/home/dwemer/python-minime').exists()",
            ["-d", LauncherConstants.DistroName, "-u", LauncherConstants.DistroUser, "--", "/home/dwemer/minime-t5/ddistro_install.sh"]),
        new(
            "parakeet",
            "Parakeet",
            "Local speech-to-text service",
            "Path('/home/dwemer/parakeet-api-server/venv').exists()",
            ["-d", LauncherConstants.DistroName, "-u", LauncherConstants.DistroUser, "--", "/home/dwemer/parakeet-api-server/ddistro_install.sh"])
    ];

    private static readonly IReadOnlyDictionary<string, SetupComponent> ComponentMap =
        Components.ToDictionary(component => component.Key, StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<SetupPreset> Presets { get; } =
    [
        new(
            SetupPresetKey.NvidiaPowerful,
            "NVIDIA Powerful",
            "Recommended",
            "High VRAM NVIDIA",
            "chatterbox",
            "Chatterbox",
            "Installs CUDA, Chatterbox cloned voices, Minime/TXT2VEC, and Parakeet.",
            ["cuda", "chatterbox", "minime", "parakeet"]),
        new(
            SetupPresetKey.NvidiaStandard,
            "NVIDIA Standard",
            "Recommended",
            "NVIDIA CUDA",
            "pockettts",
            "Pocket-TTS",
            "Installs CUDA, Pocket-TTS cloned voices, Minime/TXT2VEC, and Parakeet.",
            ["cuda", "pockettts", "minime", "parakeet"]),
        new(
            SetupPresetKey.AmdCpu,
            "AMD / CPU",
            "Recommended",
            "AMD or no CUDA GPU",
            "pockettts",
            "Pocket-TTS",
            "Installs Pocket-TTS cloned voices, Minime/TXT2VEC, and Parakeet without CUDA.",
            ["pockettts", "minime", "parakeet"])
    ];

    public SetupPreset GetPreset(SetupPresetKey key)
    {
        return Presets.FirstOrDefault(preset => preset.Key == key) ?? Presets.Last();
    }

    public SetupComponent GetComponent(string key)
    {
        return ComponentMap[key];
    }

    public async Task<DistroSetupStatus> ProbeAsync(SetupPreset preset, CancellationToken cancellationToken = default)
    {
        if (!await wsl.DistroExistsAsync(cancellationToken).ConfigureAwait(false))
        {
            return new DistroSetupStatus(false, "Distro not registered", BuildMissingComponentStates(preset));
        }

        var result = await wsl.RunDistroAsUserAsync(
                LauncherConstants.DistroUser,
                new[] { "python3", "-c", BuildProbeScript() },
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        if (!result.Succeeded)
        {
            return new DistroSetupStatus(
                true,
                "Unable to check installed components",
                BuildUnknownComponentStates(preset, BuildCommandError(result)));
        }

        Dictionary<string, bool>? installed;
        try
        {
            installed = JsonSerializer.Deserialize<Dictionary<string, bool>>(result.StandardOutput.Trim());
        }
        catch (JsonException)
        {
            installed = null;
        }

        if (installed is null)
        {
            return new DistroSetupStatus(
                true,
                "Unable to parse component status",
                BuildUnknownComponentStates(preset, "Component status output was not valid JSON."));
        }

        var components = preset.ComponentKeys
            .Select(key =>
            {
                var component = GetComponent(key);
                return new SetupComponentState(
                    component.Key,
                    component.Title,
                    component.Description,
                    installed.TryGetValue(component.Key, out var isInstalled) && isInstalled,
                    null);
            })
            .ToArray();

        var summary = components.All(component => component.IsInstalled)
            ? "Recommended setup is installed"
            : "Recommended setup needs components";

        return new DistroSetupStatus(true, summary, components);
    }

    public async Task<DistroSetupStatus> InstallPresetAsync(
        SetupPreset preset,
        Action<string>? output = null,
        CancellationToken cancellationToken = default)
    {
        var current = await ProbeAsync(preset, cancellationToken).ConfigureAwait(false);
        if (!current.DistroExists)
        {
            output?.Invoke("DwemerAI4Skyrim3 is not registered. Install or import the distro first." + Environment.NewLine);
            return current;
        }

        foreach (var componentKey in preset.ComponentKeys)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var component = GetComponent(componentKey);
            var currentComponent = current.Components.FirstOrDefault(item =>
                string.Equals(item.Key, component.Key, StringComparison.OrdinalIgnoreCase));

            if (currentComponent?.IsInstalled == true)
            {
                output?.Invoke($"{component.Title} already installed. Skipping." + Environment.NewLine);
                continue;
            }

            output?.Invoke($"Installing {component.Title}..." + Environment.NewLine);
            var result = await wsl.RunWslAsync(component.InstallArguments, output, cancellationToken).ConfigureAwait(false);
            if (!result.Succeeded)
            {
                output?.Invoke($"{component.Title} failed: {BuildCommandError(result)}" + Environment.NewLine);
                return await ProbeAsync(preset, cancellationToken).ConfigureAwait(false);
            }

            output?.Invoke($"{component.Title} installed." + Environment.NewLine);
            current = await ProbeAsync(preset, cancellationToken).ConfigureAwait(false);
        }

        return await ProbeAsync(preset, cancellationToken).ConfigureAwait(false);
    }

    private static string BuildProbeScript()
    {
        var lines = new List<string>
        {
            "from pathlib import Path",
            "import json",
            "import shutil",
            "status = {"
        };

        foreach (var component in Components)
        {
            lines.Add($"    {JsonSerializer.Serialize(component.Key)}: bool({component.InstallCheckExpression}),");
        }

        lines.Add("}");
        lines.Add("print(json.dumps(status))");
        return string.Join("\n", lines);
    }

    private static SetupComponentState[] BuildMissingComponentStates(SetupPreset preset)
    {
        return preset.ComponentKeys
            .Select(key =>
            {
                var component = ComponentMap[key];
                return new SetupComponentState(
                    component.Key,
                    component.Title,
                    component.Description,
                    false,
                    "Distro not registered.");
            })
            .ToArray();
    }

    private static SetupComponentState[] BuildUnknownComponentStates(SetupPreset preset, string error)
    {
        return preset.ComponentKeys
            .Select(key =>
            {
                var component = ComponentMap[key];
                return new SetupComponentState(
                    component.Key,
                    component.Title,
                    component.Description,
                    false,
                    error);
            })
            .ToArray();
    }

    private static string BuildCommandError(Models.CommandResult result)
    {
        var text = (result.StandardError + result.StandardOutput).Trim();
        return string.IsNullOrWhiteSpace(text) ? $"Exit code {result.ExitCode}" : text;
    }
}

public enum SetupPresetKey
{
    NvidiaPowerful,
    NvidiaStandard,
    AmdCpu
}

public sealed record SetupPreset(
    SetupPresetKey Key,
    string Title,
    string Badge,
    string HardwareLabel,
    string VoiceEngineKey,
    string VoiceEngineName,
    string Description,
    IReadOnlyList<string> ComponentKeys);

public sealed record SetupComponent(
    string Key,
    string Title,
    string Description,
    string InstallCheckExpression,
    IReadOnlyList<string> InstallArguments);

public sealed record SetupComponentState(
    string Key,
    string Title,
    string Description,
    bool IsInstalled,
    string? Error);

public sealed record DistroSetupStatus(
    bool DistroExists,
    string Summary,
    IReadOnlyList<SetupComponentState> Components)
{
    public bool AllRequiredInstalled => DistroExists && Components.All(component => component.IsInstalled);
}

