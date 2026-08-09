using Korat.Cloud.Web.Auth;
using Korat.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Korat.Cloud.Web.Auth.Security;

/// <summary>
/// Deferred-fix (maintainability): the ONE shared admin-scope authorization gate, previously
/// duplicated inline in <c>AdminOpsEndpoints.RequireAdmin</c> and
/// <c>DeveloperEndpoints.MapDeveloperEndpoints</c>.
///
/// Semantics (must stay byte-identical to both former copies):
///   1. Resolve identity via <see cref="IAuthResolver"/> (session cookie or CLI Bearer);
///      unresolved → 401 (<see cref="Results.Unauthorized"/>).
///   2. SECURITY (MAJOR-2): reject bearer tokens whose <c>Scope != "full"</c> → plain 403.
///      A bridge-only token (handed to relay agents) resolves to a real identity but must not
///      reach destructive admin ops or the developer API, even when the owning user is an admin.
///      Cookie/session principals are always "full".
///   3. Require <c>AuthUser.IsAdmin</c>; missing user or non-admin → plain 403 status —
///      NOT <see cref="Results.Forbid"/>, which invokes the cookie scheme's ForbidAsync and
///      REDIRECTS to an access-denied page (wrong shape for a JSON API).
///   4. On success, stash the resolved <c>UserId</c> in
///      <c>HttpContext.Items[KoratHttpContextItems.UserIdKey]</c> so AuditLogger's actor
///      enrichment records the real admin instead of "system".
/// </summary>
public static class AdminScopeGate
{
    /// <summary>
    /// Adds the shared admin-scope endpoint filter. Generic so it applies to both a single
    /// route (<see cref="RouteHandlerBuilder"/>, /api/admin/*) and a whole group
    /// (<c>RouteGroupBuilder</c>, /api/developer).
    /// </summary>
    public static TBuilder RequireAdminScope<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder =>
        builder.AddEndpointFilter(async (ctx, next) =>
        {
            var http = ctx.HttpContext;
            var resolver = http.RequestServices.GetRequiredService<IAuthResolver>();
            var identity = await resolver.ResolveAsync(http, http.RequestAborted);
            if (identity is null)
                return Results.Unauthorized();

            // Reject bridge-only tokens — they are scoped to relay and must not reach admin ops.
            if (identity.Scope != "full")
                return Results.StatusCode(StatusCodes.Status403Forbidden);

            var db = http.RequestServices.GetRequiredService<KoratDbContext>();
            var user = await db.Users.AsNoTracking()
                .SingleOrDefaultAsync(u => u.Id == identity.UserId, http.RequestAborted);
            if (user is null || !user.IsAdmin)
                return Results.StatusCode(StatusCodes.Status403Forbidden);

            http.Items[KoratHttpContextItems.UserIdKey] = identity.UserId;
            return await next(ctx);
        });
}
