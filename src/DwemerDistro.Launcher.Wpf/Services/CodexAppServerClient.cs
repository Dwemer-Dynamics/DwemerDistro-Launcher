using System.Collections.Concurrent;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DwemerDistro.Launcher.Wpf.Services;

public sealed class AppServerException(int code, string message, string reason = "") : Exception(message)
{
    public int Code { get; } = code;

    /// <summary>A refused session's data.reason token (for example account_home_unmanaged_config); empty otherwise.</summary>
    public string Reason { get; } = reason;
}

/// <summary>
/// Codex App Server JSON-RPC over one newline-delimited stream. Message bodies are never logged.
/// Server-initiated approval requests are always declined: the launcher never grants escalation.
/// </summary>
public sealed class CodexAppServerClient : IAsyncDisposable
{
    public const int MethodNotFound = -32601;

    /// <summary>
    /// The confinement Core's broker pins for every thread and turn (approval policy never, cwd /work). The
    /// launcher states the same values, both allowed by the broker and by the rust-v0.163.0 schema, and never
    /// sends sandbox, sandboxPolicy or permissions, so the stronger managed dwemer-scratch profile always applies.
    /// </summary>
    public const string ApprovalPolicy = "never";
    public const string WorkspaceCwd = "/work";

    internal static void AddConfinement(JsonObject parameters)
    {
        parameters["approvalPolicy"] = ApprovalPolicy;
        parameters["cwd"] = WorkspaceCwd;
    }

    /// <summary>thread/start and thread/resume answer with the policy in force; anything else ends the session.</summary>
    internal static bool IsConfined(JsonElement threadResult) =>
        AssistantCliService.GetString(threadResult, "approvalPolicy") == ApprovalPolicy
        && AssistantCliService.GetString(threadResult, "cwd") == WorkspaceCwd;
    private readonly IAssistantTransport _transport;
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly CancellationTokenSource _readCts = new();
    private long _nextId;
    private int _closed;

    public CodexAppServerClient(IAssistantTransport transport)
    {
        _transport = transport;
    }

    /// <summary>Raised on the reader thread with the method and params of each notification.</summary>
    public event Action<string, JsonElement>? NotificationReceived;

    /// <summary>Raised on the reader thread after a server request was refused.</summary>
    public event Action<string>? ServerRequestDeclined;

    /// <summary>Raised once when the stream ends; every pending request has already failed.</summary>
    public event Action<string>? Disconnected;

    public bool IsConnected => Volatile.Read(ref _closed) == 0;

    public void Start() => _ = Task.Run(ReadLoopAsync);

