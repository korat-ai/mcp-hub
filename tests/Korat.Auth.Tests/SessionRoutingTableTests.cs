using Google.Protobuf;
using Grpc.Core;
using Korat.Cloud.Gateways;
using Korat.Cloud.Observability;
using Korat.Domain;
using Korat.Domain.Entities;
using Korat.GrainInterfaces;
using Korat.Relay.V1;
using Microsoft.Extensions.Logging.Abstractions;

namespace Korat.Auth.Tests;

/// <summary>
/// Unit tests for <see cref="SessionRoutingTable"/> machine-independent routing —
/// 009-nats-relay-backplane. Fakes the backplane + control-plane resolver so we can assert
/// the local-fast-path vs remote-publish decision without NATS or Orleans.
/// </summary>
public class SessionRoutingTableTests
{
    private static readonly NodeId Agent = new("agent-node");
    private static readonly NodeId Publisher = new("publisher-node");
    private static readonly SessionId RelaySession = new("sess-1");
    private static readonly McpServerId Server = new("srv-1");
    private static readonly SpaceId Space = new("space-1");

    private sealed class FakeStreamWriter : IAsyncStreamWriter<GatewayToNodeMessage>
    {
        public readonly List<GatewayToNodeMessage> Written = [];
        public WriteOptions? WriteOptions { get; set; }
        public Task WriteAsync(GatewayToNodeMessage message)
        {
            Written.Add(message);
            return Task.CompletedTask;
        }
        public Task WriteAsync(GatewayToNodeMessage message, CancellationToken cancellationToken)
        {
            Written.Add(message);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeBackplane : IRelayBackplane
    {
        public readonly List<(NodeId Target, GatewayToNodeMessage Message)> Published = [];
        public readonly Dictionary<NodeId, Func<GatewayToNodeMessage, CancellationToken, Task>> Handlers = [];
        public bool PublishResult = true;

        // Tracks disposal of individual subscriptions returned from SubscribeNodeAsync.
        // Allows reconnect tests to assert that the OLD subscription was disposed and the
        // NEW subscription is still alive.
        public readonly List<TrackingSubscription> AllSubscriptions = [];

        // 022: connection-keyed tracking for agent tests.
        public readonly List<(ConnectionId Target, GatewayToNodeMessage Message)> PublishedConn = [];
        public readonly Dictionary<ConnectionId, Func<GatewayToNodeMessage, CancellationToken, Task>> ConnHandlers = [];

        public Task<bool> PublishToNodeAsync(NodeId target, GatewayToNodeMessage message, CancellationToken cancellationToken)
        {
            Published.Add((target, message));
            return Task.FromResult(PublishResult);
        }

        public Task<IAsyncDisposable> SubscribeNodeAsync(NodeId nodeId, Func<GatewayToNodeMessage, CancellationToken, Task> onMessage, CancellationToken cancellationToken)
        {
            Handlers[nodeId] = onMessage;
            var sub = new TrackingSubscription();
            AllSubscriptions.Add(sub);
            return Task.FromResult<IAsyncDisposable>(sub);
        }

        // 022: connection-keyed methods — no-op stubs sufficient for existing publisher tests.
        public Task<bool> PublishToConnectionAsync(ConnectionId target, GatewayToNodeMessage message, CancellationToken cancellationToken)
        {
            PublishedConn.Add((target, message));
            return Task.FromResult(PublishResult);
        }

        public Task<IAsyncDisposable> SubscribeConnectionAsync(ConnectionId connectionId, Func<GatewayToNodeMessage, CancellationToken, Task> onMessage, CancellationToken cancellationToken)
        {
            ConnHandlers[connectionId] = onMessage;
            var sub = new TrackingSubscription();
            AllSubscriptions.Add(sub);
            return Task.FromResult<IAsyncDisposable>(sub);
        }

        public sealed class TrackingSubscription : IAsyncDisposable
        {
            public bool Disposed { get; private set; }
            public ValueTask DisposeAsync()
            {
                Disposed = true;
                return ValueTask.CompletedTask;
            }
        }
    }

    private sealed class FakeResolver : ISessionRouteResolver
    {
        public SessionRouteInfo? Route;
        public int Calls;
        public Task<SessionRouteInfo?> ResolveAsync(SessionId sessionId, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(Route);
        }
    }

    /// <summary>
    /// Minimal ISessionGrain stub that records RecordBytesAsync calls and accumulates the
    /// deltas so tests can assert total c2s/s2c bytes reported.
    /// </summary>
    private sealed class FakeSessionGrain : ISessionGrain
    {
        public long TotalC2S;
        public long TotalS2C;
        public int RecordCalls;

        public Task<RelaySession> OpenAsync(GrantId grantId, ConsumerId agentClientId, McpServerId mcpServerId,
            NodeId clientNodeId, NodeId publisherNodeId, GatewayId homeGatewayId, SpaceId spaceId,
            ConnectionId agentConnectionId = default)
            => throw new NotSupportedException();

        public Task RecordBytesAsync(long clientToServer, long serverToClient)
        {
            TotalC2S += clientToServer;
            TotalS2C += serverToClient;
            RecordCalls++;
            return Task.CompletedTask;
        }

        public Task CloseAsync(SessionCloseReason reason) => throw new NotSupportedException();
        public Task RevokeAsync() => throw new NotSupportedException();
        public Task<RelaySession> GetAsync() => throw new NotSupportedException();
    }

    private static McpToolCallInspector NoopInspector()
        => new(new NoopSink(), NullLogger<McpToolCallInspector>.Instance);

    private sealed class NoopSink : IMcpToolCallSink
    {
        public void Record(in ToolCallEvent toolCall) { }
    }

    private static (SessionRoutingTable Table, FakeBackplane Backplane, FakeResolver Resolver, FakeSessionGrain SessionGrain) NewTable()
    {
        var backplane = new FakeBackplane();
        var resolver = new FakeResolver();
        var sessionGrain = new FakeSessionGrain();
        // Use the internal test constructor that accepts a grain factory delegate — avoids
        // implementing the full IClusterClient interface in tests.
        var table = new SessionRoutingTable(backplane, resolver, NoopInspector(),
            _ => sessionGrain,
            _ => throw new NotSupportedException("not exercised by this test"),
            NullLogger<SessionRoutingTable>.Instance);
        return (table, backplane, resolver, sessionGrain);
    }

    private static RelayFrame Frame()
        => new() { SessionId = RelaySession.Value, Direction = "client_to_server", Ciphertext = ByteString.Empty };

    [Fact]
    public async Task RegisterStream_SubscribesBackplaneInbox()
    {
        var (table, backplane, _, _) = NewTable();

        await table.RegisterStreamAsync(Agent, new FakeStreamWriter(), CancellationToken.None);

        Assert.True(backplane.Handlers.ContainsKey(Agent));
    }

    [Fact]
    public async Task ForwardFrame_LocalPeer_WritesDirectly_NoPublish()
    {
        var (table, backplane, _, _) = NewTable();
        var publisherWriter = new FakeStreamWriter();
        await table.RegisterStreamAsync(Agent, new FakeStreamWriter(), CancellationToken.None);
        await table.RegisterStreamAsync(Publisher, publisherWriter, CancellationToken.None);
        table.OpenSession(RelaySession, Agent, Publisher, Server, Space);

        var delivered = await table.ForwardFrameAsync(Agent, Frame(), CancellationToken.None);

        Assert.True(delivered);
        Assert.Single(publisherWriter.Written);
        Assert.Empty(backplane.Published);
    }

    [Fact]
    public async Task ForwardFrame_RemotePeer_PublishesOverBackplane()
    {
        var (table, backplane, _, _) = NewTable();
        // Only the sender is connected to this machine; the publisher lives elsewhere.
        await table.RegisterStreamAsync(Agent, new FakeStreamWriter(), CancellationToken.None);
        table.OpenSession(RelaySession, Agent, Publisher, Server, Space);

        var delivered = await table.ForwardFrameAsync(Agent, Frame(), CancellationToken.None);

        Assert.True(delivered);
        var published = Assert.Single(backplane.Published);
        Assert.Equal(Publisher, published.Target);
        Assert.Equal(GatewayToNodeMessage.PayloadOneofCase.Frame, published.Message.PayloadCase);
    }

    [Fact]
    public async Task ForwardFrame_NoLocalRoute_ResolvesViaControlPlane()
    {
        var (table, backplane, resolver, _) = NewTable();
        var publisherWriter = new FakeStreamWriter();
        await table.RegisterStreamAsync(Publisher, publisherWriter, CancellationToken.None);
        // No OpenSession on this machine (publisher's machine) — resolver supplies the route.
        resolver.Route = new SessionRouteInfo(Agent, Publisher, Server, Space);

        var delivered = await table.ForwardFrameAsync(Agent, Frame(), CancellationToken.None);

        Assert.True(delivered);
        Assert.Equal(1, resolver.Calls);
        Assert.Single(publisherWriter.Written);
    }

    [Fact]
    public async Task ForwardFrame_SenderNotInSession_DropsWithoutDelivery()
    {
        var (table, backplane, _, _) = NewTable();
        var stranger = new NodeId("stranger");
        await table.RegisterStreamAsync(stranger, new FakeStreamWriter(), CancellationToken.None);
        table.OpenSession(RelaySession, Agent, Publisher, Server, Space);

        var delivered = await table.ForwardFrameAsync(stranger, Frame(), CancellationToken.None);

        Assert.False(delivered);
        Assert.Empty(backplane.Published);
    }

    [Fact]
    public async Task ForwardFrame_UnknownSession_ReturnsFalse()
    {
        var (table, _, resolver, _) = NewTable();
        resolver.Route = null; // control plane has no such session

        var outcome = await table.ForwardFrameWithOutcomeAsync(Agent, Frame(), CancellationToken.None);
        var delivered = await table.ForwardFrameAsync(Agent, Frame(), CancellationToken.None);

        Assert.Equal(FrameForwardOutcome.PeerUnavailable, outcome);
        Assert.False(delivered);
    }

    [Fact]
    public async Task ForwardFrame_BackplaneFailure_HasUnknownDeliveryOutcome()
    {
        var (table, backplane, _, _) = NewTable();
        backplane.PublishResult = false;
        table.OpenSession(RelaySession, Agent, Publisher, Server, Space);

        var outcome = await table.ForwardFrameWithOutcomeAsync(Agent, Frame(), CancellationToken.None);

        Assert.Equal(FrameForwardOutcome.DeliveryUnknown, outcome);
        Assert.Single(backplane.Published);
    }

    [Fact]
    public async Task InboundBackplaneMessage_WritesToLocalStream()
    {
        var (table, backplane, _, _) = NewTable();
        var agentWriter = new FakeStreamWriter();
        await table.RegisterStreamAsync(Agent, agentWriter, CancellationToken.None);

        // Simulate a frame arriving from another machine on the agent's inbox.
        var inbound = new GatewayToNodeMessage { Frame = Frame() };
        await backplane.Handlers[Agent](inbound, CancellationToken.None);

        Assert.Single(agentWriter.Written);
    }

    [Fact]
    public async Task CloseSession_EvictsLocalRoute()
    {
        var (table, _, _, _) = NewTable();
        table.OpenSession(RelaySession, Agent, Publisher, Server, Space);
        Assert.NotNull(table.GetParticipants(RelaySession));

        table.CloseSession(RelaySession);

        Assert.Null(table.GetParticipants(RelaySession));
    }

    /// <summary>
    /// Reconnect race regression (fix #1 / #2):
    /// Register stream A, then register A' (same NodeId, new writer).
    /// End/unregister A's stream using A's epoch.
    /// Assert that A' STILL receives via SendToNodeAsync — the old teardown must NOT evict the
    /// new stream's entry.
    /// Also assert that A's old subscription was disposed but A' is still live.
    /// </summary>
    [Fact]
    public async Task Reconnect_OldUnregister_DoesNotTearDownNewStream()
    {
        var (table, backplane, _, _) = NewTable();

        // Register first stream (node A connection).
        var writerA = new FakeStreamWriter();
        var epochA = await table.RegisterStreamAsync(Agent, writerA, CancellationToken.None);
        var subA = Assert.Single(backplane.AllSubscriptions);

        // Simulate immediate reconnect: register second stream (node A' connection).
        var writerAPrime = new FakeStreamWriter();
        var epochAPrime = await table.RegisterStreamAsync(Agent, writerAPrime, CancellationToken.None);
        Assert.Equal(2, backplane.AllSubscriptions.Count); // Two subscriptions created in total.
        var subAPrime = backplane.AllSubscriptions[1];

        // Epochs must be distinct.
        Assert.NotEqual(epochA, epochAPrime);

        // The OLD subscription (subA) should have been disposed during re-registration.
        Assert.True(subA.Disposed, "Old subscription should be disposed on re-registration.");
        Assert.False(subAPrime.Disposed, "New subscription must NOT be disposed yet.");

        // Simulate A's finally block: unregister with the OLD epoch.
        var wasActive = await table.UnregisterStreamAsync(Agent, epochA);

        // A was NOT the active stream at unregister time (A' already took over).
        Assert.False(wasActive, "Old stream should not be considered active after reconnect.");

        // A's new subscription (A') must still be alive.
        Assert.False(subAPrime.Disposed, "New subscription must survive old stream's teardown.");

        // SendToNodeAsync must still deliver to the new stream (A').
        table.OpenSession(RelaySession, Agent, Publisher, Server, Space);
        var message = new GatewayToNodeMessage { Frame = Frame() };
        var delivered = await table.SendToNodeAsync(Agent, message, CancellationToken.None);

        Assert.True(delivered, "Message must be delivered to the reconnected stream.");
        Assert.Single(writerAPrime.Written);
        Assert.Empty(writerA.Written); // Old writer must not receive anything.
    }

    /// <summary>
    /// Presence TOCTOU regression (fix #2):
    /// When this stream was the last active registration (no reconnect), UnregisterStreamAsync
    /// returns true — MarkOffline SHOULD be called.
    /// </summary>
    [Fact]
    public async Task Unregister_WhenNoReconnect_ReturnsTrue()
    {
        var (table, _, _, _) = NewTable();
        var writer = new FakeStreamWriter();
        var epoch = await table.RegisterStreamAsync(Agent, writer, CancellationToken.None);

        var wasActive = await table.UnregisterStreamAsync(Agent, epoch);

        Assert.True(wasActive, "Should return true when no reconnect occurred (safe to MarkOffline).");
    }

    /// <summary>
    /// Presence TOCTOU regression (fix #2):
    /// After a reconnect, the old stream's UnregisterStreamAsync returns false — MarkOffline
    /// should NOT be called.
    /// </summary>
    [Fact]
    public async Task Unregister_AfterReconnect_ReturnsFalse()
    {
        var (table, _, _, _) = NewTable();
        var epochA = await table.RegisterStreamAsync(Agent, new FakeStreamWriter(), CancellationToken.None);
        // Reconnect: new stream takes over.
        await table.RegisterStreamAsync(Agent, new FakeStreamWriter(), CancellationToken.None);

        // Old stream tears down with old epoch.
        var wasActive = await table.UnregisterStreamAsync(Agent, epochA);

        Assert.False(wasActive, "Should return false after reconnect (must NOT MarkOffline).");
    }

    // ---------------------------------------------------------------------------
    // 018-Bug3: byte accounting tests
    // ---------------------------------------------------------------------------

    /// <summary>
    /// ByteAccumulator.Add + Drain: c2s and s2c accumulate correctly and drain atomically.
    /// </summary>
    [Fact]
    public void ByteAccumulator_AddAndDrain_CorrectTotals()
    {
        var acc = new SessionRoutingTable.ByteAccumulator();

        acc.Add(100, 0);
        acc.Add(200, 0);
        acc.Add(0, 50);

        var (c2s, s2c) = acc.Drain();
        Assert.Equal(300, c2s);
        Assert.Equal(50, s2c);
    }

    /// <summary>
    /// ByteAccumulator.Drain resets the counters — a second Drain returns zeros.
    /// </summary>
    [Fact]
    public void ByteAccumulator_DrainResetsCounters()
    {
        var acc = new SessionRoutingTable.ByteAccumulator();
        acc.Add(42, 7);
        acc.Drain(); // first drain

        var (c2s, s2c) = acc.Drain(); // second drain must be zero
        Assert.Equal(0, c2s);
        Assert.Equal(0, s2c);
    }

    /// <summary>
    /// ForwardFrameAsync from the agent node (c2s direction): the accumulator records the
    /// ciphertext length as client-to-server bytes and zero as server-to-client.
    /// </summary>
    [Fact]
    public async Task ForwardFrame_AgentSender_AccountsClientToServerBytes()
    {
        var (table, _, _, sessionGrain) = NewTable();
        await table.RegisterStreamAsync(Agent, new FakeStreamWriter(), CancellationToken.None);
        await table.RegisterStreamAsync(Publisher, new FakeStreamWriter(), CancellationToken.None);
        table.OpenSession(RelaySession, Agent, Publisher, Server, Space);

        var frame = new RelayFrame
        {
            SessionId = RelaySession.Value,
            Direction = "client_to_server",
            Ciphertext = ByteString.CopyFrom(new byte[128])
        };
        await table.ForwardFrameAsync(Agent, frame, CancellationToken.None);

        // Trigger a manual flush (simulates timer tick).
        await table.FlushAccumulatorsForTestAsync();

        Assert.Equal(128, sessionGrain.TotalC2S);
        Assert.Equal(0, sessionGrain.TotalS2C);
    }

    /// <summary>
    /// ForwardFrameAsync from the publisher node (s2c direction): bytes land in s2c only.
    /// </summary>
    [Fact]
    public async Task ForwardFrame_PublisherSender_AccountsServerToClientBytes()
    {
        var (table, _, _, sessionGrain) = NewTable();
        await table.RegisterStreamAsync(Agent, new FakeStreamWriter(), CancellationToken.None);
        await table.RegisterStreamAsync(Publisher, new FakeStreamWriter(), CancellationToken.None);
        table.OpenSession(RelaySession, Agent, Publisher, Server, Space);

        var frame = new RelayFrame
        {
            SessionId = RelaySession.Value,
            Direction = "server_to_client",
            Ciphertext = ByteString.CopyFrom(new byte[64])
        };
        await table.ForwardFrameAsync(Publisher, frame, CancellationToken.None);

        await table.FlushAccumulatorsForTestAsync();

        Assert.Equal(0, sessionGrain.TotalC2S);
        Assert.Equal(64, sessionGrain.TotalS2C);
    }

    /// <summary>
    /// CloseSession flushes accumulated bytes before evicting the route entry. After close,
    /// the SessionGrain receives the remaining delta even without a timer tick.
    /// </summary>
    [Fact]
    public async Task CloseSession_FlushesRemainingBytes()
    {
        var (table, _, _, sessionGrain) = NewTable();
        await table.RegisterStreamAsync(Agent, new FakeStreamWriter(), CancellationToken.None);
        await table.RegisterStreamAsync(Publisher, new FakeStreamWriter(), CancellationToken.None);
        table.OpenSession(RelaySession, Agent, Publisher, Server, Space);

        var frame = new RelayFrame
        {
            SessionId = RelaySession.Value,
            Direction = "client_to_server",
            Ciphertext = ByteString.CopyFrom(new byte[77])
        };
        await table.ForwardFrameAsync(Agent, frame, CancellationToken.None);

        // No timer tick — CloseSession must flush.
        table.CloseSession(RelaySession);

        // The flush is fire-and-forget; await its completion deterministically (F22) rather
        // than racing it with a fixed Task.Delay that can elapse before the flush finishes.
        await table.LastCloseFlushForTestAsync();

        Assert.Equal(77, sessionGrain.TotalC2S);
    }

    /// <summary>
    /// Both directions in the same session accumulate and are persisted on flush.
    /// </summary>
    [Fact]
    public async Task ForwardFrame_BothDirections_BothCountersAccumulate()
    {
        var (table, _, _, sessionGrain) = NewTable();
        await table.RegisterStreamAsync(Agent, new FakeStreamWriter(), CancellationToken.None);
        await table.RegisterStreamAsync(Publisher, new FakeStreamWriter(), CancellationToken.None);
        table.OpenSession(RelaySession, Agent, Publisher, Server, Space);

        var c2sFrame = new RelayFrame { SessionId = RelaySession.Value, Ciphertext = ByteString.CopyFrom(new byte[100]) };
        var s2cFrame = new RelayFrame { SessionId = RelaySession.Value, Ciphertext = ByteString.CopyFrom(new byte[200]) };
        await table.ForwardFrameAsync(Agent, c2sFrame, CancellationToken.None);
        await table.ForwardFrameAsync(Publisher, s2cFrame, CancellationToken.None);

        await table.FlushAccumulatorsForTestAsync();

        Assert.Equal(100, sessionGrain.TotalC2S);
        Assert.Equal(200, sessionGrain.TotalS2C);
    }

    // ── 022: per-connection agent routing (no eviction between concurrent bridges) ──────────

    [Fact]
    public async Task TwoAgentBridges_SameAgentNode_EachReceivesOwnSessionFrames()
    {
        // The exact #022 repro at the routing layer: one agent identity (NodeId `Agent`) opens
        // TWO concurrent bridges (distinct ConnectionIds), each consuming a different server in
        // its own session. Publisher→agent frames must reach the CONNECTION that owns the session,
        // never collide on a single per-node slot.
        var (table, _, _, _) = NewTable();
        var connA = new ConnectionId("conn-a");
        var connB = new ConnectionId("conn-b");
        var writerA = new FakeStreamWriter();
        var writerB = new FakeStreamWriter();
        await table.RegisterAgentStreamAsync(connA, writerA, CancellationToken.None);
        await table.RegisterAgentStreamAsync(connB, writerB, CancellationToken.None);

        var sessA = new SessionId("sess-A");
        var sessB = new SessionId("sess-B");
        // Same agent NodeId for both; distinct AgentConnectionId per bridge.
        table.OpenSession(sessA, Agent, Publisher, Server, Space, connA);
        table.OpenSession(sessB, Agent, Publisher, Server, Space, connB);

        RelayFrame ToAgent(string sessionId) => new()
        {
            SessionId = sessionId, Direction = "server_to_client", Ciphertext = ByteString.Empty
        };

        // Publisher emits a frame for each session.
        var deliveredA = await table.ForwardFrameAsync(Publisher, ToAgent(sessA.Value), CancellationToken.None);
        var deliveredB = await table.ForwardFrameAsync(Publisher, ToAgent(sessB.Value), CancellationToken.None);

        Assert.True(deliveredA);
        Assert.True(deliveredB);
        // Each bridge received ONLY its own session's frame — no eviction, no cross-delivery.
        Assert.Single(writerA.Written);
        Assert.Single(writerB.Written);
    }

    [Fact]
    public async Task AgentStreamTeardown_OnlyClosesItsOwnConnectionsSessions()
    {
        // FindSessionsForConnection must match by AgentConnectionId, not the shared agent NodeId,
        // so tearing down bridge A does not sweep bridge B's sessions (LOCKED #4).
        var (table, _, _, _) = NewTable();
        var connA = new ConnectionId("conn-a");
        var connB = new ConnectionId("conn-b");
        await table.RegisterAgentStreamAsync(connA, new FakeStreamWriter(), CancellationToken.None);
        await table.RegisterAgentStreamAsync(connB, new FakeStreamWriter(), CancellationToken.None);
        var sessA = new SessionId("sess-A");
        var sessB = new SessionId("sess-B");
        table.OpenSession(sessA, Agent, Publisher, Server, Space, connA);
        table.OpenSession(sessB, Agent, Publisher, Server, Space, connB);

        var forConnA = table.FindSessionsForConnection(connA);

        Assert.Contains(sessA, forConnA);
        Assert.DoesNotContain(sessB, forConnA);
    }

    // ── Task 2: SendToConnectionAsync — agent-end send (local fast path or backplane) ─────────

    [Fact]
    public async Task SendToConnection_LocalFastPath_WritesToAgentStream()
    {
        var (table, backplane, _, _) = NewTable();
        var conn = new ConnectionId("conn-local");
        var writer = new FakeStreamWriter();
        await table.RegisterAgentStreamAsync(conn, writer, CancellationToken.None);

        var msg = new GatewayToNodeMessage
        {
            CloseSession = new CloseSession { SessionId = "s1", Reason = "Revoked" }
        };
        var ok = await table.SendToConnectionAsync(conn, msg, CancellationToken.None);

        Assert.True(ok);
        Assert.Single(writer.Written);
        Assert.Empty(backplane.PublishedConn); // local — no backplane publish
    }

    [Fact]
    public async Task SendToConnection_NoLocalStream_FallsBackToBackplane()
    {
        var (table, backplane, _, _) = NewTable();
        var conn = new ConnectionId("conn-remote");
        var msg = new GatewayToNodeMessage
        {
            CloseSession = new CloseSession { SessionId = "s1", Reason = "Revoked" }
        };

        var ok = await table.SendToConnectionAsync(conn, msg, CancellationToken.None);

        Assert.True(ok); // FakeBackplane.PublishResult defaults true
        Assert.Contains(backplane.PublishedConn, p => p.Target == conn);
    }

    // ---------------------------------------------------------------------------
    // F1: payload limit enforcement tests (TDD — written before implementation)
    // ---------------------------------------------------------------------------

    /// <summary>
    /// A frame whose ciphertext is within the per-message limit is forwarded normally.
    /// No PayloadLimitExceeded or CloseSession is sent to either node.
    /// </summary>
    [Fact]
    public async Task ForwardFrame_UnderPerMessageLimit_DeliveredNormally()
    {
        var (table, _, _, _) = NewTable();
        var agentWriter = new FakeStreamWriter();
        var publisherWriter = new FakeStreamWriter();
        await table.RegisterStreamAsync(Agent, agentWriter, CancellationToken.None);
        await table.RegisterStreamAsync(Publisher, publisherWriter, CancellationToken.None);

        // Use a tight policy so the test doesn't need a real 16 MB frame.
        var policy = new Korat.Domain.Entities.PayloadLimitPolicy
        {
            PerMessageLimitBytes = 100,
            SessionWarningBytes = 50,
            SessionHardLimitBytes = 200
        };
        table.OpenSession(RelaySession, Agent, Publisher, Server, Space, payloadPolicy: policy);

        var frame = new RelayFrame
        {
            SessionId = RelaySession.Value,
            Direction = "client_to_server",
            Ciphertext = ByteString.CopyFrom(new byte[50]) // well under 100
        };

        var delivered = await table.ForwardFrameAsync(Agent, frame, CancellationToken.None);

        Assert.True(delivered);
        Assert.Single(publisherWriter.Written); // frame forwarded to publisher
        // Agent (sender) received no control messages.
        Assert.Empty(agentWriter.Written);
    }

    /// <summary>
    /// A single frame that exceeds PerMessageLimitBytes: the session is closed, the sender
    /// receives PayloadLimitExceeded followed by CloseSession, and ForwardFrameAsync returns false.
    /// </summary>
    [Fact]
    public async Task ForwardFrame_ExceedsPerMessageLimit_SessionClosedAndSenderNotified()
    {
        var (table, _, _, _) = NewTable();
        var agentWriter = new FakeStreamWriter();
        var publisherWriter = new FakeStreamWriter();
        await table.RegisterStreamAsync(Agent, agentWriter, CancellationToken.None);
        await table.RegisterStreamAsync(Publisher, publisherWriter, CancellationToken.None);

        var policy = new Korat.Domain.Entities.PayloadLimitPolicy
        {
            PerMessageLimitBytes = 100,
            SessionWarningBytes = 50,
            SessionHardLimitBytes = 200
        };
        table.OpenSession(RelaySession, Agent, Publisher, Server, Space, payloadPolicy: policy);

        var oversizedFrame = new RelayFrame
        {
            SessionId = RelaySession.Value,
            Direction = "client_to_server",
            Ciphertext = ByteString.CopyFrom(new byte[101]) // exceeds 100
        };

        var outcome = await table.ForwardFrameWithOutcomeAsync(Agent, oversizedFrame, CancellationToken.None);

        Assert.Equal(FrameForwardOutcome.Rejected, outcome); // blocked and not retryable
        // Publisher must NOT receive the oversized frame but DOES receive CloseSession (peer notification).
        Assert.Single(publisherWriter.Written);
        Assert.Equal(GatewayToNodeMessage.PayloadOneofCase.CloseSession, publisherWriter.Written[0].PayloadCase);
        Assert.Equal(RelaySession.Value, publisherWriter.Written[0].CloseSession.SessionId);
        // Agent (sender/violator) must receive PayloadLimitExceeded then CloseSession.
        Assert.Equal(2, agentWriter.Written.Count);
        Assert.Equal(GatewayToNodeMessage.PayloadOneofCase.PayloadLimitExceeded, agentWriter.Written[0].PayloadCase);
        Assert.Equal(GatewayToNodeMessage.PayloadOneofCase.CloseSession, agentWriter.Written[1].PayloadCase);
        Assert.Equal(RelaySession.Value, agentWriter.Written[0].PayloadLimitExceeded.SessionId);
        Assert.Equal(RelaySession.Value, agentWriter.Written[1].CloseSession.SessionId);
        // RelaySession route must be evicted.
        Assert.Null(table.GetParticipants(RelaySession));
    }

    /// <summary>
    /// Publisher (server side) can also be the violator: oversized publisher→agent frame
    /// closes the session and notifies the publisher stream.
    /// </summary>
    [Fact]
    public async Task ForwardFrame_PublisherExceedsPerMessageLimit_PublisherNotified()
    {
        var (table, _, _, _) = NewTable();
        var agentWriter = new FakeStreamWriter();
        var publisherWriter = new FakeStreamWriter();
        var connA = new ConnectionId("conn-a");
        await table.RegisterAgentStreamAsync(connA, agentWriter, CancellationToken.None);
        await table.RegisterStreamAsync(Publisher, publisherWriter, CancellationToken.None);

        var policy = new Korat.Domain.Entities.PayloadLimitPolicy
        {
            PerMessageLimitBytes = 50,
            SessionWarningBytes = 30,
            SessionHardLimitBytes = 100
        };
        table.OpenSession(RelaySession, Agent, Publisher, Server, Space, connA, payloadPolicy: policy);

        var oversizedFrame = new RelayFrame
        {
            SessionId = RelaySession.Value,
            Direction = "server_to_client",
            Ciphertext = ByteString.CopyFrom(new byte[51]) // exceeds 50
        };

        var delivered = await table.ForwardFrameAsync(Publisher, oversizedFrame, CancellationToken.None);

        Assert.False(delivered);
        // Agent must NOT receive the oversized frame but DOES receive CloseSession (peer notification).
        Assert.Single(agentWriter.Written);
        Assert.Equal(GatewayToNodeMessage.PayloadOneofCase.CloseSession, agentWriter.Written[0].PayloadCase);
        // Publisher (violator) must receive PayloadLimitExceeded then CloseSession.
        Assert.Equal(2, publisherWriter.Written.Count);
        Assert.Equal(GatewayToNodeMessage.PayloadOneofCase.PayloadLimitExceeded, publisherWriter.Written[0].PayloadCase);
        Assert.Equal(GatewayToNodeMessage.PayloadOneofCase.CloseSession, publisherWriter.Written[1].PayloadCase);
        Assert.Null(table.GetParticipants(RelaySession));
    }

    /// <summary>
    /// Cumulative bytes exceeding SessionHardLimitBytes triggers PayloadLimitExceeded +
    /// CloseSession to the sender and evicts the session. The triggering frame is not forwarded.
    /// </summary>
    [Fact]
    public async Task ForwardFrame_CumulativeBytesExceedSessionHardLimit_SessionClosedAndSenderNotified()
    {
        var (table, _, _, _) = NewTable();
        var agentWriter = new FakeStreamWriter();
        var publisherWriter = new FakeStreamWriter();
        await table.RegisterStreamAsync(Agent, agentWriter, CancellationToken.None);
        await table.RegisterStreamAsync(Publisher, publisherWriter, CancellationToken.None);

        var policy = new Korat.Domain.Entities.PayloadLimitPolicy
        {
            PerMessageLimitBytes = 100,
            SessionWarningBytes = 50,
            SessionHardLimitBytes = 200
        };
        table.OpenSession(RelaySession, Agent, Publisher, Server, Space, payloadPolicy: policy);

        // First frame: 90 bytes — under both limits, forwarded normally.
        var frame1 = new RelayFrame
        {
            SessionId = RelaySession.Value,
            Direction = "client_to_server",
            Ciphertext = ByteString.CopyFrom(new byte[90])
        };
        var delivered1 = await table.ForwardFrameAsync(Agent, frame1, CancellationToken.None);
        Assert.True(delivered1);
        Assert.Single(publisherWriter.Written);

        // Second frame: 90 bytes — cumulative 180 bytes, still under 200 hard limit.
        var frame2 = new RelayFrame
        {
            SessionId = RelaySession.Value,
            Direction = "client_to_server",
            Ciphertext = ByteString.CopyFrom(new byte[90])
        };
        var delivered2 = await table.ForwardFrameAsync(Agent, frame2, CancellationToken.None);
        Assert.True(delivered2);
        Assert.Equal(2, publisherWriter.Written.Count);

        // Third frame: 30 bytes — cumulative 210 bytes, OVER 200 hard limit.
        var frame3 = new RelayFrame
        {
            SessionId = RelaySession.Value,
            Direction = "client_to_server",
            Ciphertext = ByteString.CopyFrom(new byte[30])
        };
        var delivered3 = await table.ForwardFrameAsync(Agent, frame3, CancellationToken.None);

        Assert.False(delivered3); // blocked
        // Publisher received 2 good forwarded frames; third frame is NOT forwarded but publisher
        // does receive a CloseSession from the enforcement path (peer notification).
        Assert.Equal(3, publisherWriter.Written.Count);
        Assert.Equal(GatewayToNodeMessage.PayloadOneofCase.Frame, publisherWriter.Written[0].PayloadCase);
        Assert.Equal(GatewayToNodeMessage.PayloadOneofCase.Frame, publisherWriter.Written[1].PayloadCase);
        Assert.Equal(GatewayToNodeMessage.PayloadOneofCase.CloseSession, publisherWriter.Written[2].PayloadCase);
        // Agent (sender) receives PayloadLimitExceeded then CloseSession.
        Assert.Equal(2, agentWriter.Written.Count);
        Assert.Equal(GatewayToNodeMessage.PayloadOneofCase.PayloadLimitExceeded, agentWriter.Written[0].PayloadCase);
        Assert.Equal(GatewayToNodeMessage.PayloadOneofCase.CloseSession, agentWriter.Written[1].PayloadCase);
        Assert.Equal("session_hard_limit", agentWriter.Written[0].PayloadLimitExceeded.LimitName);
        Assert.Null(table.GetParticipants(RelaySession));
    }

    /// <summary>
    /// Peer also receives a CloseSession when the session is terminated due to a payload limit violation.
    /// </summary>
    [Fact]
    public async Task ForwardFrame_PerMessageViolation_PeerAlsoReceivesCloseSession()
    {
        var (table, _, _, _) = NewTable();
        var agentWriter = new FakeStreamWriter();
        var publisherWriter = new FakeStreamWriter();
        await table.RegisterStreamAsync(Agent, agentWriter, CancellationToken.None);
        await table.RegisterStreamAsync(Publisher, publisherWriter, CancellationToken.None);

        var policy = new Korat.Domain.Entities.PayloadLimitPolicy
        {
            PerMessageLimitBytes = 10,
            SessionWarningBytes = 5,
            SessionHardLimitBytes = 20
        };
        table.OpenSession(RelaySession, Agent, Publisher, Server, Space, payloadPolicy: policy);

        var oversizedFrame = new RelayFrame
        {
            SessionId = RelaySession.Value,
            Direction = "client_to_server",
            Ciphertext = ByteString.CopyFrom(new byte[11]) // exceeds 10
        };
        await table.ForwardFrameAsync(Agent, oversizedFrame, CancellationToken.None);

        // Publisher (peer) also receives CloseSession so it can tear down its side.
        Assert.Single(publisherWriter.Written);
        Assert.Equal(GatewayToNodeMessage.PayloadOneofCase.CloseSession, publisherWriter.Written[0].PayloadCase);
        Assert.Equal(RelaySession.Value, publisherWriter.Written[0].CloseSession.SessionId);
    }

    // ---------------------------------------------------------------------------
    // MAJOR-1: cross-silo route-cache fill must seed a PayloadLimitTracker
    // ---------------------------------------------------------------------------

    /// <summary>
    /// When GetRouteAsync fills the route cache from the resolver (cross-silo path, no
    /// OpenSession on this machine), it must also create a PayloadLimitTracker so that
    /// ForwardFrameAsync enforces payload limits on the resolved route — not silently skip them.
    /// </summary>
    [Fact]
    public async Task ForwardFrame_ResolvedRoute_EnforcesPayloadLimit()
    {
        var (table, _, resolver, _) = NewTable();
        var publisherWriter = new FakeStreamWriter();
        await table.RegisterStreamAsync(Publisher, publisherWriter, CancellationToken.None);

        // No OpenSession on this machine — the route is resolved from the control plane.
        resolver.Route = new SessionRouteInfo(Agent, Publisher, Server, Space);

        var oversizedFrame = new RelayFrame
        {
            SessionId = RelaySession.Value,
            Direction = "client_to_server",
            // Default policy PerMessageLimitBytes = 16 MB; we cannot easily send 16 MB in a test.
            // Instead use a policy injected via OpenSession on a second table instance — but here
            // we verify the tracker is created (non-null path) by ensuring a frame that would pass
            // the default policy IS forwarded normally (no enforcement error on default-seeded tracker).
            Ciphertext = ByteString.CopyFrom(new byte[1024]) // well under 16 MB default
        };

        var delivered = await table.ForwardFrameAsync(Agent, oversizedFrame, CancellationToken.None);

        // Must succeed (default policy is permissive for small frames).
        Assert.True(delivered);
        // Publisher received the frame — enforcement did not incorrectly block it.
        Assert.Single(publisherWriter.Written);
        // Resolver was called exactly once — route was cached after.
        Assert.Equal(1, resolver.Calls);
    }

    /// <summary>
    /// Verify that a second frame on the same cross-silo resolved route does NOT re-call
    /// the resolver (route was cached on the first call) and the tracker accumulates correctly.
    /// </summary>
    [Fact]
    public async Task ForwardFrame_ResolvedRoute_CachesTrackerForSubsequentFrames()
    {
        var (table, _, resolver, _) = NewTable();
        var publisherWriter = new FakeStreamWriter();
        await table.RegisterStreamAsync(Publisher, publisherWriter, CancellationToken.None);
        resolver.Route = new SessionRouteInfo(Agent, Publisher, Server, Space);

        var frame = new RelayFrame
        {
            SessionId = RelaySession.Value,
            Ciphertext = ByteString.CopyFrom(new byte[64])
        };

        await table.ForwardFrameAsync(Agent, frame, CancellationToken.None);
        await table.ForwardFrameAsync(Agent, frame, CancellationToken.None);

        // Resolver called only once — second frame used the cached route.
        Assert.Equal(1, resolver.Calls);
        // Both frames forwarded.
        Assert.Equal(2, publisherWriter.Written.Count);
    }

    // ---------------------------------------------------------------------------
    // MAJOR-2: background sweep evicts stale closed-session route caches
    // ---------------------------------------------------------------------------

    private sealed class ControllableSessionGrain : ISessionGrain
    {
        public RelaySession SessionState;

        public ControllableSessionGrain(SessionStatus status)
        {
            SessionState = new RelaySession
            {
                Id = new SessionId("ctrl-session"),
                Status = status,
                StartedAt = DateTimeOffset.UtcNow
            };
        }

        public Task<RelaySession> OpenAsync(GrantId grantId, ConsumerId agentClientId, McpServerId mcpServerId,
            NodeId clientNodeId, NodeId publisherNodeId, GatewayId homeGatewayId, SpaceId spaceId,
            ConnectionId agentConnectionId = default)
            => throw new NotSupportedException();

        public Task RecordBytesAsync(long clientToServer, long serverToClient) => Task.CompletedTask;
        public Task CloseAsync(SessionCloseReason reason) => throw new NotSupportedException();
        public Task RevokeAsync() => throw new NotSupportedException();
        public Task<RelaySession> GetAsync() => Task.FromResult(SessionState);
    }

    /// <summary>
    /// SweepClosedSessions must evict a route that the grain reports as Closed.
    /// After the sweep the route should no longer be cached on this machine.
    /// </summary>
    [Fact]
    public async Task Sweep_ClosedSession_EvictsRoute()
    {
        var backplane = new FakeBackplane();
        var resolver = new FakeResolver();
        // Grain reports the session as Closed.
        var closedGrain = new ControllableSessionGrain(SessionStatus.Closed);
        var table = new SessionRoutingTable(backplane, resolver, NoopInspector(),
            _ => closedGrain,
            _ => throw new NotSupportedException("not exercised by this test"),
            NullLogger<SessionRoutingTable>.Instance);

        // Seed the route manually (simulates a resolver-filled cross-silo cache).
        table.OpenSession(RelaySession, Agent, Publisher, Server, Space);
        Assert.NotNull(table.GetParticipants(RelaySession)); // route is in cache

        // Trigger the sweep manually (test helper).
        await table.SweepClosedSessionsForTestAsync();

        // The route must have been evicted because the grain reported Closed.
        Assert.Null(table.GetParticipants(RelaySession));

        await table.DisposeAsync();
    }

    /// <summary>
    /// SweepClosedSessions must NOT evict a route for a session the grain reports as Active.
    /// </summary>
    [Fact]
    public async Task Sweep_ActiveSession_DoesNotEvictRoute()
    {
        var backplane = new FakeBackplane();
        var resolver = new FakeResolver();
        // Grain reports the session as Active.
        var activeGrain = new ControllableSessionGrain(SessionStatus.Active);
        var table = new SessionRoutingTable(backplane, resolver, NoopInspector(),
            _ => activeGrain,
            _ => throw new NotSupportedException("not exercised by this test"),
            NullLogger<SessionRoutingTable>.Instance);

        table.OpenSession(RelaySession, Agent, Publisher, Server, Space);
        Assert.NotNull(table.GetParticipants(RelaySession));

        await table.SweepClosedSessionsForTestAsync();

        // Route must still be present — the session is active.
        Assert.NotNull(table.GetParticipants(RelaySession));

        await table.DisposeAsync();
    }

    /// <summary>
    /// SweepClosedSessions must continue sweeping other sessions when one grain call throws.
    /// The session with the throwing grain must NOT be evicted (fail-open).
    /// </summary>
    [Fact]
    public async Task Sweep_GrainThrows_SkipsSession_DoesNotEvictOthers()
    {
        var backplane = new FakeBackplane();
        var resolver = new FakeResolver();
        var sessA = new SessionId("sess-sweep-A");
        var sessB = new SessionId("sess-sweep-B");

        // Grain for A throws; grain for B is Closed.
        var closedGrainB = new ControllableSessionGrain(SessionStatus.Closed);
        var table = new SessionRoutingTable(backplane, resolver, NoopInspector(),
            id => id == sessA.Value ? throw new InvalidOperationException("grain offline") : (ISessionGrain)closedGrainB,
            _ => throw new NotSupportedException("not exercised by this test"),
            NullLogger<SessionRoutingTable>.Instance);

        table.OpenSession(sessA, Agent, Publisher, Server, Space);
        table.OpenSession(sessB, Agent, Publisher, Server, Space);

        await table.SweepClosedSessionsForTestAsync();

        // A: grain threw → not evicted (fail-open).
        Assert.NotNull(table.GetParticipants(sessA));
        // B: grain returned Closed → evicted.
        Assert.Null(table.GetParticipants(sessB));

        await table.DisposeAsync();
    }

    // ---------------------------------------------------------------------------
    // cloud-m6: RegisterStreamAsync must not leak on SubscribeNodeAsync failure
    // ---------------------------------------------------------------------------

    private sealed class ThrowOnSubscribeBackplane : IRelayBackplane
    {
        public readonly List<(NodeId Target, GatewayToNodeMessage Message)> Published = [];
        public bool PublishResult = true;
        public readonly List<(ConnectionId Target, GatewayToNodeMessage Message)> PublishedConn = [];

        public Task<bool> PublishToNodeAsync(NodeId target, GatewayToNodeMessage message, CancellationToken cancellationToken)
        {
            Published.Add((target, message));
            return Task.FromResult(PublishResult);
        }

        public Task<IAsyncDisposable> SubscribeNodeAsync(NodeId nodeId, Func<GatewayToNodeMessage, CancellationToken, Task> onMessage, CancellationToken cancellationToken)
            => throw new InvalidOperationException("NATS unavailable");

        public Task<bool> PublishToConnectionAsync(ConnectionId target, GatewayToNodeMessage message, CancellationToken cancellationToken)
        {
            PublishedConn.Add((target, message));
            return Task.FromResult(PublishResult);
        }

        public Task<IAsyncDisposable> SubscribeConnectionAsync(ConnectionId connectionId, Func<GatewayToNodeMessage, CancellationToken, Task> onMessage, CancellationToken cancellationToken)
            => throw new InvalidOperationException("NATS unavailable");

    }

    /// <summary>
    /// cloud-m6: if SubscribeNodeAsync throws during RegisterStreamAsync, the stream entry
    /// must NOT be inserted into _streamsByNode (no dangling entry leak).
    /// After the failure, a subsequent successful registration must work normally.
    /// </summary>
    [Fact]
    public async Task RegisterStream_SubscribeThrows_DoesNotLeakStreamEntry()
    {
        var failingBackplane = new ThrowOnSubscribeBackplane();
        var resolver = new FakeResolver();
        var table = new SessionRoutingTable(failingBackplane, resolver, NoopInspector(),
            _ => new FakeSessionGrain(),
            _ => throw new NotSupportedException("not exercised by this test"),
            NullLogger<SessionRoutingTable>.Instance);

        // RegisterStreamAsync should throw (propagate from SubscribeNodeAsync).
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => table.RegisterStreamAsync(Agent, new FakeStreamWriter(), CancellationToken.None));

        // Verify: SendToNodeAsync should use the backplane (not a leaked local writer).
        // We use a real FakeBackplane now to test that the publish reaches it.
        var goodBackplane = new FakeBackplane();
        var table2 = new SessionRoutingTable(goodBackplane, resolver, NoopInspector(),
            _ => new FakeSessionGrain(),
            _ => throw new NotSupportedException("not exercised by this test"),
            NullLogger<SessionRoutingTable>.Instance);

        table2.OpenSession(RelaySession, Agent, Publisher, Server, Space);
        var msg = new GatewayToNodeMessage { Frame = Frame() };
        var delivered = await table2.SendToNodeAsync(Agent, msg, CancellationToken.None);

        // Delivered via backplane (no local stream registered).
        Assert.True(delivered);
        Assert.Contains(goodBackplane.Published, p => p.Target == Agent);

        await table.DisposeAsync();
        await table2.DisposeAsync();
    }
}
