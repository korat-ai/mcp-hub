using System.Net;
using Korat.Cloud.Web.Oauth;
using Microsoft.Extensions.DependencyInjection;
using OpenIddict.Abstractions;
using OpenIddict.Validation;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Korat.Cloud.IntegrationTests.SpaceMcpOAuth;

/// <summary>
/// Space-MCP inc-2a, Task 5 (SF-7 "AllowRefreshTokenFlow with rotation"): refresh tokens
/// rotate on every use (rolling — OpenIddict default), the rotated-out token is dead
/// immediately (zero reuse leeway, Task 1), and a detected reuse revokes the whole chain.
/// </summary>
[Trait("Category", "SpaceMcpOAuth")]
public sealed class OAuthRefreshRotationTests(KoratIntegrationFixture fixture) : IClassFixture<KoratIntegrationFixture>
{
    private async Task<(string Resource, string AccessToken, string RefreshToken)> IssueAsync()
    {
        await fixture.EnsureOAuthClientAsync(OAuthFlowHelper.RedirectUri);
        var seeded = await fixture.SeedUserAsync($"refresh-{Guid.NewGuid():N}@example.com", "Refresh Owner");
        var resource = $"http://localhost/mcp/{seeded.SpaceId}";
        var client = await fixture.CreateAuthenticatedNoRedirectClientAsync(seeded.UserId);
        var (verifier, challenge) = OAuthFlowHelper.NewPkcePair();
        var code = await OAuthFlowHelper.AuthorizeAndConsentAsync(client, OAuthFlowHelper.AuthorizeUrl(resource, challenge));
        var tokens = await OAuthFlowHelper.ExchangeCodeAsync(fixture.Factory.CreateClient(), code, verifier, resource);
        return (resource, tokens["access_token"]!.GetValue<string>(), tokens["refresh_token"]!.GetValue<string>());
    }

    [Fact]
    public async Task Refresh_RotatesToken_NewAccessTokenKeepsPerSpaceAudience()
    {
        var (resource, at0, rt1) = await IssueAsync();
        var http = fixture.Factory.CreateClient();
        var validation = fixture.Services.GetRequiredService<OpenIddictValidationService>();

        // Baseline: the claims consent minted on the ORIGINAL access token — this is the
        // (client_id x ownerUserId x SpaceId) triple Task 6 will re-derive the durable
        // consumer identity from.
        var originalPrincipal = await validation.ValidateAccessTokenAsync(at0);
        var originalSpaceClaim = originalPrincipal.GetClaim(KoratOAuthConstants.SpaceClaim);
        var originalClientClaim = originalPrincipal.GetClaim(KoratOAuthConstants.ClientClaim);

        var (status, body) = await OAuthFlowHelper.RefreshAsync(http, rt1);

        Assert.Equal(HttpStatusCode.OK, status);
        var rt2 = body["refresh_token"]!.GetValue<string>();
        Assert.NotEqual(rt1, rt2); // rolling refresh tokens — rotated every use

        var principal = await validation.ValidateAccessTokenAsync(body["access_token"]!.GetValue<string>());
        Assert.Contains(resource, principal.GetAudiences());          // audience survives refresh
        Assert.True(principal.HasScope(KoratOAuthConstants.McpScope));
        // The durable consumer identity is UNCHANGED across rotation: the same (client_id x
        // owner x Space) triple comes back on the rotated access token, byte-identical to the
        // original — SF-7's point (the grant survives; rotation only swaps the token, not the
        // identity it represents).
        Assert.Equal(originalSpaceClaim, principal.GetClaim(KoratOAuthConstants.SpaceClaim));
        Assert.Equal(originalClientClaim, principal.GetClaim(KoratOAuthConstants.ClientClaim));

        // The new refresh token (rt2) is itself live and works for a SUBSEQUENT rotation —
        // rotation is a chain, not a one-shot swap.
        var (status2, body2) = await OAuthFlowHelper.RefreshAsync(http, rt2);
        Assert.Equal(HttpStatusCode.OK, status2);
        var rt3 = body2["refresh_token"]!.GetValue<string>();
        Assert.NotEqual(rt2, rt3);
        Assert.NotEqual(rt1, rt3);

        var principal2 = await validation.ValidateAccessTokenAsync(body2["access_token"]!.GetValue<string>());
        Assert.Contains(resource, principal2.GetAudiences());
        Assert.Equal(originalSpaceClaim, principal2.GetClaim(KoratOAuthConstants.SpaceClaim));
        Assert.Equal(originalClientClaim, principal2.GetClaim(KoratOAuthConstants.ClientClaim));
    }

