using System.Net.Http.Json;
using Korat.Domain;
using Korat.GrainInterfaces;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Korat.Cloud.IntegrationTests;

/// <summary>
/// Increment 2, Task 4: the callback endpoint's fail-closed paths that a plain HTTP contract
/// test can't easily set up (they need direct grain access to seed/inspect pending-flow state).
/// The HAPPY-path full round trip (real discovery→DCR→authorize→callback→token) is Task 7's
/// end-to-end test; this file is deliberately about the REJECTION paths.
/// </summary>
public sealed class McpOAuthCallbackIntegrationTests(KoratIntegrationFixture fixture) : IClassFixture<KoratIntegrationFixture>
{
    [Fact]
    public async Task Callback_MixUp_ServerBCallbackWithServerAsState_IsRejected()
    {
        var seededA = await fixture.SeedUserAsync($"oauth-mixup-a-{Guid.NewGuid():N}@example.com", "Mixup A");
        var seededB = await fixture.SeedUserAsync($"oauth-mixup-b-{Guid.NewGuid():N}@example.com", "Mixup B");
        var spaceA = fixture.ClusterClient.GetGrain<ISpaceGrain>(seededA.SpaceId);
        var spaceB = fixture.ClusterClient.GetGrain<ISpaceGrain>(seededB.SpaceId);
        var serverA = await spaceA.CreateHttpMcpServerAsync($"srv-a-{Guid.NewGuid():N}", "https://mcp-a.example.test/", McpServerAuthModes.Oauth, null, null);
        var serverB = await spaceB.CreateHttpMcpServerAsync($"srv-b-{Guid.NewGuid():N}", "https://mcp-b.example.test/", McpServerAuthModes.Oauth, null, null);

        // A pending flow exists for server A, bound to owner A.
        var stateForA = $"state-{Guid.NewGuid():N}";
        var pendingA = new PendingOAuthState(
            serverA.Id.Value, seededA.UserId.Value, seededA.SpaceId, "verifier-a",
            "https://as.example.test", "https://as.example.test/authorize", "https://as.example.test/token", "client-a", null);
        await fixture.ClusterClient.GetGrain<IPendingOAuthGrain>(stateForA).InitializeAsync(pendingA, TimeSpan.FromMinutes(15));
        await fixture.ClusterClient.GetGrain<IPendingOAuthPointerGrain>(serverA.Id.Value).SetCurrentStateAsync(stateForA, TimeSpan.FromMinutes(15));

        // The mix-up VICTIM is the real owner (A) — an attacker's rogue AS 302-redirected owner
        // A's own browser to server B's callback path while replaying server A's state/code_challenge
        // (RFC 9700 §4.4's actual shape: the confusion is at the AS layer, not the caller's
        // identity) — so this request is authenticated as owner A, and the owner check passes;
        // the path-serverId mismatch is what must reject it.
        using var clientA = await CreateAuthenticatedClientNoRedirectAsync(seededA.UserId);
        var resp = await clientA.GetAsync($"/api/mcp/oauth/callback/{serverB.Id.Value}?code=whatever&state={stateForA}");

        Assert.Equal(System.Net.HttpStatusCode.Redirect, resp.StatusCode);
        Assert.Contains("reason=mismatch", resp.Headers.Location!.ToString());
        Assert.StartsWith("/app/servers/", resp.Headers.Location!.ToString()); // console SPA base, not bare /servers (404s on the deployed app)

        // Blocker 2 fix (fable plan-review): the mismatch check is now a PEEK, not a burn — a
        // REJECTED mix-up attempt must not consume owner A's still-pending, legitimate consent.
        // The previous draft of this test asserted the OPPOSITE (that the pending grain was
        // burned) — that was itself a symptom of the bug: the old handler order actually hit the
        // (path-keyed) supersession check FIRST, returned "superseded" without ever calling
        // ConsumeAsync, and the assertion below would have failed since the state was in fact NOT
        // consumed. Restated as a same-server, real-callback-flow assertion: a legitimate retry of
        // the REAL callback for server A, using this SAME (still-unconsumed) state, must be able
        // to actually consume it — proving the rejected mix-up attempt left it intact.
        var stillPending = await fixture.ClusterClient.GetGrain<IPendingOAuthGrain>(stateForA).ConsumeAsync();
        Assert.NotNull(stillPending);
        Assert.Equal(serverA.Id.Value, stillPending!.ServerId);
        Assert.Equal(seededA.UserId.Value, stillPending.OwnerUserId);
    }

