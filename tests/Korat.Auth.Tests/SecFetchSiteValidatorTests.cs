using Korat.Cloud.Web.Auth.Security;
using Microsoft.AspNetCore.Http;

namespace Korat.Auth.Tests;

/// <summary>
/// Unit tests for <see cref="SecFetchSiteValidator.IsLegitimateCallback"/>.
///
/// Design rationale: OAuth callbacks from a real IdP arrive cross-site (the browser
/// navigates from github.com / accounts.google.com to our /finish path).
/// A same-origin or same-site Sec-Fetch-Site value indicates the request was initiated
/// from our own origin, which is suspicious for a top-level IdP redirect and should
/// be rejected. Absent headers (pre-Fetch-Metadata browsers) are accepted as legitimate.
/// </summary>
public class SecFetchSiteValidatorTests
{
    private static HttpContext CtxWithSecFetchSite(string? value)
    {
        var ctx = new DefaultHttpContext();
        if (value is not null)
            ctx.Request.Headers["Sec-Fetch-Site"] = value;
        return ctx;
    }

    // ── Cross-site: real IdP redirect ─────────────────────────────────────────

    [Fact]
    public void CrossSite_IsLegitimateCallback_ReturnsTrue()
    {
        // A genuine OAuth callback from GitHub / Google arrives with Sec-Fetch-Site: cross-site.
        var ctx = CtxWithSecFetchSite("cross-site");
        Assert.True(SecFetchSiteValidator.IsLegitimateCallback(ctx));
    }

    // ── Absent / empty header: legacy browsers ────────────────────────────────

    [Fact]
    public void HeaderAbsent_IsLegitimateCallback_ReturnsTrue()
    {
        // Pre-Fetch-Metadata browsers do not send Sec-Fetch-Site; we must not block them.
        var ctx = CtxWithSecFetchSite(null);
        Assert.True(SecFetchSiteValidator.IsLegitimateCallback(ctx));
    }

    [Fact]
    public void EmptyHeader_IsLegitimateCallback_ReturnsTrue()
    {
        // An empty string is treated the same as absent (legacy-browser allowance).
        var ctx = CtxWithSecFetchSite("");
        Assert.True(SecFetchSiteValidator.IsLegitimateCallback(ctx));
    }

    // ── Same-origin: suspect CSRF attempt ─────────────────────────────────────

    [Fact]
    public void SameOrigin_IsLegitimateCallback_ReturnsFalse()
    {
        // A same-origin request to /finish cannot be a real IdP redirect — reject.
        var ctx = CtxWithSecFetchSite("same-origin");
        Assert.False(SecFetchSiteValidator.IsLegitimateCallback(ctx));
    }

    // ── Same-site: also suspect ───────────────────────────────────────────────

    [Fact]
    public void SameSite_IsLegitimateCallback_ReturnsFalse()
    {
        // same-site means the request came from our own site (a subdomain, e.g.),
        // which should not be the source of a real IdP callback — reject.
        var ctx = CtxWithSecFetchSite("same-site");
        Assert.False(SecFetchSiteValidator.IsLegitimateCallback(ctx));
    }

    // ── Edge: "none" value (used for no-cors cross-origin fetches) ────────────

    [Fact]
    public void NoneValue_IsLegitimateCallback_ReturnsFalse()
    {
        // Sec-Fetch-Site: none indicates an embedded/fetched resource, not a top-level
        // browser navigation from an IdP — reject to be safe.
        var ctx = CtxWithSecFetchSite("none");
        Assert.False(SecFetchSiteValidator.IsLegitimateCallback(ctx));
    }
}
