using Korat.Domain;
using Korat.Domain.Entities;
using UserId = Korat.Domain.Auth.UserId;

namespace Korat.GrainInterfaces;

// Orleans-grain'ы: один grain ≈ один агрегат с in-memory состоянием + вызовы в Postgres.
// Ключ grain (string): Id сущности — например SpaceId (UUID), NodeId, McpServerId и т.д.

/// <summary>
/// 021 (Layer 1): server descriptor from a declarative SyncMcpServers message.
/// Contains only the fields needed to upsert or soft-retire a server; the stable McpServerId
/// is assigned by SpaceGrain (idempotent: same node+displayName → same id every time).
/// </summary>
/// <param name="DisplayName">Unique name within the Space (same as PublishMcpServer).</param>
/// <param name="Command">Launch command (e.g. npx, uvx).</param>
/// <param name="Args">Launch arguments as a single space-joined string (mirrors HandlePublishAsync).</param>
public record McpServerSpec(string DisplayName, string Command, string Args);

/// <summary>Space — корень trust-метаданных: запросы доступа, grants, публикация MCP.</summary>
public interface ISpaceGrain : IGrainWithStringKey
{
    /// <summary>Сохраняет или обновляет метаданные узла в Postgres.</summary>
    /// <param name="node">Узел с заполненным Id и SpaceId.</param>
    Task RegisterNodeAsync(Node node);

    /// <summary>
    /// Публикует новый MCP-сервер от имени узла: создаёт <see cref="IMcpServerGrain"/> и пишет в БД.
    /// </summary>
    /// <param name="publisherNodeId">Узел, на котором будет запускаться MCP-процесс.</param>
    /// <param name="displayName">Имя в UI (уникально в Space, пока сервер не Disabled).</param>
    /// <param name="command">Исполняемая команда (например <c>npx</c>, <c>uvx</c>).</param>
    /// <param name="args">Аргументы команды (например <c>-y @modelcontextprotocol/server-filesystem /tmp</c>).</param>
    /// <returns>Созданный/обновлённый <see cref="McpServer"/>; <c>null</c> если (узел, имя)
    /// заблокирован delete-tombstone (Step B) — пассивная пере-декларация удалённого сервера
    /// не должна его воссоздавать.</returns>
    /// <exception cref="KoratDomainException">DuplicateServerName — имя уже занято активным сервером.</exception>
    Task<McpServer?> PublishMcpServerAsync(NodeId publisherNodeId, string displayName, string command, string args);

    /// <summary>
    /// Р26/Р27: publish, and report whether this was a REDEFINITION of an already-approved server.
    ///
    /// Same idempotency as <see cref="PublishMcpServerAsync"/>; the difference is what comes back.
    /// When the launch definition changed under an existing (node, name), permissions for that
    /// server are suspended and the outcome carries the suspended grant ids, the live sessions the
    /// CALLER must terminate (grains cannot reach SessionTerminator), and the before/after command
    /// pair the owner-facing notification has to show.
    ///
    /// Callers that ignore the redefinition leave sessions running against a definition nobody
    /// approved — use this overload anywhere that reacts to publishes from the wire.
    /// </summary>
    Task<McpServerPublishOutcome> PublishMcpServerWithOutcomeAsync(
        NodeId publisherNodeId, string displayName, string command, string args);

    /// <summary>
    /// All nodes for this Space, served from the grain's in-memory hydrate cache (SC-6).
    /// No DB round-trip after the first hydration — the grain key IS the isolation boundary.
    /// </summary>
    Task<IReadOnlyList<Node>> ListNodesAsync();

    /// <summary>
    /// Node-visibility-doctor design (2026-07-02): sets/clears the owner-editable Note on a node
    /// that belongs to this Space, then forwards to <see cref="INodeGrain.SetNoteAsync"/> (the
    /// canonical writer — grains-are-the-cache rule). Mirrors <see cref="GetMcpServerAsync"/>'s
    /// BOLA pattern: a node that is not a member of this Space (foreign or unknown) returns null,
    /// which the endpoint maps to 404 — same response for both cases, no existence oracle.
    /// </summary>
    /// <param name="nodeId">Node to update.</param>
    /// <param name="note">New note text, or null/empty/whitespace to clear.</param>
    /// <returns>The updated Node, or null if the node is not a member of this Space.</returns>
    Task<Node?> SetNodeNoteAsync(NodeId nodeId, string? note);

    /// <summary>
    /// All published MCP servers for this Space. The grain holds only the membership
    /// registry (server IDs); each server's status is fetched from the canonical
    /// <see cref="IMcpServerGrain"/> so <c>DisableAsync</c> side-effects are always
    /// reflected. This is an O(servers) fan-out, not a Postgres query.
    /// </summary>
    Task<IReadOnlyList<McpServer>> ListMcpServersAsync();

    /// <summary>
    /// Returns the MCP server with the given id if it belongs to this Space; otherwise null.
    /// Content reads go through the grain (design §3.4) — callers do NOT inject IMetadataRepository.
    /// </summary>
    Task<McpServer?> GetMcpServerAsync(McpServerId serverId);

    /// <summary>
    /// Lists sessions (MCP relay sessions) for this Space.
    /// Wraps IMetadataRepository.ListSessionsAsync scoped to this Space's id.
    /// All session reads funnel through the grain so the grain key IS the isolation boundary.
    /// </summary>
    Task<IReadOnlyList<RelaySession>> ListSessionsAsync(bool includeClosed = true);

