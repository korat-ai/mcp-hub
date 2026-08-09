using Korat.Domain;
using Korat.GrainInterfaces;

namespace Korat.Cloud.IntegrationTests.Push;

/// <summary>
/// Integration tests verifying that <see cref="INodeGrain.ConnectAsync"/> and
/// <see cref="INodeGrain.MarkOnlineForTestingAsync"/> preserve push-token fields
/// set via <see cref="INodeGrain.RegisterPushTokenAsync"/>.
///
/// Before the fix, both methods rebuilt <c>_state</c> from scratch, dropping
/// <c>PushToken</c> / <c>PushPlatform</c> / <c>PushTokenUpdatedAt</c> — so a
/// reconnect silently cleared wake capability until the next RegisterPushToken
/// message arrived from the app.
///
/// IMPORTANT: <see cref="INodeGrain.RegisterPushTokenAsync"/> early-returns when
/// <c>_state.Id == default</c> (grain not yet connected). All tests therefore call
/// <see cref="INodeGrain.ConnectAsync"/> first to initialise the grain, then register
/// the push token, then reconnect to assert preservation. This matches the real app
/// lifecycle: connect → receive APNs token → RegisterPushToken → disconnect/reconnect.
/// </summary>
public sealed class NodeGrainPushTokenTests(KoratIntegrationFixture fixture)
    : IClassFixture<KoratIntegrationFixture>
{
    // ── ConnectAsync preserves push token ──────────────────────────────────────

    [Fact]
    public async Task ConnectAsync_After_RegisterPushToken_Preserves_Token()
    {
        // Arrange — connect first so _state.Id is initialised (RegisterPushTokenAsync
        // early-returns when _state.Id == default on a fresh grain).
        var nodeId = NodeId.New().Value;
        var spaceId = new SpaceId(fixture.LegacyOwnerSpaceId);
        var gatewayId = GatewayId.New();
        var grain = fixture.ClusterClient.GetGrain<INodeGrain>(nodeId);

        await grain.ConnectAsync(spaceId, "Test iPhone", gatewayId);

        // Register a push token now that the grain is alive.
        await grain.RegisterPushTokenAsync("aabbccdd0011223344556677", "apns_sandbox");

        // Confirm it was stored.
        var afterRegister = await grain.GetAsync();
        Assert.Equal("aabbccdd0011223344556677", afterRegister.PushToken);

        // Act — call ConnectAsync again (simulates the app reconnecting after backgrounding).
        var afterReconnect = await grain.ConnectAsync(spaceId, "Test iPhone", gatewayId);

        // Assert — push token must still be present; ConnectAsync must not wipe it.
        Assert.Equal("aabbccdd0011223344556677", afterReconnect.PushToken);  // must preserve PushToken
        Assert.Equal("apns_sandbox", afterReconnect.PushPlatform);           // must preserve PushPlatform
        Assert.NotNull(afterReconnect.PushTokenUpdatedAt);
    }

    [Fact]
    public async Task ConnectAsync_Without_PriorToken_HasNullPushToken()
    {
        // Arrange — fresh grain, no prior RegisterPushToken.
        var nodeId = NodeId.New().Value;
        var spaceId = new SpaceId(fixture.LegacyOwnerSpaceId);
        var gatewayId = GatewayId.New();
        var grain = fixture.ClusterClient.GetGrain<INodeGrain>(nodeId);

        // Act
        var result = await grain.ConnectAsync(spaceId, "Fresh iPhone", gatewayId);

        // Assert — no push token on a brand-new node.
        Assert.Null(result.PushToken);
        Assert.Null(result.PushPlatform);
    }

    [Fact]
    public async Task ConnectAsync_After_RegisterPushToken_Then_ClearToken_HasNullPushToken()
    {
        // Arrange — connect first, then register, then clear.
        var nodeId = NodeId.New().Value;
        var spaceId = new SpaceId(fixture.LegacyOwnerSpaceId);
        var gatewayId = GatewayId.New();
        var grain = fixture.ClusterClient.GetGrain<INodeGrain>(nodeId);

        await grain.ConnectAsync(spaceId, "Cleared iPhone", gatewayId);
        await grain.RegisterPushTokenAsync("tokenToBeCleared", "apns");
        // Simulate APNs 410 clearing the token.
        await grain.RegisterPushTokenAsync("", "");

        // Act — ConnectAsync after token was cleared.
        var result = await grain.ConnectAsync(spaceId, "Cleared iPhone", gatewayId);

        // Assert — cleared token is preserved as null (not re-populated from a stale state).
        Assert.Null(result.PushToken);
    }

    // ── MarkOnlineForTestingAsync preserves push token ─────────────────────────

    [Fact]
    public async Task MarkOnlineForTestingAsync_After_RegisterPushToken_Preserves_Token()
    {
        // Arrange — connect first so the grain is initialised, then register a token.
        var nodeId = NodeId.New().Value;
        var spaceId = new SpaceId(fixture.LegacyOwnerSpaceId);
        var gatewayId = GatewayId.New();
        var grain = fixture.ClusterClient.GetGrain<INodeGrain>(nodeId);

        await grain.ConnectAsync(spaceId, "Test Device", gatewayId);
        await grain.RegisterPushTokenAsync("deadbeef00000000", "apns");

        // Act — MarkOnlineForTestingAsync (simulates the test-helper reconnect path).
        var result = await grain.MarkOnlineForTestingAsync(spaceId, "Test Device");

        // Assert — push token must survive MarkOnlineForTestingAsync.
        Assert.Equal("deadbeef00000000", result.PushToken);  // must preserve PushToken
        Assert.Equal("apns", result.PushPlatform);
    }

    // ── ClearPushTokenIfMatchesAsync (compare-and-clear) ───────────────────────

    [Fact]
    public async Task ClearPushTokenIfMatchesAsync_Clears_When_Token_Matches()
    {
        var nodeId = NodeId.New().Value;
        var spaceId = new SpaceId(fixture.LegacyOwnerSpaceId);
        var gatewayId = GatewayId.New();
        var grain = fixture.ClusterClient.GetGrain<INodeGrain>(nodeId);

        await grain.ConnectAsync(spaceId, "Compare-Clear iPhone", gatewayId);
        await grain.RegisterPushTokenAsync("matchingtoken0000", "apns");

        await grain.ClearPushTokenIfMatchesAsync("matchingtoken0000");

        var result = await grain.GetAsync();
        Assert.Null(result.PushToken);
        Assert.Null(result.PushPlatform);
    }

    [Fact]
    public async Task ClearPushTokenIfMatchesAsync_NoOp_When_Token_Already_Rotated()
    {
        var nodeId = NodeId.New().Value;
        var spaceId = new SpaceId(fixture.LegacyOwnerSpaceId);
        var gatewayId = GatewayId.New();
        var grain = fixture.ClusterClient.GetGrain<INodeGrain>(nodeId);

        await grain.ConnectAsync(spaceId, "Rotated iPhone", gatewayId);
        await grain.RegisterPushTokenAsync("oldDeadToken00000", "apns");
        // Simulate the app re-registering a FRESH token before the compare-and-clear call lands.
        await grain.RegisterPushTokenAsync("newLiveToken00000", "apns");

        await grain.ClearPushTokenIfMatchesAsync("oldDeadToken00000"); // stale — must NOT clobber

        var result = await grain.GetAsync();
        Assert.Equal("newLiveToken00000", result.PushToken); // fresh token survives
    }

    [Fact]
    public async Task ClearPushTokenIfMatchesAsync_NoOp_On_NeverPersistedNode()
    {
        var nodeId = NodeId.New().Value;
        var grain = fixture.ClusterClient.GetGrain<INodeGrain>(nodeId); // never ConnectAsync'd

        // Must not throw even though the grain was never persisted.
        await grain.ClearPushTokenIfMatchesAsync("anything");
    }
}
