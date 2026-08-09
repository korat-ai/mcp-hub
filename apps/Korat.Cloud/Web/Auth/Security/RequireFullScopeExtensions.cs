using Korat.Cloud.Web.Auth;

namespace Korat.Cloud.Web.Auth.Security;

public static class RequireFullScopeExtensions
{
    /// <summary>
    /// Endpoint filter for the account / session / CLI-token-management surface that resolves
    /// the caller identity via <see cref="IAuthResolver"/>, rejects non-"full"-scope credentials,
    /// and stashes the resolved <c>UserId</c> in <c>HttpContext.Items</c> for the handler.
    ///
    /// <para>
    /// <b>Only "full"-scope credentials are accepted.</b>
    /// Bridge-only tokens (scope "bridge-only") are issued to relay agents and resolve to a real
    /// identity, but must not reach account-management actions (profile rename, primary-email
    /// change, web-session revocation, CLI-token list/revoke, invite list/revoke/redemptions).
    /// This mirrors the scope-floor enforced by <see cref="RequireSpaceOwner"/> on the
    /// owner-management surface and <see cref="AdminScopeGate"/> on the admin surface; the
    /// account/session surface previously resolved identity without any scope check, a latent
    /// privilege gap behind the (currently unbuilt) bridge-only issuance path.
    /// </para>
    ///
    /// <para>
    /// Returns 401 when no identity resolves, 403 when the identity's scope is not "full".
    /// Cookie/session principals always carry scope "full", so this does not affect the
    /// browser/console path. Handlers read the stashed id via
    /// <c>(UserId)ctx.Items[KoratHttpContextItems.UserIdKey]!</c>.
    /// </para>
    /// </summary>
    public static RouteHandlerBuilder RequireFullScope(this RouteHandlerBuilder builder) =>
        builder.AddEndpointFilter(async (ctx, next) =>
        {
            var resolver = ctx.HttpContext.RequestServices.GetRequiredService<IAuthResolver>();
            var identity = await resolver.ResolveAsync(ctx.HttpContext, ctx.HttpContext.RequestAborted);
            if (identity is null) return Results.Unauthorized();

            // Reject bridge-only tokens before any grain or DB call, matching RequireSpaceOwner.
            if (identity.Scope != "full")
                return Results.StatusCode(StatusCodes.Status403Forbidden);

            ctx.HttpContext.Items[KoratHttpContextItems.UserIdKey] = identity.UserId;
            return await next(ctx);
        });
}
