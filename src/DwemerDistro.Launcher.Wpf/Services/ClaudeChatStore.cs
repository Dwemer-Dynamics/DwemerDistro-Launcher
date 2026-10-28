using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DwemerDistro.Launcher.Wpf.Services;

/// <summary>One shown message of a local Claude chat: who said it and the text as shown.</summary>
public sealed record ClaudeChatMessage(string Role, string Text);

/// <summary>
/// The launcher's private copy of one local Claude chat. Core keeps only the thread id and engine, and Claude Code keeps
/// its own session under its own sign-in, so this is what History, Export and a reopened chat show. It holds the user's
/// messages and the replies as shown, the Claude Code session UUID used with <c>--resume</c>, and the model last used.
/// Never a credential, token, account detail, launcher context block or tool payload.
/// </summary>
public sealed record ClaudeChat(int Version, string ThreadId, string SessionId, bool SessionStarted, string Model, ClaudeChatMessage[] Messages)
{
    public const int CurrentVersion = 1;
    public const int MaxMessages = 200;
    public const int MaxMessageChars = 64 * 1024;
    /// <summary>Ends a message the local copy had to shorten. Only the copy is shortened; Claude's own session is untouched.</summary>
    public const string ClippedMarker = "\n\n[Shortened in the launcher's local copy: this message was longer than it keeps.]";
    public static readonly string[] Roles = ["You", "Claude", "DwemerDistro", "Activity"];
    private static readonly Regex ThreadIdPattern = new(@"^[A-Za-z0-9._:-]{1,128}\z");
    private static readonly Regex ModelPattern = new(@"^[A-Za-z0-9._\-\[\]]{1,64}\z");

    public bool IsValid() =>
        Version == CurrentVersion && ThreadIdPattern.IsMatch(ThreadId ?? string.Empty) && Guid.TryParseExact(SessionId, "D", out _)
        && ModelPattern.IsMatch(Model ?? string.Empty) && Messages is { Length: <= MaxMessages }
        && Messages.All(message => message is not null && Roles.Contains(message.Role) && message.Text is { Length: <= MaxMessageChars });

    /// <summary>
    /// True once the local copy no longer holds every shown message: older ones dropped past <see cref="MaxMessages"/> or the
    /// file bound, or a long one shortened. It stays true for the chat.
    /// </summary>
    public bool Partial { get; init; }
}

/// <summary>
/// <c>%LOCALAPPDATA%\DwemerDistro\assistant\claude-chats\&lt;thread&gt;.json</c>, one bounded file per Claude chat, written
/// atomically. Reads fail closed to "no local copy", which the chat then says honestly instead of pretending to continue.
/// </summary>
public sealed class ClaudeChatStore
{
    internal const int MaxFileBytes = 16 * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };
    private readonly string _directory;

    public ClaudeChatStore(string? localAppDataDirectory = null)
    {
        LocalAppDataDirectory = localAppDataDirectory;
        var root = localAppDataDirectory ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        _directory = Path.Combine(root, "DwemerDistro", "assistant", "claude-chats");
    }

    /// <summary>The profile root Claude turns use for their working and per-turn folders too; null is the user's own.</summary>
    public string? LocalAppDataDirectory { get; }

    public ClaudeChat? Load(string threadId)
    {
        if (PathFor(threadId) is not { } path) return null;
        try
        {
            var file = new FileInfo(path);
            if (!file.Exists || file.Length > MaxFileBytes || ClaudeCodeLocator.HasReparsePoint(path)) return null;
            var chat = JsonSerializer.Deserialize<ClaudeChat>(File.ReadAllBytes(path), JsonOptions);
            return chat is not null && chat.IsValid() && chat.ThreadId == threadId ? chat : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>Best effort; returns false when nothing was written (the chat still works, but would reopen without history).</summary>
    public bool Save(ClaudeChat chat)
    {
        chat = Bound(chat);
        if (!chat.IsValid() || PathFor(chat.ThreadId) is not { } path) return false;
        var json = JsonSerializer.SerializeToUtf8Bytes(chat, JsonOptions);
        if (json.Length > MaxFileBytes) return false;
        try
        {
            Directory.CreateDirectory(_directory);
            if (ClaudeCodeLocator.HasReparsePoint(_directory)) return false;
            var temporaryPath = Path.Combine(_directory, $"chat.{Guid.NewGuid():N}.tmp");
            try
            {
                File.WriteAllBytes(temporaryPath, json);
                File.Move(temporaryPath, path, overwrite: true);
            }
            finally
            {
                if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
            }
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LauncherLogService.Operation("Claude chat history could not be saved.");
            return false;
        }
    }

    /// <summary>
    /// The chat as the local copy keeps it: the newest <see cref="ClaudeChat.MaxMessages"/> messages, each at most
    /// <see cref="ClaudeChat.MaxMessageChars"/> with a visible marker, and only as many as fit the file bound beside the
    /// metadata, so the session, model and newest messages always save. Anything left out marks the copy partial.
    /// </summary>
    public static ClaudeChat Bound(ClaudeChat chat)
    {
        var partial = chat.Partial || chat.Messages.Length > ClaudeChat.MaxMessages;
        var messages = chat.Messages.TakeLast(ClaudeChat.MaxMessages).Select(message =>
        {
            if (message?.Text is not { Length: > ClaudeChat.MaxMessageChars } text) return message!;
            partial = true;
            var keep = ClaudeChat.MaxMessageChars - ClaudeChat.ClippedMarker.Length;
            if (char.IsHighSurrogate(text[keep - 1])) keep--;
            return message with { Text = text[..keep] + ClaudeChat.ClippedMarker };
        }).ToArray();
        // The metadata is small and bounded (ids and model are pattern-checked); this headroom always covers it.
        long budget = MaxFileBytes - 64 * 1024;
        var start = messages.Length;
        while (start > 0)
        {
            var size = messages[start - 1] is { } message ? JsonSerializer.SerializeToUtf8Bytes(message, JsonOptions).Length + 1 : 8;
            if (size > budget) break;
            budget -= size;
            start--;
        }
        if (start > 0) partial = true;
        return chat with { Messages = messages[start..], Partial = partial };
    }

    public void Delete(string threadId)
    {
        if (PathFor(threadId) is not { } path) return;
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LauncherLogService.Operation("Claude chat history could not be deleted.");
        }
    }

    // Thread ids are broker-minted UUIDs; anything else never becomes a file name.
    private string? PathFor(string threadId) =>
        Guid.TryParseExact(threadId, "D", out var id) ? Path.Combine(_directory, id.ToString("D") + ".json") : null;
}
