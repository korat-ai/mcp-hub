using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Korat.Cloud.IntegrationTests.SpaceMcp;
using Korat.Cloud.Mcp.Space;
using Korat.Domain;
using Korat.GrainInterfaces;
using Korat.Mcp;

namespace Korat.Cloud.IntegrationTests.SpaceMcpOAuth;

/// <summary>
/// Space-MCP inc-2a, Task 9 — the spec's Inc-2a exit criterion, scripted: a pre-registered
/// MCP client walks the FULL 2025-06-18 authorization flow with NOTHING hardcoded that the
/// protocol can discover — 401 → parse resource_metadata → fetch PRM → derive AS metadata →
/// PKCE authorize → owner consent → code → token → initialize → tools/list against a REAL
/// relay-backed publisher → refresh (same session survives — identity is rotation-stable) →
/// consent revoke → 401. Plus the no-identity-scope E2E rejection.
/// </summary>
[Trait("Category", "SpaceMcpOAuth")]
public sealed class SpaceMcpOAuthEndToEndTests(KoratIntegrationFixture fixture) : IClassFixture<KoratIntegrationFixture>
{
    private const string InitializeBody = """
        {"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"e2e","version":"1"}}}
        """;
    private const string InitializedNotification = """{"jsonrpc":"2.0","method":"notifications/initialized"}""";
    private const string ToolsListBody = """{"jsonrpc":"2.0","id":2,"method":"tools/list"}""";

