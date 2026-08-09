using Korat.Domain;
using Korat.Domain.Entities;
using Korat.GrainInterfaces;
using Korat.Domain.Persistence;
using Microsoft.Extensions.Logging;

namespace Korat.Grains;

/// <summary>
/// Grain сессии: метаданные relay (статус, байты, причина закрытия).
/// MCP-payload в БД не попадает.
/// </summary>
public sealed class SessionGrain(IMetadataRepository repository, ILogger<SessionGrain> logger) : Grain, ISessionGrain
{
    private RelaySession _state = new()
    {
        Id = default,
        SpaceId = default,
        GrantId = default,
        ConsumerId = default,
        McpServerId = default,
        ClientNodeId = default,
        PublisherNodeId = default,
        HomeGatewayId = default,
        StartedAt = DateTimeOffset.UtcNow
    };

    public override async Task OnActivateAsync(CancellationToken cancellationToken)
    {
        var sessionId = new SessionId(this.GetPrimaryKeyString());
        var persisted = await repository.GetSessionAsync(sessionId, cancellationToken);
        if (persisted is not null)
        {
            _state = persisted;

            // TODO(cloud-M4): A smarter liveness gate would query node presence/heartbeat
            // freshness and avoid closing sessions that are still live on another silo
            // (benign cross-silo reactivation). That gate requires stream-presence awareness
            // (e.g. HomeGatewayGrain.HasLiveRelayAsync) which is out of scope for the
            // current hardening pass. NodePresenceRules + IsSessionAbandonedAsync are kept
            // in place (NodePresenceTests, SessionReaperServiceTests) and can be wired in
            // here once stream-presence is surfaced through the grain interface.
            //
            // For now we revert to the known-good origin/dev behavior: force-close any
            // Active/Opening session on activation. The idle-reactivation UX bug this
            // reintroduces is pre-existing and accepted until the liveness gate lands.
            if (_state.Status is SessionStatus.Active or SessionStatus.Opening)
            {
                logger.LogInformation(
                    "SessionGrain.OnActivateAsync: force-closing session={SessionId} on activation (ServiceRestart)",
                    sessionId.Value);
                _state.Status = SessionStatus.Closed;
                _state.CloseReason = SessionCloseReason.ServiceRestart;
                _state.EndedAt = DateTimeOffset.UtcNow;
                await repository.UpsertSessionAsync(_state, cancellationToken);
            }
        }

        await base.OnActivateAsync(cancellationToken);
    }

    public async Task<RelaySession> OpenAsync(
        GrantId grantId,
        ConsumerId agentClientId,
        McpServerId mcpServerId,
        NodeId clientNodeId,
        NodeId publisherNodeId,
        GatewayId homeGatewayId,
        SpaceId spaceId,
        ConnectionId agentConnectionId = default)
    {
        var now = DateTimeOffset.UtcNow;
        _state = new RelaySession
        {
            Id = new SessionId(this.GetPrimaryKeyString()),
            SpaceId = spaceId,
            GrantId = grantId,
            ConsumerId = agentClientId,
            McpServerId = mcpServerId,
            ClientNodeId = clientNodeId,
            PublisherNodeId = publisherNodeId,
            HomeGatewayId = homeGatewayId,
            Status = SessionStatus.Active,
            StartedAt = now,
            // 022/Step-A: AgentConnectionId is now persisted via EntityMapping.ToRecord
            // (EF column on Sessions table) so any silo can address the agent stream for
            // cross-silo teardown. UpsertSessionAsync below writes it to the DB.
            AgentConnectionId = agentConnectionId
        };

        await repository.UpsertSessionAsync(_state);
        return _state;
    }

    /// <summary>
    /// Increments the session's byte counters by the given deltas and persists to Postgres so
    /// the values survive grain read-through and appear in the console Sessions view.
    /// Called by SessionRoutingTable's periodic flush (every ~5 s) and on session close.
    /// </summary>
    public async Task RecordBytesAsync(long clientToServer, long serverToClient)
    {
        // H1: guard against writing a default/empty-id row — this grain may be
        // activated speculatively for a session that was never persisted (e.g. the
        // routing table flushed bytes for a session that was already closed and
        // removed before the grain had a chance to open). In that case _state.Id
        // is still the zero-value (default(SessionId)), so we skip the upsert.
        if (_state.Id == default)
            return;

        _state.BytesClientToServer += clientToServer;
        _state.BytesServerToClient += serverToClient;
        await repository.UpsertSessionAsync(_state);
    }

    public async Task CloseAsync(SessionCloseReason reason)
    {
        StateTransitions.CloseSession(_state, reason, DateTimeOffset.UtcNow);
        await repository.UpsertSessionAsync(_state);
    }

    public Task RevokeAsync() => CloseAsync(SessionCloseReason.Revoked);

    public Task<RelaySession> GetAsync() => Task.FromResult(_state);
}
