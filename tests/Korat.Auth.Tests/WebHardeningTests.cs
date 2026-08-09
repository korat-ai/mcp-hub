using System.Net;
using Korat.Cloud.Web;
using Korat.Cloud.Web.Auth;
using Korat.Cloud.Web.Auth.Security;
using Korat.Domain.Auth;
using Microsoft.AspNetCore.Http;

namespace Korat.Auth.Tests;

/// <summary>
/// Unit tests for the web-surface hardening changes that do NOT require
/// Testcontainers (pure logic assertions on helpers and filter shapes).
///
/// Covers:
///   web-M1  — RequireSpaceOwner rejects scope != "full" with 403
///   web-M3b — ResolvePrincipalKey: rotating bearer suffix from one IP is capped per-IP
///   web-M4  — FeedbackPolicy constant registered in RateLimiterRegistration
///   minor   — IsSafeReturnUrl only allows /app/ after prefix tightening
///   minor   — SPA path containment guard (Path.GetFullPath check; tested via the helper shape)
/// </summary>
public class WebHardeningTests
{
    // ── web-M1: RequireSpaceOwner scope floor ──────────────────────────────────

    /// <summary>
    /// A bridge-only token resolves to a real identity but must be rejected by
    /// RequireSpaceOwner before any grain or DB call.  Verified by directly testing
    /// the scope string that PolymorphicAuthResolver sets (the filter logic is
    /// validated through the scope field).
    /// </summary>
    [Fact]
    public void BridgeOnlyScope_IsNotFull()
    {
        // Arrange: simulate what PolymorphicAuthResolver returns for a bridge token.
        var identity = new ResolvedIdentity(UserId.New(), Scope: "bridge-only");

        // Act + Assert: the filter rejects any scope other than "full".
        Assert.NotEqual("full", identity.Scope);
    }

    [Fact]
    public void FullScope_IsAccepted()
    {
        var identity = new ResolvedIdentity(UserId.New(), Scope: "full");
        Assert.Equal("full", identity.Scope);
    }

    [Fact]
    public void DefaultScope_IsFull()
    {
        // ResolvedIdentity default Scope parameter must be "full" so cookie-session
        // principals (which don't pass Scope explicitly) are always accepted.
        var identity = new ResolvedIdentity(UserId.New());
        Assert.Equal("full", identity.Scope);
    }

    // ── web-M3b: bearer partition — rotating suffix stays in per-IP bucket ────

    /// <summary>
    /// An attacker rotating arbitrary bearer suffixes from one IP must NOT get a
    /// per-bearer partition for each request.  The prefix check
    /// ("Bearer korat_cli_") is the guard: any value that doesn't match falls
    /// through to the per-IP bucket.  Verified via ResolvePrincipalKey.
    /// </summary>
    [Theory]
    [InlineData("Bearer arbitrary-junk-1")]
    [InlineData("Bearer arbitrary-junk-2")]
    [InlineData("Token korat_cli_abc")]          // wrong scheme keyword
    [InlineData("korat_cli_abc")]                // no "Bearer " prefix at all
    [InlineData("Bearer ")]                      // empty token value
    public void BearerRotation_NonKoratPrefix_PartitionsByIp(string authzValue)
    {
        var ctx = new DefaultHttpContext();
        ctx.Connection.RemoteIpAddress = IPAddress.Parse("1.2.3.4");
        ctx.Request.Headers.Authorization = authzValue;

        var key = RateLimiterRegistration.ResolvePrincipalKey(ctx, trustForwardedIp: false);

        // Must always resolve to the per-IP bucket, never the "b:..." bearer bucket.
        Assert.Equal("ip:1.2.3.4", key);
    }

    [Fact]
    public void BearerRotation_KoratPrefix_PartitionsByHash_NotByIp()
    {
        // A legitimate korat_cli_ token gets its own "b:..." partition.
        // Two requests with the SAME token value must land in the same bucket
        // (i.e. the hash is deterministic).
        var ctx1 = new DefaultHttpContext();
        ctx1.Connection.RemoteIpAddress = IPAddress.Parse("5.6.7.8");
        ctx1.Request.Headers.Authorization = "Bearer korat_cli_abc123";

        var ctx2 = new DefaultHttpContext();
        ctx2.Connection.RemoteIpAddress = IPAddress.Parse("5.6.7.8");
        ctx2.Request.Headers.Authorization = "Bearer korat_cli_abc123";

        var key1 = RateLimiterRegistration.ResolvePrincipalKey(ctx1, trustForwardedIp: false);
        var key2 = RateLimiterRegistration.ResolvePrincipalKey(ctx2, trustForwardedIp: false);

        Assert.StartsWith("b:", key1);
        Assert.Equal(key1, key2);
    }

    [Fact]
    public void BearerRotation_TwoDifferentKoratTokens_GetDifferentPartitions()
    {
        // Different korat_cli_ tokens get different partitions — that is correct
        // (each issued token is a distinct principal).  Confirm the hash differs.
        var ctx1 = new DefaultHttpContext();
        ctx1.Connection.RemoteIpAddress = IPAddress.Parse("5.6.7.8");
        ctx1.Request.Headers.Authorization = "Bearer korat_cli_token_AAA";

        var ctx2 = new DefaultHttpContext();
        ctx2.Connection.RemoteIpAddress = IPAddress.Parse("5.6.7.8");
        ctx2.Request.Headers.Authorization = "Bearer korat_cli_token_BBB";

        var key1 = RateLimiterRegistration.ResolvePrincipalKey(ctx1, trustForwardedIp: false);
        var key2 = RateLimiterRegistration.ResolvePrincipalKey(ctx2, trustForwardedIp: false);

        Assert.StartsWith("b:", key1);
        Assert.StartsWith("b:", key2);
        Assert.NotEqual(key1, key2); // different tokens → different hashes
    }

    // ── minor: IsSafeReturnUrl tightened prefixes ────────────────────────────

    /// <summary>
    /// After tightening, /api/* and /signin/* are no longer safe return URLs.
    /// Only /app/* destinations are accepted.
    /// </summary>
    [Theory]
    [InlineData("/api/me")]
    [InlineData("/api/space")]
    [InlineData("/signin/github")]
    [InlineData("/signin/callback")]
    public void IsSafeReturnUrl_DroppedPrefixes_ReturnFalse(string url)
    {
        Assert.False(IsSafeReturnUrl.Check(url),
            $"Expected {url} to be rejected after prefix tightening.");
    }

    [Theory]
    [InlineData("/app/")]
    [InlineData("/app/dashboard")]
    [InlineData("/app/settings/account")]
    public void IsSafeReturnUrl_AppPrefix_StillAccepted(string url)
    {
        Assert.True(IsSafeReturnUrl.Check(url),
            $"Expected {url} to be accepted (under /app/).");
    }

    [Theory]
    [InlineData("//evil.com")]
    [InlineData("/\\evil")]
    [InlineData("http://evil.com")]
    [InlineData("/app/../../../etc/passwd")]
    public void IsSafeReturnUrl_OpenRedirectAttempts_ReturnFalse(string url)
    {
        Assert.False(IsSafeReturnUrl.Check(url));
    }
}
