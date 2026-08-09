using Korat.Cloud.Web.Auth.Security;
using Microsoft.AspNetCore.Http;

namespace Korat.Auth.Tests;

/// <summary>
/// Tokens from the sign-in provider get their own counter, like this app's own credential does.
///
/// They carry no prefix, so before this they fell into the shared IP bucket: several agents
/// behind one NAT would share a limit designed to be per-credential. Nobody chose that — it is
/// what happened when the credential changed shape and this function was not told.
/// </summary>
public sealed class RateLimitJwtPartitionTests
{
    private const string Jwt =
        "eyJhbGciOiJSUzI1NiIsImtpZCI6IlhUSFNVVk8xRFFSSyJ9.eyJzdWIiOiI5ZmRjNzM5MyJ9.c2lnbmF0dXJl";

    private static HttpContext WithAuthorization(string? value)
    {
        var ctx = new DefaultHttpContext();
        ctx.Connection.RemoteIpAddress = System.Net.IPAddress.Parse("10.0.0.9");
        if (value is not null) ctx.Request.Headers.Authorization = value;
        return ctx;
    }

    [Fact]
    public void A_jwt_bearer_gets_its_own_partition()
    {
        var key = RateLimiterRegistration.ResolvePrincipalKey(
            WithAuthorization($"Bearer {Jwt}"), trustForwardedIp: false);

        Assert.StartsWith("b:", key);
        // The raw token never becomes the key — it is hashed, like the CLI credential is.
        Assert.DoesNotContain(Jwt, key);
    }

    [Fact]
    public void Two_different_tokens_do_not_share_a_counter()
    {
        var first = RateLimiterRegistration.ResolvePrincipalKey(
            WithAuthorization($"Bearer {Jwt}"), trustForwardedIp: false);
        var second = RateLimiterRegistration.ResolvePrincipalKey(
            WithAuthorization($"Bearer {Jwt}x"), trustForwardedIp: false);

        Assert.NotEqual(first, second);
    }

    [Theory]
    [InlineData("Bearer not-a-jwt")]                    // no dots
    [InlineData("Bearer only.two")]                     // one dot
    [InlineData("Bearer a..c")]                         // empty middle segment
    [InlineData("Bearer .b.c")]                         // empty first segment
    [InlineData("Bearer a.b.c")]                        // too short to be a token
    [InlineData("Bearer aaaa.bbbb.cc/cc+dd=ee+ffffff")] // characters outside base64url
    public void Junk_falls_back_to_the_ip_bucket(string authorization)
    {
        // The same reason the cookie and the CLI prefix are shape-checked: an unauthenticated
        // flood with varying Authorization headers must not mint an unbounded number of
        // in-memory partitions.
        var key = RateLimiterRegistration.ResolvePrincipalKey(
            WithAuthorization(authorization), trustForwardedIp: false);

        Assert.Equal("ip:10.0.0.9", key);
    }

    [Fact]
    public void Our_own_credential_still_partitions_as_before()
    {
        var key = RateLimiterRegistration.ResolvePrincipalKey(
            WithAuthorization("Bearer korat_cli_abcdef0123456789"), trustForwardedIp: false);

        Assert.StartsWith("b:", key);
    }
}
