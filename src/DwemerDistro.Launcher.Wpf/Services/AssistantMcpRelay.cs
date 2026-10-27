using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace DwemerDistro.Launcher.Wpf.Services;

/// <summary>
/// One external turn's MCP endpoint for the local Claude Code CLI: Streamable HTTP with JSON-only replies, on an ephemeral
/// IPv4 loopback port, so no http.sys URL reservation is needed. Every <c>tools/call</c> is forwarded unchanged as
/// <c>ddistro/external/tool/call</c> through the existing bridge, and Core's broker decides everything about it (arguments,
/// budgets, approvals, journals). The relay only checks that the name is one the broker declared for this thread.
/// </summary>
/// <remarks>
/// Each request is one connection: the reply always carries <c>Connection: close</c>, so nothing after the declared body
/// is ever read as a second request. A wrong bearer, a non-loopback Host or any Origin gets 403, never 401, so the client
/// never starts OAuth discovery. Revoke ends the turn's access: pending and new calls get a JSON-RPC error.
/// </remarks>
public sealed class AssistantMcpRelay : IAsyncDisposable
{
    public const string ServerName = "ddistro";
    public const string ToolPrefix = "mcp__" + ServerName + "__";
    public const string BrokerToolMethod = "ddistro/external/tool/call";

    /// <summary>Above the broker's 600 s live-run card expiry, so an approval wait ends in Core first.</summary>
    public static readonly TimeSpan ToolCallTimeout = TimeSpan.FromSeconds(660);

    internal const int MaxBodyBytes = 8 * 1024 * 1024;
    internal const int MaxHeadBytes = 16 * 1024;
    internal const int MaxHeaders = 64;
    internal const int MaxConnections = 16;
    internal const int MaxTools = 64;
    /// <summary>Distinct <c>tools/call</c> request ids remembered per turn; more than this in one turn is refused.</summary>
    internal const int MaxCallsPerTurn = 256;
    internal static readonly string[] ProtocolVersions = ["2025-11-25", "2025-06-18", "2025-03-26", "2024-11-05"];
    private static readonly TimeSpan HeadTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan BodyTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan WriteTimeout = TimeSpan.FromSeconds(30);
    private static readonly Regex ToolName = new(@"^[A-Za-z0-9_-]{1,64}\z");
    private static readonly Regex HeaderName = new(@"^[!#$%&'*+\-.^_`|~0-9A-Za-z]{1,64}\z");
    private static readonly byte[] HeadEnd = "\r\n\r\n"u8.ToArray();

    /// <summary>Sends one broker tool call. Production uses <see cref="ForBroker"/>; tests pass a recorded reply.</summary>
    public delegate Task<JsonElement> ToolForwarder(JsonObject parameters, CancellationToken cancellationToken);

    private readonly TcpListener _listener;
    private readonly Dictionary<string, JsonObject> _tools;
    private readonly string _threadId;
    private readonly string _turnId;
    private readonly ToolForwarder _forward;
    private readonly byte[] _bearerHash;
    private readonly string _bearer;
    private readonly SemaphoreSlim _slots = new(MaxConnections, MaxConnections);
    private readonly CancellationTokenSource _revoked = new();
    private readonly CancellationTokenSource _stopped = new();
    private readonly ConcurrentDictionary<TcpClient, byte> _connections = new();
    private readonly Dictionary<string, ToolCall> _calls = new(StringComparer.Ordinal);
    private Task _acceptLoop = Task.CompletedTask;
    private int _disposed;

    private AssistantMcpRelay(TcpListener listener, Dictionary<string, JsonObject> tools, string threadId, string turnId, ToolForwarder forward)
    {
        _listener = listener;
        _tools = tools;
        _threadId = threadId;
        _turnId = turnId;
        _forward = forward;
        _bearer = Base64Url(RandomNumberGenerator.GetBytes(32));
        _bearerHash = SHA256.HashData(Encoding.ASCII.GetBytes("Bearer " + _bearer));
    }

