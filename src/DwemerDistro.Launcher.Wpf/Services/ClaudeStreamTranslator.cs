using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace DwemerDistro.Launcher.Wpf.Services;

public enum ClaudeInitState
{
    Pending,
    Allowed,
    Refused
}

public enum ClaudeTurnOutcome
{
    Completed,
    Failed,
    SignInNeeded,
    /// <summary>Claude started with tools, MCP servers or plugins DwemerDistro did not allow, or without the ddistro server.</summary>
    Refused,
    Stopped,
    /// <summary>
    /// <c>--resume</c> named a session Claude Code does not have: its only output was an error result before init, with no
    /// model turn, for exactly the requested session. Nothing ran, so the message was not answered.
    /// </summary>
    SessionMissing
}

/// <summary>
/// How a Claude turn ended. <see cref="Message"/> is fixed launcher text, never CLI or model output.
/// <see cref="SessionReported"/> is true once an allowed init reported the requested session, the only proof that Claude
/// Code created it.
/// </summary>
public sealed record ClaudeTurnResult(ClaudeTurnOutcome Outcome, string SessionId, string Message, bool SessionReported = false);

/// <summary>
/// Turns Claude Code <c>stream-json</c> lines into the Codex-shaped notifications <c>AssistantViewModel.OnNotification</c>
/// already handles: <c>item/started</c>, <c>item/agentMessage/delta</c> and <c>item/completed</c> on agentMessage items.
/// Tool activity comes from <see cref="AssistantMcpRelay"/> (the broker's own replies), not from the stream, and
/// <c>turn/started</c>/<c>turn/completed</c> come only from the broker. This class does no I/O.
/// </summary>
/// <remarks>
/// It is also the init gate: the first <c>system/init</c> must list only the allowed <c>mcp__ddistro__*</c> tools, exactly one
/// MCP server (<c>ddistro</c>, connected), no plugins and the dontAsk permission mode. Output before an accepted init, or a
/// tool_use for any other name later, refuses the turn; the caller then kills the process. The one exception is a start-up
/// failure: an error <c>result</c> with no model turn as the very first output, which ends the turn as failed (or as
/// <see cref="ClaudeTurnOutcome.SessionMissing"/>) instead. A success, or a result after any model turn, still refuses.
/// </remarks>
public sealed class ClaudeStreamTranslator
{
    public const string RefusedMessage = "Claude started with tools DwemerDistro did not allow.";
    public const string SignInMessage = "Claude sign-in needed. Run claude once in a terminal to sign in.";
    public const string FailedMessage = "Claude could not finish the response.";
    public const string TruncatedMessage = "Claude stopped before finishing the response.";
    public const string SessionMissingMessage = "Claude Code no longer has this chat's session, so the message was not answered.";
    private static readonly Regex SessionNotFound = new(@"^No conversation found with session ID\b", RegexOptions.IgnoreCase);

    /// <summary>
    /// Built-in tools the CLI may still list with <c>--tools ""</c> that reach nothing outside the conversation. They are
    /// tolerated in init but never put in <c>--allowedTools</c>, so dontAsk denies them anyway.
    /// </summary>
    internal static readonly string[] InertCliTools = ["EndConversation"];

    /// <summary>
    /// Claude Code built-in tool names a refusal reason may name. Any other name is only counted, so a connector or
    /// tool name from the user's own setup never reaches the log.
    /// </summary>
    internal static readonly string[] KnownCliTools =
    [
        "Agent", "Task", "TaskOutput", "TaskStop", "Bash", "BashOutput", "KillShell", "PowerShell", "Glob", "Grep", "LS", "Read",
        "Edit", "MultiEdit", "Write", "NotebookEdit", "NotebookRead", "WebFetch", "WebSearch", "TodoWrite", "ToolSearch",
        "ListMcpResourcesTool", "ReadMcpResourceTool", "ReadMcpResourceDirTool", "ExitPlanMode", "EnterPlanMode", "SlashCommand",
        "Skill", "AskUserQuestion", "EndConversation", "Monitor", "EnterWorktree", "ExitWorktree", "CronCreate", "CronDelete",
        "CronList", "SendMessage", "ListAgents", "ScheduleWakeup", "RemoteTrigger", "PushNotification", "LSP", "Workflow"
    ];

