using Korat.Cloud.Observability;
using Korat.Cloud.Web.Auth;
using Korat.Cloud.Web.Auth.Security;
using Korat.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Korat.Cloud.Security.Audit;

/// <summary>
/// 032 (#57 Leg 3 C1): the production <see cref="IAuditLog"/>.
///
/// Write path (relational / Postgres):
///   1. BEGIN; SELECT the single AuditChainHead row FOR UPDATE (serializes the chain).
///   2. Seq = LastSeq + 1; canonicalize; RowHash = SHA256(canonical || PrevHash).
///   3. INSERT AuditEvents row; UPDATE head; COMMIT.
/// InMemory (tests): same logic without the row lock — the integration-test assembly runs
/// with DisableTestParallelization, and InMemory does not support raw SQL/transactions.
///
/// Every event is mirrored (structured, secret-free) to ILogger category "Korat.Audit" —
/// an off-box best-effort copy on the Fly log stream.
///
/// DI lifetime: SINGLETON (uses IDbContextFactory; no scoped dependencies) so it is
/// injectable into singleton services such as SpaceDekProvider.
/// </summary>
public sealed class AuditLogger : IAuditLog
{
    /// <summary>Cap stored DetailsJson — audit rows are evidence, not payload storage.</summary>
    private const int MaxDetailsLength = 2048;

    private readonly IDbContextFactory<KoratDbContext> _dbFactory;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly bool _trustForwardedIp;
    private readonly ILogger<AuditLogger> _logger;
    private readonly ILogger _auditMirror;

    public AuditLogger(
        IDbContextFactory<KoratDbContext> dbFactory,
        IHttpContextAccessor httpContextAccessor,
        IConfiguration configuration,
        ILogger<AuditLogger> logger,
        ILoggerFactory loggerFactory)
    {
        _dbFactory = dbFactory;
        _httpContextAccessor = httpContextAccessor;
        // Same switch the rate limiter uses (Program.cs binds Korat:Cloud:TrustForwardedIp).
        _trustForwardedIp = configuration.GetValue<bool>("Korat:Cloud:TrustForwardedIp");
        _logger = logger;
        _auditMirror = loggerFactory.CreateLogger("Korat.Audit");
    }

    /// <inheritdoc/>
    public async Task<long?> RecordAsync(AuditEvent auditEvent, bool required, CancellationToken ct = default)
    {
        try
        {
            var record = BuildRecord(auditEvent);
            var seq = await InsertChainedAsync(record, ct);
            MirrorToLog(record);
            return seq;
        }
        catch (Exception ex)
        {
            // NEVER include DetailsJson in the failure report — treat it as untrusted.
            _logger.LogCritical(ex,
                "AUDIT WRITE FAILED (required={Required}) action={Action} target={TargetType}/{TargetId} actor={ActorType}/{ActorId}.",
                required, auditEvent.Action, auditEvent.TargetType, auditEvent.TargetId,
                auditEvent.ActorType, auditEvent.ActorId);
            // ALARM: GlitchTip event on every miss (both classes), so a silent audit outage
            // is impossible. Scrub defensively even though the message is constructed.
            SentrySdk.CaptureMessage(
                SentryScrub.ScrubText($"audit.write_failed action={auditEvent.Action} required={required}"),
                SentryLevel.Error);

            if (required)
                throw new AuditWriteException(
                    $"Audit write failed for action '{auditEvent.Action}' — the operation is treated as failed (fail-closed).", ex);
            return null;
        }
    }

    // ── Internals ──────────────────────────────────────────────────────────────