    /// <summary>
    /// Opens the endpoint for one turn. <paramref name="brokerTools"/> is the <c>tools</c> array from
    /// <c>ddistro/external/thread/start</c> or <c>resume</c>; anything malformed fails closed with <see cref="ArgumentException"/>.
    /// </summary>
    public static AssistantMcpRelay Start(JsonElement brokerTools, string threadId, string turnId, ToolForwarder forward)
    {
        if (threadId.Length is 0 or > 128 || turnId.Length is 0 or > 128) throw new ArgumentException("Thread and turn ids are required.");
        var tools = ParseTools(brokerTools);
        var listener = new TcpListener(IPAddress.Loopback, 0) { ExclusiveAddressUse = true };
        listener.Start(MaxConnections);
        var relay = new AssistantMcpRelay(listener, tools, threadId, turnId, forward);
        relay._acceptLoop = Task.Run(relay.AcceptLoopAsync);
        return relay;
    }

    /// <summary>The production forwarder: the call goes to Core's broker over the attached bridge session.</summary>
    public static ToolForwarder ForBroker(CodexAppServerClient client) =>
        (parameters, cancellationToken) => client.RequestAsync(BrokerToolMethod, parameters, ToolCallTimeout, cancellationToken);

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    public string Endpoint => $"http://127.0.0.1:{Port}/mcp";

    public bool IsRevoked => _revoked.IsCancellationRequested;

    /// <summary>The broker's tool names, as Claude Code exposes them (<c>mcp__ddistro__name</c>). Exactly these are allowed.</summary>
    public IReadOnlySet<string> ClaudeToolNames => _tools.Keys.Select(name => ToolPrefix + name).ToHashSet(StringComparer.Ordinal);

    /// <summary>Raised off the UI thread with Codex-shaped dynamicToolCall item/started and item/completed notifications.</summary>
    public event Action<string, JsonElement>? Notification;

    /// <summary>
    /// The private MCP config for <c>--mcp-config</c>. It carries the bearer, so it is written only to the turn's private
    /// file and never logged or put on a command line.
    /// </summary>
    internal string BuildClientConfig() => new JsonObject
    {
        ["mcpServers"] = new JsonObject
        {
            [ServerName] = new JsonObject
            {
                ["type"] = "http",
                ["url"] = Endpoint,
                ["headers"] = new JsonObject { ["Authorization"] = "Bearer " + _bearer }
            }
        }
    }.ToJsonString();

    /// <summary>Ends this turn's access. Pending calls stop waiting and every later call is refused. Safe to call repeatedly.</summary>
    public void Revoke()
    {
        try
        {
            _revoked.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
        Revoke();
        _stopped.Cancel();
        _listener.Stop();
        foreach (var connection in _connections.Keys) connection.Dispose();
        try
        {
            await _acceptLoop.ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException)
        {
        }
    }

    internal static Dictionary<string, JsonObject> ParseTools(JsonElement brokerTools)
    {
        if (brokerTools.ValueKind != JsonValueKind.Array || brokerTools.GetArrayLength() is 0 or > MaxTools)
            throw new ArgumentException("The broker declared no usable tools.");
        var tools = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        foreach (var tool in brokerTools.EnumerateArray())
        {
            var name = AssistantCliService.GetString(tool, "name");
            if (!ToolName.IsMatch(name) || tools.ContainsKey(name)
                || !tool.TryGetProperty("inputSchema", out var schema) || schema.ValueKind != JsonValueKind.Object)
                throw new ArgumentException("The broker declared a malformed tool.");
            var definition = new JsonObject
            {
                ["name"] = name,
                ["description"] = AssistantCliService.GetString(tool, "description"),
                ["inputSchema"] = JsonNode.Parse(schema.GetRawText())
            };
            tools[name] = definition;
        }
        return tools;
    }

    // --- HTTP ------------------------------------------------------------------------------------

    private async Task AcceptLoopAsync()
    {
        while (!_stopped.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_stopped.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException or InvalidOperationException)
            {
                break;
            }

            if (client.Client.RemoteEndPoint is not IPEndPoint { } remote || !IPAddress.IsLoopback(remote.Address))
            {
                client.Dispose();
                continue;
            }
            if (!_slots.Wait(0))
            {
                _ = RefuseBusyAsync(client);
                continue;
            }
            _connections[client] = 0;
            _ = Task.Run(async () =>
            {
                try
                {
                    await HandleConnectionAsync(client).ConfigureAwait(false);
                }
                finally
                {
                    _connections.TryRemove(client, out _);
                    client.Dispose();
                    _slots.Release();
                }
            });
        }
    }

