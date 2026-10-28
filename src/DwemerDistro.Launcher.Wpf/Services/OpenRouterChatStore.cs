using System.IO;
using System.Text.Json;

namespace DwemerDistro.Launcher.Wpf.Services;

/// <summary>
/// Which global OpenRouter key and model one OpenRouter chat uses: the key's <c>key_id</c> UUID and the model id, nothing
/// else. Core keeps the transcript and resolves the key's secret itself on every turn, so no secret, preview, account detail
/// or message ever lands here. A null <see cref="KeyId"/> means no key was chosen for this chat yet.
/// </summary>
public sealed record OpenRouterChat(int Version, string ThreadId, string? KeyId, string? Model)
{
    public const int CurrentVersion = 1;

    public bool IsValid() =>
        Version == CurrentVersion && Guid.TryParseExact(ThreadId, "D", out _)
        && (KeyId is null || IsKeyId(KeyId))
        && (Model is null || Model.Length is > 0 and <= 128 && Model.All(c => char.IsAsciiLetterOrDigit(c) || c is '/' or '.' or '-' or '_' or ':'));

    /// <summary>Core takes a lowercase UUID as <c>keyId</c>.</summary>
    public static bool IsKeyId(string? keyId) =>
        keyId is { Length: 36 } && Guid.TryParseExact(keyId, "D", out var id) && id.ToString("D") == keyId;
}

/// <summary>
/// <c>%LOCALAPPDATA%\DwemerDistro\assistant\openrouter-chats\&lt;thread&gt;.json</c>, one tiny file per OpenRouter chat, written
/// atomically. Reads fail closed to "no binding", which the chat then states instead of choosing another key.
/// </summary>
public sealed class OpenRouterChatStore
{
    internal const int MaxFileBytes = 4 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };
    private readonly string _directory;

    public OpenRouterChatStore(string? localAppDataDirectory = null)
    {
        var root = localAppDataDirectory ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        _directory = Path.Combine(root, "DwemerDistro", "assistant", "openrouter-chats");
    }

    public OpenRouterChat? Load(string threadId)
    {
        if (PathFor(threadId) is not { } path) return null;
        try
        {
            var file = new FileInfo(path);
            if (!file.Exists || file.Length > MaxFileBytes || ClaudeCodeLocator.HasReparsePoint(path)) return null;
            var chat = JsonSerializer.Deserialize<OpenRouterChat>(File.ReadAllBytes(path), JsonOptions);
            return chat is not null && chat.IsValid() && chat.ThreadId == threadId ? chat : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>Best effort; returns false when nothing was written (the chat then asks for its key again when reopened).</summary>
    public bool Save(OpenRouterChat chat)
    {
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
            LauncherLogService.Operation("OpenRouter chat settings could not be saved.");
            return false;
        }
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
            LauncherLogService.Operation("OpenRouter chat settings could not be deleted.");
        }
    }

    // Thread ids are broker-minted UUIDs; anything else never becomes a file name.
    private string? PathFor(string threadId) =>
        Guid.TryParseExact(threadId, "D", out var id) ? Path.Combine(_directory, id.ToString("D") + ".json") : null;
}
