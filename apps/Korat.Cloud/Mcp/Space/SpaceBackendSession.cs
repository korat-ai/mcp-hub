using System.Collections.Concurrent;
using System.Text;
using System.Text.Json.Nodes;
using Korat.Mcp;
using Google.Protobuf;
using Korat.Cloud.Gateways;
using Korat.Domain;
using Korat.Relay.V1;

namespace Korat.Cloud.Mcp.Space;

/// <summary>A single backend MCP tool as exposed through the aggregator (namespaced).</summary>
/// <summary>
/// Space-MCP (increment 1, Task 4): one relay session to a single granted backend MCP server,
/// opened by <c>SpaceMcpAggregatorGrain</c> via <c>ISessionAdmission.AdmitAsync</c> with
/// <c>ConsumerBindPolicy.ServerMinted</c>. Server-side port of
/// <c>apps/Korat.Cli/Mcp/Aggregation/BackendSession.cs</c> with these deliberate deltas from the
/// CLI reference:
///
///   • E2E DELETED entirely (no <c>_e2eCipher</c>/<c>InstallCipher</c>/cipher branches) — every
///     Space-MCP backend session is forced plaintext at admission (Global Constraint "Forced
///     peer_supports_e2e=false", SF-8: the cloud is always the plaintext terminus for this path).
///   • N3 (plan-review correction): the CLI's cipher-aware <c>OnInboundBytes</c> had FOUR
///     enc-branches (enc==1+cipher / enc!=0+cipher / cipher+enc==0 / enc!=0+no-cipher). With no
///     cipher ever installed, three of those collapse away — but the CLI's <c>enc!=0</c>
///     fail-closed branch (never parse ciphertext/garbage as plaintext JSON-RPC) is explicitly
///     KEPT: <see cref="OnInboundBytesAsync"/> closes the session on ANY nonzero <c>enc</c>
///     rather than silently treating it as plaintext.
///   • SF-9 (plan-review correction): the CLI's "other lines (backend-originated requests...)
///     are ignored" comment (<c>BackendSession.cs:263</c>) is replaced with an explicit JSON-RPC
///     error reply (<c>-32601</c>) — a backend `sampling`/`elicitation`/`roots` request is
///     answered, never silently dropped. Acceptable for the CLI's single-consumer stdio client;
///     not for a shared cloud aggregator serving many consumer sessions.
///   • Ctor takes <see cref="SessionRoutingTable"/> + the sender's sentinel <see cref="NodeId"/>
///     (<c>SessionAdmission.AggregatorSentinelNodeId</c>) instead of an <c>IGatewayConnection</c>
///     — <see cref="SendLineAsync"/> builds a <see cref="RelayFrame"/> directly and forwards it
///     via <see cref="SessionRoutingTable.ForwardFrameAsync"/> (mirrors the plan's exact framing:
///     <c>Direction="client_to_server"</c>, <c>Enc=0</c>).
///   • <see cref="OnInboundBytesAsync"/> is async (not the CLI's synchronous <c>OnInboundBytes</c>)
///     so a backend-initiated-request error reply can be awaited inline — the CLI's manager ran a
///     dedicated single drain loop off any one caller's thread and used <c>Task.Run</c> to avoid
///     stalling it; here the caller is always <c>SpaceMcpAggregatorGrain.OnDeliveryAsync</c>
///     already running on the grain's own Orleans scheduler turn (a <c>[Reentrant]</c> grain call),
///     so awaiting directly is both simpler and correct.
///
/// Inbound bytes are fed in ONLY by <c>SpaceMcpAggregatorGrain.OnDeliveryAsync</c> (never read
/// directly here) — see the B1 plan-review correction on <see cref="ISpaceMcpAggregatorGrain"/>
/// for why that delivery path is mandatory to marshal onto the grain's scheduler.
/// </summary>
internal sealed class SpaceBackendSession
{
    // MCP protocol version advertised in the aggregator's OWN handshake toward each backend.
    // Independent of the external client's negotiated protocol version (N4, handled at the
    // grain's InitializeAsync level) — the aggregator is itself an MCP client to each backend.
    private const string McpProtocolVersion = "2025-06-18";

    /// <summary>
    /// Timeout for handshake operations: initialize, tools/list. S9: also the per-backend
    /// request timeout. <c>SpaceMcpAggregatorGrain</c>'s whole-open budget is longer because it
    /// also includes admission and an optional mobile wake wait.
    /// </summary>
    internal static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(20);