    private static readonly string[] KnownServerStatuses = ["connected", "pending", "failed", "needs-auth", "disabled"];
    private static readonly string[] KnownPermissionModes = ["default", "acceptEdits", "bypassPermissions", "plan", "dontAsk", "auto"];
    private static readonly string[] KnownMessageTypes =
        ["assistant", "user", "result", "stream_event", "rate_limit_event", "control_request", "control_response", "tool_progress", "auth_status", "keep_alive"];

    private const int MaxMessageChars = 1024 * 1024;
    private static readonly Regex SafeId = new(@"^[A-Za-z0-9_.:-]{1,96}\z");

    private readonly string _threadId;
    private readonly string _turnId;
    private readonly IReadOnlySet<string> _allowedTools;
    private readonly string? _resumeSessionId;
    private readonly Dictionary<int, TextBlock> _blocks = [];
    private readonly HashSet<string> _streamedMessages = new(StringComparer.Ordinal);
    private string _messageId = string.Empty;
    private int _messageCount;
    private bool _anyText;
    private bool _authFailure;

    /// <param name="resumeSessionId">The session <c>--resume</c> asked for; null for a new session.</param>
    public ClaudeStreamTranslator(string threadId, string turnId, IReadOnlySet<string> allowedTools, string? resumeSessionId = null)
    {
        _threadId = threadId;
        _turnId = turnId;
        _allowedTools = allowedTools;
        _resumeSessionId = resumeSessionId;
    }

    public ClaudeInitState Init { get; private set; } = ClaudeInitState.Pending;

    /// <summary>
    /// Why the turn was first refused, for the operation log; empty until then. Built only from fixed words, counts and
    /// the known names above: never stream text, prompts, paths, account data or unknown tool and server names.
    /// </summary>
    public string RefusalReason { get; private set; } = string.Empty;

    /// <summary>
    /// Which start-up failure ended the turn before init, for the operation log; empty otherwise. One fixed word:
    /// <c>session-not-found</c>, <c>sign-in</c> or <c>other</c>. The CLI's own error text is never kept.
    /// </summary>
    public string StartupError { get; private set; } = string.Empty;

    /// <summary>Claude's session UUID from init or result; empty until known.</summary>
    public string SessionId { get; private set; } = string.Empty;

    /// <summary>Set by the <c>result</c> line. Null until then (or forever, when the stream was cut short).</summary>
    public ClaudeTurnResult? Result { get; private set; }

    /// <summary>The notifications for one stdout line. Unknown or malformed lines produce none.</summary>
    public IReadOnlyList<(string Method, JsonElement Params)> Translate(string line)
    {
        var output = new List<(string, JsonElement)>();
        if (Init == ClaudeInitState.Refused || Result is not null) return output;
        JsonElement message;
        try
        {
            using var document = JsonDocument.Parse(line, new JsonDocumentOptions { MaxDepth = 128 });
            message = document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return output;
        }
        if (message.ValueKind != JsonValueKind.Object) return output;

        var type = AssistantCliService.GetString(message, "type");
        if (type == "system")
        {
            if (AssistantCliService.GetString(message, "subtype") == "init") CheckInit(message);
            return output;
        }
        if (Init != ClaudeInitState.Allowed)
        {
            if (type != "result" || !FinishBeforeInit(message)) Refuse("output before init: " + Known(type, KnownMessageTypes));
            return output;
        }
        // Subagent traffic cannot happen without the Task tool; ignore rather than show it as the reply.
        if (message.TryGetProperty("parent_tool_use_id", out var parent) && parent.ValueKind != JsonValueKind.Null) return output;

        switch (type)
        {
            case "stream_event":
                TranslateStreamEvent(Prop(message, "event"), output);
                break;
            case "assistant":
                TranslateAssistant(message, output);
                break;
            case "result":
                FinishFromResult(message, output);
                break;
        }
        return output;
    }

    /// <summary>The result when stdout ended: the <c>result</c> line's, or a truncation failure.</summary>
    public ClaudeTurnResult Finish() =>
        Init == ClaudeInitState.Refused ? new ClaudeTurnResult(ClaudeTurnOutcome.Refused, SessionId, RefusedMessage)
        : Result ?? new ClaudeTurnResult(_authFailure ? ClaudeTurnOutcome.SignInNeeded : ClaudeTurnOutcome.Failed, SessionId,
            _authFailure ? SignInMessage : TruncatedMessage);

