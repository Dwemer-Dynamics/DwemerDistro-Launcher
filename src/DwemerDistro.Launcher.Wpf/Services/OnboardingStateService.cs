using System.IO;
using System.Text.Json;

namespace DwemerDistro.Launcher.Wpf.Services;

public sealed class OnboardingStateService
{
    /// <summary>
    /// Schema written by this build. Version 2 adds the products the user chose in Quickstart and the
    /// per-product install outcome. A version 1 file on disk is still read as-is: Completed and
    /// Skipped mean the same thing in both versions, so an existing setup never sees Quickstart again
    /// just because the schema moved on.
    /// </summary>
    public const int CurrentVersion = 2;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public OnboardingStateService(string? statePath = null)
    {
        StatePath = statePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DwemerDistro",
            "onboarding.json");
    }

    public string StatePath { get; }

    public async Task<OnboardingState> LoadAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            if (!File.Exists(StatePath))
            {
                return new OnboardingState();
            }

            await using var stream = File.OpenRead(StatePath);
            return await JsonSerializer.DeserializeAsync<OnboardingState>(stream, JsonOptions, cancellationToken)
                       .ConfigureAwait(false)
                   ?? new OnboardingState();
        }
        catch
        {
            return new OnboardingState();
        }
    }

    public async Task SaveAsync(OnboardingState state, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(StatePath)!);
        await using var stream = File.Create(StatePath);
        await JsonSerializer.SerializeAsync(stream, state, JsonOptions, cancellationToken).ConfigureAwait(false);
    }

    public Task MarkCompletedAsync(
        SetupPresetKey preset,
        string voiceEngine,
        bool openRouterConfigured,
        bool huggingFaceConfigured,
        IReadOnlyList<string>? selectedProducts = null,
        IReadOnlyDictionary<string, string>? productInstallResults = null,
        CancellationToken cancellationToken = default)
    {
        var state = new OnboardingState
        {
            Version = CurrentVersion,
            Completed = true,
            CompletedAtUtc = DateTimeOffset.UtcNow,
            LastReadyUtc = DateTimeOffset.UtcNow,
            SelectedPreset = preset.ToString(),
            VoiceEngine = voiceEngine,
            OpenRouterConfigured = openRouterConfigured,
            HuggingFaceConfigured = huggingFaceConfigured,
            SelectedProducts = selectedProducts?.ToList(),
            ProductInstallResults = productInstallResults is null
                ? null
                : new Dictionary<string, string>(productInstallResults, StringComparer.OrdinalIgnoreCase)
        };

        return SaveAsync(state, cancellationToken);
    }

    public Task MarkSkippedAsync(
        SetupPresetKey preset,
        IReadOnlyList<string>? selectedProducts = null,
        IReadOnlyDictionary<string, string>? productInstallResults = null,
        CancellationToken cancellationToken = default)
    {
        var state = new OnboardingState
        {
            Version = CurrentVersion,
            Skipped = true,
            SkippedAtUtc = DateTimeOffset.UtcNow,
            SelectedPreset = preset.ToString(),
            SelectedProducts = selectedProducts?.ToList(),
            ProductInstallResults = productInstallResults is null
                ? null
                : new Dictionary<string, string>(productInstallResults, StringComparer.OrdinalIgnoreCase)
        };

        return SaveAsync(state, cancellationToken);
    }
}

public sealed class OnboardingState
{
    /// <summary>
    /// Defaults to 1 so a version 1 file that predates the field, and one that states it, both read
    /// back as version 1 rather than claiming to carry the product keys.
    /// </summary>
    public int Version { get; set; } = 1;

    public bool Completed { get; set; }

    public DateTimeOffset? CompletedAtUtc { get; set; }

    public bool Skipped { get; set; }

    public DateTimeOffset? SkippedAtUtc { get; set; }

    public DateTimeOffset? LastReadyUtc { get; set; }

    public string? SelectedPreset { get; set; }

    public string? VoiceEngine { get; set; }

    public bool OpenRouterConfigured { get; set; }

    public bool HuggingFaceConfigured { get; set; }

    /// <summary>Product keys ticked in Choose Your Mods. Null on a version 1 file.</summary>
    public List<string>? SelectedProducts { get; set; }

    /// <summary>Product key to install outcome ("installed", "failed", "skipped"). Null on version 1.</summary>
    public Dictionary<string, string>? ProductInstallResults { get; set; }
}
