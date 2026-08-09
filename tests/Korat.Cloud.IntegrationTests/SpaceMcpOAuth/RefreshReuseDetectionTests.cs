using System.Net;
using Korat.Cloud.Security.Audit;
using Korat.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Korat.Cloud.IntegrationTests.SpaceMcpOAuth;

/// <summary>
/// Р31: a refused refresh token must leave a trace.
///
/// <para>Rejection was already correct and already tested (<c>OAuthRefreshRotationTests</c>);
/// this file is about the part that was missing — the SIGNAL. The threat model states that a
/// credential on disk cannot be protected from other processes of the same OS user, so prevention
/// is not on the table. What is on the table is noticing: a stolen refresh token collides with the
/// legitimate client's rotation exactly once, and that collision is the only moment the theft is
/// observable at all.</para>
///
/// <para>The test asserts the audit ROW, not the log line. A log line is not a durable artefact —
/// it can be filtered out by configuration and cannot be queried after the fact, which is the one
/// thing an operator investigating a suspected theft actually needs to do.</para>
/// </summary>
public sealed class RefreshReuseDetectionTests(KoratIntegrationFixture fixture)
    : IClassFixture<KoratIntegrationFixture>
{
    [Fact]
    public async Task ReplayedRefreshToken_IsAudited()
    {
        await fixture.EnsureOAuthClientAsync(OAuthFlowHelper.RedirectUri);
        var seeded = await fixture.SeedUserAsync($"reuse-{Guid.NewGuid():N}@example.com", "Reuse Owner");

        var resource = $"http://localhost/mcp/{seeded.SpaceId}";
        var browser = await fixture.CreateAuthenticatedNoRedirectClientAsync(seeded.UserId);
        var (verifier, challenge) = OAuthFlowHelper.NewPkcePair();
        var code = await OAuthFlowHelper.AuthorizeAndConsentAsync(
            browser, OAuthFlowHelper.AuthorizeUrl(resource, challenge, "korat:mcp offline_access"));

        using var http = fixture.Factory.CreateClient();
        var tokens = await OAuthFlowHelper.ExchangeCodeAsync(http, code, verifier, resource);
        var refreshToken = tokens["refresh_token"]!.GetValue<string>();

        // First use rotates it.
        var (firstStatus, _) = await OAuthFlowHelper.RefreshAsync(http, refreshToken);
        Assert.Equal(HttpStatusCode.OK, firstStatus);

        var auditedBefore = await CountRefreshRejectionsAsync();

        // Second use of the SAME token — the shape of a stolen copy being redeemed.
        var (replayStatus, replayBody) = await OAuthFlowHelper.RefreshAsync(http, refreshToken);
        Assert.Equal(HttpStatusCode.BadRequest, replayStatus);
        Assert.Equal("invalid_grant", replayBody["error"]!.GetValue<string>());

        var auditedAfter = await CountRefreshRejectionsAsync();
        Assert.True(
            auditedAfter > auditedBefore,
            "a refused refresh token must leave an audit row — rejecting it silently means the one "
            + "moment a stolen credential is observable passes unrecorded.");
    }

    [Fact]
    public async Task SuccessfulRefresh_IsNotAudited()
    {
        // Without this, a detector that recorded EVERY refresh would satisfy the test above while
        // burying the real signal in noise — which is the same as having no signal.
        await fixture.EnsureOAuthClientAsync(OAuthFlowHelper.RedirectUri);
        var seeded = await fixture.SeedUserAsync($"reuse-ok-{Guid.NewGuid():N}@example.com", "Reuse Ok");

        var resource = $"http://localhost/mcp/{seeded.SpaceId}";
        var browser = await fixture.CreateAuthenticatedNoRedirectClientAsync(seeded.UserId);
        var (verifier, challenge) = OAuthFlowHelper.NewPkcePair();
        var code = await OAuthFlowHelper.AuthorizeAndConsentAsync(
            browser, OAuthFlowHelper.AuthorizeUrl(resource, challenge, "korat:mcp offline_access"));

        using var http = fixture.Factory.CreateClient();
        var tokens = await OAuthFlowHelper.ExchangeCodeAsync(http, code, verifier, resource);
        var refreshToken = tokens["refresh_token"]!.GetValue<string>();

        var before = await CountRefreshRejectionsAsync();
        var (status, _) = await OAuthFlowHelper.RefreshAsync(http, refreshToken);
        Assert.Equal(HttpStatusCode.OK, status);

        Assert.Equal(before, await CountRefreshRejectionsAsync());
    }

    [Fact]
    public async Task FailureOnADifferentGrantType_IsNotRecordedAsARefreshRejection()
    {
        // The grant-type filter is the one condition that actually discriminates here, so it is
        // the one that needs pinning: without it every refused token request — a bad
        // authorization code, a bad client — would land in the same bucket and the signal this
        // whole mechanism exists to produce would be indistinguishable from routine noise.
        var before = await CountRefreshRejectionsAsync();

        using var http = fixture.Factory.CreateClient();
        var response = await http.PostAsync("/connect/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = "not-a-real-code",
            ["client_id"] = OAuthFlowHelper.ClientId,
            ["redirect_uri"] = OAuthFlowHelper.RedirectUri,
            ["code_verifier"] = "not-a-real-verifier",
        }));

        // The exact rejection code is OpenIddict's business (401 here, since the bogus code makes
        // it a client-authentication failure); what this test pins is that it was refused AND that
        // the refusal did not land in the refresh bucket.
        Assert.False(response.IsSuccessStatusCode);
        Assert.Equal(before, await CountRefreshRejectionsAsync());
    }

    private async Task<int> CountRefreshRejectionsAsync()
    {
        using var scope = fixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<KoratDbContext>();
        return await db.AuditEvents.AsNoTracking()
            .CountAsync(e => e.Action == AuditActions.OAuthRefreshRejected);
    }
}