    private void CheckInit(JsonElement message)
    {
        var problems = new List<string>();
        var tools = Prop(message, "tools");
        if (tools.ValueKind != JsonValueKind.Array)
        {
            problems.Add("tools missing");
        }
        else
        {
            var unexpected = tools.EnumerateArray()
                .Where(tool => tool.ValueKind != JsonValueKind.String
                               || !_allowedTools.Contains(tool.GetString()!) && !InertCliTools.Contains(tool.GetString()))
                .Select(tool => tool.ValueKind == JsonValueKind.String ? tool.GetString()! : string.Empty)
                .ToList();
            if (unexpected.Count > 0) problems.Add("tools " + Names(unexpected));
        }
        var servers = Prop(message, "mcp_servers");
        if (servers.ValueKind != JsonValueKind.Array)
        {
            problems.Add("mcp_servers missing");
        }
        else
        {
            var ours = servers.EnumerateArray().Count(server => AssistantCliService.GetString(server, "name") == AssistantMcpRelay.ServerName);
            var others = servers.GetArrayLength() - ours;
            var status = ours == 1
                ? AssistantCliService.GetString(servers.EnumerateArray().First(server => AssistantCliService.GetString(server, "name") == AssistantMcpRelay.ServerName), "status")
                : string.Empty;
            if (ours != 1 || others != 0 || status != "connected")
                problems.Add($"mcp_servers ddistro={ours} other={others} status={(ours == 1 ? Known(status, KnownServerStatuses) : "n/a")}");
        }
        var plugins = Prop(message, "plugins");
        if (!(plugins.ValueKind == JsonValueKind.Undefined || plugins.ValueKind == JsonValueKind.Array && plugins.GetArrayLength() == 0))
            problems.Add("plugins " + (plugins.ValueKind == JsonValueKind.Array
                ? $"{plugins.GetArrayLength()} builtin={plugins.EnumerateArray().Count(plugin => AssistantCliService.GetString(plugin, "source").EndsWith("@builtin", StringComparison.Ordinal))}"
                : "malformed"));
        var mode = Prop(message, "permissionMode");
        if (!(mode.ValueKind == JsonValueKind.Undefined || mode.ValueKind == JsonValueKind.String && mode.GetString() == "dontAsk"))
            problems.Add("permissionMode " + (mode.ValueKind == JsonValueKind.String ? Known(mode.GetString()!, KnownPermissionModes) : "malformed"));
        var session = AssistantCliService.GetString(message, "session_id");
        if (Guid.TryParseExact(session, "D", out _)) SessionId = session;
        // A later init (a second user message) must pass again; one failure ends the turn.
        if (problems.Count > 0) Refuse("init: " + string.Join("; ", problems));
        else if (Init != ClaudeInitState.Refused) Init = ClaudeInitState.Allowed;
    }

    /// <summary>Refuses the turn, keeping the reason of the first refusal only.</summary>
    private void Refuse(string reason)
    {
        if (Init != ClaudeInitState.Refused) RefusalReason = reason;
        Init = ClaudeInitState.Refused;
    }

    /// <summary><paramref name="value"/> when it is one of <paramref name="known"/>, otherwise a fixed word.</summary>
    private static string Known(string value, string[] known) =>
        value.Length == 0 ? "none" : known.Contains(value, StringComparer.Ordinal) ? value : "other";

    /// <summary>Unexpected tool names: known built-ins by name, sorted; every other name only counted.</summary>
    private static string Names(IReadOnlyCollection<string> names)
    {
        var known = names.Where(name => KnownCliTools.Contains(name, StringComparer.Ordinal)).Distinct().Order(StringComparer.Ordinal).ToList();
        var mcp = names.Count(name => name.StartsWith("mcp__", StringComparison.Ordinal));
        var other = names.Count - names.Count(name => KnownCliTools.Contains(name, StringComparer.Ordinal)) - mcp;
        var parts = new List<string>();
        if (known.Count > 0) parts.Add("[" + string.Join(",", known) + "]");
        if (mcp > 0) parts.Add($"mcp={mcp}");
        if (other > 0) parts.Add($"other={other}");
        return string.Join(" ", parts);
    }

