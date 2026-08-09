using Korat.Domain;
using Korat.Domain.Entities;
using Korat.GrainInterfaces;
using Korat.Domain.Persistence;
using Microsoft.EntityFrameworkCore;
using UserId = Korat.Domain.Auth.UserId;

namespace Korat.Grains;

/// <summary>
/// Grain Space: trust-метаданные (запросы, grants) + nodes + MCP-серверы.
/// Postgres — источник правды; in-memory списки — кэш после Hydrate.
/// Ключ grain = SpaceId (каждый Space получает изолированный grain-инстанс — SC-6).
///
/// CancellationToken: все методы grain используют CancellationToken.None вместо
/// проброса токена из вызывающего кода — Orleans не предоставляет CancellationToken
/// на уровне вызова grain (жизненный цикл управляется Orleans, не вызывающим кодом).
///
/// In-memory читаемые данные (SC-6): nodes, access-requests, grants — из памяти после Hydrate.
/// Pass-through reads (намеренные исключения из SC-6):
///   • ListSessionsAsync — сессии высококонвертируемы (счётчики байт, частые переходы
///     статусов), кеширование давало бы устаревшие данные; изоляция гарантируется
///     SpaceId-ключом репозитория.
///   • ListMcpServersAsync / GetMcpServerAsync — McpServerGrain является каноническим
///     владельцем статуса (Published/Disabled); фан-аут через McpServerGrain гарантирует
///     что DisableAsync всегда отражается в ответе.
/// </summary>
public sealed class SpaceGrain(IMetadataRepository repository) : Grain, ISpaceGrain
{
    private bool _hydrated;
    private readonly List<AccessRequest> _accessRequests = [];
    private readonly List<Grant> _grants = [];
    // Task-5 (F2, SC-6): real in-memory state for nodes.
    // Loaded once in HydrateAsync; subsequent reads serve from memory without DB round-trips.
    // The grain key IS the SpaceId, so grain-A physically cannot hold grain-B's rows.
    // Note: MCP servers are NOT cached here because McpServerGrain is their canonical owner
    // (DisableAsync mutates state there independently). ListMcpServersAsync reads through
    // McpServerGrains to always reflect current status.
    private readonly List<Node> _nodes = [];
    // Registry of McpServer IDs that belong to this Space (used as a membership set).
    // Status reads go through the canonical McpServerGrain to stay consistent with DisableAsync.
    private readonly HashSet<McpServerId> _mcpServerIds = [];

    // 029: Inference Point membership registry (mirrors _mcpServerIds).
    // Indexed by agentName (OrdinalIgnoreCase) for O(1) path-segment lookup.
    private readonly HashSet<InferencePointId> _inferencePointIds = [];
    private readonly Dictionary<string, InferencePointId> _pointIdByAgentName =
        new(StringComparer.OrdinalIgnoreCase);

    // Hosted Agents (PR-1): Agent membership registry (mirrors _inferencePointIds).
    // Canonical state (persona/status/etc.) always comes from IAgentGrain — this is
    // membership only, same pattern as _mcpServerIds/_inferencePointIds.
    private readonly HashSet<AgentId> _agentIds = [];

    // Channels (PR-2 Task 6): ChannelBinding membership registry (mirrors _agentIds).
    // Canonical state (Verified/Address/Purposes/etc.) always comes from IChannelBindingGrain —
    // this is membership only, used by ListChannelBindingsAsync's fan-out. ResolveInboundAsync
    // deliberately does NOT use this set — it's a targeted repository lookup, not an enumeration.
    private readonly HashSet<ChannelBindingId> _channelBindingIds = [];

    public override async Task OnActivateAsync(CancellationToken cancellationToken)
    {
        await HydrateAsync(cancellationToken);
        await base.OnActivateAsync(cancellationToken);
    }

    public async Task RegisterNodeAsync(Node node)
    {
        await HydrateAsync(CancellationToken.None);
        await repository.UpsertNodeAsync(node);
        UpsertCachedNode(node);
    }

    /// <summary>
    /// Convenience wrapper over <see cref="PublishMcpServerWithOutcomeAsync"/> for the many callers
    /// that only need the server. Anything that must react to a REDEFINITION (Р26: suspended
    /// permissions, sessions to terminate, the before/after pair the owner has to see) must use the
    /// outcome overload — this one deliberately drops that information rather than hiding it in
    /// grain state where a caller could forget to collect it.
    /// </summary>
    public async Task<McpServer?> PublishMcpServerAsync(NodeId publisherNodeId, string displayName, string command, string args) =>
        (await PublishMcpServerWithOutcomeAsync(publisherNodeId, displayName, command, args)).Server;

