using Korat.Cloud.Web.Auth;

namespace Korat.Cloud.Web;

public static class RequireAuthExtensions
{
    /// <summary>
    /// Endpoint filter that resolves the caller identity via <see cref="IAuthResolver"/> and
    /// stashes the <see cref="UserId"/> in <c>HttpContext.Items</c> for downstream handlers.
    ///
    /// <para>
    /// <b>Only "full"-scope credentials are accepted.</b>
    /// Bridge-only tokens (scope "bridge-only") are issued to relay agents and must not reach
    /// the owner-management surface (space overview, sessions, access-request approve/deny,
    /// grant revoke, hard-delete MCP servers, inference key issuance, etc.).  A bridge-only
    /// token resolves to a real identity but carries restricted privileges — this check rejects
    /// it before any grain or DB call, matching the same scope-floor enforced by
    /// <see cref="Korat.Cloud.Web.Auth.Security.AdminScopeGate"/> for the admin surface.
    /// </para>
    /// </summary>
    public static RouteHandlerBuilder RequireSpaceOwner(this RouteHandlerBuilder builder) =>
        builder.AddEndpointFilter(async (ctx, next) =>
        {
            var resolver = ctx.HttpContext.RequestServices.GetRequiredService<IAuthResolver>();
            var identity = await resolver.ResolveAsync(ctx.HttpContext, ctx.HttpContext.RequestAborted);
            if (identity is null) return Results.Unauthorized();

            // SECURITY MAJOR (web-M1): reject bridge-only tokens — they are scoped to relay
            // and must not reach the owner-management surface even when the owning user is
            // otherwise valid.  Cookie/session principals always carry scope "full".
            if (identity.Scope != "full")
                return Results.StatusCode(StatusCodes.Status403Forbidden);

            // Stash the resolved UserId for downstream handlers (consumed in Sub-project 2).
            ctx.HttpContext.Items[KoratHttpContextItems.UserIdKey] = identity.UserId;
            return await next(ctx);
        });
}
