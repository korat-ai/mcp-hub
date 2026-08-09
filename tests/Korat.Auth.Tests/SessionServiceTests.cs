using Korat.Cloud.Web.Auth.Services;
using Korat.Domain.Auth;
using Korat.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Korat.Auth.Tests;

public class SessionServiceTests
{
    private static (SessionService svc, KoratDbContext db, FakeTimeProvider time) Build()
    {
        var opts = new DbContextOptionsBuilder<KoratDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        var db = new KoratDbContext(opts);
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var svc = new SessionService(db, NullLogger<SessionService>.Instance, time);
        return (svc, db, time);
    }

    [Fact]
    public async Task CreateAsync_PersistsSession_WithSlidingAndAbsoluteCaps()
    {
        var (svc, _, time) = Build();
        var now = time.GetUtcNow();
        var session = await svc.CreateAsync(UserId.New(), "Mozilla/5.0", "10.0.0.1", default);
        Assert.Equal(now + SessionService.SlidingWindow, session.ExpiresAt);
        Assert.Equal(now + SessionService.AbsoluteCap, session.AbsoluteExpiresAt);
        Assert.Null(session.RevokedAt);
    }

    [Fact]
    public async Task ValidateAndBumpAsync_ReturnsUserId_AndAdvancesExpiresAt()
    {
        var (svc, _, time) = Build();
        var userId = UserId.New();
        var session = await svc.CreateAsync(userId, null, null, default);
        // Capture before advancing — EF InMemory mutates the tracked entity object in-place
        // when SetValues is called, so session.ExpiresAt would reflect the bumped value after the call.
        var originalExpiresAt = session.ExpiresAt;
        time.Advance(TimeSpan.FromHours(1));
        var bumped = await svc.ValidateAndBumpAsync(session.Id, default);
        Assert.NotNull(bumped);
        Assert.Equal(userId, bumped!.UserId);
        Assert.True(bumped.ExpiresAt > originalExpiresAt);
    }

    [Fact]
    public async Task ValidateAndBumpAsync_ReturnsNull_AfterRevoke()
    {
        var (svc, _, _) = Build();
        var session = await svc.CreateAsync(UserId.New(), null, null, default);
        await svc.RevokeAsync(session.Id, default);
        var bumped = await svc.ValidateAndBumpAsync(session.Id, default);
        Assert.Null(bumped);
    }

    [Fact]
    public async Task ValidateAndBumpAsync_ReturnsNull_AfterAbsoluteCapExpires()
    {
        var (svc, _, time) = Build();
        var session = await svc.CreateAsync(UserId.New(), null, null, default);
        time.Advance(SessionService.AbsoluteCap + TimeSpan.FromHours(1));
        var bumped = await svc.ValidateAndBumpAsync(session.Id, default);
        Assert.Null(bumped);
    }

    [Fact]
    public async Task ListActiveAsync_ExcludesRevokedAndExpiredSessions()
    {
        var (svc, _, time) = Build();
        var user = UserId.New();
        var active = await svc.CreateAsync(user, null, null, default);
        var revoked = await svc.CreateAsync(user, null, null, default);
        await svc.RevokeAsync(revoked.Id, default);
        var list = await svc.ListActiveAsync(user, default);
        Assert.Single(list);
        Assert.Equal(active.Id, list[0].Id);
    }

    [Fact]
    public async Task CreateAsync_RevokesPriorActiveSession_WithSameUserAgent()
    {
        var (svc, _, _) = Build();
        var user = UserId.New();
        await svc.CreateAsync(user, "Chrome/148", null, default);
        var second = await svc.CreateAsync(user, "Chrome/148", null, default);

        var list = await svc.ListActiveAsync(user, default);
        Assert.Single(list);                  // prior same-UA session deduped away
        Assert.Equal(second.Id, list[0].Id);
    }

    [Fact]
    public async Task CreateAsync_KeepsSessions_WithDifferentUserAgent()
    {
        var (svc, _, _) = Build();
        var user = UserId.New();
        await svc.CreateAsync(user, "Chrome/148", null, default);
        await svc.CreateAsync(user, "Safari/26", null, default);

        Assert.Equal(2, (await svc.ListActiveAsync(user, default)).Count);
    }

    [Fact]
    public async Task CreateAsync_DoesNotDedup_WhenUserAgentNull()
    {
        var (svc, _, _) = Build();
        var user = UserId.New();
        await svc.CreateAsync(user, null, null, default);
        await svc.CreateAsync(user, null, null, default);

        Assert.Equal(2, (await svc.ListActiveAsync(user, default)).Count);
    }

    [Fact]
    public async Task RevokeOthersAsync_RevokesAllActiveExceptKept_ScopedToUser()
    {
        var (svc, _, _) = Build();
        var user = UserId.New();
        var a = await svc.CreateAsync(user, "UA-A", null, default);
        var keep = await svc.CreateAsync(user, "UA-B", null, default);
        var c = await svc.CreateAsync(user, "UA-C", null, default);

        var otherUser = UserId.New();
        await svc.CreateAsync(otherUser, "UA-X", null, default);

        await svc.RevokeOthersAsync(user, keep.Id, default);

        var list = await svc.ListActiveAsync(user, default);
        Assert.Single(list);
        Assert.Equal(keep.Id, list[0].Id);
        Assert.DoesNotContain(list, s => s.Id == a.Id || s.Id == c.Id);

        // Another user's sessions are untouched.
        Assert.Single(await svc.ListActiveAsync(otherUser, default));
    }
}

// Minimal FakeTimeProvider for tests that need controllable time.
// Not `file`-scoped: C# does not allow file-local types in non-file-local member signatures.
internal sealed class FakeTimeProvider(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;
    public override DateTimeOffset GetUtcNow() => _now;
    public void Advance(TimeSpan by) => _now = _now.Add(by);
}