    public async Task<McpServerPublishOutcome> PublishMcpServerWithOutcomeAsync(
        NodeId publisherNodeId, string displayName, string command, string args)
    {
        await HydrateAsync(CancellationToken.None);
        var spaceId = new SpaceId(this.GetPrimaryKeyString());

        // Idempotency: McpServerGrain is the canonical status owner, so always query the
        // repository for an authoritative answer (includes Disabled status from DisableAsync).
        var existing = await repository.GetMcpServerByDisplayNameAsync(spaceId, displayName);
        if (existing is not null)
        {
            if (existing.Status != McpServerStatus.Disabled)
            {
                // Same publisher re-publishing on reconnect/restart: UPSERT — update command/args,
                // return the stable existing McpServerId so the daemon can build its routing table
                // from the ack without generating a new id on every reconnect.
                if (existing.PublisherNodeId == publisherNodeId)
                {
                    var serverGrain = GrainFactory.GetGrain<IMcpServerGrain>(existing.Id.Value);
                    var beforeDigest = McpServerDefinition.Digest(existing);
                    var updated = await serverGrain.UpdateCommandAsync(command, args);
                    _mcpServerIds.Add(updated.Id);
                    McpServerRedefinition? redefinition = null;

                    // Р26: this UPSERT is the escalation path we are closing. The stable id is a
                    // feature for the daemon's routing table, but it also means an owner's earlier
                    // approval would silently carry over to a DIFFERENT launch definition. Anyone
                    // who can present this node's credential could re-publish under an existing
                    // name and inherit its permissions.
                    //
                    // So: the definition changing suspends every permission for this server. The
                    // consumer's next RequestSession finds no active grant and raises a fresh
                    // access request, which is exactly the state the owner expects to be asked
                    // about. Live sessions are returned to the caller for termination — a session
                    // opened against the old definition must not keep running against the new one.
                    var afterDigest = McpServerDefinition.Digest(updated);
                    if (!string.Equals(beforeDigest, afterDigest, StringComparison.Ordinal))
                        redefinition = await SuspendGrantsForRedefinedServerAsync(
                            existing, updated, beforeDigest, afterDigest);

                    return new McpServerPublishOutcome(updated, redefinition);
                }

                // Different node owns this display name — conflict.
                throw new KoratDomainException(KoratErrorCode.DuplicateServerName);
            }

            // Existing record is Disabled. Disable is now ONLY reachable via the owner's HTTP
            // /disable (unpublish is a hard delete — see UnpublishMcpServerAsync), so a Disabled
            // server means the owner deliberately took it out of service but kept it in the system.
            // A same-node re-publish (daemon reconnect) must NOT silently re-enable it — that would
            // override the owner's intent. Return the existing record AS-IS (still Disabled, stable
            // id); the cloud already refuses to open sessions for a Disabled server (RequestSession),
            // so the daemon may map the id but no traffic flows until the owner re-enables it in the
            // UI. A Disabled record owned by a DIFFERENT node is treated as available (fall through).
            if (existing.PublisherNodeId == publisherNodeId)
            {
                _mcpServerIds.Add(existing.Id);
                return new McpServerPublishOutcome(existing, null);
            }
        }

        // Step-B (delete-tombstone): no live/disabled row exists for this (node, name). Before
        // minting a brand-new id, refuse if the owner deleted this exact (node, name) and the
        // node has NOT yet dropped it from its config (tombstone still present). This stops a
        // passive SyncMcpServers re-declaration from silently undoing an owner delete. The
        // tombstone is cleared by SyncMcpServersAsync once the node stops declaring the name.
        if (await repository.TombstoneExistsAsync(spaceId, publisherNodeId, displayName))
            return new McpServerPublishOutcome(null, null);

        var serverId = McpServerId.New();
        var serverGrain2 = GrainFactory.GetGrain<IMcpServerGrain>(serverId.Value);
        var published = await serverGrain2.PublishAsync(spaceId, publisherNodeId, displayName, command, args);
        // Track this server's id in our membership registry.
        _mcpServerIds.Add(published.Id);
        return new McpServerPublishOutcome(published, null);
    }

    public async Task UnpublishMcpServerAsync(NodeId publisherNodeId, McpServerId serverId)
    {
        await HydrateAsync(CancellationToken.None);

        // No-op: server not in this Space's membership registry.
        if (!_mcpServerIds.Contains(serverId))
            return;

        // Authoritative check: read from the grain so we get current status/ownership.
        var serverGrain = GrainFactory.GetGrain<IMcpServerGrain>(serverId.Value);
        var server = await serverGrain.GetAsync();

        // No-op: published by a different node — one node cannot unpublish another's server.
        if (server.PublisherNodeId != publisherNodeId)
            return;

        // `korat mcp remove` = HARD DELETE (distinct from the owner's HTTP /disable, which keeps
        // the server in the catalog as Disabled). Delete the row via the grain, then drop it from
        // the membership registry. Because the row is gone, a later grain rehydrate won't reload it
        // — the server stays removed for good (no Disabled "ghost" reappearing after deactivation).
        await serverGrain.RemoveAsync();
        _mcpServerIds.Remove(serverId);
    }

    public async Task<IReadOnlyList<Node>> ListNodesAsync()
    {
        await HydrateAsync(CancellationToken.None);
        // Fan out to each NodeGrain for live LastSeenAt (updated by every heartbeat).
        // _nodes holds the MEMBERSHIP set (which node ids belong to this space); the
        // canonical per-node state (LastSeenAt, Status) comes from NodeGrain.GetAsync()
        // — mirrors ListMcpServersAsync / McpServerGrain so the bug class is removed:
        // heartbeats update NodeGrain but NOT the SpaceGrain hydrate cache, so reading
        // the cache directly would return a frozen LastSeenAt and flip nodes Offline.
        var nodeIds = _nodes
            .Where(n => n.Id != default)
            .Select(n => n.Id)
            .ToList();
        var results = await Task.WhenAll(
            nodeIds.Select(id => GrainFactory.GetGrain<INodeGrain>(id.Value).GetAsync()));
        return results.ToList<Node>();
    }

