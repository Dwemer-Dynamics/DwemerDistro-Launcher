using System.Text.Json;

namespace DwemerDistro.Launcher.Wpf.Services;

public sealed class DistroSetupService(WslService wsl)
{
    private const int ComponentInstallTimeoutSeconds = 7200;
    private const string NonInteractiveInstallInput = "\n\n\n\n\n\n\n\n\n\n\n\n\n\n\n\n\n\n\n\n";
    private const string CudaInstallCommand =
        "set -e; " +
        "printf '%s\\n' " +
        "'nvidia-cudnn nvidia-cudnn/question select I Agree' " +
        "'nvidia-cudnn nvidia-cudnn/question seen true' " +
        "'nvidia-cudnn nvidia-cudnn/license seen true' " +
        "| debconf-set-selections; " +
        "/usr/local/bin/install_full_packages </dev/null";
    private const string PrepareDistroCommand = """
set -e
export DEBIAN_FRONTEND=noninteractive
export DEBIAN_PRIORITY=critical
export APT_LISTCHANGES_FRONTEND=none
export NEEDRESTART_MODE=a
export UCF_FORCE_CONFFOLD=1

echo "Preparing DwemerDistro package state..."
dpkg --configure -a
apt-get update
apt-get install -y git ca-certificates python3 python3-venv python3-pip

runuser -u dwemer -- bash -lc "
set -e
cd /home/dwemer

if [ ! -d pocket-tts/.git ]; then
    rm -rf pocket-tts
    git clone https://github.com/Dwemer-Dynamics/pocket-tts pocket-tts
else
    git -C pocket-tts pull --ff-only || echo 'Skipping pocket-tts update; local changes or divergent history.'
fi

if [ ! -d parakeet-api-server/.git ]; then
    rm -rf parakeet-api-server
    git clone https://github.com/Dwemer-Dynamics/parakeet-api-server parakeet-api-server
else
    git -C parakeet-api-server pull --ff-only || echo 'Skipping parakeet-api-server update; local changes or divergent history.'
fi

if [ -d minime-t5/.git ]; then
    git -C minime-t5 pull --ff-only || echo 'Skipping minime-t5 update; local changes or divergent history.'
fi

if [ -d remote-faster-whisper/.git ]; then
    git -C remote-faster-whisper pull --ff-only || echo 'Skipping remote-faster-whisper update; local changes or divergent history.'
fi
"

echo "Distro preparation complete."
""";
    private const string PocketTtsInstallCommand = """
set -e
export PIP_NO_INPUT=1
export PIP_DISABLE_PIP_VERSION_CHECK=1

mkdir -p /home/dwemer
cd /home/dwemer

if [ ! -d /home/dwemer/pocket-tts/.git ]; then
    rm -rf /home/dwemer/pocket-tts
    git clone https://github.com/Dwemer-Dynamics/pocket-tts /home/dwemer/pocket-tts
else
    git -C /home/dwemer/pocket-tts pull --ff-only
fi

cd /home/dwemer/pocket-tts

if [ ! -d venv ]; then
    python3 -m venv venv
fi

. venv/bin/activate
python -m pip install --upgrade pip wheel

if command -v nvcc >/dev/null 2>&1 || [ -e /usr/bin/nvcc ] || [ -e /usr/local/cuda/bin/nvcc ]; then
    python -m pip install --upgrade torch --index-url https://download.pytorch.org/whl/cu128
else
    python -m pip install --upgrade torch --index-url https://download.pytorch.org/whl/cpu
fi

python -m pip install -e .

if [ ! -s /home/dwemer/.cache/huggingface/token ]; then
    echo "Hugging Face token is required for Pocket-TTS. Save it in Quickstart and retry."
    exit 22
fi

if command -v nvcc >/dev/null 2>&1 || [ -e /usr/bin/nvcc ] || [ -e /usr/local/cuda/bin/nvcc ]; then
    ln -sf /home/dwemer/pocket-tts/start-gpu.sh /home/dwemer/pocket-tts/start.sh
    echo "Pocket-TTS installed and enabled in GPU / CUDA mode."
else
    ln -sf /home/dwemer/pocket-tts/start-cpu.sh /home/dwemer/pocket-tts/start.sh
    echo "Pocket-TTS installed and enabled in CPU mode."
fi
""";
    private const string ParakeetInstallCommand = """
set -e
export PIP_NO_INPUT=1
export PIP_DISABLE_PIP_VERSION_CHECK=1

mkdir -p /home/dwemer
cd /home/dwemer

if [ ! -d /home/dwemer/parakeet-api-server/.git ]; then
    rm -rf /home/dwemer/parakeet-api-server
    git clone https://github.com/Dwemer-Dynamics/parakeet-api-server /home/dwemer/parakeet-api-server
else
    git -C /home/dwemer/parakeet-api-server pull --ff-only
fi

cd /home/dwemer/parakeet-api-server

if command -v nvcc >/dev/null 2>&1 || [ -e /usr/bin/nvcc ] || [ -e /usr/local/cuda/bin/nvcc ]; then
    printf 'Y\n' | PYTORCH_INDEX_URL="https://download.pytorch.org/whl/cu128" ./install.sh
    ln -sf /home/dwemer/parakeet-api-server/start-gpu.sh /home/dwemer/parakeet-api-server/start.sh
    echo "Parakeet installed and enabled in GPU / CUDA mode."
else
    ./install.sh
    ln -sf /home/dwemer/parakeet-api-server/start-cpu.sh /home/dwemer/parakeet-api-server/start.sh
    echo "Parakeet installed and enabled in CPU mode."
fi
""";

