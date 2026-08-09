using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Google.Protobuf;
using Korat.Cli.Mcp.Aggregation;
using Korat.Relay.V1;
using Xunit;
using Korat.Mcp;

/// <summary>
/// Tests for the three timeout / resilience fixes:
///   1. tools/call hangs → TimeoutException within bounded window
///   2. Bring-up with one silent server → other servers still open, bridge reaches ready
///   3. RunAsync client loop: a throwing handler → loop survives and processes next message
/// </summary>
public class AggregatorTimeoutTests
{
    // ── Shared fakes ─────────────────────────────────────────────────────────

    /// <summary>
    /// Gateway that grants sessions immediately but NEVER sends back any frames
    /// (simulates a backend that accepted the session then went silent).
    /// </summary>
    private sealed class SilentGatewayConnection : IGatewayConnection
    {
        private readonly Channel<GatewayToNodeMessage> _in = Channel.CreateUnbounded<GatewayToNodeMessage>();
        public ChannelReader<GatewayToNodeMessage> IncomingMessages => _in.Reader;

        public Task SendRequestSessionAsync(string requestId, string agentClientId, string mcpServerId, CancellationToken ct = default)
        {
            // Grant the session so the caller proceeds past RequestSessionAndAwaitAsync,
            // but then never reply to the subsequent initialize/tools/list/tools/call frame.
            _in.Writer.TryWrite(new GatewayToNodeMessage
            {
                SessionOpened = new SessionOpened { RequestId = requestId, SessionId = $"sess-{mcpServerId}" }
            });
            return Task.CompletedTask;
        }

        public Task SendFrameAsync(string sessionId, ReadOnlyMemory<byte> ciphertext, ulong seq, string direction, CancellationToken ct = default)
            => Task.CompletedTask; // frames disappear — never echoed back

        public Task SendHeartbeatAsync(CancellationToken ct = default) => Task.CompletedTask;

        // 031 (MAJOR-3): never replies — E2E handshake will time out (plaintext fallback).
        public Task SendE2eFrameAsync(string sessionId, ReadOnlyMemory<byte> wirePayload, ulong sequenceNumber, string direction, Korat.Relay.V1.FrameMetadata meta, CancellationToken ct = default)
            => Task.CompletedTask;
        public Task SendE2eKeyOfferAsync(string sessionId, uint version, string curve, byte[] pubKey, byte[] salt, CancellationToken ct = default)
            => Task.CompletedTask; // never replies to offer → timeout → plaintext fallback
        public Task SendE2eKeyConfirmAsync(string sessionId, byte[] confirmTag, CancellationToken ct = default)
            => Task.CompletedTask;
        public Task SendCloseSessionAsync(string sessionId, string reason, CancellationToken ct = default)
            => Task.CompletedTask;
    }

    /// <summary>
    /// Gateway that:
    ///   - for serverId == SilentServerId: grants session, then never replies to any frame
    ///   - for everything else: grants session AND auto-replies to JSON-RPC requests
    /// </summary>
    private sealed class MixedGatewayConnection : IGatewayConnection
    {
        public const string SilentServerId = "silent-server";

        private readonly Channel<GatewayToNodeMessage> _in = Channel.CreateUnbounded<GatewayToNodeMessage>();
        public ChannelReader<GatewayToNodeMessage> IncomingMessages => _in.Reader;

        public Task SendRequestSessionAsync(string requestId, string agentClientId, string mcpServerId, CancellationToken ct = default)
        {
            _in.Writer.TryWrite(new GatewayToNodeMessage
            {
                SessionOpened = new SessionOpened { RequestId = requestId, SessionId = $"sess-{mcpServerId}" }
            });
            return Task.CompletedTask;
        }

        public Task SendFrameAsync(string sessionId, ReadOnlyMemory<byte> ciphertext, ulong seq, string direction, CancellationToken ct = default)
        {
            // If the session belongs to the silent server, drop the frame.
            if (sessionId == $"sess-{SilentServerId}")
                return Task.CompletedTask;

            var text = Encoding.UTF8.GetString(ciphertext.Span).TrimEnd('\n');
            var node = JsonNode.Parse(text)!.AsObject();
            if (node.TryGetPropertyValue("id", out var idNode) && idNode is not null &&
                node.TryGetPropertyValue("method", out var mNode))
            {
                var method = mNode!.GetValue<string>();
                JsonObject result = method == "tools/list"
                    ? new JsonObject
                    {
                        ["tools"] = new JsonArray(
                            (JsonNode)new JsonObject
                            {
                                ["name"] = "do_thing",
                                ["description"] = "d",
                                ["inputSchema"] = new JsonObject { ["type"] = "object" },
                            })
                    }
                    : new JsonObject { ["ok"] = true };
                var reply = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = idNode.DeepClone(), ["result"] = result };
                _in.Writer.TryWrite(new GatewayToNodeMessage
                {
                    Frame = new RelayFrame
                    {
                        SessionId = sessionId,
                        Ciphertext = ByteString.CopyFrom(Encoding.UTF8.GetBytes(reply.ToJsonString() + "\n")),
                    }
                });
            }
            return Task.CompletedTask;
        }

