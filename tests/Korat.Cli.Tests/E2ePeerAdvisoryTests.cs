using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Google.Protobuf;
using Korat.Cli.Commands;
using Korat.Cli.Mcp.Aggregation;
using Korat.Relay.V1;
using Xunit;

/// <summary>
/// Deferred-fix (latency): the agent consumes the cloud's advisory
/// <c>SessionOpened.peer_supports_e2e</c> flag.
///
///   - prefer + EXPLICIT peer-unsupported → no E2eKeyOffer is sent, no handshake-timeout
///     wait, session proceeds plaintext immediately;
///   - require + EXPLICIT peer-unsupported → fail-closed immediately (no offer, no wait);
///   - flag ABSENT (old cloud, proto3 presence not set) → behavior unchanged: the offer
///     IS sent and the handshake outcome stays authoritative.
/// </summary>
public class E2ePeerAdvisoryTests
{
    // Generous guard, but far below the 10s/20s production handshake timeouts: if the agent
    // ever waits for a handshake despite an explicit peer-unsupported advisory, the
    // .WaitAsync(Guard) below times the test out instead of silently passing.
    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(5);

    // ── Advisory extraction (presence semantics) ─────────────────────────────────────────────

    [Fact]
    public void Advisory_FieldAbsent_IsNull_NotFalse()
    {
        // Old cloud: field never set. proto3 default would read as false — presence must
        // distinguish "cloud didn't tell us" (null) from "cloud says unsupported" (false).
        var opened = new SessionOpened { RequestId = "r", SessionId = "s" };
        Assert.Null(ConnectCommand.GetPeerSupportsE2eAdvisory(opened));

        // And survive a wire round-trip (absent stays absent).
        var restored = SessionOpened.Parser.ParseFrom(opened.ToByteArray());
        Assert.Null(ConnectCommand.GetPeerSupportsE2eAdvisory(restored));
    }

    [Fact]
    public void Advisory_ExplicitFalse_SurvivesWireRoundTrip()
    {
        // New cloud explicitly stamps false → presence-aware serialization keeps it on the wire.
        var opened = new SessionOpened { RequestId = "r", SessionId = "s", PeerSupportsE2E = false };
        var restored = SessionOpened.Parser.ParseFrom(opened.ToByteArray());
        Assert.False(ConnectCommand.GetPeerSupportsE2eAdvisory(restored));
    }

    [Fact]
    public void ShouldSkipE2eOffer_OnlyOnExplicitFalse()
    {
        Assert.False(ConnectCommand.ShouldSkipE2eOffer(null));   // old cloud → keep offer path
        Assert.False(ConnectCommand.ShouldSkipE2eOffer(true));   // supported → keep offer path
        Assert.True(ConnectCommand.ShouldSkipE2eOffer(false));   // explicit unsupported → skip
    }

    // ── Aggregator behavior (BackendSessionManager over a fake connection) ───────────────────

    [Fact]
    public async Task Prefer_PeerExplicitlyUnsupported_NoOffer_NoWait_Plaintext()
    {
        // Fake never answers an offer: if one were sent, OpenAsync would stall for the full
        // handshake timeout and trip the Guard. Explicit advisory must avoid that entirely.
        var fake = new AdvisoryFakeGatewayConnection { PeerSupportsE2eAdvisory = false, AnswerOffers = false };
        await using var mgr = new BackendSessionManager(fake, agentClientId: "ag1",
            e2ePolicy: ConnectCommand.E2ePolicy.Prefer);

        var tools = await mgr.OpenAsync(new ServerDescriptor("s1", "GitHub", true), "github", default)
            .WaitAsync(Guard);

        Assert.Equal(0, fake.OfferCount);                                  // no E2eKeyOffer sent
        Assert.Contains(tools, t => t.NamespacedName == "github__create_issue"); // plaintext session works
    }

    [Fact]
    public async Task Require_PeerExplicitlyUnsupported_FailsClosedFast_NoOffer()
    {
        var fake = new AdvisoryFakeGatewayConnection { PeerSupportsE2eAdvisory = false, AnswerOffers = false };
        await using var mgr = new BackendSessionManager(fake, agentClientId: "ag1",
            e2ePolicy: ConnectCommand.E2ePolicy.Require);

        // Fail-closed immediately (no offer, no handshake-timeout wait before the throw).
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            mgr.OpenAsync(new ServerDescriptor("s1", "GitHub", true), "github", default)).WaitAsync(Guard);