    private static readonly SetupComponent[] Components =
    [
        new(
            "cuda",
            "CUDA",
            "NVIDIA CUDA runtime",
            "shutil.which('nvcc') is not None or Path('/usr/bin/nvcc').exists() or Path('/usr/local/cuda/bin/nvcc').exists()",
            ["-d", LauncherConstants.DistroName, "--", "bash", "-lc", CudaInstallCommand]),
        new(
            "pockettts",
            "Pocket-TTS",
            "Cloned voice engine",
            "Path('/home/dwemer/pocket-tts/venv').exists()",
            ["-d", LauncherConstants.DistroName, "-u", LauncherConstants.DistroUser, "--", "bash", "-lc", PocketTtsInstallCommand]),
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
            ["-d", LauncherConstants.DistroName, "-u", LauncherConstants.DistroUser, "--", "bash", "-lc", ParakeetInstallCommand])
    ];

    private static readonly SetupComponent PrepareComponent = new(
        "prepare",
        "Prepare distro",
        "Refreshes package metadata and required component repositories before installing services.",
        "True",
        ["-d", LauncherConstants.DistroName, "--", "bash", "-lc", PrepareDistroCommand]);

    private static readonly IReadOnlyDictionary<string, SetupComponent> ComponentMap =
        Components.ToDictionary(component => component.Key, StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<SetupPreset> Presets { get; } =
    [
        new(
            SetupPresetKey.NvidiaGpu,
            "NVIDIA",
            "Recommended",
            "NVIDIA GPU",
            "pockettts",
            "Pocket-TTS",
            "Installs CUDA, Pocket-TTS cloned voices, Minime/TXT2VEC, and Parakeet for GPU-backed setup.",
            ["cuda", "pockettts", "minime", "parakeet"]),
        new(
            SetupPresetKey.AmdCpu,
            "AMD / CPU",
            "Recommended",
            "AMD GPU or CPU",
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

    public IReadOnlyList<SetupComponent> GetComponents(SetupPreset preset)
    {
        return preset.ComponentKeys.Select(GetComponent).ToArray();
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
        Action<SetupInstallProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var current = await ProbeAsync(preset, cancellationToken).ConfigureAwait(false);
        if (!current.DistroExists)
        {
            output?.Invoke("DwemerAI4Skyrim3 is not registered. Install or import the distro first." + Environment.NewLine);
            return current;
        }

        var totalComponents = preset.ComponentKeys.Count + 1;
        var completedComponents = 0;
        progress?.Invoke(new SetupInstallProgress(0, totalComponents, preset.Title, "Starting setup"));

        output?.Invoke("Preparing distro before component installs..." + Environment.NewLine);
        progress?.Invoke(new SetupInstallProgress(
            completedComponents,
            totalComponents,
            PrepareComponent.Title,
            "Preparing distro"));

        var prepareResult = await RunComponentInstallAsync(
                PrepareComponent,
                output,
                cancellationToken)
            .ConfigureAwait(false);

        if (!prepareResult.Succeeded)
        {
            var errorText = prepareResult.ExitCode == 124
                ? $"{PrepareComponent.Title} timed out after 2 hours."
                : $"{PrepareComponent.Title} failed: {BuildCommandError(prepareResult)}";
            output?.Invoke(errorText + Environment.NewLine);
            progress?.Invoke(new SetupInstallProgress(
                completedComponents,
                totalComponents,
                PrepareComponent.Title,
                prepareResult.ExitCode == 124 ? $"{PrepareComponent.Title} timed out" : $"{PrepareComponent.Title} failed"));
            return await ProbeAsync(preset, cancellationToken).ConfigureAwait(false);
        }

        completedComponents++;
        progress?.Invoke(new SetupInstallProgress(
            completedComponents,
            totalComponents,
            PrepareComponent.Title,
            "Distro prepared"));

        foreach (var componentKey in preset.ComponentKeys)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var component = GetComponent(componentKey);
            var currentComponent = current.Components.FirstOrDefault(item =>
                string.Equals(item.Key, component.Key, StringComparison.OrdinalIgnoreCase));

            if (currentComponent?.IsInstalled == true)
            {
                output?.Invoke($"{component.Title} already installed. Skipping." + Environment.NewLine);
                completedComponents++;
                progress?.Invoke(new SetupInstallProgress(
                    completedComponents,
                    totalComponents,
                    component.Title,
                    $"{component.Title} already installed"));
                continue;
            }

            output?.Invoke($"Installing {component.Title}..." + Environment.NewLine);
            output?.Invoke("Running in non-interactive mode with a 2 hour safety timeout." + Environment.NewLine);
            progress?.Invoke(new SetupInstallProgress(
                completedComponents,
                totalComponents,
                component.Title,
                $"Installing {component.Title}"));

            var result = await RunComponentInstallAsync(component, output, cancellationToken).ConfigureAwait(false);

            if (!result.Succeeded)
            {
                var errorText = result.ExitCode == 124
                    ? $"{component.Title} timed out after 2 hours."
                    : $"{component.Title} failed: {BuildCommandError(result)}";
                output?.Invoke(errorText + Environment.NewLine);
                progress?.Invoke(new SetupInstallProgress(
                    completedComponents,
                    totalComponents,
                    component.Title,
                    result.ExitCode == 124 ? $"{component.Title} timed out" : $"{component.Title} failed"));
                return await ProbeAsync(preset, cancellationToken).ConfigureAwait(false);
            }

            output?.Invoke($"{component.Title} installed." + Environment.NewLine);
            completedComponents++;
            progress?.Invoke(new SetupInstallProgress(
                completedComponents,
                totalComponents,
                component.Title,
                $"{component.Title} installed"));
            current = await ProbeAsync(preset, cancellationToken).ConfigureAwait(false);
        }

        progress?.Invoke(new SetupInstallProgress(totalComponents, totalComponents, preset.Title, "Setup complete"));
        return await ProbeAsync(preset, cancellationToken).ConfigureAwait(false);
    }

    private async Task<Models.CommandResult> RunComponentInstallAsync(
        SetupComponent component,
        Action<string>? output,
        CancellationToken cancellationToken)
    {
        using var componentTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        componentTimeout.CancelAfter(TimeSpan.FromSeconds(ComponentInstallTimeoutSeconds + 60));

        try
        {
            return await wsl.RunWslWithInputAsync(
                    BuildNonInteractiveInstallArguments(component),
                    component.InstallInput ?? NonInteractiveInstallInput,
                    output,
                    componentTimeout.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new Models.CommandResult(124, string.Empty, $"{component.Title} timed out after 2 hours.");
        }
    }

    private static IReadOnlyList<string> BuildNonInteractiveInstallArguments(SetupComponent component)
    {
        var separatorIndex = component.InstallArguments
            .Select((argument, index) => new { argument, index })
            .FirstOrDefault(item => item.argument == "--")
            ?.index;

        if (separatorIndex is null)
        {
            return component.InstallArguments;
        }

        var prefix = component.InstallArguments.Take(separatorIndex.Value + 1);
        var command = EscapeWslShellCommandArguments(component.InstallArguments.Skip(separatorIndex.Value + 1));
        return prefix.Concat(
            new[]
            {
                "env",
                "DEBIAN_FRONTEND=noninteractive",
                "DEBIAN_PRIORITY=critical",
                "APT_LISTCHANGES_FRONTEND=none",
                "NEEDRESTART_MODE=a",
                "UCF_FORCE_CONFFOLD=1",
                "PIP_NO_INPUT=1",
                "PIP_DISABLE_PIP_VERSION_CHECK=1",
                "timeout",
                "--foreground",
                "--kill-after=30s",
                $"{ComponentInstallTimeoutSeconds}s"
            }).Concat(command).ToArray();
    }

    private static IReadOnlyList<string> EscapeWslShellCommandArguments(IEnumerable<string> arguments)
    {
        var escaped = new List<string>();
        var escapeNext = false;

        foreach (var argument in arguments)
        {
            escaped.Add(escapeNext ? argument.Replace("$", "\\$") : argument);
            escapeNext = argument is "-c" or "-lc";
        }

        return escaped;
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
    NvidiaGpu,
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
    IReadOnlyList<string> InstallArguments,
    string? InstallInput = null);

public sealed record SetupInstallProgress(
    int CompletedComponents,
    int TotalComponents,
    string ComponentTitle,
    string StatusText)
{
    public double Percentage => TotalComponents <= 0
        ? 0
        : Math.Clamp((double)CompletedComponents / TotalComponents * 100, 0, 100);
}

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

