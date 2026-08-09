using Korat.Cloud.Web.Auth.Security;
using Korat.Domain.Auth;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Google;

namespace Korat.Cloud.Web.Auth.Endpoints;

public static class SigninInitiationEndpoints
{
    public static IEndpointRouteBuilder MapSigninInitiationEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/signin/{provider}", async (HttpContext ctx, string provider, IOAuthStateProtector state, IAuthResolver resolver, TimeProvider time, CancellationToken ct) =>
        {
            var returnUrl = ctx.Request.Query["returnUrl"].ToString();
            var isLink = ctx.Request.Query["link"].ToString() == "1";

            // "Connect provider" (027): only honoured for an already-authenticated caller.
            // Stamp their UserId into the signed state so /finish links instead of signing in.
            // No session → link=1 is ignored and this is a normal signin.
            Guid? linkUserId = null;
            if (isLink)
            {
                var identity = await resolver.ResolveAsync(ctx, ct);
                if (identity is not null) linkUserId = identity.UserId.Value;
            }

            var defaultReturn = linkUserId is not null ? "/app/account/profile" : "/app/";
            var safeReturn = IsSafeReturnUrl.Check(returnUrl) ? returnUrl : defaultReturn;
            var statePayload = state.Protect(new OAuthStatePayload(
                safeReturn,
                Guid.NewGuid(),
                time.GetUtcNow(),
                linkUserId));

            var scheme = provider.ToLowerInvariant() switch
            {
                "github" => GitHubOAuthExtensions.Scheme,
                "google" => GoogleDefaults.AuthenticationScheme,
                // Провайдер входа Korat встаёт сюда же, а не отдельным путём: связывание,
                // заведение учётки с пространством по умолчанию и экран подтверждения
                // привязки уже живут дальше по этому маршруту и одинаковы для всех.
                "korat" => KoratSsoDefaults.Scheme,
                _ => null,
            };
            if (scheme is null) return Results.NotFound();

            var props = new AuthenticationProperties
            {
                // IMPORTANT: this is the LOCAL post-auth landing path, NOT the OAuth redirect_uri.
                // It must NOT equal the OAuth handler's CallbackPath ("/signin/{provider}/callback"),
                // otherwise the OAuth RemoteAuthenticationHandler re-intercepts this follow-up request
                // (which carries korat_state but no OAuth `state`) and fails with
                // "The oauth state was missing or invalid." Use a distinct "/finish" path so the
                // finalize endpoint below is actually reached after a successful token exchange.
                RedirectUri = $"/signin/{provider}/finish?korat_state={Uri.EscapeDataString(statePayload)}",
            };
            return Results.Challenge(props, new[] { scheme });
        }).RequireRateLimiting(RateLimiterRegistration.SigninProviderPolicy);

        // Finalize endpoint — reached AFTER the OAuth handler (at CallbackPath
        // "/signin/{provider}/callback") completes the token exchange and signs in the
        // intermediate cookie. Distinct path so it is not re-intercepted by the OAuth handler.
        //
        // Sec-Fetch-Site CSRF defence: a real IdP redirect to /finish arrives cross-site
        // (Sec-Fetch-Site: cross-site). A same-origin or same-site request to this path is
        // suspicious — a forged in-page navigation cannot mimic a genuine top-level IdP
        // redirect. We reject those and accept absent headers for pre-Fetch-Metadata browsers.
        app.MapGet("/signin/{provider}/finish",
            async (HttpContext ctx, string provider, IOAuthStateProtector stateProtector, CanonicalSigninHandler canonical, CancellationToken ct) =>
        {
            // Reject requests where Sec-Fetch-Site is explicitly same-origin or same-site.
            // Cross-site (real IdP redirect) and absent (legacy browser) are both accepted.
            if (!SecFetchSiteValidator.IsLegitimateCallback(ctx))
                return Results.Redirect("/app/signin?error=signin_failed");

            var rawState = ctx.Request.Query["korat_state"].ToString();
            var state = stateProtector.TryUnprotect(rawState);
            if (state is null) return Results.Redirect("/app/signin?error=signin_failed");

            var principal = ctx.User;
            var providerEnum = provider.ToLowerInvariant() switch
            {
                "github" => LoginProvider.GitHub,
                "google" => LoginProvider.Google,
                "korat" => LoginProvider.KoratSso,
                _ => (LoginProvider?)null,
            };
            if (providerEnum is null) return Results.NotFound();

            var providerUserId = principal.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
            if (providerUserId is null) return Results.Redirect("/app/signin?error=signin_failed");

            // email_verified is case-insensitive: Google's claim is mapped from a JSON boolean,
            // which JsonElement.ToString() renders as "True" (capital), while GitHub sets "true".
            // A `== "true"` check silently failed every Google verification (signin AND link).
            var emailVerified = bool.TryParse(principal.FindFirst("email_verified")?.Value, out var ev) && ev;

            var signinReq = new CanonicalSigninRequest(
                Provider: providerEnum.Value,
                ProviderUserId: providerUserId,
                Email: principal.FindFirst(System.Security.Claims.ClaimTypes.Email)?.Value,
                EmailVerified: emailVerified,
                DisplayName: principal.FindFirst(System.Security.Claims.ClaimTypes.Name)?.Value,
                ReturnUrl: state.ReturnUrl);

            // "Connect provider" (027): link the proven identity to the already-authenticated
            // user instead of signing in. The live session is re-checked inside LinkAsync.
            if (state.LinkUserId is { } linkUserId)
                return await canonical.LinkAsync(ctx, signinReq, linkUserId, ct);

            return await canonical.CompleteAsync(ctx, signinReq, ct);
        }).RequireRateLimiting(RateLimiterRegistration.SigninProviderPolicy);

        return app;
    }
}
