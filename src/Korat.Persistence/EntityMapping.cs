using System.Text.Json;
using Korat.Domain;
using Korat.Domain.Entities;
using UserId = Korat.Domain.Auth.UserId;

namespace Korat.Persistence;

/// <summary>
/// Record ↔ Domain mappers for relay entities (Node, McpServer, AccessRequest, Grant, RelaySession).
///
/// Auth-domain entities (User, AuthSession, CliToken, Invite, MagicLinkToken, BootstrapState)
/// are intentionally NOT mapped through this file. They are mapped directly by EF Core value
/// conversions in <see cref="KoratDbContext.OnModelCreating"/> and queried as domain types
/// via their own DbSet. This is a deliberate two-tier persistence convention:
///   - Relay entities: ORM reads/writes go through Record DTOs + EntityMapping pair.
///   - Auth entities:  ORM reads/writes go directly through the domain record types.
/// When touching persistence, add new relay entities here; add new auth entities to KoratDbContext.
///
/// <see cref="Korat.Domain.Entities.Agent"/> (hosted agents, PR-1) also lives on the direct-mapping
/// tier — like the Auth entities, it is a plain domain type mapped straight in
/// <see cref="KoratDbContext.OnModelCreating"/> with no Record/EntityMapping pair (see the
/// "Hosted Agents (PR-1)" section there).
///
/// <see cref="Korat.Domain.Entities.Thread"/> and <see cref="Korat.Domain.Entities.Message"/>
/// (hosted agents PR-2, Threads/Channels) follow the same direct-mapping tier — see the
/// "Threads &amp; Channels (PR-2)" section in <see cref="KoratDbContext.OnModelCreating"/>.
/// </summary>
internal static class EntityMapping
{
    public static NodeRecord ToRecord(Node node) => new()
    {
        Id = node.Id.Value,
        SpaceId = node.SpaceId.Value,
        DisplayName = node.DisplayName,
        DeviceFingerprint = node.DeviceFingerprint,
        Status = node.Status,
        Kind = node.Kind,
        CurrentGatewayId = node.CurrentGatewayId?.Value,
        LastSeenAt = node.LastSeenAt,
        CreatedAt = node.CreatedAt,
        UpdatedAt = node.UpdatedAt,
        // 030 (push-to-wake): nullable — null-round-trips cleanly for non-mobile nodes.
        PushToken = node.PushToken,
        PushPlatform = node.PushPlatform,
        PushTokenUpdatedAt = node.PushTokenUpdatedAt,
        // cloud-m9: capabilities — null when empty to save storage on non-capability nodes.
        CapabilitiesJson = node.Capabilities.Count > 0
            ? JsonSerializer.Serialize(node.Capabilities)
            : null,
        // Node host metadata (additive, node-visibility-doctor design 2026-07-02) — nullable,
        // round-trips as-is.
        Hostname = node.Hostname,
        Os = node.Os,
        Arch = node.Arch,
        CliVersion = node.CliVersion,
        // Owner-editable note (additive, node-visibility-doctor design 2026-07-02) — nullable,
        // round-trips as-is.
        Note = node.Note
    };

    public static Node ToDomain(NodeRecord record) => new()
    {
        Id = new NodeId(record.Id),
        SpaceId = new SpaceId(record.SpaceId),
        DisplayName = record.DisplayName,
        DeviceFingerprint = record.DeviceFingerprint,
        Status = record.Status,
        Kind = record.Kind,
        CurrentGatewayId = record.CurrentGatewayId is null ? null : new GatewayId(record.CurrentGatewayId),
        LastSeenAt = record.LastSeenAt,
        CreatedAt = record.CreatedAt,
        UpdatedAt = record.UpdatedAt,
        // 030 (push-to-wake): nullable — old rows have null; new mobile rows carry token.
        PushToken = record.PushToken,
        PushPlatform = record.PushPlatform,
        PushTokenUpdatedAt = record.PushTokenUpdatedAt,
        // cloud-m9: capabilities — deserialize from JSON; default to empty list for null/old rows.
        Capabilities = record.CapabilitiesJson is not null
            ? JsonSerializer.Deserialize<List<string>>(record.CapabilitiesJson) ?? []
            : [],
        // Node host metadata (additive, node-visibility-doctor design 2026-07-02) — nullable,
        // round-trips as-is.
        Hostname = record.Hostname,
        Os = record.Os,
        Arch = record.Arch,
        CliVersion = record.CliVersion,
        // Owner-editable note (additive, node-visibility-doctor design 2026-07-02) — nullable,
        // round-trips as-is.
        Note = record.Note
    };

