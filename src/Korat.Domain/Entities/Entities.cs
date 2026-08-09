using UserId = Korat.Domain.Auth.UserId;

namespace Korat.Domain.Entities;

// Доменные сущности Korat — «что есть в системе», без деталей хранения и транспорта.

/// <summary>Личное пространство владельца: изолированный контур узлов, серверов и доступов.</summary>
public sealed class Space
{
    public SpaceId Id { get; init; }
    public UserId OwnerUserId { get; init; }
    public string DisplayName { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>
/// Transport runtime endpoint connected to Korat. Publisher nodes normally represent a device
/// hosting MCP servers; agent nodes are consumer identities and are not necessarily distinct
/// physical machines.
/// </summary>
public sealed class Node
{
    public NodeId Id { get; init; }
    public SpaceId SpaceId { get; init; }
    public string DisplayName { get; set; } = string.Empty;
    public string DeviceFingerprint { get; set; } = string.Empty;
    public NodeStatus Status { get; set; } = NodeStatus.Offline;
    /// <summary>017: Publisher (hosts MCP servers, runs `korat up`/`service`) or Agent
    /// (a `korat connect` consumer identity). Default Publisher keeps pre-017 nodes valid.</summary>
    public NodeKind Kind { get; set; } = NodeKind.Publisher;
    /// <summary>Gateway, через который узел сейчас подключён к облаку.</summary>
    public GatewayId? CurrentGatewayId { get; set; }
    public DateTimeOffset? LastSeenAt { get; set; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; set; }

    // 030 (push-to-wake): APNs device token for silent-push wake.
    // Null = node is foreground-only (CLI / Android / old iOS — behaviour unchanged).
    // Empty string = token was cleared via a 410 Unregistered response.
    /// <summary>APNs device token (lowercase hex). Null = not wake-capable.</summary>
    public string? PushToken { get; set; }
    /// <summary>"apns" (production) or "apns_sandbox" (debug/dev builds). Null = none.</summary>
    public string? PushPlatform { get; set; }
    /// <summary>When the push token was last registered or cleared.</summary>
    public DateTimeOffset? PushTokenUpdatedAt { get; set; }

    /// <summary>
    /// cloud-m9: capability set advertised in the last NodeHello, persisted so NodeGrain
    /// can repopulate _capabilities on reactivation (including cross-silo activations).
    /// Stored as a JSON array string (e.g. '["e2e-v1","inference"]'). Null = none.
    /// Populated by NodeGrain.ConnectAsync; repopulated from this field on OnActivateAsync.
    /// </summary>
    public List<string> Capabilities { get; set; } = [];

    // Node host metadata (additive, node-visibility-doctor design 2026-07-02): "где кто запущен
    // на каком хосте". Populated from NodeHello.hostname/os/arch/cli_version, refreshed on EVERY
    // hello (unlike PushToken, these are not preserved across a hello that omits them — a legacy
    // CLI reconnecting genuinely has no metadata to report). Null = never advertised.
    /// <summary>Machine hostname reported by the connecting CLI. Null = legacy CLI / not yet advertised.</summary>
    public string? Hostname { get; set; }
    /// <summary>"macos" | "linux" | "windows". Null = legacy CLI / not yet advertised.</summary>
    public string? Os { get; set; }
    /// <summary>Lowercase OS architecture (e.g. "arm64", "x64"). Null = legacy CLI / not yet advertised.</summary>
    public string? Arch { get; set; }
    /// <summary>Bare SemVer of the connecting CLI (e.g. "0.4.1"). Null = legacy CLI / not yet advertised.</summary>
    public string? CliVersion { get; set; }

    // Owner-editable note (additive, node-visibility-doctor design 2026-07-02): "нельзя добавить
    // комментарий к запущенному инстансу". The human layer, distinct from the machine-assigned
    // DisplayName — set/cleared only via PATCH /api/nodes/{id} (owner-scoped), never by a hello.
    /// <summary>Owner-set free-text label, ≤500 chars (enforced at the endpoint). Null = unset.</summary>
    public string? Note { get; set; }
}

/// <summary>MCP-сервер, опубликованный с узла (например filesystem, git) — «что удалённо вызывают».</summary>
public sealed class McpServer
{
    public McpServerId Id { get; init; }
    public SpaceId SpaceId { get; init; }
    /// <summary>Узел, на котором запускается процесс MCP.</summary>
    public NodeId PublisherNodeId { get; init; }
    public string DisplayName { get; set; } = string.Empty;
    public string Transport { get; set; } = "Stdio";
    public string LaunchCommand { get; set; } = string.Empty;
    public string LaunchArguments { get; set; } = string.Empty;
    public McpServerStatus Status { get; set; } = McpServerStatus.Published;
    /// <summary>
    /// 021: true when the publisher node's last SyncMcpServers included this server (or it was
    /// published via an explicit PublishMcpServer). False = the node reconnected without declaring
    /// this server (soft-retire). Default true keeps pre-021 publish-only daemons correct —
    /// they never send a sync so all their servers are always asserted.
    /// Availability = Published &amp;&amp; IsAsserted &amp;&amp; ownerNode.Online (Layer 2).
    /// </summary>
    public bool IsAsserted { get; set; } = true;
    /// <summary>
    /// Increment 1 (http_cloud only): the remote Streamable-HTTP MCP endpoint URL.
    /// Null for stdio_node servers.
    /// </summary>
    public string? RemoteUrl { get; set; }
    /// <summary>
    /// Increment 1 (http_cloud only): "none" | "bearer" | "header" (see McpServerAuthModes).
    /// Null for stdio_node servers.
    /// </summary>
    public string? AuthMode { get; set; }
    /// <summary>
    /// Increment 1 (http_cloud only): custom header name when AuthMode == "header". Null otherwise.
    /// </summary>
    public string? AuthHeaderName { get; set; }
    /// <summary>
    /// Increment 1 (http_cloud only): non-secret masked hint of the stored static secret
    /// (e.g. "…ab12"), mirrors InferencePoint.SecretHint. Null if no secret has been set.
    /// The ciphertext itself is EF-only (EncryptedSecret on McpServerRecord) — never in this
    /// domain entity, never in ToRecord/ToDomain (see EntityMapping.cs, IMetadataRepository.cs).
    /// </summary>
    public string? SecretHint { get; set; }
    /// <summary>
    /// Р27: the launch command this server had BEFORE its most recent definition change, and when
    /// that change happened. Null until a definition actually changes.
    ///
    /// Kept on the entity rather than read back from the audit log because of where it has to be
    /// shown: the owner approving a fresh access request needs to see WHAT changed at the moment
    /// they click, and the audit log is admin-scoped and not part of that screen. A notification
    /// that only says "the definition changed" invites a reflexive approve, which is how Р26's
    /// protection gets bypassed through the human rather than through the code.
    /// </summary>
    public string? PreviousLaunchCommand { get; set; }
    /// <summary>Р27: arguments that went with <see cref="PreviousLaunchCommand"/>.</summary>
    public string? PreviousLaunchArguments { get; set; }
    /// <summary>Р27: when the definition last changed. Null = never changed since publication.</summary>
    public DateTimeOffset? DefinitionChangedAt { get; set; }
    public DateTimeOffset? LastSeenAt { get; set; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>
/// Step-B (delete-tombstone): a durable record that an owner deleted the MCP server named
/// <see cref="DisplayName"/> published by <see cref="PublisherNodeId"/> in <see cref="SpaceId"/>.
/// While this row exists, <c>SpaceGrain.PublishMcpServerAsync</c> REFUSES to re-create that
/// (node, name) on a passive SyncMcpServers re-declaration — so a delete performed while the node
/// was offline is not silently undone when the node reconnects and re-asserts its full config.
/// Cleared by <c>SyncMcpServersAsync</c> when the node stops declaring the name (genuine drop →
/// a later re-add is allowed). Keyed by (SpaceId, PublisherNodeId, DisplayName).
/// </summary>
public sealed class McpServerTombstone
{
    public SpaceId SpaceId { get; init; }
    public NodeId PublisherNodeId { get; init; }
    public string DisplayName { get; init; } = string.Empty;
    public DateTimeOffset CreatedAt { get; init; }
    /// <summary>Owner who performed the delete (audit). System reaper deletes do NOT tombstone.</summary>
    public UserId CreatedByUserId { get; init; }
}

/// <summary>Клиент агента (Cursor, CLI и т.д.) на узле — «кто хочет вызвать MCP».</summary>
public sealed class Consumer
{
    public ConsumerId Id { get; init; }
    public SpaceId SpaceId { get; init; }
    public NodeId NodeId { get; init; }
    public string DisplayName { get; set; } = string.Empty;
    public ConsumerStatus Status { get; set; } = ConsumerStatus.Offline;
    public DateTimeOffset? LastSeenAt { get; set; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>Запрос доступа: агент просит владельца разрешить доступ к конкретному MCP-серверу.</summary>
public sealed class AccessRequest
{
    public AccessRequestId Id { get; init; }
    public SpaceId SpaceId { get; init; }
    public ConsumerId ConsumerId { get; init; }
    public McpServerId McpServerId { get; init; }
    /// <summary>Узел, с которого пришёл запрос (обычно там же живёт агент).</summary>
    public NodeId RequestedByNodeId { get; init; }
    /// <summary>Узел, где крутится целевой MCP-сервер.</summary>
    public NodeId PublisherNodeId { get; init; }
    public AccessRequestStatus Status { get; set; } = AccessRequestStatus.Pending;
    public DateTimeOffset RequestedAt { get; init; }
    public DateTimeOffset? ResolvedAt { get; set; }
    public UserId? ResolvedByUserId { get; set; }
}

/// <summary>Выданное разрешение: пара (агент, MCP-сервер) может открыть relay-сессию.</summary>
public sealed class Grant
{
    public GrantId Id { get; init; }
    public SpaceId SpaceId { get; init; }
    public ConsumerId ConsumerId { get; init; }
    public McpServerId McpServerId { get; init; }
    public GrantStatus Status { get; set; } = GrantStatus.Active;
    /// <summary>
    /// Р26: <see cref="McpServerDefinition.Digest(McpServer)"/> of the server AS IT WAS when the
    /// owner approved. Admission compares it against the server's current digest and refuses to
    /// apply the permission when they differ, so a re-published server with a changed launch
    /// definition cannot inherit an approval given for the old one.
    ///
    /// Empty string for grants approved before this field existed: those are treated as
    /// "digest unknown", which admission handles explicitly rather than by silently passing.
    /// </summary>
    public string ApprovedDefinitionDigest { get; set; } = string.Empty;
    public AccessRequestId? CreatedFromAccessRequestId { get; init; }
    public UserId ApprovedByUserId { get; init; }
    public DateTimeOffset ApprovedAt { get; init; }
    public DateTimeOffset? RevokedAt { get; set; }
    public UserId? RevokedByUserId { get; set; }
}

/// <summary>Операционная история relay-сессии. Содержимое MCP-пакетов не хранится — только метаданные.</summary>
public sealed class RelaySession
{
    public SessionId Id { get; init; }
    public SpaceId SpaceId { get; init; }
    public GrantId GrantId { get; init; }
    public ConsumerId ConsumerId { get; init; }
    public McpServerId McpServerId { get; init; }
    public NodeId ClientNodeId { get; init; }
    public NodeId PublisherNodeId { get; init; }
    /// <summary>Gateway, который маршрутизирует трафик этой сессии.</summary>
    public GatewayId HomeGatewayId { get; set; }
    public SessionStatus Status { get; set; } = SessionStatus.Opening;
    public SessionCloseReason? CloseReason { get; set; }
    public DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset? EndedAt { get; set; }
    public long BytesClientToServer { get; set; }
    public long BytesServerToClient { get; set; }
    public bool LargeTransferWarning { get; set; }

    /// <summary>
    /// 022/Step-A: per-stream ConnectionId of the agent bridge that opened this session.
    /// PERSISTED (EntityMapping + EF column) so any silo can address the agent stream via
    /// PublishToConnectionAsync for cross-silo session teardown on revoke/delete. Read by
    /// OrleansSessionRouteResolver (publisher→agent routing) and by SessionTerminator.
    /// </summary>
    public ConnectionId AgentConnectionId { get; set; }
}

/// <summary>Экземпляр облачного gateway — точка входа узлов и relay между ними.</summary>
public sealed class Gateway
{
    public GatewayId Id { get; init; }
    public GatewayStatus Status { get; set; } = GatewayStatus.Offline;
    public DateTimeOffset? LastHeartbeatAt { get; set; }
}

/// <summary>Лимиты размера сообщений и сессии (защита от перегрузки relay).</summary>
public sealed class PayloadLimitPolicy
{
    public const long DefaultPerMessageBytes = 16L * 1024 * 1024;
    public const long DefaultSessionWarningBytes = 50L * 1024 * 1024;
    public const long DefaultSessionHardLimitBytes = 250L * 1024 * 1024;

    public long PerMessageLimitBytes { get; init; } = DefaultPerMessageBytes;
    public long SessionWarningBytes { get; init; } = DefaultSessionWarningBytes;
    public long SessionHardLimitBytes { get; init; } = DefaultSessionHardLimitBytes;
}

/// <summary>
/// Agent Coordination Rooms (Plan A): status of a <see cref="Room"/>.
/// </summary>
public static class RoomStatuses
{
    public const string Active = "active";
    public const string Paused = "paused";
    public const string BudgetExhausted = "budget_exhausted";
}