    /// <summary>Timeout for tools/call. Generous so legitimately long operations are not killed,
    /// but infinite hangs are bounded.</summary>
    internal static readonly TimeSpan ToolCallTimeout = TimeSpan.FromSeconds(300);

    private readonly SessionRoutingTable _routingTable;
    private readonly NodeId _senderSentinel;
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonRpcMessage>> _pending = new();

    // Decoded-but-unconsumed inbound text. Frames may carry partial lines or several
    // complete lines; we split on '\n' and keep the remainder here.
    private readonly StringBuilder _buffer = new();
    private readonly object _bufferLock = new();

    private long _reqId;
    private long _seq;

    public SpaceBackendSession(SessionRoutingTable routingTable, NodeId senderSentinel, string serverId, string slug, string sessionId)
    {
        _routingTable = routingTable;
        _senderSentinel = senderSentinel;
        ServerId = serverId;
        Slug = slug;
        SessionId = sessionId;
    }

    public string ServerId { get; }
    public string Slug { get; }
    public string SessionId { get; }
    private volatile bool _isAlive = true;
    public bool IsAlive => _isAlive;
    private volatile IReadOnlyList<ToolInfo> _tools = Array.Empty<ToolInfo>();
    public IReadOnlyList<ToolInfo> Tools => _tools;

    /// <summary>Raised when the backend sends notifications/tools/list_changed. Invoked
    /// synchronously — the caller (OnDeliveryAsync) already runs on the grain's own scheduler
    /// turn, unlike the CLI's dedicated drain-thread which needed Task.Run to avoid stalling.</summary>
    public event Action? ToolsChanged;

    /// <summary>
    /// Sends a JSON-RPC request and awaits the response, subject to <paramref name="timeout"/>.
    /// Throws <see cref="TimeoutException"/> if the backend does not reply within the window.
    /// </summary>
    public async Task<JsonRpcMessage> SendRequestAsync(
        string method, JsonObject? @params, CancellationToken ct, TimeSpan timeout)
    {
        var id = Interlocked.Increment(ref _reqId);
        var key = id.ToString();
        var tcs = new TaskCompletionSource<JsonRpcMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[key] = tcs;
        try
        {
            var line = JsonRpcMessage.Request(JsonValue.Create(id), method, @params);
            await SendLineAsync(line, ct);
            return await tcs.Task.WaitAsync(timeout, ct);
        }
        catch
        {
            _pending.TryRemove(key, out _);
            throw;
        }
    }

    public Task SendNotificationAsync(string method, JsonObject? @params, CancellationToken ct)
    {
        var line = JsonRpcMessage.Notification(method, @params);
        return SendLineAsync(line, ct);
    }

    private async Task SendLineAsync(string line, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(line + "\n");
        var seq = (ulong)Interlocked.Increment(ref _seq);
        var frame = new RelayFrame
        {
            SessionId = SessionId,
            SequenceNumber = seq,
            Direction = "client_to_server",
            Ciphertext = ByteString.CopyFrom(bytes),
            Enc = 0,
        };
        // SF-1 (adversarial review): the forwarding result was previously discarded, so a missing
        // route silently left the caller waiting out the full handshake/tool timeout. Use the
        // detailed outcome here: fail fast for every non-delivery, but authorize an automatic
        // retry only when the routing layer proves that no transport attempted the frame.
        var outcome = await _routingTable.ForwardFrameWithOutcomeAsync(_senderSentinel, frame, ct);
        if (outcome != FrameForwardOutcome.Delivered)
        {
            OnClosed("backend unreachable");
            if (outcome == FrameForwardOutcome.PeerUnavailable)
            {
                // No transport attempted delivery. Callers may safely open a fresh session and
                // retry without risking duplicate tool execution.
                throw new BackendRequestNotDeliveredException();
            }

            // Policy rejection and ambiguous gRPC/NATS failures must never enter the automatic
            // retry path. The backend may have accepted a frame whose acknowledgement was lost.
            throw new InvalidOperationException($"backend frame delivery failed: {outcome}");
        }
    }

