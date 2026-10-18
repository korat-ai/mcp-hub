using Korat.Cloud.Web.Auth.Services;
using Korat.Domain.Auth;
using Korat.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Testcontainers.PostgreSql;

namespace Korat.Auth.Tests;

public sealed class SessionServicePostgresTests
{
    [SkippableFact]
    public async Task ValidateAndBumpAsync_AtomicSql_RequiresActiveUser()
    {
        PostgreSqlContainer? postgres = null;
        var connectionString = Environment.GetEnvironmentVariable("KORAT_TEST_POSTGRES");
        try
        {
            if (string.IsNullOrWhiteSpace(connectionString))
            {
                try
                {
                    postgres = new PostgreSqlBuilder("postgres:16-alpine").Build();
                    await postgres.StartAsync();
                    connectionString = postgres.GetConnectionString();
                }
                catch (Exception ex)
                {
                    throw new SkipException($"Docker/Postgres unavailable: {ex.GetType().Name}");
                }
            }

            var options = new DbContextOptionsBuilder<KoratDbContext>()
                .UseNpgsql(connectionString)
                .Options;
            await using var db = new KoratDbContext(options);
            await db.Database.EnsureCreatedAsync();

            var clock = new MutableTime(DateTimeOffset.Parse("2026-10-18T10:00:00Z"));
            var service = new SessionService(db, NullLogger<SessionService>.Instance, clock);
            var userId = UserId.New();
            db.Users.Add(new User
            {
                Id = userId,
                PrimaryEmail = $"postgres-session-{userId.Value:N}@example.com",
                DisplayName = "Postgres Session User",
                CreatedAt = clock.GetUtcNow(),
                Status = UserStatus.Active,
                IsAdmin = false,
            });
            await db.SaveChangesAsync();

            var session = await service.CreateAsync(userId, null, null, default);
            clock.Advance(TimeSpan.FromHours(1));
            Assert.NotNull(await service.ValidateAndBumpAsync(session.Id, default));

            var bumped = await db.AuthSessions.AsNoTracking().SingleAsync(s => s.Id == session.Id);
            var user = await db.Users.SingleAsync(u => u.Id == userId);
            db.Entry(user).CurrentValues.SetValues(user with { Status = UserStatus.Suspended });
            await db.SaveChangesAsync();

            clock.Advance(TimeSpan.FromHours(1));
            Assert.Null(await service.ValidateAndBumpAsync(session.Id, default));

            var rejected = await db.AuthSessions.AsNoTracking().SingleAsync(s => s.Id == session.Id);
            Assert.Equal(bumped.LastUsedAt, rejected.LastUsedAt);
            Assert.Equal(bumped.ExpiresAt, rejected.ExpiresAt);
        }
        finally
        {
            if (postgres is not null)
                await postgres.DisposeAsync();
        }
    }

    private sealed class MutableTime(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now = _now.Add(by);
    }
}