    [Fact]
    public async Task Callback_WrongOwner_IsRejected()
    {
        var seededOwner = await fixture.SeedUserAsync($"oauth-owner-{Guid.NewGuid():N}@example.com", "Owner");
        var seededAttacker = await fixture.SeedUserAsync($"oauth-attacker-{Guid.NewGuid():N}@example.com", "Attacker");
        var space = fixture.ClusterClient.GetGrain<ISpaceGrain>(seededOwner.SpaceId);
        var server = await space.CreateHttpMcpServerAsync($"srv-owner-{Guid.NewGuid():N}", "https://mcp.example.test/", McpServerAuthModes.Oauth, null, null);

        var state = $"state-{Guid.NewGuid():N}";
        var pending = new PendingOAuthState(
            server.Id.Value, seededOwner.UserId.Value, seededOwner.SpaceId, "verifier-1",
            "https://as.example.test", "https://as.example.test/authorize", "https://as.example.test/token", "client-1", null);
        await fixture.ClusterClient.GetGrain<IPendingOAuthGrain>(state).InitializeAsync(pending, TimeSpan.FromMinutes(15));
        await fixture.ClusterClient.GetGrain<IPendingOAuthPointerGrain>(server.Id.Value).SetCurrentStateAsync(state, TimeSpan.FromMinutes(15));

        using var attackerClient = await CreateAuthenticatedClientNoRedirectAsync(seededAttacker.UserId);
        var resp = await attackerClient.GetAsync($"/api/mcp/oauth/callback/{server.Id.Value}?code=whatever&state={state}");

        Assert.Equal(System.Net.HttpStatusCode.Redirect, resp.StatusCode);
        Assert.Contains("reason=wrong_owner", resp.Headers.Location!.ToString());
    }

    [Fact]
    public async Task Callback_SupersededState_IsRejected()
    {
        var seeded = await fixture.SeedUserAsync($"oauth-superseded-{Guid.NewGuid():N}@example.com", "Superseded");
        var space = fixture.ClusterClient.GetGrain<ISpaceGrain>(seeded.SpaceId);
        var server = await space.CreateHttpMcpServerAsync($"srv-superseded-{Guid.NewGuid():N}", "https://mcp.example.test/", McpServerAuthModes.Oauth, null, null);

        var oldState = $"state-old-{Guid.NewGuid():N}";
        var oldPending = new PendingOAuthState(
            server.Id.Value, seeded.UserId.Value, seeded.SpaceId, "verifier-old",
            "https://as.example.test", "https://as.example.test/authorize", "https://as.example.test/token", "client-1", null);
        await fixture.ClusterClient.GetGrain<IPendingOAuthGrain>(oldState).InitializeAsync(oldPending, TimeSpan.FromMinutes(15));
        await fixture.ClusterClient.GetGrain<IPendingOAuthPointerGrain>(server.Id.Value).SetCurrentStateAsync(oldState, TimeSpan.FromMinutes(15));

        // A NEW consent attempt supersedes the old one (e.g. the owner clicked Reconnect again).
        var newState = $"state-new-{Guid.NewGuid():N}";
        await fixture.ClusterClient.GetGrain<IPendingOAuthPointerGrain>(server.Id.Value).SetCurrentStateAsync(newState, TimeSpan.FromMinutes(15));

        using var client = await CreateAuthenticatedClientNoRedirectAsync(seeded.UserId);
        var resp = await client.GetAsync($"/api/mcp/oauth/callback/{server.Id.Value}?code=whatever&state={oldState}");

        Assert.Equal(System.Net.HttpStatusCode.Redirect, resp.StatusCode);
        Assert.Contains("reason=superseded", resp.Headers.Location!.ToString());
    }

