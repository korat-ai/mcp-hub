using Korat.Domain;
using Korat.Domain.Entities;
using Korat.Domain.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Korat.Persistence;

public sealed class EfMetadataRepository(IDbContextFactory<KoratDbContext> dbContextFactory) : IMetadataRepository
{
    public async Task EnsureCreatedAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        // Relational providers (Npgsql) get the full migration pipeline so the schema
        // matches the committed migrations (string-typed Status columns, filtered
        // Pending index, etc.). InMemory provider doesn't support migrations and
        // needs EnsureCreatedAsync to build the schema from the model snapshot.
        if (db.Database.IsRelational())
            await db.Database.MigrateAsync(cancellationToken);
        else
            await db.Database.EnsureCreatedAsync(cancellationToken);
    }

    public async Task UpsertNodeAsync(Node node, CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var record = EntityMapping.ToRecord(node);
        var existing = await db.Nodes.FindAsync([record.Id], cancellationToken);
        if (existing is null)
            db.Nodes.Add(record);
        else
            db.Entry(existing).CurrentValues.SetValues(record);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<Node?> GetNodeAsync(NodeId nodeId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var record = await db.Nodes.FindAsync([nodeId.Value], cancellationToken);
        return record is null ? null : EntityMapping.ToDomain(record);
    }

    public async Task<IReadOnlyList<Node>> ListNodesAsync(SpaceId spaceId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var records = await db.Nodes.Where(n => n.SpaceId == spaceId.Value).ToListAsync(cancellationToken);
        return records.Select(EntityMapping.ToDomain).ToList();
    }

    public async Task DeleteNodeAsync(NodeId nodeId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var record = await db.Nodes.FindAsync([nodeId.Value], cancellationToken);
        if (record is null)
            return;
        db.Nodes.Remove(record);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task UpsertMcpServerAsync(McpServer server, CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var record = EntityMapping.ToRecord(server);
        var existing = await db.McpServers.FindAsync([record.Id], cancellationToken);
        if (existing is null)
        {
            db.McpServers.Add(record);
        }
        else
        {
            db.Entry(existing).CurrentValues.SetValues(record);
            // Increment 1: EncryptedSecret is NEVER in the domain entity, so SetValues would set
            // it to null on every unrelated update (RemoteUrl/status/etc.) — mirrors the identical
            // guard on InferencePointRecord.EncryptedSecret (UpsertInferencePointAsync).
            db.Entry(existing).Property(x => x.EncryptedSecret).IsModified = false;
            // Increment 2: EncryptedOAuthToken is NEVER in the domain entity either (same
            // reasoning as EncryptedSecret immediately above) — guard it the same way.
            db.Entry(existing).Property(x => x.EncryptedOAuthToken).IsModified = false;
        }
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<McpServer?> GetMcpServerAsync(McpServerId serverId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var record = await db.McpServers.FindAsync([serverId.Value], cancellationToken);
        return record is null ? null : EntityMapping.ToDomain(record);
    }

    public async Task<McpServer?> GetMcpServerByDisplayNameAsync(SpaceId spaceId, string displayName, CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var record = await db.McpServers
            .FirstOrDefaultAsync(s => s.SpaceId == spaceId.Value && s.DisplayName == displayName, cancellationToken);
        return record is null ? null : EntityMapping.ToDomain(record);
    }

    public async Task<IReadOnlyList<McpServer>> ListMcpServersAsync(SpaceId spaceId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var records = await db.McpServers.Where(s => s.SpaceId == spaceId.Value).ToListAsync(cancellationToken);
        return records.Select(EntityMapping.ToDomain).ToList();
    }

    public async Task<IReadOnlyList<PurgeableServer>> ListPurgeableMcpServersAsync(DateTimeOffset cutoff, CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        // Increment 1 fix: an http_cloud server's PublisherNodeId is ALWAYS "" by design (no
        // relay node — see McpServerTransports), which would otherwise always satisfy
        // `n == null` below regardless of cutoff, making every http_cloud server immediately
        // "purgeable". Exclude Transport == http_cloud explicitly — it has its own lifecycle
        // (owner-managed disable/delete), not the node-offline TTL this reaper targets.
        var rows = await (
            from s in db.McpServers
            where s.Status == McpServerStatus.Published
                  && s.Transport != Korat.Domain.McpServerTransports.HttpCloud
            join n in db.Nodes on s.PublisherNodeId equals n.Id into g
            from n in g.DefaultIfEmpty()
            where n == null || n.LastSeenAt == null || n.LastSeenAt < cutoff
            select new
            {
                s.Id,
                s.SpaceId,
                s.PublisherNodeId,
                OwnerLastSeenAt = n == null ? (DateTimeOffset?)null : n.LastSeenAt
            }).ToListAsync(cancellationToken);

        return rows.Select(r => new PurgeableServer(
            new McpServerId(r.Id),
            new SpaceId(r.SpaceId),
            new NodeId(r.PublisherNodeId),
            r.OwnerLastSeenAt)).ToList();
    }

    public async Task<IReadOnlyList<ReapableSession>> ListReapableSessionsAsync(
        DateTimeOffset cutoff, DateTimeOffset sentinelSessionAgeCutoff, CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        // Increment 1 fix (Finding 16, B1): an http_cloud session's PublisherNodeId is ALWAYS ""
        // by design (no relay node) — under the pre-fix query this made the publisher-side
        // OR-clause fire unconditionally, reaping every live http_cloud session within the hour
        // regardless of how healthy its client node was. The client-node OR-clause is fully
        // transport-agnostic and is UNCHANGED; only the publisher-node OR-clause is now gated on
        // "this session's server is not http_cloud" — mirrors the server-reaper fix's reasoning
        // exactly (ListPurgeableMcpServersAsync above). A session whose McpServerId doesn't
        // resolve to any row (srv == null; shouldn't normally happen) is conservatively treated
        // as NOT http_cloud, so it keeps today's reap-eligible-on-stale-publisher behavior rather
        // than silently becoming un-reapable.
        // MUST-FIX 2 (adversarial review, Space-MCP increment 1 Tasks 4-6): SessionAdmission
        // persists every Space-MCP aggregator-opened (ConsumerBindPolicy.ServerMinted) backend
        // session with ClientNodeId = the synthetic
        // Korat.Domain.WellKnownNodeIds.AggregatorSentinelNodeId ("cagg-sentinel"), which NEVER
        // has a Nodes row — there is no real bridge/publisher behind it, only the aggregator's
        // in-process delivery leg. Before this fix, the client-node OR-clause below fired
        // unconditionally for cn == null (a missing row looks identical to "abandoned client"),
        // so SessionReaperService's hourly sweep (SessionGrain.CloseAsync(Abandoned)) closed
        // EVERY live Space-MCP session within ~75 minutes regardless of health. Mirrors the
        // http_cloud publisher-gate fix immediately below it (Finding 16, B1), just on the
        // CLIENT side instead: a synthetic aggregator-sentinel client deliberately has no Nodes
        // row, so its session's liveness is governed by its backend PUBLISHER instead (the
        // untouched publisher-side OR-clause already does this — the backend publisher IS a real
        // node with a row). Lifecycle teardown (MUST-FIX 1: SpaceMcpAggregatorGrain now calls
        // SessionTerminator on every DELETE/deactivate/handshake-timeout path) closes these
        // sessions promptly on its own — so it must not also be reap-eligible purely because the
        // sentinel has no client row.
        //
        // MUST-FIX F2 (adversarial review, second pass, should-fix): the two clauses above leave a
        // zero-reap-clause HOLE for a sentinel session with a HEALTHY relay publisher, and for ANY
        // sentinel x http_cloud session — discovery does not filter by transport, and AdmitAsync
        // supports http_cloud under ConsumerBindPolicy.ServerMinted with PublisherNodeId="" — so
        // BOTH the client clause (gated OUT for the sentinel id) and the publisher clause (gated
        // OUT for http_cloud) are inapplicable to that combination at once. MUST-FIX 1's own
        // failure modes (a silo crash mid-teardown, a best-effort TerminateSessionAsync that
        // itself failed and was only logged, or a shutdown-deadline-canceled terminate) then leak
        // that session as Active forever with NO backstop. This third clause is a coarse,
        // crude-age BACKSTOP scoped ONLY to sentinel-client sessions: because a sentinel client has
        // no per-session node-liveness signal at all (by design — there is no real bridge/publisher
        // behind it), a generous absolute age past sentinelSessionAgeCutoff is the only failure-mode
        // net available, and it covers the sentinel x http_cloud combination the other two clauses
        // both miss. This is a coarse backstop, NOT the primary lifecycle close — that remains
        // SpaceMcpAggregatorGrain's own teardown (MUST-FIX 1). Known dev-limitation: a
        // legitimately-active Space-MCP session older than sentinelSessionAgeCutoff is reaped and
        // the client must reconnect; an activity-based refinement (e.g. last-frame timestamp) is a
        // follow-up, not this increment's scope.
        var rows = await (
            from s in db.Sessions
            where s.Status == SessionStatus.Active || s.Status == SessionStatus.Opening
            join srv in db.McpServers on s.McpServerId equals srv.Id into svg
            from srv in svg.DefaultIfEmpty()
            join cn in db.Nodes on s.ClientNodeId equals cn.Id into cg
            from cn in cg.DefaultIfEmpty()
            join pn in db.Nodes on s.PublisherNodeId equals pn.Id into pg
            from pn in pg.DefaultIfEmpty()
            where ((s.ClientNodeId != Korat.Domain.WellKnownNodeIds.AggregatorSentinelNodeId)
                   && (cn == null || cn.LastSeenAt == null || cn.LastSeenAt < cutoff))
               || ((srv == null || srv.Transport != Korat.Domain.McpServerTransports.HttpCloud)
                   && (pn == null || pn.LastSeenAt == null || pn.LastSeenAt < cutoff))
               || (s.ClientNodeId == Korat.Domain.WellKnownNodeIds.AggregatorSentinelNodeId
                   && s.StartedAt < sentinelSessionAgeCutoff)
            select new { s.Id, s.SpaceId, s.ClientNodeId, s.PublisherNodeId }
        ).ToListAsync(cancellationToken);

        return rows.Select(r => new ReapableSession(
            new SessionId(r.Id),
            new SpaceId(r.SpaceId),
            new NodeId(r.ClientNodeId),
            new NodeId(r.PublisherNodeId))).ToList();
    }

    public async Task DeleteMcpServerAsync(McpServerId serverId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var record = await db.McpServers.FindAsync([serverId.Value], cancellationToken);
        if (record is null)
            return;
        db.McpServers.Remove(record);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task SetMcpServerSecretAsync(McpServerId id, string ciphertext, string secretHint, CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var record = await db.McpServers.FindAsync([id.Value], cancellationToken);
        if (record is null)
            return;
        record.EncryptedSecret = ciphertext;
        record.SecretHint = secretHint;
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<string?> GetMcpServerSecretCiphertextAsync(McpServerId id, CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var record = await db.McpServers.FindAsync([id.Value], cancellationToken);
        return record?.EncryptedSecret;
    }

    public async Task ClearMcpServerSecretAsync(McpServerId id, CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var record = await db.McpServers.FindAsync([id.Value], cancellationToken);
        if (record is null)
            return;
        record.EncryptedSecret = null;
        record.SecretHint = null;
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task SetMcpServerOAuthTokenAsync(McpServerId id, string ciphertext, CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var record = await db.McpServers.FindAsync([id.Value], cancellationToken);
        if (record is null)
            return;
        record.EncryptedOAuthToken = ciphertext;
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<string?> GetMcpServerOAuthTokenCiphertextAsync(McpServerId id, CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var record = await db.McpServers.FindAsync([id.Value], cancellationToken);
        return record?.EncryptedOAuthToken;
    }

    public async Task ClearMcpServerOAuthTokenAsync(McpServerId id, CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var record = await db.McpServers.FindAsync([id.Value], cancellationToken);
        if (record is null)
            return;
        record.EncryptedOAuthToken = null;
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task AddTombstoneAsync(SpaceId spaceId, NodeId publisherNodeId, string displayName, Korat.Domain.Auth.UserId userId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var existing = await db.McpServerTombstones
            .FindAsync([spaceId.Value, publisherNodeId.Value, displayName], cancellationToken);
        var record = EntityMapping.ToRecord(new McpServerTombstone
        {
            SpaceId = spaceId,
            PublisherNodeId = publisherNodeId,
            DisplayName = displayName,
            CreatedAt = DateTimeOffset.UtcNow,
            CreatedByUserId = userId
        });
        if (existing is null)
            db.McpServerTombstones.Add(record);
        else
            db.Entry(existing).CurrentValues.SetValues(record);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<bool> TombstoneExistsAsync(SpaceId spaceId, NodeId publisherNodeId, string displayName, CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        return await db.McpServerTombstones.AnyAsync(
            t => t.SpaceId == spaceId.Value
                && t.PublisherNodeId == publisherNodeId.Value
                && t.DisplayName == displayName,
            cancellationToken);
    }

    public async Task RemoveTombstoneAsync(SpaceId spaceId, NodeId publisherNodeId, string displayName, CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var record = await db.McpServerTombstones
            .FindAsync([spaceId.Value, publisherNodeId.Value, displayName], cancellationToken);
        if (record is null)
            return;
        db.McpServerTombstones.Remove(record);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<McpServerTombstone>> ListTombstonesForNodeAsync(SpaceId spaceId, NodeId publisherNodeId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var records = await db.McpServerTombstones
            .Where(t => t.SpaceId == spaceId.Value && t.PublisherNodeId == publisherNodeId.Value)
            .ToListAsync(cancellationToken);
        return records.Select(EntityMapping.ToDomain).ToList();
    }

    public async Task UpsertAgentClientAsync(Consumer agentClient, CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var record = EntityMapping.ToRecord(agentClient);
        var existing = await db.AgentClients.FindAsync([record.Id], cancellationToken);
        if (existing is null)
            db.AgentClients.Add(record);
        else
            db.Entry(existing).CurrentValues.SetValues(record);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<Consumer?> GetAgentClientAsync(ConsumerId id, CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var record = await db.AgentClients.FindAsync([id.Value], cancellationToken);
        return record is null ? null : EntityMapping.ToDomain(record);
    }

    public async Task UpsertAccessRequestAsync(AccessRequest request, CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var record = EntityMapping.ToRecord(request);
        var existing = await db.AccessRequests.FindAsync([record.Id], cancellationToken);
        if (existing is null)
            db.AccessRequests.Add(record);
        else
            db.Entry(existing).CurrentValues.SetValues(record);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<AccessRequest?> GetAccessRequestAsync(AccessRequestId id, CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var record = await db.AccessRequests.FindAsync([id.Value], cancellationToken);
        return record is null ? null : EntityMapping.ToDomain(record);
    }

    public async Task<AccessRequest?> GetPendingAccessRequestAsync(
        SpaceId spaceId,
        ConsumerId agentClientId,
        McpServerId mcpServerId,
        CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var record = await db.AccessRequests.FirstOrDefaultAsync(r =>
            r.SpaceId == spaceId.Value &&
            r.ConsumerId == agentClientId.Value &&
            r.McpServerId == mcpServerId.Value &&
            r.Status == AccessRequestStatus.Pending, cancellationToken);
        return record is null ? null : EntityMapping.ToDomain(record);
    }

    public async Task<IReadOnlyList<AccessRequest>> ListAccessRequestsAsync(
        SpaceId spaceId,
        AccessRequestStatus? status = null,
        CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var query = db.AccessRequests.Where(r => r.SpaceId == spaceId.Value);
        if (status is not null)
            query = query.Where(r => r.Status == status);
        var records = await query.ToListAsync(cancellationToken);
        return records.Select(EntityMapping.ToDomain).ToList();
    }

    public async Task UpsertGrantAsync(Grant grant, CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var record = EntityMapping.ToRecord(grant);
        var existing = await db.Grants.FindAsync([record.Id], cancellationToken);
        if (existing is null)
            db.Grants.Add(record);
        else
            db.Entry(existing).CurrentValues.SetValues(record);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<Grant?> GetGrantAsync(GrantId id, CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var record = await db.Grants.FindAsync([id.Value], cancellationToken);
        return record is null ? null : EntityMapping.ToDomain(record);
    }

    public async Task<Grant?> GetActiveGrantAsync(
        SpaceId spaceId,
        ConsumerId agentClientId,
        McpServerId mcpServerId,
        CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var record = await db.Grants.FirstOrDefaultAsync(g =>
            g.SpaceId == spaceId.Value &&
            g.ConsumerId == agentClientId.Value &&
            g.McpServerId == mcpServerId.Value &&
            g.Status == GrantStatus.Active, cancellationToken);
        return record is null ? null : EntityMapping.ToDomain(record);
    }

    public async Task<IReadOnlyList<Grant>> ListGrantsAsync(SpaceId spaceId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var records = await db.Grants.Where(g => g.SpaceId == spaceId.Value).ToListAsync(cancellationToken);
        return records.Select(EntityMapping.ToDomain).ToList();
    }

    public async Task UpsertSessionAsync(RelaySession session, CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var record = EntityMapping.ToRecord(session);
        var existing = await db.Sessions.FindAsync([record.Id], cancellationToken);
        if (existing is null)
            db.Sessions.Add(record);
        else
            db.Entry(existing).CurrentValues.SetValues(record);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<RelaySession?> GetSessionAsync(SessionId id, CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var record = await db.Sessions.FindAsync([id.Value], cancellationToken);
        return record is null ? null : EntityMapping.ToDomain(record);
    }

    public async Task<IReadOnlyList<RelaySession>> ListSessionsAsync(
        SpaceId spaceId,
        bool includeClosed = true,
        CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var query = db.Sessions.Where(s => s.SpaceId == spaceId.Value);
        if (!includeClosed)
            query = query.Where(s => s.Status != SessionStatus.Closed && s.Status != SessionStatus.Failed);
        var records = await query.ToListAsync(cancellationToken);
        return records.Select(EntityMapping.ToDomain).ToList();
    }

    public async Task<IReadOnlyList<Korat.Domain.Auth.UserId>> ListUserIdsWithOnlineServerAsync(
        DateTimeOffset staleCutoff, CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var owners = await (
            from s in db.McpServers
            where s.IsAsserted && s.Status == McpServerStatus.Published
            join n in db.Nodes on s.PublisherNodeId equals n.Id
            where n.Status == NodeStatus.Online && n.LastSeenAt != null && n.LastSeenAt > staleCutoff
            join sp in db.Spaces on s.SpaceId equals sp.Id
            select sp.OwnerUserId            // string ("N"-format guid)
        ).Distinct().ToListAsync(cancellationToken);
        return owners.Select(g => new Korat.Domain.Auth.UserId(Guid.ParseExact(g, "N"))).ToList();
    }

    public async Task<bool> HasOnlineServerAsync(
        Korat.Domain.Auth.UserId userId, DateTimeOffset staleCutoff, CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var owner = userId.Value.ToString("N");
        return await (
            from s in db.McpServers
            where s.IsAsserted && s.Status == McpServerStatus.Published
            join n in db.Nodes on s.PublisherNodeId equals n.Id
            where n.Status == NodeStatus.Online && n.LastSeenAt != null && n.LastSeenAt > staleCutoff
            join sp in db.Spaces on s.SpaceId equals sp.Id
            where sp.OwnerUserId == owner
            select s.Id
        ).AnyAsync(cancellationToken);
    }

    public async Task<(AccessRequest Request, Grant Grant)> ApproveAccessRequestAsync(
        AccessRequest request,
        Grant grant,
        CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);

        // FR-007: не больше одного active grant на (space, agent, server).
        var duplicateActive = await db.Grants.AnyAsync(g =>
            g.SpaceId == grant.SpaceId.Value &&
            g.ConsumerId == grant.ConsumerId.Value &&
            g.McpServerId == grant.McpServerId.Value &&
            g.Status == GrantStatus.Active, cancellationToken);
        if (duplicateActive)
            throw new KoratDomainException(KoratErrorCode.AccessDenied);

        var requestRecord = EntityMapping.ToRecord(request);
        var existingRequest = await db.AccessRequests.FindAsync([requestRecord.Id], cancellationToken);
        if (existingRequest is null)
            db.AccessRequests.Add(requestRecord);
        else
            db.Entry(existingRequest).CurrentValues.SetValues(requestRecord);

        db.Grants.Add(EntityMapping.ToRecord(grant));
        // Один SaveChanges: статус заявки + insert grant атомарно.
        await db.SaveChangesAsync(cancellationToken);
        return (request, grant);
    }

    // ── Inference Points (029) ─────────────────────────────────────────────────

    // ── Space slug (029) ──────────────────────────────────────────────────────

    public async Task<Space?> GetSpaceAsync(SpaceId spaceId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var record = await db.Spaces.FindAsync([spaceId.Value], cancellationToken);
        if (record is null)
            return null;
        return new Space
        {
            Id = new SpaceId(record.Id),
            // SpaceRecord.OwnerUserId is stored Guid.ToString("N") (UserProvisioningService.cs:49,
            // SpaceResolver.cs:40) — parse with the explicit "N" format, mirroring
            // ListUserIdsWithOnlineServerAsync above, so a caller comparing UserId values never
            // falls into the "D" vs "N" string-format landmine (S1).
            OwnerUserId = new Korat.Domain.Auth.UserId(Guid.ParseExact(record.OwnerUserId, "N")),
            DisplayName = record.DisplayName,
            CreatedAt = record.CreatedAt,
            UpdatedAt = record.UpdatedAt,
        };
    }

    public async Task<SpaceId?> GetSpaceIdBySlugAsync(string slug, CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var record = await db.Spaces.FirstOrDefaultAsync(s => s.Slug == slug, cancellationToken);
        return record is null ? null : new SpaceId(record.Id);
    }

    public async Task<string?> GetSpaceSlugAsync(SpaceId spaceId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var record = await db.Spaces.FindAsync([spaceId.Value], cancellationToken);
        return record?.Slug;
    }

    public async Task<bool> TrySetSpaceSlugAsync(SpaceId spaceId, string slug, CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var record = await db.Spaces.FindAsync([spaceId.Value], cancellationToken);
        if (record is null)
            return false;
        // No-op if already the same slug.
        if (record.Slug == slug)
            return true;
        record.Slug = slug;
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (Microsoft.EntityFrameworkCore.DbUpdateException)
        {
            // Unique constraint violation — slug taken by another space.
            return false;
        }
    }

    // ── User profile (F6) ─────────────────────────────────────────────────────

    public async Task<Korat.Domain.Auth.User?> GetUserAsync(Korat.Domain.Auth.UserId userId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        return await db.Users.SingleOrDefaultAsync(u => u.Id == userId, cancellationToken);
    }

    public async Task<Korat.Domain.Auth.User> UpdateUserDisplayNameAsync(Korat.Domain.Auth.UserId userId, string displayName, CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);

        // EF Core InMemory does not support ExecuteUpdateAsync. Fall back to the
        // change-tracking path when running under the test InMemory provider.
        // Production always uses the Postgres provider and takes the atomic branch.
        var providerName = db.Database.ProviderName;
        if (providerName is not null && providerName.Contains("InMemory", StringComparison.OrdinalIgnoreCase))
        {
            var existing = await db.Users.SingleOrDefaultAsync(u => u.Id == userId, cancellationToken);
            if (existing is null)
                throw new InvalidOperationException($"User {userId.Value} not found.");

            var updated = existing with { DisplayName = displayName };
            db.Entry(existing).CurrentValues.SetValues(updated);
            await db.SaveChangesAsync(cancellationToken);
            return updated;
        }

        // Production path: single parameterised UPDATE, no lost-update window.
        var affected = await db.Users
            .Where(u => u.Id == userId)
            .ExecuteUpdateAsync(s => s.SetProperty(u => u.DisplayName, displayName), cancellationToken);

        if (affected == 0)
            throw new InvalidOperationException($"User {userId.Value} not found.");

        // Read back the authoritative row so the caller's in-memory state is fresh.
        return await db.Users.SingleAsync(u => u.Id == userId, cancellationToken);
    }

    public async Task<Korat.Domain.Auth.User> ReloadUserAsync(Korat.Domain.Auth.UserId userId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var user = await db.Users.SingleOrDefaultAsync(u => u.Id == userId, cancellationToken);
        if (user is null)
            throw new InvalidOperationException($"User {userId.Value} not found.");
        return user;
    }

    // ── Threads & Channels (PR-2 Task 3) ─────────────────────────────────────
    // Thread/Message are direct-mapping-tier entities (see EntityMapping.cs header) — no
    // Record/ToDomain pair; reads/writes go straight through the Threads/Messages DbSets,
    // mirroring the Agent methods above.

    // ── Channels (PR-2 Task 5) ────────────────────────────────────────────────
    // ChannelBinding is a direct-mapping-tier entity (see EntityMapping.cs header) — no
    // Record/ToDomain pair; reads/writes go straight through the ChannelBindings DbSet,
    // mirroring the Agent/Thread methods above.

    // ── Agent Coordination Rooms (Plan A, Task 2) ────────────────────────────
    // Room/RoomParticipant/RoomMessage are direct-mapping-tier entities (see EntityMapping.cs
    // header) — no Record/ToDomain pair; reads/writes go straight through the Rooms/
    // RoomParticipants/RoomMessages DbSets, mirroring the Thread/Message methods above.

}
