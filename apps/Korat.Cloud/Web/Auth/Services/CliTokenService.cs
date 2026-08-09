using System.Text.Json;
using Korat.Cloud.Security.Audit;
using Korat.Domain.Auth;
using Korat.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

// Bring UserStatus values into scope for the ValidateAsync join predicate.
using UserStatus = Korat.Domain.Auth.UserStatus;

namespace Korat.Cloud.Web.Auth.Services;

public sealed class CliTokenService(
    KoratDbContext db,
    ILogger<CliTokenService> logger,
    TimeProvider time,
    IAuditLog? auditLog = null) : ICliTokenService
{
    /// <summary>Raw-token prefix. Public since inc-2a: SpaceMcpAuth discriminates the inc-1
    /// CLI-token path from the OAuth-token path on this prefix (an OpenIddict reference token
    /// can never carry it).</summary>
    public const string TokenPrefix = "korat_cli_";
    private const string Prefix = TokenPrefix;
    private static readonly string[] ValidScopes = ["full", "bridge-only"];

    /// <summary>
    /// Space-MCP (increment 1, Task 1, correction S5): the <c>space-mcp</c> scope is
    /// Space-pinned at issuance — the scope string is literally <c>"space-mcp:{spaceId}"</c>
    /// (no schema migration needed for a separate SpaceId column).
    /// <c>Korat.Cloud.Web.Mcp.Space.SpaceMcpAuth</c> enforces path-Space == token-Space by
    /// comparing the path's resolved SpaceId against the suffix here.
    /// </summary>
    internal const string SpaceMcpScopePrefix = "space-mcp:";

    /// <summary>
    /// True for the two fixed scopes ("full"/"bridge-only") and for any well-formed
    /// Space-pinned <c>space-mcp:{spaceId}</c> scope (non-empty suffix).
    /// </summary>
    private static bool IsValidScope(string scope) =>
        Array.IndexOf(ValidScopes, scope) >= 0 ||
        (scope.StartsWith(SpaceMcpScopePrefix, StringComparison.Ordinal) && scope.Length > SpaceMcpScopePrefix.Length);

    /// <summary>
    /// True when <paramref name="scope"/> is subject to the 365-day absolute lifetime cap
    /// (see <see cref="AbsoluteCap"/>). "full" always was; Space-MCP correction S5 adds
    /// every <c>space-mcp:*</c> token — a Space-pinned owner bearer must not be exempted
    /// from the cap the way machine-issued "bridge-only" relay credentials are.
    /// </summary>
    private static bool IsCappedScope(string scope) =>
        scope == "full" || scope.StartsWith(SpaceMcpScopePrefix, StringComparison.Ordinal);

    /// <summary>
    /// Sliding window for CLI token expiry. Each use within the window extends ExpiresAt by
    /// this duration from now (see ValidateAsync).
    /// </summary>
    private static readonly TimeSpan SlidingWindow = TimeSpan.FromDays(90);
    private static readonly TimeSpan RollingRenewal = TimeSpan.FromDays(1);

    /// <summary>
    /// Absolute lifetime cap for "full"-scope (and, since S5, "space-mcp:*"-scope) CLI
    /// tokens, measured from <c>IssuedAt</c>.
    /// A token that keeps being used cannot remain valid beyond this ceiling — the user
    /// must re-run <c>korat login</c> once per year.  Chosen generously (365 days) to
    /// avoid surprising active users while still bounding credential lifetime.
    ///
    /// The cap is enforced at validation time by comparing <c>now</c> against
    /// <c>IssuedAt + AbsoluteCap</c>; no schema migration is required because
    /// <c>IssuedAt</c> is already stored.  Existing tokens are not retroactively
    /// invalidated immediately — they continue working until they naturally reach
    /// 365 days from their original <c>IssuedAt</c>.
    ///
    /// "bridge-only" tokens are explicitly excluded from the absolute cap: they are
    /// machine-issued relay credentials whose key-rotation cadence is controlled via
    /// explicit server revocation, not calendar age.
    ///
    /// Same absolute-cap intent as <see cref="SessionService.AbsoluteCap"/>, but enforced
    /// from the stored <c>IssuedAt</c> at validation time rather than a precomputed
    /// <c>AbsoluteExpiresAt</c> column — so a sliding-window renewal can never desync the cap.
    /// </summary>
    internal static readonly TimeSpan AbsoluteCap = TimeSpan.FromDays(365);

    public async Task<CliTokenIssueResult> IssueAsync(Guid userId, string scope, CancellationToken ct)
    {
        if (!IsValidScope(scope))
            throw new ArgumentException($"Invalid scope '{scope}'.", nameof(scope));

        var raw = Prefix + GenerateToken();
        var now = time.GetUtcNow();
        var expiresAt = now.Add(SlidingWindow);
        var tokenId = Guid.NewGuid();
        db.CliTokens.Add(new CliToken
        {
            Id = tokenId,
            UserId = new UserId(userId),
            TokenHash = Hash(raw),
            Scope = scope,
            IssuedAt = now,
            LastUsedAt = now,
            ExpiresAt = expiresAt,
        });
        await db.SaveChangesAsync(ct);
        logger.LogInformation("CLI token issued for user {UserId}, scope {Scope}", userId, scope);
        // 032 C1: credential issuance — audited fail-closed. Records the token ROW id only,
        // never the raw token or its hash.
        if (auditLog is not null)
        {
            await auditLog.RecordAsync(new AuditEvent(
                Action: AuditActions.CliTokenIssue,
                TargetType: "cli_token",
                TargetId: tokenId.ToString(),
                ActorType: AuditActorTypes.User,
                ActorId: userId.ToString(),
                DetailsJson: AuditDetails.Json(new { scope })),
                required: true, ct);
        }
        return new CliTokenIssueResult(raw, expiresAt);
    }

    public async Task<Guid?> ValidateAsync(string rawToken, CancellationToken ct)
    {
        // MAJOR-6 fix: delegate to ValidateWithScopeAsync to eliminate duplicated SQL.
        // Behaviour is identical: same WHERE clause, same sliding window, same JOIN on User.
        var result = await ValidateWithScopeAsync(rawToken, ct);
        return result?.UserId;
    }

    /// <summary>
    /// Validates <paramref name="rawToken"/> and returns both the <c>UserId</c> and the
    /// token's <c>Scope</c>. Returns <c>null</c> when the token is invalid, expired, or
    /// revoked (same semantics as <see cref="ValidateAsync"/>).
    ///
    /// Used by <see cref="Korat.Cloud.Web.Auth.PolymorphicAuthResolver"/> so that
    /// privilege-checking filters can reject bridge-only tokens on admin/developer surfaces.
    /// The gRPC relay path uses the scope-less <see cref="ValidateAsync"/> because bridge
    /// tokens are valid credentials for relay — the scope check happens at the endpoint layer.
    /// </summary>
    public async Task<(Guid UserId, string Scope)?> ValidateWithScopeAsync(string rawToken, CancellationToken ct)
    {
        // Наш пропуск ВСЕГДА начинается с этого префикса — его добавляет IssueAsync, другого
        // пути выдачи нет. Без этой проверки каждый запрос с токеном провайдера оплачивал бы
        // SHA-256 и два обращения к Postgres впустую: обновление отметки использования и
        // выборку. Ровно это и происходило, пока комментарий в резолвере утверждал, что
        // пропуска различаются формой «без обращения к базе».
        if (!rawToken.StartsWith(Prefix, StringComparison.Ordinal)) return null;

        if (string.IsNullOrWhiteSpace(rawToken)) return null;
        var hash = Hash(rawToken);
        var now = time.GetUtcNow();
        var newExpiresAt = now.Add(SlidingWindow);

        if (db.Database.IsInMemory())
        {
            // ─────────────────────────────────────────────────────────────────
            // InMemory race-safety disclaimer (mirrors ValidateAsync).
            // ─────────────────────────────────────────────────────────────────
            var token = await db.CliTokens
                .Join(db.Users,
                    t => t.UserId,
                    u => u.Id,
                    (t, u) => new { Token = t, UserStatus = u.Status })
                .FirstOrDefaultAsync(
                    x => x.Token.TokenHash == hash
                      && x.Token.RevokedAt == null
                      && x.Token.ExpiresAt > now
                      && x.UserStatus == UserStatus.Active
                      // Absolute cap: full-scope AND space-mcp-scoped tokens expire 365 days
                      // from IssuedAt regardless of sliding-window activity (S5).
                      && (!IsCappedScope(x.Token.Scope) || x.Token.IssuedAt.Add(AbsoluteCap) > now), ct);
            if (token is null) return null;

            if (now - token.Token.LastUsedAt > RollingRenewal)
            {
                db.Entry(token.Token).CurrentValues.SetValues(token.Token with { LastUsedAt = now, ExpiresAt = newExpiresAt });
                await db.SaveChangesAsync(ct);
            }
            return (token.Token.UserId.Value, token.Token.Scope);
        }

        // Absolute cap deadline for capped-scope tokens: IssuedAt + 365 days.
        // Computed here so it can be referenced in both SQL branches below.
        // bridge-only tokens are exempt (machine relay credentials; lifetime managed via revocation).
        var absoluteDeadline = now - AbsoluteCap; // tokens issued before this instant are past cap

        // Single atomic SQL: validates + bumps + returns (UserId, Scope) in one round trip.
        // S5: space-mcp-scoped tokens ("space-mcp:{spaceId}", via LIKE 'space-mcp:%') are
        // capped exactly like "full" — only "bridge-only" is exempt.
        var rows = await db.Database.SqlQuery<CliTokenValidateScopeRow>($@"
            UPDATE ""CliToken"" t
               SET ""LastUsedAt"" = {now},
                   ""ExpiresAt""  = {newExpiresAt}
              FROM ""User"" u
             WHERE t.""TokenHash""  = {hash}
               AND t.""RevokedAt""  IS NULL
               AND t.""ExpiresAt""  > {now}
               AND t.""LastUsedAt"" < {now - RollingRenewal}
               AND t.""UserId""     = u.""Id""
               AND u.""Status""     = 'Active'
               AND ((t.""Scope"" <> 'full' AND t.""Scope"" NOT LIKE 'space-mcp:%') OR t.""IssuedAt"" > {absoluteDeadline})
            RETURNING t.""UserId"", t.""Scope""
        ").ToListAsync(ct);

        if (rows.Count > 0)
            return (rows[0].UserId, rows[0].Scope);

        // Token valid but within rolling renewal window — no write needed, just verify.
        var exists = await db.Database.SqlQuery<CliTokenValidateScopeRow>($@"
            SELECT t.""UserId"", t.""Scope""
              FROM ""CliToken"" t
              JOIN ""User"" u ON u.""Id"" = t.""UserId""
             WHERE t.""TokenHash"" = {hash}
               AND t.""RevokedAt"" IS NULL
               AND t.""ExpiresAt"" > {now}
               AND u.""Status""    = 'Active'
               AND ((t.""Scope"" <> 'full' AND t.""Scope"" NOT LIKE 'space-mcp:%') OR t.""IssuedAt"" > {absoluteDeadline})
        ").ToListAsync(ct);

        return exists.Count > 0 ? (exists[0].UserId, exists[0].Scope) : null;
    }

    public async Task<Guid?> GetTokenIdAsync(string rawToken, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(rawToken)) return null;
        var hash = Hash(rawToken);
        // Plain LINQ (no raw SQL / bump) — works identically on InMemory and Postgres, and
        // deliberately does not re-validate expiry/scope: the caller (SpaceMcpAuth) has
        // already called ValidateWithScopeAsync earlier in the same request.
        return await db.CliTokens
            .Where(t => t.TokenHash == hash && t.RevokedAt == null)
            .Select(t => (Guid?)t.Id)
            .FirstOrDefaultAsync(ct);
    }

    public async Task<bool> RevokeAsync(string rawToken, CancellationToken ct)
    {
        var hash = Hash(rawToken);
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
            var token = await db.CliTokens.FirstOrDefaultAsync(
                t => t.TokenHash == hash && t.RevokedAt == null, ct);
            if (token is null) return false;
            db.Entry(token).CurrentValues.SetValues(token with { RevokedAt = now });
            await db.SaveChangesAsync(ct);
            logger.LogInformation("CLI token {TokenId} for user {UserId} revoked", token.Id, token.UserId.Value);
            await AuditRevokeAsync(token.Id, token.UserId.Value, ct);
            return true;
        }

        var rows = await db.Database.SqlQuery<CliTokenRevokeRow>($@"
            UPDATE ""CliToken""
               SET ""RevokedAt"" = {now}
             WHERE ""TokenHash"" = {hash}
               AND ""RevokedAt"" IS NULL
            RETURNING ""Id"", ""UserId""
        ").ToListAsync(ct);

        if (rows.Count == 0) return false;
        logger.LogInformation("CLI token {TokenId} for user {UserId} revoked", rows[0].Id, rows[0].UserId);
        await AuditRevokeAsync(rows[0].Id, rows[0].UserId, ct);
        return true;
    }

    /// <summary>032 C1: shared `cli_token.revoke` audit (fail-closed). Token ROW id only.</summary>
    private async Task AuditRevokeAsync(Guid tokenId, Guid ownerUserId, CancellationToken ct)
    {
        if (auditLog is null) return;
        await auditLog.RecordAsync(new AuditEvent(
            Action: AuditActions.CliTokenRevoke,
            TargetType: "cli_token",
            TargetId: tokenId.ToString(),
            ActorType: AuditActorTypes.User,
            ActorId: ownerUserId.ToString()),
            required: true, ct);
    }

    public async Task<int> RevokeAllForUserAsync(Guid userId, CancellationToken ct)
    {
        var uid = new UserId(userId);
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
            var live = await db.CliTokens.Where(t => t.UserId == uid && t.RevokedAt == null).ToListAsync(ct);
            foreach (var t in live)
                db.Entry(t).CurrentValues.SetValues(t with { RevokedAt = now });
            if (live.Count > 0) await db.SaveChangesAsync(ct);
            if (live.Count > 0)
            {
                logger.LogInformation("Revoked {Count} CLI token(s) for user {UserId}", live.Count, userId);
                await AuditRevokeAllAsync(userId, live.Count, ct);
            }
            return live.Count;
        }

        var count = await db.Database.ExecuteSqlInterpolatedAsync($@"
            UPDATE ""CliToken""
               SET ""RevokedAt"" = {now}
             WHERE ""UserId""    = {userId}
               AND ""RevokedAt"" IS NULL", ct);

        if (count > 0)
        {
            logger.LogInformation("Revoked {Count} CLI token(s) for user {UserId}", count, userId);
            await AuditRevokeAllAsync(userId, count, ct);
        }
        return count;
    }

    /// <summary>032 C1: `cli_token.revoke_all` audit (fail-closed).</summary>
    private async Task AuditRevokeAllAsync(Guid userId, int count, CancellationToken ct)
    {
        if (auditLog is null) return;
        await auditLog.RecordAsync(new AuditEvent(
            Action: AuditActions.CliTokenRevokeAll,
            TargetType: "user",
            TargetId: userId.ToString(),
            ActorType: AuditActorTypes.User,
            ActorId: userId.ToString(),
            DetailsJson: AuditDetails.Json(new { revokedCount = count })),
            required: true, ct);
    }

    public async Task<IReadOnlyList<CliTokenListItem>> ListForUserAsync(Guid userId, CancellationToken ct)
    {
        var uid = new UserId(userId);
        // Return live (non-revoked) tokens only, newest first.
        return await db.CliTokens
            .Where(t => t.UserId == uid && t.RevokedAt == null)
            .OrderByDescending(t => t.IssuedAt)
            .Select(t => new CliTokenListItem(t.Id, t.Scope, t.IssuedAt, t.LastUsedAt, t.ExpiresAt))
            .ToListAsync(ct);
    }

    public async Task<bool> RevokeByIdForUserAsync(Guid userId, Guid tokenId, CancellationToken ct)
    {
        var uid = new UserId(userId);
        var now = time.GetUtcNow();

        if (db.Database.IsInMemory())
        {
            // ─────────────────────────────────────────────────────────────────
            // InMemory race-safety disclaimer
            // EF Core InMemory does not support raw SQL and cannot serialise
            // concurrent UPDATE statements. This LINQ fallback exists for unit-
            // test ergonomics ONLY. Production uses the Postgres branch below,
            // which is validated by the integration test in the CLI token tests.
            // The LINQ filter MUST mirror the SQL WHERE clause one-for-one.
            // ─────────────────────────────────────────────────────────────────
            var token = await db.CliTokens.FirstOrDefaultAsync(
                t => t.Id == tokenId && t.UserId == uid && t.RevokedAt == null, ct);
            if (token is null) return false;
            db.Entry(token).CurrentValues.SetValues(token with { RevokedAt = now });
            await db.SaveChangesAsync(ct);
            logger.LogInformation("CLI token {TokenId} for user {UserId} revoked by id", tokenId, userId);
            await AuditRevokeAsync(tokenId, userId, ct);
            return true;
        }

        // Atomic UPDATE: verifies ownership (UserId == uid) and liveness (RevokedAt IS NULL)
        // in a single round trip to prevent IDOR — user A cannot revoke user B's token
        // because the WHERE clause scopes to the caller's own UserId.
        var rows = await db.Database.SqlQuery<CliTokenRevokeRow>($@"
            UPDATE ""CliToken""
               SET ""RevokedAt"" = {now}
             WHERE ""Id""        = {tokenId}
               AND ""UserId""    = {userId}
               AND ""RevokedAt"" IS NULL
            RETURNING ""Id"", ""UserId""
        ").ToListAsync(ct);

        if (rows.Count == 0) return false;
        logger.LogInformation("CLI token {TokenId} for user {UserId} revoked by id", tokenId, userId);
        await AuditRevokeAsync(tokenId, userId, ct);
        return true;
    }

    private static string GenerateToken() => AuthTokens.GenerateRawBase64Url();

    private static string Hash(string raw) => AuthTokens.Sha256Hex(raw);

    private sealed record CliTokenValidateRow(Guid UserId);
    private sealed record CliTokenValidateScopeRow(Guid UserId, string Scope);
    private sealed record CliTokenRevokeRow(Guid Id, Guid UserId);
}
