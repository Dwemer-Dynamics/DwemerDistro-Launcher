using System.Diagnostics;
using System.Text.Json;

namespace DwemerDistro.Launcher.Wpf.Services;

public sealed class DistroSetupService(WslService wsl)
{
    private const int ComponentInstallTimeoutSeconds = 7200;
    private const string NonInteractiveInstallInput = "";
    private const string StorageCleanupCommand = """
set -e

if command -v ddistro_storage >/dev/null 2>&1; then
    ddistro_storage safe-cleanup
else
    apt-get clean
    find /var/lib/apt/lists -mindepth 1 -maxdepth 1 -type f -delete 2>/dev/null || true
    rm -rf /home/dwemer/.cache/pip /root/.cache/pip
    rm -rf /home/dwemer/.cache/uv /root/.cache/uv
    rm -rf /home/dwemer/.npm/_cacache /root/.npm/_cacache
fi
""";
    private const string CudaInstallCommand = """
set -e

if [ ! -x /usr/local/bin/install_cuda_dependencies ]; then
    echo "The minimal CUDA installer is missing. Update DwemerDistro in Quickstart and retry."
    exit 23
fi

/usr/local/bin/install_cuda_dependencies auto
""";
    private const string PrepareDistroCommand = """
set -e
export DEBIAN_FRONTEND=noninteractive
export DEBIAN_PRIORITY=critical
export APT_LISTCHANGES_FRONTEND=none
export NEEDRESTART_MODE=a
export UCF_FORCE_CONFFOLD=1
export GIT_TERMINAL_PROMPT=0

echo "Preparing DwemerDistro package state..."
missing_packages=()
command -v git >/dev/null 2>&1 || missing_packages+=(git)
test -s /etc/ssl/certs/ca-certificates.crt || missing_packages+=(ca-certificates)
command -v python3 >/dev/null 2>&1 || missing_packages+=(python3)
python3 -m venv --help >/dev/null 2>&1 || missing_packages+=(python3-venv)
python3 -m pip --version >/dev/null 2>&1 || missing_packages+=(python3-pip)

if [ ${#missing_packages[@]} -gt 0 ]; then
    echo "Installing missing base packages: ${missing_packages[*]}"
    dpkg --configure -a
    apt-get update
    apt-get install -y "${missing_packages[@]}"
    apt-get clean
    find /var/lib/apt/lists/ -type f -delete
else
    echo "Base distro tools already installed; skipping apt package preparation."
fi

runuser -u dwemer -- bash -lc "
set -e
cd /home/dwemer

if [ ! -d parakeet-api-server/.git ]; then
    rm -rf parakeet-api-server
    git clone --depth 1 https://github.com/Dwemer-Dynamics/parakeet-api-server parakeet-api-server
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
    private const string PocketTtsAudioCppInstallCommand = """
set -e
export DEBIAN_FRONTEND=noninteractive
export DEBIAN_PRIORITY=critical
export APT_LISTCHANGES_FRONTEND=none
export NEEDRESTART_MODE=a
export UCF_FORCE_CONFFOLD=1
export GIT_TERMINAL_PROMPT=0

if [ ! -s /home/dwemer/.cache/huggingface/token ]; then
    echo "Hugging Face token is required for Pocket-TTS. Save it in Quickstart and retry."
    exit 22
fi

if [ ! -x /usr/local/bin/install_audiocpp_pockettts ]; then
    echo "The audio.cpp Pocket-TTS installer is missing. Update DwemerDistro in Quickstart and retry."
    exit 23
fi

BUILD_PARALLEL="${BUILD_PARALLEL:-2}" /usr/local/bin/install_audiocpp_pockettts auto

ln -sfn /home/dwemer/audio.cpp/start-audiocpp-pockettts.sh /home/dwemer/audio.cpp/start.sh
chown -h dwemer:dwemer /home/dwemer/audio.cpp/start.sh

# Keep the legacy Python install available, but prevent it from starting beside audio.cpp.
rm -f /home/dwemer/pocket-tts/start.sh

echo "Pocket-TTS audio.cpp installed and enabled in GPU / CUDA mode."
""";
    private const string PocketTtsInstallCommand = """
set -e
export PIP_NO_INPUT=1
export PIP_DISABLE_PIP_VERSION_CHECK=1
export PIP_NO_CACHE_DIR=1
export GIT_TERMINAL_PROMPT=0

if [ ! -s /home/dwemer/.cache/huggingface/token ]; then
    echo "Hugging Face token is required for Pocket-TTS. Save it in Quickstart and retry."
    exit 22
fi

mkdir -p /home/dwemer
cd /home/dwemer

POCKETTTS_FRESH=0
if [ ! -d /home/dwemer/pocket-tts/venv ]; then
    POCKETTTS_FRESH=1
fi

if [ ! -d /home/dwemer/pocket-tts/.git ]; then
    rm -rf /home/dwemer/pocket-tts
    git clone --depth 1 https://github.com/Dwemer-Dynamics/pocket-tts /home/dwemer/pocket-tts
else
    git -C /home/dwemer/pocket-tts pull --ff-only
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

ln -sf /home/dwemer/pocket-tts/start-cpu.sh /home/dwemer/pocket-tts/start.sh
rm -f /home/dwemer/audio.cpp/start.sh
echo "Pocket-TTS installed and enabled in CPU mode."
""";
    private const string MinimeInstallCommand = """
set -e
export PIP_NO_INPUT=1
export PIP_DISABLE_PIP_VERSION_CHECK=1
export PIP_NO_CACHE_DIR=1

if [ ! -d /home/dwemer/minime-t5 ]; then
    echo "Missing /home/dwemer/minime-t5. Cannot install Minime/TXT2VEC."
    exit 23
fi

cd /home/dwemer/minime-t5

if [ ! -d /home/dwemer/python-minime ]; then
    python3 -m venv /home/dwemer/python-minime
fi

. /home/dwemer/python-minime/bin/activate
python -m pip install --no-cache-dir --upgrade pip wheel setuptools

pytorch_gpu_available() {
    local cuda_home

    [ -r /var/lib/dwemerdistro/cuda-selection.env ] || return 1
    grep -qx 'CUDA_PYTORCH_SUPPORTED=1' /var/lib/dwemerdistro/cuda-selection.env || return 1
    cuda_home="$(sed -n 's|^CUDA_HOME=\(/usr/local/cuda-\(12\.8\|13\.0\)\)$|\1|p' /var/lib/dwemerdistro/cuda-selection.env | head -n 1)"
    [ -n "$cuda_home" ] && [ -x "$cuda_home/bin/nvcc" ]
}

if pytorch_gpu_available; then
    python -m pip install --no-cache-dir --upgrade torch --index-url https://download.pytorch.org/whl/cu128
else
    python -m pip install --no-cache-dir --upgrade torch --index-url https://download.pytorch.org/whl/cpu
fi

python -m pip install --no-cache-dir -r requirements.txt

if pytorch_gpu_available; then
    ln -sf /home/dwemer/minime-t5/start-gpu.sh /home/dwemer/minime-t5/start.sh
    echo "Minime/TXT2VEC installed and enabled in GPU / CUDA mode."
else
    ln -sf /home/dwemer/minime-t5/start-cpu.sh /home/dwemer/minime-t5/start.sh
    echo "Minime/TXT2VEC installed and enabled in CPU mode."
fi
""";
    private const string ParakeetInstallCommand = """
set -e
export PIP_NO_INPUT=1
export PIP_DISABLE_PIP_VERSION_CHECK=1
export PIP_NO_CACHE_DIR=1
export GIT_TERMINAL_PROMPT=0

mkdir -p /home/dwemer
cd /home/dwemer

if [ ! -d /home/dwemer/parakeet-api-server/.git ]; then
    rm -rf /home/dwemer/parakeet-api-server
    git clone --depth 1 https://github.com/Dwemer-Dynamics/parakeet-api-server /home/dwemer/parakeet-api-server
else
    git -C /home/dwemer/parakeet-api-server pull --ff-only
fi

cd /home/dwemer/parakeet-api-server

if [ ! -d venv ]; then
    python3 -m venv venv
fi

. venv/bin/activate
python -m pip install --no-cache-dir --upgrade pip wheel setuptools

cuda_home=""
if [ -r /var/lib/dwemerdistro/cuda-selection.env ] &&
   grep -qx 'CUDA_PYTORCH_SUPPORTED=1' /var/lib/dwemerdistro/cuda-selection.env; then
    cuda_home="$(sed -n 's|^CUDA_HOME=\(/usr/local/cuda-\(12\.8\|13\.0\)\)$|\1|p' /var/lib/dwemerdistro/cuda-selection.env | head -n 1)"
fi

if [ -n "$cuda_home" ] && [ -x "$cuda_home/bin/nvcc" ]; then
    python -m pip install --upgrade --no-cache-dir torch torchvision torchaudio --index-url https://download.pytorch.org/whl/cu128
    python -m pip install --no-cache-dir sherpa-onnx==1.12.13+cuda12.cudnn9 -f https://k2-fsa.github.io/sherpa/onnx/cuda.html
    python -m pip install --no-cache-dir nvidia-cudnn-cu12
    python -m pip install --no-cache-dir -r requirements.txt
    ln -sf /home/dwemer/parakeet-api-server/start-gpu.sh /home/dwemer/parakeet-api-server/start.sh
    echo "Parakeet installed and enabled in GPU / CUDA mode."
else
    python -m pip install --upgrade --no-cache-dir torch torchvision torchaudio --index-url https://download.pytorch.org/whl/cpu
    python -m pip install --no-cache-dir "sherpa-onnx>=1.10.0"
    python -m pip install --no-cache-dir -r requirements.txt
    ln -sf /home/dwemer/parakeet-api-server/start-cpu.sh /home/dwemer/parakeet-api-server/start.sh
    echo "Parakeet installed and enabled in CPU mode."
fi
""";
    private const string ActiveInstallerProbeCommand = """
python3 - <<'PY'
import os
from pathlib import Path

tokens = (
    "/usr/local/bin/install_full_packages",
    "/usr/local/bin/install_cuda_dependencies",
    "ddistro_install.sh",
    "/conf.sh",
    "/install.sh",
    "apt-get install",
    "dpkg --configure",
    "python -m pip install",
    "pip install",
    "/usr/local/bin/install_audiocpp_pockettts",
    "git clone https://github.com/Dwemer-Dynamics/pocket-tts",
    "git clone https://github.com/Dwemer-Dynamics/parakeet-api-server",
)

matches = []
skip_pids = {str(os.getpid()), str(os.getppid())}
for proc in Path("/proc").iterdir():
    if not proc.name.isdigit():
        continue
    if proc.name in skip_pids:
        continue
    try:
        raw = (proc / "cmdline").read_bytes()
    except OSError:
        continue
    if not raw:
        continue
    cmd = raw.replace(b"\0", b" ").decode("utf-8", errors="replace").strip()
    if any(token in cmd for token in tokens):
        matches.append(f"{proc.name}: {cmd}")

print("\n".join(matches[:12]))
PY
""";

    private static readonly SetupComponent[] Components =
    [
        new(
            "cuda",
            "CUDA",
            "NVIDIA CUDA runtime",
            "Path('/var/lib/dwemerdistro/cuda-selection.env').is_file() and (shutil.which('nvcc') is not None or Path('/usr/bin/nvcc').exists() or Path('/usr/local/cuda/bin/nvcc').exists())",
            ["-d", LauncherConstants.DistroName, "--", "bash", "-lc", CudaInstallCommand]),
        new(
            "audiocpp",
            "Pocket-TTS audio.cpp",
            "CUDA Pocket-TTS runtime",
            "Path('/home/dwemer/audio.cpp/build/bin/audiocpp_server').is_file() and Path('/home/dwemer/audio.cpp/start.sh').exists()",
            ["-d", LauncherConstants.DistroName, "--", "bash", "-lc", PocketTtsAudioCppInstallCommand]),
        new(
            "pockettts",
            "Pocket-TTS",
            "CPU Pocket-TTS runtime",
            "Path('/home/dwemer/pocket-tts/venv/bin/python').exists() and Path('/home/dwemer/pocket-tts/start.sh').exists()",
            ["-d", LauncherConstants.DistroName, "-u", LauncherConstants.DistroUser, "--", "bash", "-lc", PocketTtsInstallCommand]),
        new(
            "minime",
            "Minime and TXT2VEC",
            "Local helper model and vector service",
            "Path('/home/dwemer/python-minime/bin/python').exists() and Path('/home/dwemer/minime-t5/start.sh').exists()",
            ["-d", LauncherConstants.DistroName, "-u", LauncherConstants.DistroUser, "--", "bash", "-lc", MinimeInstallCommand]),
        new(
            "parakeet",
            "Parakeet",
            "Local speech-to-text service",
            "Path('/home/dwemer/parakeet-api-server/venv/bin/python').exists() and Path('/home/dwemer/parakeet-api-server/start.sh').exists()",
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
            "Installs CUDA-backed Pocket-TTS audio.cpp, Minime/TXT2VEC, and Parakeet.",
            ["cuda", "audiocpp", "minime", "parakeet"]),
        new(
            SetupPresetKey.AmdCpu,
            "AMD / CPU",
            "Recommended",
            "AMD GPU or CPU",
            "pockettts",
            "Pocket-TTS",
            "Installs Python Pocket-TTS, Minime/TXT2VEC, and Parakeet without CUDA.",
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
        bool skipPreparation = false,
        CancellationToken cancellationToken = default)
    {
        var current = await ProbeAsync(preset, cancellationToken).ConfigureAwait(false);
        if (!current.DistroExists)
        {
            output?.Invoke("DwemerAI4Skyrim3 is not registered. Install or import the distro first." + Environment.NewLine);
            return current;
        }

        var activeInstaller = await GetActiveInstallerProcessSummaryAsync(cancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(activeInstaller))
        {
            output?.Invoke("Another distro installer is already running. Stop it before starting quick setup again." + Environment.NewLine);
            output?.Invoke(activeInstaller + Environment.NewLine);
            return current;
        }

        var totalComponents = preset.ComponentKeys.Count + (skipPreparation ? 0 : 1);
        var completedComponents = 0;
        progress?.Invoke(new SetupInstallProgress(0, totalComponents, preset.Title, "Starting setup"));

        if (skipPreparation)
        {
            output?.Invoke("Distro was already updated in Quick Setup. Skipping extra package preparation." + Environment.NewLine);
            progress?.Invoke(new SetupInstallProgress(
                completedComponents,
                totalComponents,
                preset.Title,
                "Using updated distro"));
        }
        else
        {
            output?.Invoke("Preparing distro before component installs..." + Environment.NewLine);
            progress?.Invoke(new SetupInstallProgress(
                completedComponents,
                totalComponents,
                PrepareComponent.Title,
                "Preparing distro"));

            var prepareResult = await RunComponentInstallAsync(
                    PrepareComponent,
                    output,
                    progress,
                    completedComponents,
                    totalComponents,
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
        }

        if (HuggingFaceTokenService.HasManagedToken)
        {
            var tokenService = new HuggingFaceTokenService(wsl);
            var seededToken = await tokenService.EnsureManagedTokenAsync(
                    overwriteExisting: true,
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            output?.Invoke(seededToken
                ? "Managed Hugging Face token configured for voice model downloads." + Environment.NewLine
                : "Hugging Face token already configured for voice model downloads." + Environment.NewLine);
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
                completedComponents++;
                progress?.Invoke(new SetupInstallProgress(
                    completedComponents,
                    totalComponents,
                    component.Title,
                    $"{component.Title} already installed",
                    IsInstalled: true));
                continue;
            }

            output?.Invoke($"Installing {component.Title}..." + Environment.NewLine);
            output?.Invoke("Running in non-interactive mode with a 2 hour safety timeout." + Environment.NewLine);
            progress?.Invoke(new SetupInstallProgress(
                completedComponents,
                totalComponents,
                component.Title,
                $"Installing {component.Title}"));

            var result = await RunComponentInstallAsync(
                    component,
                    output,
                    progress,
                    completedComponents,
                    totalComponents,
                    cancellationToken)
                .ConfigureAwait(false);

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
                $"{component.Title} installed",
                IsInstalled: true));
            current = await ProbeAsync(preset, cancellationToken).ConfigureAwait(false);
        }

        progress?.Invoke(new SetupInstallProgress(totalComponents, totalComponents, preset.Title, "Setup complete"));
        return await ProbeAsync(preset, cancellationToken).ConfigureAwait(false);
    }

    private async Task<Models.CommandResult> RunComponentInstallAsync(
        SetupComponent component,
        Action<string>? output,
        Action<SetupInstallProgress>? progress,
        int completedComponents,
        int totalComponents,
        CancellationToken cancellationToken)
    {
        using var componentTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        componentTimeout.CancelAfter(TimeSpan.FromSeconds(ComponentInstallTimeoutSeconds + 60));
        using var heartbeatCancellation = CancellationTokenSource.CreateLinkedTokenSource(componentTimeout.Token);
        var stopwatch = Stopwatch.StartNew();

        void PublishProgress(string statusText, string? detailText = null)
        {
            progress?.Invoke(new SetupInstallProgress(
                completedComponents,
                totalComponents,
                component.Title,
                statusText,
                detailText));
        }

        var heartbeatTask = Task.Run(async () =>
        {
            while (!heartbeatCancellation.Token.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(5), heartbeatCancellation.Token).ConfigureAwait(false);
                PublishProgress(
                    $"Installing {component.Title}",
                    $"{component.Title} is still running ({FormatElapsed(stopwatch.Elapsed)} elapsed). Large downloads can take several minutes.");
            }
        }, CancellationToken.None);

        try
        {
            output?.Invoke($"Starting {component.Title} command..." + Environment.NewLine);
            PublishProgress($"Installing {component.Title}", $"Starting {component.Title} command...");
            void HandleOutput(string text)
            {
                output?.Invoke(text);
                var detailText = BuildOutputProgressDetail(text);
                if (!string.IsNullOrWhiteSpace(detailText))
                {
                    PublishProgress($"Installing {component.Title}", detailText);
                }
            }

            var result = await wsl.RunWslWithInputAsync(
                    BuildNonInteractiveInstallArguments(component),
                    component.InstallInput ?? NonInteractiveInstallInput,
                    HandleOutput,
                    componentTimeout.Token)
                .ConfigureAwait(false);
            output?.Invoke($"{component.Title} exited with code {result.ExitCode}." + Environment.NewLine);
            await CleanupInstallerStorageAsync(output).ConfigureAwait(false);
            return result;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new Models.CommandResult(124, string.Empty, $"{component.Title} timed out after 2 hours.");
        }
        finally
        {
            heartbeatCancellation.Cancel();
            try
            {
                await heartbeatTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    // Reclaim only reproducible installer downloads without changing the component result.
    private async Task CleanupInstallerStorageAsync(Action<string>? output)
    {
        using var cleanupTimeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));

        try
        {
            output?.Invoke("Removing reproducible installer downloads..." + Environment.NewLine);
            var result = await wsl.RunWslAsync(
                    ["-d", LauncherConstants.DistroName, "-u", "root", "--", "bash", "-lc", StorageCleanupCommand],
                    cancellationToken: cleanupTimeout.Token)
                .ConfigureAwait(false);
            if (!result.Succeeded)
            {
                output?.Invoke("Storage cleanup was skipped; the component result is unchanged." + Environment.NewLine);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            output?.Invoke($"Storage cleanup was skipped: {ex.Message}" + Environment.NewLine);
        }
    }

    private static string? BuildOutputProgressDetail(string text)
    {
        var line = text.Replace("\r", string.Empty).Replace("\n", string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(line))
        {
            return null;
        }

        if (line.StartsWith("Collecting ", StringComparison.OrdinalIgnoreCase))
        {
            return TrimProgressDetail("Resolving " + line["Collecting ".Length..]);
        }

        if (line.StartsWith("Downloading ", StringComparison.OrdinalIgnoreCase))
        {
            return TrimProgressDetail(line);
        }

        if (line.StartsWith("Installing collected packages:", StringComparison.OrdinalIgnoreCase))
        {
            return "Installing Python packages...";
        }

        if (line.StartsWith("Successfully installed", StringComparison.OrdinalIgnoreCase))
        {
            return "Python packages installed.";
        }

        if (line.StartsWith("Requirement already satisfied:", StringComparison.OrdinalIgnoreCase))
        {
            return TrimProgressDetail("Already installed: " + line["Requirement already satisfied:".Length..].Trim());
        }

        if (line.StartsWith("Setting up ", StringComparison.OrdinalIgnoreCase))
        {
            return TrimProgressDetail("Configuring " + line["Setting up ".Length..]);
        }

        if (line.StartsWith("Unpacking ", StringComparison.OrdinalIgnoreCase))
        {
            return TrimProgressDetail(line);
        }

        if (line.StartsWith("Fetched ", StringComparison.OrdinalIgnoreCase))
        {
            return TrimProgressDetail(line);
        }

        if (line.Contains("Already up to date.", StringComparison.OrdinalIgnoreCase))
        {
            return "Repository already up to date.";
        }

        if (line.Contains("Cloning into", StringComparison.OrdinalIgnoreCase))
        {
            return TrimProgressDetail(line);
        }

        if (line.Contains("MB", StringComparison.OrdinalIgnoreCase) &&
            line.Contains("/", StringComparison.OrdinalIgnoreCase))
        {
            return TrimProgressDetail(line);
        }

        return null;
    }

    private static string TrimProgressDetail(string text)
    {
        const int maxLength = 150;
        var normalized = string.Join(" ", text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return normalized.Length <= maxLength ? normalized : normalized[..(maxLength - 3)] + "...";
    }

    private static string FormatElapsed(TimeSpan elapsed)
    {
        return elapsed.TotalMinutes >= 1
            ? $"{(int)elapsed.TotalMinutes}m {elapsed.Seconds:00}s"
            : $"{elapsed.Seconds}s";
    }

    private async Task<string?> GetActiveInstallerProcessSummaryAsync(CancellationToken cancellationToken)
    {
        try
        {
            var result = await wsl.RunWslAsync(
                    ["-d", LauncherConstants.DistroName, "--", "bash", "-lc", ActiveInstallerProbeCommand],
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            return result.Succeeded ? result.StandardOutput.Trim() : null;
        }
        catch
        {
            return null;
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
        var command = NormalizeWslShellCommandArguments(component.InstallArguments.Skip(separatorIndex.Value + 1));
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
                "PIP_NO_CACHE_DIR=1",
                "timeout",
                "--foreground",
                "--kill-after=30s",
                $"{ComponentInstallTimeoutSeconds}s"
            }).Concat(command).ToArray();
    }

    private static IReadOnlyList<string> NormalizeWslShellCommandArguments(IEnumerable<string> arguments)
    {
        var normalized = new List<string>();
        var normalizeShellCommand = false;

        foreach (var argument in arguments)
        {
            normalized.Add(normalizeShellCommand
                ? argument.Replace("\r\n", "\n").Replace("\r", "\n").Replace("$", "\\$")
                : argument);
            normalizeShellCommand = argument is "-c" or "-lc";
        }

        return normalized;
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
    string StatusText,
    string? DetailText = null,
    bool? IsInstalled = null)
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