    /// <summary>
    /// Снимает MCP-сервер с публикации: удаляет его из каталога Space.
    /// Безопасно игнорирует вызов если сервер не является членом этого Space или
    /// был опубликован другим узлом — один узел не может снять чужой сервер.
    /// </summary>
    /// <param name="publisherNodeId">Узел, инициировавший снятие с публикации.</param>
    /// <param name="serverId">Id сервера, который нужно снять.</param>
    Task UnpublishMcpServerAsync(NodeId publisherNodeId, McpServerId serverId);

    /// <summary>
    /// TEST-ONLY: forces the grain to drop its in-memory cache and re-hydrate from the
    /// repository on the next read. Used by integration tests that seed Postgres directly and
    /// need the grain to observe the new rows without waiting for Orleans deactivation
    /// (SpaceGrainInconsistentStateTests, McpServerTombstoneTests, NodeKindTests).
    ///
    /// Must NOT be called from production code paths (gateway, node, auth endpoints).
    /// </summary>
    Task InvalidateCacheAsync();

    // ── Inference Points (029) — additive methods ─────────────────────────────

    /// <summary>
    /// Increment 1 (HTTP MCP direct-to-Space): owner-initiated create of an http_cloud McpServer.
    /// Mirrors CreateOutboundInferencePointAsync exactly: NOT idempotent — throws
    /// KoratDomainException(DuplicateServerName) whenever displayName is already taken in this
    /// Space by ANY existing server, regardless of transport (same duplicate-submit-must-not-
    /// silently-overwrite-the-secret reasoning as PR #145 review B1). The caller (the owner
    /// endpoint, Task 3) is responsible for calling IMetadataRepository.SetMcpServerSecretAsync
    /// with the returned Id after this call — the secret plaintext is NOT passed to this grain.
    /// </summary>
    Task<McpServer> CreateHttpMcpServerAsync(
        string displayName, string remoteUrl, string authMode, string? authHeaderName, string? secretHint);

    /// <summary>
    /// Запрос доступа: агент просит разрешение вызывать MCP на другом узле.
    /// Идемпотентен — повтор для той же пары (agent, server) вернёт существующий Pending.
    /// </summary>
    /// <param name="agentClientId">Кто запрашивает (Cursor / адаптер на client-узле).</param>
    /// <param name="mcpServerId">К какому MCP-серверу нужен доступ.</param>
    /// <param name="requestedByNodeId">Узел, с которого ушёл запрос.</param>
    /// <returns>Pending <see cref="AccessRequest"/> (новый или уже существующий).</returns>
    Task<AccessRequest> CreateAccessRequestAsync(ConsumerId agentClientId, McpServerId mcpServerId, NodeId requestedByNodeId);

    /// <summary>
    /// 031 (mobile-push increment 2): same idempotent semantics as
    /// <see cref="CreateAccessRequestAsync"/>, but ALSO signals whether this call produced a
    /// FRESH insert (Created=true) versus returning an already-pending request (Created=false) —
    /// the trigger for <c>AccessRequestNotifier</c> (never notify on the idempotent path).
    /// <see cref="CreateAccessRequestAsync"/> is now a thin wrapper over this method (returns just
    /// <c>.Request</c>) so its ~53 existing call sites (2 production + tests) are untouched by
    /// this feature — MINIMAL RIPPLE per design §4b.
    /// </summary>
    /// <returns>The pending <see cref="AccessRequest"/> plus whether it was newly created.</returns>
    Task<CreateAccessRequestResult> CreateAccessRequestWithStatusAsync(
        ConsumerId agentClientId, McpServerId mcpServerId, NodeId requestedByNodeId);

    /// <summary>
    /// Одобряет pending-заявку: обновляет заявку и атомарно создаёт active <see cref="Grant"/>.
    /// </summary>
    /// <param name="accessRequestId">Id заявки в статусе Pending.</param>
    /// <param name="userId">Владелец Space, который одобрил.</param>
    /// <returns>Новый Grant со статусом Active.</returns>
    Task<Grant> ApproveAccessRequestAsync(AccessRequestId accessRequestId, UserId userId);

    /// <summary>Отклоняет pending-заявку без создания Grant.</summary>
    /// <param name="accessRequestId">Id заявки в статусе Pending.</param>
    /// <param name="userId">Владелец, который отклонил.</param>
    Task DenyAccessRequestAsync(AccessRequestId accessRequestId, UserId userId);

    /// <summary>Все grants Space (active и revoked) из hydrated-кэша grain.</summary>
    Task<IReadOnlyList<Grant>> ListGrantsAsync();

    /// <summary>
    /// Revokes an active grant and returns the ids of the live (Active/Opening)
    /// sessions that the revoke affected, so the caller can tear them down.
    /// </summary>
    /// <param name="grantId">Id grant в статусе Active.</param>
    /// <param name="userId">Владелец, который отозвал.</param>
    Task<IReadOnlyList<SessionId>> RevokeGrantAsync(GrantId grantId, UserId userId);

    /// <summary>Все access requests Space (pending, approved, denied, …) из hydrated-кэша.</summary>
    Task<IReadOnlyList<AccessRequest>> ListAccessRequestsAsync();

