using System.Net;
using System.Net.Http.Json;
using Korat.Cloud.Web.Auth.Services;
using Microsoft.Extensions.DependencyInjection;

namespace Korat.Cloud.IntegrationTests;

/// <summary>
/// F39 integration tests: CLI token Scope enforcement on the account / session /
/// CLI-token-management / invite-management surface.
///
/// A bridge-only token (Scope="bridge-only") resolves to a real identity but must be rejected
/// with 403 by the <c>RequireFullScope()</c> filter on every resolver-backed account endpoint:
///   - PUT  /api/auth/me
///   - POST /api/auth/email/change
///   - POST /api/auth/email/change/confirm
///   - GET  /api/auth/sessions
///   - POST /api/auth/sessions/{id}/revoke
///   - POST /api/auth/sessions/revoke-others
///   - GET  /api/cli/tokens
///   - POST /api/cli/tokens/{id}/revoke
///
/// The scope filter runs outermost (registered before rate-limiting / antiforgery), so a
/// bridge-only Bearer request is rejected with 403 before any antiforgery (400) check fires.
///
/// Cookie/session auth is unaffected (always Scope="full"). The /api/auth/signout (pure-cookie)
/// and /api/auth/cli/revoke (Bearer-only, RFC 7009 idempotent) endpoints intentionally carry no
/// resolver and are therefore out of scope for this gate.
/// </summary>
[Trait("Category", "AccountScopeEnforcement")]
public sealed class AccountScopeEnforcementTests(KoratIntegrationFixture fixture)
    : IClassFixture<KoratIntegrationFixture>
{
    private async Task<string> IssueBridgeOnlyTokenAsync(Guid userId)
    {
        using var scope = fixture.Factory.Services.CreateScope();
        var tokens = scope.ServiceProvider.GetRequiredService<ICliTokenService>();
        var result = await tokens.IssueAsync(userId, "bridge-only", default);
        return result.RawToken;
    }

    /// <summary>
    /// Bridge-only token → 403 on every account / session / CLI-token / invite-management
    /// endpoint. This is the F39 regression case: a relay agent's bridge-only token must not
    /// reach the account-management surface even though the owning user is valid.
    /// </summary>
    [Fact]
    public async Task AccountEndpoints_BridgeOnlyToken_Returns403()
    {
        var user = await fixture.SeedUserAsync(
            $"acct-scope-{Guid.NewGuid():N}@example.com", "Acct Scope");
        await fixture.MakeAdminAsync(user.UserId);  // even an admin's bridge-only token is rejected

        var bridgeToken = await IssueBridgeOnlyTokenAsync(user.UserId.Value);
        var client = fixture.Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", bridgeToken);

        var fakeId = Guid.NewGuid();

        var cases = new (string Method, string Path, object? Body)[]
        {
            ("PUT",  "/api/auth/me",                                     new { displayName = "x" }),
            ("POST", "/api/auth/email/change",                           new { newEmail = "new@example.com" }),
            ("POST", "/api/auth/email/change/confirm",                   new { token = "tok" }),
            ("GET",  "/api/auth/sessions",                               null),
            ("POST", $"/api/auth/sessions/{fakeId}/revoke",             null),
            ("POST", "/api/auth/sessions/revoke-others",                null),
            ("GET",  "/api/cli/tokens/",                                 null),
            ("POST", $"/api/cli/tokens/{fakeId}/revoke",               null),
        };

        foreach (var (method, path, body) in cases)
        {
            HttpResponseMessage resp = method switch
            {
                "GET" => await client.GetAsync(path),
                "PUT" => await client.PutAsJsonAsync(path, body!),
                "POST" => body is null
                    ? await client.PostAsync(path, null)
                    : await client.PostAsJsonAsync(path, body),
                _ => throw new InvalidOperationException(method),
            };

            Assert.True(
                resp.StatusCode == HttpStatusCode.Forbidden,
                $"{method} {path} expected 403 for bridge-only token but got {(int)resp.StatusCode} {resp.StatusCode}");
        }
    }
}
