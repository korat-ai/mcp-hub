using Korat.Domain.Auth;
using Korat.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Korat.Cloud.Web.Auth.Services;

/// <summary>
/// DELIBERATE EXCEPTION to the "all logic & state lives in Orleans grains" rule (see
/// specs/015-sessions-in-orleans). Auth sessions stay in this DB-direct scoped service — NOT a
/// grain — on purpose:
///  - validate+bump runs on EVERY authenticated request (the app's hottest gate). Today it's a
///    single atomic UPDATE…RETURNING (~1ms, race-safe). Routing it through a single-activation
///    UserSessionsGrain would add a per-request, possibly cross-silo, hop + per-user
///    serialization on that gate.
///  - A half-migration (management in a grain, bump in SQL) is worse than either extreme: the
///    grain's cached LastUsedAt/ExpiresAt would go stale on the very fields the list shows.
/// Decision (2026-06-01): keep DB-direct (Option B). Revisit only if session-validation
/// throughput becomes a bottleneck. Postgres remains the source of truth.
/// </summary>
public sealed class SessionService(
    KoratDbContext db,
    ILogger<SessionService> logger,
    TimeProvider time) : ISessionService
{
    public static readonly TimeSpan SlidingWindow = TimeSpan.FromDays(30);
    public static readonly TimeSpan AbsoluteCap = TimeSpan.FromDays(90);

    public async Task<LoginSession> CreateAsync(UserId userId, string? userAgent, string? ip, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var session = new LoginSession
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            CreatedAt = now,
            LastUsedAt = now,
            ExpiresAt = now + SlidingWindow,
            AbsoluteExpiresAt = now + AbsoluteCap,
            UserAgent = userAgent is { Length: > 512 } ua ? ua[..512] : userAgent,
            CreatedFromIp = ip,
            RevokedAt = null,
        };
        db.AuthSessions.Add(session);
        await db.SaveChangesAsync(ct);

        // Dedup by browser: revoke any OTHER active session for this user with the same
        // User-Agent, so re-logging-in from the same browser replaces it instead of piling up
        // (prevents the "6 active sessions after a day of re-logins" clutter).
        if (!string.IsNullOrEmpty(session.UserAgent))
        {
            await RevokeWhereAsync(
                db.AuthSessions.Where(s =>
                    s.UserId == userId
                    && s.UserAgent == session.UserAgent
                    && s.Id != session.Id
                    && s.RevokedAt == null),
                now, ct);
        }

        logger.LogInformation("Session created for user {UserId}", userId);
        return session;
    }

    public async Task<SessionBumpResult?> ValidateAndBumpAsync(Guid sessionId, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var slidingTarget = now + SlidingWindow;

        if (db.Database.IsInMemory())
        {
            // ─────────────────────────────────────────────────────────────────
            // InMemory race-safety disclaimer
            // EF Core InMemory does not support raw SQL and cannot serialise
            // concurrent UPDATE statements. This LINQ fallback exists for unit-
            // test ergonomics ONLY. Production uses the Postgres branch below,
            // which is validated by the integration test in Task 14.
            // The LINQ filter MUST mirror the SQL WHERE clause one-for-one.
            // ─────────────────────────────────────────────────────────────────
            var session = await db.AuthSessions.FirstOrDefaultAsync(s =>
                s.Id == sessionId
                && s.RevokedAt == null
                && s.ExpiresAt > now
                && s.AbsoluteExpiresAt > now, ct);
            if (session is null) return null;
            var newExpiresAt = slidingTarget < session.AbsoluteExpiresAt ? slidingTarget : session.AbsoluteExpiresAt;
            var updated = session with { LastUsedAt = now, ExpiresAt = newExpiresAt };
            db.Entry(session).CurrentValues.SetValues(updated);
            await db.SaveChangesAsync(ct);
            return new SessionBumpResult(session.UserId, newExpiresAt);
        }

        // Single atomic SQL: validates + bumps + returns identity in one round trip.
        // LEAST() enforces the absolute cap independent of activity.
        var rows = await db.Database.SqlQuery<SessionBumpRow>($@"
            UPDATE ""AuthSession""
               SET ""LastUsedAt"" = {now},
                   ""ExpiresAt""  = LEAST({slidingTarget}, ""AbsoluteExpiresAt"")
             WHERE ""Id""                  = {sessionId}
               AND ""RevokedAt""           IS NULL
               AND ""ExpiresAt""           > {now}
               AND ""AbsoluteExpiresAt""   > {now}
            RETURNING ""UserId"" AS ""UserIdValue"", ""ExpiresAt""
        ").ToListAsync(ct);

        if (rows.Count == 0) return null;
        return new SessionBumpResult(new UserId(rows[0].UserIdValue), rows[0].ExpiresAt);
    }

    public async Task RevokeAsync(Guid sessionId, CancellationToken ct)
    {
        var now = time.GetUtcNow();

        if (db.Database.IsInMemory())
        {
            // ─────────────────────────────────────────────────────────────────
            // InMemory race-safety disclaimer
            // EF Core InMemory does not support raw SQL and cannot serialise
            // concurrent UPDATE statements. This LINQ fallback exists for unit-
            // test ergonomics ONLY. Production uses the Postgres branch below,
            // which is validated by the integration test in Task 14.
            // The LINQ filter MUST mirror the SQL WHERE clause one-for-one.
            // ─────────────────────────────────────────────────────────────────
            var session = await db.AuthSessions.FirstOrDefaultAsync(s => s.Id == sessionId && s.RevokedAt == null, ct);
            if (session is null) return;
            var updated = session with { RevokedAt = now };
            db.Entry(session).CurrentValues.SetValues(updated);
            await db.SaveChangesAsync(ct);
        }
        else
        {
            await db.Database.ExecuteSqlInterpolatedAsync($@"
                UPDATE ""AuthSession""
                   SET ""RevokedAt"" = {now}
                 WHERE ""Id"" = {sessionId}
                   AND ""RevokedAt"" IS NULL", ct);
        }
        logger.LogInformation("Session {SessionId} revoked", sessionId);
    }

    public async Task RevokeOthersAsync(UserId userId, Guid exceptSessionId, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        await RevokeWhereAsync(
            db.AuthSessions.Where(s =>
                s.UserId == userId && s.Id != exceptSessionId && s.RevokedAt == null),
            now, ct);
        logger.LogInformation("Revoked other sessions for user {UserId} (kept {SessionId})", userId, exceptSessionId);
    }

    // Bulk-revoke the sessions matched by <paramref name="query"/>. Mirrors RevokeAsync's dual
    // path: a LINQ load + SaveChanges for the InMemory test provider (no raw SQL / ExecuteUpdate),
    // ExecuteUpdateAsync for Postgres.
    private async Task RevokeWhereAsync(IQueryable<LoginSession> query, DateTimeOffset now, CancellationToken ct)
    {
        if (db.Database.IsInMemory())
        {
            var stale = await query.ToListAsync(ct);
            foreach (var s in stale)
                db.Entry(s).CurrentValues.SetValues(s with { RevokedAt = now });
            if (stale.Count > 0) await db.SaveChangesAsync(ct);
        }
        else
        {
            await query.ExecuteUpdateAsync(setters => setters.SetProperty(s => s.RevokedAt, now), ct);
        }
    }

    public async Task<IReadOnlyList<LoginSession>> ListActiveAsync(UserId userId, CancellationToken ct) =>
        await db.AuthSessions.Where(s => s.UserId == userId && s.RevokedAt == null && s.ExpiresAt > time.GetUtcNow())
                              .OrderByDescending(s => s.LastUsedAt)
                              .ToListAsync(ct);

    private sealed record SessionBumpRow(Guid UserIdValue, DateTimeOffset ExpiresAt);
}
