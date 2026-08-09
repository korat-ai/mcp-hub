using Korat.Cloud.IntegrationTests;
using Korat.Domain;
using Korat.GrainInterfaces;

namespace Korat.Cloud.ContractTests;

public sealed class PersistenceContractTests(KoratIntegrationFixture fixture) : IClassFixture<KoratIntegrationFixture>
{
    [Fact]
    public async Task GrantList_ReflectsPersistedStateAfterGrainRecycle()
    {
        var space = fixture.ClusterClient.GetGrain<ISpaceGrain>(fixture.LegacyOwnerSpaceId);
        var nodeId = NodeId.New();
        var server = await space.PublishMcpServerAsync(nodeId, "contract-grant", "echo", "ok");
        var request = await space.CreateAccessRequestAsync(ConsumerId.New(), server.Id, nodeId);
        var grant = await space.ApproveAccessRequestAsync(request.Id, KoratIntegrationFixture.DevSpaceOwnerUserId);

        await fixture.RecycleSilosAsync();

        var grants = await fixture.ClusterClient.GetGrain<ISpaceGrain>(fixture.LegacyOwnerSpaceId).ListGrantsAsync();
        var persisted = grants.Single(g => g.Id == grant.Id);
        Assert.Equal(GrantStatus.Active, persisted.Status);
    }
}