    public async Task<Node?> SetNodeNoteAsync(NodeId nodeId, string? note)
    {
        await HydrateAsync(CancellationToken.None);

        // BOLA: the node must be a member of this Space's cached membership set (mirrors
        // GetMcpServerAsync's _mcpServerIds.Contains check). Foreign/unknown node → null,
        // which the endpoint maps to 404 — same response for both, no existence oracle.
        if (!_nodes.Any(n => n.Id == nodeId))
            return null;

        var updated = await GrainFactory.GetGrain<INodeGrain>(nodeId.Value).SetNoteAsync(note);
        UpsertCachedNode(updated);
        return updated;
    }

    /// <summary>
    /// Р26: the server behind an approved name changed, so every permission for it stops applying
    /// until the owner approves the new definition.
    ///
    /// <para>Suspension happens HERE, at redefinition, rather than only as a comparison at
    /// admission, for two reasons. The owner must see the server needing re-approval immediately
    /// in the console, not on whenever some consumer next connects. And a session already open
    /// against the old definition has to be closed — an admission-time check alone would leave it
    /// running, because admission does not run again for a live session.</para>
    ///
    /// <para>Returns the sessions to terminate rather than terminating them: session teardown
    /// lives in the Cloud host (SessionTerminator), which grains cannot reach. Same shape as
    /// <see cref="RevokeGrantAsync"/>.</para>
    /// </summary>
    private async Task<McpServerRedefinition> SuspendGrantsForRedefinedServerAsync(
        McpServer before, McpServer after, string beforeDigest, string afterDigest)
    {
        var now = DateTimeOffset.UtcNow;
        var suspended = new List<GrantId>();

        foreach (var grant in _grants.Where(g => g.McpServerId == after.Id && g.Status == GrantStatus.Active).ToList())
        {
            StateTransitions.SuspendGrantForRedefinition(grant, now);
            await repository.UpsertGrantAsync(grant);
            UpsertCachedGrant(grant);
            suspended.Add(grant.Id);
        }

        // Sessions are looked up per suspended grant (not per server) so the returned set is
        // exactly "sessions that were running under a permission we just took away".
        var sessions = new List<SessionId>();
        foreach (var grantId in suspended)
            sessions.AddRange(await FindLiveSessionsForGrantAsync(grantId));

        return new McpServerRedefinition(
            ServerId: after.Id,
            SpaceId: after.SpaceId,
            DisplayName: after.DisplayName,
            PreviousCommand: before.LaunchCommand,
            PreviousArguments: before.LaunchArguments,
            NewCommand: after.LaunchCommand,
            NewArguments: after.LaunchArguments,
            PreviousDigest: beforeDigest,
            NewDigest: afterDigest,
            SuspendedGrantIds: suspended,
            SessionsToTerminate: sessions.Distinct().ToList());
    }

    /// <summary>
    /// Convenience wrapper — see <see cref="PublishMcpServerAsync"/> for why both shapes exist.
    /// </summary>
    public async Task<IReadOnlyList<McpServer>> SyncMcpServersAsync(NodeId publisherNodeId, IReadOnlyList<McpServerSpec> servers) =>
        (await SyncMcpServersWithOutcomeAsync(publisherNodeId, servers)).Servers;

