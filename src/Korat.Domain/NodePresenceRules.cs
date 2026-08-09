using Korat.Domain.Entities;

namespace Korat.Domain;

/// <summary>
/// Доменное правило liveness ноды: нода, помеченная <see cref="NodeStatus.Online"/>,
/// но не присылавшая heartbeat дольше <see cref="StaleThreshold"/>, считается offline.
/// </summary>
/// <remarks>
/// Живёт в Korat.Domain, чтобы любой слой (endpoint, grain, relay) применял
/// одну и ту же интерпретацию stored <c>node.Status</c> + <c>LastSeenAt</c> без cross-layer
/// зависимостей и без дублирования <see cref="StaleThreshold"/>.
/// </remarks>
public static class NodePresenceRules
{
    public static readonly TimeSpan StaleThreshold = TimeSpan.FromSeconds(90);

    public static NodeStatus EffectiveStatus(Node node)
    {
        if (node.Status != NodeStatus.Online)
            return NodeStatus.Offline;

        // Online without a successful hello/heartbeat timestamp is not evidence of a live
        // transport. Real ConnectAsync calls always stamp LastSeenAt, so null is a legacy or
        // synthetic row and must fail closed just like the CLI and SPA presence helpers.
        if (node.LastSeenAt is not { } lastSeen)
            return NodeStatus.Offline;

        return DateTimeOffset.UtcNow - lastSeen < StaleThreshold
            ? NodeStatus.Online
            : NodeStatus.Offline;
    }
}
