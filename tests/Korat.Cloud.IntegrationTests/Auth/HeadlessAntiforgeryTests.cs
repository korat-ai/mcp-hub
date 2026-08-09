using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace Korat.Cloud.IntegrationTests.Auth;

/// <summary>
/// <c>RequireAntiforgeryUnlessHeadless()</c>: bearer/cookie-less callers skip antiforgery
/// (the headless CLI admin path), while cookie callers still get full CSRF enforcement.
///
/// Vehicle: <c>POST /api/admin/envelope/rewrap</c> — an admin-gated mutation carrying the same
/// three attributes as every other endpoint using this filter (RequireAdmin +
/// RequireAntiforgeryUnlessHeadless + AdminOpsPolicy rate limiting). It is safe to call in a
/// test because with no DEK rows it simply reports <c>processed: 0</c>.
///
/// These assertions previously rode on POST /api/auth/invites. The invite gate was removed
/// (Р15, open registration), so the vehicle moved; the invariant did not change.
///
/// SCOPE enforcement for the same filter chain (bridge-only token → 403, full-scope non-admin →
/// 403, no token → 401) lives in <c>AdminOpsScopeEnforcementTests</c> and is deliberately not
/// duplicated here — this file covers the antiforgery half only.
/// </summary>
public sealed class HeadlessAntiforgeryTests(KoratIntegrationFixture fixture)
    : IClassFixture<KoratIntegrationFixture>
{
    private const string AdminMutation = "/api/admin/envelope/rewrap";

    // Rewrap refuses outright ("No active KEK configured") unless an envelope KEK is present, so
    // the host needs one for the accepted-path assertions to distinguish "the filter let it
    // through" from "the handler blew up". With no DEK rows it then reports processed: 0.
    private const string KekId = "antiforgery-k1";
    private static readonly string KekBase64 = Convert.ToBase64String(new byte[32]);

    private WebApplicationFactory<Program> CreateKekFactory() =>
        fixture.Factory.WithWebHostBuilder(b =>
            b.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(new Dictionary<string, string?>
            {
                [$"Korat:Envelope:Keks:{KekId}"] = KekBase64,
                ["Korat:Envelope:ActiveKekId"] = KekId,
            })));

    [Fact]
    public async Task BearerAdmin_NoCookie_NoXsrf_IsAccepted()
    {
        var seeded = await fixture.SeedUserAsync(
            $"headless-admin-{Guid.NewGuid():N}@test.local", "Headless Admin");
        await fixture.MakeAdminAsync(seeded.UserId);
        var token = await fixture.IssueCliTokenAsync(seeded.UserId);

        using var factory = CreateKekFactory();
        var client = factory.CreateClient();
        var req = new HttpRequestMessage(HttpMethod.Post, AdminMutation);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var resp = await client.SendAsync(req);

        // No cookie ⇒ the filter treats the caller as headless and does not demand an XSRF token.
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    [Fact]
    public async Task CookieAdmin_NoXsrf_Returns400AntiforgeryFailure()
    {
        var seeded = await fixture.SeedUserAsync(
            $"cookie-admin-{Guid.NewGuid():N}@test.local", "Cookie Admin");
        await fixture.MakeAdminAsync(seeded.UserId);
        using var client = await fixture.CreateAuthenticatedClientAsync(seeded.UserId); // cookie, NO xsrf

        var resp = await client.PostAsync(AdminMutation, content: null);

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<ErrorResponse>();
        Assert.Equal("antiforgery-failure", body?.Error);
    }

    [Fact]
    public async Task CookieAdmin_WithXsrf_IsAccepted()
    {
        var seeded = await fixture.SeedUserAsync(
            $"cookie-admin-ok-{Guid.NewGuid():N}@test.local", "Cookie Admin Ok");
        await fixture.MakeAdminAsync(seeded.UserId);
        using var factory = CreateKekFactory();
        using var client = await fixture.CreateAuthenticatedClientWithAntiforgeryAsync(
            seeded.UserId, factory);

        var resp = await client.PostAsync(AdminMutation, content: null);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    private sealed record ErrorResponse(string Error);
}
