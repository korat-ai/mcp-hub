using Korat.Cloud.Security.Audit;
using Korat.Cloud.Security.Envelope;
using Korat.Cloud.Web.Auth;
using Korat.Cloud.Web.Auth.Security;
using Korat.Domain;
using Korat.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Korat.Cloud.Web.Admin;

/// <summary>
/// 032 (#57 Leg 3 C2): admin-only operational endpoints. These close the "ShredAsync /
/// RewrapAllDeksAsync have no production caller" finding — KEK rotation (IR runbook §4.1
/// step 4) and per-space crypto-shred (IR §5) no longer require running ad-hoc code
/// against the production DB.
///
/// Auth: ADMIN-gated (AuthUser.IsAdmin), NOT just
/// space-owner: these operations are global / cross-tenant. Anonymous → 401, non-admin → 403.
/// Mutations additionally require antiforgery (headless CLI callers skip per the existing
/// cookie-absent rule) and carry the strict `admin-ops` rate-limit policy.
/// Every mutation is audited fail-closed (C1).
/// </summary>
public static class AdminOpsEndpoints
{
    public sealed record CryptoShredRequest(string? Confirm);

    public static void MapAdminOpsEndpoints(this WebApplication app)
    {
        // ── POST /api/admin/envelope/rewrap — KEK rotation step (IR §4.1.4) ──────
        app.MapPost("/api/admin/envelope/rewrap", async (
            HttpContext ctx,
            SpaceDekProvider dekProvider,
            IAuditLog auditLog,
            CancellationToken ct) =>
        {
            var processed = await dekProvider.RewrapAllDeksAsync(ct);
            await auditLog.RecordAsync(new AuditEvent(
                Action: AuditActions.KekRewrap,
                TargetType: "envelope",
                TargetId: "all-deks",
                DetailsJson: AuditDetails.Json(new { processed })),
                required: true, ct);
            return Results.Ok(new { processed });
        }).RequireAdmin()
          .RequireAntiforgeryUnlessHeadless()
          .RequireRateLimiting(RateLimiterRegistration.AdminOpsPolicy);

        // ── POST /api/admin/spaces/{spaceId}/crypto-shred — destroy a space's DEKs (IR §5) ──
        app.MapPost("/api/admin/spaces/{spaceId}/crypto-shred", async (
            string spaceId,
            CryptoShredRequest? body,
            HttpContext ctx,
            SpaceDekProvider dekProvider,
            IAuditLog auditLog,
            CancellationToken ct) =>
        {
            // Irreversible: require the spaceId echoed back as explicit confirmation.
            if (!string.Equals(body?.Confirm, spaceId, StringComparison.Ordinal))
                return Results.Json(
                    new { error = "Confirmation mismatch: body.confirm must equal the spaceId." },
                    statusCode: 400);

            var deleted = await dekProvider.ShredAsync(new SpaceId(spaceId), ct);
            await auditLog.RecordAsync(new AuditEvent(
                Action: AuditActions.DekShred,
                TargetType: "space",
                TargetId: spaceId,
                SpaceId: spaceId,
                DetailsJson: AuditDetails.Json(new { deletedDekRows = deleted })),
                required: true, ct);

            return Results.Ok(new
            {
                deletedDekRows = deleted,
                // v1 multi-silo caveat (plan §2 / impl-plan §6): only THIS machine's DEK cache
                // is evicted — restart all machines to flush other silos' ≤15-min caches.
                note = "Restart all machines to flush in-memory DEK caches on other silos."
            });
        }).RequireAdmin()
          .RequireAntiforgeryUnlessHeadless()
          .RequireRateLimiting(RateLimiterRegistration.AdminOpsPolicy);

        // ── GET /api/admin/audit/verify — recompute + check the hash chain ───────
        app.MapGet("/api/admin/audit/verify", async (
            long? fromSeq,
            AuditVerifier verifier,
            CancellationToken ct) =>
        {
            var result = await verifier.VerifyAsync(fromSeq, ct);
            return Results.Ok(new
            {
                ok = result.Ok,
                checkedCount = result.CheckedCount,
                firstBrokenSeq = result.FirstBrokenSeq,
                headMismatch = result.HeadMismatch,
                headSeq = result.HeadSeq,
                headHash = result.HeadHashHex
            });
        }).RequireAdmin()
          .RequireRateLimiting(RateLimiterRegistration.OwnerManagementPolicy);

        // ── GET /api/admin/audit/events — paged read-only query ──────────────────
        app.MapGet("/api/admin/audit/events", async (
            string? spaceId,
            string? action,
            long? afterSeq,
            int? limit,
            IDbContextFactory<KoratDbContext> dbFactory,
            CancellationToken ct) =>
        {
            var take = Math.Clamp(limit ?? 100, 1, 500);
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var query = db.AuditEvents.AsNoTracking().AsQueryable();
            if (!string.IsNullOrEmpty(spaceId)) query = query.Where(e => e.SpaceId == spaceId);
            if (!string.IsNullOrEmpty(action))  query = query.Where(e => e.Action == action);
            if (afterSeq is { } a)              query = query.Where(e => e.Seq > a);

            var events = await query.OrderBy(e => e.Seq).Take(take).ToListAsync(ct);
            return Results.Ok(events.Select(e => new
            {
                seq = e.Seq,
                occurredAtUtc = e.OccurredAtUtc,
                actorType = e.ActorType,
                actorId = e.ActorId,
                authKind = e.AuthKind,
                spaceId = e.SpaceId,
                action = e.Action,
                targetType = e.TargetType,
                targetId = e.TargetId,
                outcome = e.Outcome,
                detailsJson = e.DetailsJson,
                traceId = e.TraceId,
                sourceIp = e.SourceIp,
                prevHash = Convert.ToHexString(e.PrevHash),
                rowHash = Convert.ToHexString(e.RowHash)
            }));
        }).RequireAdmin()
          .RequireRateLimiting(RateLimiterRegistration.OwnerManagementPolicy);
    }

    /// <summary>
    /// Endpoint filter: resolve identity via <see cref="IAuthResolver"/> (cookie or CLI Bearer),
    /// then require <c>AuthUser.IsAdmin</c>. Stashes the
    /// UserId in HttpContext.Items so AuditLogger's actor enrichment records the real admin.
    ///
    /// SECURITY (MAJOR-2): also rejects bearer tokens whose Scope != "full".
    /// A bridge-only token (handed to relay agents) resolves to a real identity but must not
    /// reach destructive admin ops (KEK rewrap, crypto-shred) or audit query/verify endpoints,
    /// even when the owning user is an admin.  Cookie/session principals are always "full".
    ///
    /// Deferred-fix (maintainability): the filter body now lives in the shared
    /// <see cref="AdminScopeGate.RequireAdminScope{TBuilder}"/> (also used by the /api/developer
    /// group) — behavior unchanged.
    /// </summary>
    internal static RouteHandlerBuilder RequireAdmin(this RouteHandlerBuilder builder) =>
        builder.RequireAdminScope();
}
