using Grpc.Core;
using Korat.Cloud.Gateways;
using Korat.Cloud.Observability;
using Korat.Domain;
using Korat.Domain.Entities;
using Korat.GrainInterfaces;
using Korat.Domain.Persistence;
using Korat.Persistence;
using Korat.Relay.V1;
using Microsoft.Extensions.Logging.Abstractions;
using Korat.Mcp;
// PR-2: Thread collides with System.Threading.Thread (global using) — alias to the domain entity.

namespace Korat.Auth.Tests;

/// <summary>
/// Unit tests for <see cref="SessionTerminator"/>. Uses routing-table fakes (no NATS, no Orleans)
/// to verify the dual-end CloseSession push, route eviction, and grain persistence — per the
/// 2026-06-06-session-teardown-on-revoke plan (Task 3).
/// </summary>
public class SessionTerminatorTests
{
    private static readonly NodeId Agent = new("agent-node");
    private static readonly NodeId Publisher = new("publisher-node");
    private static readonly ConnectionId AgentConn = new("conn-A");
    private static readonly SessionId RelaySession = new("sess-1");
    private static readonly McpServerId Server = new("srv-1");
    private static readonly SpaceId Space = new("space-1");

    // ── Fakes ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Minimal <see cref="IMetadataRepository"/> fake: only <see cref="GetSessionAsync"/> is
    /// exercised. Every other member throws <see cref="NotSupportedException"/>.
    /// </summary>
    private sealed class FakeRepo : IMetadataRepository
    {
        public RelaySession? StoredSession;

        public Task<RelaySession?> GetSessionAsync(SessionId id, CancellationToken ct = default)
            => Task.FromResult(id == RelaySession ? StoredSession : null);

