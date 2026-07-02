namespace DwemerDistro.Launcher.Wpf.Services;

public sealed class HardwareDetectionService(ProcessRunner processRunner)
{
    private const int PowerfulNvidiaVramMb = 12000;

    public async Task<HardwareDetectionResult> DetectAsync(CancellationToken cancellationToken = default)
    {
        var nvidiaResult = await TryDetectNvidiaAsync(cancellationToken).ConfigureAwait(false);
        if (nvidiaResult is not null)
        {
            return nvidiaResult;
        }

        var adapterNames = await TryGetDisplayAdapterNamesAsync(cancellationToken).ConfigureAwait(false);
        if (adapterNames.Any(name => name.Contains("nvidia", StringComparison.OrdinalIgnoreCase)))
        {
            return new HardwareDetectionResult(
                SetupPresetKey.NvidiaStandard,
                "NVIDIA GPU detected",
                string.Join(", ", adapterNames),
                true);
        }

        if (adapterNames.Any(name =>
                name.Contains("amd", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("radeon", StringComparison.OrdinalIgnoreCase)))
        {
            return new HardwareDetectionResult(
                SetupPresetKey.AmdCpu,
                "AMD GPU detected",
                string.Join(", ", adapterNames),
                true);
        }

        return new HardwareDetectionResult(
            SetupPresetKey.AmdCpu,
            "No NVIDIA CUDA GPU detected",
            adapterNames.Count == 0
                ? "Using the AMD / CPU setup path."
                : $"Detected adapters: {string.Join(", ", adapterNames)}",
            adapterNames.Count > 0);
    }

    private async Task<HardwareDetectionResult?> TryDetectNvidiaAsync(CancellationToken cancellationToken)
    {
        try
        {
            var result = await processRunner.RunHiddenAsync(
                    "nvidia-smi.exe",
                    new[] { "--query-gpu=name,memory.total", "--format=csv,noheader,nounits" },
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            if (!result.Succeeded || string.IsNullOrWhiteSpace(result.StandardOutput))
            {
                return null;
            }

            var gpus = result.StandardOutput
                .Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(ParseNvidiaGpuLine)
                .Where(gpu => !string.IsNullOrWhiteSpace(gpu.Name))
                .ToArray();

            if (gpus.Length == 0)
            {
                return null;
            }

            var strongest = gpus.OrderByDescending(gpu => gpu.MemoryMb).First();
            var preset = strongest.MemoryMb >= PowerfulNvidiaVramMb
                ? SetupPresetKey.NvidiaPowerful
                : SetupPresetKey.NvidiaStandard;

            var summary = preset == SetupPresetKey.NvidiaPowerful
                ? "Powerful NVIDIA GPU detected"
                : "NVIDIA GPU detected";

            var detail = strongest.MemoryMb > 0
                ? $"{strongest.Name} with {strongest.MemoryMb:N0} MB VRAM"
                : strongest.Name;

            return new HardwareDetectionResult(preset, summary, detail, true);
        }
        catch
        {
            return null;
        }
    }

    private async Task<List<string>> TryGetDisplayAdapterNamesAsync(CancellationToken cancellationToken)
    {
        try
        {
            var result = await processRunner.RunHiddenAsync(
                    "powershell.exe",
                    new[]
                    {
                        "-NoProfile",
                        "-ExecutionPolicy",
                        "Bypass",
                        "-Command",
                        "Get-CimInstance Win32_VideoController | Select-Object -ExpandProperty Name"
                    },
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            if (!result.Succeeded)
            {
                return [];
            }

            return result.StandardOutput
                .Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(line => !string.IsNullOrWhiteSpace(line))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch
        {
            return [];
        }
    }

    private static NvidiaGpu ParseNvidiaGpuLine(string line)
    {
        var parts = line.Split(',', 2, StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
        {
            return new NvidiaGpu(string.Empty, 0);
        }

        var memoryMb = parts.Length > 1 && int.TryParse(parts[1], out var parsedMemory)
            ? parsedMemory
            : 0;

        return new NvidiaGpu(parts[0], memoryMb);
    }

    private readonly record struct NvidiaGpu(string Name, int MemoryMb);
}

public sealed record HardwareDetectionResult(
    SetupPresetKey RecommendedPreset,
    string Summary,
    string Detail,
    bool WasDetected);

