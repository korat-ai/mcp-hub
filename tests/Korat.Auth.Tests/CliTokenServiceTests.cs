using Korat.Cloud.Web.Auth.Services;
using Korat.Domain.Auth;
using Korat.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Korat.Auth.Tests;

public class CliTokenServiceTests
{
    // Tests run on UseInMemoryDatabase. The unique TokenHash index, FK cascade, and
    // atomic UPDATE behavior are not exercised here; those are covered by integration
    // tests (see Task 14). This matches the InMemory disclaimer convention used across
    // all auth service test fixtures.
    //
    // ValidateAsync joins User (for account status check). Tests must seed a User row
    // so the InMemory LINQ join resolves — the production path does this via FK.
    private static (CliTokenService svc, KoratDbContext db, FakeTimeProvider time) Build()
    {
        var opts = new DbContextOptionsBuilder<KoratDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        var db = new KoratDbContext(opts);
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-05-30T00:00:00Z"));
        return (new CliTokenService(db, NullLogger<CliTokenService>.Instance, time), db, time);
    }

    /// <summary>
    /// Seeds an active User row so that CliTokenService.ValidateAsync joins succeed.
    /// Must be called before IssueAsync for any test that exercises ValidateAsync.
    /// </summary>
    private static async Task<Guid> SeedActiveUserAsync(KoratDbContext db, Guid? userId = null)
    {
        var id = userId ?? Guid.NewGuid();
        db.Users.Add(new User
        {
            Id = new UserId(id),
            PrimaryEmail = $"test-{id:N}@example.com",
            DisplayName = "Test User",
            CreatedAt = DateTimeOffset.UtcNow,
            Status = UserStatus.Active,
            IsAdmin = false,
        });
        await db.SaveChangesAsync();
        return id;
    }

    [Fact]
    public async Task IssueAsync_returns_prefixed_token_and_validates_to_user()
    {
        var (svc, db, _) = Build();
        var userId = await SeedActiveUserAsync(db);
        var r = await svc.IssueAsync(userId, "full", default);
        Assert.StartsWith("korat_cli_", r.RawToken);
        Assert.Equal(userId, await svc.ValidateAsync(r.RawToken, default));
    }

    [Fact]
    public async Task ValidateAsync_returns_null_for_revoked()
    {
        var (svc, db, _) = Build();
        var userId = await SeedActiveUserAsync(db);
        var r = await svc.IssueAsync(userId, "full", default);
        await svc.RevokeAsync(r.RawToken, default);
        Assert.Null(await svc.ValidateAsync(r.RawToken, default));
    }

    [Fact]
    public async Task ValidateAsync_returns_null_for_expired()
    {
        var (svc, db, time) = Build();
        var userId = await SeedActiveUserAsync(db);
        var r = await svc.IssueAsync(userId, "full", default);
        time.Advance(TimeSpan.FromDays(91));
        Assert.Null(await svc.ValidateAsync(r.RawToken, default));
    }

    [Fact]
    public async Task ValidateAsync_rolling_refresh_extends_expiry()
    {
        var (svc, db, time) = Build();
        var userId = await SeedActiveUserAsync(db);
        var r = await svc.IssueAsync(userId, "full", default);
        time.Advance(TimeSpan.FromDays(2));
        await svc.ValidateAsync(r.RawToken, default);
        var stored = await db.CliTokens.SingleAsync();
        // ExpiresAt should be ~90 days from now (2026-05-30 + 2 days + 90 days).
        Assert.True(stored.ExpiresAt > DateTimeOffset.Parse("2026-08-28T00:00:00Z"));
    }