    private AuditEventRecord BuildRecord(AuditEvent e)
    {
        var ctx = _httpContextAccessor.HttpContext;

        // Defence-in-depth: deny-scrub DetailsJson with the same redaction the Sentry egress
        // uses (tokens, DSNs, connection-string secrets, emails), then truncate.
        var details = e.DetailsJson is null ? null : SentryScrub.ScrubText(e.DetailsJson);
        if (details is { Length: > MaxDetailsLength })
            details = details[..MaxDetailsLength];

        // Actor enrichment: service-layer call sites (secret set/clear, DEK create, …) record
        // with the default "system" actor; when the operation runs inside an authenticated HTTP
        // request, resolve the real user from HttpContext.Items (set by RequireSpaceOwner).
        var actorType = e.ActorType;
        var actorId = e.ActorId;
        if (actorType == AuditActorTypes.System && actorId == "system"
            && ctx?.Items.TryGetValue(KoratHttpContextItems.UserIdKey, out var uid) == true
            && uid is Korat.Domain.Auth.UserId userId)
        {
            actorType = AuditActorTypes.User;
            actorId = userId.Value.ToString();
        }

        return new AuditEventRecord
        {
            // Truncate to whole microseconds so the canonical string round-trips exactly
            // through Postgres timestamptz (which stores at µs precision). Without this,
            // Linux DateTimeOffset.UtcNow has sub-µs ticks that Npgsql truncates on write;
            // AuditVerifier re-reads the truncated value, recanonicalizes, and the hash
            // mismatches — reporting false tampering on every production row.
            OccurredAtUtc = TruncateToMicroseconds(DateTimeOffset.UtcNow),
            ActorType = actorType,
            ActorId = actorId,
            AuthKind = e.AuthKind ?? ResolveAuthKind(ctx, actorType),
            SpaceId = e.SpaceId,
            Action = e.Action,
            TargetType = e.TargetType,
            TargetId = Truncate(e.TargetId, 256),
            Outcome = e.Outcome,
            DetailsJson = details,
            TraceId = ctx?.TraceIdentifier,
            SourceIp = ctx is null ? null : RateLimiterRegistration.ResolveClientIp(ctx, _trustForwardedIp),
        };
    }

    private async Task<long> InsertChainedAsync(AuditEventRecord record, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        if (db.Database.IsInMemory())
        {
            // Test substrate: no raw SQL / transactions. Safe because the integration-test
            // assembly disables parallelization (see KoratTestHost remarks).
            var head = await db.AuditChainHead.SingleOrDefaultAsync(h => h.Id == 1, ct)
                       ?? CreateGenesisHead(db);
            Chain(record, head);
            db.AuditEvents.Add(record);
            await db.SaveChangesAsync(ct);
            return record.Seq;
        }

        // Relational path: a single short transaction with the head row locked FOR UPDATE.
        // The row lock serializes Seq assignment across silos; volume is low (privileged ops),
        // so contention is not a concern (impl-plan §1: escape hatch = per-day chains, NOT built).
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var lockedHead = (await db.AuditChainHead
                .FromSqlRaw("""SELECT * FROM "AuditChainHead" WHERE "Id" = 1 FOR UPDATE""")
                .ToListAsync(ct))
            .SingleOrDefault();
        if (lockedHead is null)
        {
            // Lazy genesis (e.g. a fresh DB where the migration seed was bypassed).
            // Two concurrent writers can both see null here before either INSERT commits;
            // the winner inserts the genesis head and the loser gets a PK unique-violation.
            // On that violation we discard the current context, open a fresh one, and re-read
            // the now-existing head so we can chain off it — one retry is sufficient because
            // the genesis head is a singleton and whoever won the race will have committed it.
            lockedHead = CreateGenesisHead(db);
            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException ex) when (IsUniqueViolation(ex))
            {
                // Lost the genesis race — the winner's head is now in the DB.
                // Roll back our failed attempt, dispose the tainted context, and start a fresh
                // transaction that will find and lock the already-inserted head row.
                await tx.RollbackAsync(ct);
                await tx.DisposeAsync();
                await db.DisposeAsync();

                await using var db2 = await _dbFactory.CreateDbContextAsync(ct);
                await using var tx2 = await db2.Database.BeginTransactionAsync(ct);
                lockedHead = (await db2.AuditChainHead
                        .FromSqlRaw("""SELECT * FROM "AuditChainHead" WHERE "Id" = 1 FOR UPDATE""")
                        .ToListAsync(ct))
                    .Single(); // must exist now — the winner committed it
                Chain(record, lockedHead);
                db2.AuditEvents.Add(record);
                await db2.SaveChangesAsync(ct);
                await tx2.CommitAsync(ct);
                return record.Seq;
            }
        }