    /// <summary>
    /// Feed raw inbound frame bytes (newline-delimited JSON-RPC), called ONLY from
    /// <c>SpaceMcpAggregatorGrain.OnDeliveryAsync</c>.
    /// </summary>
    public async Task OnInboundBytesAsync(byte[] bytes, uint enc, CancellationToken ct)
    {
        if (enc != 0)
        {
            // N3: fail closed. Space-MCP backend sessions are always plaintext (enc=0, forced
            // peer_supports_e2e=false at admission — there is no cipher to ever install here).
            // A nonzero enc is either a protocol error or an injection attempt; never parse it
            // as plaintext JSON-RPC.
            OnClosed("e2e protocol error (unsupported enc, Space-MCP backend sessions are always plaintext)");
            return;
        }

        var lines = new List<string>();
        lock (_bufferLock)
        {
            _buffer.Append(Encoding.UTF8.GetString(bytes));
            var text = _buffer.ToString();
            int start = 0;
            int nl;
            while ((nl = text.IndexOf('\n', start)) >= 0)
            {
                lines.Add(text.Substring(start, nl - start));
                start = nl + 1;
            }
            _buffer.Clear();
            if (start < text.Length) _buffer.Append(text, start, text.Length - start);
        }

        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            JsonRpcMessage m;
            try { m = JsonRpcMessage.Parse(line); }
            catch { continue; } // skip malformed line

            if (m.IsResponse && m.IdAsString is { } id)
            {
                if (_pending.TryRemove(id, out var tcs)) tcs.TrySetResult(m);
            }
            else if (m.IsNotification && m.Method == "notifications/tools/list_changed")
            {
                ToolsChanged?.Invoke();
            }
            else if (m.Method is not null && m.Id is not null)
            {
                // SF-9 (plan-review correction): a backend-initiated request (sampling/
                // elicitation/roots/etc) is answered with a JSON-RPC error, never silently
                // ignored — the CLI aggregator's "ignored" behaviour is acceptable for its
                // single-consumer stdio client, not for a shared cloud service.
                var error = JsonRpcMessage.Error(m.Id, -32601,
                    "server-initiated requests are not supported by the Korat aggregator");
                await SendLineAsync(error, ct);
            }
            // Other notifications are ignored.
        }
    }

    /// <summary>Mark the session dead and fault any in-flight requests.</summary>
    public void OnClosed(string? reason)
    {
        _isAlive = false;
        foreach (var kv in _pending)
        {
            if (_pending.TryRemove(kv.Key, out var tcs))
                tcs.TrySetException(new InvalidOperationException(
                    $"session closed{(string.IsNullOrEmpty(reason) ? "" : $": {reason}")}"));
        }
    }

    public async Task InitializeAsync(CancellationToken ct, TimeSpan? handshakeTimeout = null)
    {
        var timeout = handshakeTimeout ?? HandshakeTimeout;
        var @params = new JsonObject
        {
            ["protocolVersion"] = McpProtocolVersion,
            ["capabilities"] = new JsonObject(),
            ["clientInfo"] = new JsonObject
            {
                ["name"] = "korat-space",
                ["version"] = "1",
            },
        };
        _ = await SendRequestAsync("initialize", @params, ct, timeout);
        await SendNotificationAsync("notifications/initialized", null, ct);
    }

    public async Task<IReadOnlyList<ToolInfo>> ListToolsAsync(CancellationToken ct, TimeSpan? handshakeTimeout = null)
    {
        var timeout = handshakeTimeout ?? HandshakeTimeout;
        var resp = await SendRequestAsync("tools/list", null, ct, timeout);
        var result = JsonNode.Parse(resp.Raw())!["result"];
        var arr = result?["tools"] as JsonArray;
        var list = new List<ToolInfo>();
        if (arr is not null)
        {
            foreach (var node in arr)
            {
                if (node is not JsonObject t) continue;
                var name = t["name"]?.GetValue<string>();
                if (string.IsNullOrEmpty(name)) continue;
                var description = t["description"]?.GetValue<string>();
                var schema = (t["inputSchema"] as JsonObject)?.DeepClone() as JsonObject;
                list.Add(new ToolInfo(
                    ToolNamespacer.Namespaced(Slug, name),
                    name,
                    Slug,
                    schema,
                    description));
            }
        }
        _tools = list;
        return list;
    }
}

/// <summary>
/// The relay route was absent, so the request frame was not delivered to the backend. This is the
/// only tools/call failure that is safe to retry automatically on a newly opened backend session.
/// </summary>
internal sealed class BackendRequestNotDeliveredException()
    : InvalidOperationException("backend request was not delivered");
