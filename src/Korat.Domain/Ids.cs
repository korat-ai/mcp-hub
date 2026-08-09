namespace Korat.Domain;

// Строго типизированные идентификаторы — чтобы не перепутать nodeId и serverId на уровне компилятора.

public readonly record struct SpaceId(string Value)
{
    public override string ToString() => Value;
    public static SpaceId New() => new(Guid.NewGuid().ToString("N"));
}

public readonly record struct NodeId(string Value)
{
    public override string ToString() => Value;
    public static NodeId New() => new(Guid.NewGuid().ToString("N"));

    /// <summary>
    /// Returns true when <paramref name="value"/> is a well-formed NodeId — exactly 32
    /// lowercase hex characters (i.e. a <see cref="Guid"/> serialised with format "N").
    /// This is the canonical shape produced by <see cref="New"/>.
    ///
    /// Server-side admission validates this to prevent clients from registering
    /// low-entropy or attacker-shaped NodeIds, which would otherwise give them
    /// control over the NATS relay subject key (<c>korat.relay.frame.&lt;encode(NodeId)&gt;</c>).
    /// </summary>
    public static bool IsWellFormed(string? value) =>
        value is { Length: 32 } && Guid.TryParseExact(value, "N", out _);
}

public readonly record struct McpServerId(string Value)
{
    public override string ToString() => Value;
    public static McpServerId New() => new(Guid.NewGuid().ToString("N"));
}

public readonly record struct ConsumerId(string Value)
{
    public override string ToString() => Value;
    public static ConsumerId New() => new(Guid.NewGuid().ToString("N"));
}

public readonly record struct AccessRequestId(string Value)
{
    public override string ToString() => Value;
    public static AccessRequestId New() => new(Guid.NewGuid().ToString("N"));
}

public readonly record struct GrantId(string Value)
{
    public override string ToString() => Value;
    public static GrantId New() => new(Guid.NewGuid().ToString("N"));
}

public readonly record struct SessionId(string Value)
{
    public override string ToString() => Value;
    public static SessionId New() => new(Guid.NewGuid().ToString("N"));
}

public readonly record struct GatewayId(string Value)
{
    public override string ToString() => Value;
    public static GatewayId New() => new(Guid.NewGuid().ToString("N"));
}

public readonly record struct ConnectionId(string Value)
{
    public override string ToString() => Value;
    public static ConnectionId New() => new(Guid.NewGuid().ToString("N"));
}

public readonly record struct InferencePointId(string Value)
{
    public override string ToString() => Value;
    public static InferencePointId New() => new(Guid.NewGuid().ToString("N"));
}

public readonly record struct AgentId(string Value)
{
    public override string ToString() => Value;
    public static AgentId New() => new(Guid.NewGuid().ToString("N"));
}

public readonly record struct ThreadId(string Value)
{
    public override string ToString() => Value;
    public static ThreadId New() => new(Guid.NewGuid().ToString("N"));
}

public readonly record struct MessageId(string Value)
{
    public override string ToString() => Value;
    public static MessageId New() => new(Guid.NewGuid().ToString("N"));
}

public readonly record struct RoomId(string Value)
{
    public override string ToString() => Value;
    public static RoomId New() => new(Guid.NewGuid().ToString("N"));

    /// <summary>Review LOW (create race): a deterministic id per (space, owner) so two concurrent
    /// first-time "create-or-return" POSTs converge on the SAME grain (one room per owner) instead
    /// of racing two <see cref="New"/> ids into the (SpaceId, OwnerPrincipalUserId) unique index —
    /// EnsureCreatedAsync then serializes at the single activation and is idempotent-by-id.</summary>
    public static RoomId ForOwner(SpaceId spaceId, string ownerPrincipalUserId) =>
        new(System.Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes($"room:{spaceId.Value}:{ownerPrincipalUserId}")))[..32].ToLowerInvariant());
}

public readonly record struct RoomParticipantId(string Value)
{
    public override string ToString() => Value;
    public static RoomParticipantId New() => new(Guid.NewGuid().ToString("N"));
}

public readonly record struct RoomMessageId(string Value)
{
    public override string ToString() => Value;
    public static RoomMessageId New() => new(Guid.NewGuid().ToString("N"));
}

public readonly record struct ChannelBindingId(string Value)
{
    public override string ToString() => Value;
    public static ChannelBindingId New() => new(Guid.NewGuid().ToString("N"));
}

/// <summary>
/// Well-known, non-random NodeId (and other id) values with special meaning across the codebase
/// — as opposed to the *Id.New() factories above, which always mint random Guid-shaped values.
/// </summary>
public static class WellKnownNodeIds
{
    /// <summary>
    /// MUST-FIX 2 (adversarial review, Space-MCP increment 1 Tasks 4-6): the synthetic "publisher"
    /// NodeId <c>Korat.Cloud.Gateways.Admission.SessionAdmission.AggregatorSentinelNodeId</c>
    /// stamps on every backend relay session a Space-MCP aggregator grain opens as a
    /// <c>ConsumerBindPolicy.ServerMinted</c> consumer — never a real bridge/publisher node; there
    /// is no gRPC stream behind it, only the aggregator's in-process delivery leg.
    ///
    /// Defined here (in Korat.Domain, not Korat.Cloud) as the SINGLE source of truth because
    /// <c>Korat.Persistence</c> — which cannot reference <c>Korat.Cloud</c> — needs the exact same
    /// literal to gate <c>EfMetadataRepository.ListReapableSessionsAsync</c>'s client-node
    /// OR-clause: a sentinel-client session deliberately has no <c>Nodes</c> row (see that method's
    /// own comment), so it must not be reap-eligible purely on the missing row the way a genuinely
    /// abandoned client would be.
    /// </summary>
    public const string AggregatorSentinelNodeId = "cagg-sentinel";
}
