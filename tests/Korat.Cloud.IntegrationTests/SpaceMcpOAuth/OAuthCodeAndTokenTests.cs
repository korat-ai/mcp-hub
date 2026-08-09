using System.Net;
using Korat.Cloud.Web.Oauth;
using Microsoft.Extensions.DependencyInjection;
using OpenIddict.Abstractions;
using OpenIddict.Validation;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Korat.Cloud.IntegrationTests.SpaceMcpOAuth;

/// <summary>
/// Space-MCP inc-2a, Task 4: consent POST → code → PKCE token exchange. The issued access
/// token is validated SERVER-SIDE (reference token; opaque to the client) via the same
/// OpenIddictValidationService the resource server uses (Task 6) — asserting the per-Space
/// audience (BLOCKER-1's raw material), scope, subject, and the korat:space / korat:client
/// claims the resource server keys on. Also proves MF-2 (offline_access granted server-side
/// so a refresh_token is actually issued).
/// </summary>
[Trait("Category", "SpaceMcpOAuth")]
public sealed class OAuthCodeAndTokenTests(KoratIntegrationFixture fixture) : IClassFixture<KoratIntegrationFixture>
{
    [Fact]
    public async Task ConsentAllow_CodeExchange_IssuesResourceScopedTokens()
    {
        await fixture.EnsureOAuthClientAsync(OAuthFlowHelper.RedirectUri);
        var seeded = await fixture.SeedUserAsync($"tok-{Guid.NewGuid():N}@example.com", "Tok Owner");
        var resource = $"http://localhost/mcp/{seeded.SpaceId}";
        var client = await fixture.CreateAuthenticatedNoRedirectClientAsync(seeded.UserId);
        var (verifier, challenge) = OAuthFlowHelper.NewPkcePair();

        var code = await OAuthFlowHelper.AuthorizeAndConsentAsync(
            client, OAuthFlowHelper.AuthorizeUrl(resource, challenge));
        var tokens = await OAuthFlowHelper.ExchangeCodeAsync(fixture.Factory.CreateClient(), code, verifier, resource);

        Assert.NotNull(tokens["access_token"]);
        Assert.NotNull(tokens["refresh_token"]); // MF-2 proof: AllowRefreshTokenFlow + offline_access granted server-side
        Assert.Equal("Bearer", tokens["token_type"]!.GetValue<string>());

        var validation = fixture.Services.GetRequiredService<OpenIddictValidationService>();
        var principal = await validation.ValidateAccessTokenAsync(tokens["access_token"]!.GetValue<string>());
        Assert.Contains(resource, principal.GetAudiences());
        Assert.True(principal.HasScope(KoratOAuthConstants.McpScope));
        Assert.Equal(seeded.UserId.Value.ToString("N"), principal.GetClaim(Claims.Subject));
        Assert.Equal(seeded.SpaceId, principal.GetClaim(KoratOAuthConstants.SpaceClaim));
        Assert.Equal(OAuthFlowHelper.ClientId, principal.GetClaim(KoratOAuthConstants.ClientClaim));
    }

    [Fact]
    public async Task ConsentDeny_RedirectsWithAccessDenied_NoCode()
    {
        await fixture.EnsureOAuthClientAsync(OAuthFlowHelper.RedirectUri);
        var seeded = await fixture.SeedUserAsync($"deny-{Guid.NewGuid():N}@example.com", "Deny Owner");
        var client = await fixture.CreateAuthenticatedNoRedirectClientAsync(seeded.UserId);
        var (_, challenge) = OAuthFlowHelper.NewPkcePair();
        var url = OAuthFlowHelper.AuthorizeUrl($"http://localhost/mcp/{seeded.SpaceId}", challenge);

        // GET the page, then POST submit=deny (same antiforgery mechanics as the helper).
        var page = await client.GetAsync(url);
        var html = await page.Content.ReadAsStringAsync();
        var xsrfCookie = page.Headers.GetValues("Set-Cookie")
            .First(c => c.StartsWith("__Secure-korat_xsrf=", StringComparison.Ordinal)).Split(';')[0];
        var fields = System.Text.RegularExpressions.Regex
            .Matches(html, "<input type=\"hidden\" name=\"([^\"]+)\" value=\"([^\"]*)\" />")
            .Select(m => new KeyValuePair<string, string>(
                System.Net.WebUtility.HtmlDecode(m.Groups[1].Value),
                System.Net.WebUtility.HtmlDecode(m.Groups[2].Value)))
            .Append(new("submit", "deny")).ToList();
        var post = new HttpRequestMessage(HttpMethod.Post, "/connect/authorize")
        { Content = new FormUrlEncodedContent(fields) };
        // See OAuthFlowHelper.AuthorizeAndConsentAsync's note: DefaultRequestHeaders' session
        // Cookie is NOT auto-merged with a per-request Cookie header — combine explicitly.
        var sessionCookie = client.DefaultRequestHeaders.TryGetValues("Cookie", out var sessionCookies)
            ? sessionCookies.First() : null;
        post.Headers.Add("Cookie", sessionCookie is null ? xsrfCookie : $"{sessionCookie}; {xsrfCookie}");

        var response = await client.SendAsync(post);

        Assert.True(response.StatusCode is HttpStatusCode.Redirect or HttpStatusCode.Found);
        var location = response.Headers.Location!.ToString();
        Assert.Contains("error=access_denied", location);
        Assert.DoesNotContain("code=", location);
    }

