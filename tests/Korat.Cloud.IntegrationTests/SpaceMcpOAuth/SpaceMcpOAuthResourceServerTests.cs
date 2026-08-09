using System.Net;
using System.Net.Http.Headers;
using System.Text;

namespace Korat.Cloud.IntegrationTests.SpaceMcpOAuth;

/// <summary>
/// Space-MCP inc-2a, Task 6 (SF-4 swap + BLOCKER-1): /mcp/{spaceSeg} accepts an OAuth access
/// token — validated in-process (UseLocalServer), with the two load-bearing checks
/// (audience == this exact per-Space URL; consent-Space claim == path-Space) — WHILE the
/// inc-1 scoped korat_cli_ bearer keeps working unchanged.
/// </summary>
[Trait("Category", "SpaceMcpOAuth")]
public sealed class SpaceMcpOAuthResourceServerTests(KoratIntegrationFixture fixture) : IClassFixture<KoratIntegrationFixture>
{
    private const string InitializeBody = """
        {"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"t","version":"1"}}}
        """;

    private static HttpRequestMessage McpInitialize(string spaceSeg, string bearer)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/mcp/{spaceSeg}")
        {
            Content = new StringContent(InitializeBody, Encoding.UTF8, "application/json"),
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        request.Headers.TryAddWithoutValidation("MCP-Protocol-Version", "2025-06-18");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        return request;
    }

    private async Task<(string SpaceId, string Resource, string AccessToken)> IssueForNewOwnerAsync()
    {
        await fixture.EnsureOAuthClientAsync(OAuthFlowHelper.RedirectUri);
        var seeded = await fixture.SeedUserAsync($"rs-{Guid.NewGuid():N}@example.com", "RS Owner");
        var resource = $"http://localhost/mcp/{seeded.SpaceId}";
        var client = await fixture.CreateAuthenticatedNoRedirectClientAsync(seeded.UserId);
        var (verifier, challenge) = OAuthFlowHelper.NewPkcePair();
        var code = await OAuthFlowHelper.AuthorizeAndConsentAsync(client, OAuthFlowHelper.AuthorizeUrl(resource, challenge));
        var tokens = await OAuthFlowHelper.ExchangeCodeAsync(fixture.Factory.CreateClient(), code, verifier, resource);
        return (seeded.SpaceId, resource, tokens["access_token"]!.GetValue<string>());
    }

