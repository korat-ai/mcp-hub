using Korat.Cloud.Web.Auth.Services;

namespace Korat.Auth.Tests;

// Note: DeviceCodeStoreTests use the InMemoryDeviceCodeStore (a test double).
// The Orleans-backed GrainDeviceCodeStore wiring (grain lifecycle, cluster membership,
// TTL deactivation) is covered by the integration tests in Task 5 (CliDeviceFlowEndpointsTests).
public class DeviceCodeStoreTests
{
    private static IDeviceCodeStore Build() => new InMemoryDeviceCodeStore();
    private static (IDeviceCodeStore store, FakeTimeProvider time) BuildWithClock(DateTimeOffset? start = null)
    {
        var time = new FakeTimeProvider(start ?? DateTimeOffset.UtcNow);
        return (new InMemoryDeviceCodeStore(time), time);
    }

    [Fact]
    public async Task CreateAsync_returns_pending_entry_with_user_and_device_codes()
    {
        var store = Build();
        var entry = await store.CreateAsync(TimeSpan.FromMinutes(10), default);

        Assert.NotNull(entry.DeviceCode);
        Assert.NotNull(entry.UserCode);
        Assert.NotEmpty(entry.DeviceCode);
        Assert.NotEmpty(entry.UserCode);
        Assert.Equal(DeviceCodeStatus.Pending, entry.Status);
        Assert.Null(entry.UserId);
    }

    [Fact]
    public async Task ApproveAsync_then_GetStatus_returns_approved_with_userId()
    {
        var store = Build();
        var userId = Guid.NewGuid();
        var entry = await store.CreateAsync(TimeSpan.FromMinutes(10), default);

        var approved = await store.ApproveAsync(entry.UserCode, userId, default);
        Assert.True(approved);

        var status = await store.GetStatusAsync(entry.DeviceCode, default);
        Assert.NotNull(status);
        Assert.Equal(DeviceCodeStatus.Approved, status!.Status);
        Assert.Equal(userId, status.UserId);
    }

    [Fact]
    public async Task ApproveAsync_unknown_user_code_returns_false()
    {
        var store = Build();
        var result = await store.ApproveAsync("BADCODE1", Guid.NewGuid(), default);
        Assert.False(result);
    }

    [Fact]
    public async Task DenyAsync_unknown_user_code_returns_false()
    {
        var store = Build();
        var result = await store.DenyAsync("BADCODE1", default);
        Assert.False(result);
    }

    [Fact]
    public async Task GetStatusAsync_unknown_device_code_returns_null_before_create()
    {
        var store = Build();
        var result = await store.GetStatusAsync("dev-unknown-standalone", default);
        Assert.Null(result);
    }

    [Fact]
    public async Task GetStatusAsync_pending_entry_returns_pending()
    {
        var store = Build();
        var entry = await store.CreateAsync(TimeSpan.FromMinutes(10), default);

        var status = await store.GetStatusAsync(entry.DeviceCode, default);
        Assert.NotNull(status);
        Assert.Equal(DeviceCodeStatus.Pending, status!.Status);
    }

    [Fact]
    public async Task DenyAsync_then_GetStatus_returns_denied()
    {
        var store = Build();
        var entry = await store.CreateAsync(TimeSpan.FromMinutes(10), default);

        var denied = await store.DenyAsync(entry.UserCode, default);
        Assert.True(denied);

        var status = await store.GetStatusAsync(entry.DeviceCode, default);
        Assert.NotNull(status);
        Assert.Equal(DeviceCodeStatus.Denied, status!.Status);
    }

    // ── GetStatusAsync + MarkConsumedAsync (safe consume ordering) ────────────

    [Fact]
    public async Task GetStatusAsync_returns_approved_without_burning_state()
    {
        var store = Build();
        var userId = Guid.NewGuid();
        var entry = await store.CreateAsync(TimeSpan.FromMinutes(10), default);
        await store.ApproveAsync(entry.UserCode, userId, default);

        // Non-destructive peek — must NOT burn Approved → Expired.
        var peeked = await store.GetStatusAsync(entry.DeviceCode, default);
        Assert.NotNull(peeked);
        Assert.Equal(DeviceCodeStatus.Approved, peeked!.Status);

        // Second peek must still be Approved (state not consumed).
        var peeked2 = await store.GetStatusAsync(entry.DeviceCode, default);
        Assert.Equal(DeviceCodeStatus.Approved, peeked2!.Status);
    }

    [Fact]
    public async Task MarkConsumedAsync_burns_approved_to_expired()
    {
        var store = Build();
        var userId = Guid.NewGuid();
        var entry = await store.CreateAsync(TimeSpan.FromMinutes(10), default);
        await store.ApproveAsync(entry.UserCode, userId, default);

        await store.MarkConsumedAsync(entry.DeviceCode, default);

        // After MarkConsumedAsync the entry must no longer be Approved.
        var after = await store.GetStatusAsync(entry.DeviceCode, default);
        Assert.NotNull(after);
        Assert.NotEqual(DeviceCodeStatus.Approved, after!.Status);
    }

    [Fact]
    public async Task GetStatusAsync_unknown_device_code_returns_null()
    {
        var store = Build();
        var result = await store.GetStatusAsync("dev-unknown", default);
        Assert.Null(result);
    }