    public async Task<McpServerSyncOutcome> SyncMcpServersWithOutcomeAsync(NodeId publisherNodeId, IReadOnlyList<McpServerSpec> servers)
    {
        await HydrateAsync(CancellationToken.None);

        // 021 (Layer 1): declarative reconcile. ORDER MATTERS: upsert first so a server present
        // in the set is never transiently retired. See spec §Layer 1 for the reasoning.

        // Pass 1 — UPSERT: publish (or update) every server in the declared set.
        // Reuses the idempotent PublishMcpServerAsync logic: same (node, displayName) ⇒ same stable
        // McpServerId; IsAsserted is set to true inside PublishAsync / UpdateCommandAsync.
        // Step-B: PublishMcpServerAsync returns null when a delete-tombstone refuses the (node, name)
        // — skip those so a passive re-declaration cannot resurrect a deleted server.
        var upserted = new List<McpServer>(servers.Count);
        var redefinitions = new List<McpServerRedefinition>();
        foreach (var spec in servers)
        {
            var outcome = await PublishMcpServerWithOutcomeAsync(
                publisherNodeId, spec.DisplayName, spec.Command, spec.Args);
            if (outcome.Server is not null)
                upserted.Add(outcome.Server);
            if (outcome.Redefinition is not null)
                redefinitions.Add(outcome.Redefinition);
        }

        // Pass 2 — SOFT-RETIRE (AFTER upserts): find servers owned by this node that were NOT
        // declared in the sync set and flip IsAsserted = false. We do NOT hard-delete — a transient
        // empty/partial config can never cause permanent data loss. Hard delete is explicit only
        // (UnpublishMcpServerAsync / DeleteMcpServerAsync).
        var syncedNames = new HashSet<string>(servers.Select(s => s.DisplayName), StringComparer.Ordinal);

        // Identify owned server ids: read each via McpServerGrain.GetAsync() to get current state.
        // We only iterate ids currently in the membership registry (hard-deleted servers are already
        // absent from _mcpServerIds — their retire is a no-op, no re-create).
        var retireIds = _mcpServerIds.Where(id => id != default).ToList();
        var retireTasks = retireIds.Select(id => GrainFactory.GetGrain<IMcpServerGrain>(id.Value).GetAsync()).ToList();
        var retireStates = await Task.WhenAll(retireTasks);

        foreach (var (serverId, serverState) in retireIds.Zip(retireStates))
        {
            // Only retire servers owned by THIS node that are absent from the sync set.
            if (serverState.PublisherNodeId != publisherNodeId)
                continue;
            if (syncedNames.Contains(serverState.DisplayName))
                continue;

            // Soft-retire: flip IsAsserted = false. The row stays in the catalog (still visible as
            // Unavailable in the UI); the owner can hard-delete via DeleteMcpServerAsync if desired.
            await GrainFactory.GetGrain<IMcpServerGrain>(serverId.Value).SetAssertedAsync(false);
        }

        // Pass 3 — CLEAR tombstones (Step B): a tombstone whose name the node NO LONGER declares
        // means the node dropped it from its config — a future re-declaration is a genuine re-add,
        // so lift the block. A tombstone whose name IS still declared persists (the bug scenario:
        // Pass 1 already refused it above), keeping the delete durable.
        var spaceId = new SpaceId(this.GetPrimaryKeyString());
        foreach (var tombstone in await repository.ListTombstonesForNodeAsync(spaceId, publisherNodeId))
        {
            if (!syncedNames.Contains(tombstone.DisplayName))
                await repository.RemoveTombstoneAsync(spaceId, publisherNodeId, tombstone.DisplayName);
        }

        return new McpServerSyncOutcome(upserted, redefinitions);
    }

    public async Task<DeleteMcpServerResult> DeleteMcpServerAsync(McpServerId serverId, UserId userId, bool writeTombstone = true)
    {
        await HydrateAsync(CancellationToken.None);

        // 021 (Layer 3): owner-initiated hard delete — same mechanism as UnpublishMcpServerAsync
        // but without the publisherNodeId check (the owner may purge any server in their Space).
        if (!_mcpServerIds.Contains(serverId))
            return new DeleteMcpServerResult(false, []); // not in this Space → 404

        var spaceId = new SpaceId(this.GetPrimaryKeyString());

        // Step-B: capture (node, name) BEFORE removal so we can write the tombstone. Read through
        // the canonical McpServerGrain (same grain RemoveAsync will deactivate below).
        var serverGrain = GrainFactory.GetGrain<IMcpServerGrain>(serverId.Value);
        var toDelete = await serverGrain.GetAsync();

        // 022/Step-A: revoke all Active grants for this server so no orphaned Active grant rows
        // linger on the deleted id. Collect affected live sessions across those grants.
        var affected = new List<SessionId>();
        var liveSessions = await repository.ListSessionsAsync(spaceId, includeClosed: false, CancellationToken.None);
        var now = DateTimeOffset.UtcNow;
        foreach (var grant in _grants
            .Where(g => g.McpServerId == serverId && g.Status == GrantStatus.Active)
            .ToList())
        {
            StateTransitions.RevokeGrant(grant, userId, now);
            await repository.UpsertGrantAsync(grant);
            UpsertCachedGrant(grant);
            affected.AddRange(liveSessions
                .Where(s => s.GrantId == grant.Id
                    && s.Status is SessionStatus.Active or SessionStatus.Opening)
                .Select(s => s.Id));
        }

        await serverGrain.RemoveAsync();
        _mcpServerIds.Remove(serverId);

        // Step-B: write the tombstone (owner delete only — NOT the reaper). A returning node
        // whose server was reaped should be allowed to re-publish; an owner-deleted server should
        // not be silently resurrected by a passive SyncMcpServers re-declaration.
        if (writeTombstone)
            await repository.AddTombstoneAsync(spaceId, toDelete.PublisherNodeId, toDelete.DisplayName, userId);

        return new DeleteMcpServerResult(true, affected.Distinct().ToList());
    }

    // TTL reaper (#17, shipped in 024): a Published server whose owner node has been Offline past
    // the purge horizon is hard-deleted automatically by McpServerReaperService (BackgroundService,
    // 6h) → ListPurgeableMcpServersAsync → DeleteMcpServerAsync below. (The owner DELETE endpoint
    // remains the manual escape hatch.)

