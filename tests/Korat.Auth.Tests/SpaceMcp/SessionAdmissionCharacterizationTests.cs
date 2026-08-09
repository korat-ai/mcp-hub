using Korat.Cloud.Gateways;
using Korat.Cloud.Gateways.Admission;
using Korat.Cloud.Observability;
using Korat.Cloud.Push;
using Korat.Domain;
using Korat.Domain.Entities;
using Korat.GrainInterfaces;
using Korat.Domain.Persistence;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Orleans;
using Orleans.Runtime;
// PR-2: Thread collides with System.Threading.Thread (global using) — alias to the domain entity,
// mirroring KoratDbContext.cs / SessionTerminatorTests.cs.

namespace Korat.Auth.Tests.SpaceMcp;

/// <summary>
/// Characterization tests for <see cref="SessionAdmission"/> — the gauntlet extracted from
/// <see cref="NodeGatewayService.HandleRequestSessionAsync"/> (2026-07-10 Space-MCP plan Task 2,
/// BLOCKER-3). Proves, branch by branch, that every check the gRPC gateway relied on is preserved
/// on the <see cref="ConsumerBindPolicy.NodeTofu"/> path, PLUS the new
/// <see cref="ConsumerBindPolicy.ServerMinted"/> fork the Space-MCP aggregator (Task 4) will use.
///
/// No Orleans cluster, no NATS — pure in-memory fakes (mirrors the stub-grain-factory style of
/// <c>SessionTerminatorTests</c> / <c>SessionRoutingTable</c>'s internal test constructor). The
/// companion end-to-end proof that the gRPC path is BYTE-FOR-BYTE unchanged is
/// <c>ConnectAccessRequestTests</c> (tests/Korat.Cloud.IntegrationTests), which must stay green.
/// </summary>
public class SessionAdmissionCharacterizationTests
{
    // ── Shared routing-table plumbing (mirrors SessionTerminatorTests' fakes) ──────────────────

    private static McpToolCallInspector NoopInspector() => new(new NoopSink(), NullLogger<McpToolCallInspector>.Instance);

    private sealed class NoopSink : IMcpToolCallSink
    {
        public void Record(in ToolCallEvent toolCall) { }
    }

    private sealed class NoResolver : ISessionRouteResolver
    {
        public Task<SessionRouteInfo?> ResolveAsync(SessionId sessionId, CancellationToken cancellationToken)
            => Task.FromResult<SessionRouteInfo?>(null);
    }

    private static SessionRoutingTable NewRoutingTable() =>
        new(new NullRelayBackplane(), new NoResolver(), NoopInspector(),
            _ => throw new NotSupportedException("not exercised by SessionAdmission — OpenSession never touches the grain factory"),
            _ => throw new NotSupportedException("not exercised by SessionAdmission"),
            NullLogger<SessionRoutingTable>.Instance);

    // ── Fake IMetadataRepository — only GetMcpServerAsync/GetActiveGrantAsync are exercised ────

    /// <summary>
    /// <see cref="SessionAdmission.AdmitAsync"/> calls <c>GetMcpServerAsync</c> up to twice
    /// (the initial load, and — only on the post-wake path — a re-fetch after the wake wait).
    /// <see cref="ServerAfterWake"/>, when set, is returned starting from the 2nd call so a test
    /// can simulate the server having changed state while the wake wait elapsed.
    /// </summary>
    private sealed class FakeMetadataRepo : IMetadataRepository
    {
        public McpServer? Server;
        public Grant? Grant;
        public McpServer? ServerAfterWake;
        private int _getMcpServerCalls;

        public Task<McpServer?> GetMcpServerAsync(McpServerId serverId, CancellationToken cancellationToken = default)
        {
            _getMcpServerCalls++;
            var result = _getMcpServerCalls > 1 && ServerAfterWake is not null ? ServerAfterWake : Server;
            return Task.FromResult(result);
        }

        /// <summary>
        /// Fable should-fix SF1: <see cref="SessionAdmission.AdmitAsync"/> calls
        /// <c>GetActiveGrantAsync</c> TWICE on the happy path — the initial gate (do we have a
        /// grant at all?) and, once past the availability/wake checks, the pre-open re-check
        /// (Step-A defense-in-depth — has the grant been revoked in the window since the initial
        /// gate?). When <see cref="RevokeGrantOnSecondCall"/> is set, the SECOND call onward
        /// returns null (simulating a revoke landing mid-open) so a test can prove the re-check
        /// actually runs and is load-bearing, not dead code.
        /// </summary>
        public bool RevokeGrantOnSecondCall;
        private int _getActiveGrantCalls;
        public int GetActiveGrantCallCount => _getActiveGrantCalls;

        public Task<Grant?> GetActiveGrantAsync(SpaceId spaceId, ConsumerId agentClientId, McpServerId mcpServerId, CancellationToken cancellationToken = default)
        {
            _getActiveGrantCalls++;
            var result = RevokeGrantOnSecondCall && _getActiveGrantCalls > 1 ? null : Grant;
            return Task.FromResult(result);
        }

