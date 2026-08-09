using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Korat.Cloud.Mcp.Space;
using Korat.Cloud.Web.Auth.Services;
using Korat.Domain;
using Microsoft.Extensions.DependencyInjection;

namespace Korat.Cloud.IntegrationTests.SpaceMcpOAuth;

/// <summary>
/// Space-MCP inc-2a, Task 7: every initialized aggregator session registers itself in the
/// per-consumer registry grain, and BOTH teardown paths (DELETE → TerminateAsync; abandoned →
/// OnDeactivateAsync) unregister it — the index Task 8's consent-revoke fans out over (SF-6).
/// Driven over HTTP with the inc-1 scoped token (identity = Derive(tokenId, space)); the
/// registry is credential-agnostic — it keys on the derived ConsumerId either way.
///
/// Also covers <see cref="ISpaceMcpConsumerSessionsGrain.TerminateAllAsync"/> (the snapshot +
/// fan-out Task 8 will call on revoke) and the SF-1 plan-review correction: a registry
/// registration failure must abort <c>InitializeCoreAsync</c> WITHOUT leaving
/// <c>_initialized == true</c> + an unregistered live session, and a retry must re-attempt
/// registration cleanly (not short-circuit on the cached-result guard).
/// </summary>
[Trait("Category", "SpaceMcpOAuth")]
public sealed class SpaceMcpConsumerSessionsTests(KoratIntegrationFixture fixture) : IClassFixture<KoratIntegrationFixture>
{
    private const string InitializeBody = """
        {"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"t","version":"1"}}}
        """;

    private const string ClientInitializeJson = """
        {"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"test-client","version":"1.0"}}}
        """;

    [Fact]
    public async Task Initialize_Registers_Delete_Unregisters()
    {
        var seeded = await fixture.SeedUserAsync($"reg-{Guid.NewGuid():N}@example.com", "Reg Owner");
        // Р25: OAuth-derived identity — the CLI-token derivation is gone.
        var (scoped, identity) =
            await SpaceMcpOAuthTestAccess.IssueAsync(fixture, seeded.UserId, seeded.SpaceId);
        var registry = fixture.ClusterClient.GetGrain<ISpaceMcpConsumerSessionsGrain>(identity.Value);

        var http = fixture.Factory.CreateClient();
        var init = new HttpRequestMessage(HttpMethod.Post, $"/mcp/{seeded.SpaceId}")
        { Content = new StringContent(InitializeBody, Encoding.UTF8, "application/json") };
        init.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        init.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        init.Headers.Authorization = new AuthenticationHeaderValue("Bearer", scoped);
        var initResponse = await http.SendAsync(init);
        Assert.Equal(HttpStatusCode.OK, initResponse.StatusCode);
        var sessionId = initResponse.Headers.GetValues("Mcp-Session-Id").Single();

        Assert.Contains(sessionId, await registry.ListAsync());

        var delete = new HttpRequestMessage(HttpMethod.Delete, $"/mcp/{seeded.SpaceId}");
        delete.Headers.Authorization = new AuthenticationHeaderValue("Bearer", scoped);
        delete.Headers.Add("Mcp-Session-Id", sessionId);
        var deleteResponse = await http.SendAsync(delete);
        Assert.Equal(HttpStatusCode.NoContent, deleteResponse.StatusCode);

        Assert.DoesNotContain(sessionId, await registry.ListAsync());
    }

    /// <summary>
    /// The registry's own fan-out primitive (Task 8's consent-revoke entry point): two REAL
    /// aggregator sessions registered under the SAME identity both get torn down (asserted via
    /// <c>GetBindingAsync</c> flipping to null — the same "gone" signal the HTTP responder's own
    /// binding re-check relies on), the set is emptied, AND a "phantom" session id (registered
    /// directly, never actually driven through initialize) is skipped harmlessly — Orleans
    /// reactivates a fresh, uninitialized grain for it and TerminateAsync no-ops. A second
    /// TerminateAllAsync call on the now-empty set is itself idempotent (no exception, no-op).
    /// </summary>
    [Fact]
    public async Task TerminateAllAsync_TerminatesEachRealSession_SkipsAPhantom_ThenEmptiesTheSet()
    {
        var seeded = await fixture.SeedUserAsync($"regall-{Guid.NewGuid():N}@example.com", "RegAll Owner");
        var spaceId = new SpaceId(seeded.SpaceId);
        // Р25: /mcp/{space} accepts OAuth only — the bearer comes from the real
        // authorize→consent→code→token flow, not from a machine-wide CLI token.
        var (cliToken, consumerIdentity) =
            await SpaceMcpOAuthTestAccess.IssueAsync(fixture, seeded.UserId, seeded.SpaceId);
        var registry = fixture.ClusterClient.GetGrain<ISpaceMcpConsumerSessionsGrain>(consumerIdentity.Value);

        var ctx = new SpaceMcpSessionContext(consumerIdentity, spaceId, seeded.UserId);

        var sessionKeyA = $"test-session-{Guid.NewGuid():N}";
        var sessionKeyB = $"test-session-{Guid.NewGuid():N}";
        var grainA = fixture.ClusterClient.GetGrain<ISpaceMcpAggregatorGrain>(sessionKeyA);
        var grainB = fixture.ClusterClient.GetGrain<ISpaceMcpAggregatorGrain>(sessionKeyB);
        await grainA.InitializeAsync(ctx, ClientInitializeJson);
        await grainB.InitializeAsync(ctx, ClientInitializeJson);

        Assert.NotNull(await grainA.GetBindingAsync());
        Assert.NotNull(await grainB.GetBindingAsync());

        // A phantom entry — never driven through InitializeAsync, so its aggregator grain has no
        // live state at all (as if it had long since deactivated). Registered directly to prove
        // the fan-out treats an "already gone" session as a harmless no-op, not a failure that
        // aborts the rest of the fan-out.
        var phantomSessionKey = $"phantom-session-{Guid.NewGuid():N}";
        await registry.RegisterAsync(phantomSessionKey);

        var beforeTerminate = await registry.ListAsync();
        Assert.Contains(sessionKeyA, beforeTerminate);
        Assert.Contains(sessionKeyB, beforeTerminate);
        Assert.Contains(phantomSessionKey, beforeTerminate);

        await registry.TerminateAllAsync();

        Assert.Null(await grainA.GetBindingAsync());
        Assert.Null(await grainB.GetBindingAsync());
        Assert.Empty(await registry.ListAsync());

        // Idempotent: calling again on an already-empty set is a harmless no-op.
        await registry.TerminateAllAsync();
        Assert.Empty(await registry.ListAsync());
    }

