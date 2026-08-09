using Korat.Domain;
using Korat.Domain.Entities;
using Korat.GrainInterfaces;
using Korat.Domain.Persistence;
using Korat.Relay.V1;
using Orleans;

namespace Korat.Cloud.Gateways;

/// <summary>
/// Step-A security: tears down an in-flight relay session in response to a lifecycle event
/// (grant revoke / server delete). Pushes a CloseSession control message to BOTH ends — the
/// publisher (by NodeId) and the agent bridge (by ConnectionId) — each over the local fast
/// path or the backplane, so the close reaches the owning silo in a multi-silo cluster. Then
/// evicts the local route cache and persists Closed on the SessionGrain.
///
/// Idempotent / best-effort: an already-closed or unknown session is a no-op. Sends are
/// best-effort (a dead peer stream must not throw into the caller).
///
/// OnActivateAsync force-close hazard: we deliberately call ONLY CloseAsync on the SessionGrain
/// (single-activation cluster-wide). We do NOT enumerate/activate other grains speculatively,
/// so the activation-time force-close in SessionGrain.OnActivateAsync is not tripped by teardown —
/// the route eviction + dual CloseSession are what stop traffic; CloseAsync only persists the
/// terminal state.
/// </summary>
public sealed class SessionTerminator
{
    private readonly SessionRoutingTable _routingTable;
    private readonly IMetadataRepository _repository;
    private readonly Func<string, ISessionGrain> _sessionGrainFactory;
    private readonly ILogger<SessionTerminator> _logger;

    /// <summary>
    /// Test constructor: inject a grain factory delegate to avoid needing a real IClusterClient.
    /// </summary>
    internal SessionTerminator(
        SessionRoutingTable routingTable,
        IMetadataRepository repository,
        Func<string, ISessionGrain> sessionGrainFactory,
        ILogger<SessionTerminator> logger)
    {
        _routingTable = routingTable;
        _repository = repository;
        _sessionGrainFactory = sessionGrainFactory;
        _logger = logger;
    }

    /// <summary>
    /// Production DI constructor: resolves the session grain factory off IClusterClient.
    /// Registered as Singleton so it is shared between the gRPC gateway and minimal-API endpoints
    /// over the same routing state.
    /// </summary>
    public SessionTerminator(
        SessionRoutingTable routingTable,
        IMetadataRepository repository,
        IClusterClient clusterClient,
        ILogger<SessionTerminator> logger)
        : this(routingTable, repository,
               key => clusterClient.GetGrain<ISessionGrain>(key),
               logger)
    {
    }

    /// <summary>
    /// Tear down a relay session: push CloseSession to both ends, evict the local route, and
    /// persist the terminal state on the SessionGrain. Safe to call multiple times (idempotent).
    /// </summary>
    public async Task TerminateSessionAsync(SessionId sessionId, SessionCloseReason reason, CancellationToken cancellationToken)
    {
        var session = await _repository.GetSessionAsync(sessionId, cancellationToken);
        if (session is null)
            return; // unknown — nothing to tear down

        // Already terminal — only ensure the local route is gone (idempotent, no-op otherwise).
        if (session.Status is SessionStatus.Closed or SessionStatus.Failed)
        {
            _routingTable.CloseSession(sessionId);
            return;
        }

        var reasonText = reason.ToString();

        // Increment 1 (Crux Finding 5): resolve IsHttpCloud via the routing table (cache-first,
        // falls back to the Orleans resolver — the same GetRouteAsync Task 5 already grounds
        // IsHttpCloud resolution in) so this authoritative close path releases the consumer's
        // upstream MCP session inside HttpMcpProxyGrain instead of notifying a publisher node
        // that, for an http_cloud session, was never there to begin with.
        var route = await _routingTable.GetRouteAsync(sessionId, cancellationToken);
        if (route is { IsHttpCloud: true } r)
        {
            await _routingTable.CloseHttpCloudConsumerSessionAsync(r.McpServerId, sessionId, cancellationToken);
        }
        else if (!string.IsNullOrEmpty(session.PublisherNodeId.Value))
        {
            // 1. Publisher end (by NodeId). Best-effort.
            await SendBestEffortAsync(
                () => _routingTable.SendToNodeAsync(session.PublisherNodeId,
                    BuildClose(sessionId, reasonText), cancellationToken),
                $"publisher node={session.PublisherNodeId.Value}");
        }

        // 2. Agent end (by ConnectionId). Best-effort; may be "" for pre-migration rows.
        if (!string.IsNullOrEmpty(session.AgentConnectionId.Value))
            await SendBestEffortAsync(
                () => _routingTable.SendToConnectionAsync(session.AgentConnectionId,
                    BuildClose(sessionId, reasonText), cancellationToken),
                $"agent conn={session.AgentConnectionId.Value}");

        // 3. Evict the local route so any in-flight ForwardFrameAsync returns false here.
        _routingTable.CloseSession(sessionId);

        // 4. Persist terminal state on the grain (single cluster-wide activation — safe across silos).
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(2));
            await _sessionGrainFactory(sessionId.Value).CloseAsync(reason).WaitAsync(cts.Token);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                "Terminate: grain CloseAsync failed session={SessionId} errorType={ErrorType}",
                sessionId.Value, ex.GetType().Name);
        }
    }

    private static GatewayToNodeMessage BuildClose(SessionId id, string reason) => new()
    {
        CloseSession = new CloseSession { SessionId = id.Value, Reason = reason }
    };

    private async Task SendBestEffortAsync(Func<Task<bool>> send, string target)
    {
        try
        {
            await send();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                "Terminate: CloseSession send failed target={Target} errorType={ErrorType}",
                target, ex.GetType().Name);
        }
    }
}