    private async Task RefuseBusyAsync(TcpClient client)
    {
        using (client)
        {
            try
            {
                await WriteResponseAsync(client.GetStream(), 503, null, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException or OperationCanceledException)
            {
            }
        }
    }

    private async Task HandleConnectionAsync(TcpClient client)
    {
        var stream = client.GetStream();
        try
        {
            var (status, extra, body) = await ReadRequestAsync(stream).ConfigureAwait(false);
            if (status != 200)
            {
                await WriteResponseAsync(stream, status, null, _stopped.Token, extra).ConfigureAwait(false);
                return;
            }
            var (replyStatus, reply) = await DispatchAsync(body!, _stopped.Token).ConfigureAwait(false);
            await WriteResponseAsync(stream, replyStatus, reply, _stopped.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException or OperationCanceledException)
        {
            // The client went away or the relay closed; there is nobody to answer.
        }
    }

    /// <summary>
    /// Reads one request head and body. Returns 200 with the body when it may be dispatched, otherwise the HTTP status to
    /// answer (and an extra header line for 405). Every check happens before the body is read.
    /// </summary>
    private async Task<(int Status, string? Extra, byte[]? Body)> ReadRequestAsync(NetworkStream stream)
    {
        var buffer = new byte[MaxHeadBytes];
        var filled = 0;
        var headLength = -1;
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(_stopped.Token))
        {
            timeout.CancelAfter(HeadTimeout);
            while (headLength < 0)
            {
                if (filled == buffer.Length) return (431, null, null);
                int read;
                try
                {
                    read = await stream.ReadAsync(buffer.AsMemory(filled), timeout.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!_stopped.IsCancellationRequested)
                {
                    return (408, null, null);
                }
                if (read == 0) return (400, null, null);
                filled += read;
                var end = buffer.AsSpan(0, filled).IndexOf(HeadEnd);
                if (end >= 0) headLength = end + HeadEnd.Length;
            }
        }

        var head = ParseHead(buffer.AsSpan(0, headLength - HeadEnd.Length));
        var check = CheckHead(head);
        if (check.Status != 200) return (check.Status, check.Extra, null);

        var length = check.ContentLength;
        var already = filled - headLength;
        // Bytes past the declared body would be a second request on this connection: refuse rather than ignore.
        if (already > length) return (400, null, null);
        var body = new byte[length];
        buffer.AsSpan(headLength, already).CopyTo(body);
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(_stopped.Token))
        {
            timeout.CancelAfter(BodyTimeout);
            var offset = already;
            while (offset < length)
            {
                int read;
                try
                {
                    read = await stream.ReadAsync(body.AsMemory(offset), timeout.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!_stopped.IsCancellationRequested)
                {
                    return (408, null, null);
                }
                if (read == 0) return (400, null, null);
                offset += read;
            }
        }
        return (200, null, body);
    }

    internal sealed record RequestHead(string Method, string Target, string Version, List<KeyValuePair<string, string>> Headers, bool Malformed);

    /// <summary>Request line and header fields. Bare LF, obsolete line folding, bad names and control bytes mark it malformed.</summary>
    internal static RequestHead ParseHead(ReadOnlySpan<byte> bytes)
    {
        var malformed = false;
        foreach (var b in bytes)
        {
            if (b is < 0x20 and not (byte)'\r' and not (byte)'\n' and not (byte)'\t' || b >= 0x7F) malformed = true;
        }
        var text = Encoding.ASCII.GetString(bytes);
        var lines = text.Split("\r\n");
        var headers = new List<KeyValuePair<string, string>>();
        if (lines.Any(line => line.Contains('\n') || line.Contains('\r'))) malformed = true;
        var requestLine = lines[0].Split(' ');
        if (requestLine.Length != 3) return new RequestHead(string.Empty, string.Empty, string.Empty, headers, true);
        for (var i = 1; i < lines.Length; i++)
        {
            var line = lines[i];
            var colon = line.IndexOf(':');
            if (line.Length == 0 || line[0] is ' ' or '\t' || colon <= 0 || !HeaderName.IsMatch(line[..colon]))
            {
                malformed = true;
                continue;
            }
            headers.Add(new KeyValuePair<string, string>(line[..colon], line[(colon + 1)..].Trim(' ', '\t')));
        }
        if (headers.Count > MaxHeaders) malformed = true;
        return new RequestHead(requestLine[0], requestLine[1], requestLine[2], headers, malformed);
    }

    internal readonly record struct HeadCheck(int Status, string? Extra, int ContentLength);

    /// <summary>The order matters: framing, then path, then Host/Origin/bearer (403), then method (405), then the body rules.</summary>
    internal HeadCheck CheckHead(RequestHead head)
    {
        if (head.Malformed) return new HeadCheck(400, null, 0);
        if (head.Version != "HTTP/1.1") return new HeadCheck(505, null, 0);
        if (head.Target != "/mcp") return new HeadCheck(404, null, 0);

        string[] Values(string name) => head.Headers.Where(h => h.Key.Equals(name, StringComparison.OrdinalIgnoreCase)).Select(h => h.Value).ToArray();
        var host = Values("Host");
        if (host.Length != 1 || !IsLoopbackHost(host[0], Port)) return new HeadCheck(403, null, 0);
        if (Values("Origin").Length != 0) return new HeadCheck(403, null, 0);
        var authorization = Values("Authorization");
        if (authorization.Length != 1 || !BearerMatches(authorization[0])) return new HeadCheck(403, null, 0);
        if (head.Method != "POST") return new HeadCheck(405, "Allow: POST", 0);

        if (Values("Transfer-Encoding").Length != 0) return new HeadCheck(400, null, 0);
        if (Values("Expect").Length != 0) return new HeadCheck(417, null, 0);
        var lengths = Values("Content-Length");
        if (lengths.Length == 0) return new HeadCheck(411, null, 0);
        if (lengths.Length != 1 || lengths[0].Length is 0 or > 10 || !lengths[0].All(char.IsAsciiDigit)) return new HeadCheck(400, null, 0);
        var length = long.Parse(lengths[0]);
        if (length > MaxBodyBytes) return new HeadCheck(413, null, 0);
        var contentType = Values("Content-Type");
        if (contentType.Length != 1 || !MediaTypeIs(contentType[0], "application/json")) return new HeadCheck(415, null, 0);
        var accept = Values("Accept");
        if (accept.Length > 0 && !AcceptsJson(string.Join(",", accept))) return new HeadCheck(406, null, 0);
        return new HeadCheck(200, null, (int)length);
    }

    internal static bool IsLoopbackHost(string host, int port) =>
        host.Equals($"127.0.0.1:{port}", StringComparison.OrdinalIgnoreCase) || host.Equals($"localhost:{port}", StringComparison.OrdinalIgnoreCase);

    private bool BearerMatches(string presented) =>
        CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(presented)), _bearerHash);

