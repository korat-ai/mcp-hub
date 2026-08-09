using System.Net;
using System.Net.Http.Json;

namespace Korat.Cloud.IntegrationTests.Auth;

/// <summary>
/// Verifies that every auth JSON POST endpoint returns 400 { error: "antiforgery-failure" }
/// when the X-XSRF-TOKEN header is absent.
///
/// UseAntiforgery() middleware only auto-validates form-bound endpoints; JSON minimal-API
/// POSTs require the RequireAntiforgeryValidation() endpoint filter applied explicitly.
/// These tests ensure the filter is wired on all 6 targeted endpoints.
///
/// The fixture runs in the Testing environment (dev-shortcut mode) so the startup
/// guards do not block — this is the same posture as all other integration tests.
/// </summary>
public sealed class AntiforgeryEnforcementTests(KoratIntegrationFixture fixture) : IClassFixture<KoratIntegrationFixture>
{
    // A raw client with no antiforgery token. We use a JSON content body where
    // required so the endpoint reaches the antiforgery filter (routing must match
    // before the filter runs — empty body would cause a 400 from model binding
    // before we'd hit the antiforgery check on endpoints that bind [FromBody]).
    private HttpClient RawClient() => fixture.Factory.CreateClient();

    [Fact]
    public async Task Signout_WithoutAntiforgery_Returns400AntiforgeryFailure()
    {
        var client = RawClient();
        var response = await client.PostAsync("/api/auth/signout", null);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<ErrorResponse>();
        Assert.Equal("antiforgery-failure", body?.Error);
    }

    [Fact]
    public async Task SessionRevoke_WithoutAntiforgery_Returns400AntiforgeryFailure()
    {
        // Endpoint is full-scope gated, so the request must be authenticated to reach the
        // antiforgery filter (unauthenticated → 401). Cookie present, no XSRF token → 400.
        var seeded = await fixture.SeedUserAsync($"sessrevoke-noxsrf-{Guid.NewGuid():N}@test.local", "Name");
        using var client = await fixture.CreateAuthenticatedClientAsync(seeded.UserId);
        var id = Guid.NewGuid();
        var response = await client.PostAsync($"/api/auth/sessions/{id}/revoke", null);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<ErrorResponse>();
        Assert.Equal("antiforgery-failure", body?.Error);
    }

    [Fact]
    public async Task PendingLinkConfirm_WithoutAntiforgery_Returns400AntiforgeryFailure()
    {
        var client = RawClient();
        var response = await client.PostAsync("/api/auth/pending-link/confirm", null);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<ErrorResponse>();
        Assert.Equal("antiforgery-failure", body?.Error);
    }

    [Fact]
    public async Task PendingLinkCancel_WithoutAntiforgery_Returns400AntiforgeryFailure()
    {
        var client = RawClient();
        var response = await client.PostAsync("/api/auth/pending-link/cancel", null);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<ErrorResponse>();
        Assert.Equal("antiforgery-failure", body?.Error);
    }



    // ── CLI device-flow endpoints (approve / deny / revoke-all) ─────────────

    [Fact]
    public async Task CliApprove_WithoutAntiforgery_Returns400AntiforgeryFailure()
    {
        var client = RawClient();
        // Must include a JSON body so routing reaches the antiforgery filter.
        var response = await client.PostAsJsonAsync("/api/auth/cli/approve", new { user_code = "TESTCODE" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<ErrorResponse>();
        Assert.Equal("antiforgery-failure", body?.Error);
    }

    [Fact]
    public async Task CliDeny_WithoutAntiforgery_Returns400AntiforgeryFailure()
    {
        var client = RawClient();
        var response = await client.PostAsJsonAsync("/api/auth/cli/deny", new { user_code = "TESTCODE" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<ErrorResponse>();
        Assert.Equal("antiforgery-failure", body?.Error);
    }

    [Fact]
    public async Task CliRevokeAll_WithoutAntiforgery_Returns400AntiforgeryFailure()
    {
        var client = RawClient();
        var response = await client.PostAsync("/api/auth/cli/revoke-all", null);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<ErrorResponse>();
        Assert.Equal("antiforgery-failure", body?.Error);
    }

    private sealed record ErrorResponse(string Error);
}