    /// <summary>
    /// 021 (Layer 1): declarative server reconcile — the node declares its COMPLETE server set;
    /// the grain makes cloud state match that declaration.
    /// <para>
    /// Pass 1 (upsert): each spec is published via the same idempotent logic as
    /// <see cref="PublishMcpServerAsync"/> — same (node, displayName) ⇒ same stable McpServerId.
    /// Sets <c>IsAsserted = true</c> on every upserted server. Disabled servers stay Disabled
    /// (owner intent is preserved).
    /// </para>
    /// <para>
    /// Pass 2 (soft-retire, AFTER upserts): servers owned by this node that are NOT in the synced
    /// set have <c>IsAsserted</c> flipped to <c>false</c>. They are NOT hard-deleted — a transient
    /// empty config can never cause permanent data loss. Hard delete is reserved for explicit intent
    /// (<see cref="DeleteMcpServerAsync"/>, <c>korat mcp remove</c>).
    /// </para>
    /// Idempotent: re-running with the same set yields the same state.
    /// </summary>
    /// <param name="publisherNodeId">Node that sent the SyncMcpServers message.</param>
    /// <param name="servers">Complete current server set declared by the node.</param>
    /// <returns>The upserted servers (the synced set, not the retired ones).</returns>
    Task<IReadOnlyList<McpServer>> SyncMcpServersAsync(NodeId publisherNodeId, IReadOnlyList<McpServerSpec> servers);

    /// <summary>
    /// Р26/Р27: <see cref="SyncMcpServersAsync"/> plus the redefinitions it performed. A declarative
    /// re-sync is the most likely way a changed definition arrives (the daemon re-declares its whole
    /// config on reconnect), so this is the overload the gateway uses.
    /// </summary>
    Task<McpServerSyncOutcome> SyncMcpServersWithOutcomeAsync(
        NodeId publisherNodeId, IReadOnlyList<McpServerSpec> servers);

    /// <summary>
    /// 021 (Layer 3): owner-initiated hard delete — removes the server row permanently, same path
    /// as <c>korat mcp remove</c> (UnpublishMcpServerAsync → RemoveAsync). Does NOT check
    /// publisherNodeId — the owner may purge any orphan regardless of which node published it.
    /// Revokes all Active grants for the server before deleting, and returns the live sessions
    /// affected so the caller can tear them down.
    /// </summary>
    /// <param name="serverId">Server to delete.</param>
    /// <param name="userId">The user initiating the delete (used as the revoker on affected grants).</param>
    /// <param name="writeTombstone">true (owner delete) writes a delete-tombstone so a passive
    /// node re-declaration cannot resurrect the server (Step B). false (024 reaper) skips the
    /// tombstone — a returning node SHOULD be allowed to re-publish.</param>
    /// <returns>Deleted=false if the server is not a member of this Space (→ 404).</returns>
    Task<DeleteMcpServerResult> DeleteMcpServerAsync(McpServerId serverId, UserId userId, bool writeTombstone = true);

    /// <summary>
    /// #165 (`korat nodes prune`): owner-initiated GC of stale <c>Agent</c>-kind nodes (the
    /// one-shot <c>korat connect --agent</c> identities), never <c>Publisher</c> nodes — those
    /// are precious. A node qualifies when its canonical LastSeenAt (read live from
    /// <see cref="INodeGrain"/>, same source ListNodesAsync uses) is older than
    /// <paramref name="olderThan"/>; a node that has never connected (LastSeenAt null) falls
    /// back to CreatedAt so a freshly-registered-but-never-seen node is not immediately
    /// eligible. For each pruned node: revokes every Active grant reachable from it (via the
    /// AgentClientIds that ever filed an AccessRequest with <c>RequestedByNodeId</c> == this
    /// node — the only way a Grant can exist, mirrors <see cref="DeleteMcpServerAsync"/>'s
    /// grant sweep) and hard-deletes the node row through <see cref="INodeGrain.RemoveAsync"/>.
    /// </summary>
    /// <param name="userId">The owner initiating the prune (recorded as the grant revoker).</param>
    /// <param name="olderThan">Cutoff instant — nodes last-seen (or created, if never seen)
    /// before this are pruned.</param>
    /// <returns>The pruned node display names and the live session ids affected by grant
    /// revocation (tear these down after the grain call, mirroring DeleteMcpServerAsync).</returns>
    Task<PruneAgentNodesResult> PruneAgentNodesAsync(UserId userId, DateTimeOffset olderThan);

    // ── Hosted Agents (PR-1) ────────────────────────────────────────────────────

    // ── Threads (PR-2 Task 4) ────────────────────────────────────────────────────

    // ── Channels (PR-2 Task 6) ──────────────────────────────────────────────────

}

