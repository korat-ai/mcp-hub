using Korat.Domain;
using Korat.Domain.Entities;
using Korat.Domain.Persistence;
using Korat.GrainInterfaces;

namespace Korat.Cloud.Mcp.Space;

/// <summary>
/// Space-MCP (increment 1, Task 4): one Published MCP server in the aggregator's Space, tagged
/// with whether <paramref name="Granted"/> — the consumer identity presented to
/// <see cref="DiscoverAsync"/> currently holds an active grant for it.
/// </summary>
public sealed record BackendServer(McpServerId Id, string DisplayName, bool IsAsserted, bool Granted);

/// <summary>
/// Server-side analog of <c>apps/Korat.Cli/Mcp/Aggregation/SpaceDiscovery.cs</c> — same intent
/// (snapshot the Space's Published MCP servers, tagged granted/ungranted for this consumer
/// identity) but with NO REST hop: the CLI version calls <c>GET api/space</c> + <c>GET
/// api/grants</c> over HTTP because it runs outside the cloud process; this grain-side port reads
/// straight from <see cref="ISpaceGrain.ListMcpServersAsync"/> (the grains-are-the-cache canonical
/// membership list) and <see cref="IMetadataRepository.GetActiveGrantAsync"/> (the same targeted
/// per-(space, agent, server) grant query <c>SessionAdmission.AdmitAsync</c> itself uses — Task
/// 2's shared admission gauntlet — so "granted" here always means exactly what admission would
/// decide, not a stale or differently-computed snapshot).
/// </summary>
public static class SpaceServerDiscovery
{
    public static async Task<IReadOnlyList<BackendServer>> DiscoverAsync(
        IClusterClient clusterClient,
        IMetadataRepository repository,
        SpaceId spaceId,
        ConsumerId consumerIdentity,
        CancellationToken ct)
    {
        var servers = await clusterClient.GetGrain<ISpaceGrain>(spaceId.Value).ListMcpServersAsync();

        var result = new List<BackendServer>(servers.Count);
        foreach (var s in servers)
        {
            // Mirrors SpaceDiscovery.cs's own filter: only Published servers are ever surfaced
            // to a consumer (Disabled/NeedsReauth servers are invisible to the catalog, same as
            // the CLI aggregator and the console's own server list).
            if (s.Status != McpServerStatus.Published) continue;

            var grant = await repository.GetActiveGrantAsync(spaceId, consumerIdentity, s.Id, ct);
            result.Add(new BackendServer(s.Id, s.DisplayName, s.IsAsserted, Granted: grant is not null));
        }
        return result;
    }
}
