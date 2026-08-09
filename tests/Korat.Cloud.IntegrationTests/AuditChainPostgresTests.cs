using Korat.Cloud.Security.Audit;
using Korat.Persistence;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Testcontainers.PostgreSql;
using Xunit.Abstractions;

namespace Korat.Cloud.IntegrationTests;

/// <summary>
/// Postgres-backed audit hash-chain round-trip tests.
///
/// These tests exercise the relational path (FOR UPDATE lock, timestamptz write/read,
/// chain verification) that the InMemory-backed suite cannot cover. They are the
/// authoritative gate for the blocking flaw fixed in #57 Leg 3: OccurredAtUtc must be
/// truncated to whole microseconds before hashing so that Npgsql's timestamptz
/// round-trip does not break the chain on Linux (where UtcNow has sub-µs ticks).
///
/// Skipped (not failed) when Docker is unavailable so the suite stays green in
/// environments without a container runtime.
/// </summary>
[Trait("Category", "AuditChainPostgres")]
public sealed class AuditChainPostgresTests : IAsyncLifetime
{
    private readonly ITestOutputHelper _output;
    private PostgreSqlContainer? _postgres;
    private string? _connectionString;

    public AuditChainPostgresTests(ITestOutputHelper output) => _output = output;

    public async Task InitializeAsync()
    {
        try
        {
            _postgres = new PostgreSqlBuilder()
                .WithImage("postgres:16-alpine")
                .WithDatabase("korat_audit_test")
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
                $"[SKIP] SKIPPED: Docker unavailable — relational invariants not checked " +
                $"(AuditChainPostgresTests: postgres:16-alpine container failed to start: {ex.Message})");
        }
    }

    public async Task DisposeAsync()
    {
        if (_postgres is not null)
            await _postgres.DisposeAsync();
    }

    private const string DockerSkipReason =
        "SKIPPED: Docker unavailable — relational invariants not checked " +
        "(AuditChainPostgresTests requires a running Docker daemon with postgres:16-alpine).";

    private void SkipIfNoDocker()
    {
        if (_connectionString is null)
        {
            _output.WriteLine(DockerSkipReason);
            Console.Error.WriteLine($"[SKIP] {DockerSkipReason}");
            throw Xunit.Sdk.SkipException.ForSkip(DockerSkipReason);
        }
    }

    private async Task<PgDbContextFactory> BuildMigratedFactoryAsync()
    {
        var factory = new PgDbContextFactory(_connectionString!);
        await using var db = factory.CreateDbContext();
        await db.Database.MigrateAsync();
        return factory;
    }

    private static AuditLogger MakeLogger(IDbContextFactory<KoratDbContext> factory) =>
        new(factory,
            new HttpContextAccessor(),
            new ConfigurationBuilder().Build(),
            NullLogger<AuditLogger>.Instance,
            NullLoggerFactory.Instance);

    // ── Tests ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Core regression: write two chained events through the real Postgres relational path
    /// (FOR UPDATE lock, timestamptz write/read) and verify the chain is intact.
    ///
    /// Before the fix (truncate OccurredAtUtc to µs), this test would produce
    /// ok=false on Linux because sub-µs ticks survived the canonical string but were
    /// truncated by Npgsql, making the re-read recomputation mismatch — reporting false
    /// tampering on essentially every production row.
    /// </summary>
    [Fact]
    public async Task Postgres_ChainedEvents_VerifyReturnsOk()
    {
        SkipIfNoDocker();

        var factory = await BuildMigratedFactoryAsync();
        var logger = MakeLogger(factory);

        var seq1 = await logger.RecordAsync(new AuditEvent("invite.create", "invite", "i-pg-1"), required: true);
        var seq2 = await logger.RecordAsync(new AuditEvent("invite.revoke", "invite", "i-pg-1"), required: true);
        Assert.Equal(1L, seq1);
        Assert.Equal(2L, seq2);

        var verifier = new AuditVerifier(factory);
        var result = await verifier.VerifyAsync();

        Assert.True(result.Ok,
            $"Chain verify must return ok=true after clean insert/read through Postgres " +
            $"(firstBrokenSeq={result.FirstBrokenSeq}, checkedCount={result.CheckedCount}). " +
            $"A failure on seq=1 typically means OccurredAtUtc sub-µs precision was not truncated.");
        Assert.Equal(2L, result.CheckedCount);
        Assert.False(result.HeadMismatch);
    }

    /// <summary>
    /// Verifies that OccurredAtUtc is stored and re-read with exactly microsecond precision —
    /// i.e. the written ticks are divisible by 10 (1 µs = 10 ticks) and the hash recomputed
    /// from the re-read row matches the stored RowHash exactly.
    /// </summary>
    [Fact]
    public async Task Postgres_OccurredAtUtc_RoundTrips_Exactly_At_Microsecond_Precision()
    {
        SkipIfNoDocker();

        var factory = await BuildMigratedFactoryAsync();
        var logger = MakeLogger(factory);

        await logger.RecordAsync(new AuditEvent("secret.set", "inference_point", "p-pg-rtt"), required: true);

        await using var db = factory.CreateDbContext();
        var row = await db.AuditEvents.AsNoTracking().OrderBy(e => e.Seq).FirstAsync();

        // The stored value must be µs-aligned (Npgsql truncates sub-µs on write).
        var subMicrosecondRemainder = row.OccurredAtUtc.UtcTicks % 10;
        Assert.Equal(0L, subMicrosecondRemainder);

        // The hash recomputed from the re-read row must match what was stored.
        var recomputed = AuditHasher.ComputeRowHash(AuditCanonical.Canonicalize(row), row.PrevHash);
        Assert.True(recomputed.AsSpan().SequenceEqual(row.RowHash),
            "Recomputed hash on re-read row must match stored RowHash. If it differs, the " +
            "canonical string changed between write and read (OccurredAtUtc precision mismatch).");
    }

    /// <summary>
    /// Tamper detection still works through the real Postgres path.
    /// </summary>
    [Fact]
    public async Task Postgres_TamperedRow_ReportsFirstBrokenSeq()
    {
        SkipIfNoDocker();

        var factory = await BuildMigratedFactoryAsync();
        var logger = MakeLogger(factory);

        await logger.RecordAsync(new AuditEvent("invite.create", "invite", "i-pg-t1"), required: true);
        await logger.RecordAsync(new AuditEvent("invite.revoke", "invite", "i-pg-t1"), required: true);
        await logger.RecordAsync(new AuditEvent("grant.revoke",  "grant",  "g-pg-t1"), required: true);

        // Tamper row 2 (hide the revocation).
        await using (var db = factory.CreateDbContext())
        {
            var row = await db.AuditEvents.SingleAsync(e => e.Seq == 2);
            row.Action = "invite.create";
            await db.SaveChangesAsync();
        }

        var verifier = new AuditVerifier(factory);
        var result = await verifier.VerifyAsync();

        Assert.False(result.Ok);
        Assert.Equal(2L, result.FirstBrokenSeq);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private sealed class PgDbContextFactory(string connectionString) : IDbContextFactory<KoratDbContext>
    {
        public KoratDbContext CreateDbContext()
        {
            var options = new DbContextOptionsBuilder<KoratDbContext>()
                .UseNpgsql(connectionString)
                .Options;
            return new KoratDbContext(options);
        }

        public Task<KoratDbContext> CreateDbContextAsync(CancellationToken ct = default) =>
            Task.FromResult(CreateDbContext());
    }
}