/// <summary>Жизненный цикл одного узла (online/offline, heartbeat). Ключ grain = NodeId.</summary>
public interface INodeGrain : IGrainWithStringKey
{
    /// <summary>Регистрирует узел online и привязывает к gateway.</summary>
    /// <param name="spaceId">Space, к которому принадлежит узел.</param>
    /// <param name="displayName">Имя узла в UI (например hostname).</param>
    /// <param name="gatewayId">Gateway, через который установлено соединение.</param>
    /// <param name="kind">017: Publisher (runs korat up/service) or Agent (korat connect consumer). Default Publisher.</param>
    /// <param name="capabilities">029: optional capability set advertised in NodeHello (e.g. "inference"). VOLATILE — reset on reconnect.</param>
    /// <param name="hostname">Node host metadata (additive, node-visibility-doctor 2026-07-02): machine hostname. Null = not advertised (legacy CLI). Refreshed on every hello.</param>
    /// <param name="os">Node host metadata: "macos" | "linux" | "windows". Null = not advertised.</param>
    /// <param name="arch">Node host metadata: lowercase OS architecture (e.g. "arm64"). Null = not advertised.</param>
    /// <param name="cliVersion">Node host metadata: bare SemVer of the connecting CLI. Null = not advertised.</param>
    /// <returns>Обновлённый Node со статусом Online.</returns>
    Task<Node> ConnectAsync(SpaceId spaceId, string displayName, GatewayId gatewayId,
        NodeKind kind = NodeKind.Publisher,
        IReadOnlyList<string>? capabilities = null,
        string? hostname = null,
        string? os = null,
        string? arch = null,
        string? cliVersion = null);

    /// <summary>
    /// 029: True if this node advertised the given capability in its last NodeHello.
    /// VOLATILE — returns false when the node is offline or has not yet reconnected.
    /// </summary>
    Task<bool> HasCapabilityAsync(string capability);

    /// <summary>Обновляет LastSeenAt и текущий gateway (keep-alive).</summary>
    /// <param name="gatewayId">Gateway активного соединения.</param>
    Task HeartbeatAsync(GatewayId gatewayId);

    /// <summary>Помечает узел Offline (обрыв соединения, shutdown).</summary>
    Task MarkOfflineAsync();

    /// <summary>Текущее состояние узла из grain / Postgres.</summary>
    Task<Node> GetAsync();

    /// <summary>
    /// Только для разработки. Устанавливает <c>Status = NodeStatus.Online</c> и
    /// <c>LastSeenAt = DateTimeOffset.UtcNow</c> без gRPC-хэндшейка.
    /// НЕ ДОЛЖЕН вызываться из production-кода (gateway, web-эндпоинты, CLI).
    /// Единственный вызывающий — интеграционный тест push-to-wake
    /// (<c>NodeGrainPushTokenTests</c>), которому нужен онлайн-узел без gRPC-стрима.
    /// </summary>
    /// <param name="spaceId">Space, к которому принадлежит узел.</param>
    /// <param name="displayName">Имя узла в UI.</param>
    /// <returns>Обновлённый Node со статусом Online.</returns>
    Task<Node> MarkOnlineForTestingAsync(SpaceId spaceId, string displayName);

    /// <summary>
    /// 030 (push-to-wake): stores or clears the APNs device token for silent-push wake.
    /// Idempotent upsert — the grain is the single writer (grains-are-the-cache rule).
    /// <para>
    /// Pass an empty <paramref name="token"/> (and any <paramref name="platform"/>) to CLEAR
    /// the stored token — used by the APNs 410 Unregistered response path to mark the node
    /// as no longer wake-capable until the app re-registers.
    /// </para>
    /// <para>
    /// The token is NOT logged in full — only an 8-character prefix is written to logs.
    /// </para>
    /// </summary>
    /// <param name="token">APNs device token (lowercase hex). Empty string = clear.</param>
    /// <param name="platform">"apns" (production) or "apns_sandbox" (debug/dev builds).</param>
    Task RegisterPushTokenAsync(string token, string platform);

    /// <summary>
    /// 031 (mobile-push increment 2): compare-and-clear — clears the stored push token ONLY if it
    /// still equals <paramref name="deadToken"/>. Fixes a race in the unconditional
    /// <c>RegisterPushTokenAsync("", "")</c> clear: if the app re-registered a NEW live token
    /// between the failed send and the clear call, an unconditional clear would wipe the fresh
    /// token. Grain single-threading makes the compare-and-clear atomic (no separate
    /// read-then-write race). No-op if the node was never persisted, the stored token differs
    /// (already rotated), or is already empty.
    /// </summary>
    /// <param name="deadToken">The token that was just found invalid (410/BadDeviceToken/FCM Unregistered).</param>
    Task ClearPushTokenIfMatchesAsync(string deadToken);

    /// <summary>
    /// Node-visibility-doctor design (2026-07-02): sets or clears the owner-editable
    /// <see cref="Node.Note"/>. The grain is the single writer (grains-are-the-cache rule).
    /// Trims whitespace; a null/whitespace-only value clears the note. Length (≤500 chars) is
    /// validated by the caller (PATCH /api/nodes/{id}) BEFORE this is invoked — this method does
    /// not re-validate length.
    /// </summary>
    /// <param name="note">New note text, or null/empty/whitespace to clear.</param>
    /// <returns>The updated Node.</returns>
    Task<Node> SetNoteAsync(string? note);

    /// <summary>
    /// #165 (`korat nodes prune`): hard-delete the node row. Mirrors
    /// <see cref="IMcpServerGrain.RemoveAsync"/> — resets in-memory state and deactivates the
    /// grain so a later reactivation finds no row to reload (the node stays removed for good).
    /// No-op if the node was never persisted / already removed. Called ONLY by
    /// <see cref="ISpaceGrain.PruneAgentNodesAsync"/> (Kind is checked there — Publisher nodes
    /// are never routed to this method).
    /// </summary>
    Task RemoveAsync();
}

