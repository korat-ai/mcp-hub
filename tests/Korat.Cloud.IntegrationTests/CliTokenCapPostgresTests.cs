using Korat.Cloud.Web.Auth.Services;
using Korat.Domain.Auth;
using Korat.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Testcontainers.PostgreSql;
using Xunit.Abstractions;

namespace Korat.Cloud.IntegrationTests;

/// <summary>
/// Postgres-backed CLI token absolute-lifetime-cap (F41) tests.
///
/// These tests drive <see cref="CliTokenService.ValidateAsync"/> against a real Postgres
/// container so that the raw-SQL UPDATE/SELECT branches — and in particular the cap predicate
///   <c>t."Scope" &lt;&gt; 'full' OR t."IssuedAt" > {absoluteDeadline}</c>
/// — are exercised. The InMemory-backed suite in Korat.Auth.Tests validates only the
/// LINQ predicate path and does NOT cover the production SQL path.
///
/// Skipped (not failed) when Docker is unavailable so the suite stays green in
/// environments without a container runtime.
/// </summary>
[Trait("Category", "CliTokenCapPostgres")]
public sealed class CliTokenCapPostgresTests : IAsyncLifetime
{
    private readonly ITestOutputHelper _output;
    private PostgreSqlContainer? _postgres;
    private string? _connectionString;

    public CliTokenCapPostgresTests(ITestOutputHelper output) => _output = output;

    public async Task InitializeAsync()
    {
        try
        {
            _postgres = new PostgreSqlBuilder()
                .WithImage("postgres:16-alpine")
                .WithDatabase("korat_clicap_test")
                .WithUsername("korat")
                .WithPassword("korat")
                .Build();
            await _postgres.StartAsync();
            _connectionString = _postgres.GetConnectionString();
        }
        catch (Exception ex)
        {
            // Docker unavailable — container stays null; tests skip via SkipIfNoDocker().
            _postgres = null;
            _connectionString = null;
            Console.Error.WriteLine(
                $"[SKIP] SKIPPED: Docker unavailable — F41 SQL cap predicate not checked " +
                $"(CliTokenCapPostgresTests: postgres:16-alpine container failed to start: {ex.Message})");
        }
    }

    public async Task DisposeAsync()
    {
        if (_postgres is not null)
            await _postgres.DisposeAsync();
    }

    // ── Skip helpers ──────────────────────────────────────────────────────────

    private const string DockerSkipReason =
        "SKIPPED: Docker unavailable — F41 SQL cap predicate not checked " +
        "(CliTokenCapPostgresTests requires a running Docker daemon with postgres:16-alpine).";

    private void SkipIfNoDocker()
    {
        if (_connectionString is null)
        {
            _output.WriteLine(DockerSkipReason);
            Console.Error.WriteLine($"[SKIP] {DockerSkipReason}");
            throw Xunit.Sdk.SkipException.ForSkip(DockerSkipReason);
        }
    }

    // ── DB factory + migration ────────────────────────────────────────────────

    private KoratDbContext BuildMigratedDbContext()
    {
        var options = new DbContextOptionsBuilder<KoratDbContext>()
            .UseNpgsql(_connectionString!)
            .Options;
        return new KoratDbContext(options);
    }

    private async Task EnsureMigratedAsync()
    {
        await using var db = BuildMigratedDbContext();
        await db.Database.MigrateAsync();
    }

    // ── Seed helper ───────────────────────────────────────────────────────────

    /// <summary>
    /// Inserts a fresh Active <see cref="User"/> row so that
    /// <see cref="CliTokenService.ValidateAsync"/> JOIN lookups succeed.
    /// Each test call uses a distinct <see cref="Guid"/> to avoid cross-test collisions.
    /// </summary>
    private static async Task<Guid> SeedActiveUserAsync(KoratDbContext db)
    {
        var id = Guid.NewGuid();
        db.Users.Add(new User
        {
            Id        = new UserId(id),
            PrimaryEmail  = $"cap-test-{id:N}@example.com",
            DisplayName   = "Cap Test User",
            CreatedAt     = DateTimeOffset.UtcNow,
            Status        = UserStatus.Active,
            IsAdmin       = false,
        });
        await db.SaveChangesAsync();
        return id;
    }

    // ── Tests ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// F41 — full-scope token must be rejected at day 366 by the raw-SQL cap predicate
    /// even when the sliding window was kept alive by 30-day renewal calls.
    ///
    /// This is the authoritative gate for the production SQL path; the InMemory twin in
    /// Korat.Auth.Tests exercises only the LINQ predicate branch.
    /// </summary>
    [Fact]
    public async Task FullToken_rejected_by_absolute_cap_via_SQL()
    {
        SkipIfNoDocker();
        await EnsureMigratedAsync();

        var clock = new MutableTime(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
        await using var db = BuildMigratedDbContext();
        var svc    = new CliTokenService(db, NullLogger<CliTokenService>.Instance, clock);
        var userId = await SeedActiveUserAsync(db);
        var r      = await svc.IssueAsync(userId, "full", default);

        // Keep the sliding window alive: validate every 30 days for 12 rounds (day 360).
        for (var i = 0; i < 12; i++)
        {
            clock.Advance(TimeSpan.FromDays(30));
            var mid = await svc.ValidateAsync(r.RawToken, default);
            Assert.NotNull(mid); // must still be valid inside the 365-day cap
        }

        // Advance 6 more days: now at day 366, past the 365-day absolute cap.
        clock.Advance(TimeSpan.FromDays(6));

        // The SQL cap predicate must fire — token invalid despite live sliding window.
        Assert.Null(await svc.ValidateAsync(r.RawToken, default));
    }

    /// <summary>
    /// F41 — bridge-only tokens are exempt from the absolute cap; the SQL predicate
    /// <c>(t."Scope" &lt;&gt; 'full' OR t."IssuedAt" > {absoluteDeadline})</c> must
    /// pass them through even at day 366.
    /// </summary>
    [Fact]
    public async Task BridgeOnly_exempt_from_absolute_cap_via_SQL()
    {
        SkipIfNoDocker();
        await EnsureMigratedAsync();

        var clock = new MutableTime(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
        await using var db = BuildMigratedDbContext();
        var svc    = new CliTokenService(db, NullLogger<CliTokenService>.Instance, clock);
        var userId = await SeedActiveUserAsync(db);
        var r      = await svc.IssueAsync(userId, "bridge-only", default);

        // Same 30-day renewal loop as the full-token test.
        for (var i = 0; i < 12; i++)
        {
            clock.Advance(TimeSpan.FromDays(30));
            _ = await svc.ValidateAsync(r.RawToken, default);
        }

        // Advance past the 365-day cap threshold (day 366).
        clock.Advance(TimeSpan.FromDays(6));

        // bridge-only must still be valid — absolute cap does not apply to this scope.
        Assert.Equal(userId, await svc.ValidateAsync(r.RawToken, default));
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Minimal controllable <see cref="TimeProvider"/> for integration tests.
    /// Defined here because <c>Microsoft.Extensions.TimeProvider.Testing</c> is not
    /// referenced by this project, and the internal <c>FakeTimeProvider</c> in
    /// Korat.Auth.Tests is not accessible across assembly boundaries.
    /// </summary>
    private sealed class MutableTime(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now = _now.Add(by);
    }
}