    /// <summary>
    /// FIX C (holistic review, coverage nit): the PRIMARY reuse defense, split out so it runs in
    /// CI unskipped. Replaying a rotated-out refresh token must be rejected with
    /// <c>400 invalid_grant</c> — this passes on EF Core InMemory (the integration fixture's
    /// provider) because OpenIddict wraps its `RevokeByAuthorizationIdAsync` chain-revocation call
    /// in `catch when (!IsFatal)`, swallows InMemory's `ExecuteUpdateAsync`
    /// <see cref="InvalidOperationException"/>, and STILL executes the reject. Only the
    /// DESCENDANT chain-death assertion needs real Postgres — see
    /// <see cref="ReusedRefreshToken_RevokesDescendantChain"/>.
    /// </summary>
    [Fact]
    public async Task ReusedRefreshToken_IsRejected_InvalidGrant()
    {
        var (_, _, rt1) = await IssueAsync();
        var http = fixture.Factory.CreateClient();

        var (firstStatus, _) = await OAuthFlowHelper.RefreshAsync(http, rt1);
        Assert.Equal(HttpStatusCode.OK, firstStatus);

        // Replay the rotated-out rt1: zero leeway → invalid_grant.
        var (replayStatus, replayBody) = await OAuthFlowHelper.RefreshAsync(http, rt1);
        Assert.Equal(HttpStatusCode.BadRequest, replayStatus);
        Assert.Equal("invalid_grant", replayBody["error"]!.GetValue<string>());
    }

    /// <summary>
    /// FIX C (holistic review, coverage nit): the secondary hardening — reuse detection also
    /// revokes the DESCENDANT refresh token (rt2) that was rotated from the replayed rt1. This
    /// assertion alone needs Postgres — see <see cref="ReusedRefreshToken_IsRejected_InvalidGrant"/>
    /// for the primary (unskipped) reuse→invalid_grant defense.
    /// </summary>
    [Fact(Skip = "Chain revocation (RevokeByAuthorizationIdAsync) uses ExecuteUpdateAsync, unsupported by EF Core InMemory (the integration fixture's provider); OpenIddict 7.5.0 source confirms it IS called on reuse-detection; exercised on real Postgres via the dev deploy + T9/live-verify. See plan Known Limitations.")]
    public async Task ReusedRefreshToken_RevokesDescendantChain()
    {
        var (_, _, rt1) = await IssueAsync();
        var http = fixture.Factory.CreateClient();

        var (firstStatus, firstBody) = await OAuthFlowHelper.RefreshAsync(http, rt1);
        Assert.Equal(HttpStatusCode.OK, firstStatus);
        var rt2 = firstBody["refresh_token"]!.GetValue<string>();

        // Trigger reuse detection.
        var (replayStatus, _) = await OAuthFlowHelper.RefreshAsync(http, rt1);
        Assert.Equal(HttpStatusCode.BadRequest, replayStatus);

        // The DESCENDANT rt2 is dead too (reuse detection revokes the authorization's tokens).
        var (chainStatus, chainBody) = await OAuthFlowHelper.RefreshAsync(http, rt2);
        Assert.Equal(HttpStatusCode.BadRequest, chainStatus);
        Assert.Equal("invalid_grant", chainBody["error"]!.GetValue<string>());
    }
}