        Chain(record, lockedHead);
        db.AuditEvents.Add(record);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return record.Seq;
    }

    /// <summary>
    /// Returns true when the <see cref="DbUpdateException"/> was caused by a unique / PK
    /// constraint violation — the signal that we lost the lazy-genesis race (cloud-m8).
    /// Postgres error code 23505; EF Core InMemory throws InvalidOperationException with
    /// the same outer DbUpdateException wrapper during duplicate-key inserts, but the InMemory
    /// path does not use this helper (it is single-threaded in tests).
    /// Internal (not private) so unit tests can verify the classifier directly.
    /// </summary>
    internal static bool IsUniqueViolation(DbUpdateException ex) =>
        ex.InnerException?.Message.Contains("23505", StringComparison.Ordinal) == true  // Postgres SqlState
        || ex.InnerException?.Message.Contains("UNIQUE", StringComparison.OrdinalIgnoreCase) == true
        || ex.InnerException?.Message.Contains("unique", StringComparison.OrdinalIgnoreCase) == true
        || ex.InnerException?.Message.Contains("duplicate key", StringComparison.OrdinalIgnoreCase) == true
        || ex.InnerException?.Message.Contains("PRIMARY KEY", StringComparison.OrdinalIgnoreCase) == true;

    private static AuditChainHeadRecord CreateGenesisHead(KoratDbContext db)
    {
        var head = new AuditChainHeadRecord { Id = 1, LastSeq = 0, LastHash = AuditHasher.GenesisHash };
        db.AuditChainHead.Add(head);
        return head;
    }

    private static void Chain(AuditEventRecord record, AuditChainHeadRecord head)
    {
        record.Seq = head.LastSeq + 1;
        record.PrevHash = head.LastHash;
        record.RowHash = AuditHasher.ComputeRowHash(AuditCanonical.Canonicalize(record), record.PrevHash);
        head.LastSeq = record.Seq;
        head.LastHash = record.RowHash;
    }

    private void MirrorToLog(AuditEventRecord r) =>
        // Structured, secret-free mirror to the Fly log stream (category "Korat.Audit").
        _auditMirror.LogInformation(
            "audit seq={Seq} action={Action} outcome={Outcome} actor={ActorType}:{ActorId} auth={AuthKind} space={SpaceId} target={TargetType}:{TargetId} trace={TraceId} hash={RowHashPrefix}",
            r.Seq, r.Action, r.Outcome, r.ActorType, r.ActorId, r.AuthKind, r.SpaceId,
            r.TargetType, r.TargetId, r.TraceId, Convert.ToHexString(r.RowHash.AsSpan(0, 8)));

    /// <summary>
    /// Best-effort AuthKind derivation when the call site did not specify one:
    /// CLI bearer header → cli_bearer; inference key header → inference_key;
    /// session cookie → cookie; no HTTP context → internal.
    /// </summary>
    private static string ResolveAuthKind(HttpContext? ctx, string actorType)
    {
        if (ctx is null || actorType == AuditActorTypes.System)
            return AuditAuthKinds.Internal;
        var authz = ctx.Request.Headers.Authorization.FirstOrDefault();
        if (authz is not null)
        {
            if (authz.Contains("korat_inf_", StringComparison.Ordinal))
                return AuditAuthKinds.InferenceKey;
            return AuditAuthKinds.CliBearer;
        }
        return ctx.Request.Cookies.ContainsKey(CanonicalSigninHandler.SessionCookieName)
            ? AuditAuthKinds.Cookie
            : AuditAuthKinds.Internal;
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max];

    /// <summary>
    /// Truncates a <see cref="DateTimeOffset"/> to whole-microsecond precision.
    /// Postgres <c>timestamptz</c> stores at µs precision (ticks divisible by 10);
    /// CLR <see cref="DateTimeOffset.UtcNow"/> on Linux can have sub-µs ticks, so
    /// Npgsql truncates them on write. We truncate before hashing so the canonical
    /// string built pre-insert matches the one rebuilt from the re-read row — making
    /// chain verification stable on real Postgres.
    /// </summary>
    internal static DateTimeOffset TruncateToMicroseconds(DateTimeOffset value)
    {
        var ticks = value.UtcTicks;
        return new DateTimeOffset(ticks - ticks % 10, TimeSpan.Zero);
    }
}

/// <summary>Thrown when a REQUIRED audit write fails — the surrounding operation must fail (fail-closed).</summary>
public sealed class AuditWriteException(string message, Exception inner) : InvalidOperationException(message, inner);