    [Fact]
    public async Task ValidateAsync_within_rolling_renewal_does_not_extend_expiry()
    {
        var (svc, db, time) = Build();
        var issuedAt = time.GetUtcNow();
        var userId = await SeedActiveUserAsync(db);
        var r = await svc.IssueAsync(userId, "full", default);
        var originalExpiry = (await db.CliTokens.SingleAsync()).ExpiresAt;

        // Advance less than the 1-day rolling renewal window.
        time.Advance(TimeSpan.FromHours(1));
        await svc.ValidateAsync(r.RawToken, default);

        var stored = await db.CliTokens.SingleAsync();
        // ExpiresAt must NOT have been extended; it should equal the original issue expiry.
        Assert.Equal(originalExpiry, stored.ExpiresAt);
        // LastUsedAt must also be unchanged (no write occurred).
        Assert.Equal(issuedAt, stored.LastUsedAt);
    }

    [Fact]
    public async Task ValidateAsync_returns_null_for_suspended_user()
    {
        // Tokens for suspended users must not validate — account status is checked on every call.
        var (svc, db, _) = Build();
        var id = Guid.NewGuid();
        db.Users.Add(new User
        {
            Id = new UserId(id),
            PrimaryEmail = $"suspended-{id:N}@example.com",
            DisplayName = "Suspended",
            CreatedAt = DateTimeOffset.UtcNow,
            Status = UserStatus.Suspended,
            IsAdmin = false,
        });
        await db.SaveChangesAsync();
        var r = await svc.IssueAsync(id, "full", default);
        Assert.Null(await svc.ValidateAsync(r.RawToken, default));
    }

    [Fact]
    public async Task IssueAsync_invalid_scope_throws()
    {
        var (svc, _, _) = Build();
        await Assert.ThrowsAsync<ArgumentException>(
            () => svc.IssueAsync(Guid.NewGuid(), "wat", default));
    }

    [Fact]
    public async Task ValidateAsync_unknown_token_returns_null()
    {
        var (svc, _, _) = Build();
        Assert.Null(await svc.ValidateAsync("korat_cli_nope", default));
    }

    [Fact]
    public async Task RevokeAsync_returns_false_for_unknown_token()
    {
        var (svc, _, _) = Build();
        var result = await svc.RevokeAsync("korat_cli_doesnotexist", default);
        Assert.False(result);
    }

    [Fact]
    public async Task RevokeAsync_is_idempotent_for_already_revoked_token()
    {
        var (svc, db, _) = Build();
        var userId = await SeedActiveUserAsync(db);
        var r = await svc.IssueAsync(userId, "full", default);
        var first = await svc.RevokeAsync(r.RawToken, default);
        var second = await svc.RevokeAsync(r.RawToken, default);
        Assert.True(first);
        Assert.False(second);
        // Token still validates as null after double revoke.
        Assert.Null(await svc.ValidateAsync(r.RawToken, default));
    }

    [Fact]
    public async Task RevokeAllForUserAsync_revokes_all_live_tokens_for_target_user_only()
    {
        var (svc, db, _) = Build();
        var targetUser = await SeedActiveUserAsync(db);
        var otherUser = await SeedActiveUserAsync(db);

        var r1 = await svc.IssueAsync(targetUser, "full", default);
        var r2 = await svc.IssueAsync(targetUser, "bridge-only", default);
        var r3 = await svc.IssueAsync(otherUser, "full", default);

        var count = await svc.RevokeAllForUserAsync(targetUser, default);

        Assert.Equal(2, count);
        Assert.Null(await svc.ValidateAsync(r1.RawToken, default));
        Assert.Null(await svc.ValidateAsync(r2.RawToken, default));
        // Other user's token must remain valid.
        Assert.Equal(otherUser, await svc.ValidateAsync(r3.RawToken, default));
    }

    [Fact]
    public async Task RevokeAllForUserAsync_returns_zero_when_no_live_tokens()
    {
        var (svc, _, _) = Build();
        var count = await svc.RevokeAllForUserAsync(Guid.NewGuid(), default);
        Assert.Equal(0, count);
    }

    // ── MAJOR-2: ValidateWithScopeAsync returns scope alongside userId ────────

