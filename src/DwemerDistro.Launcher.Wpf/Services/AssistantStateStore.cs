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
    public const int MaxEarlierTasks = 20;

    /// <summary>
    /// Tasks this conversation line used before the current one, oldest first: kept so their conversations stay reopenable
    /// and Core's receipts for them findable. Absent in references written by older launchers.
    /// </summary>
    public AssistantPriorTask[]? EarlierTasks { get; init; }

    /// <summary>
    /// The DwemerDistro tool set <see cref="ThreadId"/> was started with (see <c>AssistantViewModel.ToolRevisionFor</c>);
    /// null or 0 means none. A thread only gets tools at thread/start, so a lower revision than Core now offers is upgraded
    /// once to a new thread of the same task.
    /// </summary>
    public int? ThreadToolRevision { get; init; }

    /// <summary>
    /// Earlier threads of the current task, oldest first, that a tool upgrade replaced. Kept so their conversations stay
    /// reachable; never reassigned to another task.
    /// </summary>
    public string[]? SupersededThreads { get; init; }

    /// <summary>Rejects anything a different or older build could have written, or a hand-edited file.</summary>
    public bool IsValid() =>
        Version == CurrentVersion &&
        Guid.TryParse(InstallationId, out _) &&
        Distro == LauncherConstants.DistroName &&
        ProviderRegex().IsMatch(Provider ?? string.Empty) &&
        Product == "herika" &&
        AssistantCliCommands.IsValidTaskId(TaskId) &&
        IsValidThreadId(ThreadId) &&
        (EarlierTasks is null || EarlierTasks.Length <= MaxEarlierTasks && EarlierTasks.All(task =>
            task is not null && AssistantCliCommands.IsValidTaskId(task.TaskId) && task.TaskId != TaskId && IsValidThreadId(task.ThreadId))) &&
        ThreadToolRevision is null or (>= 0 and <= 1000) &&
        (SupersededThreads is null || SupersededThreads.Length <= MaxEarlierTasks
            && SupersededThreads.All(thread => thread is not null && IsValidThreadId(thread) && thread != ThreadId));

    private static bool IsValidThreadId(string? threadId) => threadId is null || ThreadIdRegex().IsMatch(threadId);

    /// <summary>Same identifiers and the same earlier tasks in order.</summary>
    public bool Equals(AssistantTaskReference? other) =>
        other is not null && Version == other.Version && InstallationId == other.InstallationId && Distro == other.Distro &&
        Provider == other.Provider && Product == other.Product && TaskId == other.TaskId && ThreadId == other.ThreadId &&
        (EarlierTasks ?? []).SequenceEqual(other.EarlierTasks ?? []) && (ThreadToolRevision ?? 0) == (other.ThreadToolRevision ?? 0) &&
        (SupersededThreads ?? []).SequenceEqual(other.SupersededThreads ?? []);

    public override int GetHashCode() => HashCode.Combine(InstallationId, TaskId, ThreadId, EarlierTasks?.Length ?? 0);

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

/// <summary>An earlier task's ids; <paramref name="LiveChange"/> notes that it applied, or may have applied, a reviewed batch.</summary>
public sealed record AssistantPriorTask(string TaskId, string? ThreadId, bool LiveChange);

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
    // Every reference IsValid accepts, 20 earlier tasks and 20 superseded threads with the longest ids included, serializes
    // to well under this (about 10 KiB); Save still measures, so a file it writes is always one Load reads.
    internal const int MaxFileBytes = 24 * 1024;
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
        var json = JsonSerializer.SerializeToUtf8Bytes(reference, JsonOptions);
        if (json.Length > MaxFileBytes)
        {
            LauncherLogService.Operation("Assistant task reference was not saved: it is larger than the launcher reads back.");
            return;
        }
        try
        {
            var directory = Path.GetDirectoryName(StatePath)!;
            Directory.CreateDirectory(directory);
            // Replace atomically so a crash mid-write leaves the previous reference, not a torn file.
            var temporaryPath = Path.Combine(directory, $"task.{Guid.NewGuid():N}.tmp");
            try
            {
                File.WriteAllBytes(temporaryPath, json);
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
