using System.IO;
using System.Text.Json;

namespace DwemerDistro.Launcher.Wpf.Services;

/// <summary>
/// One conversation in the launcher's history list: local metadata and the ids needed to reopen it. Codex's own sessions
/// stay canonical for the messages; nothing here holds transcript text, prompts, credentials or account details.
/// </summary>
public sealed record AssistantCatalogEntry(
    string InstallationId, string Distro, string Provider, string TaskId, string? ThreadId, string Title,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt)
{
    public const int MaxTitleChars = 120;

    /// <summary>Threads of this task a tool upgrade or failed resume replaced, oldest first.</summary>
    public string[]? SupersededThreads { get; init; }
    public int? ThreadToolRevision { get; init; }
    /// <summary>The task applied, or may have applied, a reviewed batch; its receipt stays with Core.</summary>
    public bool LiveChange { get; init; }
    /// <summary>Hidden from the default list by the user; still reopenable from Show archived.</summary>
    public bool Archived { get; init; }
    /// <summary>The user named it; automatic titles never replace a user's.</summary>
    public bool Renamed { get; init; }

    public bool IsValid() =>
        Guid.TryParse(InstallationId, out _) && Distro is { Length: > 0 and <= 64 } &&
        AssistantTaskReference.ProviderRegex().IsMatch(Provider ?? string.Empty) &&
        AssistantCliCommands.IsValidTaskId(TaskId) &&
        // Same rules as the task reference, through it, so the two files never disagree on what is reopenable.
        new AssistantTaskReference(AssistantTaskReference.CurrentVersion, InstallationId, LauncherConstants.DistroName, Provider ?? string.Empty, "herika", TaskId, ThreadId)
        { SupersededThreads = SupersededThreads, ThreadToolRevision = ThreadToolRevision }.IsValid() &&
        IsValidTitle(Title) && CreatedAt <= UpdatedAt.AddMinutes(1);

    public static bool IsValidTitle(string? title) =>
        title is { Length: > 0 and <= MaxTitleChars } && title.Trim() == title && !title.Any(char.IsControl);

    /// <summary>A bounded single-line title: whitespace folded, cut at a word where possible.</summary>
    public static string MakeTitle(string text)
    {
        var line = string.Join(" ", (text ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        line = new string(line.Where(c => !char.IsControl(c)).ToArray()).Trim();
        if (line.Length == 0) return "New chat";
        if (line.Length <= MaxTitleChars) return line;
        var cut = line[..(MaxTitleChars - 1)];
        var space = cut.LastIndexOf(' ');
        return (space > 40 ? cut[..space] : cut).TrimEnd() + "…";
    }

    public bool BelongsTo(string? installationId, string provider) =>
        installationId is not null && string.Equals(InstallationId, installationId, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(Provider, provider, StringComparison.Ordinal);
}

public sealed record AssistantCatalog(int Version, AssistantCatalogEntry[] Entries)
{
    public const int CurrentVersion = 1;
    public const int MaxEntries = 200;
}

public interface IAssistantCatalogStore
{
    /// <summary>The saved entries, or null with a reason when a saved catalogue exists but cannot be used.</summary>
    IReadOnlyList<AssistantCatalogEntry>? Load(out string problem);

    /// <summary>Replaces the catalogue atomically; false with a reason when nothing was written.</summary>
    bool Save(IReadOnlyList<AssistantCatalogEntry> entries, out string problem);
}

/// <summary>
/// <c>%LOCALAPPDATA%\DwemerDistro\assistant\catalog.json</c>, next to the current task reference. A catalogue that
/// exists but is unreadable, too large or invalid is never overwritten: saving is refused until the user moves it,
/// so a bug or a newer launcher's file cannot be replaced by less. Entries that fail validation one by one are left out
/// of a load but reported, and the file stays as it was until the next successful save.
/// </summary>
public sealed class AssistantCatalogStore : IAssistantCatalogStore
{
    // 200 entries with 20 superseded threads of the longest ids each serialize to about 640 KiB; Save still measures.
    internal const int MaxFileBytes = 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private bool _blocked;

    public AssistantCatalogStore(string? localAppDataDirectory = null)
    {
        var resolvedDirectory = localAppDataDirectory ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        CatalogPath = Path.Combine(resolvedDirectory, "DwemerDistro", "assistant", "catalog.json");
    }

    public string CatalogPath { get; }

    public IReadOnlyList<AssistantCatalogEntry>? Load(out string problem)
    {
        problem = string.Empty;
        try
        {
            var file = new FileInfo(CatalogPath);
            if (!file.Exists)
            {
                _blocked = false;
                return [];
            }
            if (file.Length > MaxFileBytes) return Block(out problem, "it is larger than the launcher reads");
            var catalog = JsonSerializer.Deserialize<AssistantCatalog>(File.ReadAllText(CatalogPath), JsonOptions);
            if (catalog is null || catalog.Version != AssistantCatalog.CurrentVersion || catalog.Entries is null)
                return Block(out problem, "it was written by a different launcher version or is damaged");
            var valid = catalog.Entries.Where(entry => entry is not null && entry.IsValid()).Take(AssistantCatalog.MaxEntries).ToList();
            var dropped = catalog.Entries.Length - valid.Count;
            // Keep the first of any duplicated task: the file is newest first.
            var unique = valid.GroupBy(entry => (entry.InstallationId.ToLowerInvariant(), entry.TaskId)).Select(group => group.First()).ToList();
            dropped += valid.Count - unique.Count;
            if (dropped > 0) problem = $"{dropped} saved history entr{(dropped == 1 ? "y was" : "ies were")} unreadable and left out. The file is kept as it was until the next change.";
            _blocked = false;
            return unique;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            return Block(out problem, "it could not be read");
        }
    }

    private IReadOnlyList<AssistantCatalogEntry>? Block(out string problem, string why)
    {
        _blocked = true;
        problem = $"The conversation history file could not be used ({why}), so the launcher will not change it. " +
                  $"Move {CatalogPath} aside to start a new history; your Codex conversations themselves are not affected.";
        return null;
    }

    public bool Save(IReadOnlyList<AssistantCatalogEntry> entries, out string problem)
    {
        problem = string.Empty;
        if (_blocked)
        {
            problem = "The conversation history was not saved: the existing history file could not be read, so it is left unchanged.";
            return false;
        }
        var kept = entries.Where(entry => entry.IsValid()).OrderByDescending(entry => entry.UpdatedAt).Take(AssistantCatalog.MaxEntries).ToArray();
        var json = JsonSerializer.SerializeToUtf8Bytes(new AssistantCatalog(AssistantCatalog.CurrentVersion, kept), JsonOptions);
        if (json.Length > MaxFileBytes)
        {
            problem = "The conversation history was not saved: it is larger than the launcher reads back.";
            return false;
        }
        try
        {
            var directory = Path.GetDirectoryName(CatalogPath)!;
            Directory.CreateDirectory(directory);
            var temporaryPath = Path.Combine(directory, $"catalog.{Guid.NewGuid():N}.tmp");
            try
            {
                File.WriteAllBytes(temporaryPath, json);
                File.Move(temporaryPath, CatalogPath, overwrite: true);
            }
            finally
            {
                if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
            }
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LauncherLogService.Operation("Assistant conversation history could not be saved.");
            problem = "The conversation history could not be saved; the previous history file is unchanged.";
            return false;
        }
    }

    /// <summary>
    /// History entries for a task reference written before the catalogue existed: the current task, its earlier tasks
    /// (newest first) and its superseded threads. The reference file and every native chat stay where they are.
    /// </summary>
    internal static List<AssistantCatalogEntry> Migrate(AssistantTaskReference reference, DateTimeOffset now)
    {
        var entries = new List<AssistantCatalogEntry>
        {
            new(reference.InstallationId, reference.Distro, reference.Provider, reference.TaskId, reference.ThreadId,
                "Current chat (task " + Short(reference.TaskId) + ")", now, now)
            {
                SupersededThreads = reference.SupersededThreads, ThreadToolRevision = reference.ThreadToolRevision
            }
        };
        var earlier = reference.EarlierTasks ?? [];
        for (var index = earlier.Length - 1; index >= 0; index--)
        {
            var task = earlier[index];
            if (entries.Any(entry => entry.TaskId == task.TaskId)) continue;
            var stamp = now.AddSeconds(index - earlier.Length);
            entries.Add(new AssistantCatalogEntry(reference.InstallationId, reference.Distro, reference.Provider, task.TaskId, task.ThreadId,
                "Earlier chat (task " + Short(task.TaskId) + ")", stamp, stamp) { LiveChange = task.LiveChange });
        }
        return entries.Where(entry => entry.IsValid()).ToList();
    }

    private static string Short(string taskId) => taskId.Length > 8 ? taskId[..8] : taskId;
}
