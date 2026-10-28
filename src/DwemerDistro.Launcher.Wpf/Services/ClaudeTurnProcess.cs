using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace DwemerDistro.Launcher.Wpf.Services;

/// <summary>
/// One Claude turn's inputs. <see cref="Input"/> is the same Codex-shaped user input array a Codex turn sends
/// (<c>{type:"text",text}</c> and <c>{type:"image",url:"data:…"}</c> items); it goes to Claude on stdin, never argv.
/// </summary>
public sealed record ClaudeTurnRequest(
    string ExecutablePath,
    string SessionId,
    bool Resume,
    string Model,
    string? Effort,
    string SystemPrompt,
    JsonArray Input);

/// <summary>
/// Runs one Claude Code turn: <c>claude.exe -p</c> with stream-json on stdin and stdout, no built-in tools, the user's
/// settings, hooks, plugins and MCP servers ignored, and only this turn's <see cref="AssistantMcpRelay"/> as a tool source.
/// The CLI signs in with the user's own Claude account; the launcher never reads or passes a token.
/// </summary>
/// <remarks>
/// The process tree lives in a kill-on-close job. <see cref="Stop"/> (Stop, a broker <c>turn/completed</c>, a refused
/// init or a start-up failure) revokes the relay and ends the tree; it is safe at any time after <see cref="Start"/>,
/// including before the CLI has printed anything. This object owns the relay and disposes it, and removes the turn's
/// private config folder, when the turn ends.
/// </remarks>
public sealed class ClaudeTurnProcess : IAsyncDisposable
{
    /// <summary>MCP connect wait (30 s) plus the CLI's own start-up.</summary>
    internal static readonly TimeSpan InitTimeout = TimeSpan.FromSeconds(60);
    internal const int MaxCommandLineChars = 30000;
    public const string InitTimeoutMessage = "Claude Code did not connect to DwemerDistro in time.";
    public const string JobUnavailableMessage = "Claude Code could not be started safely on this PC.";
    /// <summary>
    /// No hooks, and Claude Code's own built-in plugins off: they load even with <c>--restricted</c> and
    /// <c>--strict-mcp-config</c>, and the init gate refuses any plugin. A built-in this list does not know still refuses.
    /// </summary>
    internal const string TurnSettings =
        "{\"disableAllHooks\":true,\"enabledPlugins\":{\"agents-md@builtin\":false,\"telemetry@builtin\":false}}";
    internal static readonly string[] Efforts = ["low", "medium", "high", "xhigh", "max"];
    private static readonly Regex ModelPattern = new(@"^[A-Za-z0-9._\-\[\]]{1,64}\z");
    private static readonly Regex ImageMediaType = new(@"^image/(png|jpeg|gif|webp)\z");

    /// <summary>
    /// Inherited variables that would sign Claude in some other way than the user's own account (an API key, a token,
    /// another endpoint or provider), change how it loads, or inject code into it. Matched case-insensitively; every
    /// <c>ANTHROPIC_*</c> variable is removed too.
    /// </summary>
    internal static readonly string[] RemovedEnvironment =
    [
        "CLAUDE_CODE_USE_BEDROCK", "CLAUDE_CODE_USE_VERTEX", "CLAUDE_CODE_USE_FOUNDRY", "CLAUDE_CODE_SKIP_BEDROCK_AUTH",
        "CLAUDE_CODE_SKIP_VERTEX_AUTH", "CLAUDE_CODE_SKIP_FOUNDRY_AUTH", "AWS_BEARER_TOKEN_BEDROCK", "CLAUDE_CODE_API_KEY_HELPER_TTL_MS",
        "CLAUDE_CODE_SIMPLE", "CLAUDE_CODE_SAFE_MODE", "CLAUDE_CODE_CLIENT_DATA_URL", "NODE_OPTIONS"
    ];

    internal static readonly KeyValuePair<string, string>[] AddedEnvironment =
    [
        new("MCP_TOOL_TIMEOUT", ((int)AssistantMcpRelay.ToolCallTimeout.TotalMilliseconds).ToString()),
        new("MCP_TIMEOUT", "30000"),
        new("ENABLE_CLAUDEAI_MCP_SERVERS", "false"),
        new("DISABLE_AUTOUPDATER", "1")
    ];

