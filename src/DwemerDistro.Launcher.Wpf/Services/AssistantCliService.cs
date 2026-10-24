using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using DwemerDistro.Launcher.Wpf.Models;

namespace DwemerDistro.Launcher.Wpf.Services;

/// <summary>One assistant memory note as Core reports it; text is the user's or assistant's note, never a transcript.</summary>
public sealed record AssistantMemoryNote(long Id, string Kind, string Text, string Source, string? TaskId, string? ThreadId,
    string CapturedAt, string UpdatedAt, string? ExpiresAt, bool Expired)
{
    public string SourceText =>
        (Source == "assistant" ? "Learned by the assistant" : "Added by you") + $" · {Kind} · captured {CapturedAt}" +
        (UpdatedAt != CapturedAt ? $" · edited {UpdatedAt}" : string.Empty) +
        (TaskId is null ? string.Empty : $" · task {(TaskId.Length > 8 ? TaskId[..8] : TaskId)}") +
        (ThreadId is null ? string.Empty : $" · chat {(ThreadId.Length > 8 ? ThreadId[..8] : ThreadId)}") +
        (Expired ? " · expired" : ExpiresAt is null ? string.Empty : $" · expires {ExpiresAt}");
}

/// <summary>One <c>ddistro --json</c> document, or a launcher-side failure when none was readable.</summary>
public sealed record AssistantCliResult(
    bool Ok,
    string Command,
    string? OperationId,
    JsonElement Data,
    string ErrorCode,
    string ErrorMessage,
    IReadOnlyList<string> Warnings,
    bool HasDocument)
{
    // Launcher-side codes. Core codes (busy, conflict, partial...) arrive verbatim from the document.
    public const string CliMissing = "cli_missing";
    public const string DistroMissing = "distro_missing";
    public const string Protocol = "protocol";
    public const string Timeout = "timeout";

    /// <summary><c>error.details</c>: for a failed apply or rollback, Core's per-file result lives here, not in Data.</summary>
    public JsonElement ErrorDetails { get; init; }

    /// <summary>How many warnings the document carried; <see cref="Warnings"/> may hold fewer.</summary>
    public int WarningTotal { get; init; }

    internal static AssistantCliResult Failure(string code, string message) =>
        new(false, string.Empty, null, default, code, message, [], HasDocument: false);
}

public interface IAssistantCli
{
    /// <summary>The WSL distro every command and session of this CLI reaches.</summary>
    string DistroName => LauncherConstants.DistroName;

    Task<AssistantCliResult> RunAsync(IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken);

    /// <summary>Attaches this UI to Core's authenticated Assistant API through its root-local relay.</summary>
    Task<IAssistantTransport> StartServeAsync(string taskId, CancellationToken cancellationToken);
}

public interface IAssistantTransport : IAsyncDisposable
{
    /// <summary>Returns the next line, or null once the stream has closed.</summary>
    Task<string?> ReadLineAsync(CancellationToken cancellationToken);

    Task WriteLineAsync(string line, CancellationToken cancellationToken);

    /// <summary>Bounded stderr tail for a disconnect message. Never logged.</summary>
    string DiagnosticTail { get; }
}

/// <summary>
/// Allowlisted argument vectors for the Core assistant contract. Task ids, hashes, products and
/// relative paths are validated here, so nothing typed into the launcher reaches the CLI unchecked.
/// </summary>
public static partial class AssistantCliCommands
{
    public const string CliPath = "/usr/local/bin/ddistro";
    public const int MaxRelativePaths = 20;

    public static string[] Capabilities() => ["capabilities"];
    public static string[] AgentStatus() => ["agent", "status"];
    public static string[] AgentProbe() => ["agent", "probe"];
    /// <summary>Dry run for the one account-home config Core can repair; its sha256 is the proposal the user confirms.</summary>
    public static string[] AgentHomeConfig() => ["agent", "home-config"];

    public static string[] AgentHomeConfigRepair(string sha256)
    {
        if (!Sha256Regex().IsMatch(sha256 ?? string.Empty))
            throw new ArgumentException("The proposal hash must be 64 lowercase hex characters.", nameof(sha256));
        return ["agent", "home-config-repair", "--sha256", sha256!];
    }
    /// <summary>CHIM's configured profile/model assignments, already projected and redacted by Core.</summary>
    public static string[] Models() => ["models", "herika"];
    /// <summary>A fresh, read-only presence snapshot of the whole distro: servers, ext folders, components, custom mods.</summary>
    public static string[] Inventory() => ["inventory"];
    public static string[] Diff(string taskId) => ["changes", "diff", RequireTaskId(taskId)];
    public static string[] Stage(string taskId) => ["changes", "stage", RequireTaskId(taskId)];
    public static string[] ChangeStatus(string taskId) => ["changes", "status", RequireTaskId(taskId)];
    public static string[] Rollback(string taskId) => ["changes", "rollback", RequireTaskId(taskId)];

    // ddistro memory: Core's MEMORY_SPEC limits, checked here too so a refused value never reaches the CLI.
    public const int MaxMemoryText = 2000;
    public const int MaxMemoryQuery = 200;
    public const int MaxMemoryListPage = 200;
    public static readonly string[] MemoryKinds = ["preference", "decision", "note"];

    public static string[] MemoryStatus() => ["memory", "status"];
    public static string[] MemoryExport() => ["memory", "export"];
    public static string[] MemoryReset() => ["memory", "reset", "--yes"];

    public static string[] MemoryList(int limit, int offset) =>
        limit is < 1 or > MaxMemoryListPage || offset is < 0 or > 1000
            ? throw new ArgumentException("The memory page is out of range.")
            : ["memory", "list", "--limit", limit.ToString(System.Globalization.CultureInfo.InvariantCulture),
               "--offset", offset.ToString(System.Globalization.CultureInfo.InvariantCulture)];

    public static string[] MemorySearch(string query) =>
        ["memory", "search", "--query", RequireMemoryValue(query, MaxMemoryQuery, "search"), "--limit", "10"];

    /// <summary>A user note; the task and thread it was written from are kept as its source when known.</summary>
    public static string[] MemoryAdd(string text, string kind, string? taskId, string? threadId)
    {
        var arguments = new List<string> { "memory", "add", "--text", RequireMemoryValue(text, MaxMemoryText, "note"), "--kind", RequireKind(kind) };
        if (taskId is not null) arguments.AddRange(["--task", RequireTaskId(taskId)]);
        if (threadId is not null) arguments.AddRange(["--thread", RequireMemoryThread(threadId)]);
        return [.. arguments];
    }

    public static string[] MemoryUpdate(long id, string text, string kind) =>
        ["memory", "update", "--id", RequireNoteId(id), "--text", RequireMemoryValue(text, MaxMemoryText, "note"), "--kind", RequireKind(kind)];

