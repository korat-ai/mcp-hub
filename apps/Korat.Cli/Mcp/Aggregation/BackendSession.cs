using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Korat.Mcp;
using Google.Protobuf;
using Korat.Protocol;
using Korat.Relay.V1;

namespace Korat.Cli.Mcp.Aggregation;

/// <summary>A single backend MCP tool as exposed through the aggregator (namespaced).</summary>
/// <summary>
/// One relay session to a single granted backend MCP server, multiplexed over the
/// shared <see cref="IGatewayConnection"/>. Owns the per-session JSON-RPC id space,
/// the monotonic frame sequence counter, the inbound line buffer, and the table of
/// in-flight requests awaiting a response.
///
/// Inbound bytes are pushed in by <see cref="BackendSessionManager"/>'s single drain
/// loop (never read directly here). Outbound frames are written via the connection.
/// </summary>
internal sealed class BackendSession
{
    // MCP protocol version advertised in the initialize handshake.
    // NOTE: may need adjustment during T12 live E2E if a real backend rejects it.
    private const string McpProtocolVersion = "2025-06-18";

    /// <summary>
    /// Timeout for handshake operations: session open, initialize, tools/list.
    /// These are expected to be fast; a slow one indicates a misbehaving backend.
    /// </summary>
    internal static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(20);

    /// <summary>
    /// Timeout for tools/call. Generous so legitimately long operations (e.g. a build
    /// via run_process) are not killed, but infinite hangs are bounded.
    /// Overridable via <c>KORAT_TOOL_TIMEOUT_SECONDS</c> environment variable.
    /// </summary>
    internal static readonly TimeSpan ToolCallTimeout = ResolveToolCallTimeout();

    private static TimeSpan ResolveToolCallTimeout()
    {
        var raw = Environment.GetEnvironmentVariable("KORAT_TOOL_TIMEOUT_SECONDS");
        if (int.TryParse(raw, out var seconds) && seconds >= 1)
            return TimeSpan.FromSeconds(seconds);
        return TimeSpan.FromSeconds(300);
    }

    private readonly IGatewayConnection _conn;
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonRpcMessage>> _pending = new();

    // Decoded-but-unconsumed inbound text. Frames may carry partial lines or several
    // complete lines; we split on '\n' and keep the remainder here.
    private readonly StringBuilder _buffer = new();
    private readonly object _bufferLock = new();

    private long _reqId;
    private long _seq;

    // 031 (MAJOR-3): per-session E2E cipher installed after handshake; null = plaintext.
    private E2eSessionCipher? _e2eCipher;
    private readonly object _cipherLock = new();

    public BackendSession(IGatewayConnection conn, string serverId, string slug, string sessionId)
    {
        _conn = conn;
        ServerId = serverId;
        Slug = slug;
        SessionId = sessionId;
    }

    /// <summary>
    /// 031 (MAJOR-3): installs the E2E cipher after a successful handshake.
    /// Thread-safe; called by the manager after EstablishAsync returns.
    /// </summary>
    internal void InstallCipher(E2eSessionCipher cipher)
    {
        lock (_cipherLock)
        {
            _e2eCipher?.Dispose();
            _e2eCipher = cipher;
        }
    }

    /// <summary>031 (MAJOR-3): returns the installed cipher, or null if plaintext.</summary>
    internal E2eSessionCipher? Cipher
    {
        get { lock (_cipherLock) return _e2eCipher; }
    }

    /// <summary>Dispose the cipher and mark the session closed.</summary>
    private void DisposeCipher()
    {
        lock (_cipherLock)
        {
            _e2eCipher?.Dispose();
            _e2eCipher = null;
        }
    }

    public string ServerId { get; }
    public string Slug { get; }
    public string SessionId { get; }
    private volatile bool _isAlive = true;
    public bool IsAlive => _isAlive;
    private volatile IReadOnlyList<ToolInfo> _tools = Array.Empty<ToolInfo>();
    public IReadOnlyList<ToolInfo> Tools => _tools;

    /// <summary>Raised when the backend sends notifications/tools/list_changed.</summary>
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

