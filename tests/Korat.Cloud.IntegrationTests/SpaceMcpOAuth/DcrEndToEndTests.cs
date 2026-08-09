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
/// Space-MCP inc-2b exit criterion, scripted: a client with ZERO pre-configuration
/// auto-registers via RFC 7591 DCR, then walks the UNCHANGED inc-2a flow (401 → PRM → AS metadata
/// → authorize → consent → token → initialize → tools/list against a REAL relay-backed publisher)
/// using its DCR-assigned client_id. Plus the SF-7 proof that a DCR client can NEVER obtain an
/// identity scope — stopped at the consent layer (OpenIddict exempts openid from the per-client
/// scope-permission check, so the consent policy is the real gate; verified grounding #6).
/// </summary>
[Trait("Category", "SpaceMcpOAuth")]
public sealed class DcrEndToEndTests(KoratIntegrationFixture fixture) : IClassFixture<KoratIntegrationFixture>
{
    private const string InitializeBody = """
        {"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"dcr","version":"1"}}}
        """;
    private const string InitializedNotification = """{"jsonrpc":"2.0","method":"notifications/initialized"}""";
    private const string ToolsListBody = """{"jsonrpc":"2.0","id":2,"method":"tools/list"}""";
    private const string DcrRedirectUri = "http://127.0.0.1:47777/callback";

