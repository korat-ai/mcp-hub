using Korat.Domain.Entities;
using Korat.GrainInterfaces;
using Orleans;

namespace Korat.Cloud.Push;

/// <summary>
/// Test seam for <see cref="AccessRequestNotifier"/> — narrows the grain/helper surface it needs
/// down to exactly the operations it calls, mirroring how <see cref="INodeGrainLocator"/> narrows
/// NodeWakeCoordinator's surface to GetNodeGrain(nodeId). Keeping the surface narrow (rather than
/// exposing the full ISpaceGrain) means unit tests supply plain in-memory fakes instead of a
/// 30+-method ISpaceGrain stub.
/// </summary>
public interface IAccessRequestGrainLocator
{
    /// <summary>The owner Space's nodes (mirrors ISpaceGrain.ListNodesAsync — live per-node state).</summary>
    Task<IReadOnlyList<Node>> ListNodesAsync(string spaceId);

    /// <summary>The owner Space's published MCP servers (mirrors ISpaceGrain.ListMcpServersAsync).</summary>
    Task<IReadOnlyList<McpServer>> ListMcpServersAsync(string spaceId);

    /// <summary>Resolves one node's grain for the compare-and-clear call on a dead token.</summary>
    INodeGrain GetNodeGrain(string nodeId);

    /// <summary>Resolves agentClientIds to friendly display names (delegates to FriendlyNameHelpers in production).</summary>
    Task<Dictionary<string, string>> ResolveAgentNamesAsync(
        IEnumerable<string> agentClientIds, Dictionary<string, string> nodeNames, CancellationToken ct);
}

/// <summary>
/// Production adapter: resolves grains via the Orleans cluster client. Mirrors
/// <see cref="ClusterNodeGrainLocator"/>.
/// </summary>
public sealed class ClusterAccessRequestGrainLocator(IClusterClient cluster) : IAccessRequestGrainLocator
{
    public Task<IReadOnlyList<Node>> ListNodesAsync(string spaceId) =>
        cluster.GetGrain<ISpaceGrain>(spaceId).ListNodesAsync();

    public Task<IReadOnlyList<McpServer>> ListMcpServersAsync(string spaceId) =>
        cluster.GetGrain<ISpaceGrain>(spaceId).ListMcpServersAsync();

    public INodeGrain GetNodeGrain(string nodeId) => cluster.GetGrain<INodeGrain>(nodeId);

    public Task<Dictionary<string, string>> ResolveAgentNamesAsync(
        IEnumerable<string> agentClientIds, Dictionary<string, string> nodeNames, CancellationToken ct)
        => Korat.Cloud.Web.FriendlyNameHelpers.ResolveAgentNamesAsync(agentClientIds, cluster, nodeNames, ct);
}
