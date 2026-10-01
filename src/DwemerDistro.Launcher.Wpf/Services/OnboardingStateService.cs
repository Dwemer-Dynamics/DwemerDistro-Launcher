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

    public async Task MarkCompletedAsync(
        SetupPresetKey preset,
        string voiceEngine,
        bool openRouterConfigured,
        bool huggingFaceConfigured,
        IReadOnlyList<string>? selectedProducts = null,
        IReadOnlyDictionary<string, string>? productInstallResults = null,
        CancellationToken cancellationToken = default)
    {
        var previous = await LoadAsync(cancellationToken).ConfigureAwait(false);
        var state = new OnboardingState
        {
            LocalAiChoice = previous.LocalAiChoice,
            LocalAiCompletedChoice = previous.LocalAiCompletedChoice,
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

        await SaveAsync(state, cancellationToken).ConfigureAwait(false);
    }

    public async Task MarkSkippedAsync(
        SetupPresetKey preset,
        IReadOnlyList<string>? selectedProducts = null,
        IReadOnlyDictionary<string, string>? productInstallResults = null,
        CancellationToken cancellationToken = default)
    {
        var previous = await LoadAsync(cancellationToken).ConfigureAwait(false);
        var state = new OnboardingState
        {
            LocalAiChoice = previous.LocalAiChoice,
            LocalAiCompletedChoice = previous.LocalAiCompletedChoice,
            Version = CurrentVersion,
            Skipped = true,
            SkippedAtUtc = DateTimeOffset.UtcNow,
            SelectedPreset = preset.ToString(),
            SelectedProducts = selectedProducts?.ToList(),
            ProductInstallResults = productInstallResults is null
                ? null
                : new Dictionary<string, string>(productInstallResults, StringComparer.OrdinalIgnoreCase)
        };

        await SaveAsync(state, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Records the Quickstart voice setup that must run once the voice service is up.</summary>
    public async Task SetPendingVoiceApplyAsync(
        string engineKey,
        string? pocketTtsBackend,
        CancellationToken cancellationToken = default)
    {
        var state = await LoadAsync(cancellationToken).ConfigureAwait(false);
        state.PendingVoiceApply = new PendingVoiceApplyState
        {
            EngineKey = engineKey,
            PocketTtsBackend = pocketTtsBackend,
            RequestedAtUtc = DateTimeOffset.UtcNow
        };
        await SaveAsync(state, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Runs the saved Quickstart voice setup through <paramref name="apply"/> and clears it only when
    /// <see cref="VoiceEngineService.IsInitialApplyComplete"/> holds. Returns null, without calling
    /// apply, when no completed onboarding has a request saved.
    /// </summary>
    public async Task<IReadOnlyList<VoiceEngineApplyTargetStatus>?> RunPendingVoiceApplyAsync(
        Func<string, string?, Task<IReadOnlyList<VoiceEngineApplyTargetStatus>>> apply,
        CancellationToken cancellationToken = default)
    {
        var state = await LoadAsync(cancellationToken).ConfigureAwait(false);
        var pending = state.PendingVoiceApply;
        if (!state.Completed || pending is null || string.IsNullOrWhiteSpace(pending.EngineKey))
        {
            return null;
        }

        var targets = await apply(pending.EngineKey, pending.PocketTtsBackend).ConfigureAwait(false);
        if (VoiceEngineService.IsInitialApplyComplete(targets))
        {
            await ClearPendingVoiceApplyAsync(cancellationToken).ConfigureAwait(false);
        }

        return targets;
    }

    /// <summary>Clears the pending voice setup; call only after it succeeded.</summary>
    public async Task ClearPendingVoiceApplyAsync(CancellationToken cancellationToken = default)
    {
        var state = await LoadAsync(cancellationToken).ConfigureAwait(false);
        if (state.PendingVoiceApply is null)
        {
            return;
        }

        state.PendingVoiceApply = null;
        await SaveAsync(state, cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>An explicit Quickstart voice setup waiting for the voice service to start.</summary>
public sealed class PendingVoiceApplyState
{
    public string? EngineKey { get; set; }

    /// <summary>"audiocpp" or "python" when the Quickstart preset chose one; null lets Core decide.</summary>
    public string? PocketTtsBackend { get; set; }

    public DateTimeOffset? RequestedAtUtc { get; set; }
}

public sealed class OnboardingState
{
    public string? LocalAiChoice { get; set; }

    public string? LocalAiCompletedChoice { get; set; }

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

    /// <summary>
    /// Quickstart voice setup deferred until DwemerDistro starts. Kept until it succeeds, so closing
    /// the launcher before the voice service is ready does not lose it. Null when nothing is pending.
    /// </summary>
    public PendingVoiceApplyState? PendingVoiceApply { get; set; }
}
