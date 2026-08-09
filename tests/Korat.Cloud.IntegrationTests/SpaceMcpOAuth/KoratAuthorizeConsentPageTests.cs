using System.Net;
using Korat.Cloud.Web.Auth;
using Korat.Cloud.Web.Auth.Security;
using Korat.Domain.Auth;
using Korat.Persistence;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Korat.Cloud.IntegrationTests.SpaceMcpOAuth;

/// <summary>
/// Space-MCP inc-2a, Task 3 (spec §Pillar C "Consent"): /connect/authorize GET —
/// cookie-session-only auth, korat:mcp-only scope policy (SF-7), exactly-one-per-Space
/// resource (RFC 8707), owner-owns-Space enforcement, and the consent page itself.
///
/// Also covers MF-3 (plan-review correction): IsSafeReturnUrl must allow
/// /connect/authorize as a returnUrl prefix, or an unauthenticated owner bounces to
/// /app/ after signin instead of back to consent, and the OAuth flow can never
/// complete. <see cref="Unauthenticated_SigninReturnUrl_RoundTrips_BackToAuthorize"/>
/// drives the RETURN leg (signin → back to /connect/authorize), not just the
/// outbound redirect covered by <see cref="Unauthenticated_RedirectsToSignin_WithReturnUrl"/>.
/// </summary>
[Trait("Category", "SpaceMcpOAuth")]
public sealed class KoratAuthorizeConsentPageTests(KoratIntegrationFixture fixture) : IClassFixture<KoratIntegrationFixture>
{
    private const string RedirectUri = "http://127.0.0.1:45123/callback";
    // Any well-formed S256 challenge works for the GET page (verifier only matters at exchange).
    private const string Challenge = "E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM";

    private static string AuthorizeUrl(string resource, string scope = "korat:mcp") =>
        "/connect/authorize?response_type=code" +
        $"&client_id=korat-mcp" +
        $"&redirect_uri={Uri.EscapeDataString(RedirectUri)}" +
        $"&scope={Uri.EscapeDataString(scope)}" +
        $"&resource={Uri.EscapeDataString(resource)}" +
        $"&code_challenge={Challenge}&code_challenge_method=S256&state=st-123";

