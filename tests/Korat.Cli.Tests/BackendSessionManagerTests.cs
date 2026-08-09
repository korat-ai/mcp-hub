using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Google.Protobuf;
using Korat.Cli.Mcp.Aggregation;
using Korat.Relay.V1;
using Xunit;

public class BackendSessionManagerTests
{
    // Fake: backs IncomingMessages with an unbounded channel; records sent frames;
    // auto-replies to JSON-RPC requests so OpenAsync/CallAsync don't hang.
    private sealed class FakeGatewayConnection : IGatewayConnection
    {
        private readonly Channel<GatewayToNodeMessage> _in = Channel.CreateUnbounded<GatewayToNodeMessage>();
        public ChannelReader<GatewayToNodeMessage> IncomingMessages => _in.Reader;
        public List<(string SessionId, string Text)> SentFrames { get; } = new();
        public string[] ToolsToReturn = { "create_issue" };

        public Task SendRequestSessionAsync(string requestId, string agentClientId, string mcpServerId, CancellationToken ct = default)
        {
            // Grant immediately: SessionOpened correlated by requestId, session id = "sess-<serverId>".
            _in.Writer.TryWrite(new GatewayToNodeMessage
            {
                SessionOpened = new SessionOpened { RequestId = requestId, SessionId = $"sess-{mcpServerId}" }
            });
            return Task.CompletedTask;
        }

        public Task SendFrameAsync(string sessionId, ReadOnlyMemory<byte> ciphertext, ulong seq, string direction, CancellationToken ct = default)
        {
            var text = Encoding.UTF8.GetString(ciphertext.Span).TrimEnd('\n');
            SentFrames.Add((sessionId, text));
            // Auto-reply to any JSON-RPC *request* (has an id). Notifications (no id) get no reply.
            var node = JsonNode.Parse(text)!.AsObject();
            if (node.TryGetPropertyValue("id", out var idNode) && idNode is not null && node.TryGetPropertyValue("method", out var mNode))
            {
                var method = mNode!.GetValue<string>();
                JsonObject result = method switch
                {
                    "tools/list" => new JsonObject
                    {
                        ["tools"] = new JsonArray(ToolsToReturn.Select(t => (JsonNode)new JsonObject
                        {
                            ["name"] = t,
                            ["description"] = $"desc {t}",
                            ["inputSchema"] = new JsonObject { ["type"] = "object" }
                        }).ToArray())
                    },
                    _ => new JsonObject { ["ok"] = true },
                };
                var reply = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = idNode.DeepClone(), ["result"] = result };
                var bytes = Encoding.UTF8.GetBytes(reply.ToJsonString() + "\n");
                _in.Writer.TryWrite(new GatewayToNodeMessage
                {
                    Frame = new RelayFrame { SessionId = sessionId, Ciphertext = ByteString.CopyFrom(bytes), Direction = "server_to_client" }
                });
            }
            return Task.CompletedTask;
        }

        public Task SendHeartbeatAsync(CancellationToken ct = default) => Task.CompletedTask;

        // 031 (MAJOR-3): E2E stubs — reply E2eNotSupported so the handshake falls back to plaintext.
        public Task SendE2eFrameAsync(string sessionId, ReadOnlyMemory<byte> wirePayload, ulong sequenceNumber, string direction, Korat.Relay.V1.FrameMetadata meta, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task SendE2eKeyOfferAsync(string sessionId, uint version, string curve, byte[] pubKey, byte[] salt, CancellationToken ct = default)
        {
            _in.Writer.TryWrite(new GatewayToNodeMessage
            {
                E2ENotSupported = new E2eNotSupported { SessionId = sessionId, Reason = "test-fake" }
            });
            return Task.CompletedTask;
        }

        public Task SendE2eKeyConfirmAsync(string sessionId, byte[] confirmTag, CancellationToken ct = default)
            => Task.CompletedTask;
        public Task SendCloseSessionAsync(string sessionId, string reason, CancellationToken ct = default)
            => Task.CompletedTask;
    }

    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task Open_fetches_namespaced_tools_and_routes_call_to_session()
    {
        var fake = new FakeGatewayConnection();
        await using var mgr = new BackendSessionManager(fake, agentClientId: "ag1");

        var server = new ServerDescriptor("s1", "GitHub", true);
        var tools = await mgr.OpenAsync(server, "github", default).WaitAsync(Guard);

        Assert.Contains(tools, t => t.NamespacedName == "github__create_issue" && t.OriginalName == "create_issue");

        await mgr.CallAsync("github__create_issue", argsJson: "{}", idNode: JsonValue.Create(1), default).WaitAsync(Guard);
        Assert.Contains(fake.SentFrames, f => f.SessionId == "sess-s1" && f.Text.Contains("tools/call"));
    }

    [Fact]
    public async Task Initialize_handshake_sends_initialize_then_initialized_notification()
    {
        var fake = new FakeGatewayConnection();
        await using var mgr = new BackendSessionManager(fake, agentClientId: "ag1");
        await mgr.OpenAsync(new ServerDescriptor("s1", "GitHub", true), "github", default).WaitAsync(Guard);

        Assert.Contains(fake.SentFrames, f => f.Text.Contains("\"initialize\""));
        Assert.Contains(fake.SentFrames, f => f.Text.Contains("notifications/initialized"));
        Assert.Contains(fake.SentFrames, f => f.Text.Contains("tools/list"));
    }

    [Fact]
    public async Task CallAsync_to_unknown_slug_throws()
    {
        var fake = new FakeGatewayConnection();
        await using var mgr = new BackendSessionManager(fake, agentClientId: "ag1");

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            mgr.CallAsync("nope__do_thing", argsJson: "{}", idNode: JsonValue.Create(1), default));
    }

    [Fact]
    public async Task CallAsync_with_non_namespaced_name_throws()
    {
        var fake = new FakeGatewayConnection();
        await using var mgr = new BackendSessionManager(fake, agentClientId: "ag1");

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            mgr.CallAsync("notnamespaced", argsJson: "{}", idNode: JsonValue.Create(1), default));
    }
}
