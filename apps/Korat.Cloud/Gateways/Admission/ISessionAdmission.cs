using Korat.Domain;

namespace Korat.Cloud.Gateways.Admission;

/// <summary>
/// How a consumer identity's TOFU bind (to a NodeId) is trusted before a session is admitted.
/// See <see cref="SessionAdmission.AdmitAsync"/> for exactly where the two variants diverge.
/// </summary>
public enum ConsumerBindPolicy
{
    /// <summary>
    /// The gRPC agent-bridge path (<see cref="NodeGatewayService.HandleRequestSessionAsync"/>):
    /// the ConsumerId is bound Trust-On-First-Use to whichever NodeId's authenticated stream
    /// first presents it (023/ARCH-CRITICAL-2). A <c>cagg_</c>-prefixed ConsumerId is rejected
    /// outright — that namespace is reserved for <see cref="ServerMinted"/> identities only.
    /// </summary>
    NodeTofu,

    /// <summary>
    /// The Space-MCP aggregator path (2026-07-10 increment 1): the consumer identity is minted
    /// server-side (<c>SpaceMcpConsumerIdentity</c>, always <c>cagg_</c>-prefixed) and bound to
    /// <see cref="SessionAdmission.AggregatorSentinelNodeId"/> instead of a real bridge NodeId —
    /// there is no gRPC stream backing these sessions. Skips the hosted-agent attribution stamp
    /// and forces <c>PeerSupportsE2e=false</c> (the cloud is always the plaintext terminus for
    /// this path — SF-8).
    /// </summary>
    ServerMinted
}

/// <summary>
/// The identity requesting a session, abstracted away from the gRPC-specific
/// <c>ConnectionContext</c> so the same admission gauntlet can be driven by both the gRPC gateway
/// (<see cref="ConsumerBindPolicy.NodeTofu"/>) and the Space-MCP aggregator grain
/// (<see cref="ConsumerBindPolicy.ServerMinted"/>).
/// </summary>
/// <param name="ConsumerId">The consumer identity requesting the session.</param>
/// <param name="ConsumerSpaceId">The consumer's own Space — authoritatively resolved server-side
/// (never a wire field the caller controls).</param>
/// <param name="AgentConnectionId">The per-stream (or synthetic, for the aggregator) ConnectionId
/// that publisher→agent frames are routed back to.</param>
/// <param name="RequestingNodeId">Attributed as "requested by" on a new AccessRequest, and
/// (<see cref="ConsumerBindPolicy.NodeTofu"/> only) the NodeId the ConsumerId is bound to.</param>
/// <param name="AgentId">Non-null only for a NodeTofu stream that is a hosted-agent bridge
/// (<c>NodeHello.agent_id</c>) — stamps <c>Agent.ConsumerAgentClientId</c>. Always null for
/// <see cref="ConsumerBindPolicy.ServerMinted"/>.</param>
/// <param name="BindPolicy">Which TOFU-bind behaviour to apply.</param>
/// <param name="DisplayName">Optional user-facing consumer name. It is informational only and
/// never participates in authentication or authorization.</param>
public sealed record ConsumerPrincipal(
    ConsumerId ConsumerId,
    SpaceId ConsumerSpaceId,
    ConnectionId AgentConnectionId,
    NodeId RequestingNodeId,
    string? AgentId,
    ConsumerBindPolicy BindPolicy,
    string? DisplayName = null);

/// <summary>
/// Outcome of <see cref="ISessionAdmission.AdmitAsync"/>. The caller (the gRPC adapter in
/// <see cref="NodeGatewayService"/>, or the Space-MCP aggregator grain) translates this into its
/// own wire/internal representation — this type carries no gRPC- or HTTP-specific shape.
/// </summary>
public abstract record AdmissionResult
{
    private AdmissionResult() { }

    /// <summary>A relay session was opened. <paramref name="PeerSupportsE2e"/> is advisory only —
    /// callers must still validate the handshake outcome rather than trusting this flag blindly.</summary>
    public sealed record Opened(SessionId SessionId, GatewayId HomeGatewayId, bool PeerSupportsE2e) : AdmissionResult;

    /// <summary>No active grant exists — an AccessRequest was created and is awaiting owner approval.</summary>
    public sealed record Pending(AccessRequestId AccessRequestId) : AdmissionResult;

    /// <summary>Admission refused. <paramref name="Reason"/> is the exact wire reason string —
    /// either <c>KoratError.Message(...)</c> text or a raw machine reason such as
    /// <c>"agent_client_node_mismatch"</c> — unchanged from what <c>HandleRequestSessionAsync</c>
    /// wrote to the gRPC stream before this extraction.</summary>
    public sealed record Denied(string Reason) : AdmissionResult;
}

/// <summary>
/// The shared session-admission gauntlet extracted from
/// <see cref="NodeGatewayService.HandleRequestSessionAsync"/> (BLOCKER-3, 2026-07-10 Space-MCP
/// plan Task 2) so the gRPC gateway and the future Space-MCP aggregator grain (Task 4) can never
/// diverge on grant enforcement — a dropped or reordered check here is a tenant hole on BOTH
/// consumption paths at once.
///
/// Every check the original method performed is preserved byte-for-byte on the
/// <see cref="ConsumerBindPolicy.NodeTofu"/> path — see
/// tests/Korat.Auth.Tests/SpaceMcp/SessionAdmissionCharacterizationTests.cs for the branch-by-branch
/// proof, and <c>ConnectAccessRequestTests</c> for the end-to-end gRPC-path regression gate.
/// </summary>
public interface ISessionAdmission
{
    Task<AdmissionResult> AdmitAsync(McpServerId serverId, ConsumerPrincipal principal, CancellationToken cancellationToken);
}
