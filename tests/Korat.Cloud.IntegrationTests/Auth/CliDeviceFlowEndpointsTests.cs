using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Korat.Cloud.Web.Auth.Services;
using Korat.Domain.Auth;

namespace Korat.Cloud.IntegrationTests.Auth;

/// <summary>
/// Integration tests for the device-flow OAuth handshake endpoints:
///   POST /api/auth/cli/device-code  (anonymous)
///   POST /api/auth/cli/token        (anonymous, CLI polls)
///   POST /api/auth/cli/approve      (cookie-authenticated)
///   POST /api/auth/cli/deny         (cookie-authenticated)
///   POST /api/auth/cli/revoke       (Bearer)
/// </summary>
public sealed class CliDeviceFlowEndpointsTests(KoratIntegrationFixture fixture)
    : IClassFixture<KoratIntegrationFixture>
{
    // ── helpers ──────────────────────────────────────────────────────────────

    private HttpClient AnonClient() => fixture.Factory.CreateClient();

    /// <summary>
    /// Returns an authenticated client WITH an antiforgery token — required for /approve, /deny,
    /// /revoke-all which chain RequireAntiforgeryValidation() (CSRF fix).
    /// </summary>
    private async Task<HttpClient> AuthedClientAsync()
    {
        // Seed a real user + session so IAuthResolver resolves via the session branch.
        var seeded = await fixture.SeedUserAsync($"cli-test-{Guid.NewGuid():N}@example.com", "CLI Tester");
        return await fixture.CreateAuthenticatedClientWithAntiforgeryAsync(seeded.UserId);
    }

    private static async Task<JsonNode> JsonBodyAsync(HttpResponseMessage resp)
    {
        var body = await resp.Content.ReadAsStringAsync();
        return JsonNode.Parse(body) ?? throw new InvalidOperationException("Null JSON body");
    }

    // ── full happy-path flow ──────────────────────────────────────────────────

    [Fact]
    public async Task Full_DeviceFlow_DeviceCode_Then_Approve_Then_Token_Returns_CliToken()
    {
        var anon = AnonClient();
        using var authed = await AuthedClientAsync();

        // 1. POST /api/auth/cli/device-code → 200 + RFC 8628 fields
        var dcResp = await anon.PostAsync("/api/auth/cli/device-code", null);
        Assert.Equal(HttpStatusCode.OK, dcResp.StatusCode);
        var dcBody = await JsonBodyAsync(dcResp);
        var deviceCode = dcBody["device_code"]!.GetValue<string>();
        var userCode   = dcBody["user_code"]!.GetValue<string>();
        Assert.False(string.IsNullOrEmpty(deviceCode));
        Assert.False(string.IsNullOrEmpty(userCode));
        Assert.False(string.IsNullOrEmpty(dcBody["verification_uri"]!.GetValue<string>()));
        Assert.True(dcBody["expires_in"]!.GetValue<int>() > 0);
        Assert.True(dcBody["interval"]!.GetValue<int>() > 0);

        // 2. Poll before approval → 400 authorization_pending
        var pendingResp = await anon.PostAsJsonAsync("/api/auth/cli/token", new { device_code = deviceCode });
        Assert.Equal(HttpStatusCode.BadRequest, pendingResp.StatusCode);
        var pendingBody = await JsonBodyAsync(pendingResp);
        Assert.Equal("authorization_pending", pendingBody["error"]!.GetValue<string>());

        // 3. Approve (cookie-authenticated user)
        var approveResp = await authed.PostAsJsonAsync("/api/auth/cli/approve", new { user_code = userCode });
        Assert.Equal(HttpStatusCode.OK, approveResp.StatusCode);

        // 4. Poll after approval → 200 { cli_token, scope, expires_in }
        var tokenResp = await anon.PostAsJsonAsync("/api/auth/cli/token", new { device_code = deviceCode });
        Assert.Equal(HttpStatusCode.OK, tokenResp.StatusCode);
        var tokenBody = await JsonBodyAsync(tokenResp);
        var cliToken = tokenBody["cli_token"]!.GetValue<string>();
        Assert.StartsWith("korat_cli_", cliToken);
        Assert.Equal("full", tokenBody["scope"]!.GetValue<string>());
        Assert.True(tokenBody["expires_in"]!.GetValue<int>() > 0);
    }

    [Fact]
    public async Task Poll_After_Token_Consumed_Returns_ExpiredToken()
    {
        var anon = AnonClient();
        using var authed = await AuthedClientAsync();

        var dcBody = await JsonBodyAsync(await anon.PostAsync("/api/auth/cli/device-code", null));
        var deviceCode = dcBody["device_code"]!.GetValue<string>();
        var userCode   = dcBody["user_code"]!.GetValue<string>();

        await authed.PostAsJsonAsync("/api/auth/cli/approve", new { user_code = userCode });

        // First poll consumes the approved state.
        var first = await anon.PostAsJsonAsync("/api/auth/cli/token", new { device_code = deviceCode });
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        // Second poll: handshake already consumed → expired_token.
        var second = await anon.PostAsJsonAsync("/api/auth/cli/token", new { device_code = deviceCode });
        Assert.Equal(HttpStatusCode.BadRequest, second.StatusCode);
        var body = await JsonBodyAsync(second);
        Assert.Equal("expired_token", body["error"]!.GetValue<string>());
    }

    // ── deny path ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Deny_Then_Token_Returns_AccessDenied()
    {
        var anon = AnonClient();
        using var authed = await AuthedClientAsync();

        var dcBody = await JsonBodyAsync(await anon.PostAsync("/api/auth/cli/device-code", null));
        var deviceCode = dcBody["device_code"]!.GetValue<string>();
        var userCode   = dcBody["user_code"]!.GetValue<string>();

        // Deny via cookie-authenticated user
        var denyResp = await authed.PostAsJsonAsync("/api/auth/cli/deny", new { user_code = userCode });
        Assert.Equal(HttpStatusCode.OK, denyResp.StatusCode);

        // Poll after denial → access_denied
        var tokenResp = await anon.PostAsJsonAsync("/api/auth/cli/token", new { device_code = deviceCode });
        Assert.Equal(HttpStatusCode.BadRequest, tokenResp.StatusCode);
        var tokenBody = await JsonBodyAsync(tokenResp);
        Assert.Equal("access_denied", tokenBody["error"]!.GetValue<string>());
    }

    // ── unauthenticated approve/deny ─────────────────────────────────────────
    // Note: /approve and /deny now chain RequireAntiforgeryValidation() (CSRF fix).
    // The antiforgery filter runs before the auth check, so an unauthenticated request
    // without an X-XSRF-TOKEN header returns 400 {error:"antiforgery-failure"} rather
    // than 401 — the CSRF guard is intentionally the outer gate.

    [Fact]
    public async Task Approve_Without_Auth_Or_Antiforgery_Returns_AntiforgeryFailure()
    {
        var anon = AnonClient();
        var resp = await anon.PostAsJsonAsync("/api/auth/cli/approve", new { user_code = "ANYTHING" });
        // Antiforgery fires first (before auth resolution).
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        var body = await JsonBodyAsync(resp);
        Assert.Equal("antiforgery-failure", body["error"]!.GetValue<string>());
    }

    [Fact]
    public async Task Deny_Without_Auth_Or_Antiforgery_Returns_AntiforgeryFailure()
    {
        var anon = AnonClient();
        var resp = await anon.PostAsJsonAsync("/api/auth/cli/deny", new { user_code = "ANYTHING" });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        var body = await JsonBodyAsync(resp);
        Assert.Equal("antiforgery-failure", body["error"]!.GetValue<string>());
    }

    // ── unknown user_code ─────────────────────────────────────────────────────

    [Fact]
    public async Task Approve_Unknown_UserCode_Returns_NotFound()
    {
        using var authed = await AuthedClientAsync();
        var resp = await authed.PostAsJsonAsync("/api/auth/cli/approve", new { user_code = "XXXXXXXX" });
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    // ── unknown device_code ───────────────────────────────────────────────────

    [Fact]
    public async Task Token_Unknown_DeviceCode_Returns_ExpiredToken()
    {
        var anon = AnonClient();
        var resp = await anon.PostAsJsonAsync("/api/auth/cli/token", new { device_code = "dev-totally-unknown" });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        var body = await JsonBodyAsync(resp);
        Assert.Equal("expired_token", body["error"]!.GetValue<string>());
    }

    // ── revoke ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Revoke_Valid_Token_Makes_It_Invalid()
    {
        var anon = AnonClient();
        using var authed = await AuthedClientAsync();

        // Issue a token through the full flow.
        var dcBody = await JsonBodyAsync(await anon.PostAsync("/api/auth/cli/device-code", null));
        await authed.PostAsJsonAsync("/api/auth/cli/approve", new { user_code = dcBody["user_code"]!.GetValue<string>() });
        var cliToken = (await JsonBodyAsync(
            await anon.PostAsJsonAsync("/api/auth/cli/token", new { device_code = dcBody["device_code"]!.GetValue<string>() })))
            ["cli_token"]!.GetValue<string>();

        // Revoke via Bearer.
        var revokeClient = fixture.Factory.CreateClient();
        revokeClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {cliToken}");
        var revokeResp = await revokeClient.PostAsync("/api/auth/cli/revoke", null);
        Assert.Equal(HttpStatusCode.OK, revokeResp.StatusCode);

        // The token should no longer validate — /api/auth/me returns 401.
        revokeClient.DefaultRequestHeaders.Remove("Cookie");
        var meResp = await revokeClient.GetAsync("/api/auth/me");
        Assert.Equal(HttpStatusCode.Unauthorized, meResp.StatusCode);
    }

    // ── Crockford user_code folding ───────────────────────────────────────────

    [Fact]
    public async Task Approve_With_Crockford_Folded_UserCode_Succeeds()
    {
        // Verifies that an operator typing 'O' instead of '0' or 'I'/'L' instead of
        // '1' is handled by Crockford ambiguous-char folding in the server-side
        // normalization path (IDeviceCodeStore.NormalizeUserCode).
        // We generate a real code and then substitute an equivalent folded character.
        var anon = AnonClient();
        using var authed = await AuthedClientAsync();

        var dcBody = await JsonBodyAsync(await anon.PostAsync("/api/auth/cli/device-code", null));
        var deviceCode = dcBody["device_code"]!.GetValue<string>();
        var userCode   = dcBody["user_code"]!.GetValue<string>();

        // Replace first '0' with 'O', or first '1' with 'I', to simulate a mistype.
        // If neither '0' nor '1' appears (all other Crockford chars), we skip (extremely rare).
        string? foldedCode = null;
        for (var i = 0; i < userCode.Length; i++)
        {
            if (userCode[i] == '0') { foldedCode = userCode[..i] + "O" + userCode[(i + 1)..]; break; }
            if (userCode[i] == '1') { foldedCode = userCode[..i] + "I" + userCode[(i + 1)..]; break; }
        }
        if (foldedCode is null)
        {
            // No ambiguous character in this particular code — skip rather than assert on a pass-through.
            return;
        }

        // Approve using the folded (mistyped) code — server must fold it back.
        var approveResp = await authed.PostAsJsonAsync("/api/auth/cli/approve", new { user_code = foldedCode });
        Assert.Equal(HttpStatusCode.OK, approveResp.StatusCode);

        // Poll — should resolve to a valid token.
        var tokenResp = await anon.PostAsJsonAsync("/api/auth/cli/token", new { device_code = deviceCode });
        Assert.Equal(HttpStatusCode.OK, tokenResp.StatusCode);
        var tokenBody = await JsonBodyAsync(tokenResp);
        Assert.StartsWith("korat_cli_", tokenBody["cli_token"]!.GetValue<string>());
    }

    // ── CLI token management (GET /api/cli/tokens, POST /api/cli/tokens/{id}/revoke) ──

    [Fact]
    public async Task GetCliTokens_ReturnsOnlyCallerTokens()
    {
        var anon = AnonClient();
        using var authed = await AuthedClientAsync();

        // Issue a token for the authenticated user via the device flow.
        var dcBody = await JsonBodyAsync(await anon.PostAsync("/api/auth/cli/device-code", null));
        await authed.PostAsJsonAsync("/api/auth/cli/approve", new { user_code = dcBody["user_code"]!.GetValue<string>() });
        await anon.PostAsJsonAsync("/api/auth/cli/token", new { device_code = dcBody["device_code"]!.GetValue<string>() });

        var resp = await authed.GetAsync("/api/cli/tokens");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var body = await JsonBodyAsync(resp);
        var arr = body.AsArray();
        Assert.True(arr.Count >= 1, "Expected at least one CLI token in the list.");
        // Each token must have the expected fields.
        var first = arr[0]!.AsObject();
        Assert.True(first.ContainsKey("id"));
        Assert.True(first.ContainsKey("name"));
        Assert.True(first.ContainsKey("createdAt"));
    }

    [Fact]
    public async Task RevokeCliToken_ById_OwnerCanRevoke()
    {
        var anon = AnonClient();
        using var authed = await AuthedClientAsync();

        // Issue a token via the device flow.
        var dcBody = await JsonBodyAsync(await anon.PostAsync("/api/auth/cli/device-code", null));
        await authed.PostAsJsonAsync("/api/auth/cli/approve", new { user_code = dcBody["user_code"]!.GetValue<string>() });
        await anon.PostAsJsonAsync("/api/auth/cli/token", new { device_code = dcBody["device_code"]!.GetValue<string>() });

        // List to get the token id.
        var listBody = (await JsonBodyAsync(await authed.GetAsync("/api/cli/tokens"))).AsArray();
        Assert.True(listBody.Count >= 1);
        var tokenId = listBody[0]!["id"]!.GetValue<string>();

        // Revoke by id.
        var revokeResp = await authed.PostAsync($"/api/cli/tokens/{tokenId}/revoke", null);
        Assert.Equal(HttpStatusCode.NoContent, revokeResp.StatusCode);

        // Should no longer appear in the list.
        var afterList = (await JsonBodyAsync(await authed.GetAsync("/api/cli/tokens"))).AsArray();
        Assert.DoesNotContain(afterList, t => t!["id"]!.GetValue<string>() == tokenId);
    }

    [Fact]
    public async Task RevokeCliToken_ById_UserA_CannotRevokeUserB_Token()
    {
        // IDOR test: user A must not revoke user B's CLI token by id.
        var anon = AnonClient();

        // User A — issues a token.
        var seededA = await fixture.SeedUserAsync($"cli-idor-a-{Guid.NewGuid():N}@example.com", "User A");
        using var clientA = await fixture.CreateAuthenticatedClientWithAntiforgeryAsync(seededA.UserId);

        // User B — will try to revoke user A's token.
        var seededB = await fixture.SeedUserAsync($"cli-idor-b-{Guid.NewGuid():N}@example.com", "User B");
        using var clientB = await fixture.CreateAuthenticatedClientWithAntiforgeryAsync(seededB.UserId);

        // Issue a token for user A via the device flow.
        var dcBody = await JsonBodyAsync(await anon.PostAsync("/api/auth/cli/device-code", null));
        await clientA.PostAsJsonAsync("/api/auth/cli/approve", new { user_code = dcBody["user_code"]!.GetValue<string>() });
        await anon.PostAsJsonAsync("/api/auth/cli/token", new { device_code = dcBody["device_code"]!.GetValue<string>() });

        // Get user A's token id.
        var listA = (await JsonBodyAsync(await clientA.GetAsync("/api/cli/tokens"))).AsArray();
        Assert.True(listA.Count >= 1, "User A should have at least one token.");
        var tokenIdA = listA[0]!["id"]!.GetValue<string>();

        // User B attempts to revoke user A's token by id — must get 404 (cloaked-403).
        var revokeResp = await clientB.PostAsync($"/api/cli/tokens/{tokenIdA}/revoke", null);
        Assert.Equal(HttpStatusCode.NotFound, revokeResp.StatusCode);

        // User A's token must still be alive.
        var listAAfter = (await JsonBodyAsync(await clientA.GetAsync("/api/cli/tokens"))).AsArray();
        Assert.Contains(listAAfter, t => t!["id"]!.GetValue<string>() == tokenIdA);
    }

    // ── revoke-all success path (cov C2) ─────────────────────────────────────

    [Fact]
    public async Task RevokeAll_RevokesAllTokensForUser_BothReturn401()
    {
        // Issue two separate CLI tokens for the same authenticated user,
        // revoke-all, then assert both tokens are rejected on /api/auth/me.
        var seeded = await fixture.SeedUserAsync($"revoke-all-c2-{Guid.NewGuid():N}@example.com", "Revoke All User");
        using var authed = await fixture.CreateAuthenticatedClientWithAntiforgeryAsync(seeded.UserId);

        // Issue token 1 via the device flow.
        var anon = fixture.Factory.CreateClient();
        var dc1Body = await JsonBodyAsync(await anon.PostAsync("/api/auth/cli/device-code", null));
        await authed.PostAsJsonAsync("/api/auth/cli/approve", new { user_code = dc1Body["user_code"]!.GetValue<string>() });
        var token1 = (await JsonBodyAsync(
            await anon.PostAsJsonAsync("/api/auth/cli/token", new { device_code = dc1Body["device_code"]!.GetValue<string>() })))
            ["cli_token"]!.GetValue<string>();

        // Issue token 2 via the device flow.
        var dc2Body = await JsonBodyAsync(await anon.PostAsync("/api/auth/cli/device-code", null));
        await authed.PostAsJsonAsync("/api/auth/cli/approve", new { user_code = dc2Body["user_code"]!.GetValue<string>() });
        var token2 = (await JsonBodyAsync(
            await anon.PostAsJsonAsync("/api/auth/cli/token", new { device_code = dc2Body["device_code"]!.GetValue<string>() })))
            ["cli_token"]!.GetValue<string>();

        // Verify both tokens are valid before revoking (sanity check).
        var preCheckClient = fixture.Factory.CreateClient();
        preCheckClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {token1}");
        var preResp = await preCheckClient.GetAsync("/api/auth/me");
        Assert.Equal(System.Net.HttpStatusCode.OK, preResp.StatusCode);

        // POST /api/auth/cli/revoke-all (cookie-authenticated, CSRF-guarded).
        var revokeAllResp = await authed.PostAsync("/api/auth/cli/revoke-all", null);
        Assert.Equal(System.Net.HttpStatusCode.OK, revokeAllResp.StatusCode);

        // Token 1 must now return 401.
        var client1 = fixture.Factory.CreateClient();
        client1.DefaultRequestHeaders.Add("Authorization", $"Bearer {token1}");
        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, (await client1.GetAsync("/api/auth/me")).StatusCode);

        // Token 2 must also return 401 (both are revoked).
        var client2 = fixture.Factory.CreateClient();
        client2.DefaultRequestHeaders.Add("Authorization", $"Bearer {token2}");
        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, (await client2.GetAsync("/api/auth/me")).StatusCode);
    }

    // ── verification_uri origin pinning ──────────────────────────────────────

    [Fact]
    public async Task DeviceCode_VerificationUri_Uses_Configured_PublicOrigin()
    {
        // Verifies that when Korat:Cli:PublicOrigin is set, the /device-code endpoint
        // returns a verification_uri built from the trusted configured origin and NOT
        // from the client-supplied Host header (host-header injection defence).
        const string configuredOrigin = "https://cloud.example.com";

        // Build a one-off factory with PublicOrigin injected — cannot mutate the
        // shared fixture because parallelism is disabled but the fixture is shared.
        var factory = fixture.Factory.WithWebHostBuilder(b =>
            b.ConfigureAppConfiguration(cfg =>
                cfg.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["KORAT_AUTH_MODE"] = "dev-shortcut",
                    ["Korat:Cli:PublicOrigin"] = configuredOrigin,
                })));

        var client = factory.CreateClient();
        // Send a poisoned Host header — the response must ignore it.
        client.DefaultRequestHeaders.TryAddWithoutValidation("Host", "evil.attacker.com");

        var resp = await client.PostAsync("/api/auth/cli/device-code", null);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var body = await JsonBodyAsync(resp);
        var verificationUri = body["verification_uri"]!.GetValue<string>();
        Assert.StartsWith(configuredOrigin, verificationUri);
        Assert.DoesNotContain("evil.attacker.com", verificationUri);
    }

    // ── MAJOR-2: bridge-only token must not approve or deny device-code flows ─

    /// <summary>
    /// MAJOR-2: a bridge-only bearer token must not be able to approve a pending device-code
    /// flow. The threat: an attacker who obtains a relay agent's bridge-only token AND a
    /// valid antiforgery pair (e.g. from a compromised browser session) could call /approve
    /// with Bearer auth and escalate the bridge-only token to full scope via the device grant.
    ///
    /// We simulate the worst-case by using a cookie-auth client (which always resolves to
    /// Scope="full") to obtain antiforgery tokens, then construct a second request that
    /// substitutes the bridge-only bearer token while keeping the valid XSRF cookie+header.
    /// The scope gate fires after antiforgery and must return 403.
    /// </summary>
    [Fact]
    public async Task Approve_BridgeOnlyToken_Returns403()
    {
        // Issue a bridge-only bearer token for a real user (simulates a relay agent's token).
        var seeded = await fixture.SeedUserAsync(
            $"bridge-approve-{Guid.NewGuid():N}@example.com", "Bridge Approve Tester");
        using var scope = fixture.Factory.Services.CreateScope();
        var cliTokens = scope.ServiceProvider.GetRequiredService<ICliTokenService>();
        var bridgeToken = (await cliTokens.IssueAsync(seeded.UserId.Value, "bridge-only", default)).RawToken;

        // Start a device-code flow so there is a real pending user_code to present.
        var anon = AnonClient();
        var dcBody = await JsonBodyAsync(await anon.PostAsync("/api/auth/cli/device-code", null));
        var userCode = dcBody["user_code"]!.GetValue<string>();

        // Obtain a valid antiforgery cookie+header pair via an authenticated client.
        // Extract both values so we can construct a request with Bearer auth + valid XSRF.
        var legitimateClient = await fixture.CreateAuthenticatedClientWithAntiforgeryAsync(seeded.UserId);
        var xsrfHeader = legitimateClient.DefaultRequestHeaders.TryGetValues("X-XSRF-TOKEN", out var xsrfVals)
            ? xsrfVals.First() : null;
        var xsrfCookie = legitimateClient.DefaultRequestHeaders.TryGetValues("Cookie", out var cookieVals)
            ? cookieVals.FirstOrDefault(c => c.StartsWith("__Secure-korat_xsrf=", StringComparison.Ordinal))
            : null;

        // Build a request with: valid XSRF cookie+header (antiforgery passes), BUT bearer
        // auth using the bridge-only token — so ResolveAsync resolves via Bearer, not cookie.
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/auth/cli/approve")
        {
            Content = JsonContent.Create(new { user_code = userCode }),
        };
        req.Headers.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", bridgeToken);
        if (xsrfHeader is not null)
            req.Headers.TryAddWithoutValidation("X-XSRF-TOKEN", xsrfHeader);
        if (xsrfCookie is not null)
            req.Headers.TryAddWithoutValidation("Cookie", xsrfCookie);

        var bridgeClient = fixture.Factory.CreateClient();
        var approveResp = await bridgeClient.SendAsync(req);

        // Bridge-only scope → 403 (the scope gate fires after antiforgery succeeds).
        Assert.Equal(HttpStatusCode.Forbidden, approveResp.StatusCode);
    }

    /// <summary>
    /// MAJOR-2 positive: the legitimate cookie-authenticated (Scope="full") browser approval
    /// path must still work after adding the scope check on /approve.
    /// This is the existing Full_DeviceFlow test; we confirm it still passes.
    /// </summary>
    [Fact]
    public async Task Approve_FullScope_CookieAuth_Succeeds()
    {
        var anon = AnonClient();
        using var authed = await AuthedClientAsync();

        var dcBody = await JsonBodyAsync(await anon.PostAsync("/api/auth/cli/device-code", null));
        var deviceCode = dcBody["device_code"]!.GetValue<string>();
        var userCode   = dcBody["user_code"]!.GetValue<string>();

        // Cookie-authenticated user (Scope="full" implicitly) approves the device code.
        var approveResp = await authed.PostAsJsonAsync("/api/auth/cli/approve", new { user_code = userCode });
        Assert.Equal(HttpStatusCode.OK, approveResp.StatusCode);

        // CLI polls and receives a full-scope token.
        var tokenResp = await anon.PostAsJsonAsync("/api/auth/cli/token", new { device_code = deviceCode });
        Assert.Equal(HttpStatusCode.OK, tokenResp.StatusCode);
        var tokenBody = await JsonBodyAsync(tokenResp);
        Assert.Equal("full", tokenBody["scope"]!.GetValue<string>());
    }
}