        // ── Not exercised by SessionTerminator ────────────────────────────────────
        public Task EnsureCreatedAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task UpsertNodeAsync(Node node, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<Node?> GetNodeAsync(NodeId nodeId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<Node>> ListNodesAsync(SpaceId spaceId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task DeleteNodeAsync(NodeId nodeId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task UpsertMcpServerAsync(McpServer server, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SetMcpServerSecretAsync(McpServerId id, string ciphertext, string secretHint, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<string?> GetMcpServerSecretCiphertextAsync(McpServerId id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task ClearMcpServerSecretAsync(McpServerId id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SetMcpServerOAuthTokenAsync(McpServerId id, string ciphertext, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<string?> GetMcpServerOAuthTokenCiphertextAsync(McpServerId id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task ClearMcpServerOAuthTokenAsync(McpServerId id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<McpServer?> GetMcpServerAsync(McpServerId serverId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<McpServer?> GetMcpServerByDisplayNameAsync(SpaceId spaceId, string displayName, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<McpServer>> ListMcpServersAsync(SpaceId spaceId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<PurgeableServer>> ListPurgeableMcpServersAsync(DateTimeOffset cutoff, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<ReapableSession>> ListReapableSessionsAsync(DateTimeOffset cutoff, DateTimeOffset sentinelSessionAgeCutoff, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task DeleteMcpServerAsync(McpServerId serverId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task UpsertAccessRequestAsync(AccessRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<AccessRequest?> GetAccessRequestAsync(AccessRequestId id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<AccessRequest?> GetPendingAccessRequestAsync(SpaceId spaceId, ConsumerId agentClientId, McpServerId mcpServerId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<AccessRequest>> ListAccessRequestsAsync(SpaceId spaceId, AccessRequestStatus? status = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task UpsertGrantAsync(Grant grant, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<Grant?> GetGrantAsync(GrantId id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<Grant?> GetActiveGrantAsync(SpaceId spaceId, ConsumerId agentClientId, McpServerId mcpServerId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<Grant>> ListGrantsAsync(SpaceId spaceId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task UpsertAgentClientAsync(Consumer agentClient, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<Consumer?> GetAgentClientAsync(ConsumerId id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task UpsertSessionAsync(RelaySession session, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<RelaySession>> ListSessionsAsync(SpaceId spaceId, bool includeClosed = true, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<(AccessRequest Request, Grant Grant)> ApproveAccessRequestAsync(AccessRequest request, Grant grant, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task AddTombstoneAsync(SpaceId spaceId, NodeId publisherNodeId, string displayName, Korat.Domain.Auth.UserId userId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> TombstoneExistsAsync(SpaceId spaceId, NodeId publisherNodeId, string displayName, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task RemoveTombstoneAsync(SpaceId spaceId, NodeId publisherNodeId, string displayName, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<McpServerTombstone>> ListTombstonesForNodeAsync(SpaceId spaceId, NodeId publisherNodeId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<Korat.Domain.Auth.UserId>> ListUserIdsWithOnlineServerAsync(DateTimeOffset staleCutoff, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> HasOnlineServerAsync(Korat.Domain.Auth.UserId userId, DateTimeOffset staleCutoff, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        // 029: Inference Points — not exercised by SessionTerminator.
        public Task<Korat.Domain.Entities.Space?> GetSpaceAsync(Korat.Domain.SpaceId spaceId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<Korat.Domain.SpaceId?> GetSpaceIdBySlugAsync(string slug, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<string?> GetSpaceSlugAsync(Korat.Domain.SpaceId spaceId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> TrySetSpaceSlugAsync(Korat.Domain.SpaceId spaceId, string slug, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        // T3/T4 secret methods — not exercised by SessionTerminator.
        // F6: user-profile methods — not exercised by SessionTerminator.
        public Task<Korat.Domain.Auth.User?> GetUserAsync(Korat.Domain.Auth.UserId userId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<Korat.Domain.Auth.User> UpdateUserDisplayNameAsync(Korat.Domain.Auth.UserId userId, string displayName, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<Korat.Domain.Auth.User> ReloadUserAsync(Korat.Domain.Auth.UserId userId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        // Hosted Agents (PR-1) — not exercised by SessionTerminator.
        public Task DeleteAgentAsync(AgentId id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task TouchThreadAsync(ThreadId id, DateTimeOffset updatedAt, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    /// <summary>
    /// Fake <see cref="ISessionGrain"/> that records which close reason it received.
    /// </summary>
    private sealed class RecordingSessionGrain : ISessionGrain
    {
        public SessionCloseReason? ClosedWith;

        public Task CloseAsync(SessionCloseReason reason)
        {
            ClosedWith = reason;
            return Task.CompletedTask;
        }

        public Task RevokeAsync() => CloseAsync(SessionCloseReason.Revoked);

        public Task<RelaySession> OpenAsync(GrantId g, ConsumerId a, McpServerId m, NodeId c, NodeId p,
            GatewayId h, SpaceId s, ConnectionId conn = default) => throw new NotSupportedException();
        public Task RecordBytesAsync(long c, long sv) => throw new NotSupportedException();
        public Task<RelaySession> GetAsync() => throw new NotSupportedException();
    }

    /// <summary>
    /// IRelayBackplane fake that records node and connection publishes and returns true for both.
    /// </summary>
    private sealed class FakeBackplaneForTerminator : IRelayBackplane
    {
        public readonly List<(NodeId Target, GatewayToNodeMessage Message)> NodePublishes = [];
        public readonly List<(ConnectionId Target, GatewayToNodeMessage Message)> ConnPublishes = [];

        public Task<bool> PublishToNodeAsync(NodeId target, GatewayToNodeMessage message, CancellationToken cancellationToken)
        {
            NodePublishes.Add((target, message));
            return Task.FromResult(true);
        }

        public Task<bool> PublishToConnectionAsync(ConnectionId target, GatewayToNodeMessage message, CancellationToken cancellationToken)
        {
            ConnPublishes.Add((target, message));
            return Task.FromResult(true);
        }

        public Task<IAsyncDisposable> SubscribeNodeAsync(NodeId nodeId, Func<GatewayToNodeMessage, CancellationToken, Task> onMessage, CancellationToken cancellationToken)
            => Task.FromResult<IAsyncDisposable>(new NullDisposable());

        public Task<IAsyncDisposable> SubscribeConnectionAsync(ConnectionId connectionId, Func<GatewayToNodeMessage, CancellationToken, Task> onMessage, CancellationToken cancellationToken)
            => Task.FromResult<IAsyncDisposable>(new NullDisposable());

        private sealed class NullDisposable : IAsyncDisposable
        {
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    /// <summary>No-op resolver — no local routes (all sends fall through to backplane).</summary>
    private sealed class NoResolver : ISessionRouteResolver
    {
        public Task<SessionRouteInfo?> ResolveAsync(SessionId sessionId, CancellationToken cancellationToken)
            => Task.FromResult<SessionRouteInfo?>(null);
    }

    private static McpToolCallInspector NoopInspector()
        => new(new NoopSink(), NullLogger<McpToolCallInspector>.Instance);

    private sealed class NoopSink : IMcpToolCallSink
    {
        public void Record(in ToolCallEvent toolCall) { }
    }

    private static SessionRoutingTable NewRoutingTable(FakeBackplaneForTerminator backplane)
        => new(backplane, new NoResolver(), NoopInspector(),
               _ => new RecordingSessionGrain(),
               _ => throw new NotSupportedException("not exercised by this test"),
               NullLogger<SessionRoutingTable>.Instance);

    // ── Tests ─────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Terminate_sends_CloseSession_to_both_ends_and_closes_grain()
    {
        var session = new RelaySession
        {
            Id = RelaySession, SpaceId = Space, GrantId = GrantId.New(),
            ConsumerId = ConsumerId.New(), McpServerId = Server,
            ClientNodeId = Agent, PublisherNodeId = Publisher,
            HomeGatewayId = new GatewayId("gw"), Status = SessionStatus.Active,
            StartedAt = DateTimeOffset.UtcNow, AgentConnectionId = AgentConn,
        };
        var repo = new FakeRepo { StoredSession = session };
        var backplane = new FakeBackplaneForTerminator();
        var table = NewRoutingTable(backplane);
        var grain = new RecordingSessionGrain();

        var terminator = new SessionTerminator(table, repo, _ => grain,
            NullLogger<SessionTerminator>.Instance);

        await terminator.TerminateSessionAsync(RelaySession, SessionCloseReason.Revoked, CancellationToken.None);

        // Publisher end addressed by NodeId, agent end by ConnectionId — both via backplane
        // (no local streams registered → fall-through to backplane in this fake).
        Assert.Contains(backplane.NodePublishes, p => p.Target == Publisher
            && p.Message.PayloadCase == GatewayToNodeMessage.PayloadOneofCase.CloseSession
            && p.Message.CloseSession.Reason == "Revoked");
        Assert.Contains(backplane.ConnPublishes, p => p.Target == AgentConn
            && p.Message.CloseSession.SessionId == RelaySession.Value);
        Assert.Equal(SessionCloseReason.Revoked, grain.ClosedWith);
    }

    [Fact]
    public async Task Terminate_unknown_session_is_a_noop()
    {
        var repo = new FakeRepo { StoredSession = null };
        var backplane = new FakeBackplaneForTerminator();
        var terminator = new SessionTerminator(NewRoutingTable(backplane), repo,
            _ => new RecordingSessionGrain(),
            NullLogger<SessionTerminator>.Instance);

        await terminator.TerminateSessionAsync(new SessionId("missing"),
            SessionCloseReason.Revoked, CancellationToken.None);

        Assert.Empty(backplane.NodePublishes);
        Assert.Empty(backplane.ConnPublishes);
    }

    [Fact]
    public async Task Terminate_already_closed_session_evicts_route_but_skips_sends()
    {
        var session = new RelaySession
        {
            Id = RelaySession, SpaceId = Space, GrantId = GrantId.New(),
            ConsumerId = ConsumerId.New(), McpServerId = Server,
            ClientNodeId = Agent, PublisherNodeId = Publisher,
            HomeGatewayId = new GatewayId("gw"), Status = SessionStatus.Closed,
            StartedAt = DateTimeOffset.UtcNow, AgentConnectionId = AgentConn,
        };
        var repo = new FakeRepo { StoredSession = session };
        var backplane = new FakeBackplaneForTerminator();
        var table = NewRoutingTable(backplane);
        // Seed a local route — terminate should evict it even for already-closed sessions.
        table.OpenSession(RelaySession, Agent, Publisher, Server, Space, AgentConn);

        var terminator = new SessionTerminator(table, repo, _ => new RecordingSessionGrain(),
            NullLogger<SessionTerminator>.Instance);

        await terminator.TerminateSessionAsync(RelaySession, SessionCloseReason.Revoked, CancellationToken.None);

        // No sends on an already-closed session.
        Assert.Empty(backplane.NodePublishes);
        Assert.Empty(backplane.ConnPublishes);
        // ToolRoute must be evicted regardless.
        Assert.Null(table.GetParticipants(RelaySession));
    }
}
