using System.Net;
using System.Net.Http.Json;
using Korat.Cloud.Web.Auth.Services;
using Microsoft.Extensions.DependencyInjection;

namespace Korat.Cloud.IntegrationTests;

/// <summary>
/// MAJOR-2 integration tests: CLI token Scope enforcement on admin and developer surfaces.
///
/// A bridge-only token (Scope="bridge-only") resolves to a real identity but must be rejected
/// with 403 on:
///   - POST /api/admin/envelope/rewrap   (RequireAdmin filter)
///   - POST /api/admin/spaces/.../crypto-shred   (RequireAdmin filter)
///   - GET  /api/admin/audit/verify      (RequireAdmin filter)
///   - GET  /api/admin/audit/events      (RequireAdmin filter)
///
/// A full-scope admin token (Scope="full", IsAdmin=true) must be accepted (200/202/204).
/// A full-scope NON-admin token (Scope="full", IsAdmin=false) must be rejected with 403.
///
/// Cookie/session auth is unaffected (always Scope="full" by default).
/// Bridge/relay flows (gRPC Hello) are covered separately by NodeGatewayBearerHelloTests.
/// </summary>
[Trait("Category", "AdminOpsScopeEnforcement")]
public sealed class AdminOpsScopeEnforcementTests(KoratIntegrationFixture fixture)
    : IClassFixture<KoratIntegrationFixture>
{
    // ── helpers ──────────────────────────────────────────────────────────────

    private async Task<string> IssueBridgeOnlyTokenAsync(Guid userId)
    {
        using var scope = fixture.Factory.Services.CreateScope();
        var tokens = scope.ServiceProvider.GetRequiredService<ICliTokenService>();
        var result = await tokens.IssueAsync(userId, "bridge-only", default);
        return result.RawToken;
    }

    private async Task<string> IssueFullTokenAsync(Guid userId)
    {
        using var scope = fixture.Factory.Services.CreateScope();
        var tokens = scope.ServiceProvider.GetRequiredService<ICliTokenService>();
        var result = await tokens.IssueAsync(userId, "full", default);
        return result.RawToken;
    }

    // ── RequireAdmin: Scope enforcement ──────────────────────────────────────

    /// <summary>
    /// Bridge-only token whose owner IS admin → 403 on all four admin-ops endpoints.
    /// This is the primary MAJOR-2 regression case: the attacker steals the relay agent's
    /// bridge-only token, but even though the owner is an admin, the scope blocks access.
    /// </summary>
    [Fact]
    public async Task AdminEndpoints_BridgeOnlyToken_AdminUser_Returns403()
    {
        var adminUser = await fixture.SeedUserAsync(
            $"scope-admin-{Guid.NewGuid():N}@example.com", "Scope Admin");
        await fixture.MakeAdminAsync(adminUser.UserId);

        var bridgeToken = await IssueBridgeOnlyTokenAsync(adminUser.UserId.Value);
        var client = fixture.Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", bridgeToken);

        // POST /api/admin/envelope/rewrap
        var rewrap = await client.PostAsync("/api/admin/envelope/rewrap", null);
        Assert.Equal(HttpStatusCode.Forbidden, rewrap.StatusCode);

        // POST /api/admin/spaces/{id}/crypto-shred
        var shred = await client.PostAsJsonAsync(
            "/api/admin/spaces/test-space/crypto-shred",
            new { confirm = "test-space" });
        Assert.Equal(HttpStatusCode.Forbidden, shred.StatusCode);

        // GET /api/admin/audit/verify
        var verify = await client.GetAsync("/api/admin/audit/verify");
        Assert.Equal(HttpStatusCode.Forbidden, verify.StatusCode);

        // GET /api/admin/audit/events
        var events = await client.GetAsync("/api/admin/audit/events");
        Assert.Equal(HttpStatusCode.Forbidden, events.StatusCode);
    }

    /// <summary>
    /// Full-scope admin token → 200/202 on rewrap and audit/verify.
    /// Ensures the scope check does NOT block legitimate admin access.
    /// </summary>
    [Fact]
    public async Task AdminAuditVerify_FullScope_AdminToken_ReturnsOk()
    {
        var adminUser = await fixture.SeedUserAsync(
            $"scope-full-admin-{Guid.NewGuid():N}@example.com", "Full Admin");
        await fixture.MakeAdminAsync(adminUser.UserId);

        var fullToken = await IssueFullTokenAsync(adminUser.UserId.Value);
        var client = fixture.Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", fullToken);

        // GET /api/admin/audit/verify — should return 200 with ok=true on an empty chain
        var verify = await client.GetAsync("/api/admin/audit/verify");
        Assert.Equal(HttpStatusCode.OK, verify.StatusCode);
    }

    /// <summary>
    /// Full-scope NON-admin token → 403 on admin endpoints.
    /// Ensures that full scope alone is not enough; IsAdmin must also be true.
    /// </summary>
    [Fact]
    public async Task AdminEndpoints_FullScope_NonAdminToken_Returns403()
    {
        var regularUser = await fixture.SeedUserAsync(
            $"scope-nonadmin-{Guid.NewGuid():N}@example.com", "Non-Admin");
        // IsAdmin intentionally NOT set.

        var fullToken = await IssueFullTokenAsync(regularUser.UserId.Value);
        var client = fixture.Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", fullToken);

        var verify = await client.GetAsync("/api/admin/audit/verify");
        Assert.Equal(HttpStatusCode.Forbidden, verify.StatusCode);

        var events = await client.GetAsync("/api/admin/audit/events");
        Assert.Equal(HttpStatusCode.Forbidden, events.StatusCode);
    }

    /// <summary>
    /// Anonymous (no token) → 401 on admin endpoints (not 403 — must not leak admin existence).
    /// </summary>
    [Fact]
    public async Task AdminEndpoints_NoToken_Returns401()
    {
        var client = fixture.Factory.CreateClient();

        var verify = await client.GetAsync("/api/admin/audit/verify");
        Assert.Equal(HttpStatusCode.Unauthorized, verify.StatusCode);
    }
}