    private Task SendLineAsync(string line, CancellationToken ct)
    {
        var plaintext = Encoding.UTF8.GetBytes(line + "\n");
        var cipher = Cipher;
        if (cipher is not null)
        {
            // 031 (MAJOR-3): E2E path — Seal and send encrypted frame.
            var meta = FrameMetadataFactory.FromPlaintext(
                plaintext, E2eSessionCipher.DirectionClientToServer, (ulong)plaintext.Length);
            var metaBytes = meta.ToByteArray();
            var wirePayload = cipher.Seal(
                plaintext,
                E2eSessionCipher.DirClientToServer,
                metaBytes,
                out var seqUsed);
            return _conn.SendE2eFrameAsync(SessionId, wirePayload, seqUsed, E2eSessionCipher.DirectionClientToServer, meta, ct);
        }
        var seq = (ulong)Interlocked.Increment(ref _seq);
        return _conn.SendFrameAsync(SessionId, plaintext, seq, "client_to_server", ct);
    }

    /// <summary>
    /// Feed raw inbound frame bytes (newline-delimited JSON-RPC). Called only by the drain loop.
    /// 031 (MAJOR-3): enc==1 frames are decrypted with the session cipher before parsing.
    /// </summary>
    public void OnInboundBytes(byte[] bytes, uint enc = 0, Korat.Relay.V1.FrameMetadata? meta = null, ulong sequenceNumber = 0)
    {
        byte[] plaintext;
        var cipher = Cipher;
        if (enc == 1 && cipher is not null)
        {
            try
            {
                var metaBytes = meta?.ToByteArray() ?? Array.Empty<byte>();
                plaintext = cipher.Open(
                    bytes,
                    E2eSessionCipher.DirServerToClient,
                    sequenceNumber,
                    metaBytes);
            }
            catch (CryptographicException ex)
            {
                Console.Error.WriteLine(
                    $"[e2e] AEAD decryption failed session={SessionId}: {ex.Message} — closing session.");
                OnClosed("e2e decryption failed");
                return;
            }
        }
        else if (enc != 0 && cipher is not null)
        {
            // ANTI-DOWNGRADE (aggregator): enc!=0 with an installed cipher but not enc==1 = protocol error.
            Console.Error.WriteLine(
                $"[e2e] DOWNGRADE/INJECTION: session={SessionId} enc={enc} received after E2E established. Closing.");
            OnClosed("e2e downgrade/injection detected");
            return;
        }
        else if (cipher is not null && enc == 0)
        {
            // ANTI-DOWNGRADE (aggregator): plaintext frame received on an established E2E session.
            Console.Error.WriteLine(
                $"[e2e] DOWNGRADE/INJECTION: session={SessionId} plaintext (enc=0) received after E2E established. Closing.");
            OnClosed("e2e downgrade/injection detected");
            return;
        }
        else if (enc != 0)
        {
            // MAJOR-2 fix: enc!=0 with no installed cipher = protocol error / injection attempt.
            // Fail closed — never forward ciphertext/garbage as plaintext JSON-RPC.
            Console.Error.WriteLine(
                $"[e2e] PROTOCOL ERROR: session={SessionId} enc={enc} received but no E2E cipher installed. Closing (fail-closed).");
            OnClosed("e2e protocol error");
            return;
        }
        else
        {
            // enc==0, cipher==null: legitimate plaintext session (no E2E negotiated).
            plaintext = bytes;
        }

        var lines = new List<string>();
        lock (_bufferLock)
        {
            _buffer.Append(Encoding.UTF8.GetString(plaintext));
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
                // Raise off the drain thread: a slow or throwing subscriber must not
                // stall (or tear down) the manager's single demux loop.
                var handler = ToolsChanged;
                if (handler is not null)
                    _ = Task.Run(() => handler());
            }
            // Other lines (backend-originated requests, unknown notifications) are ignored.
        }
    }

    /// <summary>Mark the session dead, fault any in-flight requests, and zero the cipher.</summary>
    public void OnClosed(string? reason)
    {
        _isAlive = false;
        DisposeCipher();
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
                ["version"] = "0",
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
