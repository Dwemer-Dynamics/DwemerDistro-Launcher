using System.IO;
using System.Text.Json;

namespace DwemerDistro.Launcher.Wpf.Services;

public sealed class OnboardingStateService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public string StatePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DwemerDistro",
        "onboarding.json");

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
        CancellationToken cancellationToken = default)
    {
        var state = new OnboardingState
        {
            Version = 1,
            Completed = true,
            CompletedAtUtc = DateTimeOffset.UtcNow,
            LastReadyUtc = DateTimeOffset.UtcNow,
            SelectedPreset = preset.ToString(),
            VoiceEngine = voiceEngine,
            OpenRouterConfigured = openRouterConfigured,
            HuggingFaceConfigured = huggingFaceConfigured
        };

        return SaveAsync(state, cancellationToken);
    }
}

public sealed class OnboardingState
{
    public int Version { get; set; } = 1;

    public bool Completed { get; set; }

    public DateTimeOffset? CompletedAtUtc { get; set; }

    public DateTimeOffset? LastReadyUtc { get; set; }

    public string? SelectedPreset { get; set; }

    public string? VoiceEngine { get; set; }

    public bool OpenRouterConfigured { get; set; }

    public bool HuggingFaceConfigured { get; set; }
}

