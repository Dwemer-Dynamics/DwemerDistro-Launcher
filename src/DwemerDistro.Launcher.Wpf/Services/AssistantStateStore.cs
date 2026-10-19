using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DwemerDistro.Launcher.Wpf.Services;

/// <summary>
/// The references needed to reopen one assistant task after a launcher restart. Identifiers only:
/// no credentials, account details, prompts or transcript text are ever stored here.
/// </summary>
public sealed partial record AssistantTaskReference(
    int Version, string InstallationId, string Distro, string Provider, string Product, string TaskId, string? ThreadId)
{
    public const int CurrentVersion = 1;

    /// <summary>Rejects anything a different or older build could have written, or a hand-edited file.</summary>
    public bool IsValid() =>
        Version == CurrentVersion &&
        Guid.TryParse(InstallationId, out _) &&
        Distro == LauncherConstants.DistroName &&
        ProviderRegex().IsMatch(Provider ?? string.Empty) &&
        Product == "herika" &&
        AssistantCliCommands.IsValidTaskId(TaskId) &&
        (ThreadId is null || ThreadIdRegex().IsMatch(ThreadId));

    /// <summary>The task may be reopened only on the same distro installation and assistant provider.</summary>
    public bool BelongsTo(string? installationId, string provider) =>
        installationId is not null &&
        string.Equals(InstallationId, installationId, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(Provider, provider, StringComparison.Ordinal);

    [GeneratedRegex(@"^[A-Za-z0-9._-]{1,32}\z")]
    internal static partial Regex ProviderRegex();

    [GeneratedRegex(@"^[A-Za-z0-9._:-]{1,128}\z")]
    private static partial Regex ThreadIdRegex();
}

public interface IAssistantStateStore
{
    /// <summary>The saved reference, or null when none is saved or the saved one is unusable.</summary>
    AssistantTaskReference? Load();

    void Save(AssistantTaskReference reference);

    void Clear();
}

/// <summary>
/// One small file in <c>%LOCALAPPDATA%\DwemerDistro\assistant</c>. Every read fails closed to "nothing
/// saved"; every write is best effort, because losing a reference only means starting a new task.
/// </summary>
public sealed class AssistantStateStore : IAssistantStateStore
{
    private const int MaxFileBytes = 4096;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public AssistantStateStore(string? localAppDataDirectory = null)
    {
        var resolvedDirectory = localAppDataDirectory
                                ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        StatePath = Path.Combine(resolvedDirectory, "DwemerDistro", "assistant", "task.json");
    }

    public string StatePath { get; }

    public AssistantTaskReference? Load()
    {
        try
        {
            var file = new FileInfo(StatePath);
            if (!file.Exists || file.Length > MaxFileBytes) return null;
            var reference = JsonSerializer.Deserialize<AssistantTaskReference>(File.ReadAllText(StatePath), JsonOptions);
            return reference is not null && reference.IsValid() ? reference : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            return null;
        }
    }

    public void Save(AssistantTaskReference reference)
    {
        if (!reference.IsValid()) return;
        try
        {
            var directory = Path.GetDirectoryName(StatePath)!;
            Directory.CreateDirectory(directory);
            // Replace atomically so a crash mid-write leaves the previous reference, not a torn file.
            var temporaryPath = Path.Combine(directory, $"task.{Guid.NewGuid():N}.tmp");
            try
            {
                File.WriteAllText(temporaryPath, JsonSerializer.Serialize(reference, JsonOptions));
                File.Move(temporaryPath, StatePath, overwrite: true);
            }
            finally
            {
                if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LauncherLogService.Operation("Assistant task reference could not be saved.");
        }
    }

    public void Clear()
    {
        try
        {
            if (File.Exists(StatePath)) File.Delete(StatePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LauncherLogService.Operation("Assistant task reference could not be cleared.");
        }
    }
}
