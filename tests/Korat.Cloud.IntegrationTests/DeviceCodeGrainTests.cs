using Korat.GrainInterfaces;
using Microsoft.Extensions.DependencyInjection;

namespace Korat.Cloud.IntegrationTests;

/// <summary>
/// In-process Orleans TestCluster tests for DeviceCodeGrain and DeviceCodeRegistryGrain.
/// Uses the KoratIntegrationFixture which sets up a TestCluster with InMemory EF + real grains.
/// </summary>
public sealed class DeviceCodeGrainTests(KoratIntegrationFixture fixture)
    : IClassFixture<KoratIntegrationFixture>
{
    private static readonly TimeSpan ShortTtl = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan LongTtl = TimeSpan.FromHours(1);

    // ── DeviceCodeRegistryGrain ────────────────────────────────────────────────

    [Fact]
    public async Task Registry_RegisterAsync_ReturnsFalse_WhenNonExpiredEntryExists()
    {
        // Arrange: register a live entry.
        var registry = fixture.ClusterClient.GetGrain<IDeviceCodeRegistryGrain>("global");
        var userCode = $"uc-{Guid.NewGuid():N}";
        var deviceCode1 = Guid.NewGuid().ToString("N");
        var registered = await registry.RegisterAsync(userCode, deviceCode1, LongTtl);
        Assert.True(registered, "First registration must succeed.");

        // Act: attempt a second registration for the same user_code before TTL elapses.
        var deviceCode2 = Guid.NewGuid().ToString("N");
        var registered2 = await registry.RegisterAsync(userCode, deviceCode2, LongTtl);

        // Assert: must be refused — would redirect victim's approval to attacker's device_code.
        Assert.False(registered2, "Registration must fail when a live entry already exists.");

        // The original mapping must still resolve.
        var resolved = await registry.ResolveAsync(userCode);
        Assert.Equal(deviceCode1, resolved);
    }

    [Fact]
    public async Task Registry_RegisterAsync_Succeeds_AfterTtlElapsed()
    {
        // Arrange: register with an already-elapsed TTL (negative duration).
        var registry = fixture.ClusterClient.GetGrain<IDeviceCodeRegistryGrain>("global");
        var userCode = $"uc-expired-{Guid.NewGuid():N}";
        var deviceCode1 = Guid.NewGuid().ToString("N");

        // Negative TTL → instantly expired entry; the grain treats (now + negative) as in the past.
        // Use a tiny positive TTL and rely on the grain's sweep-on-register to clean it.
        // Strategy: register with 1-tick TTL so it expires immediately from the grain's perspective.
        var registeredFirst = await registry.RegisterAsync(userCode, deviceCode1, TimeSpan.FromTicks(1));
        Assert.True(registeredFirst, "First registration must succeed regardless of TTL.");

        // Verify that ResolveAsync returns null (expired).
        var resolvedExpired = await registry.ResolveAsync(userCode);
        Assert.Null(resolvedExpired);

        // Act: second registration for the same user_code — the expired entry must be swept.
        var deviceCode2 = Guid.NewGuid().ToString("N");
        var registeredSecond = await registry.RegisterAsync(userCode, deviceCode2, LongTtl);
        Assert.True(registeredSecond, "Registration must succeed after the previous entry expired.");

        var resolved = await registry.ResolveAsync(userCode);
        Assert.Equal(deviceCode2, resolved);
    }

    [Fact]
    public async Task Registry_ResolveAsync_ReturnsNull_AndEvicts_PastExpiry()
    {
        // Arrange: register a 1-tick TTL entry so it's expired before we query it.
        var registry = fixture.ClusterClient.GetGrain<IDeviceCodeRegistryGrain>("global");
        var userCode = $"uc-evict-{Guid.NewGuid():N}";
        var deviceCode = Guid.NewGuid().ToString("N");
        await registry.RegisterAsync(userCode, deviceCode, TimeSpan.FromTicks(1));

        // Act: resolve should find it expired, evict it, and return null.
        var resolved = await registry.ResolveAsync(userCode);
        Assert.Null(resolved);

        // Confirm eviction: a new registration for the same user_code must now succeed.
        var newDeviceCode = Guid.NewGuid().ToString("N");
        var registered = await registry.RegisterAsync(userCode, newDeviceCode, LongTtl);
        Assert.True(registered, "Evicted entry must allow re-registration.");
    }

    [Fact]
    public async Task Registry_RemoveAsync_MakesUserCodeUnresolvable()
    {
        // Arrange: register a live entry.
        var registry = fixture.ClusterClient.GetGrain<IDeviceCodeRegistryGrain>("global");
        var userCode = $"uc-remove-{Guid.NewGuid():N}";
        var deviceCode = Guid.NewGuid().ToString("N");
        await registry.RegisterAsync(userCode, deviceCode, LongTtl);

        // Confirm it resolves.
        var resolved = await registry.ResolveAsync(userCode);
        Assert.Equal(deviceCode, resolved);

        // Act: remove (single-use enforcement after consumption).
        await registry.RemoveAsync(userCode);

        // Assert: no longer resolvable.
        var resolvedAfter = await registry.ResolveAsync(userCode);
        Assert.Null(resolvedAfter);
    }

    // ── DeviceCodeGrain ────────────────────────────────────────────────────────

    [Fact]
    public async Task DeviceCode_Approve_ThenConsume_ReturnsApproved_ThenBurns()
    {
        // Arrange: create and initialize a new grain.
        var deviceCode = Guid.NewGuid().ToString("N");
        var userId = Guid.NewGuid();
        var grain = fixture.ClusterClient.GetGrain<IDeviceCodeGrain>(deviceCode);
        await grain.InitializeAsync("USER1234", LongTtl);

        // Act: approve.
        await grain.ApproveAsync(userId);

        // First consume: must return Approved with the correct UserId.
        var consumed1 = await grain.ConsumeAsync();
        Assert.Equal(DeviceCodeGrainStatus.Approved, consumed1.Status);
        Assert.Equal(userId, consumed1.UserId);

        // Second consume: must NOT return Approved (single-use guarantee).
        var consumed2 = await grain.ConsumeAsync();
        Assert.NotEqual(DeviceCodeGrainStatus.Approved, consumed2.Status);
        Assert.Equal(DeviceCodeGrainStatus.Expired, consumed2.Status);
    }

    [Fact]
    public async Task DeviceCode_Approved_PastDeadline_Expires()
    {
        // Arrange: approve with 1-tick TTL so the deadline is already in the past.
        var deviceCode = Guid.NewGuid().ToString("N");
        var userId = Guid.NewGuid();
        var grain = fixture.ClusterClient.GetGrain<IDeviceCodeGrain>(deviceCode);
        // Initialize with 1-tick TTL → deadline = UtcNow + 1 tick ≈ immediately expired.
        await grain.InitializeAsync("USER5678", TimeSpan.FromTicks(1));

        // Approve while still Pending (approval happens before expiry check on the next Get).
        await grain.ApproveAsync(userId);

        // Act: GetAsync triggers ExpireIfNeeded — even Approved state expires past the deadline.
        var state = await grain.GetAsync();

        // Assert: the Approved-unconsumed handshake must expire past the deadline.
        Assert.Equal(DeviceCodeGrainStatus.Expired, state.Status);
    }

    [Fact]
    public async Task DeviceCode_ApproveAndDeny_AreNoOp_OnceNonPending()
    {
        // Arrange: deny first.
        var deviceCode = Guid.NewGuid().ToString("N");
        var grain = fixture.ClusterClient.GetGrain<IDeviceCodeGrain>(deviceCode);
        await grain.InitializeAsync("NOOPTEST", LongTtl);
        await grain.DenyAsync();

        var stateAfterDeny = await grain.GetAsync();
        Assert.Equal(DeviceCodeGrainStatus.Denied, stateAfterDeny.Status);

        // Act: further approve must be a no-op (non-Pending state).
        await grain.ApproveAsync(Guid.NewGuid());
        var stateAfterApprove = await grain.GetAsync();
        // State must remain Denied — Approve is only applied when Pending.
        Assert.Equal(DeviceCodeGrainStatus.Denied, stateAfterApprove.Status);

        // Act: further deny must also be a no-op.
        await grain.DenyAsync();
        var stateAfterSecondDeny = await grain.GetAsync();
        Assert.Equal(DeviceCodeGrainStatus.Denied, stateAfterSecondDeny.Status);
    }

    [Fact]
    public async Task DeviceCode_Approve_IsNoOp_OnceApproved()
    {
        // Arrange: approve.
        var deviceCode = Guid.NewGuid().ToString("N");
        var userId1 = Guid.NewGuid();
        var userId2 = Guid.NewGuid();
        var grain = fixture.ClusterClient.GetGrain<IDeviceCodeGrain>(deviceCode);
        await grain.InitializeAsync("APVNOOPTEST", LongTtl);
        await grain.ApproveAsync(userId1);

        // Act: approve again with a different userId — must not overwrite.
        await grain.ApproveAsync(userId2);
        var state = await grain.GetAsync();

        // Assert: still Approved, still the first user.
        Assert.Equal(DeviceCodeGrainStatus.Approved, state.Status);
        Assert.Equal(userId1, state.UserId);
    }
}
