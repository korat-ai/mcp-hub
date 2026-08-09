using Korat.Domain;
using Korat.Domain.Entities;
using Korat.GrainInterfaces;

namespace Korat.Cloud.Gateways;

/// <summary>
/// 009-nats-relay-backplane: control-plane resolution of a session's topology. The relay
/// data plane (frame routing) NEVER trusts the wire <c>target_node_id</c>; it derives the
/// peer from the authoritative <see cref="ISessionGrain"/> state (cluster-wide via Orleans),
/// then transports bytes over NATS. This is the seam that keeps Orleans = control plane.
///
/// 022: <see cref="AgentConnectionId"/> carries the per-stream ConnectionId for the agent
/// side. It is persisted on the session (DB column, added by the session-teardown change)
/// so any silo can address the agent stream. On a route-cache miss the resolver reads it
/// from SessionGrain.GetAsync(), which is cluster-visible via Orleans single-activation.
/// </summary>
public readonly record struct SessionRouteInfo(
    NodeId Agent,
    NodeId Publisher,
    McpServerId McpServerId,
    SpaceId SpaceId,
    // 022: transport address of the agent bridge stream that opened this session.
    // Keyed by ConnectionId (not NodeId) so publisher→agent frames reach the exact
    // bridge process among N concurrent bridges for the same agent identity.
    ConnectionId AgentConnectionId = default,
    // Increment 1 (HTTP MCP direct-to-Space): true when this session's target server has
    // Transport == http_cloud. Publisher is NodeId.Empty for these sessions BY DESIGN (there is
    // no relay node) — ForwardFrameAsync must NOT treat that as "undeliverable" the way it would
    // for a genuinely broken stdio_node route. Defaulted false so every existing construction
    // site of this record (there is exactly one today, inside OrleansSessionRouteResolver) keeps
    // compiling unchanged until Task 5 teaches it to compute a real value.
    bool IsHttpCloud = false);

public interface ISessionRouteResolver
{
    /// <summary>Resolve a session to its participants + MCP/space context, or null if unknown.</summary>
    Task<SessionRouteInfo?> ResolveAsync(SessionId sessionId, CancellationToken cancellationToken);
}

/// <summary>Resolves session topology from the <see cref="ISessionGrain"/> (Orleans control plane).</summary>
public sealed class OrleansSessionRouteResolver(
    IClusterClient clusterClient,
    ILogger<OrleansSessionRouteResolver> logger) : ISessionRouteResolver
{
    public async Task<SessionRouteInfo?> ResolveAsync(SessionId sessionId, CancellationToken cancellationToken)
    {
        try
        {
            var session = await clusterClient.GetGrain<ISessionGrain>(sessionId.Value).GetAsync();

            // A never-opened / unknown session yields a default (empty) client node id — treat
            // as unresolved. Unlike ClientNodeId, an empty PublisherNodeId is NOT automatically
            // unresolved below — an http_cloud session has one by design (Crux Finding 4).
            if (string.IsNullOrEmpty(session.ClientNodeId.Value))
                return null;

            // Step-A: a Closed or Failed session must NOT be returned as a valid route — doing so
            // allows frames to be forwarded to a terminated session's participants after a revoke.
            // The local route cache is evicted by SessionTerminator.TerminateSessionAsync
            // (CloseSession call), but a cache-miss would reach this resolver. Returning null here
            // ensures ForwardFrameAsync rejects the frame on the control-plane fallback path too.
            if (session.Status is SessionStatus.Closed or SessionStatus.Failed)
                return null;

            // Finding 16, S2: only pay for the extra IMcpServerGrain.GetAsync() call in the ONE
            // case that is ever ambiguous — an empty PublisherNodeId. A NON-empty PublisherNodeId
            // can never belong to an http_cloud session (they always have PublisherNodeId == ""
            // by construction — see McpServerGrain.PublishHttpCloudAsync), so the common stdio
            // cross-silo cache-miss path (the overwhelming majority of resolves) is unaffected —
            // it never reaches this branch at all.
            var isHttpCloud = false;
            if (string.IsNullOrEmpty(session.PublisherNodeId.Value))
            {
                var server = await clusterClient.GetGrain<IMcpServerGrain>(session.McpServerId.Value).GetAsync();
                isHttpCloud = Korat.Domain.McpServerTransports.IsHttpCloud(server.Transport);

                // A stdio_node session with an empty PublisherNodeId is genuinely
                // unresolved/corrupt — preserve that rejection. An http_cloud session's empty
                // PublisherNodeId is normal.
                if (!isHttpCloud)
                    return null;
            }

            return new SessionRouteInfo(
                session.ClientNodeId,
                session.PublisherNodeId,
                session.McpServerId,
                session.SpaceId,
                // 022: AgentConnectionId is persisted on the session (DB column). GetAsync()
                // is cluster-visible via Orleans single-activation semantics, so the resolver
                // reads it even on a cross-silo cache miss.
                session.AgentConnectionId,
                isHttpCloud);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning("Session route resolve failed session={SessionId} errorType={ErrorType}", sessionId.Value, ex.GetType().Name);
            return null;
        }
    }
}
