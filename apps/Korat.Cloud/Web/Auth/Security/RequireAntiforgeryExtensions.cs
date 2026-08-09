using Korat.Cloud.Web.Auth;
using Microsoft.AspNetCore.Antiforgery;

namespace Korat.Cloud.Web.Auth.Security;

public static class RequireAntiforgeryExtensions
{
    /// <summary>
    /// Adds an endpoint filter that calls <see cref="IAntiforgery.ValidateRequestAsync"/>
    /// before the endpoint runs. Returns 400 BadRequest with body <c>{ error: "antiforgery-failure" }</c>
    /// on failure.
    ///
    /// Use this on JSON minimal-API POSTs that are not form-bound: <c>UseAntiforgery()</c>
    /// middleware only auto-validates form-encoded endpoints. SPA JSON POSTs rely on
    /// <c>SameSite=Lax</c> alone without this filter.
    ///
    /// Mirrors the explicit <c>ValidateRequestAsync</c> call previously in
    /// <c>MagicLinkEndpoints</c> — single source of truth for the pattern.
    /// </summary>
    public static TBuilder RequireAntiforgeryValidation<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder
    {
        builder.Add(endpointBuilder =>
        {
            endpointBuilder.FilterFactories.Add((_, next) => async ctx =>
                await ValidateOrFailAsync(ctx, next));
        });
        return builder;
    }

    /// <summary>
    /// Like <see cref="RequireAntiforgeryValidation{TBuilder}"/> but skips antiforgery
    /// entirely when the session cookie (<c>__Host-korat_session</c>) is absent.
    ///
    /// <para>
    /// A request without the session cookie cannot be authenticated via cookie
    /// (<c>PolymorphicAuthResolver</c>: cookie wins first, bearer second), so it is either:
    /// <list type="bullet">
    ///   <item>A non-ambient bearer caller (CLI). The <c>Authorization</c> header is never
    ///     auto-attached cross-site by browsers, so there is no CSRF surface.</item>
    ///   <item>Anonymous. Rejected later by the endpoint's admin gate.</item>
    /// </list>
    /// Either way there is no CSRF surface to protect. When the cookie IS present the request
    /// authenticates via cookie, so full antiforgery validation runs — identical to
    /// <see cref="RequireAntiforgeryValidation{TBuilder}"/>.
    /// </para>
    ///
    /// <para>
    /// Safe because the session cookie is <c>SameSite=Lax</c>: a cookie-bearing request can
    /// never be a cross-site POST, so an attacker cannot simultaneously send the cookie (to
    /// authenticate as the victim) and omit it (to trigger the antiforgery skip).
    /// </para>
    /// </summary>
    public static TBuilder RequireAntiforgeryUnlessHeadless<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder
    {
        builder.Add(endpointBuilder =>
        {
            endpointBuilder.FilterFactories.Add((_, next) => async ctx =>
            {
                // No session cookie ⇒ the request cannot be cookie-authenticated
                // (PolymorphicAuthResolver: cookie wins first, bearer second). It is either
                // a non-ambient bearer caller (CLI; Authorization is never auto-attached
                // cross-site ⇒ not forgeable) or anonymous (rejected by the endpoint's
                // IsAdmin gate). Either way there is no CSRF surface, so skip validation.
                // When the cookie IS present, fall through to full antiforgery enforcement —
                // identical to RequireAntiforgeryValidation. Safe because the session cookie
                // is SameSite=Lax: a cookie-bearing request can never be a cross-site POST.
                if (!ctx.HttpContext.Request.Cookies.ContainsKey(CanonicalSigninHandler.SessionCookieName))
                    return await next(ctx);
                return await ValidateOrFailAsync(ctx, next);
            });
        });
        return builder;
    }

    /// <summary>
    /// Shared inner logic: validates the antiforgery token pair and returns 400 on failure.
    /// Called by both <see cref="RequireAntiforgeryValidation{TBuilder}"/> and
    /// <see cref="RequireAntiforgeryUnlessHeadless{TBuilder}"/>.
    /// </summary>
    private static async ValueTask<object?> ValidateOrFailAsync(
        EndpointFilterInvocationContext ctx,
        EndpointFilterDelegate next)
    {
        var antiforgery = ctx.HttpContext.RequestServices.GetRequiredService<IAntiforgery>();
        try
        {
            await antiforgery.ValidateRequestAsync(ctx.HttpContext);
        }
        catch (AntiforgeryValidationException ex)
        {
            // Log the concrete reason (token decryption vs. user-mismatch vs. missing) —
            // the client only ever sees the opaque "antiforgery-failure", which made the
            // anonymous-token-after-login bug hard to diagnose.
            var logger = ctx.HttpContext.RequestServices
                .GetRequiredService<ILoggerFactory>().CreateLogger("Antiforgery");
            var hasHeader = ctx.HttpContext.Request.Headers.ContainsKey("X-XSRF-TOKEN");
            var hasCookie = ctx.HttpContext.Request.Cookies.ContainsKey("__Secure-korat_xsrf");
            logger.LogWarning(
                "Antiforgery validation failed path={Path} authenticated={Auth} hasHeader={HasHeader} hasCookie={HasCookie} reason={Reason}",
                ctx.HttpContext.Request.Path,
                ctx.HttpContext.User.Identity?.IsAuthenticated == true,
                hasHeader, hasCookie, ex.Message);
            return Results.BadRequest(new { error = "antiforgery-failure" });
        }
        return await next(ctx);
    }
}