/// <summary>
/// Один MCP-сервер. Ключ grain = McpServerId.
/// Хранит команду запуска и статус Published / Disabled.
/// </summary>
public interface IMcpServerGrain : IGrainWithStringKey
{
    /// <summary>
    /// Первая публикация сервера: записывает метаданные и LaunchCommand в Postgres.
    /// Вызывается из <see cref="ISpaceGrain.PublishMcpServerAsync"/>.
    /// </summary>
    /// <param name="spaceId">Space-владелец.</param>
    /// <param name="publisherNodeId">Узел, где будет запущен MCP-процесс.</param>
    /// <param name="displayName">Имя в каталоге MCP Space.</param>
    /// <param name="command">Бинарь/команда запуска (первая часть argv).</param>
    /// <param name="args">Остальные аргументы командной строки (склеиваются при launch на node).</param>
    /// <returns>Сохранённый <see cref="McpServer"/>; Id grain = <see cref="McpServer.Id"/>.</returns>
    Task<McpServer> PublishAsync(SpaceId spaceId, NodeId publisherNodeId, string displayName, string command, string args);

    /// <summary>
    /// Обновляет команду/аргументы существующего сервера без изменения Id или CreatedAt.
    /// Вызывается из идемпотентного пути <see cref="ISpaceGrain.PublishMcpServerAsync"/>
    /// когда тот же узел повторно публикует сервер с тем же именем (reconnect/restart).
    /// Статус возвращается в Published если был Disabled.
    /// </summary>
    /// <param name="command">Новая команда запуска.</param>
    /// <param name="args">Новые аргументы.</param>
    /// <returns>Обновлённый <see cref="McpServer"/>.</returns>
    Task<McpServer> UpdateCommandAsync(string command, string args);

    /// <summary>
    /// Отключает сервер для новых grants/сессий; существующие grants не удаляются.
    /// Сервер ОСТАЁТСЯ в системе (виден в каталоге как Disabled, можно включить обратно).
    /// </summary>
    /// <param name="userId">Владелец, инициировавший disable.</param>
    /// <returns>
    /// true, если сервер реально перешёл в Disabled; false, если он уже был Disabled
    /// (идемпотентный no-op — UpdatedAt не трогается). Вызывающий код (эндпоинт /disable)
    /// использует это, чтобы не писать лишнюю audit-запись на повторный disable.
    /// </returns>
    Task<bool> DisableAsync(UserId userId);

    /// <summary>
    /// Обратная операция к <see cref="DisableAsync"/>: возвращает сервер в Published.
    /// Не трогает IsAsserted — если публикующий узел уже не оффлайн переасертил сервер,
    /// сервер станет видимым/доступным сразу; если узел молчит, он останется
    /// Published-но-недоступным до следующего re-assert (см. формулу availability в Endpoints.cs).
    /// </summary>
    /// <param name="userId">Владелец, инициировавший enable.</param>
    /// <returns>
    /// true, если сервер реально перешёл в Published; false, если он уже был Published
    /// (идемпотентный no-op — UpdatedAt не трогается), та же конвенция, что и у
    /// <see cref="DisableAsync"/>.
    /// </returns>
    Task<bool> EnableAsync(UserId userId);

    /// <summary>
    /// Полное удаление сервера из репозитория (`korat mcp remove` / unpublish). В отличие от
    /// <see cref="DisableAsync"/> строка удаляется навсегда: после rehydrate сервер не вернётся.
    /// Сбрасывает состояние grain и деактивирует его.
    /// </summary>
    Task RemoveAsync();

    /// <summary>
    /// 021 (Layer 1): set the <c>IsAsserted</c> bit without changing Status or other fields.
    /// Called by <see cref="ISpaceGrain.SyncMcpServersAsync"/> during the soft-retire pass
    /// (asserted=false) or as a side-effect of publish/update (asserted=true, done inline there).
    /// Persists the change immediately.
    /// </summary>
    /// <param name="asserted">True to assert (include in sync set); false to soft-retire.</param>
    /// <returns>Updated McpServer record.</returns>
    Task<McpServer> SetAssertedAsync(bool asserted);

    /// <summary>Текущие метаданные сервера (в т.ч. Status).</summary>
    Task<McpServer> GetAsync();

    /// <summary>
    /// Increment 1: first publish of an http_cloud server. PublisherNodeId is ALWAYS
    /// NodeId.Empty (new NodeId(string.Empty)) — mirrors InferencePointGrain.PublishOutboundAsync's
    /// "outbound: no relay node" convention. secretHint is the non-secret masked display value
    /// only (e.g. "…ab12") — the ciphertext is written separately via
    /// IMetadataRepository.SetMcpServerSecretAsync by the caller, never through this grain.
    /// </summary>
    Task<McpServer> PublishHttpCloudAsync(
        SpaceId spaceId, string displayName, string remoteUrl, string authMode, string? authHeaderName, string? secretHint);