    private readonly AssistantMcpRelay _relay;
    private readonly ProcessRunner _runner;
    private readonly ClaudeStreamTranslator _translator;
    private readonly Action<string, JsonElement> _notify;
    private readonly Action<string>? _sessionStarted;
    private readonly TaskCompletionSource<ClaudeTurnResult> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly object _gate = new();
    private Process? _process;
    private WindowsJobObject? _job;
    private string? _turnDirectory;
    private bool _stopped;
    private bool _initTimedOut;
    private int? _exitCode;
    private string _userMessage = string.Empty;

    private ClaudeTurnProcess(AssistantMcpRelay relay, ProcessRunner runner, string threadId, string turnId, Action<string, JsonElement> notify,
        Action<string>? sessionStarted, string? resumeSessionId)
    {
        _relay = relay;
        _runner = runner;
        _notify = notify;
        _sessionStarted = sessionStarted;
        _translator = new ClaudeStreamTranslator(threadId, turnId, relay.ClaudeToolNames, resumeSessionId);
        relay.Notification += Forward;
    }

    /// <summary>Completes once the process has exited and the relay and private files are gone. Never faults.</summary>
    public Task<ClaudeTurnResult> Completion => _completion.Task;

    /// <summary>
    /// Starts the turn. On any failure before the process runs, the relay is disposed and the exception propagates.
    /// The broker's <c>ddistro/external/turn/start</c> must already have answered; the caller ends that turn when
    /// <see cref="Completion"/> finishes. <paramref name="notify"/> gets Codex-shaped item notifications off the UI thread:
    /// agentMessage text from the stream and dynamicToolCall items from the relay. It is a parameter, not an event, so
    /// nothing printed before a subscription could be missed. <paramref name="sessionStarted"/> gets the session id once,
    /// off the UI thread, as soon as an allowed init reports the requested session: Claude Code has created it from then on.
    /// </summary>
    public static ClaudeTurnProcess Start(ClaudeTurnRequest request, AssistantMcpRelay relay, string threadId, string turnId,
        Action<string, JsonElement> notify, ProcessRunner runner, string? localAppDataDirectory = null, Action<string>? sessionStarted = null)
    {
        var turn = new ClaudeTurnProcess(relay, runner, threadId, turnId, notify, sessionStarted, request.Resume ? request.SessionId : null);
        try
        {
            turn.Launch(request, localAppDataDirectory);
        }
        catch
        {
            turn.Cleanup();
            _ = relay.DisposeAsync().AsTask();
            throw;
        }
        _ = turn.RunAsync(request);
        return turn;
    }

    /// <summary>
    /// Revokes the relay and ends the whole process tree. Idempotent; the result becomes Stopped unless already refused or
    /// completed. Because the tree is killed, a stdin write blocked on a CLI that is not reading fails at once.
    /// </summary>
    public void Stop()
    {
        lock (_gate) _stopped = true;
        _relay.Revoke();
        _job?.Terminate();
        _runner.TryKill(_process);
    }

    public async ValueTask DisposeAsync()
    {
        Stop();
        await Completion.ConfigureAwait(false);
    }

