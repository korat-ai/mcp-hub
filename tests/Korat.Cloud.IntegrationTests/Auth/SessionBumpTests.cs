using Korat.Cloud.Web.Auth.Services;
using Korat.Domain.Auth;
using Korat.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Korat.Cloud.IntegrationTests.Auth;

/// <summary>
/// Integration tests for SessionService.ValidateAndBumpAsync and RevokeAsync.
/// Each test uses its own isolated InMemory database for deterministic isolation.
///
/// The InMemory branch of these methods is exercised here. Race-safety (Postgres
/// serialised UPDATE) is out of scope for InMemory — see InviteRaceTests for
/// the [Skip] pattern.
/// </summary>
public sealed class SessionBumpTests
{
    private static (SessionService svc, KoratDbContext db) Build()
    {
        var opts = new DbContextOptionsBuilder<KoratDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        var db = new KoratDbContext(opts);
        var svc = new SessionService(db, NullLogger<SessionService>.Instance, TimeProvider.System);
        return (svc, db);
    }

    [Fact]
    public async Task ValidateAndBump_Succeeds_OnValidSession()
    {
        var (svc, _) = Build();
        var userId = UserId.New();
        var session = await svc.CreateAsync(userId, "TestUA", "10.0.0.1", default);

        var result = await svc.ValidateAndBumpAsync(session.Id, default);

        Assert.NotNull(result);
        Assert.Equal(userId, result!.UserId);
        Assert.True(result.ExpiresAt > DateTimeOffset.UtcNow);
    }

    [Fact]
    public async Task ValidateAndBump_ReturnsNull_AfterRevoke()
    {
        var (svc, _) = Build();
        var session = await svc.CreateAsync(UserId.New(), null, null, default);
        await svc.RevokeAsync(session.Id, default);

        var result = await svc.ValidateAndBumpAsync(session.Id, default);

        Assert.Null(result);
    }

    [Fact]
    public async Task RevokeAsync_IsIdempotent_SecondRevokeDoesNotThrow()
    {
        var (svc, _) = Build();
        var session = await svc.CreateAsync(UserId.New(), null, null, default);

        await svc.RevokeAsync(session.Id, default);
        // Second revoke should be a no-op, not throw.
        await svc.RevokeAsync(session.Id, default);
    }

    [Fact]
    public async Task ListActiveAsync_ExcludesRevokedSessions()
    {
        var (svc, _) = Build();
        var userId = UserId.New();
        var s1 = await svc.CreateAsync(userId, "UA1", null, default);
        var s2 = await svc.CreateAsync(userId, "UA2", null, default);
        await svc.RevokeAsync(s1.Id, default);

        var active = await svc.ListActiveAsync(userId, default);

        Assert.Single(active);
        Assert.Equal(s2.Id, active[0].Id);
    }
}