    private static bool MediaTypeIs(string value, string mediaType) =>
        value.Split(';')[0].Trim().Equals(mediaType, StringComparison.OrdinalIgnoreCase);

    private static bool AcceptsJson(string accept) =>
        accept.Split(',').Any(range => MediaTypeIs(range, "application/json") || MediaTypeIs(range, "application/*") || MediaTypeIs(range, "*/*"));

    private static async Task WriteResponseAsync(NetworkStream stream, int status, string? body, CancellationToken cancellationToken, string? extra = null)
    {
        var payload = body is null ? [] : Encoding.UTF8.GetBytes(body);
        var head = new StringBuilder()
            .Append("HTTP/1.1 ").Append(status).Append(' ').Append(Reason(status)).Append("\r\n");
        if (body is not null) head.Append("Content-Type: application/json\r\n");
        if (extra is not null) head.Append(extra).Append("\r\n");
        head.Append("Content-Length: ").Append(payload.Length).Append("\r\n")
            .Append("Cache-Control: no-store\r\n")
            .Append("Connection: close\r\n\r\n");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(WriteTimeout);
        await stream.WriteAsync(Encoding.ASCII.GetBytes(head.ToString()), timeout.Token).ConfigureAwait(false);
        if (payload.Length > 0) await stream.WriteAsync(payload, timeout.Token).ConfigureAwait(false);
        await stream.FlushAsync(timeout.Token).ConfigureAwait(false);
    }

    private static string Reason(int status) => status switch
    {
        200 => "OK",
        202 => "Accepted",
        400 => "Bad Request",
        403 => "Forbidden",
        404 => "Not Found",
        405 => "Method Not Allowed",
        406 => "Not Acceptable",
        408 => "Request Timeout",
        411 => "Length Required",
        413 => "Content Too Large",
        415 => "Unsupported Media Type",
        417 => "Expectation Failed",
        431 => "Request Header Fields Too Large",
        503 => "Service Unavailable",
        505 => "HTTP Version Not Supported",
        _ => "Error"
    };

    // --- JSON-RPC --------------------------------------------------------------------------------