    private void Launch(ClaudeTurnRequest request, string? localAppDataDirectory)
    {
        _userMessage = BuildUserMessage(request.Input);
        var workDirectory = EnsureWorkDirectory(localAppDataDirectory);
        _turnDirectory = CreateTurnDirectory(localAppDataDirectory);
        var mcpConfig = Path.Combine(_turnDirectory, "mcp.json");
        using (var file = new FileStream(mcpConfig, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
        {
            file.Write(Encoding.UTF8.GetBytes(_relay.BuildClientConfig()));
        }
        var arguments = BuildArguments(request, mcpConfig, _relay.ClaudeToolNames);
        var startInfo = CreateStartInfo(request.ExecutablePath, arguments, workDirectory, redirectInput: true);
        var process = new Process { StartInfo = startInfo };
        // The kill-on-close job is what guarantees the tree dies with the launcher, so no job means no turn.
        _job = WindowsJobObject.TryCreate() ?? throw new IOException(JobUnavailableMessage);
        process.Start();
        _process = process;
        if (!_job.TryAssign(process))
        {
            // Nothing has been sent yet. Kill just this tree (never anything machine-wide) and fail closed.
            _runner.TryKill(process);
            throw new IOException(JobUnavailableMessage);
        }
        bool stopped;
        lock (_gate) stopped = _stopped;
        if (stopped) Stop();
    }

    private async Task RunAsync(ClaudeTurnRequest request)
    {
        var process = _process!;
        // The start-up watchdog runs before anything can block: a CLI that never reads stdin or never prints is stopped,
        // which kills the tree and so also fails a stdin write stuck on a full pipe.
        using var initTimer = new CancellationTokenSource(InitTimeout);
        using var registration = initTimer.Token.Register(() =>
        {
            if (_translator.Init != ClaudeInitState.Pending) return;
            _initTimedOut = true;
            Stop();
        });
        Task stdin = Task.CompletedTask;
        var sessionReported = false;
        // A start-up failure (an error result before init) ends the turn as itself: the CLI is ended, but not as a Stop.
        var failedAtStartup = false;
        try
        {
            var stderr = DrainAsync(process.StandardError);
            // stdin is written alongside the stdout read, so a large message and a chatty CLI cannot deadlock each other.
            stdin = WriteInputAsync(process, _userMessage);

            var reader = new BoundedLineReader(process.StandardOutput);
            while (true)
            {
                string? line;
                try
                {
                    line = await reader.ReadLineAsync(CancellationToken.None).ConfigureAwait(false);
                }
                catch (InvalidDataException)
                {
                    Stop();
                    break;
                }
                if (line is null) break;
                if (line.Length == 0) continue;
                foreach (var (method, parameters) in _translator.Translate(line)) _notify(method, parameters);
                if (!sessionReported && _translator.Init == ClaudeInitState.Allowed && _translator.SessionId == request.SessionId)
                {
                    sessionReported = true;
                    _sessionStarted?.Invoke(request.SessionId);
                }
                if (_translator.Init == ClaudeInitState.Refused)
                {
                    Stop();
                    break;
                }
                if (_translator.Init == ClaudeInitState.Pending && _translator.Result is not null)
                {
                    lock (_gate) failedAtStartup = !_stopped;
                    Stop();
                    break;
                }
            }
            await process.WaitForExitAsync().ConfigureAwait(false);
            _exitCode = process.ExitCode;
            await stderr.ConfigureAwait(false);
            await stdin.ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            Stop();
        }
        finally
        {
            await ObserveAsync(stdin).ConfigureAwait(false);
            var result = _translator.Finish();
            bool stopped;
            lock (_gate) stopped = _stopped;
            if (_initTimedOut && !failedAtStartup)
                result = new ClaudeTurnResult(ClaudeTurnOutcome.Failed, result.SessionId, InitTimeoutMessage);
            else if (stopped && !failedAtStartup && result.Outcome is not (ClaudeTurnOutcome.Refused or ClaudeTurnOutcome.Completed))
                result = new ClaudeTurnResult(ClaudeTurnOutcome.Stopped, result.SessionId, "Stopped.");
            else if (result.Outcome == ClaudeTurnOutcome.Completed && _exitCode != 0)
                // A streamed success is only a success if the CLI also exited cleanly (an unknown exit counts as failed).
                result = new ClaudeTurnResult(ClaudeTurnOutcome.Failed, result.SessionId, ClaudeStreamTranslator.FailedMessage);
            result = result with { SessionReported = sessionReported };
            if (result.Outcome is ClaudeTurnOutcome.Refused)
                LauncherLogService.Operation($"Claude turn ended: Refused ({_translator.RefusalReason})");
            else if (failedAtStartup)
                LauncherLogService.Operation($"Claude turn ended: {result.Outcome} (error before init: {_translator.StartupError})");
            else if (result.Outcome is not ClaudeTurnOutcome.Completed)
                LauncherLogService.Operation("Claude turn ended: " + result.Outcome);
            _relay.Notification -= Forward;
            await _relay.DisposeAsync().ConfigureAwait(false);
            Cleanup();
            _completion.TrySetResult(result);
        }
    }

    private void Forward(string method, JsonElement parameters) => _notify(method, parameters);

    private void Cleanup()
    {
        _job?.Dispose();
        _process?.Dispose();
        if (_turnDirectory is null) return;
        try
        {
            if (Directory.Exists(_turnDirectory) && !ClaudeCodeLocator.HasReparsePoint(_turnDirectory)) Directory.Delete(_turnDirectory, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LauncherLogService.Operation("Claude turn config folder could not be removed.");
        }
    }

    // --- Command line, environment and folders ------------------------------------------------

    /// <summary>
    /// The full argument vector. Values that could start with a dash use <c>--flag=value</c>. The bearer is only in the
    /// <paramref name="mcpConfigPath"/> file. <c>--bare</c> is never used, because it turns off the user's own sign-in.
    /// </summary>
    internal static List<string> BuildArguments(ClaudeTurnRequest request, string mcpConfigPath, IReadOnlySet<string> allowedTools)
    {
        if (!Guid.TryParseExact(request.SessionId, "D", out _)) throw new ArgumentException("The Claude session id must be a UUID.");
        if (!ModelPattern.IsMatch(request.Model)) throw new ArgumentException("The Claude model name is not valid.");
        if (request.Effort is not null && !Efforts.Contains(request.Effort)) throw new ArgumentException("The Claude effort level is not valid.");
        if (allowedTools.Count == 0 || allowedTools.Any(tool => !tool.StartsWith(AssistantMcpRelay.ToolPrefix, StringComparison.Ordinal)))
            throw new ArgumentException("Only DwemerDistro tools may be allowed.");

        var arguments = new List<string>
        {
            "--print",
            "--input-format", "stream-json",
            "--output-format", "stream-json",
            "--verbose",
            "--include-partial-messages",
            // No built-in tool at all: no shell, files, web or code. Restricted also ignores user, project and local settings.
            "--tools", "",
            "--restricted",
            "--strict-mcp-config",
            "--mcp-config", mcpConfigPath,
            "--settings", TurnSettings,
            "--allowedTools", string.Join(",", allowedTools.Order(StringComparer.Ordinal)),
            "--permission-mode", "dontAsk",
            "--permission-prompts", "none",
            "--disable-slash-commands",
            "--system-prompt=" + request.SystemPrompt,
            "--model=" + request.Model
        };
        if (request.Effort is not null) arguments.Add("--effort=" + request.Effort);
        arguments.Add((request.Resume ? "--resume=" : "--session-id=") + request.SessionId);

        if (arguments.Sum(argument => argument.Length + 3) + request.ExecutablePath.Length > MaxCommandLineChars)
            throw new ArgumentException("The assistant instructions are too long for Claude Code's command line.");
        return arguments;
    }

    /// <summary>A hidden, no-shell start of <paramref name="path"/> with the sanitized environment and UTF-8 pipes.</summary>
    internal static ProcessStartInfo CreateStartInfo(string path, IEnumerable<string> arguments, string workDirectory, bool redirectInput)
    {
        var startInfo = new ProcessStartInfo(path)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = workDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = redirectInput,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false)
        };
        if (redirectInput) startInfo.StandardInputEncoding = new UTF8Encoding(false);
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
        SanitizeEnvironment(startInfo.Environment);
        return startInfo;
    }

    internal static void SanitizeEnvironment(IDictionary<string, string?> environment)
    {
        foreach (var key in environment.Keys.ToList())
        {
            if (key.StartsWith("ANTHROPIC_", StringComparison.OrdinalIgnoreCase)
                || RemovedEnvironment.Contains(key, StringComparer.OrdinalIgnoreCase))
                environment.Remove(key);
        }
        foreach (var (key, value) in AddedEnvironment) environment[key] = value;
    }

    /// <summary>
    /// <c>%LOCALAPPDATA%\DwemerDistro\assistant\claude-work</c>: Claude's working folder, which keeps its sessions apart
    /// from the user's own projects. It must be a plain, empty folder, so no CLAUDE.md or project config can be planted there.
    /// </summary>
    internal static string EnsureWorkDirectory(string? localAppDataDirectory)
    {
        var path = Path.Combine(AssistantRoot(localAppDataDirectory), "claude-work");
        Directory.CreateDirectory(path);
        if (ClaudeCodeLocator.HasReparsePoint(path)) throw new IOException("The Claude working folder is not a plain folder.");
        if (Directory.EnumerateFileSystemEntries(path).Any()) throw new IOException("The Claude working folder is not empty.");
        return path;
    }

    /// <summary>A new per-turn folder under <c>assistant\claude-turn</c> for the private MCP config. Removed when the turn ends.</summary>
    private static string CreateTurnDirectory(string? localAppDataDirectory)
    {
        var parent = Path.Combine(AssistantRoot(localAppDataDirectory), "claude-turn");
        Directory.CreateDirectory(parent);
        var path = Path.Combine(parent, Guid.NewGuid().ToString("N"));
        if (Directory.Exists(path)) throw new IOException("The Claude turn folder already exists.");
        Directory.CreateDirectory(path);
        if (ClaudeCodeLocator.HasReparsePoint(path)) throw new IOException("The Claude turn folder is not a plain folder.");
        return path;
    }

    private static string AssistantRoot(string? localAppDataDirectory) =>
        Path.Combine(localAppDataDirectory ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DwemerDistro", "assistant");

    // --- stdin ------------------------------------------------------------------------------------

    /// <summary>
    /// The Codex-shaped user input as one stream-json user message: text items as text blocks, data-URL images as base64
    /// image blocks. Anything else fails closed.
    /// </summary>
    internal static string BuildUserMessage(JsonArray input)
    {
        var content = new JsonArray();
        foreach (var item in input)
        {
            var type = item?["type"]?.GetValueKind() == JsonValueKind.String ? item["type"]!.GetValue<string>() : string.Empty;
            if (type == "text" && item!["text"]?.GetValueKind() == JsonValueKind.String)
            {
                content.Add(new JsonObject { ["type"] = "text", ["text"] = item["text"]!.GetValue<string>() });
            }
            else if (type == "image" && item!["url"]?.GetValueKind() == JsonValueKind.String
                     && TryParseDataUrl(item["url"]!.GetValue<string>(), out var mediaType, out var data))
            {
                content.Add(new JsonObject
                {
                    ["type"] = "image",
                    ["source"] = new JsonObject { ["type"] = "base64", ["media_type"] = mediaType, ["data"] = data }
                });
            }
            else
            {
                throw new ArgumentException("The message has an input Claude cannot take.");
            }
        }
        if (content.Count == 0) throw new ArgumentException("The message is empty.");
        return new JsonObject
        {
            ["type"] = "user",
            ["session_id"] = string.Empty,
            ["parent_tool_use_id"] = null,
            ["message"] = new JsonObject { ["role"] = "user", ["content"] = content }
        }.ToJsonString();
    }

    /// <summary>A <c>data:image/(png|jpeg|gif|webp);base64,…</c> URL split into its media type and base64 payload.</summary>
    internal static bool TryParseDataUrl(string url, out string mediaType, out string data)
    {
        mediaType = data = string.Empty;
        const string prefix = "data:";
        const string marker = ";base64,";
        if (!url.StartsWith(prefix, StringComparison.Ordinal)) return false;
        var split = url.IndexOf(marker, StringComparison.Ordinal);
        if (split < 0) return false;
        var type = url[prefix.Length..split].ToLowerInvariant();
        var payload = url[(split + marker.Length)..];
        if (!ImageMediaType.IsMatch(type) || payload.Length == 0 || payload.Length % 4 != 0
            || !payload.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '+' or '/' or '='))
            return false;
        mediaType = type;
        data = payload;
        return true;
    }

    private static async Task ObserveAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException or ArgumentException)
        {
        }
    }

    private static async Task WriteInputAsync(Process process, string message)
    {
        await Task.Yield();
        try
        {
            await process.StandardInput.WriteAsync(message + "\n").ConfigureAwait(false);
            await process.StandardInput.FlushAsync().ConfigureAwait(false);
        }
        finally
        {
            // One message per turn: closing stdin tells the CLI to finish after this reply.
            process.StandardInput.Close();
        }
    }

    /// <summary>Stderr is read so the pipe never fills, and dropped: it can echo account or environment details.</summary>
    private static async Task DrainAsync(StreamReader reader)
    {
        var buffer = new char[4096];
        try
        {
            while (await reader.ReadAsync(buffer.AsMemory()).ConfigureAwait(false) > 0)
            {
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
        }
    }
}