    /// <summary>
    /// Increment 1: partial update of an http_cloud server's non-secret config. null = keep
    /// existing value (mirrors IInferencePointGrain.UpdateOutboundConfigAsync's convention)
    /// EXCEPT authHeaderName/secretHint, whose explicit clear is signalled by
    /// clearAuthHeaderName=true / clearSecretHint=true respectively (distinguishes "omitted"
    /// from "set to null" the same way UpdateOutboundConfigAsync's
    /// clearBaseUrl/clearAuthHeaderName/clearSecret flags do). clearSecretHint (Finding 16, M4)
    /// is REQUIRED for a correct secret-clear: the caller (Task 3's PATCH handler) already calls
    /// IMetadataRepository.ClearMcpServerSecretAsync to null the ciphertext, but without this
    /// flag there is no way to also null SecretHint on THIS grain's in-memory state — passing
    /// secretHint: null would be interpreted as "keep the existing hint", silently re-upserting
    /// the stale value and leaving hasSecret reporting true forever.
    /// </summary>
    Task<McpServer> UpdateHttpCloudConfigAsync(
        string? remoteUrl, string? authMode, string? authHeaderName, string? secretHint,
        bool clearAuthHeaderName = false, bool clearSecretHint = false);

    /// <summary>
    /// Increment 2 (HTTP MCP OAuth): flips an oauth server from NeedsReauth to Published after a
    /// successful authorize/callback token exchange. Idempotent (no-op, no repository write, if
    /// already Published) — mirrors EnableAsync/DisableAsync's idempotency convention. Evicts the
    /// HttpMcpProxyGrain activation on a REAL transition so the next dispatched frame reloads the
    /// freshly-stored token instead of the stale (missing) one cached at OnActivateAsync.
    /// </summary>
    Task<McpServer> MarkOAuthConnectedAsync();

    /// <summary>
    /// Increment 2: flips an oauth server to NeedsReauth — the initial pre-consent state, a
    /// refresh failure, or an edit-path invalidation (RemoteUrl change / authMode switch away
    /// from oauth then back). Idempotent; evicts the proxy grain on a real transition.
    /// </summary>
    Task<McpServer> MarkNeedsReauthAsync();
}

/// <summary>
/// Increment 1 (HTTP MCP direct-to-Space): the cloud-side MCP Streamable-HTTP client. Keyed by
/// McpServerId (single Orleans activation per server — cross-silo correctness per Global
/// Constraints). Holds ONE UPSTREAM MCP SESSION PER CONSUMER SESSION (Crux Finding 14 — never
/// multiplexed across consumers); the single activation per serverId centralizes only Orleans
/// placement, auth injection, and (Increment 2) OAuth token refresh across those sessions.
///
/// Dispatch is ONE-WAY (Crux Finding 13): DispatchFrameAsync returns once the frame is ACCEPTED,
/// never once the upstream MCP call completes — the grain implementation (in Korat.Cloud, not
/// this interface) performs the upstream call asynchronously and PUSHES the response back to
/// the consumer via SessionRoutingTable, because MCP tool calls commonly run minutes while
/// Orleans grain calls have a ~30s response timeout and a single-threaded activation would
/// otherwise serialize every consumer of a server behind whichever call is slowest.
///
/// Deliberately takes/returns only Domain types + primitives, never IRelayBackplane or the
/// protobuf RelayFrame type — Korat.GrainInterfaces has no dependency on Korat.Relay.V1/
/// Google.Protobuf/Korat.Cloud (confirmed: Korat.GrainInterfaces.csproj references only
/// Korat.Domain); the backplane-reaching push machinery is injected into the grain
/// IMPLEMENTATION (which lives in Korat.Cloud — Crux Finding 13), never exposed through this
/// interface, and every other grain interface in this file is transport-agnostic the same way.
/// </summary>
public interface IHttpMcpProxyGrain : IGrainWithStringKey
{
    /// <summary>
    /// One-way dispatch: hands one MCP JSON-RPC request (raw UTF-8 bytes, as received from the
    /// consumer) to this grain and returns as soon as the frame is ACCEPTED for processing — NOT
    /// when the upstream call finishes. The grain performs the upstream call on that consumer's
    /// own upstream MCP session (lazily `initialize`s it on first use), then pushes the response
    /// bytes (and any future notification frames) to <paramref name="consumerConnectionId"/> for
    /// <paramref name="consumerSessionId"/> asynchronously. NEVER throws for expected failure
    /// modes (decrypt failure, network error, non-2xx, malformed upstream JSON-RPC, oversized
    /// upstream response) — it always eventually pushes a valid JSON-RPC response, synthesizing a
    /// generic {"jsonrpc":"2.0","id":...,"error":{...}} object when necessary. The raw upstream
    /// error body, secret, or token is never included in what gets pushed.
    /// </summary>
    Task DispatchFrameAsync(byte[] frameBytes, ConnectionId consumerConnectionId, SessionId consumerSessionId, CancellationToken cancellationToken);

    /// <summary>
    /// Releases ONE consumer session's upstream MCP session (disposes its HttpMcpClient/HttpClient
    /// and forgets its state) — does NOT affect any other consumer's upstream session (Crux
    /// Finding 14). Called on that consumer session's close/teardown (Crux Finding 5: peer-initiated
    /// CloseSession, SessionTerminator revoke/delete, or a payload-limit-violation close).
    /// </summary>
    Task CloseConsumerSessionAsync(SessionId consumerSessionId);

    /// <summary>
    /// Evicts ALL consumers' upstream sessions and deactivates. Called by
    /// McpServerGrain.DisableAsync/RemoveAsync when Transport == http_cloud (spec §6: "disabled/
    /// removed → the proxy grain closes the upstream session(s) and evicts cached state").
    /// </summary>
    Task EvictAsync();
}