    public async Task<PruneAgentNodesResult> PruneAgentNodesAsync(UserId userId, DateTimeOffset olderThan)
    {
        // #167 review (fix 3): defense in depth. The HTTP endpoint (POST /api/nodes/prune) already
        // enforces olderThanDays >= 1 before computing this cutoff, so in normal operation this
        // never fires from the HTTP path — but a future internal/programmatic caller of this grain
        // method directly (bypassing the endpoint's validation) could pass a cutoff newer than
        // "1 day ago" and sweep just-created agent nodes. Guard here too, at the source of truth.
        if (olderThan > DateTimeOffset.UtcNow.AddDays(-1))
            throw new KoratDomainException(KoratErrorCode.Validation,
                "olderThan cutoff must be at least 1 day in the past.");

        await HydrateAsync(CancellationToken.None);
        var spaceId = new SpaceId(this.GetPrimaryKeyString());

        // #167 review (fix 4): accepted race — a node can reconnect (send Hello) in the window
        // between the liveNodes read below and this method actually calling INodeGrain.RemoveAsync()
        // on it. This self-heals and never leaves a broken/phantom node behind:
        //   - A reconnect goes through NodeGatewayService.HandleHelloAsync, which calls
        //     INodeGrain.ConnectAsync (unconditionally rebuilds _state, ignoring any prior
        //     RemoveAsync) followed by ISpaceGrain.RegisterNodeAsync (re-inserts into _nodes /
        //     Postgres). So a node that raced back online simply reappears via its next Hello,
        //     regardless of whether RemoveAsync ran on it moments earlier.
        //   - INodeGrain.HeartbeatAsync no-ops when _state.Id == default (its very first check),
        //     so a heartbeat landing on a just-removed grain does not resurrect a phantom row or
        //     throw.
        //   - Grants stay revoked and pending AccessRequests stay denied (this method's grant
        //     sweep above / pending-request sweep below) — a reconnect does NOT undo either. The
        //     agent has to go through re-approval, which is the correct, safe outcome even inside
        //     this race window.
        //
        // Canonical current state — mirrors ListNodesAsync's live fan-out: LastSeenAt is owned
        // by NodeGrain (heartbeats update it there, not the SpaceGrain hydrate cache), so a
        // cutoff check against the cached _nodes snapshot could prune a node that reconnected
        // moments ago.
        var nodeIds = _nodes.Where(n => n.Id != default).Select(n => n.Id).ToList();
        var liveNodes = await Task.WhenAll(
            nodeIds.Select(id => GrainFactory.GetGrain<INodeGrain>(id.Value).GetAsync()));

        // Publisher nodes are never pruned (v1 scope, #165) — only the one-shot `korat connect
        // --agent` identities. A node that has never connected (LastSeenAt null) falls back to
        // CreatedAt so a just-registered node isn't immediately eligible.
        var candidates = liveNodes
            .Where(n => n.Kind == NodeKind.Agent)
            .Where(n => (n.LastSeenAt ?? n.CreatedAt) < olderThan)
            .ToList();

        var prunedNames = new List<string>();
        var affectedSessions = new HashSet<SessionId>();

        if (candidates.Count > 0)
        {
            var now = DateTimeOffset.UtcNow;
            // Fetched once, reused across every candidate below (mirrors DeleteMcpServerAsync).
            var liveSessions = await repository.ListSessionsAsync(spaceId, includeClosed: false, CancellationToken.None);

            foreach (var node in candidates)
            {
                // A Grant only ever exists via an approved AccessRequest, and AccessRequest
                // records RequestedByNodeId — the node the agent connected FROM. So walking
                // _accessRequests for this NodeId reaches every ConsumerId that could hold a
                // Grant tied to this node, without a separate Consumer-by-NodeId repository
                // lookup. Revoke all their Active grants so no orphaned Active grant survives
                // the node's deletion (mirrors DeleteMcpServerAsync's grant sweep).
                var agentClientIds = _accessRequests
                    .Where(r => r.RequestedByNodeId == node.Id)
                    .Select(r => r.ConsumerId)
                    .ToHashSet();

                if (agentClientIds.Count > 0)
                {
                    foreach (var grant in _grants
                        .Where(g => agentClientIds.Contains(g.ConsumerId) && g.Status == GrantStatus.Active)
                        .ToList())
                    {
                        StateTransitions.RevokeGrant(grant, userId, now);
                        await repository.UpsertGrantAsync(grant);
                        UpsertCachedGrant(grant);
                        foreach (var sessionId in liveSessions
                            .Where(s => s.GrantId == grant.Id
                                && s.Status is SessionStatus.Active or SessionStatus.Opening)
                            .Select(s => s.Id))
                        {
                            affectedSessions.Add(sessionId);
                        }
                    }
                }

                // #167 review (fix 2): also deny any still-Pending AccessRequests filed FROM this
                // node — otherwise they're left dangling after the node is deleted, and the owner's
                // approvals UI would show a pending approval attributed to a now-gone NodeId with no
                // way to resolve it. Mirrors DenyAccessRequestAsync's own StateTransitions call, but
                // filtered to Pending only — StateTransitions.DenyAccessRequest throws on a request
                // that isn't Pending, and Approved/Denied requests here don't need touching.
                foreach (var request in _accessRequests
                    .Where(r => r.RequestedByNodeId == node.Id && r.Status == AccessRequestStatus.Pending)
                    .ToList())
                {
                    StateTransitions.DenyAccessRequest(request, userId, now);
                    await repository.UpsertAccessRequestAsync(request);
                    UpsertCachedRequest(request);
                }

                await GrainFactory.GetGrain<INodeGrain>(node.Id.Value).RemoveAsync();
                _nodes.RemoveAll(n => n.Id == node.Id);
                prunedNames.Add(node.DisplayName);
            }
        }

        return new PruneAgentNodesResult(prunedNames, affectedSessions.ToList());
    }

