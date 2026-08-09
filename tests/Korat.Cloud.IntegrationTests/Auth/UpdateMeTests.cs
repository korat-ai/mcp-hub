using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Korat.Domain;

namespace Korat.Cloud.IntegrationTests.Auth;

/// <summary>
/// Integration tests for PUT /api/auth/me (display-name update) and
/// GET /api/auth/me (profile read-through the user grain).
/// </summary>
public sealed class UpdateMeTests(KoratIntegrationFixture fixture) : IClassFixture<KoratIntegrationFixture>
{
    [Fact]
    public async Task PutMe_UpdatesDisplayName_ReturnsUpdatedMe()
    {
        var seeded = await fixture.SeedUserAsync($"putme-{Guid.NewGuid():N}@example.com", "Original Name");
        using var client = await fixture.CreateAuthenticatedClientWithAntiforgeryAsync(seeded.UserId);

        var resp = await client.PutAsJsonAsync("/api/auth/me", new { displayName = "Ada Lovelace" });

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<JsonDocument>();
        Assert.Equal("Ada Lovelace", body!.RootElement.GetProperty("displayName").GetString());
    }

    [Fact]
    public async Task PutMe_DisplayName_PersistedAfterRoundtrip()
    {
        var seeded = await fixture.SeedUserAsync($"putme-persist-{Guid.NewGuid():N}@example.com", "Before");
        using var client = await fixture.CreateAuthenticatedClientWithAntiforgeryAsync(seeded.UserId);

        await client.PutAsJsonAsync("/api/auth/me", new { displayName = "Grace Hopper" });

        // GET /api/auth/me reads through the user grain and must reflect the updated value.
        var getResp = await client.GetAsync("/api/auth/me");
        Assert.Equal(HttpStatusCode.OK, getResp.StatusCode);
        var body = await getResp.Content.ReadFromJsonAsync<JsonDocument>();
        Assert.Equal("Grace Hopper", body!.RootElement.GetProperty("displayName").GetString());
    }

    [Fact]
    public async Task PutMe_RejectsBlankDisplayName_Returns400()
    {
        var seeded = await fixture.SeedUserAsync($"putme-blank-{Guid.NewGuid():N}@example.com", "Name");
        using var client = await fixture.CreateAuthenticatedClientWithAntiforgeryAsync(seeded.UserId);

        var resp = await client.PutAsJsonAsync("/api/auth/me", new { displayName = "   " });

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task PutMe_RejectsTooLongDisplayName_Returns400()
    {
        var seeded = await fixture.SeedUserAsync($"putme-long-{Guid.NewGuid():N}@example.com", "Name");
        using var client = await fixture.CreateAuthenticatedClientWithAntiforgeryAsync(seeded.UserId);

        // One character over the profile-specific cap.
        var tooLong = new string('A', DisplayNameRules.MaxProfileDisplayNameLength + 1);
        var resp = await client.PutAsJsonAsync("/api/auth/me", new { displayName = tooLong });

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task PutMe_RejectsControlCharactersInDisplayName_Returns400()
    {
        var seeded = await fixture.SeedUserAsync($"putme-ctrl-{Guid.NewGuid():N}@example.com", "Name");
        using var client = await fixture.CreateAuthenticatedClientWithAntiforgeryAsync(seeded.UserId);

        // Newline embedded in name must be rejected (stored-data hygiene / UI-injection seam).
        var resp = await client.PutAsJsonAsync("/api/auth/me", new { displayName = "Ada\nLovelace" });

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    /// <summary>
    /// Authenticated (valid full-scope session cookie) but with NO antiforgery token: the
    /// full-scope endpoint filter passes, then antiforgery rejects the mutating request with
    /// 400 Bad Request. This verifies CSRF protection on the endpoint. (An *unauthenticated*
    /// request now returns 401 from the scope filter — that path is covered elsewhere.)
    /// </summary>
    [Fact]
    public async Task PutMe_WithoutAntiforgeryToken_Returns400()
    {
        var seeded = await fixture.SeedUserAsync($"putme-noxsrf-{Guid.NewGuid():N}@example.com", "Name");
        using var client = await fixture.CreateAuthenticatedClientAsync(seeded.UserId);

        var resp = await client.PutAsJsonAsync("/api/auth/me", new { displayName = "Hacker" });

        // Antiforgery rejects the authenticated, token-less mutating request.
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    /// <summary>
    /// Verifies that the PUT target user is derived from server-side identity (the session
    /// cookie), not from anything in the request body. User A's update must not touch User B.
    /// </summary>
    [Fact]
    public async Task PutMe_OnlyUpdatesAuthenticatedUser_NotOtherUsers()
    {
        var userA = await fixture.SeedUserAsync($"putme-a-{Guid.NewGuid():N}@example.com", "Alice");
        var userB = await fixture.SeedUserAsync($"putme-b-{Guid.NewGuid():N}@example.com", "Bob");

        // Authenticate as user A and update display name.
        using var clientA = await fixture.CreateAuthenticatedClientWithAntiforgeryAsync(userA.UserId);
        var putResp = await clientA.PutAsJsonAsync("/api/auth/me", new { displayName = "Alice Updated" });
        Assert.Equal(HttpStatusCode.OK, putResp.StatusCode);

        // User A's profile reflects the new name.
        var bodyA = await putResp.Content.ReadFromJsonAsync<JsonDocument>();
        Assert.Equal("Alice Updated", bodyA!.RootElement.GetProperty("displayName").GetString());

        // User B's profile is unchanged — the grain key comes from server-side identity.
        using var clientB = await fixture.CreateAuthenticatedClientWithAntiforgeryAsync(userB.UserId);
        var getResp = await clientB.GetAsync("/api/auth/me");
        Assert.Equal(HttpStatusCode.OK, getResp.StatusCode);
        var bodyB = await getResp.Content.ReadFromJsonAsync<JsonDocument>();
        Assert.Equal("Bob", bodyB!.RootElement.GetProperty("displayName").GetString());
    }
}
