using Korat.Domain.Entities;
// PR-2: Thread collides with System.Threading.Thread (Orleans.Sdk brings a global using for
// System.Threading) — alias to the domain entity, mirroring KoratDbContext.cs.

namespace Korat.Domain.Persistence;

/// <summary>
/// Доступ к метаданным в Postgres (EF). Grain'ы пишут сюда и читают при Hydrate.
/// </summary>
public interface IMetadataRepository
{
    Task EnsureCreatedAsync(CancellationToken cancellationToken = default);
    Task UpsertNodeAsync(Node node, CancellationToken cancellationToken = default);
    Task<Node?> GetNodeAsync(NodeId nodeId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<Node>> ListNodesAsync(SpaceId spaceId, CancellationToken cancellationToken = default);

    /// <summary>
    /// #165 (`korat nodes prune`): hard-delete a node row. No-op if absent. Mirrors
    /// <see cref="DeleteMcpServerAsync"/> — used by <c>NodeGrain.RemoveAsync</c> (agent-kind
    /// nodes only; publishers are never pruned by the caller).
    /// </summary>
    Task DeleteNodeAsync(NodeId nodeId, CancellationToken cancellationToken = default);
    Task UpsertMcpServerAsync(McpServer server, CancellationToken cancellationToken = default);
    Task<McpServer?> GetMcpServerAsync(McpServerId serverId, CancellationToken cancellationToken = default);
    Task<McpServer?> GetMcpServerByDisplayNameAsync(SpaceId spaceId, string displayName, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<McpServer>> ListMcpServersAsync(SpaceId spaceId, CancellationToken cancellationToken = default);

    /// <summary>
    /// 024 reaper: cross-space query of <c>Published</c> servers whose owner node is missing or
    /// hasn't been seen since <paramref name="cutoff"/>. Returns enough to delete + audit-log.
    /// </summary>
    Task<IReadOnlyList<PurgeableServer>> ListPurgeableMcpServersAsync(DateTimeOffset cutoff, CancellationToken cancellationToken = default);

    /// <summary>
    /// Hard-delete an MCP server row (used by `korat mcp remove` / UnpublishMcpServerAsync).
    /// No-op if the row does not exist. Distinct from disabling, which keeps the row in place.
    /// </summary>
    Task DeleteMcpServerAsync(McpServerId serverId, CancellationToken cancellationToken = default);

    /// <summary>Step-B: idempotent upsert of a delete-tombstone for (spaceId, publisherNodeId,
    /// displayName). Re-adding an existing tombstone is a no-op (refreshes audit fields).</summary>
    Task AddTombstoneAsync(SpaceId spaceId, NodeId publisherNodeId, string displayName, Korat.Domain.Auth.UserId userId, CancellationToken cancellationToken = default);

    /// <summary>Step-B: true if a delete-tombstone exists for (spaceId, publisherNodeId, displayName).</summary>
    Task<bool> TombstoneExistsAsync(SpaceId spaceId, NodeId publisherNodeId, string displayName, CancellationToken cancellationToken = default);

    /// <summary>Step-B: remove a tombstone for (spaceId, publisherNodeId, displayName). No-op if absent.</summary>
    Task RemoveTombstoneAsync(SpaceId spaceId, NodeId publisherNodeId, string displayName, CancellationToken cancellationToken = default);

    /// <summary>Step-B: all tombstones for a publisher node in a space — used by the SyncMcpServers
    /// CLEAR pass to drop tombstones whose name the node no longer declares.</summary>
    Task<IReadOnlyList<McpServerTombstone>> ListTombstonesForNodeAsync(SpaceId spaceId, NodeId publisherNodeId, CancellationToken cancellationToken = default);

    // ── McpServer secret (Increment 1, http_cloud) — EF-only, never in domain ──────────────
    // Mirrors the InferencePoint SetInferenceSecretAsync/GetInferenceSecretCiphertextAsync/
    // ClearInferenceSecretAsync trio exactly (same reasoning: the ciphertext bypasses the
    // domain entity and grain entirely so a partial PATCH update can never null it out).

    /// <summary>Writes ciphertext and secretHint for an http_cloud McpServer. Does not touch any
    /// other column. Must be called ONLY from the owner endpoint handler (Task 3) directly —
    /// there is no McpServerSecretService wrapper, mirroring the Channels/Threads pattern
    /// (IEnvelopeCrypto consumed directly, not through a per-entity service).</summary>
    Task SetMcpServerSecretAsync(McpServerId id, string ciphertext, string secretHint, CancellationToken cancellationToken = default);

    Task<string?> GetMcpServerSecretCiphertextAsync(McpServerId id, CancellationToken cancellationToken = default);

    Task ClearMcpServerSecretAsync(McpServerId id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Increment 2 (HTTP MCP OAuth): stores the single envelope ciphertext for an oauth server's
    /// token document (access/refresh/expiry/endpoints/client credentials — see
    /// Korat.Cloud.Mcp.Oauth.McpOAuthTokenDocument). One ciphertext, atomic rotation — mirrors
    /// SetMcpServerSecretAsync's shape exactly but with NO hint parameter: no SecretHint-style
    /// masked display is ever derived from token material (spec: "No SecretHint/…last4 hint is
    /// ever derived from token material").
    /// </summary>
    Task SetMcpServerOAuthTokenAsync(McpServerId id, string ciphertext, CancellationToken cancellationToken = default);

    Task<string?> GetMcpServerOAuthTokenCiphertextAsync(McpServerId id, CancellationToken cancellationToken = default);

    Task ClearMcpServerOAuthTokenAsync(McpServerId id, CancellationToken cancellationToken = default);

    Task UpsertAccessRequestAsync(AccessRequest request, CancellationToken cancellationToken = default);
    Task<AccessRequest?> GetAccessRequestAsync(AccessRequestId id, CancellationToken cancellationToken = default);
    Task<AccessRequest?> GetPendingAccessRequestAsync(SpaceId spaceId, ConsumerId agentClientId, McpServerId mcpServerId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<AccessRequest>> ListAccessRequestsAsync(SpaceId spaceId, AccessRequestStatus? status = null, CancellationToken cancellationToken = default);

    Task UpsertGrantAsync(Grant grant, CancellationToken cancellationToken = default);
    Task<Grant?> GetGrantAsync(GrantId id, CancellationToken cancellationToken = default);
    Task<Grant?> GetActiveGrantAsync(SpaceId spaceId, ConsumerId agentClientId, McpServerId mcpServerId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<Grant>> ListGrantsAsync(SpaceId spaceId, CancellationToken cancellationToken = default);

    Task UpsertAgentClientAsync(Consumer agentClient, CancellationToken cancellationToken = default);
    Task<Consumer?> GetAgentClientAsync(ConsumerId id, CancellationToken cancellationToken = default);

    Task UpsertSessionAsync(RelaySession session, CancellationToken cancellationToken = default);
    Task<RelaySession?> GetSessionAsync(SessionId id, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<RelaySession>> ListSessionsAsync(SpaceId spaceId, bool includeClosed = true, CancellationToken cancellationToken = default);

    /// <summary>
    /// Step-C session reaper: cross-space query of Active/Opening sessions whose client OR publisher
    /// node is missing, never-seen, or hasn't been seen since <paramref name="cutoff"/>. The reaper
    /// persists Closed for these. Node LastSeenAt is DB-persisted (NodeGrain heartbeats upsert it),
    /// so this is a pure DB query — no grain fan-out. Mirrors ListPurgeableMcpServersAsync.
    ///
    /// MUST-FIX F2 (adversarial review, second pass): <paramref name="sentinelSessionAgeCutoff"/>
    /// is a THIRD, independent backstop clause for Space-MCP aggregator-opened (sentinel-client)
    /// sessions — see <c>EfMetadataRepository</c>'s implementation doc comment for why a sentinel
    /// session (and especially a sentinel×http_cloud session) is otherwise gated out of BOTH of
    /// the other two clauses and would never become reap-eligible on its own.
    /// </summary>
    Task<IReadOnlyList<ReapableSession>> ListReapableSessionsAsync(
        DateTimeOffset cutoff, DateTimeOffset sentinelSessionAgeCutoff, CancellationToken cancellationToken = default);

    Task<(AccessRequest Request, Grant Grant)> ApproveAccessRequestAsync(AccessRequest request, Grant grant, CancellationToken cancellationToken = default);




    /// <summary>Distinct owner UserIds of every asserted MCP server whose publisher node is
    /// Online with LastSeenAt &gt; staleCutoff. Generic presence read.</summary>
    Task<IReadOnlyList<Korat.Domain.Auth.UserId>> ListUserIdsWithOnlineServerAsync(
        DateTimeOffset staleCutoff, CancellationToken cancellationToken = default);

    /// <summary>True if the given user owns at least one such online server.</summary>
    Task<bool> HasOnlineServerAsync(
        Korat.Domain.Auth.UserId userId, DateTimeOffset staleCutoff, CancellationToken cancellationToken = default);

    // ── Inference Points (029) ─────────────────────────────────────────────────

    // ── Inference secret (T3/T4) — EF-only, never in domain ─────────────────

    // ── Space slug (029) ──────────────────────────────────────────────────────

    /// <summary>
    /// Returns the Space entity for the given id, or null if not found. Used by
    /// SpaceMcpAuth (Space-MCP increment 1, Task 1, correction S1) for the owner-owns-Space
    /// check on <c>/mcp/{spaceSeg}</c> — compare the returned <c>Space.OwnerUserId</c>
    /// (Korat.Domain.Auth.UserId) against the validated token's UserId.
    /// </summary>
    Task<Space?> GetSpaceAsync(SpaceId spaceId, CancellationToken cancellationToken = default);

    /// <summary>Returns the SpaceId for the given slug, or null if not assigned.</summary>
    Task<SpaceId?> GetSpaceIdBySlugAsync(string slug, CancellationToken cancellationToken = default);

    /// <summary>Returns the slug for a space, or null if not yet assigned.</summary>
    Task<string?> GetSpaceSlugAsync(SpaceId spaceId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Attempts to set the slug for a space. Returns false on unique-constraint violation
    /// (slug taken) so the caller can retry with a suffixed variant. No-op if already the same.
    /// </summary>
    Task<bool> TrySetSpaceSlugAsync(SpaceId spaceId, string slug, CancellationToken cancellationToken = default);

    // ── User profile (F6) ─────────────────────────────────────────────────────

    /// <summary>Returns the User row for the given id, or null if not found.</summary>
    Task<Korat.Domain.Auth.User?> GetUserAsync(Korat.Domain.Auth.UserId userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates the DisplayName for the given user. Uses <c>ExecuteUpdateAsync</c> on relational
    /// providers (Postgres) for an atomic single-statement UPDATE. Falls back to the
    /// change-tracking path on InMemory (tests). Returns the refreshed row.
    /// Throws <see cref="InvalidOperationException"/> when no row exists for <paramref name="userId"/>.
    /// </summary>
    Task<Korat.Domain.Auth.User> UpdateUserDisplayNameAsync(Korat.Domain.Auth.UserId userId, string displayName, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reloads and returns the authoritative User row from the database without writing any
    /// columns. Used by UserGrain after an out-of-band email promotion (committed by
    /// EmailChangeService.ConfirmAsync) to refresh the grain's in-memory cache.
    /// Throws <see cref="InvalidOperationException"/> when no row exists.
    /// </summary>
    Task<Korat.Domain.Auth.User> ReloadUserAsync(Korat.Domain.Auth.UserId userId, CancellationToken cancellationToken = default);

    // ── Hosted Agents (PR-1) ─────────────────────────────────────────────────

    // ── Threads & Channels (PR-2 Task 3) ────────────────────────────────────

    // ── Channels (PR-2 Task 5) ───────────────────────────────────────────────

    // ── Agent Coordination Rooms (Plan A, Task 2) ────────────────────────────

}

/// <summary>024 reaper projection: a Published server eligible for purge + its owner's last-seen for audit.</summary>
public readonly record struct PurgeableServer(
    McpServerId Id,
    SpaceId SpaceId,
    NodeId PublisherNodeId,
    DateTimeOffset? OwnerLastSeenAt);

/// <summary>Step-C reaper projection: an Active/Opening session eligible for ghost-reconciliation.</summary>
public readonly record struct ReapableSession(
    SessionId Id,
    SpaceId SpaceId,
    NodeId ClientNodeId,
    NodeId PublisherNodeId);