    public async Task<IReadOnlyList<McpServer>> ListMcpServersAsync()
    {
        await HydrateAsync(CancellationToken.None);
        // McpServerGrain is the canonical owner of each server's status (e.g. Disabled via HTTP).
        // We read through the McpServerGrains so DisableAsync side-effects are always reflected.
        // _mcpServerIds is the membership registry (which servers belong to this Space).
        var results = await Task.WhenAll(
            _mcpServerIds
                // Guard against registry/DB drift: skip default/empty-Id entries that would
                // activate a grain with a zero-value key and return a blank McpServer record.
                .Where(id => id != default)
                .Select(id => GrainFactory.GetGrain<IMcpServerGrain>(id.Value).GetAsync()));
        // Disabled servers ARE returned here (status reflected): the HTTP /disable path keeps a
        // server in the catalog as Disabled (management UI shows it + can re-enable). Servers
        // removed via `korat mcp remove` (UnpublishMcpServerAsync → hard delete) have no row and
        // are dropped from _mcpServerIds, so they never appear here — even after a rehydrate.
        // _mcpServerIds is a HashSet (no insertion order); sort by display name (then id) so the
        // console renders a deterministic, intentional order rather than hash-iteration order.
        return results
            .OrderBy(s => s.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(s => s.Id.Value)
            .ToList<McpServer>();
    }

    public async Task<McpServer?> GetMcpServerAsync(McpServerId serverId)
    {
        await HydrateAsync(CancellationToken.None);
        // Return null if the server is not a member of this Space — cross-Space → 404.
        if (!_mcpServerIds.Contains(serverId))
            return null;
        return await GrainFactory.GetGrain<IMcpServerGrain>(serverId.Value).GetAsync();
    }

    public async Task<IReadOnlyList<RelaySession>> ListSessionsAsync(bool includeClosed = true)
    {
        // Sessions are ephemeral — not cached in-memory because they change frequently
        // (byte counters, open/close transitions). Always read through the repository
        // but scoped to this grain's SpaceId so the grain key IS the isolation boundary.
        var spaceId = new SpaceId(this.GetPrimaryKeyString());
        return await repository.ListSessionsAsync(spaceId, includeClosed, CancellationToken.None);
    }

    public Task InvalidateCacheAsync()
    {
        // Drop the hydration flag so the next read triggers a full re-hydrate from the DB.
        // Called by POST /api/developer/reset after direct-delete operations.
        // The explicit .Clear() calls below are redundant — HydrateAsync clears each list
        // before refilling it — but are kept for safety: if a new in-memory list is ever
        // added to this grain, it MUST also be cleared here AND in HydrateAsync (the two
        // sites must stay in sync). After InvalidateCacheAsync the lists are empty until
        // the next read triggers re-hydration.
        _hydrated = false;
        _accessRequests.Clear();
        _grants.Clear();
        _nodes.Clear();
        _mcpServerIds.Clear();
        // 029: clear inference point membership registry.
        _inferencePointIds.Clear();
        _pointIdByAgentName.Clear();
        // Hosted Agents (PR-1): clear agent membership registry.
        _agentIds.Clear();
        // Channels (PR-2 Task 6): clear channel-binding membership registry.
        _channelBindingIds.Clear();
        return Task.CompletedTask;
    }

    public async Task<AccessRequest> CreateAccessRequestAsync(ConsumerId agentClientId, McpServerId mcpServerId, NodeId requestedByNodeId)
    {
        // 031: thin wrapper — MINIMAL RIPPLE. CreateAccessRequestWithStatusAsync is the real
        // implementation; this keeps the ~53 existing call sites (2 production + tests) untouched.
        var result = await CreateAccessRequestWithStatusAsync(agentClientId, mcpServerId, requestedByNodeId);
        return result.Request;
    }

    public async Task<CreateAccessRequestResult> CreateAccessRequestWithStatusAsync(
        ConsumerId agentClientId, McpServerId mcpServerId, NodeId requestedByNodeId)
    {
        await HydrateAsync(CancellationToken.None);
        var spaceId = new SpaceId(this.GetPrimaryKeyString());

        var server = await repository.GetMcpServerAsync(mcpServerId)
            ?? throw new KoratDomainException(KoratErrorCode.NotFound);

        // "You already have access, stop asking" — but Р26 made that judgement conditional. A
        // grant whose ApprovedDefinitionDigest no longer matches the server is not working access:
        // admission refuses to apply it. Refusing the request too would leave the consumer with no
        // way forward at all — no session, and no way to ask for one. So the guard now means what
        // it always intended: refuse only when the caller genuinely has usable access.
        //
        // The normal Р26 flow does not reach this (redefinition suspends the grant outright). This
        // matters for the paths that change a definition without going through SpaceGrain — the
        // HTTP PATCH edit, and grants approved before Р26 that carry no digest.
        var existingGrant = await repository.GetActiveGrantAsync(spaceId, agentClientId, mcpServerId);
        if (existingGrant is not null
            && string.Equals(
                existingGrant.ApprovedDefinitionDigest,
                McpServerDefinition.Digest(server),
                StringComparison.Ordinal))
        {
            throw new KoratDomainException(KoratErrorCode.AccessDenied);
        }

        var pending = await repository.GetPendingAccessRequestAsync(spaceId, agentClientId, mcpServerId);
        if (pending is not null)
            return new CreateAccessRequestResult(pending, Created: false); // одна pending-заявка на пару (агент, сервер)

        var request = new AccessRequest
        {
            Id = AccessRequestId.New(),
            SpaceId = spaceId,
            ConsumerId = agentClientId,
            McpServerId = mcpServerId,
            RequestedByNodeId = requestedByNodeId,
            PublisherNodeId = server.PublisherNodeId,
            RequestedAt = DateTimeOffset.UtcNow
        };

        await repository.UpsertAccessRequestAsync(request);
        _accessRequests.Add(request);
        return new CreateAccessRequestResult(request, Created: true);
    }

    public async Task<Grant> ApproveAccessRequestAsync(AccessRequestId accessRequestId, UserId userId)
    {
        await HydrateAsync(CancellationToken.None);
        var request = await FindAccessRequestAsync(accessRequestId);

        // Статус Disabled читаем из grain сервера, а не только из Postgres.
        var serverGrain = GrainFactory.GetGrain<IMcpServerGrain>(request.McpServerId.Value);
        var server = await serverGrain.GetAsync();
        if (server.Status == McpServerStatus.Disabled)
            throw new KoratDomainException(KoratErrorCode.ServerDisabled);

        var now = DateTimeOffset.UtcNow;
        var transitioned = StateTransitions.ApproveAccessRequest(request, userId, now);

        if (!transitioned)
        {
            // Request was already Approved — return the existing active grant (idempotent path).
            var spaceId = new SpaceId(this.GetPrimaryKeyString());
            var existingGrant = await repository.GetActiveGrantAsync(spaceId, request.ConsumerId, request.McpServerId);
            if (existingGrant is not null)
                return existingGrant;

            // Defense-in-depth: approved request with no active grant is an inconsistent state.
            throw new KoratDomainException(KoratErrorCode.InvalidStateTransition,
                "Request is Approved but no active grant exists.");
        }

        var grant = new Grant
        {
            Id = GrantId.New(),
            SpaceId = request.SpaceId,
            ConsumerId = request.ConsumerId,
            McpServerId = request.McpServerId,
            // Р26: pin WHAT was approved, not just which server id. `server` was read from the
            // server grain a few lines above, so this is the definition the owner is looking at
            // when they click approve. If it changes later, SessionAdmission refuses to apply
            // this grant and the owner is asked again.
            ApprovedDefinitionDigest = McpServerDefinition.Digest(server),
            CreatedFromAccessRequestId = request.Id,
            ApprovedByUserId = userId,
            ApprovedAt = now
        };

        await repository.ApproveAccessRequestAsync(request, grant);
        UpsertCachedRequest(request);
        _grants.Add(grant);
        return grant;
    }

    public async Task DenyAccessRequestAsync(AccessRequestId accessRequestId, UserId userId)
    {
        await HydrateAsync(CancellationToken.None);
        var request = await FindAccessRequestAsync(accessRequestId);
        StateTransitions.DenyAccessRequest(request, userId, DateTimeOffset.UtcNow);
        await repository.UpsertAccessRequestAsync(request);
        UpsertCachedRequest(request);
    }

    public async Task<IReadOnlyList<AccessRequest>> ListAccessRequestsAsync()
    {
        await HydrateAsync(CancellationToken.None);
        return _accessRequests.ToList();
    }

    public async Task<IReadOnlyList<Grant>> ListGrantsAsync()
    {
        await HydrateAsync(CancellationToken.None);
        return _grants.ToList();
    }

    public async Task<IReadOnlyList<SessionId>> RevokeGrantAsync(GrantId grantId, UserId userId)
    {
        await HydrateAsync(CancellationToken.None);
        var thisSpaceId = new SpaceId(this.GetPrimaryKeyString());
        var grant = _grants.SingleOrDefault(g => g.Id == grantId)
            ?? await repository.GetGrantAsync(grantId);

        // P2 defense-in-depth: reject a PK lookup that resolved a grant from a DIFFERENT space.
        // The in-memory _grants list is already space-scoped (hydrated from ListGrantsAsync(spaceId)),
        // but the fallback repository.GetGrantAsync is PK-only.  A cross-space caller (or a bug
        // routing the wrong grain key) must see the same "not found" response as a missing entity.
        if (grant is null || grant.SpaceId != thisSpaceId)
            throw new KoratDomainException(KoratErrorCode.NotFound);

        StateTransitions.RevokeGrant(grant, userId, DateTimeOffset.UtcNow);
        await repository.UpsertGrantAsync(grant);
        UpsertCachedGrant(grant);

        return await FindLiveSessionsForGrantAsync(grant.Id);
    }

    /// <summary>
    /// 022/Step-A: returns the Active/Opening session ids opened under a specific grant,
    /// matched on the persisted RelaySession.GrantId. Used by the endpoint to terminate them
    /// after a revoke so no session remains live against a revoked grant.
    /// </summary>
    private async Task<IReadOnlyList<SessionId>> FindLiveSessionsForGrantAsync(GrantId grantId)
    {
        var spaceId = new SpaceId(this.GetPrimaryKeyString());
        var sessions = await repository.ListSessionsAsync(spaceId, includeClosed: false, CancellationToken.None);
        return sessions
            .Where(s => s.GrantId == grantId
                && s.Status is SessionStatus.Active or SessionStatus.Opening)
            .Select(s => s.Id)
            .ToList();
    }

    // ── Inference Points (029) ─────────────────────────────────────────────────

    public async Task<McpServer> CreateHttpMcpServerAsync(
        string displayName, string remoteUrl, string authMode, string? authHeaderName, string? secretHint)
    {
        await HydrateAsync(CancellationToken.None);
        var spaceId = new SpaceId(this.GetPrimaryKeyString());

        // Mirrors CreateOutboundInferencePointAsync (PR #145 review B1): NOT idempotent by
        // displayName — any existing server (stdio_node OR http_cloud) with this name is a
        // hard conflict, never a silent overwrite.
        var existing = await repository.GetMcpServerByDisplayNameAsync(spaceId, displayName);
        if (existing is not null)
            throw new KoratDomainException(KoratErrorCode.DuplicateServerName);

        var serverId = McpServerId.New();
        var serverGrain = GrainFactory.GetGrain<IMcpServerGrain>(serverId.Value);
        var published = await serverGrain.PublishHttpCloudAsync(
            spaceId, displayName, remoteUrl, authMode, authHeaderName, secretHint);

        _mcpServerIds.Add(published.Id);
        return published;
    }

    // ── Hosted Agents (PR-1) ─────────────────────────────────────────────────────

    // ── Threads (PR-2 Task 4) ────────────────────────────────────────────────────
    // Threads are NOT cached in this grain's hydrate cache (unlike _agentIds/_inferencePointIds):
    // there is no bounded membership set to enumerate (one live thread per agent+owner, and
    // owners are not otherwise tracked here) — every call reads/writes straight through the
    // repository, scoped by this grain's SpaceId. ThreadGrain (Task 3) remains the canonical
    // owner of append/tail; this grain only resolves WHICH thread is live.

    // ── Channels (PR-2 Task 6) ───────────────────────────────────────────────────

    /// <summary>
    /// Загрузка всех данных Space из Postgres один раз за активацию grain.
    /// После этого reads обслуживаются из памяти (no DB round-trip) — F2, SC-6.
    /// </summary>
    private async Task HydrateAsync(CancellationToken cancellationToken)
    {
        if (_hydrated)
            return;

        var spaceId = new SpaceId(this.GetPrimaryKeyString());

        _accessRequests.Clear();
        _accessRequests.AddRange(await repository.ListAccessRequestsAsync(spaceId, cancellationToken: cancellationToken));

        _grants.Clear();
        _grants.AddRange(await repository.ListGrantsAsync(spaceId, cancellationToken));

        // Task-5 (F2): load nodes into memory so ListNodesAsync reads never hit the DB again.
        _nodes.Clear();
        _nodes.AddRange(await repository.ListNodesAsync(spaceId, cancellationToken));

        // Load ALL servers (including Disabled) into the membership registry. Disabled servers
        // stay in the system (visible in the catalog, re-enableable) — only `korat mcp remove`
        // (UnpublishMcpServerAsync → hard delete) removes the row, so a removed server simply has
        // no row to reload here and won't reappear.
        _mcpServerIds.Clear();
        _mcpServerIds.UnionWith(
            (await repository.ListMcpServersAsync(spaceId, cancellationToken))
            .Select(s => s.Id));

        _hydrated = true;
    }

    private async Task<AccessRequest> FindAccessRequestAsync(AccessRequestId accessRequestId)
    {
        var thisSpaceId = new SpaceId(this.GetPrimaryKeyString());
        var request = _accessRequests.SingleOrDefault(r => r.Id == accessRequestId)
            ?? await repository.GetAccessRequestAsync(accessRequestId);

        // P2 defense-in-depth: reject a PK lookup that resolved a request from a DIFFERENT space.
        // The in-memory _accessRequests list is already space-scoped (hydrated from
        // ListAccessRequestsAsync(spaceId)), but the repository fallback is PK-only.
        // Return the same "not found" signal regardless of whether the entity exists elsewhere.
        if (request is null || request.SpaceId != thisSpaceId)
            throw new KoratDomainException(KoratErrorCode.NotFound);

        return request;
    }

    private void UpsertCachedRequest(AccessRequest request)
    {
        var index = _accessRequests.FindIndex(r => r.Id == request.Id);
        if (index >= 0)
            _accessRequests[index] = request;
        else
            _accessRequests.Add(request);
    }

    private void UpsertCachedGrant(Grant grant)
    {
        var index = _grants.FindIndex(g => g.Id == grant.Id);
        if (index >= 0)
            _grants[index] = grant;
        else
            _grants.Add(grant);
    }

    private void UpsertCachedNode(Node node)
    {
        var index = _nodes.FindIndex(n => n.Id == node.Id);
        if (index >= 0)
            _nodes[index] = node;
        else
            _nodes.Add(node);
    }
}