    [Fact]
    public async Task WrongPkceVerifier_TokenExchangeFails_InvalidGrant()
    {
        await fixture.EnsureOAuthClientAsync(OAuthFlowHelper.RedirectUri);
        var seeded = await fixture.SeedUserAsync($"pkce-{Guid.NewGuid():N}@example.com", "Pkce Owner");
        var resource = $"http://localhost/mcp/{seeded.SpaceId}";
        var client = await fixture.CreateAuthenticatedNoRedirectClientAsync(seeded.UserId);
        var (_, challenge) = OAuthFlowHelper.NewPkcePair();

        var code = await OAuthFlowHelper.AuthorizeAndConsentAsync(
            client, OAuthFlowHelper.AuthorizeUrl(resource, challenge));

        var response = await fixture.Factory.CreateClient().PostAsync("/connect/token", new FormUrlEncodedContent(
        [
            new KeyValuePair<string, string>("grant_type", "authorization_code"),
            new("code", code),
            new("code_verifier", "wrong-verifier-wrong-verifier-wrong-verifier"),
            new("redirect_uri", OAuthFlowHelper.RedirectUri),
            new("client_id", OAuthFlowHelper.ClientId),
        ]));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("invalid_grant", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task MissingCodeChallenge_AuthorizeRejected_BeforeConsent()
    {
        await fixture.EnsureOAuthClientAsync(OAuthFlowHelper.RedirectUri);
        var seeded = await fixture.SeedUserAsync($"nopkce-{Guid.NewGuid():N}@example.com", "NoPkce Owner");
        var client = await fixture.CreateAuthenticatedNoRedirectClientAsync(seeded.UserId);

        var url = "/connect/authorize?response_type=code&client_id=korat-mcp" +
                  $"&redirect_uri={Uri.EscapeDataString(OAuthFlowHelper.RedirectUri)}" +
                  "&scope=korat%3Amcp" +
                  $"&resource={Uri.EscapeDataString($"http://localhost/mcp/{seeded.SpaceId}")}&state=s";
        var response = await client.GetAsync(url);

        // RequireProofKeyForCodeExchange + the client's ft:pkce requirement: OpenIddict rejects
        // BEFORE passthrough — never a consent page.
        Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);
        if (response.Headers.Location is { } location)
            Assert.Contains("error=", location.ToString());
    }

    [Fact]
    public async Task RepeatConsent_ReusesTheSamePermanentAuthorization()
    {
        await fixture.EnsureOAuthClientAsync(OAuthFlowHelper.RedirectUri);
        var seeded = await fixture.SeedUserAsync($"reuse-{Guid.NewGuid():N}@example.com", "Reuse Owner");
        var resource = $"http://localhost/mcp/{seeded.SpaceId}";
        var client = await fixture.CreateAuthenticatedNoRedirectClientAsync(seeded.UserId);

        var (v1, c1) = OAuthFlowHelper.NewPkcePair();
        var code1 = await OAuthFlowHelper.AuthorizeAndConsentAsync(client, OAuthFlowHelper.AuthorizeUrl(resource, c1));
        await OAuthFlowHelper.ExchangeCodeAsync(fixture.Factory.CreateClient(), code1, v1, resource);
        var (v2, c2) = OAuthFlowHelper.NewPkcePair();
        var code2 = await OAuthFlowHelper.AuthorizeAndConsentAsync(client, OAuthFlowHelper.AuthorizeUrl(resource, c2));
        await OAuthFlowHelper.ExchangeCodeAsync(fixture.Factory.CreateClient(), code2, v2, resource);

        using var scope = fixture.Services.CreateScope();
        var authorizations = scope.ServiceProvider.GetRequiredService<IOpenIddictAuthorizationManager>();
        var subject = seeded.UserId.Value.ToString("N");
        var count = 0;
        await foreach (var authorization in authorizations.FindBySubjectAsync(subject))
        {
            if (await authorizations.GetTypeAsync(authorization) == AuthorizationTypes.Permanent
                && await authorizations.GetStatusAsync(authorization) == Statuses.Valid)
                count++;
        }
        Assert.Equal(1, count); // find-or-create per (subject, client, Space) — no duplicates
    }
}