        Assert.Equal(0, fake.OfferCount);
    }

    [Fact]
    public async Task AdvisoryAbsent_OldCloud_OfferStillSent_PlaintextFallbackPreserved()
    {
        // Back-compat: absence must NOT be treated as "unsupported" — the offer path runs
        // exactly as before (fake declines with E2eNotSupported → plaintext fallback).
        var fake = new AdvisoryFakeGatewayConnection { PeerSupportsE2eAdvisory = null, AnswerOffers = true };
        await using var mgr = new BackendSessionManager(fake, agentClientId: "ag1",
            e2ePolicy: ConnectCommand.E2ePolicy.Prefer);

        var tools = await mgr.OpenAsync(new ServerDescriptor("s1", "GitHub", true), "github", default)
            .WaitAsync(Guard);

        Assert.Equal(1, fake.OfferCount);                                  // current behavior preserved
        Assert.Contains(tools, t => t.NamespacedName == "github__create_issue");
    }

    // ── Fake gateway connection ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Like BackendSessionManagerTests.FakeGatewayConnection, plus:
    ///   - <see cref="PeerSupportsE2eAdvisory"/>: null → SessionOpened WITHOUT the field
    ///     (old cloud); true/false → field explicitly stamped (new cloud);
    ///   - <see cref="AnswerOffers"/>: false → a sent offer is black-holed (never answered),
    ///     so any accidental offer turns into a Guard timeout;
    ///   - <see cref="OfferCount"/>: number of E2eKeyOffer sends observed.
    /// </summary>
    private sealed class AdvisoryFakeGatewayConnection : IGatewayConnection
    {
        private readonly Channel<GatewayToNodeMessage> _in = Channel.CreateUnbounded<GatewayToNodeMessage>();
        public ChannelReader<GatewayToNodeMessage> IncomingMessages => _in.Reader;

        public bool? PeerSupportsE2eAdvisory { get; init; }
        public bool AnswerOffers { get; init; } = true;
        public int OfferCount { get; private set; }

        public Task SendRequestSessionAsync(string requestId, string agentClientId, string mcpServerId, CancellationToken ct = default)
        {
            var opened = new SessionOpened { RequestId = requestId, SessionId = $"sess-{mcpServerId}" };
            if (PeerSupportsE2eAdvisory is { } advisory)
                opened.PeerSupportsE2E = advisory; // explicit presence
            _in.Writer.TryWrite(new GatewayToNodeMessage { SessionOpened = opened });
            return Task.CompletedTask;
        }

        public Task SendCloseSessionAsync(string sessionId, string reason, CancellationToken ct = default) => Task.CompletedTask;

        public Task SendFrameAsync(string sessionId, ReadOnlyMemory<byte> ciphertext, ulong seq, string direction, CancellationToken ct = default)
        {
            var text = Encoding.UTF8.GetString(ciphertext.Span).TrimEnd('\n');
            // Auto-reply to any JSON-RPC request (has an id) so initialize/tools-list complete.
            var node = JsonNode.Parse(text)!.AsObject();
            if (node.TryGetPropertyValue("id", out var idNode) && idNode is not null
                && node.TryGetPropertyValue("method", out var mNode))
            {
                JsonObject result = mNode!.GetValue<string>() switch
                {
                    "tools/list" => new JsonObject
                    {
                        ["tools"] = new JsonArray(new JsonObject
                        {
                            ["name"] = "create_issue",
                            ["description"] = "desc",
                            ["inputSchema"] = new JsonObject { ["type"] = "object" }
                        })
                    },
                    _ => new JsonObject { ["ok"] = true },
                };
                var reply = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = idNode.DeepClone(), ["result"] = result };
                _in.Writer.TryWrite(new GatewayToNodeMessage
                {
                    Frame = new RelayFrame
                    {
                        SessionId = sessionId,
                        Ciphertext = ByteString.CopyFrom(Encoding.UTF8.GetBytes(reply.ToJsonString() + "\n")),
                        Direction = "server_to_client"
                    }
                });
            }
            return Task.CompletedTask;
        }

        public Task SendHeartbeatAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task SendE2eFrameAsync(string sessionId, ReadOnlyMemory<byte> wirePayload, ulong sequenceNumber, string direction, FrameMetadata meta, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task SendE2eKeyOfferAsync(string sessionId, uint version, string curve, byte[] pubKey, byte[] salt, CancellationToken ct = default)
        {
            OfferCount++;
            if (AnswerOffers)
            {
                _in.Writer.TryWrite(new GatewayToNodeMessage
                {
                    E2ENotSupported = new E2eNotSupported { SessionId = sessionId, Reason = "test-fake" }
                });
            }
            return Task.CompletedTask;
        }

        public Task SendE2eKeyConfirmAsync(string sessionId, byte[] confirmTag, CancellationToken ct = default)
            => Task.CompletedTask;
    }
}