    public async Task<JsonElement> RequestAsync(string method, object? parameters, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        if (!IsConnected) throw new AppServerException(0, "The assistant session is not connected.");
        var id = Interlocked.Increment(ref _nextId);
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = completion;
        try
        {
            await _transport.WriteLineAsync(Serialize(new JsonObject
            {
                ["method"] = method,
                ["id"] = id,
                ["params"] = ToNode(parameters)
            }), cancellationToken).ConfigureAwait(false);
            return await completion.Task.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            throw new AppServerException(0, $"The assistant did not answer {method} in time.");
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            throw new AppServerException(0, "The assistant session closed.");
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    public Task NotifyAsync(string method, object? parameters, CancellationToken cancellationToken = default) =>
        _transport.WriteLineAsync(Serialize(new JsonObject { ["method"] = method, ["params"] = ToNode(parameters) }), cancellationToken);

    /// <summary>
    /// The reply for each server request. Approvals are declined, never granted; anything the
    /// launcher does not implement gets a JSON-RPC error so the turn fails rather than waits.
    /// Shapes follow the generated App Server schema pinned at rust-v0.163.0.
    /// </summary>
    internal static JsonObject BuildServerRequestReply(string method)
    {
        return method switch
        {
            "item/commandExecution/requestApproval" or "item/fileChange/requestApproval" =>
                new JsonObject { ["result"] = new JsonObject { ["decision"] = "decline" } },
            // A permissions reply carries only the granted subset; an empty profile grants nothing.
            "item/permissions/requestApproval" =>
                new JsonObject { ["result"] = new JsonObject { ["permissions"] = new JsonObject() } },
            "execCommandApproval" or "applyPatchApproval" =>
                new JsonObject { ["result"] = new JsonObject { ["decision"] = "denied" } },
            "mcpServer/elicitation/request" =>
                new JsonObject { ["result"] = new JsonObject { ["action"] = "decline" } },
            _ => new JsonObject
            {
                ["error"] = new JsonObject { ["code"] = MethodNotFound, ["message"] = "Not supported by the DwemerDistro launcher." }
            }
        };
    }

    private async Task ReadLoopAsync()
    {
        var reason = "The assistant session ended.";
        try
        {
            while (!_readCts.IsCancellationRequested)
            {
                var line = await _transport.ReadLineAsync(_readCts.Token).ConfigureAwait(false);
                if (line is null) break;
                if (line.Length == 0) continue;
                await HandleLineAsync(line).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            reason = "The assistant session was stopped.";
        }
        catch (InvalidDataException ex)
        {
            reason = ex.Message;
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            reason = "The assistant session closed unexpectedly.";
        }

        Close(reason);
    }

    private async Task HandleLineAsync(string line)
    {
        JsonElement message;
        try
        {
            using var document = JsonDocument.Parse(line);
            message = document.RootElement.Clone();
        }
        catch (JsonException)
        {
            // Not protocol output; ignore it rather than echo unknown text anywhere.
            return;
        }

        if (message.ValueKind != JsonValueKind.Object) return;
        var hasId = message.TryGetProperty("id", out var id) && id.ValueKind is JsonValueKind.Number or JsonValueKind.String;
        var method = message.TryGetProperty("method", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString()! : null;
        var parameters = message.TryGetProperty("params", out var p) ? p : default;

        if (method is null && hasId && id.ValueKind == JsonValueKind.Number && id.TryGetInt64(out var responseId))
        {
            if (!_pending.TryGetValue(responseId, out var completion)) return;
            if (message.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object)
            {
                var code = error.TryGetProperty("code", out var c) && c.ValueKind == JsonValueKind.Number && c.TryGetInt32(out var parsed) ? parsed : 0;
                // A refused session (-32001) names its reason in data.reason, for example busy or probe_stale.
                var reason = error.TryGetProperty("data", out var data) ? AssistantCliService.GetString(data, "reason") : string.Empty;
                var text = AssistantCliService.GetString(error, "message");
                var token = reason.Length is > 0 and <= 64 && reason.All(ch => ch is >= 'a' and <= 'z' or >= '0' and <= '9' or '_') ? reason : string.Empty;
                completion.TrySetException(new AppServerException(code, reason.Length > 0 && reason.Length <= 64 ? $"{text} ({reason})" : text,
                    code == -32001 ? token : string.Empty));
            }
            else
            {
                completion.TrySetResult(message.TryGetProperty("result", out var result) ? result : default);
            }
            return;
        }

        if (method is null) return;
        if (hasId)
        {
            var reply = BuildServerRequestReply(method);
            reply["id"] = JsonNode.Parse(id.GetRawText());
            await _transport.WriteLineAsync(Serialize(reply), _readCts.Token).ConfigureAwait(false);
            ServerRequestDeclined?.Invoke(method);
            return;
        }

        NotificationReceived?.Invoke(method, parameters);
    }

    private void Close(string reason)
    {
        if (Interlocked.Exchange(ref _closed, 1) == 1) return;
        foreach (var pending in _pending.Values)
        {
            pending.TrySetException(new AppServerException(0, reason));
        }
        _pending.Clear();
        Disconnected?.Invoke(reason);
    }

    public async ValueTask DisposeAsync()
    {
        _readCts.Cancel();
        Close("The assistant session was stopped.");
        await _transport.DisposeAsync().ConfigureAwait(false);
    }

    private static JsonNode? ToNode(object? value) =>
        value switch
        {
            null => new JsonObject(),
            JsonNode node => node,
            _ => JsonSerializer.SerializeToNode(value)
        };

    private static string Serialize(JsonObject message) => message.ToJsonString();
}

/// <summary>
/// One turn at a time on one thread. A stop requested before <c>turn/start</c> answers is remembered
/// and interrupts exactly that turn once its id is known, from the response or from
/// <c>turn/started</c>, whichever arrives first. It never carries over to a later turn.
/// </summary>
public sealed class AssistantTurnRunner
{
    private readonly CodexAppServerClient _client;
    private readonly TimeSpan _timeout;
    private readonly object _gate = new();
    private PendingTurn? _current;

    public AssistantTurnRunner(CodexAppServerClient client, TimeSpan timeout)
    {
        _client = client;
        _timeout = timeout;
        client.NotificationReceived += OnNotification;
    }

    /// <summary>The running turn's id once known; null before turn/start answers or when no turn runs.</summary>
    public string? ActiveTurnId { get { lock (_gate) return _current?.Id; } }

    /// <summary>Raised off the UI thread when an interrupt was sent but refused or not answered.</summary>
    public event Action<string>? StopFailed;

    public async Task StartAsync(string threadId, JsonObject parameters)
    {
        var turn = new PendingTurn(threadId);
        lock (_gate)
        {
            if (_current is not null) throw new AppServerException(0, "A response is already running.");
            _current = turn;
        }

        JsonElement result;
        try
        {
            result = await _client.RequestAsync("turn/start", parameters, _timeout).ConfigureAwait(false);
        }
        catch
        {
            lock (_gate) if (ReferenceEquals(_current, turn)) _current = null;
            throw;
        }

        var id = result.ValueKind == JsonValueKind.Object && result.TryGetProperty("turn", out var t)
            ? AssistantCliService.GetString(t, "id")
            : string.Empty;
        await LearnIdAsync(turn, id).ConfigureAwait(false);
    }

    /// <summary>
    /// Stops this runner's turn: now when its id is known, otherwise as soon as it is. Returns false
    /// when no turn of this runner is running.
    /// </summary>
    public async Task<bool> StopAsync()
    {
        string? id;
        PendingTurn? turn;
        lock (_gate)
        {
            turn = _current;
            if (turn is null) return false;
            turn.StopRequested = true;
            id = turn.Id;
            if (id is null || turn.Interrupting) return true;
            turn.Interrupting = true;
        }
        await InterruptAsync(turn.ThreadId, id).ConfigureAwait(false);
        return true;
    }

    private async Task LearnIdAsync(PendingTurn turn, string id)
    {
        if (id.Length == 0) return;
        lock (_gate)
        {
            if (!ReferenceEquals(_current, turn) || turn.Id is not null && turn.Id != id) return;
            turn.Id = id;
            if (!turn.StopRequested || turn.Interrupting) return;
            turn.Interrupting = true;
        }
        await InterruptAsync(turn.ThreadId, id).ConfigureAwait(false);
    }

    private async Task InterruptAsync(string threadId, string turnId)
    {
        try
        {
            await _client.RequestAsync("turn/interrupt", new JsonObject { ["threadId"] = threadId, ["turnId"] = turnId }, _timeout)
                .ConfigureAwait(false);
        }
        catch (AppServerException ex)
        {
            StopFailed?.Invoke(ex.Message);
        }
    }

    private void OnNotification(string method, JsonElement parameters)
    {
        if (method is not ("turn/started" or "turn/completed")) return;
        var threadId = AssistantCliService.GetString(parameters, "threadId");
        var id = parameters.ValueKind == JsonValueKind.Object && parameters.TryGetProperty("turn", out var t)
            ? AssistantCliService.GetString(t, "id")
            : string.Empty;
        PendingTurn? turn;
        lock (_gate)
        {
            turn = _current;
            if (turn is null || threadId != turn.ThreadId) return;
            if (method == "turn/completed")
            {
                if (turn.Id is null || turn.Id == id) _current = null;
                return;
            }
        }
        _ = LearnIdAsync(turn, id);
    }

    private sealed class PendingTurn(string threadId)
    {
        public string ThreadId { get; } = threadId;
        public string? Id { get; set; }
        public bool StopRequested { get; set; }
        public bool Interrupting { get; set; }
    }
}