    [Fact]
    public async Task Unauthenticated_RedirectsToSignin_WithReturnUrl()
    {
        await fixture.EnsureOAuthClientAsync(RedirectUri);
        var seeded = await fixture.SeedUserAsync($"consent-anon-{Guid.NewGuid():N}@example.com", "Consent Anon");
        var client = fixture.Factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
        });

        var response = await client.GetAsync(AuthorizeUrl($"http://localhost/mcp/{seeded.SpaceId}"));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var location = response.Headers.Location!.ToString();
        Assert.StartsWith("/app/signin?returnUrl=", location);
        Assert.Contains(Uri.EscapeDataString("/connect/authorize"), location);
    }

    /// <summary>
    /// MF-3 return leg. The outbound redirect (asserted above) is only half the fix —
    /// <see cref="IsSafeReturnUrl"/> must ALSO accept the encoded returnUrl it produced,
    /// or <see cref="CanonicalSigninHandler.CompleteAsync"/> silently falls back to
    /// "/app/" (CanonicalSigninHandler.cs:136) once the owner finishes signing in, and the
    /// consent page — and the whole OAuth flow — is never reached. Drives
    /// CanonicalSigninHandler directly (the same technique as
    /// Auth/AuthProviderLinkTests.cs) to simulate "signin completes" without a real
    /// external IdP round-trip.
    /// </summary>
    [Fact]
    public async Task Unauthenticated_SigninReturnUrl_RoundTrips_BackToAuthorize()
    {
        await fixture.EnsureOAuthClientAsync(RedirectUri);
        var seeded = await fixture.SeedUserAsync($"consent-return-{Guid.NewGuid():N}@example.com", "Consent Return");
        var anonClient = fixture.Factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
        });

        // Outbound leg: unauthenticated GET /connect/authorize → redirect to signin carrying
        // the current /connect/authorize?... request as returnUrl.
        var authorizePath = AuthorizeUrl($"http://localhost/mcp/{seeded.SpaceId}");
        var outbound = await anonClient.GetAsync(authorizePath);
        Assert.Equal(HttpStatusCode.Redirect, outbound.StatusCode);
        var signinLocation = outbound.Headers.Location!.ToString();
        const string signinPrefix = "/app/signin?returnUrl=";
        Assert.StartsWith(signinPrefix, signinLocation);
        var returnUrl = Uri.UnescapeDataString(signinLocation[signinPrefix.Length..]);
        Assert.Equal(authorizePath, returnUrl);

        // MF-3: IsSafeReturnUrl must actually allow this value through — else the return leg
        // below can never land back on /connect/authorize.
        Assert.True(IsSafeReturnUrl.Check(returnUrl),
            "MF-3: /connect/authorize must be an allowed IsSafeReturnUrl prefix.");

        // Return leg: the owner completes signin (existing ExternalLogin — "returning user"
        // branch of CanonicalSigninHandler.CompleteAsync) with that returnUrl. Must land back
        // on /connect/authorize, NOT the /app/ fallback.
        using var scope = fixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<KoratDbContext>();
        var providerUserId = $"mf3-return-{Guid.NewGuid():N}";
        db.ExternalLogins.Add(new ExternalLogin
        {
            Id = Guid.NewGuid(),
            UserId = seeded.UserId,
            Provider = LoginProvider.Google,
            ProviderUserId = providerUserId,
            EmailAtLink = "consent-return@example.com",
            EmailVerified = true,
            LinkedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();

        var handler = scope.ServiceProvider.GetRequiredService<CanonicalSigninHandler>();
        var ctx = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
        var result = await handler.CompleteAsync(
            ctx,
            new CanonicalSigninRequest(
                Provider: LoginProvider.Google,
                ProviderUserId: providerUserId,
                Email: "consent-return@example.com",
                EmailVerified: true,
                DisplayName: "Consent Return",
                ReturnUrl: returnUrl),
            default);

        var redirect = Assert.IsType<Microsoft.AspNetCore.Http.HttpResults.RedirectHttpResult>(result);
        Assert.Equal(returnUrl, redirect.Url);
        Assert.NotEqual("/app/", redirect.Url);

        // The ephemeral intermediate cookie is CLEARED once the durable session exists — a delete
        // (expired) Set-Cookie for its name. Prevents it lingering (up to 10 min) as a stale
        // ctx.User that would bind antiforgery tokens (the first-run multi-click-Allow class).
        Assert.Contains(ctx.Response.Headers.SetCookie, c => c is not null
            && c.Contains(CanonicalSigninHandler.IntermediateSessionCookieName, StringComparison.Ordinal)
            && (c.Contains("expires=Thu, 01 Jan 1970", StringComparison.OrdinalIgnoreCase)
                || c.Contains("max-age=0", StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public async Task IdentityScopeRequested_IsRejected_NeverRendersConsent()
    {
        await fixture.EnsureOAuthClientAsync(RedirectUri);
        var seeded = await fixture.SeedUserAsync($"consent-scope-{Guid.NewGuid():N}@example.com", "Consent Scope");
        var client = await fixture.CreateAuthenticatedNoRedirectClientAsync(seeded.UserId);

        // "openid korat:mcp" — the identity scope must be rejected for the MCP client (SF-7).
        // Depending on layer it dies at (client scope permissions vs our handler), the error
        // surfaces as an OpenIddict error redirect back to redirect_uri — never a consent page,
        // never a code.
        var response = await client.GetAsync(AuthorizeUrl($"http://localhost/mcp/{seeded.SpaceId}", scope: "openid korat:mcp"));

        Assert.True(
            response.StatusCode is HttpStatusCode.Redirect or HttpStatusCode.Found or HttpStatusCode.BadRequest,
            $"expected an OAuth error response, got {(int)response.StatusCode}");
        if (response.Headers.Location is { } location)
        {
            Assert.StartsWith(RedirectUri, location.ToString());
            Assert.Contains("error=", location.ToString());
            Assert.DoesNotContain("code=", location.ToString());
        }
    }

    [Fact]
    public async Task NonOwner_IsRejected_AccessDenied()
    {
        await fixture.EnsureOAuthClientAsync(RedirectUri);
        var ownerA = await fixture.SeedUserAsync($"consent-a-{Guid.NewGuid():N}@example.com", "Consent A");
        var ownerB = await fixture.SeedUserAsync($"consent-b-{Guid.NewGuid():N}@example.com", "Consent B");
        // Signed in as B, consenting to A's Space — must be refused (owner-owns-Space, BLOCKER-1's consent half).
        var client = await fixture.CreateAuthenticatedNoRedirectClientAsync(ownerB.UserId);

        var response = await client.GetAsync(AuthorizeUrl($"http://localhost/mcp/{ownerA.SpaceId}"));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var location = response.Headers.Location!.ToString();
        Assert.StartsWith(RedirectUri, location);
        Assert.Contains("error=access_denied", location);
        Assert.DoesNotContain("code=", location);
    }

    [Fact]
    public async Task ForeignOriginResource_IsRejected_InvalidTarget()
    {
        await fixture.EnsureOAuthClientAsync(RedirectUri);
        var seeded = await fixture.SeedUserAsync($"consent-forig-{Guid.NewGuid():N}@example.com", "Consent Foreign");
        var client = await fixture.CreateAuthenticatedNoRedirectClientAsync(seeded.UserId);

        var response = await client.GetAsync(AuthorizeUrl($"https://evil.example/mcp/{seeded.SpaceId}"));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var location = response.Headers.Location!.ToString();
        Assert.Contains("error=invalid_target", location);
    }

    [Fact]
    public async Task OwnerWithValidRequest_GetsConsentPage()
    {
        await fixture.EnsureOAuthClientAsync(RedirectUri);
        var seeded = await fixture.SeedUserAsync($"consent-ok-{Guid.NewGuid():N}@example.com", "Consent OK");
        var client = await fixture.CreateAuthenticatedNoRedirectClientAsync(seeded.UserId);

        var response = await client.GetAsync(AuthorizeUrl($"http://localhost/mcp/{seeded.SpaceId}"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.StartsWith("text/html", response.Content.Headers.ContentType!.ToString());
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("Korat MCP client", html);                       // client display name
        Assert.Contains("__RequestVerificationToken", html);             // antiforgery field
        Assert.Contains("korat:mcp", html);                              // scope shown to the owner
        Assert.Contains($"name=\"resource\"", html);                     // original params re-emitted as hidden inputs
    }

    [Fact]
    public async Task OwnerWithOfflineAccessScope_GetsConsentPage()
    {
        // The real MCP OAuth clients (Claude Code, Cursor — both on the MCP TS SDK) auto-append
        // offline_access to the authorize request for a refresh token. It MUST be accepted
        // alongside korat:mcp (we grant offline_access server-side anyway, MF-2) — capstone
        // interop fix. Identity scopes (openid/…) stay rejected: see IdentityScopeRequested_*.
        await fixture.EnsureOAuthClientAsync(RedirectUri);
        var seeded = await fixture.SeedUserAsync($"consent-offline-{Guid.NewGuid():N}@example.com", "Consent Offline");
        var client = await fixture.CreateAuthenticatedNoRedirectClientAsync(seeded.UserId);

        var response = await client.GetAsync(
            AuthorizeUrl($"http://localhost/mcp/{seeded.SpaceId}", scope: "korat:mcp offline_access"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.StartsWith("text/html", response.Content.Headers.ContentType!.ToString());
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("korat:mcp", html);
    }

    /// <summary>
    /// Consent-UX fix: an antiforgery failure on the consent POST must NOT dead-end with silent
    /// JSON (the multi-click-Allow bug: SameSite=Strict withheld the xsrf cookie on the
    /// externally-initiated consent navigation, rotating the token per attempt). It now SELF-HEALS
    /// — a 302 back to a fresh GET /connect/authorize — with a one-shot `consent_retry` guard so a
    /// genuinely broken cookie jar (retry still fails) dead-ends with 400 instead of looping.
    /// </summary>
    [Fact]
    public async Task ConsentPost_AntiforgeryFailure_SelfHealsOnce_ThenOneShot400()
    {
        await fixture.EnsureOAuthClientAsync(RedirectUri);
        var seeded = await fixture.SeedUserAsync($"consent-selfheal-{Guid.NewGuid():N}@example.com", "Consent SelfHeal");
        var client = await fixture.CreateAuthenticatedNoRedirectClientAsync(seeded.UserId);
        // RelaySession cookie only (NO __Secure-korat_xsrf) → antiforgery must fail. Per-request Cookie
        // header overrides DefaultRequestHeaders, so we carry the session cookie explicitly.
        var sessionCookie = client.DefaultRequestHeaders.GetValues("Cookie").First();

        var fields = new List<KeyValuePair<string, string>>
        {
            new("response_type", "code"),
            new("client_id", "korat-mcp"),
            new("redirect_uri", RedirectUri),
            new("scope", "korat:mcp"),
            new("resource", $"http://localhost/mcp/{seeded.SpaceId}"),
            new("code_challenge", Challenge),
            new("code_challenge_method", "S256"),
            new("state", "st-123"),
            new("submit", "allow"),
        };

        var post1 = new HttpRequestMessage(HttpMethod.Post, "/connect/authorize")
        { Content = new FormUrlEncodedContent(fields) };
        post1.Headers.Add("Cookie", sessionCookie);
        var resp1 = await client.SendAsync(post1);

        // Self-heal, NOT a dead-end 400 and NOT a code redirect to the client.
        Assert.True(resp1.StatusCode is HttpStatusCode.Redirect or HttpStatusCode.Found,
            $"expected a self-heal 302, got {(int)resp1.StatusCode}");
        var loc = resp1.Headers.Location!.ToString();
        Assert.StartsWith("/connect/authorize", loc);
        Assert.Contains("consent_retry=1", loc);

        // Second attempt already carrying the marker → one-shot guard (key-existence, so a seeded
        // value can't defeat it) → 400 (no infinite loop).
        fields.Add(new("consent_retry", "1"));
        var post2 = new HttpRequestMessage(HttpMethod.Post, "/connect/authorize")
        { Content = new FormUrlEncodedContent(fields) };
        post2.Headers.Add("Cookie", sessionCookie);
        var resp2 = await client.SendAsync(post2);

        Assert.Equal(HttpStatusCode.BadRequest, resp2.StatusCode);

        // Healing follow-through: the self-heal Location must render a VALID consent page whose
        // proper (antiforgery'd) POST issues the auth code — i.e. the heal RECOVERS the flow, it
        // doesn't merely bounce (the healed GET re-runs OpenIddict + ValidateAsync + mints a fresh
        // antiforgery pair; the extra consent_retry param is ignored). Drive it end-to-end.
        var code = await OAuthFlowHelper.AuthorizeAndConsentAsync(client, loc);
        Assert.False(string.IsNullOrEmpty(code), "the healed consent page must complete to an auth code");
    }
}