    private static HttpRequestMessage McpPost(string spaceSeg, string body, string? bearer, string? sessionId = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/mcp/{spaceSeg}")
        { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        request.Headers.TryAddWithoutValidation("MCP-Protocol-Version", "2025-06-18");
        if (bearer is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        if (sessionId is not null) request.Headers.Add("Mcp-Session-Id", sessionId);
        return request;
    }

    private static async Task<string> RegisterDcrClientAsync(HttpClient http)
    {
        var response = await http.PostAsync("/connect/register", new StringContent(new JsonObject
        {
            ["client_name"] = "Zero-config MCP client",
            ["redirect_uris"] = new JsonArray { DcrRedirectUri },
            ["token_endpoint_auth_method"] = "none",
        }.ToJsonString(), Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var doc = JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsObject();
        Assert.False(doc.ContainsKey("client_secret"));
        return doc["client_id"]!.GetValue<string>();
    }

    [Fact]
    public async Task DcrClient_ZeroConfig_FullSpecFlow_AgainstRealRelayBackend()
    {
        var http = fixture.Factory.CreateClient();

        // ── (0) DCR: auto-register with zero manual client setup. ──
        var dcrClientId = await RegisterDcrClientAsync(http);

        // ── Arrange the Space: one Published server, granted to the DCR-derived identity,
        //    backed by a real relay publisher (SpaceMcpOAuthEndToEndTests' exact shape). ──
        var seeded = await fixture.SeedUserAsync($"dcr-{Guid.NewGuid():N}@example.com", "DCR Owner");
        var space = fixture.ClusterClient.GetGrain<ISpaceGrain>(seeded.SpaceId);
        var publisherNodeId = NodeId.New().Value;
        var server = (await space.PublishMcpServerAsync(
            new NodeId(publisherNodeId), $"dcr-srv-{Guid.NewGuid():N}", "echo", "demo"))!;
        var identity = SpaceMcpConsumerIdentity.DeriveOAuth(dcrClientId, seeded.UserId, new SpaceId(seeded.SpaceId));
        var accessRequest = await space.CreateAccessRequestAsync(identity, server.Id, NodeId.New());
        await space.ApproveAccessRequestAsync(accessRequest.Id, seeded.UserId);
        var publisherToken = await fixture.IssueCliTokenAsync(seeded.UserId);
        await using var publisher = await FakeMcpPublisher.ConnectAsync(
            fixture.Factory, publisherNodeId, publisherToken, tools: [("echo", "Echoes input back", null)]);

        // ── (1) 401 challenge → PRM → AS metadata (discovery, nothing hardcoded). ──
        var challenge401 = await http.SendAsync(McpPost(seeded.SpaceId, InitializeBody, bearer: null));
        Assert.Equal(HttpStatusCode.Unauthorized, challenge401.StatusCode);
        var prmUrl = Regex.Match(challenge401.Headers.WwwAuthenticate.ToString(),
            "resource_metadata=\"([^\"]+)\"").Groups[1].Value;
        var prm = JsonNode.Parse(await http.GetStringAsync(prmUrl))!;
        var resource = prm["resource"]!.GetValue<string>();
        Assert.Equal($"http://localhost/mcp/{seeded.SpaceId}", resource);

        // ── (2)+(3) authorize + consent + token, using the DCR-assigned client_id. ──
        var browser = await fixture.CreateAuthenticatedNoRedirectClientAsync(seeded.UserId);
        var (verifier, challenge) = OAuthFlowHelper.NewPkcePair();
        var code = await OAuthFlowHelper.AuthorizeAndConsentAsync(
            browser, OAuthFlowHelper.AuthorizeUrl(resource, challenge, dcrClientId, DcrRedirectUri), DcrRedirectUri);
        var tokens = await OAuthFlowHelper.ExchangeCodeAsync(http, code, verifier, resource, dcrClientId, DcrRedirectUri);
        var accessToken = tokens["access_token"]!.GetValue<string>();

        // ── (4) initialize → session; (5) tools/list shows the REAL relay tool, namespaced. ──
        var init = await http.SendAsync(McpPost(seeded.SpaceId, InitializeBody, accessToken));
        Assert.Equal(HttpStatusCode.OK, init.StatusCode);
        var sessionId = init.Headers.GetValues("Mcp-Session-Id").Single();
        var notified = await http.SendAsync(McpPost(seeded.SpaceId, InitializedNotification, accessToken, sessionId));
        Assert.Equal(HttpStatusCode.Accepted, notified.StatusCode);

        var toolsResponse = await http.SendAsync(McpPost(seeded.SpaceId, ToolsListBody, accessToken, sessionId));
        Assert.Equal(HttpStatusCode.OK, toolsResponse.StatusCode);
        var tools = JsonNode.Parse(await toolsResponse.Content.ReadAsStringAsync())!;
        var expectedTool = ToolNamespacer.Namespaced(ToolNamespacer.Slug(server.DisplayName, server.Id.Value), "echo");
        var toolNames = tools["result"]!["tools"]!.AsArray().Select(t => t!["name"]!.GetValue<string>()).ToList();
        Assert.Contains(expectedTool, toolNames);
    }

    [Fact]
    public async Task DcrClient_RequestingIdentityScopeAtAuthorize_RejectedAtConsent()
    {
        // SF-7 end-to-end: a DCR client asks for openid at AUTHORIZE. OpenIddict exempts openid
        // from the per-client scope-permission check (verified grounding #6), so the ONLY stop is
        // the consent handler's korat:mcp-only policy — the same gate the pre-registered client
        // hits. A DCR client can therefore NEVER obtain a Korat identity scope.
        var http = fixture.Factory.CreateClient();
        var dcrClientId = await RegisterDcrClientAsync(http);
        var seeded = await fixture.SeedUserAsync($"dcr-scope-{Guid.NewGuid():N}@example.com", "DCR Scope Owner");
        var resource = $"http://localhost/mcp/{seeded.SpaceId}";
        var browser = await fixture.CreateAuthenticatedNoRedirectClientAsync(seeded.UserId);
        var (_, challenge) = OAuthFlowHelper.NewPkcePair();

        var response = await browser.GetAsync(
            OAuthFlowHelper.AuthorizeUrl(resource, challenge, dcrClientId, DcrRedirectUri, scope: "openid korat:mcp"));

        // Not a 200 consent page; a same-origin invalid_scope error redirect back to the callback.
        Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);
        if (response.Headers.Location is { } location)
        {
            Assert.StartsWith(DcrRedirectUri, location.ToString());
            Assert.Contains("error=invalid_scope", location.ToString(), StringComparison.Ordinal);
        }
    }
}
