using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using Korat.Cloud.Security.Audit;
using Korat.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Korat.Domain;
using Korat.Cloud.Mcp.Space;

namespace Korat.Cloud.IntegrationTests.SpaceMcpOAuth;

/// <summary>
/// Space-MCP inc-2a, Task 8 (SF-6 — "a requirement for a new inbound auth surface, not
/// polish"): the owner lists consents and revokes one → access token dies IMMEDIATELY
/// (reference tokens), the refresh token dies, and every live aggregator session for the
/// derived identity is torn down (registry fan-out via <c>TerminateAllAsync</c>, Task 7's
/// encapsulation of the fan-out — NOT an inline loop in the endpoint) — plus cross-user
/// cloaking and a fail-closed audit entry.
/// </summary>
[Trait("Category", "SpaceMcpOAuth")]
public sealed class OAuthConsentRevocationTests(KoratIntegrationFixture fixture) : IClassFixture<KoratIntegrationFixture>
{
    private const string InitializeBody = """
        {"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"t","version":"1"}}}
        """;
    private const string ToolsListBody = """{"jsonrpc":"2.0","id":2,"method":"tools/list"}""";

    private static HttpRequestMessage McpPost(string spaceSeg, string bearer, string body, string? sessionId = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/mcp/{spaceSeg}")
        { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        if (sessionId is not null)
            request.Headers.Add("Mcp-Session-Id", sessionId);
        return request;
    }

    [Fact]
    public async Task RevokeConsent_KillsTokens_AndTearsDownLiveSession()
    {
        await fixture.EnsureOAuthClientAsync(OAuthFlowHelper.RedirectUri);
        var seeded = await fixture.SeedUserAsync($"rvk-{Guid.NewGuid():N}@example.com", "Rvk Owner");
        var resource = $"http://localhost/mcp/{seeded.SpaceId}";
        var browser = await fixture.CreateAuthenticatedNoRedirectClientAsync(seeded.UserId);
        var (verifier, challenge) = OAuthFlowHelper.NewPkcePair();
        var code = await OAuthFlowHelper.AuthorizeAndConsentAsync(browser, OAuthFlowHelper.AuthorizeUrl(resource, challenge));
        var tokens = await OAuthFlowHelper.ExchangeCodeAsync(fixture.Factory.CreateClient(), code, verifier, resource);
        var accessToken = tokens["access_token"]!.GetValue<string>();
        var refreshToken = tokens["refresh_token"]!.GetValue<string>();

        // Open a live MCP session with the OAuth token.
        var http = fixture.Factory.CreateClient();
        var init = await http.SendAsync(McpPost(seeded.SpaceId, accessToken, InitializeBody));
        Assert.Equal(HttpStatusCode.OK, init.StatusCode);
        var sessionId = init.Headers.GetValues("Mcp-Session-Id").Single();

        // Owner console: list → exactly one consent for this fresh owner.
        var console = await fixture.CreateAuthenticatedClientAsync(seeded.UserId);
        var list = JsonNode.Parse(await console.GetStringAsync("/api/oauth/consents"))!.AsArray();
        Assert.Single(list);
        var consent = list[0]!;
        Assert.Equal(OAuthFlowHelper.ClientId, consent["clientId"]!.GetValue<string>());
        Assert.Equal(seeded.SpaceId, consent["spaceId"]!.GetValue<string>());
        var consentId = consent["id"]!.GetValue<string>();

        // Revoke.
        var revoke = await console.PostAsync($"/api/oauth/consents/{consentId}/revoke", null);
        Assert.Equal(HttpStatusCode.NoContent, revoke.StatusCode);

        // (a) Access token dead IMMEDIATELY (reference token) → 401 + challenge.
        var afterAccess = await http.SendAsync(McpPost(seeded.SpaceId, accessToken, ToolsListBody, sessionId));
        Assert.Equal(HttpStatusCode.Unauthorized, afterAccess.StatusCode);

        // (b) Refresh token dead → invalid_grant.
        var (refreshStatus, refreshBody) = await OAuthFlowHelper.RefreshAsync(http, refreshToken);
        Assert.Equal(HttpStatusCode.BadRequest, refreshStatus);
        Assert.Equal("invalid_grant", refreshBody["error"]!.GetValue<string>());

        // (c) The aggregator session itself was TERMINATED (SF-6), not merely the token revoked.
        //
        // Р25 changed how this is proven. It used to present a second, still-valid credential (the
        // owner's own scoped CLI token) and expect 404. That credential no longer opens the
        // endpoint, and minting a second OAuth one is not a substitute: obtaining it re-runs
        // authorize+consent, which both re-creates the consent this test asserts is gone in (d)
        // and re-authenticates the browser client used above.
        //
        // So the claim is checked where it actually lives — the consumer's session registry. This
        // is in fact the sharper assertion: a 404 over HTTP would also be produced by an unrelated
        // lookup failure, whereas an empty registry says the session was torn down.
        var revokedConsumer = SpaceMcpConsumerIdentity.DeriveOAuth(
            OAuthFlowHelper.ClientId, seeded.UserId, new SpaceId(seeded.SpaceId));
        var registry = fixture.ClusterClient
            .GetGrain<ISpaceMcpConsumerSessionsGrain>(revokedConsumer.Value);
        Assert.DoesNotContain(sessionId, await registry.ListAsync());

        // (d) The consent list is now empty.
        var after = JsonNode.Parse(await console.GetStringAsync("/api/oauth/consents"))!.AsArray();
        Assert.Empty(after);

        // (e) A fail-closed audit entry was recorded for the revoke (032 C1 requirement —
        // mirrors AgentDeleteCascadeTests' direct-DbContext assertion pattern).
        using var scope = fixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<KoratDbContext>();
        Assert.Contains(db.AuditEvents,
            e => e.Action == AuditActions.OAuthConsentRevoked && e.TargetId == consentId);
    }

    [Fact]
    public async Task RevokeForeignConsent_Cloaked404_NothingRevoked()
    {
        await fixture.EnsureOAuthClientAsync(OAuthFlowHelper.RedirectUri);
        var victim = await fixture.SeedUserAsync($"rvk-v-{Guid.NewGuid():N}@example.com", "Rvk Victim");
        var attacker = await fixture.SeedUserAsync($"rvk-a-{Guid.NewGuid():N}@example.com", "Rvk Attacker");
        var resource = $"http://localhost/mcp/{victim.SpaceId}";
        var browser = await fixture.CreateAuthenticatedNoRedirectClientAsync(victim.UserId);
        var (verifier, challenge) = OAuthFlowHelper.NewPkcePair();
        var code = await OAuthFlowHelper.AuthorizeAndConsentAsync(browser, OAuthFlowHelper.AuthorizeUrl(resource, challenge));
        var tokens = await OAuthFlowHelper.ExchangeCodeAsync(fixture.Factory.CreateClient(), code, verifier, resource);

        var victimConsole = await fixture.CreateAuthenticatedClientAsync(victim.UserId);
        var consentId = JsonNode.Parse(await victimConsole.GetStringAsync("/api/oauth/consents"))!
            .AsArray()[0]!["id"]!.GetValue<string>();

        var attackerConsole = await fixture.CreateAuthenticatedClientAsync(attacker.UserId);
        var response = await attackerConsole.PostAsync($"/api/oauth/consents/{consentId}/revoke", null);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        // Victim's access token still works.
        var http = fixture.Factory.CreateClient();
        var still = await http.SendAsync(McpPost(victim.SpaceId, tokens["access_token"]!.GetValue<string>(), InitializeBody));
        Assert.Equal(HttpStatusCode.OK, still.StatusCode);

        // Attacker's own list is empty — cross-user isolation on the list side too.
        var attackerList = JsonNode.Parse(await attackerConsole.GetStringAsync("/api/oauth/consents"))!.AsArray();
        Assert.Empty(attackerList);
    }
}