    /// <summary>
    /// One JSON-RPC message to its HTTP status and body. Notifications and client responses get 202 with no body; batches
    /// are refused (removed from MCP in 2025-06-18).
    /// </summary>
    internal async Task<(int Status, string? Body)> DispatchAsync(byte[] body, CancellationToken cancellationToken)
    {
        JsonElement message;
        try
        {
            using var document = JsonDocument.Parse(body, new JsonDocumentOptions { MaxDepth = 64 });
            message = document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return (400, Error(null, -32700, "Parse error."));
        }

        if (message.ValueKind != JsonValueKind.Object || AssistantCliService.GetString(message, "jsonrpc") != "2.0")
            return (400, Error(null, -32600, "Invalid request."));
        var hasMethod = message.TryGetProperty("method", out var methodElement) && methodElement.ValueKind == JsonValueKind.String;
        var hasId = message.TryGetProperty("id", out var idElement);
        if (!hasMethod || !hasId) return (202, null);
        if (idElement.ValueKind is not (JsonValueKind.String or JsonValueKind.Number)) return (400, Error(null, -32600, "Invalid request."));
        var id = JsonNode.Parse(idElement.GetRawText());
        var parameters = message.TryGetProperty("params", out var p) ? p : default;

        switch (methodElement.GetString())
        {
            case "initialize":
                var requested = AssistantCliService.GetString(parameters, "protocolVersion");
                return (200, Result(id, new JsonObject
                {
                    ["protocolVersion"] = ProtocolVersions.Contains(requested) ? requested : ProtocolVersions[1],
                    ["capabilities"] = new JsonObject { ["tools"] = new JsonObject { ["listChanged"] = false } },
                    ["serverInfo"] = new JsonObject { ["name"] = ServerName, ["version"] = "1" }
                }));
            case "ping":
                return (200, Result(id, new JsonObject()));
            case "tools/list":
                return (200, Result(id, new JsonObject
                {
                    ["tools"] = new JsonArray(_tools.Values.Select(tool => (JsonNode)tool.DeepClone()).ToArray())
                }));
            case "tools/call":
                return (200, await CallToolOnceAsync(idElement.GetRawText(), id, parameters, cancellationToken).ConfigureAwait(false));
            default:
                return (200, Error(id, -32601, "Method not found."));
        }
    }

    /// <summary>
    /// One broker call per JSON-RPC request id in this turn. A retried request (same id, same tool and arguments) shares
    /// the first request's call and gets its reply, whether that call is still running or finished, so a side effect never
    /// runs twice and an unknown outcome is never replayed. The same id with a different payload is refused.
    /// </summary>
    private async Task<string> CallToolOnceAsync(string requestKey, JsonNode? id, JsonElement parameters, CancellationToken cancellationToken)
    {
        if (IsRevoked) return Error(id, -32000, "This DwemerDistro turn has ended.");
        var name = AssistantCliService.GetString(parameters, "name");
        if (!_tools.ContainsKey(name)) return Error(id, -32602, "Unknown tool.");
        JsonNode arguments;
        if (parameters.ValueKind == JsonValueKind.Object && parameters.TryGetProperty("arguments", out var a))
        {
            if (a.ValueKind != JsonValueKind.Object) return Error(id, -32602, "Tool arguments must be an object.");
            arguments = JsonNode.Parse(a.GetRawText())!;
        }
        else
        {
            arguments = new JsonObject();
        }

        // Tool names never contain a space, so this is unambiguous.
        var payload = name + " " + arguments.ToJsonString();
        ToolCall call;
        lock (_calls)
        {
            if (_calls.TryGetValue(requestKey, out var existing))
            {
                if (existing.Payload != payload) return Error(id, -32600, "This request id was already used for a different tool call.");
                call = existing;
            }
            else
            {
                if (_calls.Count >= MaxCallsPerTurn) return Error(id, -32000, "Too many tool calls in this DwemerDistro turn.");
                call = new ToolCall(payload, RunToolCallAsync(name, arguments));
                _calls.Add(requestKey, call);
            }
        }

        // The broker call is shared and keeps running if this connection goes away; only this wait is cancelled.
        var reply = await call.Reply.WaitAsync(cancellationToken).ConfigureAwait(false);
        reply["id"] = id?.DeepClone();
        return reply.ToJsonString();
    }

    private sealed record ToolCall(string Payload, Task<JsonObject> Reply);