    public static McpServerRecord ToRecord(McpServer server) => new()
    {
        Id = server.Id.Value,
        SpaceId = server.SpaceId.Value,
        PublisherNodeId = server.PublisherNodeId.Value,
        DisplayName = server.DisplayName,
        Transport = server.Transport,
        LaunchCommand = server.LaunchCommand,
        LaunchArguments = server.LaunchArguments,
        Status = server.Status,
        IsAsserted = server.IsAsserted,
        LastSeenAt = server.LastSeenAt,
        CreatedAt = server.CreatedAt,
        UpdatedAt = server.UpdatedAt,
        // Increment 1 (http_cloud): non-secret fields only — EncryptedSecret is intentionally
        // NOT set here (never in the domain entity), mirrors InferencePoint's ToRecord exactly.
        RemoteUrl = server.RemoteUrl,
        AuthMode = server.AuthMode,
        AuthHeaderName = server.AuthHeaderName,
        PreviousLaunchCommand = server.PreviousLaunchCommand,
        PreviousLaunchArguments = server.PreviousLaunchArguments,
        DefinitionChangedAt = server.DefinitionChangedAt,
        SecretHint = server.SecretHint
    };

    public static McpServer ToDomain(McpServerRecord record) => new()
    {
        Id = new McpServerId(record.Id),
        SpaceId = new SpaceId(record.SpaceId),
        PublisherNodeId = new NodeId(record.PublisherNodeId),
        DisplayName = record.DisplayName,
        Transport = record.Transport,
        LaunchCommand = record.LaunchCommand,
        LaunchArguments = record.LaunchArguments,
        Status = record.Status,
        IsAsserted = record.IsAsserted,
        LastSeenAt = record.LastSeenAt,
        CreatedAt = record.CreatedAt,
        UpdatedAt = record.UpdatedAt,
        RemoteUrl = record.RemoteUrl,
        AuthMode = record.AuthMode,
        AuthHeaderName = record.AuthHeaderName,
        PreviousLaunchCommand = record.PreviousLaunchCommand,
        PreviousLaunchArguments = record.PreviousLaunchArguments,
        DefinitionChangedAt = record.DefinitionChangedAt,
        SecretHint = record.SecretHint
        // EncryptedSecret is intentionally NOT mapped to domain — stays EF-tier only.
    };

    public static McpServerTombstoneRecord ToRecord(McpServerTombstone t) => new()
    {
        SpaceId = t.SpaceId.Value,
        PublisherNodeId = t.PublisherNodeId.Value,
        DisplayName = t.DisplayName,
        CreatedAt = t.CreatedAt,
        CreatedByUserId = t.CreatedByUserId.Value.ToString("N")
    };

    public static McpServerTombstone ToDomain(McpServerTombstoneRecord r) => new()
    {
        SpaceId = new SpaceId(r.SpaceId),
        PublisherNodeId = new NodeId(r.PublisherNodeId),
        DisplayName = r.DisplayName,
        CreatedAt = r.CreatedAt,
        CreatedByUserId = new UserId(
            Guid.TryParse(r.CreatedByUserId, out var g) ? g : Guid.Empty)
    };

    public static ConsumerRecord ToRecord(Consumer agentClient) => new()
    {
        Id = agentClient.Id.Value,
        SpaceId = agentClient.SpaceId.Value,
        NodeId = agentClient.NodeId.Value,
        DisplayName = agentClient.DisplayName,
        Status = agentClient.Status,
        LastSeenAt = agentClient.LastSeenAt,
        CreatedAt = agentClient.CreatedAt,
        UpdatedAt = agentClient.UpdatedAt
    };

    public static Consumer ToDomain(ConsumerRecord record) => new()
    {
        Id = new ConsumerId(record.Id),
        SpaceId = new SpaceId(record.SpaceId),
        NodeId = new NodeId(record.NodeId),
        DisplayName = record.DisplayName,
        Status = record.Status,
        LastSeenAt = record.LastSeenAt,
        CreatedAt = record.CreatedAt,
        UpdatedAt = record.UpdatedAt
    };

    public static AccessRequestRecord ToRecord(AccessRequest request) => new()
    {
        Id = request.Id.Value,
        SpaceId = request.SpaceId.Value,
        ConsumerId = request.ConsumerId.Value,
        McpServerId = request.McpServerId.Value,
        RequestedByNodeId = request.RequestedByNodeId.Value,
        PublisherNodeId = request.PublisherNodeId.Value,
        Status = request.Status,
        RequestedAt = request.RequestedAt,
        ResolvedAt = request.ResolvedAt,
        ResolvedByUserId = request.ResolvedByUserId?.ToString()
    };