    private void TranslateStreamEvent(JsonElement streamEvent, List<(string, JsonElement)> output)
    {
        var index = Prop(streamEvent, "index") is { ValueKind: JsonValueKind.Number } i && i.TryGetInt32(out var parsed) ? parsed : -1;
        switch (AssistantCliService.GetString(streamEvent, "type"))
        {
            case "message_start":
                _blocks.Clear();
                _messageCount++;
                var id = AssistantCliService.GetString(Prop(streamEvent, "message"), "id");
                _messageId = SafeId.IsMatch(id) ? id : $"claude-msg-{_messageCount}";
                break;
            case "content_block_start":
                var block = Prop(streamEvent, "content_block");
                var blockType = AssistantCliService.GetString(block, "type");
                if (blockType == "tool_use") CheckToolUse(AssistantCliService.GetString(block, "name"));
                else if (blockType == "text" && index >= 0) _blocks[index] = new TextBlock($"{_messageId}:{index}");
                break;
            case "content_block_delta":
                var delta = Prop(streamEvent, "delta");
                if (AssistantCliService.GetString(delta, "type") != "text_delta" || !_blocks.TryGetValue(index, out var open)) break;
                var text = AssistantCliService.GetString(delta, "text");
                if (text.Length == 0 || open.Text.Length + text.Length > MaxMessageChars) break;
                if (!open.Started)
                {
                    open.Started = true;
                    _streamedMessages.Add(_messageId);
                    output.Add(("item/started", AgentItem(open.ItemId, string.Empty)));
                }
                open.Text.Append(text);
                _anyText = true;
                output.Add(("item/agentMessage/delta", Element(new JsonObject
                {
                    ["threadId"] = _threadId,
                    ["turnId"] = _turnId,
                    ["itemId"] = open.ItemId,
                    ["delta"] = text
                })));
                break;
            case "content_block_stop":
                if (_blocks.Remove(index, out var closed) && closed.Started)
                    output.Add(("item/completed", AgentItem(closed.ItemId, closed.Text.ToString())));
                break;
        }
    }

    /// <summary>A whole assistant message. Its text is shown only when it was not already streamed as deltas.</summary>
    private void TranslateAssistant(JsonElement message, List<(string, JsonElement)> output)
    {
        var body = Prop(message, "message");
        if (AssistantCliService.GetString(message, "error") == "authentication_failed") _authFailure = true;
        if (Prop(body, "content") is not { ValueKind: JsonValueKind.Array } content) return;
        var id = AssistantCliService.GetString(body, "id");
        var streamed = SafeId.IsMatch(id) && _streamedMessages.Contains(id);
        var position = 0;
        foreach (var block in content.EnumerateArray())
        {
            position++;
            var blockType = AssistantCliService.GetString(block, "type");
            if (blockType == "tool_use")
            {
                CheckToolUse(AssistantCliService.GetString(block, "name"));
                continue;
            }
            if (blockType != "text" || streamed) continue;
            var text = AssistantCliService.GetString(block, "text");
            if (text.Length is 0 or > MaxMessageChars) continue;
            var itemId = (SafeId.IsMatch(id) ? id : $"claude-msg-{++_messageCount}") + ":a" + position;
            output.Add(("item/started", AgentItem(itemId, string.Empty)));
            output.Add(("item/completed", AgentItem(itemId, text)));
            _anyText = true;
        }
    }

    private void FinishFromResult(JsonElement message, List<(string, JsonElement)> output)
    {
        var session = AssistantCliService.GetString(message, "session_id");
        if (Guid.TryParseExact(session, "D", out _)) SessionId = session;
        var isError = Prop(message, "is_error").ValueKind != JsonValueKind.False;
        var subtype = AssistantCliService.GetString(message, "subtype");
        var text = AssistantCliService.GetString(message, "result");
        if (subtype == "success" && !isError)
        {
            // Only when no text arrived any other way, so a final message is never shown twice.
            if (!_anyText && text.Length is > 0 and <= MaxMessageChars)
            {
                var itemId = $"claude-result-{_turnId}";
                output.Add(("item/started", AgentItem(itemId, string.Empty)));
                output.Add(("item/completed", AgentItem(itemId, text)));
            }
            Result = new ClaudeTurnResult(ClaudeTurnOutcome.Completed, SessionId, string.Empty);
            return;
        }
        Result = _authFailure || LooksLikeSignIn(text)
            ? new ClaudeTurnResult(ClaudeTurnOutcome.SignInNeeded, SessionId, SignInMessage)
            : new ClaudeTurnResult(ClaudeTurnOutcome.Failed, SessionId, FailedMessage);
    }