        // ── Not exercised by SessionAdmission ──────────────────────────────────────
        public Task EnsureCreatedAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task UpsertNodeAsync(Node node, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<Node?> GetNodeAsync(NodeId nodeId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<Node>> ListNodesAsync(SpaceId spaceId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task DeleteNodeAsync(NodeId nodeId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task UpsertMcpServerAsync(McpServer server, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<McpServer?> GetMcpServerByDisplayNameAsync(SpaceId spaceId, string displayName, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<McpServer>> ListMcpServersAsync(SpaceId spaceId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<PurgeableServer>> ListPurgeableMcpServersAsync(DateTimeOffset cutoff, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task DeleteMcpServerAsync(McpServerId serverId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task AddTombstoneAsync(SpaceId spaceId, NodeId publisherNodeId, string displayName, Korat.Domain.Auth.UserId userId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> TombstoneExistsAsync(SpaceId spaceId, NodeId publisherNodeId, string displayName, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task RemoveTombstoneAsync(SpaceId spaceId, NodeId publisherNodeId, string displayName, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<McpServerTombstone>> ListTombstonesForNodeAsync(SpaceId spaceId, NodeId publisherNodeId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SetMcpServerSecretAsync(McpServerId id, string ciphertext, string secretHint, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<string?> GetMcpServerSecretCiphertextAsync(McpServerId id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task ClearMcpServerSecretAsync(McpServerId id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SetMcpServerOAuthTokenAsync(McpServerId id, string ciphertext, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<string?> GetMcpServerOAuthTokenCiphertextAsync(McpServerId id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task ClearMcpServerOAuthTokenAsync(McpServerId id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task UpsertAccessRequestAsync(AccessRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<AccessRequest?> GetAccessRequestAsync(AccessRequestId id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<AccessRequest?> GetPendingAccessRequestAsync(SpaceId spaceId, ConsumerId agentClientId, McpServerId mcpServerId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<AccessRequest>> ListAccessRequestsAsync(SpaceId spaceId, AccessRequestStatus? status = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task UpsertGrantAsync(Grant grant, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<Grant?> GetGrantAsync(GrantId id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<Grant>> ListGrantsAsync(SpaceId spaceId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task UpsertAgentClientAsync(Consumer agentClient, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<Consumer?> GetAgentClientAsync(ConsumerId id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task UpsertSessionAsync(RelaySession session, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<RelaySession?> GetSessionAsync(SessionId id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<RelaySession>> ListSessionsAsync(SpaceId spaceId, bool includeClosed = true, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<ReapableSession>> ListReapableSessionsAsync(DateTimeOffset cutoff, DateTimeOffset sentinelSessionAgeCutoff, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<(AccessRequest Request, Grant Grant)> ApproveAccessRequestAsync(AccessRequest request, Grant grant, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<Korat.Domain.Auth.UserId>> ListUserIdsWithOnlineServerAsync(DateTimeOffset staleCutoff, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> HasOnlineServerAsync(Korat.Domain.Auth.UserId userId, DateTimeOffset staleCutoff, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<Space?> GetSpaceAsync(SpaceId spaceId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<SpaceId?> GetSpaceIdBySlugAsync(string slug, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<string?> GetSpaceSlugAsync(SpaceId spaceId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> TrySetSpaceSlugAsync(SpaceId spaceId, string slug, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<Korat.Domain.Auth.User?> GetUserAsync(Korat.Domain.Auth.UserId userId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<Korat.Domain.Auth.User> UpdateUserDisplayNameAsync(Korat.Domain.Auth.UserId userId, string displayName, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<Korat.Domain.Auth.User> ReloadUserAsync(Korat.Domain.Auth.UserId userId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task DeleteAgentAsync(AgentId id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task TouchThreadAsync(ThreadId id, DateTimeOffset updatedAt, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    // ── Fake node directory — shared mutable state read by both the FakeGrainClient's INodeGrain
    // dispatch AND the FakeNodeGrainLocator NodeWakeCoordinator polls through, so a simulated wake
    // (flipping Status to Online) is visible to SessionAdmission's own re-fetch afterwards. ───────

    private sealed class FakeNodeState
    {
        public NodeStatus Status = NodeStatus.Online;
        public string? PushToken;

        // B1 guard (fix/push, restrict wake-path eligibility to APNs platforms): NodeWakeCoordinator
        // .TryWakeAsync now requires PushPlatform ∈ {apns, apns_sandbox} before it will even attempt
        // a send. Defaults to "apns" so every existing wake test in this file (written before that
        // guard existed) stays wake-eligible unless a test explicitly overrides it to prove the
        // platform gate itself (mirrors the guard's own real-world default: an offline node with a
        // push token but no recorded platform predates the fcm/apns distinction).
        public string PushPlatform = "apns";
        public bool HasE2eCapability;
    }

    private sealed class FakeNodeDirectory
    {
        private readonly Dictionary<string, FakeNodeState> _nodes = new();
        public FakeNodeState GetOrAdd(string id) => _nodes.TryGetValue(id, out var s) ? s : _nodes[id] = new FakeNodeState();
    }

    private sealed class FakeNodeGrain(FakeNodeDirectory dir, string id) : INodeGrain
    {
        public Task<Node> GetAsync()
        {
            var s = dir.GetOrAdd(id);
            return Task.FromResult(new Node { Id = new NodeId(id), Status = s.Status, PushToken = s.PushToken, PushPlatform = s.PushPlatform });
        }

        public Task<bool> HasCapabilityAsync(string capability) =>
            Task.FromResult(dir.GetOrAdd(id).HasE2eCapability && capability == "e2e-v1");

        public Task<Node> ConnectAsync(SpaceId spaceId, string displayName, GatewayId gatewayId, NodeKind kind = NodeKind.Publisher,
            IReadOnlyList<string>? capabilities = null, string? hostname = null, string? os = null, string? arch = null, string? cliVersion = null)
            => throw new NotSupportedException();
        public Task HeartbeatAsync(GatewayId gatewayId) => throw new NotSupportedException();
        public Task MarkOfflineAsync() => throw new NotSupportedException();
        public Task<Node> MarkOnlineForTestingAsync(SpaceId spaceId, string displayName) => throw new NotSupportedException();
        public Task RegisterPushTokenAsync(string token, string platform) => throw new NotSupportedException();
        public Task<Node> SetNoteAsync(string? note) => throw new NotSupportedException();
        public Task RemoveAsync() => throw new NotSupportedException();

        // 031 (mobile-push increment 2): not exercised by SessionAdmission.AdmitAsync itself —
        // only AccessRequestNotifier's SendToNodeAsync calls this (compare-and-clear on a dead
        // push token), and this suite's AccessRequestNotifier fake never reaches a real node.
        public Task ClearPushTokenIfMatchesAsync(string deadToken) => throw new NotSupportedException();
    }

    private sealed class FakeNodeGrainLocator(FakeNodeDirectory dir) : INodeGrainLocator
    {
        public INodeGrain GetNodeGrain(string nodeId) => new FakeNodeGrain(dir, nodeId);
    }

    /// <summary>Flips the target node Online when "sent" (token carries the NodeId value —
    /// opaque test data, mirrors how a real APNs token would be looked up server-side).</summary>
    private sealed class FakePushWakeSender(FakeNodeDirectory dir) : IPushWakeSender
    {
        public Task<PushWakeResult> SendWakeAsync(string token, string platform, CancellationToken ct)
        {
            dir.GetOrAdd(token).Status = NodeStatus.Online;
            return Task.FromResult(PushWakeResult.Sent);
        }
    }

    // ── Fake ConsumerGrain — mirrors ConsumerGrain.RegisterAsync's real, unconditional-
    // overwrite semantics (Korat.Grains/ConsumerGrain.cs:31-48) so a hijack-guard test proves
    // SessionAdmission itself refuses the mismatch BEFORE ever calling RegisterAsync. ─────────────

    private sealed class FakeAgentClientGrain(string id) : IConsumerGrain
    {
        public Consumer State = new() { Id = new ConsumerId(id) };
        public bool ThrowOnGet;

        public Task<Consumer> GetAsync() =>
            ThrowOnGet ? throw new InvalidOperationException("simulated grain lookup failure") : Task.FromResult(State);

        public Task<Consumer> RegisterAsync(SpaceId spaceId, NodeId nodeId, string displayName)
        {
            State = new Consumer
            {
                Id = State.Id,
                SpaceId = spaceId,
                NodeId = nodeId,
                DisplayName = displayName,
                CreatedAt = State.CreatedAt == default ? DateTimeOffset.UtcNow : State.CreatedAt,
                UpdatedAt = DateTimeOffset.UtcNow,
            };
            return Task.FromResult(State);
        }
    }

    private sealed class FakeSpaceGrain : ISpaceGrain
    {
        public int CreateAccessRequestCalls;

        public Task<AccessRequest> CreateAccessRequestAsync(ConsumerId agentClientId, McpServerId mcpServerId, NodeId requestedByNodeId)
        {
            CreateAccessRequestCalls++;
            return Task.FromResult(NewAccessRequest(agentClientId, mcpServerId, requestedByNodeId));
        }

        // 031 (mobile-push increment 2, relocated into SessionAdmission): the real call site
        // SessionAdmission.AdmitAsync now uses — mirrors SpaceGrain's own CreateAccessRequestAsync
        // being a thin wrapper over this. Always reports Created=true (this fake never simulates
        // the idempotent-replay branch — no test here exercises that path).
        public Task<CreateAccessRequestResult> CreateAccessRequestWithStatusAsync(ConsumerId agentClientId, McpServerId mcpServerId, NodeId requestedByNodeId)
        {
            CreateAccessRequestCalls++;
            return Task.FromResult(new CreateAccessRequestResult(
                NewAccessRequest(agentClientId, mcpServerId, requestedByNodeId), Created: true));
        }

        private static AccessRequest NewAccessRequest(ConsumerId agentClientId, McpServerId mcpServerId, NodeId requestedByNodeId) => new()
        {
            Id = AccessRequestId.New(),
            SpaceId = default,
            ConsumerId = agentClientId,
            McpServerId = mcpServerId,
            RequestedByNodeId = requestedByNodeId,
            PublisherNodeId = default,
            RequestedAt = DateTimeOffset.UtcNow,
        };

        public Task RegisterNodeAsync(Node node) => throw new NotSupportedException();
        public Task<McpServer?> PublishMcpServerAsync(NodeId publisherNodeId, string displayName, string command, string args) => throw new NotSupportedException();
        public Task<McpServerPublishOutcome> PublishMcpServerWithOutcomeAsync(NodeId publisherNodeId, string displayName, string command, string args) => throw new NotSupportedException();
        public Task<IReadOnlyList<Node>> ListNodesAsync() => throw new NotSupportedException();
        public Task<Node?> SetNodeNoteAsync(NodeId nodeId, string? note) => throw new NotSupportedException();
        public Task<IReadOnlyList<McpServer>> ListMcpServersAsync() => throw new NotSupportedException();
        public Task<McpServer?> GetMcpServerAsync(McpServerId serverId) => throw new NotSupportedException();
        public Task<IReadOnlyList<RelaySession>> ListSessionsAsync(bool includeClosed = true) => throw new NotSupportedException();
        public Task UnpublishMcpServerAsync(NodeId publisherNodeId, McpServerId serverId) => throw new NotSupportedException();
        public Task InvalidateCacheAsync() => throw new NotSupportedException();
        public Task<McpServer> CreateHttpMcpServerAsync(string displayName, string remoteUrl, string authMode, string? authHeaderName, string? secretHint) => throw new NotSupportedException();
        public Task<Grant> ApproveAccessRequestAsync(AccessRequestId accessRequestId, Korat.Domain.Auth.UserId userId) => throw new NotSupportedException();
        public Task DenyAccessRequestAsync(AccessRequestId accessRequestId, Korat.Domain.Auth.UserId userId) => throw new NotSupportedException();
        public Task<IReadOnlyList<Grant>> ListGrantsAsync() => throw new NotSupportedException();
        public Task<IReadOnlyList<SessionId>> RevokeGrantAsync(GrantId grantId, Korat.Domain.Auth.UserId userId) => throw new NotSupportedException();
        public Task<IReadOnlyList<AccessRequest>> ListAccessRequestsAsync() => throw new NotSupportedException();
        public Task<IReadOnlyList<McpServer>> SyncMcpServersAsync(NodeId publisherNodeId, IReadOnlyList<McpServerSpec> servers) => throw new NotSupportedException();
        public Task<McpServerSyncOutcome> SyncMcpServersWithOutcomeAsync(NodeId publisherNodeId, IReadOnlyList<McpServerSpec> servers) => throw new NotSupportedException();
        public Task<DeleteMcpServerResult> DeleteMcpServerAsync(McpServerId serverId, Korat.Domain.Auth.UserId userId, bool writeTombstone = true) => throw new NotSupportedException();
        public Task<PruneAgentNodesResult> PruneAgentNodesAsync(Korat.Domain.Auth.UserId userId, DateTimeOffset olderThan) => throw new NotSupportedException();
        public Task<DeleteAgentResult> DeleteAgentAsync(AgentId id, Korat.Domain.Auth.UserId userId) => throw new NotSupportedException();
        public Task<ThreadId> GetOrCreateLiveThreadAsync(AgentId agentId, string principalUserId) => throw new NotSupportedException();
        public Task<ThreadId> ResetThreadAsync(AgentId agentId, string principalUserId) => throw new NotSupportedException();
    }

    private sealed class FakeGatewayGrain : IGatewayGrain
    {
        public Task<GatewayId> AssignSessionHomeAsync() => Task.FromResult(new GatewayId("gw-1"));
        public Task RegisterAsync() => throw new NotSupportedException();
        public Task HeartbeatAsync() => throw new NotSupportedException();
        public Task<Gateway> GetAsync() => throw new NotSupportedException();
    }

    private sealed class FakeSessionGrain : ISessionGrain
    {
        public Task<RelaySession> OpenAsync(GrantId grantId, ConsumerId agentClientId, McpServerId mcpServerId,
            NodeId clientNodeId, NodeId publisherNodeId, GatewayId homeGatewayId, SpaceId spaceId, ConnectionId agentConnectionId = default)
            => Task.FromResult(new RelaySession
            {
                Id = SessionId.New(), SpaceId = spaceId, GrantId = grantId, ConsumerId = agentClientId,
                McpServerId = mcpServerId, ClientNodeId = clientNodeId, PublisherNodeId = publisherNodeId,
                HomeGatewayId = homeGatewayId, Status = SessionStatus.Active, StartedAt = DateTimeOffset.UtcNow,
                AgentConnectionId = agentConnectionId,
            });

        public Task RecordBytesAsync(long clientToServer, long serverToClient) => throw new NotSupportedException();
        public Task CloseAsync(SessionCloseReason reason) => throw new NotSupportedException();
        public Task RevokeAsync() => throw new NotSupportedException();
        public Task<RelaySession> GetAsync() => throw new NotSupportedException();
    }

    /// <summary>Minimal IClusterClient — dispatches GetGrain{T}(string) by grain-interface type
    /// to the fakes above (mirrors InferenceKeyServiceTests.FakeGrainClient). Every other member
    /// throws NotSupportedException; SessionAdmission never calls them.</summary>
    private sealed class FakeGrainClient(FakeNodeDirectory nodeDir) : IClusterClient
    {
        public readonly Dictionary<string, FakeAgentClientGrain> AgentClients = new();
        public readonly Dictionary<string, FakeSpaceGrain> Spaces = new();
        public readonly Dictionary<string, FakeGatewayGrain> Gateways = new();
        public readonly Dictionary<string, FakeSessionGrain> Sessions = new();

        public IServiceProvider ServiceProvider => throw new NotSupportedException();

        public TGrainInterface GetGrain<TGrainInterface>(string primaryKey, string? grainClassNamePrefix = null)
            where TGrainInterface : IGrainWithStringKey
        {
            if (typeof(TGrainInterface) == typeof(IConsumerGrain))
            {
                if (!AgentClients.TryGetValue(primaryKey, out var g))
                    AgentClients[primaryKey] = g = new FakeAgentClientGrain(primaryKey);
                return (TGrainInterface)(object)g;
            }
            if (typeof(TGrainInterface) == typeof(ISpaceGrain))
            {
                if (!Spaces.TryGetValue(primaryKey, out var g))
                    Spaces[primaryKey] = g = new FakeSpaceGrain();
                return (TGrainInterface)(object)g;
            }
            if (typeof(TGrainInterface) == typeof(INodeGrain))
            {
                return (TGrainInterface)(object)new FakeNodeGrain(nodeDir, primaryKey);
            }
            if (typeof(TGrainInterface) == typeof(IGatewayGrain))
            {
                if (!Gateways.TryGetValue(primaryKey, out var g))
                    Gateways[primaryKey] = g = new FakeGatewayGrain();
                return (TGrainInterface)(object)g;
            }
            if (typeof(TGrainInterface) == typeof(ISessionGrain))
            {
                if (!Sessions.TryGetValue(primaryKey, out var g))
                    Sessions[primaryKey] = g = new FakeSessionGrain();
                return (TGrainInterface)(object)g;
            }
            throw new NotSupportedException($"Grain type {typeof(TGrainInterface).Name} not supported.");
        }

        // Unused by SessionAdmission — satisfy IClusterClient / IGrainFactory.
        public TGrainInterface GetGrain<TGrainInterface>(Guid primaryKey, string? grainClassNamePrefix = null)
            where TGrainInterface : IGrainWithGuidKey => throw new NotSupportedException();
        public TGrainInterface GetGrain<TGrainInterface>(long primaryKey, string? grainClassNamePrefix = null)
            where TGrainInterface : IGrainWithIntegerKey => throw new NotSupportedException();
        public TGrainInterface GetGrain<TGrainInterface>(Guid primaryKey, string keyExtension, string? grainClassNamePrefix = null)
            where TGrainInterface : IGrainWithGuidCompoundKey => throw new NotSupportedException();
        public TGrainInterface GetGrain<TGrainInterface>(long primaryKey, string keyExtension, string? grainClassNamePrefix = null)
            where TGrainInterface : IGrainWithIntegerCompoundKey => throw new NotSupportedException();
        public TGrainInterface GetGrain<TGrainInterface>(GrainId grainId)
            where TGrainInterface : IAddressable => throw new NotSupportedException();
        public IAddressable GetGrain(GrainId grainId) => throw new NotSupportedException();
        public IAddressable GetGrain(GrainId grainId, GrainInterfaceType interfaceType) => throw new NotSupportedException();
        public IGrain GetGrain(Type grainInterfaceType, Guid grainPrimaryKey) => throw new NotSupportedException();
        public IGrain GetGrain(Type grainInterfaceType, long grainPrimaryKey) => throw new NotSupportedException();
        public IGrain GetGrain(Type grainInterfaceType, string grainPrimaryKey) => throw new NotSupportedException();
        public IGrain GetGrain(Type grainInterfaceType, Guid primaryKey, string keyExtension) => throw new NotSupportedException();
        public IGrain GetGrain(Type grainInterfaceType, long primaryKey, string keyExtension) => throw new NotSupportedException();
        public IAddressable GetGrain(Type grainInterfaceType, IdSpan grainKey) => throw new NotSupportedException();
        public IAddressable GetGrain(Type grainInterfaceType, IdSpan grainKey, string grainClassNamePrefix) => throw new NotSupportedException();
        public TGrainObserverInterface CreateObjectReference<TGrainObserverInterface>(IGrainObserver obj)
            where TGrainObserverInterface : IGrainObserver => throw new NotSupportedException();
        public void DeleteObjectReference<TGrainObserverInterface>(IGrainObserver obj)
            where TGrainObserverInterface : IGrainObserver => throw new NotSupportedException();
    }

    // ── Harness ──────────────────────────────────────────────────────────────────────────────

    private sealed class Harness
    {
        public readonly FakeMetadataRepo Repo = new();
        public readonly FakeNodeDirectory NodeDir = new();
        public readonly FakeGrainClient GrainClient;
        public readonly SessionAdmission Admission;

        /// <param name="apnsConfigured">True mints a NodeWakeCoordinator with APNs "configured"
        /// (KeyId set) so TryWakeAsync actually attempts a send + poll for an offline node.
        /// False (default) mirrors the common dev/test posture — TryWakeAsync returns false
        /// immediately for ANY offline node, zero added latency.</param>
        /// <param name="wakeWaitSeconds">Overrides ApnsOptions.WakeWaitSeconds (default 12) so a
        /// "node stays offline through the whole wake window" test (SF3) doesn't burn 12 real
        /// seconds per run — only meaningful when <paramref name="apnsConfigured"/> is true.</param>
        public Harness(bool apnsConfigured = false, int? wakeWaitSeconds = null)
        {
            GrainClient = new FakeGrainClient(NodeDir);
            var sender = new FakePushWakeSender(NodeDir);
            var wake = new NodeWakeCoordinator(
                sender,
                new FakeNodeGrainLocator(NodeDir),
                Options.Create(new ApnsOptions
                {
                    KeyId = apnsConfigured ? "test-key" : null,
                    WakeWaitSeconds = wakeWaitSeconds ?? 12,
                }),
                NullLogger<NodeWakeCoordinator>.Instance);
            Admission = new SessionAdmission(
                GrainClient,
                Repo,
                NewRoutingTable(),
                wake,
                NewNotifier(),
                new ConfigurationBuilder().Build(),
                NullLogger<SessionAdmission>.Instance);
        }

        /// <summary>
        /// 031 (mobile-push increment 2, relocated into SessionAdmission): a real
        /// <see cref="AccessRequestNotifier"/> over an empty-locator fake — the notify trigger is
        /// fire-and-forget (Task.Run inside SessionAdmission.AdmitAsync) and this suite doesn't
        /// assert on push delivery, so a locator that reports zero nodes/servers is enough to make
        /// NotifyOwnerOfNewRequestAsync a fast, exception-free no-op (mirrors AccessRequestNotifier's
        /// own "no push-enabled device" short-circuit).
        /// </summary>
        private static AccessRequestNotifier NewNotifier() => new(
            new EmptyAccessRequestGrainLocator(),
            new NullAlertPushSender(),
            Options.Create(new AccessRequestNotifyOptions()),
            NullLogger<AccessRequestNotifier>.Instance);

        private sealed class EmptyAccessRequestGrainLocator : IAccessRequestGrainLocator
        {
            public Task<IReadOnlyList<Node>> ListNodesAsync(string spaceId) =>
                Task.FromResult<IReadOnlyList<Node>>(Array.Empty<Node>());

            public Task<IReadOnlyList<McpServer>> ListMcpServersAsync(string spaceId) =>
                Task.FromResult<IReadOnlyList<McpServer>>(Array.Empty<McpServer>());

            public INodeGrain GetNodeGrain(string nodeId) => throw new NotSupportedException();

            public Task<Dictionary<string, string>> ResolveAgentNamesAsync(
                IEnumerable<string> agentClientIds, Dictionary<string, string> nodeNames, CancellationToken ct) =>
                Task.FromResult(new Dictionary<string, string>(StringComparer.Ordinal));
        }
    }

    // ── Test fixtures ────────────────────────────────────────────────────────────────────────

    private static McpServer NewServer(SpaceId spaceId, NodeId publisherNodeId,
        McpServerStatus status = McpServerStatus.Published, bool isAsserted = true, McpServerId? id = null) => new()
    {
        Id = id ?? McpServerId.New(),
        SpaceId = spaceId,
        PublisherNodeId = publisherNodeId,
        DisplayName = "srv",
        Transport = "Stdio",
        Status = status,
        IsAsserted = isAsserted,
    };

    /// <summary>
    /// Р26 changed what "an active grant" means: it must also carry the digest of the server
    /// definition it was approved for, or admission treats it as not applying. The fixture takes
    /// the SERVER rather than just its id so the digest is derived from the same object the
    /// harness will serve — a hard-coded digest here would make every one of these tests pass
    /// against an admission that never compares anything.
    /// </summary>
    private static Grant NewGrant(SpaceId spaceId, ConsumerId agentClientId, McpServer server) => new()
    {
        Id = GrantId.New(),
        SpaceId = spaceId,
        ConsumerId = agentClientId,
        McpServerId = server.Id,
        Status = GrantStatus.Active,
        ApprovedDefinitionDigest = McpServerDefinition.Digest(server),
        ApprovedByUserId = default,
        ApprovedAt = DateTimeOffset.UtcNow,
    };

    // ═══════════════════════════════════════════════════════════════════════════════════════
    // NodeTofu branch table (mirrors NodeGatewayService.cs:1016,1022,1041,1055,1140,1170,1194,
    // 1247,1081 — one assertion per preserved branch).
    // ═══════════════════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task NodeTofu_ServerNotFound_DeniedNotFound()
    {
        var h = new Harness();
        var principal = new ConsumerPrincipal(ConsumerId.New(), SpaceId.New(), ConnectionId.New(), NodeId.New(), null, ConsumerBindPolicy.NodeTofu);

        var result = await h.Admission.AdmitAsync(McpServerId.New(), principal, CancellationToken.None);

        var denied = Assert.IsType<AdmissionResult.Denied>(result);
        Assert.Equal(KoratError.Message(KoratErrorCode.NotFound), denied.Reason);
    }

    [Fact]
    public async Task NodeTofu_Disabled_Denied()
    {
        var h = new Harness();
        var spaceId = SpaceId.New();
        var server = NewServer(spaceId, NodeId.New(), status: McpServerStatus.Disabled);
        h.Repo.Server = server;
        var principal = new ConsumerPrincipal(ConsumerId.New(), spaceId, ConnectionId.New(), NodeId.New(), null, ConsumerBindPolicy.NodeTofu);

        var result = await h.Admission.AdmitAsync(server.Id, principal, CancellationToken.None);

        var denied = Assert.IsType<AdmissionResult.Denied>(result);
        Assert.Equal(KoratError.Message(KoratErrorCode.ServerDisabled), denied.Reason);
    }

    [Fact]
    public async Task NodeTofu_NeedsReauth_Denied()
    {
        var h = new Harness();
        var spaceId = SpaceId.New();
        var server = NewServer(spaceId, NodeId.New(), status: McpServerStatus.NeedsReauth);
        h.Repo.Server = server;
        var principal = new ConsumerPrincipal(ConsumerId.New(), spaceId, ConnectionId.New(), NodeId.New(), null, ConsumerBindPolicy.NodeTofu);

        var result = await h.Admission.AdmitAsync(server.Id, principal, CancellationToken.None);

        var denied = Assert.IsType<AdmissionResult.Denied>(result);
        Assert.Equal(KoratError.Message(KoratErrorCode.ServerNeedsReauth), denied.Reason);
    }

    [Fact]
    public async Task NodeTofu_F45_CrossSpaceMismatch_DeniedAsNotFound()
    {
        var h = new Harness();
        var server = NewServer(SpaceId.New(), NodeId.New()); // server lives in a DIFFERENT space
        h.Repo.Server = server;
        var principal = new ConsumerPrincipal(ConsumerId.New(), SpaceId.New(), ConnectionId.New(), NodeId.New(), null, ConsumerBindPolicy.NodeTofu);

        var result = await h.Admission.AdmitAsync(server.Id, principal, CancellationToken.None);

        var denied = Assert.IsType<AdmissionResult.Denied>(result);
        Assert.Equal(KoratError.Message(KoratErrorCode.NotFound), denied.Reason);
    }

    [Fact]
    public async Task NodeTofu_NoGrant_Pending()
    {
        var h = new Harness();
        var spaceId = SpaceId.New();
        var server = NewServer(spaceId, NodeId.New());
        h.Repo.Server = server;
        h.Repo.Grant = null;
        var principal = new ConsumerPrincipal(ConsumerId.New(), spaceId, ConnectionId.New(), NodeId.New(), null, ConsumerBindPolicy.NodeTofu);

        var result = await h.Admission.AdmitAsync(server.Id, principal, CancellationToken.None);

        Assert.IsType<AdmissionResult.Pending>(result);
        Assert.Equal(1, h.GrainClient.Spaces.Single().Value.CreateAccessRequestCalls);
    }

    [Fact]
    public async Task NodeTofu_GrantButNotAsserted_Denied()
    {
        var h = new Harness();
        var spaceId = SpaceId.New();
        var agentClientId = ConsumerId.New();
        var server = NewServer(spaceId, NodeId.New(), isAsserted: false);
        h.Repo.Server = server;
        h.Repo.Grant = NewGrant(spaceId, agentClientId, server);
        var principal = new ConsumerPrincipal(agentClientId, spaceId, ConnectionId.New(), NodeId.New(), null, ConsumerBindPolicy.NodeTofu);

        var result = await h.Admission.AdmitAsync(server.Id, principal, CancellationToken.None);

        var denied = Assert.IsType<AdmissionResult.Denied>(result);
        Assert.Equal(KoratError.Message(KoratErrorCode.ServerUnavailable), denied.Reason);
    }

    [Fact]
    public async Task NodeTofu_GrantAssertedOfflineNonWakeable_Denied()
    {
        var h = new Harness(apnsConfigured: false); // unconfigured APNs -> TryWakeAsync always false
        var spaceId = SpaceId.New();
        var agentClientId = ConsumerId.New();
        var publisherNodeId = NodeId.New();
        h.NodeDir.GetOrAdd(publisherNodeId.Value).Status = NodeStatus.Offline;
        var server = NewServer(spaceId, publisherNodeId);
        h.Repo.Server = server;
        h.Repo.Grant = NewGrant(spaceId, agentClientId, server);
        var principal = new ConsumerPrincipal(agentClientId, spaceId, ConnectionId.New(), NodeId.New(), null, ConsumerBindPolicy.NodeTofu);

        var result = await h.Admission.AdmitAsync(server.Id, principal, CancellationToken.None);

        var denied = Assert.IsType<AdmissionResult.Denied>(result);
        Assert.Equal(KoratError.Message(KoratErrorCode.ServerUnavailable), denied.Reason);
    }

    [Fact]
    public async Task NodeTofu_GrantAssertedOnline_Opened()
    {
        var h = new Harness();
        var spaceId = SpaceId.New();
        var agentClientId = ConsumerId.New();
        var requestingNodeId = NodeId.New();
        var publisherNodeId = NodeId.New();
        h.NodeDir.GetOrAdd(publisherNodeId.Value).HasE2eCapability = true;
        var server = NewServer(spaceId, publisherNodeId);
        h.Repo.Server = server;
        h.Repo.Grant = NewGrant(spaceId, agentClientId, server);
        var principal = new ConsumerPrincipal(
            agentClientId,
            spaceId,
            ConnectionId.New(),
            requestingNodeId,
            null,
            ConsumerBindPolicy.NodeTofu,
            "cursor");

        var result = await h.Admission.AdmitAsync(server.Id, principal, CancellationToken.None);

        var opened = Assert.IsType<AdmissionResult.Opened>(result);
        // NodeTofu actually queries publisher e2e capability (not forced false) — proves the
        // ServerMinted fork (below) is a real behavioural difference, not a coincidence.
        Assert.True(opened.PeerSupportsE2e);
        // TOFU-bound to the requesting node as a side effect of opening.
        Assert.Equal(requestingNodeId, h.GrainClient.AgentClients[agentClientId.Value].State.NodeId);
        Assert.Equal("cursor", h.GrainClient.AgentClients[agentClientId.Value].State.DisplayName);
    }

    [Fact]
    public async Task NodeTofu_BoundToDifferentNode_DeniedMismatch()
    {
        var h = new Harness();
        var spaceId = SpaceId.New();
        var agentClientId = ConsumerId.New();
        var server = NewServer(spaceId, NodeId.New());
        h.Repo.Server = server;
        // Pre-bind the agent-client to a DIFFERENT node than the one presenting it now.
        h.GrainClient.AgentClients[agentClientId.Value] = new FakeAgentClientGrain(agentClientId.Value);
        await h.GrainClient.AgentClients[agentClientId.Value].RegisterAsync(spaceId, NodeId.New(), "someone-else");
        var principal = new ConsumerPrincipal(agentClientId, spaceId, ConnectionId.New(), NodeId.New(), null, ConsumerBindPolicy.NodeTofu);

        var result = await h.Admission.AdmitAsync(server.Id, principal, CancellationToken.None);

        var denied = Assert.IsType<AdmissionResult.Denied>(result);
        Assert.Equal("agent_client_node_mismatch", denied.Reason);
    }

    [Fact]
    public async Task NodeTofu_ReservedCaggNamespace_Denied()
    {
        var h = new Harness();
        var spaceId = SpaceId.New();
        var server = NewServer(spaceId, NodeId.New());
        h.Repo.Server = server;
        var principal = new ConsumerPrincipal(new ConsumerId("cagg_hijack123"), spaceId, ConnectionId.New(), NodeId.New(), null, ConsumerBindPolicy.NodeTofu);

        var result = await h.Admission.AdmitAsync(server.Id, principal, CancellationToken.None);

        var denied = Assert.IsType<AdmissionResult.Denied>(result);
        Assert.Equal("reserved_agent_client_namespace", denied.Reason);
        // Never even looked up — the guard runs BEFORE any grain call.
        Assert.False(h.GrainClient.AgentClients.ContainsKey("cagg_hijack123"));
    }

    [Fact]
    public async Task NodeTofu_AgentClientLookupFailed_FailsClosed()
    {
        var h = new Harness();
        var spaceId = SpaceId.New();
        var agentClientId = ConsumerId.New();
        var server = NewServer(spaceId, NodeId.New());
        h.Repo.Server = server;
        h.GrainClient.AgentClients[agentClientId.Value] = new FakeAgentClientGrain(agentClientId.Value) { ThrowOnGet = true };
        var principal = new ConsumerPrincipal(agentClientId, spaceId, ConnectionId.New(), NodeId.New(), null, ConsumerBindPolicy.NodeTofu);

        var result = await h.Admission.AdmitAsync(server.Id, principal, CancellationToken.None);

        var denied = Assert.IsType<AdmissionResult.Denied>(result);
        Assert.Equal("agent_client_lookup_failed", denied.Reason);
    }

    [Fact]
    public async Task NodeTofu_PostWakeReValidation_ServerDisabledAfterWake_Denied()
    {
        var h = new Harness(apnsConfigured: true);
        var spaceId = SpaceId.New();
        var agentClientId = ConsumerId.New();
        var publisherNodeId = NodeId.New();
        // Offline + wake-eligible (push token present, APNs "configured").
        var nodeState = h.NodeDir.GetOrAdd(publisherNodeId.Value);
        nodeState.Status = NodeStatus.Offline;
        nodeState.PushToken = publisherNodeId.Value; // FakePushWakeSender flips Status keyed by token
        var server = NewServer(spaceId, publisherNodeId);
        h.Repo.Server = server;
        h.Repo.Grant = NewGrant(spaceId, agentClientId, server);
        // The server was disabled WHILE the wake wait elapsed — must be re-validated, not assumed.
        h.Repo.ServerAfterWake = NewServer(spaceId, publisherNodeId, status: McpServerStatus.Disabled, id: server.Id);
        var principal = new ConsumerPrincipal(agentClientId, spaceId, ConnectionId.New(), NodeId.New(), null, ConsumerBindPolicy.NodeTofu);

        var result = await h.Admission.AdmitAsync(server.Id, principal, CancellationToken.None);

        var denied = Assert.IsType<AdmissionResult.Denied>(result);
        Assert.Equal(KoratError.Message(KoratErrorCode.ServerUnavailable), denied.Reason);
    }

    [Fact]
    public async Task NodeTofu_GrantAssertedOfflineWakeable_StaysOffline_DeniedNodeWaking()
    {
        // Fable should-fix SF3(a): distinguish the NodeWaking deny reason (a wake WAS attempted,
        // per wakeCoordinator.IsConfigured + a non-empty PushToken) from ServerUnavailable (no wake
        // attempted at all — see NodeTofu_GrantAssertedOfflineNonWakeable_Denied above). Shrink the
        // wake window to 1s so this doesn't burn the real 12s default per run.
        var h = new Harness(apnsConfigured: true, wakeWaitSeconds: 1);
        var spaceId = SpaceId.New();
        var agentClientId = ConsumerId.New();
        var publisherNodeId = NodeId.New();
        var nodeState = h.NodeDir.GetOrAdd(publisherNodeId.Value);
        nodeState.Status = NodeStatus.Offline;
        // Push token deliberately does NOT equal publisherNodeId.Value — FakePushWakeSender flips
        // Status on the directory entry KEYED BY THE TOKEN, so a mismatched token means the push
        // is "sent" (wakeWasAttempted=true) but the real publisher node's own entry never flips
        // Online — it stays offline through the whole wake window, exactly like a real APNs push
        // that fires but the device never wakes/reconnects in time.
        nodeState.PushToken = "unrelated-token-" + Guid.NewGuid().ToString("N")[..8];
        var server = NewServer(spaceId, publisherNodeId);
        h.Repo.Server = server;
        h.Repo.Grant = NewGrant(spaceId, agentClientId, server);
        var principal = new ConsumerPrincipal(agentClientId, spaceId, ConnectionId.New(), NodeId.New(), null, ConsumerBindPolicy.NodeTofu);

        var result = await h.Admission.AdmitAsync(server.Id, principal, CancellationToken.None);

        var denied = Assert.IsType<AdmissionResult.Denied>(result);
        Assert.Equal(KoratError.Message(KoratErrorCode.NodeWaking), denied.Reason);
    }

    [Fact]
    public async Task NodeTofu_GrantAssertedOfflineWakeable_WakesOnline_Opened()
    {
        // Fable should-fix SF3(b): the wake-SUCCESS happy path — distinct from
        // NodeTofu_PostWakeReValidation_ServerDisabledAfterWake_Denied (which wakes successfully
        // but then denies on a POST-wake server-state change). Here nothing changes post-wake, so
        // the session must actually Open.
        var h = new Harness(apnsConfigured: true);
        var spaceId = SpaceId.New();
        var agentClientId = ConsumerId.New();
        var publisherNodeId = NodeId.New();
        var nodeState = h.NodeDir.GetOrAdd(publisherNodeId.Value);
        nodeState.Status = NodeStatus.Offline;
        nodeState.PushToken = publisherNodeId.Value; // FakePushWakeSender flips Status keyed by token
        var server = NewServer(spaceId, publisherNodeId);
        h.Repo.Server = server;
        h.Repo.Grant = NewGrant(spaceId, agentClientId, server);
        var principal = new ConsumerPrincipal(agentClientId, spaceId, ConnectionId.New(), NodeId.New(), null, ConsumerBindPolicy.NodeTofu);

        var result = await h.Admission.AdmitAsync(server.Id, principal, CancellationToken.None);

        Assert.IsType<AdmissionResult.Opened>(result);
    }

    [Fact]
    public async Task NodeTofu_GrantRevokedBetweenInitialGateAndPreOpenRecheck_DeniedAccessDenied()
    {
        // Fable should-fix SF1: the pre-open re-check (SessionAdmission.cs ~288-293, "Step-A
        // defense-in-depth") re-queries GetActiveGrantAsync immediately before OpenAsync to close
        // the window between the initial gate and session-open. FakeMetadataRepo previously
        // returned the SAME Grant on every call, so this re-check was never exercised — deleting
        // it kept every other test green. RevokeGrantOnSecondCall simulates a revoke landing in
        // that window: 1st call (initial gate) sees the grant, 2nd call (pre-open re-check) sees
        // it gone.
        var h = new Harness();
        var spaceId = SpaceId.New();
        var agentClientId = ConsumerId.New();
        var server = NewServer(spaceId, NodeId.New());
        h.Repo.Server = server;
        h.Repo.Grant = NewGrant(spaceId, agentClientId, server);
        h.Repo.RevokeGrantOnSecondCall = true;
        var principal = new ConsumerPrincipal(agentClientId, spaceId, ConnectionId.New(), NodeId.New(), null, ConsumerBindPolicy.NodeTofu);

        var result = await h.Admission.AdmitAsync(server.Id, principal, CancellationToken.None);

        var denied = Assert.IsType<AdmissionResult.Denied>(result);
        Assert.Equal(KoratError.Message(KoratErrorCode.AccessDenied), denied.Reason);
        // Proves the re-check actually ran a SECOND time (not just the initial gate).
        Assert.Equal(2, h.Repo.GetActiveGrantCallCount);
    }

    // ═══════════════════════════════════════════════════════════════════════════════════════
    // ServerMinted fork (Space-MCP aggregator, Task 4 consumer).
    // ═══════════════════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task ServerMinted_BindsToAggregatorSentinelNode()
    {
        var h = new Harness();
        var spaceId = SpaceId.New();
        var agentClientId = new ConsumerId("cagg_" + Guid.NewGuid().ToString("N")[..20]);
        var server = NewServer(spaceId, NodeId.New());
        h.Repo.Server = server;
        h.Repo.Grant = NewGrant(spaceId, agentClientId, server);
        var principal = new ConsumerPrincipal(agentClientId, spaceId, ConnectionId.New(), NodeId.New(), null, ConsumerBindPolicy.ServerMinted);

        var result = await h.Admission.AdmitAsync(server.Id, principal, CancellationToken.None);

        Assert.IsType<AdmissionResult.Opened>(result);
        Assert.Equal(SessionAdmission.AggregatorSentinelNodeId, h.GrainClient.AgentClients[agentClientId.Value].State.NodeId);
        Assert.Equal("Connected MCP client", h.GrainClient.AgentClients[agentClientId.Value].State.DisplayName);
    }

    [Fact]
    public async Task ServerMinted_AlreadyBoundToNonSentinelNode_DeniedHijackGuard()
    {
        var h = new Harness();
        var spaceId = SpaceId.New();
        var agentClientId = new ConsumerId("cagg_" + Guid.NewGuid().ToString("N")[..20]);
        var server = NewServer(spaceId, NodeId.New());
        h.Repo.Server = server;
        // Simulate the identity already bound to some OTHER (non-sentinel) node — a hijack
        // attempt, or a stale bind from before this identity existed.
        h.GrainClient.AgentClients[agentClientId.Value] = new FakeAgentClientGrain(agentClientId.Value);
        await h.GrainClient.AgentClients[agentClientId.Value].RegisterAsync(spaceId, NodeId.New(), "not-the-aggregator");
        var principal = new ConsumerPrincipal(agentClientId, spaceId, ConnectionId.New(), NodeId.New(), null, ConsumerBindPolicy.ServerMinted);

        var result = await h.Admission.AdmitAsync(server.Id, principal, CancellationToken.None);

        var denied = Assert.IsType<AdmissionResult.Denied>(result);
        Assert.Equal("agent_client_node_mismatch", denied.Reason);
    }

    [Fact]
    public async Task ServerMinted_NonCaggAgentClientId_DeniedReservedNamespace()
    {
        // Fable should-fix SF2: the MIRROR of NodeTofu_ReservedCaggNamespace_Denied above — a
        // ServerMinted caller presenting a non-cagg_ ConsumerId must be denied BEFORE any grain
        // lookup/bind. Without this guard a ServerMinted admission could bind an arbitrary
        // CLI-namespace identity to AggregatorSentinelNodeId, and because
        // ConsumerGrain.RegisterAsync overwrites unconditionally, a race with a NodeTofu
        // caller's first-use TOFU bind could hijack the real node's identity.
        var h = new Harness();
        var spaceId = SpaceId.New();
        var server = NewServer(spaceId, NodeId.New());
        h.Repo.Server = server;
        var principal = new ConsumerPrincipal(new ConsumerId("not-cagg-namespaced"), spaceId, ConnectionId.New(), NodeId.New(), null, ConsumerBindPolicy.ServerMinted);

        var result = await h.Admission.AdmitAsync(server.Id, principal, CancellationToken.None);

        var denied = Assert.IsType<AdmissionResult.Denied>(result);
        Assert.Equal("reserved_agent_client_namespace", denied.Reason);
        // Never even looked up — the guard runs BEFORE any grain call.
        Assert.False(h.GrainClient.AgentClients.ContainsKey("not-cagg-namespaced"));
    }

    [Fact]
    public async Task ServerMinted_ForcesPeerSupportsE2eFalse()
    {
        var h = new Harness();
        var spaceId = SpaceId.New();
        var agentClientId = new ConsumerId("cagg_" + Guid.NewGuid().ToString("N")[..20]);
        var publisherNodeId = NodeId.New();
        // The publisher DOES support e2e — proves the false is FORCED, not incidental.
        h.NodeDir.GetOrAdd(publisherNodeId.Value).HasE2eCapability = true;
        var server = NewServer(spaceId, publisherNodeId);
        h.Repo.Server = server;
        h.Repo.Grant = NewGrant(spaceId, agentClientId, server);
        var principal = new ConsumerPrincipal(agentClientId, spaceId, ConnectionId.New(), NodeId.New(), null, ConsumerBindPolicy.ServerMinted);

        var result = await h.Admission.AdmitAsync(server.Id, principal, CancellationToken.None);

        var opened = Assert.IsType<AdmissionResult.Opened>(result);
        Assert.False(opened.PeerSupportsE2e);
    }
}