        public Task SendHeartbeatAsync(CancellationToken ct = default) => Task.CompletedTask;

        // 031 (MAJOR-3): E2E stubs — reply E2eNotSupported so handshake falls back to plaintext.
        public Task SendE2eFrameAsync(string sessionId, ReadOnlyMemory<byte> wirePayload, ulong sequenceNumber, string direction, Korat.Relay.V1.FrameMetadata meta, CancellationToken ct = default)
            => Task.CompletedTask;
        public Task SendE2eKeyOfferAsync(string sessionId, uint version, string curve, byte[] pubKey, byte[] salt, CancellationToken ct = default)
        {
            _in.Writer.TryWrite(new GatewayToNodeMessage
            {
                E2ENotSupported = new E2eNotSupported { SessionId = sessionId, Reason = "test-mixed" }
            });
            return Task.CompletedTask;
        }
        public Task SendE2eKeyConfirmAsync(string sessionId, byte[] confirmTag, CancellationToken ct = default)
            => Task.CompletedTask;
        public Task SendCloseSessionAsync(string sessionId, string reason, CancellationToken ct = default)
            => Task.CompletedTask;
    }

    // ── Fix 1: tools/call timeout ─────────────────────────────────────────────

    [Fact]
    public async Task CallAsync_times_out_when_backend_never_replies()
    {
        // Use a tiny injected timeout (50 ms) so the test runs fast.
        var tinyTimeout = TimeSpan.FromMilliseconds(50);

        var nrGateway = new NeverReplyToCallGateway();
        await using var mgr3 = new BackendSessionManager(
            nrGateway, agentClientId: "ag1",
            handshakeTimeout: TimeSpan.FromSeconds(5),
            toolCallTimeout: tinyTimeout);

        var toolsFromNr = await mgr3.OpenAsync(new ServerDescriptor("srv", "Srv", true), "srv", default)
            .WaitAsync(TimeSpan.FromSeconds(5));
        Assert.NotEmpty(toolsFromNr);

        // tools/call should throw TimeoutException within a second (timeout=50ms + overhead).
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await Assert.ThrowsAsync<TimeoutException>(() =>
            mgr3.CallAsync("srv__do_thing", "{}", JsonValue.Create(1), default)
                .WaitAsync(TimeSpan.FromSeconds(3))); // test guard
        sw.Stop();

        // Must complete well under 1 second — not multi-second wait.
        Assert.True(sw.ElapsedMilliseconds < 1000,
            $"Expected timeout well under 1s but took {sw.ElapsedMilliseconds}ms");
    }

    /// <summary>Gateway that replies to initialize/tools/list but silently drops tools/call frames.</summary>
    private sealed class NeverReplyToCallGateway : IGatewayConnection
    {
        private readonly Channel<GatewayToNodeMessage> _in = Channel.CreateUnbounded<GatewayToNodeMessage>();
        public ChannelReader<GatewayToNodeMessage> IncomingMessages => _in.Reader;

        public Task SendRequestSessionAsync(string requestId, string agentClientId, string mcpServerId, CancellationToken ct = default)
        {
            _in.Writer.TryWrite(new GatewayToNodeMessage
            {
                SessionOpened = new SessionOpened { RequestId = requestId, SessionId = $"sess-{mcpServerId}" }
            });
            return Task.CompletedTask;
        }

        public Task SendFrameAsync(string sessionId, ReadOnlyMemory<byte> ciphertext, ulong seq, string direction, CancellationToken ct = default)
        {
            var text = Encoding.UTF8.GetString(ciphertext.Span).TrimEnd('\n');
            var node = JsonNode.Parse(text)!.AsObject();
            if (!node.TryGetPropertyValue("id", out var idNode) || idNode is null) return Task.CompletedTask;
            if (!node.TryGetPropertyValue("method", out var mNode)) return Task.CompletedTask;
            var method = mNode!.GetValue<string>();

            // Drop tools/call — the caller will time out.
            if (method == "tools/call") return Task.CompletedTask;

            JsonObject result = method == "tools/list"
                ? new JsonObject
                {
                    ["tools"] = new JsonArray(
                        (JsonNode)new JsonObject
                        {
                            ["name"] = "do_thing",
                            ["description"] = "d",
                            ["inputSchema"] = new JsonObject { ["type"] = "object" },
                        })
                }
                : new JsonObject { ["ok"] = true };

            var reply = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = idNode.DeepClone(), ["result"] = result };
            _in.Writer.TryWrite(new GatewayToNodeMessage
            {
                Frame = new RelayFrame
                {
                    SessionId = sessionId,
                    Ciphertext = ByteString.CopyFrom(Encoding.UTF8.GetBytes(reply.ToJsonString() + "\n")),
                }
            });
            return Task.CompletedTask;
        }

        public Task SendHeartbeatAsync(CancellationToken ct = default) => Task.CompletedTask;

        // 031 (MAJOR-3): E2E stubs — reply E2eNotSupported so handshake falls back to plaintext.
        public Task SendE2eFrameAsync(string sessionId, ReadOnlyMemory<byte> wirePayload, ulong sequenceNumber, string direction, Korat.Relay.V1.FrameMetadata meta, CancellationToken ct = default)
            => Task.CompletedTask;
        public Task SendE2eKeyOfferAsync(string sessionId, uint version, string curve, byte[] pubKey, byte[] salt, CancellationToken ct = default)
        {
            _in.Writer.TryWrite(new GatewayToNodeMessage
            {
                E2ENotSupported = new E2eNotSupported { SessionId = sessionId, Reason = "test-neverreplytocall" }
            });
            return Task.CompletedTask;
        }
        public Task SendE2eKeyConfirmAsync(string sessionId, byte[] confirmTag, CancellationToken ct = default)
            => Task.CompletedTask;
        public Task SendCloseSessionAsync(string sessionId, string reason, CancellationToken ct = default)
            => Task.CompletedTask;
    }

    // ── Leak regression: failed OpenAsync must not grow _sessionsById ────────────

    [Fact]
    public async Task OpenAsync_handshake_timeout_does_not_leak_session_into_manager()
    {
        // SilentGatewayConnection grants the session but never replies to initialize frames,
        // so every OpenAsync will time out during the handshake.
        var tinyHandshake = TimeSpan.FromMilliseconds(50);
        var silent = new SilentGatewayConnection();
        await using var mgr = new BackendSessionManager(
            silent, agentClientId: "ag1",
            handshakeTimeout: tinyHandshake,
            toolCallTimeout: BackendSession.ToolCallTimeout);

        var server = new ServerDescriptor("silent-server", "Silent", true);

        // First attempt: must throw (timeout) and must not leave a session in the manager.
        await Assert.ThrowsAnyAsync<Exception>(() =>
            mgr.OpenAsync(server, "slug1", default).WaitAsync(TimeSpan.FromSeconds(2)));

        Assert.Equal(0, mgr.SessionCount);

        // Second attempt (simulates a reconcile tick): still must not accumulate.
        await Assert.ThrowsAnyAsync<Exception>(() =>
            mgr.OpenAsync(server, "slug1", default).WaitAsync(TimeSpan.FromSeconds(2)));

        Assert.Equal(0, mgr.SessionCount);

        // A follow-up tools/call for the slug must fail fast with "server unavailable"
        // rather than routing to a dead session.
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            mgr.CallAsync("slug1__any_tool", "{}", JsonValue.Create(1), default));
        Assert.Contains("server unavailable", ex.Message);
    }

    // ── Fix 2: bring-up isolation ─────────────────────────────────────────────

    [Fact]
    public async Task BringUp_one_silent_server_does_not_prevent_other_servers_from_opening()
    {
        // Tiny handshake timeout so the silent server's OpenAsync fails quickly.
        var tinyHandshake = TimeSpan.FromMilliseconds(100);
        var conn = new MixedGatewayConnection();
        await using var mgr = new BackendSessionManager(
            conn, agentClientId: "ag1",
            handshakeTimeout: tinyHandshake,
            toolCallTimeout: BackendSession.ToolCallTimeout);

        var catalog = new AggregateCatalog();
        var changes = 0;

        var silentServer   = new ServerDescriptor(MixedGatewayConnection.SilentServerId, "Silent", true);
        var respondingServer = new ServerDescriptor("good-server", "Good", true);

        var baseline = new SpaceSnapshot(Array.Empty<ServerDescriptor>(), Array.Empty<ServerDescriptor>());
        var watcher = new SpaceWatcher(
            discover: _ => Task.FromResult(baseline),
            sessions: mgr,
            catalog: catalog,
            onChanged: _ => { changes++; return Task.CompletedTask; },
            baseline: baseline);

        var cur = new SpaceSnapshot(
            new[] { silentServer, respondingServer },
            Array.Empty<ServerDescriptor>());

        // ReconcileAsync must complete in a reasonable time — guard at 5s.
        // The silent server times out after 100ms handshake; the good server opens fine.
        var changed = await watcher.ReconcileAsync(cur, default).WaitAsync(TimeSpan.FromSeconds(5));

        // The catalog should have been changed by the good server opening.
        Assert.True(changed, "catalog must have changed because good-server opened");
        Assert.Equal(1, changes);

        // Good server's tool appears in catalog.
        var toolsJson = JsonNode.Parse(catalog.ToolsListJson())!["tools"]!.AsArray();
        Assert.Contains(toolsJson, t => t!["name"]!.GetValue<string>().StartsWith("good-server__") ||
                                        t!["name"]!.GetValue<string>().Contains("do_thing"));

        // Silent server's tool must NOT appear — its open timed out.
        Assert.DoesNotContain(toolsJson, t =>
        {
            var n = t!["name"]?.GetValue<string>() ?? "";
            return n.Contains(MixedGatewayConnection.SilentServerId);
        });
    }

    // ── Fix 3: RunAsync client loop catch-all ─────────────────────────────────

    /// <summary>
    /// IBackendSessions fake that throws on the first call, then succeeds.
    /// Used to verify RunAsync's loop survives a throwing handler.
    /// </summary>
    private sealed class ThrowOnceSessions : IBackendSessions
    {
        private int _callCount;
        public string GoodReturn = """{"jsonrpc":"2.0","id":99,"result":{"content":[{"type":"text","text":"ok"}]}}""";
        public AccessRequestResult AccessResult = new(false, "ar-1");

        public Task<string> CallAsync(string namespacedName, string argsJson, JsonNode idNode, CancellationToken ct)
        {
            var n = System.Threading.Interlocked.Increment(ref _callCount);
            if (n == 1) throw new InvalidOperationException("simulated handler crash");
            return Task.FromResult(GoodReturn);
        }

        public Task<AccessRequestResult> RequestAccessAsync(string serverId, CancellationToken ct)
            => Task.FromResult(AccessResult);
    }

    private static AggregateCatalog CatalogWithTool(string namespacedName, string slug)
    {
        var cat = new AggregateCatalog();
        cat.SetGranted("s1", slug, "Server", new[]
        {
            new ToolInfo(namespacedName, namespacedName.Split("__")[1], slug,
                (JsonObject)JsonNode.Parse("""{"type":"object"}""")!, "test tool"),
        });
        return cat;
    }

    [Fact]
    public async Task RunAsync_handler_throws_writes_error_and_loop_continues_for_next_message()
    {
        var output = new StringWriter();
        var sessions = new ThrowOnceSessions();
        var catalog = CatalogWithTool("srv__my_tool", "srv");
        var server = new AggregatorMcpServer(catalog, sessions, output, "test");

        // Two identical tool calls: first throws, second succeeds.
        var input = new StringReader(string.Join("\n",
            """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"srv__my_tool","arguments":{}}}""",
            """{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"srv__my_tool","arguments":{}}}""")
            + "\n");

        await server.RunAsync(input, default);

        var lines = output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => JsonNode.Parse(l)!.AsObject()).ToList();

        JsonObject ById(int id) => lines.First(o => o["id"]?.GetValue<int>() == id);

        // First call: the handler threw → must get a JSON-RPC error (not silence / not crash).
        var first = ById(1);
        Assert.NotNull(first["error"]);
        Assert.Equal(-32603, first["error"]!["code"]!.GetValue<int>());
        Assert.Contains("crash", first["error"]!["message"]!.GetValue<string>());

        // Second call: loop survived, handler returned ok.
        var second = ById(2);
        Assert.NotNull(second["result"]);
    }

    [Fact]
    public async Task RunAsync_unknown_method_exception_does_not_crash_loop()
    {
        // Regression guard: a future unknown path that somehow throws should not
        // kill the loop before the next message is processed.
        var output = new StringWriter();
        var sessions = new ThrowOnceSessions();
        var catalog = CatalogWithTool("srv__my_tool", "srv");
        var server = new AggregatorMcpServer(catalog, sessions, output, "test");

        // First message is a known tool (throws on first call), second is a tools/list (always ok).
        var input = new StringReader(string.Join("\n",
            """{"jsonrpc":"2.0","id":10,"method":"tools/call","params":{"name":"srv__my_tool","arguments":{}}}""",
            """{"jsonrpc":"2.0","id":11,"method":"tools/list"}""")
            + "\n");

        await server.RunAsync(input, default);

        var lines = output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => JsonNode.Parse(l)!.AsObject()).ToList();

        // id=10 got an error (handler threw)
        Assert.Contains(lines, l => l["id"]?.GetValue<int>() == 10 && l["error"] is not null);
        // id=11 got a result (loop survived and processed next message)
        Assert.Contains(lines, l => l["id"]?.GetValue<int>() == 11 && l["result"] is not null);
    }
}