    public static string[] MemoryForget(long id) => ["memory", "forget", "--id", RequireNoteId(id)];

    public static string[] MemoryForgetThread(string threadId) => ["memory", "forget", "--thread", RequireMemoryThread(threadId)];

    public static string[] MemoryConfigure(bool? retrieval, bool? autoLearning)
    {
        if (retrieval is null && autoLearning is null) throw new ArgumentException("Nothing to configure.");
        var arguments = new List<string> { "memory", "configure" };
        if (retrieval is { } on) arguments.AddRange(["--retrieval", on ? "on" : "off"]);
        if (autoLearning is { } learn) arguments.AddRange(["--auto-learning", learn ? "on" : "off"]);
        return [.. arguments];
    }

    /// <summary>
    /// Text Core accepts: trimmed 1..limit characters, tabs and newlines only as control characters, and never a value
    /// ddistro could read as one of its own options.
    /// </summary>
    public static bool IsValidMemoryValue(string? value, int limit)
    {
        var text = value?.Trim() ?? string.Empty;
        return text.Length > 0 && text.Length <= limit && !text.StartsWith("--", StringComparison.Ordinal)
               && !text.Any(c => char.IsControl(c) && c is not ('\n' or '\t'));
    }

    private static string RequireMemoryValue(string value, int limit, string what) =>
        IsValidMemoryValue(value, limit) ? value.Trim()
            : throw new ArgumentException($"The {what} must be 1-{limit} characters of plain text and not start with \"--\".");

    private static string RequireKind(string kind) =>
        MemoryKinds.Contains(kind) ? kind : throw new ArgumentException("Unknown memory kind.", nameof(kind));

    private static string RequireNoteId(long id) =>
        id is >= 1 and <= int.MaxValue ? id.ToString(System.Globalization.CultureInfo.InvariantCulture) : throw new ArgumentException("Invalid note id.");

    /// <summary>Core's THREAD_ID for memory provenance: narrower than the launcher's own thread id check.</summary>
    public static bool IsValidMemoryThread(string? threadId) => MemoryThreadRegex().IsMatch(threadId ?? string.Empty);

    private static string RequireMemoryThread(string threadId) =>
        IsValidMemoryThread(threadId) ? threadId : throw new ArgumentException("Invalid thread id for memory.", nameof(threadId));

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9_-]{0,127}\z")]
    private static partial Regex MemoryThreadRegex();

    public static string[] Apply(string taskId, string manifestSha256)
    {
        if (!Sha256Regex().IsMatch(manifestSha256 ?? string.Empty))
            throw new ArgumentException("The manifest hash must be 64 lowercase hex characters.", nameof(manifestSha256));
        return ["changes", "apply", RequireTaskId(taskId), "--manifest-sha256", manifestSha256!];
    }

    public static string[] Prepare(string product, IReadOnlyList<string> relativePaths)
    {
        if (product != "herika")
            throw new ArgumentException("Only CHIM (herika) is supported.", nameof(product));
        if (relativePaths.Count > MaxRelativePaths)
            throw new ArgumentException($"Select at most {MaxRelativePaths} files.", nameof(relativePaths));
        var arguments = new List<string> { "changes", "prepare", "--product", product };
        foreach (var path in relativePaths)
        {
            if (!IsSafeRelativePath(path))
                throw new ArgumentException("Paths must be relative to the product folder.", nameof(relativePaths));
            arguments.Add("--path");
            arguments.Add(path);
        }
        return [.. arguments];
    }

    /// <summary>Plain relative paths only: no root, drive, parent step, option prefix or shell characters.</summary>
    public static bool IsSafeRelativePath(string? path)
    {
        if (string.IsNullOrEmpty(path) || path.Length > 240 || path[0] == '/' || path[0] == '-' || !RelativePathRegex().IsMatch(path))
            return false;
        return path.Split('/').All(segment => segment.Length > 0 && segment != "." && segment != "..");
    }

    public static bool IsValidTaskId(string? taskId) => TaskIdRegex().IsMatch(taskId ?? string.Empty);

    public static bool IsValidManifestHash(string? hash) => Sha256Regex().IsMatch(hash ?? string.Empty);

    private static string RequireTaskId(string taskId) =>
        IsValidTaskId(taskId) ? taskId : throw new ArgumentException("Invalid assistant task id.", nameof(taskId));

    /// <summary>Read-only queries run as the application user; the root broker owns everything else.</summary>
    internal static string UserFor(IReadOnlyList<string> arguments) =>
        arguments.Count > 0 && arguments[0] == "capabilities" ? LauncherConstants.DistroUser : "root";

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._:-]{0,63}\z")]
    private static partial Regex TaskIdRegex();

    [GeneratedRegex(@"^[0-9a-f]{64}\z")]
    private static partial Regex Sha256Regex();

    [GeneratedRegex(@"^[A-Za-z0-9._/-]+\z")]
    private static partial Regex RelativePathRegex();
}

public sealed partial class AssistantCliService : IAssistantCli
{
    private const int MaxDocumentChars = 8 * 1024 * 1024;
    private readonly WslService _wsl;
    private readonly ProcessRunner _processRunner;

    public AssistantCliService(WslService wsl, ProcessRunner processRunner)
        : this(wsl, processRunner, LauncherConstants.DistroName)
    {
    }

    /// <summary>
    /// Test seam for an isolated, separately registered distro. Only the distro name changes: the CLI path,
    /// users and supervision stay fixed, and every query, session and cleanup reaches this one distro.
    /// </summary>
    internal AssistantCliService(WslService wsl, ProcessRunner processRunner, string distroName)
    {
        if (!IsPlainDistroName(distroName))
            throw new ArgumentException("The distro name must be a plain WSL distro name.", nameof(distroName));
        _wsl = wsl;
        _processRunner = processRunner;
        DistroName = distroName;
    }

    public string DistroName { get; }

