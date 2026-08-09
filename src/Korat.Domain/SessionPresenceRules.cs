using Korat.Domain.Entities;

namespace Korat.Domain;

/// <summary>
/// Defines which relay-session participants require a live Node record.
/// Cloud-terminated HTTP MCP servers and the in-process Space-MCP consumer do not have
/// corresponding publisher/client nodes, so their intentionally-empty/sentinel ids must not
/// make an otherwise-live session appear stale in owner-facing status views.
/// </summary>
public static class SessionPresenceRules
{
    /// <summary>
    /// Returns whether every participant that is backed by a real relay node is currently online.
    /// A missing server is treated conservatively as requiring a publisher node.
    /// </summary>
    public static bool RequiredParticipantsAreOnline(
        RelaySession session,
        McpServer? server,
        Node? clientNode,
        Node? publisherNode)
    {
        var clientOnline = session.ClientNodeId.Value == WellKnownNodeIds.AggregatorSentinelNodeId
            || IsOnline(clientNode);

        var publisherOnline = server is not null
            && (McpServerTransports.IsHttpCloud(server.Transport) || IsOnline(publisherNode));

        return clientOnline && publisherOnline;
    }

    private static bool IsOnline(Node? node) =>
        node is not null && NodePresenceRules.EffectiveStatus(node) == NodeStatus.Online;
}