    [Fact]
    public async Task OAuthToken_OnItsConsentedSpace_InitializeSucceeds()
    {
        var (spaceId, _, accessToken) = await IssueForNewOwnerAsync();
        var response = await fixture.Factory.CreateClient().SendAsync(McpInitialize(spaceId, accessToken));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.Contains("Mcp-Session-Id"));
    }

    /// <summary>
    /// SF-2 fix: the plan's original version of this test seeded Space-B under a SECOND,
    /// DIFFERENT owner — which contradicts the test's own name and, more importantly, tests
    /// a WEAKER case than BLOCKER-1 actually demands. When the owner differs, the live
    /// ownership re-check (<c>space.OwnerUserId != owner</c> → 404) would ALSO catch a
    /// bypass, masking whether the audience/space-claim checks are doing any work at all.
    /// The sharpest case is the SAME owner holding BOTH Spaces: the ownership re-check then
    /// passes for either Space, so ONLY the audience + consent-Space-claim checks stand
    /// between a Space-A token and Space-B. That is what this test now proves — via
    /// <see cref="KoratIntegrationFixture.SeedAdditionalSpaceForOwnerAsync"/>, a second
    /// <c>SpaceRecord</c> row for the SAME <c>UserId</c>.
    /// </summary>
    [Fact]
    public async Task CrossTenant_TokenForSpaceA_RejectedOnSpaceB_EvenForSameOwner()
    {
        await fixture.EnsureOAuthClientAsync(OAuthFlowHelper.RedirectUri);
        var seeded = await fixture.SeedUserAsync($"xt-{Guid.NewGuid():N}@example.com", "XT Owner");
        var spaceB = await fixture.SeedAdditionalSpaceForOwnerAsync(seeded.UserId, "xt-same-owner");

        var resourceA = $"http://localhost/mcp/{seeded.SpaceId}";
        var client = await fixture.CreateAuthenticatedNoRedirectClientAsync(seeded.UserId);
        var (verifier, challenge) = OAuthFlowHelper.NewPkcePair();
        var code = await OAuthFlowHelper.AuthorizeAndConsentAsync(client, OAuthFlowHelper.AuthorizeUrl(resourceA, challenge));
        var tokens = await OAuthFlowHelper.ExchangeCodeAsync(fixture.Factory.CreateClient(), code, verifier, resourceA);
        var accessToken = tokens["access_token"]!.GetValue<string>();

        var response = await fixture.Factory.CreateClient().SendAsync(McpInitialize(spaceB, accessToken));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains("resource_metadata=", response.Headers.WwwAuthenticate.ToString());
    }

    /// <summary>
    /// Kept alongside the same-owner case above (the plan's original scenario) for extra
    /// coverage: a Space-A token used against a Space owned by an entirely different user
    /// must also 401. Here BOTH the live ownership re-check AND the audience/space-claim
    /// checks would independently reject the call — this test does not by itself prove the
    /// BLOCKER-1 checks are load-bearing (the same-owner test above does that).
    /// </summary>
    [Fact]
    public async Task CrossTenant_TokenForSpaceA_RejectedOnSpaceB_DifferentOwner()
    {
        await fixture.EnsureOAuthClientAsync(OAuthFlowHelper.RedirectUri);
        var seeded = await fixture.SeedUserAsync($"xt-{Guid.NewGuid():N}@example.com", "XT Owner");
        var spaceB = await fixture.SeedUserAsync($"xt-b-{Guid.NewGuid():N}@example.com", "XT B");

        var resourceA = $"http://localhost/mcp/{seeded.SpaceId}";
        var client = await fixture.CreateAuthenticatedNoRedirectClientAsync(seeded.UserId);
        var (verifier, challenge) = OAuthFlowHelper.NewPkcePair();
        var code = await OAuthFlowHelper.AuthorizeAndConsentAsync(client, OAuthFlowHelper.AuthorizeUrl(resourceA, challenge));
        var tokens = await OAuthFlowHelper.ExchangeCodeAsync(fixture.Factory.CreateClient(), code, verifier, resourceA);
        var accessToken = tokens["access_token"]!.GetValue<string>();

        var response = await fixture.Factory.CreateClient().SendAsync(McpInitialize(spaceB.SpaceId, accessToken));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains("resource_metadata=", response.Headers.WwwAuthenticate.ToString());
    }

    [Fact]
    public async Task ExpiredOrRevokedOrGarbageOAuthToken_401WithChallenge()
    {
        var seeded = await fixture.SeedUserAsync($"garb-{Guid.NewGuid():N}@example.com", "Garb Owner");
        var response = await fixture.Factory.CreateClient()
            .SendAsync(McpInitialize(seeded.SpaceId, "definitely-not-a-korat-token"));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains("resource_metadata=", response.Headers.WwwAuthenticate.ToString());
    }

    [Fact]
    public async Task Inc1ScopedToken_IsRejected_Р25()
    {
        // INVERTED by Р25. This test used to assert the opposite — that a korat_cli_ token still
        // opened /mcp/{space} alongside OAuth. That second entrance was not a duplicate of the
        // first: it derived the consumer identity from the TOKEN, and a machine has one token, so
        // every agent on it shared one cagg_ identity. Per-agent grants were machine-wide grants
        // wearing a per-agent label.
        //
        // Rejection must be explicit rather than a fallthrough into some other 500: the client has
        // to learn it should be doing OAuth, which is what the 401 + resource_metadata challenge
        // tells it.
        var seeded = await fixture.SeedUserAsync($"inc1-{Guid.NewGuid():N}@example.com", "Inc1 Owner");
        var scoped = await fixture.IssueScopedCliTokenAsync(seeded.UserId, seeded.SpaceId);
        var response = await fixture.Factory.CreateClient().SendAsync(McpInitialize(seeded.SpaceId, scoped));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains(
            "resource_metadata",
            response.Headers.WwwAuthenticate.ToString(),
            StringComparison.OrdinalIgnoreCase);
    }
}