/// <summary>Клиент агента на узле. Ключ grain = ConsumerId.</summary>
public interface IConsumerGrain : IGrainWithStringKey
{
    /// <summary>Регистрирует агент (Cursor и т.д.) на узле в Space.</summary>
    /// <param name="spaceId">Space.</param>
    /// <param name="nodeId">Узел, где работает агент.</param>
    /// <param name="displayName">Имя для UI.</param>
    /// <returns>Зарегистрированный Consumer.</returns>
    Task<Consumer> RegisterAsync(SpaceId spaceId, NodeId nodeId, string displayName);

    /// <summary>Текущее состояние клиента агента.</summary>
    Task<Consumer> GetAsync();
}

/// <summary>
/// Одна relay-сессия agent ↔ MCP. Ключ grain = SessionId.
/// Payload не хранится — только метаданные и счётчики байт.
/// </summary>
public interface ISessionGrain : IGrainWithStringKey
{
    /// <summary>Открывает сессию после проверки grant; persist в Postgres.</summary>
    /// <param name="grantId">Active grant, разрешающий этот вызов.</param>
    /// <param name="agentClientId">Агент на client-узле.</param>
    /// <param name="mcpServerId">Целевой MCP-сервер.</param>
    /// <param name="clientNodeId">Узел агента.</param>
    /// <param name="publisherNodeId">Узел с MCP-процессом.</param>
    /// <param name="homeGatewayId">Gateway, через который идёт relay.</param>
    /// <param name="spaceId">Space, которому принадлежит сессия.</param>
    /// <param name="agentConnectionId">
    /// 022/Step-A: per-stream ConnectionId of the agent bridge that opened this session.
    /// Persisted on the session (EF column) so a remote silo can address the agent stream
    /// for teardown via PublishToConnectionAsync (SessionTerminator).
    /// </param>
    /// <returns>RelaySession со статусом Active.</returns>
    Task<RelaySession> OpenAsync(
        GrantId grantId,
        ConsumerId agentClientId,
        McpServerId mcpServerId,
        NodeId clientNodeId,
        NodeId publisherNodeId,
        GatewayId homeGatewayId,
        SpaceId spaceId,
        ConnectionId agentConnectionId = default);

    /// <summary>
    /// Увеличивает счётчики переданных байт (opaque ciphertext, не MCP-тело).
    /// </summary>
    /// <param name="clientToServer">Байты agent → MCP за этот increment.</param>
    /// <param name="serverToClient">Байты MCP → agent за этот increment.</param>
    Task RecordBytesAsync(long clientToServer, long serverToClient);

    /// <summary>Закрывает сессию с указанной причиной и flush метаданных в Postgres.</summary>
    /// <param name="reason">Completed, Revoked, ServiceRestart и т.д.</param>
    Task CloseAsync(SessionCloseReason reason);

    /// <summary>Закрывает сессию с причиной Revoked (grant отозван).</summary>
    Task RevokeAsync();

    /// <summary>Текущие метаданные сессии.</summary>
    Task<RelaySession> GetAsync();
}

/// <summary>
/// Зерно пользователя: профильные данные, не требующие перезагрузки с диска на каждом запросе.
/// Ключ grain = UserId (Guid, строка в формате "N" без дефисов).
/// Grains are the cache: все чтения И записи профиля идут через это зерно.
/// Endpoints must NOT inject KoratDbContext for profile reads — call GetAsync() instead.
/// </summary>
public interface IUserGrain : IGrainWithStringKey
{
    /// <summary>
    /// Returns the user's current profile from the grain's in-memory cache, reloading from
    /// the database on first access after activation. Callers (e.g. GET /api/auth/me) use
    /// this instead of querying KoratDbContext directly, satisfying the grains-are-the-cache invariant.
    /// </summary>
    /// <returns>The user record, or null if no row exists for this grain's key.</returns>
    Task<Korat.Domain.Auth.User?> GetAsync();

    /// <summary>
    /// Обновляет DisplayName пользователя. Пишет в базу и обновляет in-memory-состояние зерна.
    /// </summary>
    /// <param name="displayName">Новое имя (уже прошедшее валидацию на стороне вызывающего).</param>
    /// <returns>Актуальная запись пользователя после записи.</returns>
    Task<Korat.Domain.Auth.User> UpdateDisplayNameAsync(string displayName);

    /// <summary>
    /// Refreshes the grain's in-memory cache to reflect a primary-email promotion that was
    /// already committed to the database by <see cref="IEmailChangeService.ConfirmAsync"/>.
    ///
    /// Called by the confirm endpoint AFTER <see cref="IEmailChangeService.ConfirmAsync"/>
    /// succeeds, so the grain cache stays consistent with the DB row (grains-are-the-cache).
    /// The grain does NOT write to the database; the service has already done so atomically.
    /// </summary>
    /// <param name="newEmail">The new primary email (normalised) that is now persisted in the DB.</param>
    /// <returns>The refreshed user record.</returns>
    Task<Korat.Domain.Auth.User> UpdatePrimaryEmailAsync(string newEmail);
}

/// <summary>Облачный gateway. Ключ grain = GatewayId.</summary>
public interface IGatewayGrain : IGrainWithStringKey
{
    /// <summary>Регистрирует gateway online при старте процесса.</summary>
    Task RegisterAsync();