    internal static bool IsPlainDistroName(string? name) => DistroNameRegex().IsMatch(name ?? string.Empty);

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._-]{0,63}\z")]
    private static partial Regex DistroNameRegex();

    public async Task<AssistantCliResult> RunAsync(
        IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var requestId = Guid.NewGuid().ToString("N");
        string[] cli = [AssistantCliCommands.CliPath, .. arguments, "--json", "--request-id", requestId];
        var user = AssistantCliCommands.UserFor(arguments);
        var verb = string.Join(" ", arguments.Take(2));
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);
        LauncherLogService.Operation($"Assistant START {verb} ({requestId})");
        CommandResult result;
        try
        {
            result = user == "root"
                ? await RunSupervisedAsync(cli, timeoutCts.Token).ConfigureAwait(false)
                : await _wsl.RunWslAsync(ExecArguments(user, cli), cancellationToken: timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            LauncherLogService.Operation($"Assistant END {verb}; timed out");
            return AssistantCliResult.Failure(AssistantCliResult.Timeout, "DwemerDistro did not answer in time. The outcome is unknown.");
        }
        catch (System.ComponentModel.Win32Exception)
        {
            LauncherLogService.Operation($"Assistant END {verb}; wsl.exe unavailable");
            return AssistantCliResult.Failure(AssistantCliResult.DistroMissing, "WSL is not available on this PC.");
        }

        var parsed = Parse(result);
        LauncherLogService.Operation($"Assistant END {verb}; exit code {result.ExitCode}; ok={parsed.Ok}; code={parsed.ErrorCode}");
        return parsed;
    }

    public async Task<IAssistantTransport> StartServeAsync(string taskId, CancellationToken cancellationToken)
    {
        if (!AssistantCliCommands.IsValidTaskId(taskId)) throw new ArgumentException("Invalid assistant task id.", nameof(taskId));
        await TryReuseCodexSignInAsync(cancellationToken).ConfigureAwait(false);
        var pidFile = NewPidFile();
        string[] cli = ["/usr/local/bin/ddistro_api", "bridge", "--task",
            AssistantCliCommands.IsValidTaskId(taskId) ? taskId : throw new ArgumentException("Invalid assistant task id.", nameof(taskId))];
        cancellationToken.ThrowIfCancellationRequested();
        LauncherLogService.Operation("Assistant session START");
        var process = _processRunner.StartHiddenDuplexProcess("wsl.exe", ExecArguments("root", BuildSupervisor(pidFile, cli)));
        var transport = new SupervisedTransport(process, _processRunner, () => StopGroupAsync(pidFile, true));
        transport.StartStderrDrain();
        return transport;
    }

    // Seed only an empty distro account before its server starts. Older Core simply keeps normal sign-in.
    private async Task TryReuseCodexSignInAsync(CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            var home = Environment.GetEnvironmentVariable("CODEX_HOME");
            if (string.IsNullOrWhiteSpace(home))
                home = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
            var path = Path.GetFullPath(Path.Combine(home, "auth.json"));
            // Refuse junctions/symlinks rather than importing credentials from an unexpected location.
            for (FileSystemInfo? entry = new FileInfo(path); entry is not null;
                 entry = entry is FileInfo file ? file.Directory : ((DirectoryInfo)entry).Parent)
                if ((entry.Attributes & FileAttributes.ReparsePoint) != 0) return;
            using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (source.Length is <= 0 or > 65536) return;
            using var reader = new StreamReader(source, Encoding.UTF8, detectEncodingFromByteOrderMarks: false);
            var buffer = new char[65537];
            var count = await reader.ReadBlockAsync(buffer.AsMemory(), timeout.Token).ConfigureAwait(false);
            if (count is <= 0 or > 65536) return;
            var text = new string(buffer, 0, count);
            using var document = JsonDocument.Parse(text);
            if (!IsReusableCodexAuth(document.RootElement)) return;
            // Credentials travel only through stdin. Do not log input, output, paths, or exception messages.
            var check = await _wsl.RunWslAsync(ExecArguments("root", ["/usr/local/bin/ddistro_agent", "auth-import", "--check"]),
                cancellationToken: timeout.Token).ConfigureAwait(false);
            if (!check.Succeeded) return;
            using var state = JsonDocument.Parse(check.StandardOutput);
            if (state.RootElement.ValueKind != JsonValueKind.Object
                || !state.RootElement.TryGetProperty("state", out var value) || value.GetString() != "empty") return;
            // The existing maintenance gate drains an idle API and refuses queued/running work before importing.
            var pidFile = NewPidFile();
            try
            {
                await _wsl.RunWslWithInputAsync(ExecArguments("root", BuildSupervisor(pidFile,
                    ["/usr/local/bin/ddistro_api", "maintenance", "/usr/local/bin/ddistro_agent", "auth-import"])),
                    text, cancellationToken: timeout.Token).ConfigureAwait(false);
            }
            finally
            {
                await StopGroupAsync(pidFile, timeout.IsCancellationRequested).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException
                                   or ArgumentException or InvalidOperationException or NotSupportedException or System.ComponentModel.Win32Exception) { }
        cancellationToken.ThrowIfCancellationRequested();
    }

    internal static bool IsReusableCodexAuth(JsonElement document)
    {
        if (document.ValueKind != JsonValueKind.Object
            || document.TryGetProperty("OPENAI_API_KEY", out var key) && key.ValueKind is not JsonValueKind.Null
            || document.TryGetProperty("auth_mode", out var mode) && (mode.ValueKind != JsonValueKind.String || mode.GetString() != "chatgpt")
            || !document.TryGetProperty("tokens", out var tokens) || tokens.ValueKind != JsonValueKind.Object) return false;
        if (!new[] { "access_token", "refresh_token", "id_token", "account_id" }.All(name =>
            tokens.TryGetProperty(name, out var token) && token.ValueKind == JsonValueKind.String
            && token.GetString() is { Length: > 0 and <= 32768 })) return false;
        try
        {
            var parts = tokens.GetProperty("access_token").GetString()!.Split('.');
            if (parts.Length != 3) return false;
            var payload = parts[1].Replace('-', '+').Replace('_', '/');
            using var claims = JsonDocument.Parse(Convert.FromBase64String(payload.PadRight((payload.Length + 3) / 4 * 4, '=')));
            return claims.RootElement.ValueKind == JsonValueKind.Object
                && claims.RootElement.TryGetProperty("exp", out var expires) && expires.TryGetInt64(out var seconds)
                && seconds > DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 60;
        }
        catch (Exception ex) when (ex is FormatException or JsonException or InvalidOperationException) { return false; }
    }

    /// <summary>
    /// Root commands run as their own Linux process group recorded in a private pid file, because
    /// ending wsl.exe does not reliably end processes inside the distro (see DistroUpdateRunner).
    /// </summary>
    private async Task<CommandResult> RunSupervisedAsync(string[] cli, CancellationToken cancellationToken)
    {
        var pidFile = NewPidFile();
        try
        {
            return await _wsl.RunWslAsync(ExecArguments("root", BuildSupervisor(pidFile, cli)),
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await StopGroupAsync(pidFile, cancellationToken.IsCancellationRequested).ConfigureAwait(false);
        }
    }

    private async Task StopGroupAsync(string pidFile, bool waitForPidFile)
    {
        using var cleanupTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var script = (waitForPidFile ? "for attempt in 1 2 3 4 5; do [ -s \"$1\" ] && break; sleep 0.2; done; " : "") +
            "if [ -s \"$1\" ]; then read -r pid < \"$1\"; " +
            "case \"$pid\" in ''|*[!0-9]*) exit 1;; esac; " +
            "if kill -0 -- -\"$pid\" 2>/dev/null; then kill -TERM -- -\"$pid\" 2>/dev/null; sleep 1; " +
            "kill -KILL -- -\"$pid\" 2>/dev/null || true; fi; rm -f \"$1\"; fi";
        try
        {
            var result = await _wsl.RunWslAsync(ExecArguments("root", ["bash", "-c", script, "ddistro-assistant-stop", pidFile]),
                cancellationToken: cleanupTimeout.Token).ConfigureAwait(false);
            if (!result.Succeeded) LauncherLogService.Operation("Assistant could not confirm its Linux process group stopped.");
        }
        catch (Exception ex) when (ex is OperationCanceledException or System.ComponentModel.Win32Exception)
        {
            LauncherLogService.Operation("Assistant process cleanup did not complete.");
        }
    }

    private static string NewPidFile() => "/run/dwemerdistro-assistant-" + Guid.NewGuid().ToString("N") + ".pid";

    /// <summary>The one route to wsl.exe for queries, sessions and cleanup, so all of them reach <see cref="DistroName"/>.</summary>
    internal string[] ExecArguments(string user, IReadOnlyList<string> command) => BuildExecArguments(user, command, DistroName);

    // --exec bypasses the default shell, so every element reaches Linux as one argument.
    internal static string[] BuildExecArguments(string user, IReadOnlyList<string> command, string distroName = LauncherConstants.DistroName) =>
        ["-d", distroName, "-u", user, "--exec", .. command];

    internal static string[] BuildSupervisor(string pidFile, IReadOnlyList<string> cli) =>
    [
        "setsid", "--wait", "bash", "-c",
        "umask 077; printf '%s\\n' \"$$\" > \"$1\" || exit 1; shift; exec \"$@\"",
        "ddistro-assistant", pidFile, .. cli
    ];

    internal static AssistantCliResult Parse(CommandResult result)
    {
        var text = result.StandardOutput.Replace("\0", string.Empty).Trim();
        if (text.Length == 0 || text[0] != '{' || text.Length > MaxDocumentChars)
            return ClassifyMissingDocument(result);
        try
        {
            using var document = JsonDocument.Parse(text);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("schema_version", out var schema) || schema.ValueKind != JsonValueKind.Number ||
                !schema.TryGetInt32(out var schemaVersion) || schemaVersion != 1 ||
                !root.TryGetProperty("ok", out var okElement) || okElement.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            {
                return AssistantCliResult.Failure(AssistantCliResult.Protocol, "DwemerDistro returned an unsupported assistant response. Update DwemerDistro.");
            }

            var ok = okElement.GetBoolean();
            var code = string.Empty;
            var message = string.Empty;
            JsonElement details = default;
            if (root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object)
            {
                code = GetString(error, "code");
                message = GetString(error, "message");
                if (error.TryGetProperty("details", out var errorDetails) && errorDetails.ValueKind == JsonValueKind.Object)
                    details = errorDetails.Clone();
            }
            if (!ok && code.Length == 0)
                return AssistantCliResult.Failure(AssistantCliResult.Protocol, "DwemerDistro reported a failure without an error code.");
            // A failed process never counts as a success, and a clean exit never hides a failure.
            if (ok != (result.ExitCode == 0))
                return AssistantCliResult.Failure(AssistantCliResult.Protocol,
                    $"DwemerDistro's exit code {result.ExitCode} contradicts its assistant response. The outcome is unknown.");

            var warnings = root.TryGetProperty("warnings", out var w) && w.ValueKind == JsonValueKind.Array
                ? w.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String).Select(item => item.GetString()!).ToArray()
                : [];
            var data = root.TryGetProperty("data", out var d) ? d.Clone() : default;
            var operationId = GetString(root, "operation_id");
            return new AssistantCliResult(ok, GetString(root, "command"), operationId.Length > 0 ? operationId : null,
                data, code, message, warnings.Take(50).ToArray(), HasDocument: true)
            {
                ErrorDetails = details,
                WarningTotal = warnings.Length
            };
        }
        catch (JsonException)
        {
            return AssistantCliResult.Failure(AssistantCliResult.Protocol, "DwemerDistro returned an unreadable assistant response.");
        }
    }

    private static AssistantCliResult ClassifyMissingDocument(CommandResult result)
    {
        var combined = (result.StandardOutput + "\n" + result.StandardError).Replace("\0", string.Empty);
        if (combined.Contains("WSL_E_DISTRO_NOT_FOUND", StringComparison.OrdinalIgnoreCase) ||
            combined.Contains("There is no distribution", StringComparison.OrdinalIgnoreCase))
            return AssistantCliResult.Failure(AssistantCliResult.DistroMissing, "The DwemerDistro WSL distro is not installed.");
        if (result.ExitCode is 126 or 127 ||
            combined.Contains("No such file or directory", StringComparison.OrdinalIgnoreCase) ||
            combined.Contains("execvpe", StringComparison.OrdinalIgnoreCase))
            return AssistantCliResult.Failure(AssistantCliResult.CliMissing, "This DwemerDistro has no ddistro command. Run Update Distro.");
        return AssistantCliResult.Failure(AssistantCliResult.Protocol, $"DwemerDistro returned no assistant response (exit code {result.ExitCode}).");
    }

    internal static string GetString(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    internal const int MaxModelAssignments = 200;
    internal const int MaxModelFieldChars = 256;
    private static readonly string[] ModelAssignmentStrings = ["profile", "role", "connector", "model", "provider", "driver"];

    /// <summary>
    /// Re-projects Core's <c>models herika</c> data onto its exact allowlist: every field present with its type and bound, and
    /// nothing else copied. Returns null with a short reason when anything is missing, mistyped, oversized or inconsistent,
    /// so an unexpected document never reaches the model partly read or with fields the launcher did not ask for.
    /// </summary>
    internal static System.Text.Json.Nodes.JsonObject? ProjectModelsSnapshot(AssistantCliResult result, out string problem)
    {
        problem = string.Empty;
        var data = result.Data;
        if (!result.Ok || data.ValueKind != JsonValueKind.Object)
        {
            problem = "DwemerDistro did not return the model settings.";
            return null;
        }
        if (!TryField(data, "product", out string product) || product != "herika" ||
            !TryField(data, "scope", out string scope) || scope != "configured_profiles" ||
            !TryField(data, "available", out bool available) || !TryField(data, "truncated", out bool truncated) ||
            !TryField(data, "reason", out string reason) ||
            !TryField(data, "captured_at", out string capturedAt) || !IsUtcTimestamp(capturedAt) ||
            !data.TryGetProperty("assignments", out var list) || list.ValueKind != JsonValueKind.Array ||
            list.GetArrayLength() > MaxModelAssignments || !available && list.GetArrayLength() > 0)
        {
            problem = "DwemerDistro returned model settings in an unexpected shape.";
            return null;
        }

        var assignments = new System.Text.Json.Nodes.JsonArray();
        foreach (var entry in list.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object ||
                !TryField(entry, "profile_id", out int profileId) || !TryField(entry, "connector_id", out int connectorId))
            {
                problem = "DwemerDistro returned a model assignment in an unexpected shape.";
                return null;
            }
            var projected = new System.Text.Json.Nodes.JsonObject { ["profile_id"] = profileId };
            foreach (var name in ModelAssignmentStrings)
            {
                if (!TryField(entry, name, out string value))
                {
                    problem = "DwemerDistro returned a model assignment in an unexpected shape.";
                    return null;
                }
                projected[name] = value;
                if (name == "role") projected["connector_id"] = connectorId;
            }
            assignments.Add(projected);
        }

        return new System.Text.Json.Nodes.JsonObject
        {
            ["product"] = product,
            ["available"] = available,
            ["scope"] = scope,
            ["captured_at"] = capturedAt,
            ["assignments"] = assignments,
            ["truncated"] = truncated,
            ["reason"] = reason
        };
    }

    /// <summary>
    /// One note from <c>ddistro memory list|search|add|update|export</c>, re-read field by field: an id, a known kind and
    /// source, bounded text and Core's own provenance and timestamps. Anything else refuses the note.
    /// </summary>
    internal static AssistantMemoryNote? ProjectMemoryNote(JsonElement note)
    {
        if (note.ValueKind != JsonValueKind.Object || Prop(note, "id") is not { ValueKind: JsonValueKind.Number } id
            || !id.TryGetInt64(out var noteId) || noteId < 1) return null;
        var kind = GetString(note, "kind");
        var source = GetString(note, "source");
        var text = Prop(note, "text").ValueKind == JsonValueKind.String ? Prop(note, "text").GetString()! : string.Empty;
        if (!AssistantCliCommands.MemoryKinds.Contains(kind) || source is not ("user" or "assistant")
            || text.Length is 0 or > AssistantCliCommands.MaxMemoryText) return null;
        string? Optional(string name, Func<string, bool> valid) =>
            Prop(note, name) is { ValueKind: JsonValueKind.String } value && valid(value.GetString()!) ? value.GetString() : null;
        var captured = Optional("captured_at", IsUtcTimestamp);
        var updated = Optional("updated_at", IsUtcTimestamp);
        if (captured is null || updated is null) return null;
        return new AssistantMemoryNote(noteId, kind, text, source,
            Optional("task_id", AssistantCliCommands.IsValidTaskId), Optional("thread_id", AssistantCliCommands.IsValidMemoryThread),
            captured, updated, Optional("expires_at", IsUtcTimestamp), GetBool(note, "expired"));
    }

    /// <summary>The notes array under <paramref name="name"/>, bounded; null when it is missing or any note is malformed.</summary>
    internal static List<AssistantMemoryNote>? ProjectMemoryNotes(JsonElement data, string name, int limit)
    {
        if (Prop(data, name) is not { ValueKind: JsonValueKind.Array } notes || notes.GetArrayLength() > limit) return null;
        var projected = new List<AssistantMemoryNote>();
        foreach (var note in notes.EnumerateArray())
        {
            if (ProjectMemoryNote(note) is not { } valid) return null;
            projected.Add(valid);
        }
        return projected;
    }

    /// <summary>Core's memory settings, both switches present as booleans; null otherwise.</summary>
    internal static (bool Retrieval, bool AutoLearning)? ProjectMemorySettings(JsonElement data) =>
        Prop(data, "settings") is { ValueKind: JsonValueKind.Object } settings
        && Prop(settings, "retrieval").ValueKind is JsonValueKind.True or JsonValueKind.False
        && Prop(settings, "auto_learning").ValueKind is JsonValueKind.True or JsonValueKind.False
            ? (GetBool(settings, "retrieval"), GetBool(settings, "auto_learning")) : null;

    internal const int MaxInventoryEntries = 200;
    internal const int MaxInventoryMods = 100;
    internal const int MaxInventoryStringChars = 128;
    private static readonly string[] InventoryProducts = ["herika", "lorkhan", "stobe", "dialectic", "reign"];
    private static readonly string[] InventoryEntryKinds = ["dir", "file", "link", "other"];

    /// <summary>
    /// Re-projects Core's <c>inventory</c> data onto its exact allowlist, like <see cref="ProjectModelsSnapshot"/>: every
    /// field present with its type and bound, nothing else copied, and anything unexpected refuses the whole snapshot.
    /// </summary>
    internal static System.Text.Json.Nodes.JsonObject? ProjectInventory(AssistantCliResult result, out string problem)
    {
        problem = string.Empty;
        if (!result.Ok || result.Data.ValueKind != JsonValueKind.Object)
        {
            problem = "DwemerDistro did not return the inventory.";
            return null;
        }
        try
        {
            return ProjectInventoryData(result.Data);
        }
        catch (InvalidDataException)
        {
            problem = "DwemerDistro returned the inventory in an unexpected shape.";
            return null;
        }
    }

    private static System.Text.Json.Nodes.JsonObject ProjectInventoryData(JsonElement data)
    {
        var capturedAt = InventoryString(data, "captured_at");
        if (InventoryString(data, "scope") != "presence_only" || !IsUtcTimestamp(capturedAt)) throw BadInventory();

        var servers = InventoryObject(data, "servers");
        var serverItems = new System.Text.Json.Nodes.JsonArray();
        foreach (var server in InventoryArray(servers, "items", InventoryProducts.Length))
        {
            var present = Prop(server, "database_present");
            if (present.ValueKind is not (JsonValueKind.True or JsonValueKind.False or JsonValueKind.Null)) throw BadInventory();
            serverItems.Add(new System.Text.Json.Nodes.JsonObject
            {
                ["product"] = InventoryProduct(server, "product"),
                ["state"] = InventoryToken(server, "state"),
                ["repository_state"] = InventoryToken(server, "repository_state"),
                ["database_present"] = present.ValueKind == JsonValueKind.Null ? null : present.GetBoolean(),
                ["branch"] = InventoryString(server, "branch"),
                ["version"] = InventoryString(server, "version")
            });
        }

        var extensions = InventoryObject(data, "extensions");
        var extensionItems = new System.Text.Json.Nodes.JsonArray();
        foreach (var folder in InventoryArray(extensions, "items", InventoryProducts.Length))
        {
            if (InventoryString(folder, "root") != "ext") throw BadInventory();
            var entries = new System.Text.Json.Nodes.JsonArray();
            foreach (var entry in InventoryArray(folder, "entries", MaxInventoryEntries))
            {
                var name = InventoryString(entry, "name");
                var kind = InventoryString(entry, "kind");
                if (!InventoryNameRegex().IsMatch(name) || !InventoryEntryKinds.Contains(kind)) throw BadInventory();
                entries.Add(new System.Text.Json.Nodes.JsonObject { ["name"] = name, ["kind"] = kind });
            }
            extensionItems.Add(new System.Text.Json.Nodes.JsonObject
            {
                ["product"] = InventoryProduct(folder, "product"),
                ["root"] = "ext",
                ["state"] = InventoryToken(folder, "state"),
                ["total"] = InventoryCount(folder, "total"),
                ["truncated"] = InventoryBool(folder, "truncated"),
                ["skipped"] = InventoryCount(folder, "skipped"),
                ["entries"] = entries
            });
        }
        var noKnownRoot = new System.Text.Json.Nodes.JsonArray();
        foreach (var product in InventoryArray(extensions, "no_known_root", InventoryProducts.Length))
            noKnownRoot.Add(product.ValueKind == JsonValueKind.String && InventoryProducts.Contains(product.GetString()) ? product.GetString() : throw BadInventory());

        var components = InventoryObject(data, "components");
        if (InventoryString(components, "basis") != "startup_markers") throw BadInventory();
        var catalog = ProjectInventoryCatalog(data, out var catalogIds);
        var componentItems = new System.Text.Json.Nodes.JsonArray();
        foreach (var component in InventoryArray(components, "items", 32))
        {
            var row = new System.Text.Json.Nodes.JsonObject
            {
                ["id"] = InventoryToken(component, "id"),
                ["name"] = InventoryString(component, "name"),
                ["role"] = InventoryToken(component, "role"),
                ["state"] = InventoryToken(component, "state")
            };
            // Newer Cores key these startup rows by older ids; naming the catalog entry keeps them from being counted twice.
            if (catalogIds.TryGetValue((string)row["id"]!, out var catalogId)) row["catalog_id"] = catalogId;
            componentItems.Add(row);
        }

        var mods = InventoryObject(data, "custom_mods");
        var modItems = new System.Text.Json.Nodes.JsonArray();
        foreach (var mod in InventoryArray(mods, "items", MaxInventoryMods))
        {
            var id = InventoryString(mod, "id");
            var commit = InventoryString(mod, "commit");
            if (!InventoryModIdRegex().IsMatch(id) || commit.Length > 0 && !InventoryCommitRegex().IsMatch(commit)) throw BadInventory();
            modItems.Add(new System.Text.Json.Nodes.JsonObject
            {
                ["id"] = id,
                ["name"] = InventoryString(mod, "name"),
                ["state"] = InventoryToken(mod, "state"),
                ["branch"] = InventoryString(mod, "branch"),
                ["commit"] = commit
            });
        }

        // Core's complete flag is only trusted when the projected sections agree: anything unavailable,
        // truncated, skipped, redirected or unreadable makes the snapshot incomplete.
        var serverSection = InventorySection(servers, serverItems);
        var modSection = InventorySection(mods, modItems, withTruncated: true);
        var complete = InventoryBool(data, "complete")
            && (bool)serverSection["available"]! && (bool)modSection["available"]! && !(bool)modSection["truncated"]!
            && extensionItems.All(item => (string)item!["state"]! is "listed" or "absent" or "product_absent"
                                          && !(bool)item["truncated"]! && (int)item["skipped"]! == 0)
            && componentItems.All(item => (string)item!["state"]! is "present" or "absent")
            && (bool)catalog["available"]! && catalog["items"]!.AsArray().All(item => (string)item!["installed"]! != "unknown");

        return new System.Text.Json.Nodes.JsonObject
        {
            ["captured_at"] = capturedAt,
            ["scope"] = "presence_only",
            ["read_as"] = InventoryString(data, "read_as"),
            ["complete"] = complete,
            ["servers"] = serverSection,
            ["extensions"] = new System.Text.Json.Nodes.JsonObject { ["items"] = extensionItems, ["no_known_root"] = noKnownRoot },
            ["components"] = new System.Text.Json.Nodes.JsonObject { ["basis"] = "startup_markers", ["items"] = componentItems },
            ["catalog"] = catalog,
            ["custom_mods"] = modSection
        };
    }

    internal const int MaxCatalogEntries = ComponentCatalog.MaxEntries;
    private static readonly string[] CatalogInstalledStates = ["present", "absent", "unknown"];
    private static readonly string[] CatalogStartupStates = ["present", "absent", "unknown", "n/a"];
    private static readonly string[] CatalogChecks = ["presence", "port", "http"];

    /// <summary>
    /// Core's component catalog section: every catalog component's identity, installed and startup state, and the checks
    /// Core's code owns for it. Core's runtime catalog decides which components exist, so an entry this launcher does not
    /// embed still appears; drift from the embedded copy is reported, never hidden. An older Core without the section, and
    /// a section that is malformed or over its limits, become an unavailable catalog (never "absent" components) while
    /// the rest of the inventory is kept. ids maps every id and alias to its catalog id.
    /// </summary>
    private static System.Text.Json.Nodes.JsonObject ProjectInventoryCatalog(JsonElement data, out Dictionary<string, string> ids)
    {
        ids = new Dictionary<string, string>(StringComparer.Ordinal);
        var embedded = ComponentCatalog.Embedded;
        System.Text.Json.Nodes.JsonObject Unavailable(string reason) => new()
        {
            ["available"] = false, ["reason"] = reason, ["schema_version"] = null, ["catalog_version"] = "", ["sha256"] = "",
            ["basis"] = "installed_markers",
            ["launcher_catalog"] = new System.Text.Json.Nodes.JsonObject { ["catalog_version"] = embedded.CatalogVersion, ["sha256"] = embedded.Sha256 },
            ["items"] = new System.Text.Json.Nodes.JsonArray()
        };

        if (!data.TryGetProperty("catalog", out var section)) return Unavailable("core_older");
        try
        {
            if (section.ValueKind != JsonValueKind.Object || InventoryString(section, "basis") != "installed_markers") throw BadInventory();
            var available = InventoryBool(section, "available");
            var reason = InventoryString(section, "reason");
            if (!available)
            {
                if (reason.Length == 0 || !InventoryTokenRegex().IsMatch(reason) || InventoryArray(section, "items", 0).Any()) throw BadInventory();
                return Unavailable(reason);
            }

            var schema = Prop(section, "schema_version");
            if (schema.ValueKind != JsonValueKind.Number || schema.GetRawText() != ComponentCatalog.SchemaVersion.ToString())
                return Unavailable("schema_unsupported");
            var version = InventoryString(section, "catalog_version");
            var sha256 = InventoryString(section, "sha256");
            if (reason.Length > 0 || !CatalogVersionRegex().IsMatch(version) || !Sha256Regex().IsMatch(sha256)) throw BadInventory();
            if (Prop(section, "items") is { ValueKind: JsonValueKind.Array } raw && raw.GetArrayLength() > MaxCatalogEntries)
                return Unavailable("over_limit");

            var items = new System.Text.Json.Nodes.JsonArray();
            var newIds = new System.Text.Json.Nodes.JsonArray();
            foreach (var item in InventoryArray(section, "items", MaxCatalogEntries))
            {
                var id = InventoryString(item, "id");
                var aliases = new System.Text.Json.Nodes.JsonArray();
                foreach (var alias in InventoryArray(item, "aliases", ComponentCatalog.MaxAliases).Select(alias =>
                             alias.ValueKind == JsonValueKind.String ? alias.GetString()! : throw BadInventory()).Prepend(id))
                {
                    if (!ComponentCatalog.IsId(alias) || !ids.TryAdd(alias, id)) throw BadInventory();
                    if (alias != id) aliases.Add(alias);
                }

                var name = InventoryString(item, "name");
                var installed = InventoryString(item, "installed");
                var installedReason = InventoryString(item, "installed_reason");
                var startup = InventoryString(item, "startup");
                var startupReason = InventoryString(item, "startup_reason");
                var checks = InventoryArray(item, "checks", CatalogChecks.Length)
                    .Select(check => check.ValueKind == JsonValueKind.String ? check.GetString()! : throw BadInventory()).ToArray();
                if (!ComponentCatalog.IsLabel(name) || !CatalogInstalledStates.Contains(installed) || !CatalogStartupStates.Contains(startup)
                    || !ReasonMatches(installed, installedReason) || !ReasonMatches(startup, startupReason)
                    || checks.FirstOrDefault() != "presence" || checks.Distinct().Count() != checks.Length || !checks.All(CatalogChecks.Contains))
                {
                    throw BadInventory();
                }

                if (embedded.Find(id) is null) newIds.Add(id);
                items.Add(new System.Text.Json.Nodes.JsonObject
                {
                    ["id"] = id,
                    ["aliases"] = aliases,
                    ["name"] = name,
                    ["role"] = InventoryToken(item, "role"),
                    ["listed"] = InventoryBool(item, "listed"),
                    ["installed"] = installed,
                    ["installed_reason"] = installedReason,
                    ["startup"] = startup,
                    ["startup_reason"] = startupReason,
                    ["checks"] = new System.Text.Json.Nodes.JsonArray(checks.Select(check => (System.Text.Json.Nodes.JsonNode?)check).ToArray())
                });
            }

            if (items.Count == 0) throw BadInventory();
            return new System.Text.Json.Nodes.JsonObject
            {
                ["available"] = true, ["reason"] = "", ["schema_version"] = ComponentCatalog.SchemaVersion, ["catalog_version"] = version,
                ["sha256"] = sha256, ["basis"] = "installed_markers",
                // Core's runtime catalog is the authority; the launcher's embedded copy is only compared with it.
                ["launcher_catalog"] = new System.Text.Json.Nodes.JsonObject
                {
                    ["catalog_version"] = embedded.CatalogVersion, ["sha256"] = embedded.Sha256,
                    ["same"] = sha256 == embedded.Sha256, ["new_ids"] = newIds
                },
                ["items"] = items
            };
        }
        catch (InvalidDataException)
        {
            ids.Clear();
            return Unavailable("launcher_rejected");
        }
    }

    // Unknown carries a reason token; every other state has none.
    private static bool ReasonMatches(string state, string reason) =>
        state == "unknown" ? reason.Length > 0 && InventoryTokenRegex().IsMatch(reason) : reason.Length == 0;

    [GeneratedRegex(@"^[0-9A-Za-z][0-9A-Za-z._-]{0,31}\z")]
    private static partial Regex CatalogVersionRegex();

    [GeneratedRegex(@"^[0-9a-f]{64}\z")]
    private static partial Regex Sha256Regex();

    /// <summary>An available flag, a reason token (empty when available) and its items; unavailable sections hold none.</summary>
    private static System.Text.Json.Nodes.JsonObject InventorySection(JsonElement section, System.Text.Json.Nodes.JsonArray items, bool withTruncated = false)
    {
        var available = InventoryBool(section, "available");
        var reason = InventoryString(section, "reason");
        if (available ? reason.Length > 0 : items.Count > 0 || reason.Length == 0 || !InventoryTokenRegex().IsMatch(reason)) throw BadInventory();
        var projected = new System.Text.Json.Nodes.JsonObject { ["available"] = available, ["reason"] = reason };
        if (withTruncated) projected["truncated"] = InventoryBool(section, "truncated");
        projected["items"] = items;
        return projected;
    }

    private static InvalidDataException BadInventory() => new("inventory");

    private static JsonElement Prop(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) ? value : default;

    private static JsonElement InventoryObject(JsonElement element, string name) =>
        Prop(element, name) is { ValueKind: JsonValueKind.Object } value ? value : throw BadInventory();

    private static IEnumerable<JsonElement> InventoryArray(JsonElement element, string name, int limit) =>
        Prop(element, name) is { ValueKind: JsonValueKind.Array } value && value.GetArrayLength() <= limit
            ? value.EnumerateArray().ToArray() : throw BadInventory();

    // Prop is undefined for a non-object element, so an array item of the wrong kind is refused, never thrown on.
    private static string InventoryString(JsonElement element, string name) =>
        Prop(element, name) is { ValueKind: JsonValueKind.String } value && value.GetString() is { Length: <= MaxInventoryStringChars } text
            && !text.Any(char.IsControl) ? text : throw BadInventory();

    private static bool InventoryBool(JsonElement element, string name) => Prop(element, name).ValueKind switch
    {
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => throw BadInventory()
    };

    private static int InventoryCount(JsonElement element, string name) =>
        Prop(element, name) is { ValueKind: JsonValueKind.Number } value && value.TryGetInt32(out var count) && count >= 0 ? count : throw BadInventory();

    private static string InventoryToken(JsonElement element, string name) =>
        InventoryString(element, name) is var value && value.Length > 0 && InventoryTokenRegex().IsMatch(value) ? value : throw BadInventory();

    private static string InventoryProduct(JsonElement element, string name) =>
        InventoryString(element, name) is var value && InventoryProducts.Contains(value) ? value : throw BadInventory();

    // Core's state tokens and component ids; an empty reason is checked separately.
    [GeneratedRegex(@"^([a-z][a-z0-9_-]{0,31})?\z")]
    private static partial Regex InventoryTokenRegex();

    // Same as Core's INVENTORY_NAME: a plain folder or file name, never a path.
    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9 ._+()@,=~-]{0,127}\z")]
    private static partial Regex InventoryNameRegex();

    [GeneratedRegex(@"^[a-z][a-z0-9-]{1,30}[a-z0-9]\z")]
    private static partial Regex InventoryModIdRegex();

    [GeneratedRegex(@"^[0-9a-f]{40}\z")]
    private static partial Regex InventoryCommitRegex();

    private static bool TryField(JsonElement element, string name, out string value)
    {
        value = element.TryGetProperty(name, out var field) && field.ValueKind == JsonValueKind.String ? field.GetString() ?? string.Empty : string.Empty;
        return field.ValueKind == JsonValueKind.String && value.Length <= MaxModelFieldChars && !value.Any(char.IsControl);
    }

    private static bool TryField(JsonElement element, string name, out bool value)
    {
        value = element.TryGetProperty(name, out var field) && field.ValueKind == JsonValueKind.True;
        return field.ValueKind is JsonValueKind.True or JsonValueKind.False;
    }

    private static bool TryField(JsonElement element, string name, out int value)
    {
        value = 0;
        return element.TryGetProperty(name, out var field) && field.ValueKind == JsonValueKind.Number && field.TryGetInt32(out value);
    }

    /// <summary>An ISO 8601 instant that states UTC itself (Z or +00:00), not a local or unzoned time.</summary>
    private static bool IsUtcTimestamp(string value) =>
        value.Length is >= 20 and <= 40 && (value.EndsWith('Z') || value.EndsWith("+00:00", StringComparison.Ordinal)) &&
        DateTimeOffset.TryParse(value, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind, out var parsed) &&
        parsed.Offset == TimeSpan.Zero && value[4] == '-' && value[10] == 'T';

    internal static bool GetBool(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    /// <summary>File lists may be plain paths or objects with a path, action and outcome; both read as one line each.</summary>
    internal static string[] GetFiles(JsonElement element, string name, int limit = 200)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var files) || files.ValueKind != JsonValueKind.Array)
            return [];
        return files.EnumerateArray().Take(limit).Select(file => file.ValueKind switch
        {
            JsonValueKind.String => file.GetString() ?? string.Empty,
            JsonValueKind.Object => DescribeFile(file),
            _ => string.Empty
        }).Where(line => line.Length > 0).ToArray();
    }

    private static string DescribeFile(JsonElement file)
    {
        var path = GetString(file, "path");
        var action = GetString(file, "action");
        if (action.Length == 0) action = GetString(file, "status");
        // Apply and rollback receipts add each file's own outcome: written, failed, pending or unknown.
        var outcome = GetString(file, "state");
        var label = string.Join(", ", new[] { action, outcome }.Where(part => part.Length > 0));
        return label.Length > 0 ? $"{path} ({label})" : path;
    }

    /// <summary>Per-file outcome counts from a receipt or journal file list. Files without an outcome are not counted.</summary>
    internal static Dictionary<string, int> CountFileStates(JsonElement element)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty("files", out var files) || files.ValueKind != JsonValueKind.Array)
            return counts;
        foreach (var state in files.EnumerateArray().Select(file => GetString(file, "state")).Where(state => state.Length > 0))
            counts[state] = counts.GetValueOrDefault(state) + 1;
        return counts;
    }

    private sealed class SupervisedTransport(Process process, ProcessRunner runner, Func<Task> stopGroup) : IAssistantTransport
    {
        private const int MaxStderrTail = 2048;
        private readonly BoundedLineReader _stdout = new(process.StandardOutput);
        private readonly StringBuilder _stderr = new();
        private readonly SemaphoreSlim _writeLock = new(1, 1);
        private int _disposed;

        public string DiagnosticTail
        {
            get { lock (_stderr) return _stderr.ToString(); }
        }

        public Task<string?> ReadLineAsync(CancellationToken cancellationToken) => _stdout.ReadLineAsync(cancellationToken);

        public async Task WriteLineAsync(string line, CancellationToken cancellationToken)
        {
            await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await process.StandardInput.WriteAsync((line + "\n").AsMemory(), cancellationToken).ConfigureAwait(false);
                await process.StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _writeLock.Release();
            }
        }

        internal void StartStderrDrain() => _ = Task.Run(async () =>
        {
            var buffer = new char[1024];
            try
            {
                int read;
                while ((read = await process.StandardError.ReadAsync(buffer.AsMemory()).ConfigureAwait(false)) > 0)
                {
                    lock (_stderr)
                    {
                        _stderr.Append(buffer, 0, read);
                        if (_stderr.Length > MaxStderrTail) _stderr.Remove(0, _stderr.Length - MaxStderrTail);
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
            {
            }
        });

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
            try { process.StandardInput.Close(); } catch (Exception ex) when (ex is IOException or InvalidOperationException) { }
            using (var exitWait = new CancellationTokenSource(TimeSpan.FromSeconds(3)))
            {
                try { await process.WaitForExitAsync(exitWait.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { }
            }
            await stopGroup().ConfigureAwait(false);
            runner.TryKill(process);
            process.Dispose();
            LauncherLogService.Operation("Assistant session END");
        }
    }
}

/// <summary>Reads newline-delimited messages without letting one unterminated line grow without bound.</summary>
public sealed class BoundedLineReader(TextReader reader, int maxLineChars = 4 * 1024 * 1024)
{
    private readonly char[] _buffer = new char[16 * 1024];
    private int _start;
    private int _end;

    public async Task<string?> ReadLineAsync(CancellationToken cancellationToken)
    {
        var line = new StringBuilder();
        while (true)
        {
            if (_start == _end)
            {
                _start = 0;
                _end = await reader.ReadAsync(_buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
                if (_end == 0) return line.Length > 0 ? line.ToString() : null;
            }

            var newline = Array.IndexOf(_buffer, '\n', _start, _end - _start);
            var stop = newline < 0 ? _end : newline;
            if (line.Length + (stop - _start) > maxLineChars)
                throw new InvalidDataException("The assistant sent a message larger than the launcher accepts.");
            line.Append(_buffer, _start, stop - _start);
            _start = newline < 0 ? _end : newline + 1;
            if (newline >= 0) return line.ToString().TrimEnd('\r');
        }
    }
}