    [Fact]
    public async Task MarkConsumedAsync_on_pending_is_noop()
    {
        // Ensures MarkConsumedAsync does not mutate non-Approved entries.
        var store = Build();
        var entry = await store.CreateAsync(TimeSpan.FromMinutes(10), default);

        await store.MarkConsumedAsync(entry.DeviceCode, default);

        var after = await store.GetStatusAsync(entry.DeviceCode, default);
        Assert.Equal(DeviceCodeStatus.Pending, after!.Status);
    }

    // ── user_code single-use (post-consume) ──────────────────────────────────

    [Fact]
    public async Task MarkConsumedAsync_clears_user_code_mapping_so_approve_returns_false()
    {
        // After a handshake is consumed, the same user_code must not be resolvable again —
        // prevents a second approve attempt on the same code from succeeding.
        var store = Build();
        var userId = Guid.NewGuid();
        var entry = await store.CreateAsync(TimeSpan.FromMinutes(10), default);
        await store.ApproveAsync(entry.UserCode, userId, default);
        await store.MarkConsumedAsync(entry.DeviceCode, default);

        // Attempting to approve again with the same user_code must now return false.
        var result = await store.ApproveAsync(entry.UserCode, userId, default);
        Assert.False(result);
    }

    [Fact]
    public async Task DenyAsync_clears_user_code_mapping_so_subsequent_approve_returns_false()
    {
        var store = Build();
        var entry = await store.CreateAsync(TimeSpan.FromMinutes(10), default);
        await store.DenyAsync(entry.UserCode, default);

        // After deny, user_code must not be usable for a second approval attempt.
        var result = await store.ApproveAsync(entry.UserCode, Guid.NewGuid(), default);
        Assert.False(result);
    }

    // ── TTL expiry via clock advance (cov C1) ─────────────────────────────────

    [Fact]
    public async Task GetStatusAsync_PastDeadline_PendingEntry_ReturnsExpired()
    {
        // Arrange: create a code with a short TTL, do NOT approve it, advance past deadline.
        var (store, clock) = BuildWithClock();
        var entry = await store.CreateAsync(TimeSpan.FromMinutes(5), default);

        clock.Advance(TimeSpan.FromMinutes(5) + TimeSpan.FromSeconds(1));

        // The Pending entry is past its TTL — must be reported as Expired.
        var status = await store.GetStatusAsync(entry.DeviceCode, default);
        Assert.NotNull(status);
        Assert.Equal(DeviceCodeStatus.Expired, status!.Status);
    }

    [Fact]
    public async Task GetStatusAsync_PastDeadline_ApprovedEntry_ReturnsExpired()
    {
        // Cov C1: an Approved-but-unconsumed entry must also expire once the deadline passes.
        // Mirrors DeviceCodeGrain.ExpireIfNeeded which covers both Pending and Approved.
        var (store, clock) = BuildWithClock();
        var userId = Guid.NewGuid();
        var entry = await store.CreateAsync(TimeSpan.FromMinutes(10), default);

        // Approve (while still within TTL).
        var approved = await store.ApproveAsync(entry.UserCode, userId, default);
        Assert.True(approved, "Approve should succeed before TTL expires.");

        // Advance clock past the TTL.
        clock.Advance(TimeSpan.FromMinutes(10) + TimeSpan.FromSeconds(1));

        // The Approved entry is now past deadline — must be reported as Expired.
        var status = await store.GetStatusAsync(entry.DeviceCode, default);
        Assert.NotNull(status);
        Assert.Equal(DeviceCodeStatus.Expired, status!.Status);
    }

    [Fact]
    public async Task GetStatusAsync_PastDeadline_ApprovedEntry_IsNotConsumable()
    {
        // Cov C1: an approved-but-unconsumed code past deadline must NOT be consumable.
        // This guards the contract "CLI cannot redeem a token after expires_in seconds".
        var (store, clock) = BuildWithClock();
        var userId = Guid.NewGuid();
        var entry = await store.CreateAsync(TimeSpan.FromMinutes(10), default);

        await store.ApproveAsync(entry.UserCode, userId, default);

        // Advance past TTL.
        clock.Advance(TimeSpan.FromMinutes(10) + TimeSpan.FromSeconds(1));

        // GetStatus on an expired code must NOT return Approved.
        var status = await store.GetStatusAsync(entry.DeviceCode, default);
        Assert.NotNull(status);
        Assert.NotEqual(DeviceCodeStatus.Approved, status!.Status);
    }

    // ── NormalizeUserCode (Crockford folding) ─────────────────────────────────

    [Theory]
    [InlineData("A3KM7RNP", "A3KM7RNP")]   // no ambiguous chars → unchanged
    [InlineData("a3km7rnp", "A3KM7RNP")]   // lowercase → uppercased
    [InlineData("O0OO0000", "00000000")]   // O → 0
    [InlineData("IILL1100", "11111100")]   // I → 1, L → 1
    [InlineData(" A3KM ", "A3KM")]          // whitespace trimmed
    [InlineData("o0il1", "00111")]          // mixed lowercase ambiguous
    public void NormalizeUserCode_applies_Crockford_folding(string input, string expected)
    {
        Assert.Equal(expected, IDeviceCodeStore.NormalizeUserCode(input));
    }
}