    /// <summary>
    /// A <c>result</c> as the first output: accepted only as a start-up failure (<c>is_error</c> true, <c>num_turns</c> 0,
    /// not a success), so nothing reached the model. <see cref="ClaudeTurnOutcome.SessionMissing"/> needs the CLI's own
    /// not-found error for exactly the session <c>--resume</c> asked for. False means the caller refuses the turn.
    /// </summary>
    private bool FinishBeforeInit(JsonElement message)
    {
        if (Init != ClaudeInitState.Pending || _anyText) return false;
        var turns = Prop(message, "num_turns");
        if (Prop(message, "is_error").ValueKind != JsonValueKind.True || AssistantCliService.GetString(message, "subtype") == "success"
            || turns.ValueKind != JsonValueKind.Number || !turns.TryGetInt32(out var count) || count != 0)
            return false;
        var session = AssistantCliService.GetString(message, "session_id");
        if (Guid.TryParseExact(session, "D", out _)) SessionId = session;
        var errors = Prop(message, "errors") is { ValueKind: JsonValueKind.Array } list
            ? list.EnumerateArray().Where(error => error.ValueKind == JsonValueKind.String).Select(error => error.GetString()!).ToList()
            : [];
        if (_resumeSessionId is not null && session == _resumeSessionId && errors.Count == 1 && SessionNotFound.IsMatch(errors[0]))
        {
            StartupError = "session-not-found";
            Result = new ClaudeTurnResult(ClaudeTurnOutcome.SessionMissing, SessionId, SessionMissingMessage);
        }
        else if (errors.Append(AssistantCliService.GetString(message, "result")).Any(LooksLikeSignIn))
        {
            StartupError = "sign-in";
            Result = new ClaudeTurnResult(ClaudeTurnOutcome.SignInNeeded, SessionId, SignInMessage);
        }
        else
        {
            StartupError = "other";
            Result = new ClaudeTurnResult(ClaudeTurnOutcome.Failed, SessionId, FailedMessage);
        }
        return true;
    }

    /// <summary>Matched only to choose launcher text; the CLI's own wording is never shown or logged.</summary>
    internal static bool LooksLikeSignIn(string text) =>
        Regex.IsMatch(text, @"/login|not logged in|log in|sign in|authenticat|oauth|invalid api key", RegexOptions.IgnoreCase);

    private void CheckToolUse(string name)
    {
        if (!_allowedTools.Contains(name)) Refuse("tool_use " + Names([name]));
    }

    private JsonElement AgentItem(string id, string text) => Element(new JsonObject
    {
        ["threadId"] = _threadId,
        ["turnId"] = _turnId,
        ["item"] = new JsonObject { ["type"] = "agentMessage", ["id"] = id, ["text"] = text }
    });

    /// <summary>A relayed broker call as the dynamicToolCall item Codex would report when it starts.</summary>
    internal static JsonElement ToolCallStarted(string threadId, string turnId, string callId, string tool, JsonNode arguments) => Element(new JsonObject
    {
        ["threadId"] = threadId,
        ["turnId"] = turnId,
        ["item"] = new JsonObject
        {
            ["type"] = "dynamicToolCall",
            ["id"] = callId,
            ["tool"] = tool,
            ["arguments"] = arguments.DeepClone(),
            ["status"] = "inProgress"
        }
    });

    /// <summary>The broker's reply as the completed dynamicToolCall item, with its native success and contentItems.</summary>
    internal static JsonElement ToolCallCompleted(string threadId, string turnId, string callId, string tool, JsonNode arguments, bool success,
        JsonNode contentItems) => Element(new JsonObject
    {
        ["threadId"] = threadId,
        ["turnId"] = turnId,
        ["item"] = new JsonObject
        {
            ["type"] = "dynamicToolCall",
            ["id"] = callId,
            ["tool"] = tool,
            ["arguments"] = arguments.DeepClone(),
            ["status"] = success ? "completed" : "failed",
            ["success"] = success,
            ["contentItems"] = contentItems.DeepClone()
        }
    });

    private static JsonElement Element(JsonObject value) => JsonSerializer.SerializeToElement(value);

    private static JsonElement Prop(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) ? value : default;

    private sealed class TextBlock(string itemId)
    {
        public string ItemId { get; } = itemId;
        public StringBuilder Text { get; } = new();
        public bool Started { get; set; }
    }
}