    [Fact]
    public async Task ValidateWithScopeAsync_returns_userId_and_scope_for_full_token()
    {
        var (svc, db, _) = Build();
        var userId = await SeedActiveUserAsync(db);
        var r = await svc.IssueAsync(userId, "full", default);

        var result = await svc.ValidateWithScopeAsync(r.RawToken, default);

        Assert.NotNull(result);
        Assert.Equal(userId, result!.Value.UserId);
        Assert.Equal("full", result.Value.Scope);
    }

    [Fact]
    public async Task ValidateWithScopeAsync_returns_userId_and_scope_for_bridge_only_token()
    {
        var (svc, db, _) = Build();
        var userId = await SeedActiveUserAsync(db);
        var r = await svc.IssueAsync(userId, "bridge-only", default);

        var result = await svc.ValidateWithScopeAsync(r.RawToken, default);

        Assert.NotNull(result);
        Assert.Equal(userId, result!.Value.UserId);
        Assert.Equal("bridge-only", result.Value.Scope);
    }

    [Fact]
    public async Task ValidateWithScopeAsync_returns_null_for_revoked_token()
    {
        var (svc, db, _) = Build();
        var userId = await SeedActiveUserAsync(db);
        var r = await svc.IssueAsync(userId, "full", default);
        await svc.RevokeAsync(r.RawToken, default);

        Assert.Null(await svc.ValidateWithScopeAsync(r.RawToken, default));
    }

    [Fact]
    public async Task ValidateWithScopeAsync_returns_null_for_unknown_token()
    {
        var (svc, _, _) = Build();
        Assert.Null(await svc.ValidateWithScopeAsync("korat_cli_unknown", default));
    }

    // ── F41: absolute lifetime cap on full-scope tokens ──────────────────────

    /// <summary>
    /// A "full" token kept alive by repeated use must be rejected once 365 days have
    /// elapsed from IssuedAt — even when ExpiresAt is still in the future (sliding
    /// window renewed by activity).  The user must re-run `korat login`.
    /// </summary>
    [Fact]
    public async Task ValidateAsync_full_token_rejected_after_absolute_cap_despite_sliding_window()
    {
        var (svc, db, time) = Build();
        var userId = await SeedActiveUserAsync(db);
        var r = await svc.IssueAsync(userId, "full", default);

        // Simulate activity every 30 days to keep the sliding window alive.
        // After 12 rounds we are at day 360 (inside the 365-day cap, still valid).
        for (var i = 0; i < 12; i++)
        {
            time.Advance(TimeSpan.FromDays(30));
            // ValidateAsync extends ExpiresAt each call (outside the rolling renewal window)
            // — so the sliding window remains open throughout.
            var mid = await svc.ValidateAsync(r.RawToken, default);
            Assert.NotNull(mid); // must still be valid inside the cap
        }

        // Now at day 360. Advance 6 more days to cross the 365-day absolute cap (day 366).
        time.Advance(TimeSpan.FromDays(6));

        // At day 366, the absolute cap (365 days from IssuedAt) must have fired.
        Assert.Null(await svc.ValidateAsync(r.RawToken, default));
    }

    /// <summary>
    /// "bridge-only" tokens are exempt from the absolute cap — they are machine relay
    /// credentials whose lifetime is managed solely via explicit revocation.
    /// </summary>
    [Fact]
    public async Task ValidateAsync_bridge_only_token_not_rejected_by_absolute_cap()
    {
        var (svc, db, time) = Build();
        var userId = await SeedActiveUserAsync(db);
        var r = await svc.IssueAsync(userId, "bridge-only", default);

        // Advance past the full-scope absolute cap (365 days), keeping the sliding window
        // alive via 30-day renewal calls — same pattern a long-lived daemon would exhibit.
        for (var i = 0; i < 12; i++)
        {
            time.Advance(TimeSpan.FromDays(30));
            _ = await svc.ValidateAsync(r.RawToken, default);
        }
        // Now at day 360; advance 6 more days so we are at day 366 (past 365-day cap).
        time.Advance(TimeSpan.FromDays(6));

        // bridge-only must still be valid at day 366 (absolute cap does not apply).
        Assert.Equal(userId, await svc.ValidateAsync(r.RawToken, default));
    }
}
