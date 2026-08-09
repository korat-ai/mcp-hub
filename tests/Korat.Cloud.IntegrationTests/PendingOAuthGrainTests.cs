using Korat.GrainInterfaces;
using Xunit;

namespace Korat.Cloud.IntegrationTests;

/// <summary>
/// Increment 2, Task 4: the pending-OAuth grain pair — burn-on-consume for IPendingOAuthGrain
/// (mirrors IDeviceCodeGrain.ConsumeAsync), and the superseding pointer for
/// IPendingOAuthPointerGrain (plan-time decision (a) — a NEW authorize/reconnect action
/// overwrites the pointer, so an older still-unconsumed state's callback must be rejected once
/// it no longer matches).
/// </summary>
public sealed class PendingOAuthGrainTests(KoratIntegrationFixture fixture) : IClassFixture<KoratIntegrationFixture>
{
    private static PendingOAuthState MakeState(string serverId, Guid ownerUserId) => new(
        serverId, ownerUserId, "space-1", "verifier-abc", "https://as.example.test",
        "https://as.example.test/authorize", "https://as.example.test/token", "client-1", "secret-1");

    [Fact]
    public async Task ConsumeAsync_ReturnsStateOnce_ThenNullOnReplay()
    {
        var state = $"state-{Guid.NewGuid():N}";
        var grain = fixture.ClusterClient.GetGrain<IPendingOAuthGrain>(state);
        await grain.InitializeAsync(MakeState("srv-1", Guid.NewGuid()), TimeSpan.FromMinutes(15));

        var first = await grain.ConsumeAsync();
        var second = await grain.ConsumeAsync();

        Assert.NotNull(first);
        Assert.Equal("srv-1", first!.ServerId);
        Assert.Null(second); // burned — single-use
    }

    [Fact]
    public async Task ConsumeAsync_NeverInitialized_ReturnsNull()
    {
        var grain = fixture.ClusterClient.GetGrain<IPendingOAuthGrain>($"state-never-{Guid.NewGuid():N}");
        Assert.Null(await grain.ConsumeAsync());
    }

    [Fact]
    public async Task PointerGrain_SetThenGet_ReturnsCurrentState()
    {
        var serverId = $"srv-ptr-{Guid.NewGuid():N}";
        var pointer = fixture.ClusterClient.GetGrain<IPendingOAuthPointerGrain>(serverId);
        await pointer.SetCurrentStateAsync("state-A", TimeSpan.FromMinutes(15));

        Assert.Equal("state-A", await pointer.GetCurrentStateAsync());
    }

    [Fact]
    public async Task PointerGrain_NewStateSupersedesOld()
    {
        var serverId = $"srv-ptr2-{Guid.NewGuid():N}";
        var pointer = fixture.ClusterClient.GetGrain<IPendingOAuthPointerGrain>(serverId);
        await pointer.SetCurrentStateAsync("state-old", TimeSpan.FromMinutes(15));

        await pointer.SetCurrentStateAsync("state-new", TimeSpan.FromMinutes(15));

        Assert.Equal("state-new", await pointer.GetCurrentStateAsync());
        Assert.NotEqual("state-old", await pointer.GetCurrentStateAsync());
    }

    [Fact]
    public async Task PeekAsync_ReturnsStateWithoutBurning_RepeatableThenStillConsumable()
    {
        // Blocker 2 fix (fable plan-review): peek must be non-consuming, and repeatable, and a
        // real ConsumeAsync afterwards must still succeed exactly once.
        var state = $"state-{Guid.NewGuid():N}";
        var grain = fixture.ClusterClient.GetGrain<IPendingOAuthGrain>(state);
        await grain.InitializeAsync(MakeState("srv-1", Guid.NewGuid()), TimeSpan.FromMinutes(15));

        var first = await grain.PeekAsync();
        var second = await grain.PeekAsync();
        var consumed = await grain.ConsumeAsync();
        var afterConsume = await grain.PeekAsync();

        Assert.NotNull(first);
        Assert.NotNull(second); // peek does not burn — repeatable
        Assert.NotNull(consumed); // still consumable after any number of peeks
        Assert.Null(afterConsume); // peek AFTER a real consume correctly reports burned
    }
}