    /// <summary>Keep-alive gateway.</summary>
    Task HeartbeatAsync();

    /// <summary>
    /// Назначает этот gateway «домашним» для новой relay-сессии (session-home routing).
    /// </summary>
    /// <returns>Id этого gateway.</returns>
    Task<GatewayId> AssignSessionHomeAsync();

    /// <summary>Текущий статус gateway.</summary>
    Task<Gateway> GetAsync();
}

/// <summary>
/// 022/Step-A: result of <see cref="ISpaceGrain.DeleteMcpServerAsync"/>.
/// Carries both the deletion outcome and the live session ids that should be torn down
/// (their grants were revoked as part of the delete so no orphaned Active grants linger).
/// </summary>
[GenerateSerializer]
public sealed record DeleteMcpServerResult(
    /// <summary>True if the server was found and deleted; false if it was not a member of this Space (→ 404).</summary>
    [property: Id(0)] bool Deleted,
    /// <summary>Active/Opening session ids that were affected by grant revocation. Tear these down after the grain call.</summary>
    [property: Id(1)] IReadOnlyList<SessionId> AffectedSessionIds);

/// <summary>
/// #165: result of <see cref="ISpaceGrain.PruneAgentNodesAsync"/>. Carries the pruned nodes'
/// display names (for the CLI/console summary) and the live session ids that were affected by
/// grant revocation (mirrors <see cref="DeleteMcpServerResult"/>).
/// </summary>
[GenerateSerializer]
public sealed record PruneAgentNodesResult(
    [property: Id(0)] IReadOnlyList<string> PrunedNames,
    [property: Id(1)] IReadOnlyList<SessionId> AffectedSessionIds);

/// <summary>
/// 031 (mobile-push increment 2): result of <see cref="ISpaceGrain.CreateAccessRequestWithStatusAsync"/>.
/// Crosses the grain boundary (NodeGatewayService calls this over IClusterClient), hence
/// [GenerateSerializer] — unlike the plain-record AlertContent, which never leaves Korat.Cloud.
/// </summary>
[GenerateSerializer]
public sealed record CreateAccessRequestResult(
    /// <summary>The pending request — freshly inserted (Created=true) or the existing Pending row
    /// for this (agent, server) pair (Created=false, idempotent replay).</summary>
    [property: Id(0)] AccessRequest Request,
    /// <summary>True only when this call inserted a brand-new row. AccessRequestNotifier must be
    /// triggered ONLY when this is true — never on the idempotent replay path.</summary>
    [property: Id(1)] bool Created);

/// <summary>
/// PR-5 (design-review HIGH-3): result of <see cref="ISpaceGrain.DeleteAgentAsync"/>. Mirrors
/// <see cref="DeleteMcpServerResult"/> — carries the deletion outcome and the live session ids
/// affected by the linked-client cascade's grant revocation, for the caller to tear down.
/// </summary>
[GenerateSerializer]
public sealed record DeleteAgentResult(
    /// <summary>True if the agent was found and deleted; false if it was not a member of this Space (→ 404).</summary>
    [property: Id(0)] bool Deleted,
    /// <summary>Active/Opening session ids affected by the cascade's grant revocation. Empty when
    /// the agent had no linked consumer client (null ConsumerAgentClientId) or none of its grants
    /// had a live session.</summary>
    [property: Id(1)] IReadOnlyList<SessionId> AffectedSessionIds);

// ── Inference Point grains (029) ──────────────────────────────────────────────

// ── Hosted Agents (PR-1) ────────────────────────────────────────────────────

/// <summary>029: validation result returned by IInferenceEndpointKeyGrain.ValidateAsync.</summary>
[GenerateSerializer]
public sealed record EndpointKeyValidation(
    [property: Id(0)] SpaceId SpaceId,
    [property: Id(1)] InferencePointId InferencePointId,
    [property: Id(2)] string AgentName);

/// <summary>
/// 029: Grain that caches the slug→SpaceId lookup.
/// Key = lowercased slug. Caches POSITIVE results only (null re-queried each call).
/// </summary>
public interface ISpaceSlugGrain : IGrainWithStringKey
{
    /// <summary>Resolves the slug to a SpaceId. Returns null if not assigned.</summary>
    Task<SpaceId?> ResolveAsync();
}

// ── Agent Coordination Rooms (Plan A, Task 4) ─────────────────────────────────

/// <summary>
/// Result of <see cref="IRoomGrain.TryBeginWakeAsync"/> — the cascade guardrail gate.
/// </summary>
[GenerateSerializer]
public sealed record WakeDecision(
    /// <summary>True if the wake may proceed (and the cascade budget/dedup state was spent).</summary>
    [property: Id(0)] bool Allowed,
    /// <summary>Human-readable reason when <see cref="Allowed"/> is false (e.g. "budget exhausted",
    /// "no self-wake", "depth cap", "not a participant", "already woken this cascade", "room not active").</summary>
    [property: Id(1)] string? DeniedReason,
    /// <summary>True the FIRST time the turn budget is exhausted by this call (so the caller can
    /// post a one-time in-room notice) — see <see cref="RoomStatuses.BudgetExhausted"/>.</summary>
    [property: Id(2)] bool ExhaustedNow);