    private static HttpRequestMessage McpPost(string spaceSeg, string body, string? bearer, string? sessionId = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/mcp/{spaceSeg}")
        { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        request.Headers.TryAddWithoutValidation("MCP-Protocol-Version", "2025-06-18");
        if (bearer is not null)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        if (sessionId is not null)
            request.Headers.Add("Mcp-Session-Id", sessionId);
        return request;
    }

    [Fact]
    public async Task PreRegisteredClient_FullSpecFlow_AgainstRealRelayBackend()
    {
        await fixture.EnsureOAuthClientAsync(OAuthFlowHelper.RedirectUri);

        // ── Arrange the Space: one Published server, granted to the OAUTH-derived identity,
        //    backed by a real relay publisher (SpaceMcpInitializeToolsTests' exact shape). ──
        var seeded = await fixture.SeedUserAsync($"e2e-{Guid.NewGuid():N}@example.com", "E2E Owner");
        var space = fixture.ClusterClient.GetGrain<ISpaceGrain>(seeded.SpaceId);
        var publisherNodeId = NodeId.New().Value;
        var server = (await space.PublishMcpServerAsync(
            new NodeId(publisherNodeId), $"e2e-srv-{Guid.NewGuid():N}", "echo", "demo"))!;
        var oauthIdentity = SpaceMcpConsumerIdentity.DeriveOAuth(
            OAuthFlowHelper.ClientId, seeded.UserId, new SpaceId(seeded.SpaceId));
        var accessRequest = await space.CreateAccessRequestAsync(oauthIdentity, server.Id, NodeId.New());
        await space.ApproveAccessRequestAsync(accessRequest.Id, seeded.UserId);
        var publisherToken = await fixture.IssueCliTokenAsync(seeded.UserId);
        await using var publisher = await FakeMcpPublisher.ConnectAsync(
            fixture.Factory, publisherNodeId, publisherToken,
            tools: [("echo", "Echoes input back", null)]);

        var http = fixture.Factory.CreateClient();

        // ── (1) Unauthenticated POST → 401 + resource_metadata challenge. ──
        var challenge401 = await http.SendAsync(McpPost(seeded.SpaceId, InitializeBody, bearer: null));
        Assert.Equal(HttpStatusCode.Unauthorized, challenge401.StatusCode);
        var wwwAuthenticate = challenge401.Headers.WwwAuthenticate.ToString();
        var prmUrl = Regex.Match(wwwAuthenticate, "resource_metadata=\"([^\"]+)\"").Groups[1].Value;
        Assert.False(string.IsNullOrEmpty(prmUrl), $"no resource_metadata in: {wwwAuthenticate}");

        // ── (2) PRM → resource + AS issuer. ──
        var prm = JsonNode.Parse(await http.GetStringAsync(prmUrl))!;
        var resource = prm["resource"]!.GetValue<string>();
        Assert.Equal($"http://localhost/mcp/{seeded.SpaceId}", resource);
        var asIssuer = prm["authorization_servers"]!.AsArray()[0]!.GetValue<string>();

        // ── (3) AS metadata at the RFC 8414 path under the advertised issuer. ──
        var asMetadata = JsonNode.Parse(await http.GetStringAsync(
            $"{asIssuer.TrimEnd('/')}/.well-known/oauth-authorization-server"))!;
        Assert.Equal(asIssuer.TrimEnd('/'), asMetadata["issuer"]!.GetValue<string>().TrimEnd('/'));
        var authorizeEndpoint = asMetadata["authorization_endpoint"]!.GetValue<string>();
        Assert.EndsWith("/connect/authorize", authorizeEndpoint);

        // ── (4)+(5) PKCE authorize + consent + code + token. ──
        var browser = await fixture.CreateAuthenticatedNoRedirectClientAsync(seeded.UserId);
        var (verifier, challenge) = OAuthFlowHelper.NewPkcePair();
        // Request the EXACT scope the real MCP clients send — korat:mcp + offline_access (the SDK
        // auto-appends offline_access for a refresh token). Proves the capstone-shape flow end to
        // end (authorize → consent → code → token → refresh below), not just consent rendering.
        var code = await OAuthFlowHelper.AuthorizeAndConsentAsync(
            browser, OAuthFlowHelper.AuthorizeUrl(resource, challenge, "korat:mcp offline_access"));
        var tokens = await OAuthFlowHelper.ExchangeCodeAsync(http, code, verifier, resource);
        var accessToken = tokens["access_token"]!.GetValue<string>();
        var refreshToken = tokens["refresh_token"]!.GetValue<string>();

        // ── (6) initialize → session; initialized notification → 202. ──
        var init = await http.SendAsync(McpPost(seeded.SpaceId, InitializeBody, accessToken));
        Assert.Equal(HttpStatusCode.OK, init.StatusCode);
        var sessionId = init.Headers.GetValues("Mcp-Session-Id").Single();
        var notified = await http.SendAsync(McpPost(seeded.SpaceId, InitializedNotification, accessToken, sessionId));
        Assert.Equal(HttpStatusCode.Accepted, notified.StatusCode);

        // ── (7) tools/list shows the REAL relay-backed granted tool, namespaced. ──
        var toolsResponse = await http.SendAsync(McpPost(seeded.SpaceId, ToolsListBody, accessToken, sessionId));
        Assert.Equal(HttpStatusCode.OK, toolsResponse.StatusCode);
        var tools = JsonNode.Parse(await toolsResponse.Content.ReadAsStringAsync())!;
        var expectedTool = ToolNamespacer.Namespaced(
            ToolNamespacer.Slug(server.DisplayName, server.Id.Value), "echo");
        var toolNames = tools["result"]!["tools"]!.AsArray().Select(t => t!["name"]!.GetValue<string>()).ToList();
        Assert.Contains(expectedTool, toolNames);

        // ── (8) refresh: rotated tokens, SAME session keeps working (identity is
        //        (client × owner × Space) — rotation-stable, so the binding still matches). ──
        var (refreshStatus, refreshed) = await OAuthFlowHelper.RefreshAsync(http, refreshToken);
        Assert.Equal(HttpStatusCode.OK, refreshStatus);
        var accessToken2 = refreshed["access_token"]!.GetValue<string>();
        var afterRefresh = await http.SendAsync(McpPost(seeded.SpaceId, ToolsListBody, accessToken2, sessionId));
        Assert.Equal(HttpStatusCode.OK, afterRefresh.StatusCode);

        // ── (9) consent revoke → token dead + session gone. ──
        var console = await fixture.CreateAuthenticatedClientAsync(seeded.UserId);
        var consentId = JsonNode.Parse(await console.GetStringAsync("/api/oauth/consents"))!
            .AsArray()[0]!["id"]!.GetValue<string>();
        var revoke = await console.PostAsync($"/api/oauth/consents/{consentId}/revoke", null);
        Assert.Equal(HttpStatusCode.NoContent, revoke.StatusCode);
        var afterRevoke = await http.SendAsync(McpPost(seeded.SpaceId, ToolsListBody, accessToken2, sessionId));
        Assert.Equal(HttpStatusCode.Unauthorized, afterRevoke.StatusCode);
        Assert.Contains("resource_metadata=", afterRevoke.Headers.WwwAuthenticate.ToString());
    }

    [Fact]
    public async Task IdentityScopes_NeverObtainable_EndToEnd()
    {
        await fixture.EnsureOAuthClientAsync(OAuthFlowHelper.RedirectUri);
        var seeded = await fixture.SeedUserAsync($"e2e-scope-{Guid.NewGuid():N}@example.com", "E2E Scope");
        var browser = await fixture.CreateAuthenticatedNoRedirectClientAsync(seeded.UserId);
        var (_, challenge) = OAuthFlowHelper.NewPkcePair();

        // Every identity-scope combination dies before a code exists (SF-7).
        // openid (and any non-{korat:mcp,offline_access} scope) must be rejected — incl. openid
        // hidden between two whitelisted scopes, and offline_access alone without korat:mcp (the
        // only case that exercises the new !Contains(korat:mcp) clause).
        foreach (var scope in new[] { "openid", "openid email profile", "korat:mcp openid", "korat:mcp openid offline_access", "offline_access" })
        {
            var response = await browser.GetAsync(OAuthFlowHelper.AuthorizeUrl(
                $"http://localhost/mcp/{seeded.SpaceId}", challenge, scope));
            Assert.NotEqual(HttpStatusCode.OK, response.StatusCode); // never a consent page
            if (response.Headers.Location is { } location)
            {
                Assert.Contains("error=", location.ToString());
                Assert.DoesNotContain("code=", location.ToString());
            }
        }
    }
}
