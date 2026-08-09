using System.Net;
using System.Text.Json.Nodes;

namespace Korat.Cloud.IntegrationTests.SpaceMcpOAuth;

/// <summary>
/// Space-MCP inc-2b, Task 1: MCP 2025-06-18 / OAuth 2.1 require S256 PKCE. The live-dev smoke
/// found the AS advertising + accepting OpenIddict's default {plain, S256}. This pins S256-only
/// BOTH in the RFC 8414 metadata (code_challenge_methods_supported) AND at the authorization
/// endpoint (a plain code_challenge_method is rejected; a challenge with NO method is rejected).
/// </summary>
[Trait("Category", "SpaceMcpOAuth")]
public sealed class S256OnlyPkceTests(KoratIntegrationFixture fixture) : IClassFixture<KoratIntegrationFixture>
{
    [Fact]
    public async Task Metadata_AdvertisesS256Only_NoPlain()
    {
        var client = fixture.Factory.CreateClient();
        var doc = JsonNode.Parse(await client.GetStringAsync("/.well-known/oauth-authorization-server"))!;
        var methods = doc["code_challenge_methods_supported"]!.AsArray().Select(n => n!.GetValue<string>()).ToList();
        Assert.Contains("S256", methods);
        Assert.DoesNotContain("plain", methods);
    }

    [Fact]
    public async Task Authorize_WithPlainCodeChallengeMethod_IsRejected()
    {
        await fixture.EnsureOAuthClientAsync(OAuthFlowHelper.RedirectUri);
        var seeded = await fixture.SeedUserAsync($"s256-{Guid.NewGuid():N}@example.com", "S256 Owner");
        var browser = await fixture.CreateAuthenticatedNoRedirectClientAsync(seeded.UserId);
        var resource = $"http://localhost/mcp/{seeded.SpaceId}";
        var url =
            "/connect/authorize?response_type=code" +
            $"&client_id={OAuthFlowHelper.ClientId}" +
            $"&redirect_uri={Uri.EscapeDataString(OAuthFlowHelper.RedirectUri)}" +
            "&scope=korat:mcp" +
            $"&resource={Uri.EscapeDataString(resource)}" +
            "&code_challenge=abc123plainchallengevalue&code_challenge_method=plain" +
            $"&state=st-{Guid.NewGuid():N}";

        var response = await browser.GetAsync(url);

        // OpenIddict rejects the request BEFORE the consent page renders. A rejected authorize
        // request is either a same-origin error redirect back to redirect_uri carrying
        // ?error=invalid_request, or a direct 400 — never a 200 consent page.
        Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);
        if (response.Headers.Location is { } location)
            Assert.Contains("error=invalid_request", location.ToString(), StringComparison.Ordinal);
    }
}
