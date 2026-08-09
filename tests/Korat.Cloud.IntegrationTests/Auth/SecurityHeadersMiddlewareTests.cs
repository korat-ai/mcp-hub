using System.Net;

namespace Korat.Cloud.IntegrationTests.Auth;

/// <summary>
/// Integration tests for <c>SecurityHeadersMiddleware.UseKoratSecurityHeaders</c>.
///
/// Verifies that the baseline security response headers are emitted on every response
/// with the expected values. Uses the standard KoratTestHost (Testing environment) so
/// the full pipeline runs including the security-headers middleware registered in Program.cs.
/// </summary>
public sealed class SecurityHeadersMiddlewareTests(KoratIntegrationFixture fixture)
    : IClassFixture<KoratIntegrationFixture>
{
    /// <summary>
    /// Sends a GET to a well-known endpoint that always responds (even if 401/403/404)
    /// and returns the headers. Security headers must be present on every response.
    /// </summary>
    private async Task<System.Net.Http.Headers.HttpResponseHeaders> GetResponseHeadersAsync()
    {
        // GET /api/version requires auth but still emits security headers (middleware runs first).
        var client = fixture.Factory.CreateClient();
        var response = await client.GetAsync("/api/version");
        // We expect 401 (unauthenticated), but headers must still be present.
        return response.Headers;
    }

    [Fact]
    public async Task Response_Has_XContentTypeOptions_NoSniff()
    {
        var headers = await GetResponseHeadersAsync();
        Assert.True(headers.TryGetValues("X-Content-Type-Options", out var values));
        Assert.Equal("nosniff", values!.First());
    }

    [Fact]
    public async Task Response_Has_XFrameOptions_DENY()
    {
        var headers = await GetResponseHeadersAsync();
        Assert.True(headers.TryGetValues("X-Frame-Options", out var values));
        Assert.Equal("DENY", values!.First());
    }

    [Fact]
    public async Task Response_Has_ReferrerPolicy()
    {
        var headers = await GetResponseHeadersAsync();
        Assert.True(headers.TryGetValues("Referrer-Policy", out var values));
        Assert.Equal("strict-origin-when-cross-origin", values!.First());
    }

    [Fact]
    public async Task Response_Has_ContentSecurityPolicy()
    {
        var headers = await GetResponseHeadersAsync();
        Assert.True(headers.TryGetValues("Content-Security-Policy", out var values));
        var csp = values!.First();
        // Verify key CSP directives are present.
        Assert.Contains("default-src 'self'", csp, StringComparison.Ordinal);
        // External telemetry is opt-in; same-origin API calls remain available by default.
        Assert.Contains("connect-src 'self'", csp, StringComparison.Ordinal);
        Assert.DoesNotContain("telemetry.example.com", csp, StringComparison.Ordinal);
        Assert.Contains("object-src 'none'", csp, StringComparison.Ordinal);
        Assert.Contains("frame-ancestors 'none'", csp, StringComparison.Ordinal);
        Assert.Contains("form-action 'self'", csp, StringComparison.Ordinal);
        Assert.Contains("base-uri 'self'", csp, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("https://telemetry.example.com", null, "https://telemetry.example.com")]
    [InlineData(null, "https://public-key@telemetry.example.com/42", "https://telemetry.example.com")]
    [InlineData("http://telemetry.example.com", null, null)]
    [InlineData("not-a-uri", null, null)]
    public void TelemetryOrigin_IsExplicitAndHttpsOnly(
        string? explicitOrigin,
        string? sentryDsn,
        string? expected)
    {
        Assert.Equal(
            expected,
            Korat.Cloud.Web.SecurityHeadersMiddleware.ResolveTelemetryOrigin(explicitOrigin, sentryDsn));
    }

    [Fact]
    public async Task Response_Has_StrictTransportSecurity()
    {
        var headers = await GetResponseHeadersAsync();
        Assert.True(headers.TryGetValues("Strict-Transport-Security", out var values));
        var hsts = values!.First();
        Assert.Contains("max-age=", hsts, StringComparison.Ordinal);
        Assert.Contains("includeSubDomains", hsts, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Response_Has_PermissionsPolicy()
    {
        var headers = await GetResponseHeadersAsync();
        Assert.True(headers.TryGetValues("Permissions-Policy", out var values));
        var policy = values!.First();
        // Camera and microphone should be disabled.
        Assert.Contains("camera=()", policy, StringComparison.Ordinal);
        Assert.Contains("microphone=()", policy, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ConsentAuthorizePath_Csp_WidensFormAction_ForCallbacks()
    {
        // The OAuth consent page's form-action must allow the client's loopback + https callback,
        // or Chrome blocks the post-consent 302 to http://localhost:<port>/callback (the
        // "click Allow several times" bug). The middleware runs before OpenIddict, so the header
        // is present regardless of the (here deliberately minimal) authorize request's validity.
        var client = fixture.Factory.CreateClient(
            new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var response = await client.GetAsync(
            "/connect/authorize?response_type=code&client_id=x&redirect_uri=http://127.0.0.1:5000/cb&scope=korat:mcp");

        var csp = response.Headers.GetValues("Content-Security-Policy").First();
        // The callback schemes real clients actually use — loopback IPv4/localhost + https —
        // must be present so Chrome permits the post-consent 302 to the client's callback.
        Assert.Contains("form-action 'self' http://127.0.0.1:* http://localhost:* http://[::1]:* https:",
            csp, StringComparison.Ordinal);
        // NB: the http://[::1]:* token is emitted but is INERT in browsers — CSP3 host-sources
        // cannot express an IPv6 literal, so Chrome drops it. This asserts what the header CONTAINS,
        // not that an [::1] callback is actually enforceable (it isn't). See OAuthFormAction.
    }

    [Fact]
    public async Task NonConsentPath_Csp_KeepsStrictFormAction()
    {
        var headers = await GetResponseHeadersAsync(); // /api/version
        var csp = headers.GetValues("Content-Security-Policy").First();
        // Non-consent paths must carry the strict policy: form-action is exactly 'self', and it is
        // the LAST directive. EndsWith is robust to any
        // future re-ordering of the widened schemes — no callback source may leak onto this path.
        Assert.EndsWith("form-action 'self'", csp, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SecurityHeaders_PresentOn_UnauthenticatedRoute()
    {
        // The magic-link page is publicly accessible and goes through the full pipeline,
        // so security headers must be present even on non-auth-required endpoints.
        // (Any route works — we just need to prove the middleware fires universally.)
        var client = fixture.Factory.CreateClient();
        var response = await client.GetAsync("/api/auth/me");
        // 401 Unauthorized — but security headers still run (middleware is before auth).
        Assert.True(response.Headers.TryGetValues("X-Content-Type-Options", out var values));
        Assert.Equal("nosniff", values!.First());
    }
}