    /// <summary>The broker call for one request id. Returns the JSON-RPC reply without its id. Never faults.</summary>
    private async Task<JsonObject> RunToolCallAsync(string name, JsonNode arguments)
    {
        await Task.Yield();
        // Unique across turns, because the transcript keys tool activity by item id.
        var callId = "mcp_" + Guid.NewGuid().ToString("N");
        Notification?.Invoke("item/started", ClaudeStreamTranslator.ToolCallStarted(_threadId, _turnId, callId, name, arguments));

        bool success;
        JsonNode contentItems;
        try
        {
            var reply = await _forward(new JsonObject
            {
                ["threadId"] = _threadId,
                ["turnId"] = _turnId,
                ["callId"] = callId,
                ["tool"] = name,
                ["arguments"] = arguments.DeepClone()
            }, _revoked.Token).ConfigureAwait(false);
            success = reply.ValueKind == JsonValueKind.Object && reply.TryGetProperty("success", out var s) && s.ValueKind == JsonValueKind.True;
            contentItems = reply.ValueKind == JsonValueKind.Object && reply.TryGetProperty("contentItems", out var items) && items.ValueKind == JsonValueKind.Array
                ? JsonNode.Parse(items.GetRawText())!
                : new JsonArray();
        }
        catch (OperationCanceledException)
        {
            Notification?.Invoke("item/completed", ClaudeStreamTranslator.ToolCallCompleted(_threadId, _turnId, callId, name, arguments, false,
                new JsonArray(new JsonObject { ["type"] = "inputText", ["text"] = "Stopped." })));
            return ErrorNode(-32000, "This DwemerDistro turn has ended.");
        }
        catch (AppServerException ex)
        {
            success = false;
            // Code 0 is a lost answer (timeout or closed session): Core may or may not have run the tool, so say so.
            var text = ex.Code == 0
                ? "DwemerDistro lost the answer to this tool call. It may or may not have run; it was not retried."
                : "DwemerDistro could not run this tool: " + Shorten(ex.Message, 300);
            contentItems = new JsonArray(new JsonObject { ["type"] = "inputText", ["text"] = text });
        }
        catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException)
        {
            Notification?.Invoke("item/completed", ClaudeStreamTranslator.ToolCallCompleted(_threadId, _turnId, callId, name, arguments, false,
                new JsonArray(new JsonObject { ["type"] = "inputText", ["text"] = "Stopped." })));
            return ErrorNode(-32000, "This DwemerDistro turn has ended.");
        }

        Notification?.Invoke("item/completed", ClaudeStreamTranslator.ToolCallCompleted(_threadId, _turnId, callId, name, arguments, success, contentItems));
        return new JsonObject { ["jsonrpc"] = "2.0", ["result"] = new JsonObject { ["content"] = ToMcpContent(contentItems), ["isError"] = !success } };
    }

    private static JsonObject ErrorNode(int code, string message) =>
        new() { ["jsonrpc"] = "2.0", ["error"] = new JsonObject { ["code"] = code, ["message"] = message } };

    /// <summary>The broker's native contentItems as MCP content: inputText to text, a data-URL inputImage to image.</summary>
    internal static JsonArray ToMcpContent(JsonNode contentItems)
    {
        var content = new JsonArray();
        foreach (var item in contentItems as JsonArray ?? [])
        {
            var type = item?["type"]?.GetValueKind() == JsonValueKind.String ? item["type"]!.GetValue<string>() : string.Empty;
            if (type == "inputText" && item!["text"]?.GetValueKind() == JsonValueKind.String)
            {
                content.Add(new JsonObject { ["type"] = "text", ["text"] = item["text"]!.GetValue<string>() });
            }
            else if (type == "inputImage" && item!["imageUrl"]?.GetValueKind() == JsonValueKind.String
                     && ClaudeTurnProcess.TryParseDataUrl(item["imageUrl"]!.GetValue<string>(), out var mediaType, out var data))
            {
                content.Add(new JsonObject { ["type"] = "image", ["data"] = data, ["mimeType"] = mediaType });
            }
        }
        if (content.Count == 0) content.Add(new JsonObject { ["type"] = "text", ["text"] = string.Empty });
        return content;
    }

    private static string Result(JsonNode? id, JsonObject result) =>
        new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result }.ToJsonString();

    private static string Error(JsonNode? id, int code, string message) =>
        new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["error"] = new JsonObject { ["code"] = code, ["message"] = message } }.ToJsonString();

    private static string Shorten(string text, int max) => text.Length <= max ? text : text[..max] + "…";

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
