using Korat.Domain.Auth;
using Korat.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Korat.Cloud.IntegrationTests.Auth;

/// <summary>
/// Tests for the Task-1 fixture-A seeding helpers: SeedUserAsync and
/// CreateAuthenticatedClientAsync. These helpers are the foundation for every
/// later endpoint test that asserts cross-Space isolation.
/// </summary>
public sealed class TestHostSeedingTests(KoratIntegrationFixture fixture) : IClassFixture<KoratIntegrationFixture>
{
    [Fact]
    public async Task SeedUserAsync_CreatesExactlyOneDefaultSpace()
    {
        var seeded = await fixture.SeedUserAsync("alice@example.com", "Alice");

        Assert.NotEqual(default, seeded.UserId);
        Assert.False(string.IsNullOrEmpty(seeded.SpaceId));

        using var scope = fixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<KoratDbContext>();
        var ownerKey = seeded.UserId.Value.ToString("N");
        var defaults = await db.Spaces
            .Where(s => s.OwnerUserId == ownerKey && s.IsDefault)
            .ToListAsync();
        Assert.Single(defaults);
        Assert.Equal(seeded.SpaceId, defaults[0].Id);
    }

    [Fact]
    public async Task SeedUserAsync_TwoUsers_GetDistinctUsersAndSpaces()
    {
        var a = await fixture.SeedUserAsync("a@example.com", "A");
        var b = await fixture.SeedUserAsync("b@example.com", "B");
        Assert.NotEqual(a.UserId, b.UserId);
        Assert.NotEqual(a.SpaceId, b.SpaceId);
    }

    [Fact]
    public async Task CreateAuthenticatedClientAsync_AuthenticatesAsSeededUser()
    {
        var seeded = await fixture.SeedUserAsync("session@example.com", "Session");
        using var client = await fixture.CreateAuthenticatedClientAsync(seeded.UserId);

        var resp = await client.GetAsync("/api/auth/me");
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadAsStringAsync();
        Assert.Contains(seeded.UserId.ToString(), body);
    }
}