    [Fact]
    public async Task UnregisterAsync_RemovesOnlyTheGivenSession()
    {
        var seeded = await fixture.SeedUserAsync($"unreg-{Guid.NewGuid():N}@example.com", "Unreg Owner");
        // Р25: OAuth-derived identity — the CLI-token derivation is gone.
        var (cliToken, identity) =
            await SpaceMcpOAuthTestAccess.IssueAsync(fixture, seeded.UserId, seeded.SpaceId);
        var registry = fixture.ClusterClient.GetGrain<ISpaceMcpConsumerSessionsGrain>(identity.Value);

        var sessionA = $"session-a-{Guid.NewGuid():N}";
        var sessionB = $"session-b-{Guid.NewGuid():N}";
        await registry.RegisterAsync(sessionA);
        await registry.RegisterAsync(sessionB);

        await registry.UnregisterAsync(sessionA);

        var remaining = await registry.ListAsync();
        Assert.DoesNotContain(sessionA, remaining);
        Assert.Contains(sessionB, remaining);
    }

    /// <summary>
    /// SF-1 (plan-review correction): the registry is registered BEFORE the aggregator flips
    /// <c>_initialized = true</c> — a registration failure must therefore abort init cleanly
    /// (never leaving a live, delivery-leg-registered-but-unindexed session), and a later retry
    /// on the SAME grain activation must re-attempt registration rather than short-circuit on
    /// the <c>if (_initialized) return cached</c> guard. Proven end-to-end: first call (armed)
    /// throws and leaves the identity absent from the registry; second call (disarmed) on the
    /// SAME grain reference succeeds and the identity is now present.
    /// </summary>
    [Fact]
    public async Task RegisterAsync_Failure_AbortsInitCleanly_AndRetryReRegisters()
    {
        var seeded = await fixture.SeedUserAsync($"sf1-{Guid.NewGuid():N}@example.com", "SF1 Owner");
        var spaceId = new SpaceId(seeded.SpaceId);
        // Any cagg_ identity that is not a live session's: this test is about register failure,
        // not about which derivation produced the id.
        var consumerIdentity = SpaceMcpConsumerIdentity.DeriveOAuth(
            $"probe-{Guid.NewGuid():N}", seeded.UserId, spaceId);
        var registry = fixture.ClusterClient.GetGrain<ISpaceMcpConsumerSessionsGrain>(consumerIdentity.Value);

        var sessionKey = $"test-session-{Guid.NewGuid():N}";
        var grain = fixture.ClusterClient.GetGrain<ISpaceMcpAggregatorGrain>(sessionKey);
        var ctx = new SpaceMcpSessionContext(consumerIdentity, spaceId, seeded.UserId);

        SpaceMcpConsumerSessionsFaultInjector.Arm(consumerIdentity.Value);
        try
        {
            await Assert.ThrowsAsync<KoratDomainException>(
                () => grain.InitializeAsync(ctx, ClientInitializeJson));
        }
        finally
        {
            SpaceMcpConsumerSessionsFaultInjector.Disarm(consumerIdentity.Value);
        }

        // Fail-closed: the failed attempt never made it into the registry.
        Assert.DoesNotContain(sessionKey, await registry.ListAsync());

        // Clean retry on the SAME grain activation: proves _initialized was never left true —
        // if it had been, this would short-circuit on the cached-result guard and never
        // re-attempt registration, permanently hiding the session from revocation.
        var retryResult = await grain.InitializeAsync(ctx, ClientInitializeJson);
        Assert.Contains("protocolVersion", retryResult);
        Assert.Contains(sessionKey, await registry.ListAsync());
    }

    private async Task<Guid> GetTokenIdAsync(string rawToken)
    {
        using var scope = fixture.Factory.Services.CreateScope();
        var cliTokens = scope.ServiceProvider.GetRequiredService<ICliTokenService>();
        var id = await cliTokens.GetTokenIdAsync(rawToken, default);
        Assert.NotNull(id);
        return id!.Value;
    }
}