    public static AccessRequest ToDomain(AccessRequestRecord record) => new()
    {
        Id = new AccessRequestId(record.Id),
        SpaceId = new SpaceId(record.SpaceId),
        ConsumerId = new ConsumerId(record.ConsumerId),
        McpServerId = new McpServerId(record.McpServerId),
        RequestedByNodeId = new NodeId(record.RequestedByNodeId),
        PublisherNodeId = new NodeId(record.PublisherNodeId),
        Status = record.Status,
        RequestedAt = record.RequestedAt,
        ResolvedAt = record.ResolvedAt,
        ResolvedByUserId = UserId.TryParse(record.ResolvedByUserId)
    };

    public static GrantRecord ToRecord(Grant grant) => new()
    {
        Id = grant.Id.Value,
        SpaceId = grant.SpaceId.Value,
        ConsumerId = grant.ConsumerId.Value,
        McpServerId = grant.McpServerId.Value,
        Status = grant.Status,
        ApprovedDefinitionDigest = grant.ApprovedDefinitionDigest,
        CreatedFromAccessRequestId = grant.CreatedFromAccessRequestId?.Value,
        ApprovedByUserId = grant.ApprovedByUserId.ToString(),
        ApprovedAt = grant.ApprovedAt,
        RevokedAt = grant.RevokedAt,
        RevokedByUserId = grant.RevokedByUserId?.ToString()
    };

    public static Grant ToDomain(GrantRecord record) => new()
    {
        Id = new GrantId(record.Id),
        SpaceId = new SpaceId(record.SpaceId),
        ConsumerId = new ConsumerId(record.ConsumerId),
        McpServerId = new McpServerId(record.McpServerId),
        Status = record.Status,
        ApprovedDefinitionDigest = record.ApprovedDefinitionDigest ?? string.Empty,
        CreatedFromAccessRequestId = record.CreatedFromAccessRequestId is null ? null : new AccessRequestId(record.CreatedFromAccessRequestId),
        // Sentinel fallback: a bad/legacy stored value degrades to empty-guid instead of crashing
        // the grants read with FormatException. ApprovedByUserId is non-nullable in the domain.
        ApprovedByUserId = UserId.TryParse(record.ApprovedByUserId) ?? new UserId(Guid.Empty),
        ApprovedAt = record.ApprovedAt,
        RevokedAt = record.RevokedAt,
        RevokedByUserId = UserId.TryParse(record.RevokedByUserId)
    };

    public static SessionRecord ToRecord(RelaySession session) => new()
    {
        Id = session.Id.Value,
        SpaceId = session.SpaceId.Value,
        GrantId = session.GrantId.Value,
        ConsumerId = session.ConsumerId.Value,
        McpServerId = session.McpServerId.Value,
        ClientNodeId = session.ClientNodeId.Value,
        PublisherNodeId = session.PublisherNodeId.Value,
        HomeGatewayId = session.HomeGatewayId.Value,
        Status = session.Status,
        CloseReason = session.CloseReason,
        StartedAt = session.StartedAt,
        EndedAt = session.EndedAt,
        BytesClientToServer = session.BytesClientToServer,
        BytesServerToClient = session.BytesServerToClient,
        LargeTransferWarning = session.LargeTransferWarning,
        // Sessions opened without a known agent ConnectionId (default ConnectionId) carry a
        // null .Value; the column is NOT NULL (defaults to ""), so coalesce to keep persist valid.
        AgentConnectionId = session.AgentConnectionId.Value ?? string.Empty
    };

    public static RelaySession ToDomain(SessionRecord record) => new()
    {
        Id = new SessionId(record.Id),
        SpaceId = new SpaceId(record.SpaceId),
        GrantId = new GrantId(record.GrantId),
        ConsumerId = new ConsumerId(record.ConsumerId),
        McpServerId = new McpServerId(record.McpServerId),
        ClientNodeId = new NodeId(record.ClientNodeId),
        PublisherNodeId = new NodeId(record.PublisherNodeId),
        HomeGatewayId = new GatewayId(record.HomeGatewayId),
        Status = record.Status,
        CloseReason = record.CloseReason,
        StartedAt = record.StartedAt,
        EndedAt = record.EndedAt,
        BytesClientToServer = record.BytesClientToServer,
        BytesServerToClient = record.BytesServerToClient,
        LargeTransferWarning = record.LargeTransferWarning,
        AgentConnectionId = new ConnectionId(record.AgentConnectionId ?? string.Empty)
    };

}
