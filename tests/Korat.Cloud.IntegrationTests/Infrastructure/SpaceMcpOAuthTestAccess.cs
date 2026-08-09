using Korat.Cloud.Mcp.Space;
using Korat.Domain;
using Korat.Domain.Auth;
using Korat.Cloud.IntegrationTests.SpaceMcpOAuth;

namespace Korat.Cloud.IntegrationTests;

/// <summary>
/// Р25: the Space-MCP endpoint accepts OAuth access tokens only. Every Space-MCP test used to
/// obtain its bearer from <c>IssueScopedCliTokenAsync</c> and derive the consumer identity from
/// the token id — the machine-wide path that Р25 removed. This helper is the replacement vehicle:
/// it drives the real authorize → consent → code → token flow and hands back both the bearer and
/// the identity the server will derive for it.
///
/// <para>Driving the real flow, rather than minting a token by reaching into the token store, is
/// deliberate. The thing under test in those suites is the relay behind the endpoint, but the way
/// a caller reaches that endpoint is now the security-relevant part — a shortcut here would let
/// the endpoint's own auth path rot untested behind a dozen green suites.</para>
/// </summary>
public static class SpaceMcpOAuthTestAccess
{
    public static async Task<(string AccessToken, ConsumerId ConsumerId)> IssueAsync(
        KoratIntegrationFixture fixture, UserId userId, string spaceId)
    {
        await fixture.EnsureOAuthClientAsync(OAuthFlowHelper.RedirectUri);

        var resource = $"http://localhost/mcp/{spaceId}";
        var browser = await fixture.CreateAuthenticatedNoRedirectClientAsync(userId);
        var (verifier, challenge) = OAuthFlowHelper.NewPkcePair();
        var code = await OAuthFlowHelper.AuthorizeAndConsentAsync(
            browser, OAuthFlowHelper.AuthorizeUrl(resource, challenge));

        using var http = fixture.Factory.CreateClient();
        var tokens = await OAuthFlowHelper.ExchangeCodeAsync(http, code, verifier, resource);
        var accessToken = tokens["access_token"]!.GetValue<string>();

        var consumerId = SpaceMcpConsumerIdentity.DeriveOAuth(
            OAuthFlowHelper.ClientId, userId, new SpaceId(spaceId));

        return (accessToken, consumerId);
    }
}