    [Fact]
    public async Task Callback_ServerPatchedAwayFromOAuthMidConsent_IsRejectedAndStoresNoToken()
    {
        // Finding 3 (Task 4 review, hardening): if the owner PATCHes the server AWAY from
        // oauth (e.g. authMode:"none") WHILE a consent is in flight, the OLD callback must not
        // be allowed to complete it — that would store a fresh OAuth token ciphertext as dead
        // storage on a now-non-oauth server and flip it to Published via
        // MarkOAuthConnectedAsync. A pending flow is seeded directly (mirrors the mix-up/
        // wrong-owner/superseded tests above) rather than driven through the real discovery/DCR
        // dance, so this test never needs a live token endpoint — the new "still oauth?" guard
        // must reject the callback BEFORE any token exchange is attempted.
        var seeded = await fixture.SeedUserAsync($"oauth-not-oauth-{Guid.NewGuid():N}@example.com", "Not OAuth Anymore");
        var space = fixture.ClusterClient.GetGrain<ISpaceGrain>(seeded.SpaceId);
        var server = await space.CreateHttpMcpServerAsync($"srv-not-oauth-{Guid.NewGuid():N}", "https://mcp.example.test/", McpServerAuthModes.Oauth, null, null);

        var state = $"state-{Guid.NewGuid():N}";
        var pending = new PendingOAuthState(
            server.Id.Value, seeded.UserId.Value, seeded.SpaceId, "verifier-1",
            "https://as.example.test", "https://as.example.test/authorize", "https://as.example.test/token", "client-1", null);
        await fixture.ClusterClient.GetGrain<IPendingOAuthGrain>(state).InitializeAsync(pending, TimeSpan.FromMinutes(15));
        await fixture.ClusterClient.GetGrain<IPendingOAuthPointerGrain>(server.Id.Value).SetCurrentStateAsync(state, TimeSpan.FromMinutes(15));

        // The owner switches the server away from oauth WHILE the above consent is still pending.
        using (var patchClient = await fixture.CreateAuthenticatedClientAsync(seeded.UserId))
        {
            var patchResp = await patchClient.PatchAsJsonAsync($"/api/mcp-servers/{server.Id.Value}", new { authMode = "none" });
            Assert.Equal(System.Net.HttpStatusCode.OK, patchResp.StatusCode);
        }

        using var client = await CreateAuthenticatedClientNoRedirectAsync(seeded.UserId);
        var resp = await client.GetAsync($"/api/mcp/oauth/callback/{server.Id.Value}?code=whatever&state={state}");

        Assert.Equal(System.Net.HttpStatusCode.Redirect, resp.StatusCode);
        Assert.Contains("reason=not_oauth", resp.Headers.Location!.ToString());

        var repository = fixture.Services.GetRequiredService<Korat.Domain.Persistence.IMetadataRepository>();
        Assert.Null(await repository.GetMcpServerOAuthTokenCiphertextAsync(server.Id, default));
    }

    /// <summary>
    /// Reality-over-plan fix: <c>fixture.CreateAuthenticatedClientAsync</c> builds its client via
    /// <c>Factory.CreateClient()</c>, whose default <see cref="WebApplicationFactoryClientOptions.AllowAutoRedirect"/>
    /// is TRUE — so a plain authenticated client transparently FOLLOWS the callback's 302 to
    /// <c>/servers/{id}?connected=false&amp;reason=...</c>, a client-side SPA route that 404s in a
    /// plain `dotnet test` run (no built SPA in wwwroot — see WebMcpServerContractTests's
    /// SpacePage_ReturnsHtmlWithNodesAndServersSections skip-if-absent precedent), masking the
    /// actual 302 status/Location this test needs to observe directly. Mirrors the existing
    /// GitHubSigninFlowTests idiom (AllowAutoRedirect=false) for the exact same reason.
    /// </summary>
    private async Task<HttpClient> CreateAuthenticatedClientNoRedirectAsync(Korat.Domain.Auth.UserId userId)
    {
        Guid sessionId;
        using (var scope = fixture.Factory.Services.CreateScope())
        {
            var sessions = scope.ServiceProvider.GetRequiredService<Korat.Cloud.Web.Auth.Services.ISessionService>();
            var session = await sessions.CreateAsync(userId, "test-agent", "127.0.0.1", default);
            sessionId = session.Id;
        }

        var client = fixture.Factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add("Cookie", $"{Korat.Cloud.Web.Auth.CanonicalSigninHandler.SessionCookieName}={sessionId:N}");
        return client;
    }

    [Fact]
    public async Task GetMcpServer_OAuthServer_NeverReturnsTokenMaterial()
    {
        var seeded = await fixture.SeedUserAsync($"oauth-get-{Guid.NewGuid():N}@example.com", "Get Test");
        var space = fixture.ClusterClient.GetGrain<ISpaceGrain>(seeded.SpaceId);
        var server = await space.CreateHttpMcpServerAsync($"srv-get-{Guid.NewGuid():N}", "https://mcp.example.test/", McpServerAuthModes.Oauth, null, null);

        using var client = await fixture.CreateAuthenticatedClientAsync(seeded.UserId);
        var resp = await client.GetAsync($"/api/mcp-servers/{server.Id.Value}");
        var raw = await resp.Content.ReadAsStringAsync();

        Assert.DoesNotContain("access_token", raw);
        Assert.DoesNotContain("refresh_token", raw);
        Assert.DoesNotContain("client_secret", raw);
        Assert.DoesNotContain("EncryptedOAuthToken", raw);
    }
}
