using System.Net;
using System.Security.Claims;
using Korat.Cloud.Security.Audit;
using Korat.Cloud.Web.Auth;
using Korat.Cloud.Web.Auth.Options;
using Korat.Cloud.Web.Auth.Security;
using Korat.Cloud.Web.Auth.Services;
using Korat.Cloud.Web.Spaces;
using Korat.Cloud.Web.Mcp;
using Korat.Domain;
using Korat.Domain.Auth;
using Korat.Domain.Persistence;
using Microsoft.AspNetCore;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using OpenIddict.Abstractions;
using OpenIddict.Server.AspNetCore;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Korat.Cloud.Web.Oauth;

/// <summary>
/// Space-MCP inc-2a, Tasks 3+4 (spec §Pillar C "Consent"): the /connect/authorize passthrough
/// handler. OpenIddict has ALREADY validated client_id / redirect_uri / response_type / PKCE
/// presence (per-client ft:pkce requirement + global RequireProofKeyForCodeExchange) before
/// this endpoint runs — an unknown client or wrong redirect_uri never reaches this code.
/// This handler owns what OpenIddict cannot know: WHO consents (the cookie-session owner),
/// WHAT they may consent to (korat:mcp only — SF-7), and FOR WHICH Space (exactly one
/// per-Space resource, owned by the signed-in user — BLOCKER-1's consent half).
/// </summary>
public static class KoratAuthorizeEndpoints
{
    internal sealed record ConsentRequestContext(
        OpenIddictRequest Request,
        UserId Owner,
        SpaceId SpaceId,
        string SpaceSeg,
        string Resource,
        string ClientId,
        object Application,
        string ClientDisplayName,
        string SpaceDisplayName,
        bool IsDcrClient);

    public static void MapKoratAuthorizeEndpoints(this WebApplication app)
    {
        app.MapGet("/connect/authorize", HandleGetAsync)
            .RequireRateLimiting(RateLimiterRegistration.InferencePreAuthPolicy);
        app.MapPost("/connect/authorize", HandlePostAsync)
            .RequireRateLimiting(RateLimiterRegistration.InferencePreAuthPolicy);
    }

    private static async Task<IResult> HandleGetAsync(
        HttpContext ctx,
        ISessionService sessions,
        SpaceSlugService slugService,
        IMetadataRepository repository,
        IOpenIddictApplicationManager applications,
        IOptions<CliOptions> cliOptions,
        IAntiforgery antiforgery,
        CancellationToken ct)
    {
        var request = ctx.GetOpenIddictServerRequest()
            ?? throw new InvalidOperationException("The OpenIddict server request cannot be retrieved.");

        var owner = await ResolveCookieOwnerAsync(ctx, sessions, ct);
        if (owner is null)
            return RedirectToSignin(ctx);

        var (error, consent) = await ValidateAsync(ctx, request, owner.Value, slugService, repository, applications, cliOptions.Value, ct);
        if (error is not null)
            return error;

        var tokens = antiforgery.GetAndStoreTokens(ctx);
        return Results.Content(RenderConsentPage(ctx, consent!, tokens.RequestToken!), "text/html; charset=utf-8");
    }

    /// <summary>
    /// Task 4 (spec §Pillar C "Consent"): accept/deny handler for the consent form POSTed by
    /// <see cref="RenderConsentPage"/>. Re-runs the FULL <see cref="ValidateAsync"/> chain —
    /// the hidden form fields are attacker-suppliable and are never trusted as authority; only
    /// the fresh cookie-owner + owner-owns-Space + korat:mcp-only re-check decides who this
    /// identity is FOR. On "allow": builds the sign-in <see cref="ClaimsIdentity"/> (subject =
    /// owner UserId "N", korat:space/korat:client claims, korat:mcp + offline_access scopes —
    /// MF-2, the per-Space URL as the SOLE resource/audience — BLOCKER-1), finds-or-creates the
    /// permanent authorization keyed by (subject, client, Space) so re-consent reuses the same
    /// row (Task 8's revocation anchor), and hands the identity to OpenIddict's
    /// Results.SignIn — the built-in Exchange.AttachPrincipal completes the code/refresh
    /// exchange from there (verified grounding #3 — no token-endpoint passthrough).
    /// </summary>
    private static async Task<IResult> HandlePostAsync(
        HttpContext ctx,
        ISessionService sessions,
        SpaceSlugService slugService,
        IMetadataRepository repository,
        IOpenIddictApplicationManager applications,
        IOpenIddictAuthorizationManager authorizations,
        IOptions<CliOptions> cliOptions,
        IAntiforgery antiforgery,
        IAuditLog auditLog,
        CancellationToken ct)
    {
        var request = ctx.GetOpenIddictServerRequest()
            ?? throw new InvalidOperationException("The OpenIddict server request cannot be retrieved.");

        var owner = await ResolveCookieOwnerAsync(ctx, sessions, ct);
        if (owner is null)
            return RedirectToSignin(ctx);

        // CSRF: the consent POST is a cookie-authenticated state change — validate the
        // antiforgery pair issued by the GET page (house pattern: RequireAntiforgeryExtensions'
        // explicit ValidateRequestAsync, done inline here because the form also carries the
        // OAuth parameters OpenIddict must read).
        try
        {
            await antiforgery.ValidateRequestAsync(ctx);
        }
        catch (AntiforgeryValidationException)
        {
            // Observability: this was the ONLY unlogged failure path in the whole consent flow —
            // a stale-tab antiforgery mismatch produced a silent dead-end JSON with no server
            // trace (invisible in error tracking). Log it, then SELF-HEAL: redirect back to a
            // fresh GET /connect/authorize (rebuilt from the re-emitted form params) instead of
            // dead-ending, so the owner gets a new, valid consent page rather than a broken one.
            // The SameSite=Lax antiforgery cookie (see AddAntiforgery) already stops the
            // per-attempt token rotation that made this common; this is the graceful fallback for
            // a genuinely stale token (e.g. the tab sat open past the token's lifetime).
            var retryForm = await ctx.Request.ReadFormAsync(ct);
            var alreadyRetried = retryForm.ContainsKey("consent_retry");
            ctx.RequestServices.GetRequiredService<ILoggerFactory>()
                .CreateLogger("Korat.Cloud.Web.Oauth.KoratAuthorizeEndpoints")
                .LogWarning("Consent POST antiforgery validation failed for client {ClientId}; {Action}.",
                    request.ClientId, alreadyRetried ? "already retried — returning 400" : "redirecting to a fresh consent page");
            // One-shot: if we ALREADY self-healed once (marker PRESENT — key existence, not value,
            // so a caller-seeded consent_retry can't defeat the guard by never string-equalling "1")
            // and antiforgery STILL fails, dead-end rather than loop (e.g. cookies fully disabled —
            // no cookie can ever round-trip). OpenIddict ignores the unknown param on the authorize
            // GET. consent_retry is also excluded from the rebuild below so a seeded value can't
            // comma-accumulate across heals.
            if (alreadyRetried)
                return Results.BadRequest(new { error = "antiforgery-failure" });
            var qs = QueryString.Create(retryForm
                .Where(f => f.Key is not ("__RequestVerificationToken" or "submit" or "consent_retry"))
                .SelectMany(f => f.Value.Select(v => new KeyValuePair<string, string?>(f.Key, v)))
                .Append(new KeyValuePair<string, string?>("consent_retry", "1")));
            return Results.Redirect("/connect/authorize" + qs.ToUriComponent());
        }

        // Re-run the FULL validation chain — never trust what the GET page displayed
        // (ownership could have changed; the form params are attacker-suppliable).
        var (error, consent) = await ValidateAsync(ctx, request, owner.Value, slugService, repository, applications, cliOptions.Value, ct);
        if (error is not null)
            return error;

        var form = await ctx.Request.ReadFormAsync(ct);
        if (form["submit"] != "allow")
            return OpenIddictError(Errors.AccessDenied, "The owner denied the authorization request.");

        // Subject format pinned to Guid "N" — same string in FindBySubjectAsync (Task 8's
        // revocation list) and the resource server's Guid.Parse (Task 6).
        var subject = owner.Value.Value.ToString("N");
        var identity = new ClaimsIdentity(
            authenticationType: Microsoft.IdentityModel.Tokens.TokenValidationParameters.DefaultAuthenticationType,
            nameType: Claims.Name,
            roleType: Claims.Role);
        identity.SetClaim(Claims.Subject, subject);
        identity.SetClaim(KoratOAuthConstants.SpaceClaim, consent!.SpaceId.Value);
        identity.SetClaim(KoratOAuthConstants.ClientClaim, consent.ClientId);
        // MF-2: offline_access is granted SERVER-SIDE (never requested by the client — the
        // korat:mcp-only request-scope policy above is unaffected) so OpenIddict's
        // EvaluateGeneratedTokens actually mints a refresh token (AllowRefreshTokenFlow alone
        // only enables the grant TYPE; no offline_access scope on the sign-in principal ==
        // no refresh token, ever). Without this, SF-7's "no refresh = hourly re-consent" bites.
        identity.SetScopes(KoratOAuthConstants.McpScope, Scopes.OfflineAccess);
        // RFC 8707 / BLOCKER-1: the per-Space URL becomes the access token's ONLY audience
        // (PrepareAccessTokenPrincipal maps resources → aud). Single writer of resources.
        identity.SetResources(consent.Resource);

        // Find-or-create the PERMANENT authorization for (subject, client, Space) — the
        // durable consent object Task 8 lists/revokes, and what makes refresh survive.
        var applicationId = (await applications.GetIdAsync(consent.Application, ct))!;
        object? authorization = null;
        await foreach (var candidate in authorizations.FindAsync(
            subject, applicationId, Statuses.Valid, AuthorizationTypes.Permanent, scopes: null, ct))
        {
            var properties = await authorizations.GetPropertiesAsync(candidate, ct);
            if (properties.TryGetValue(KoratOAuthConstants.AuthorizationSpaceProperty, out var element)
                && element.ValueKind == System.Text.Json.JsonValueKind.String
                && element.GetString() == consent.SpaceId.Value)
            {
                authorization = candidate;
                break;
            }
        }
        if (authorization is null)
        {
            var descriptor = new OpenIddictAuthorizationDescriptor
            {
                ApplicationId = applicationId,
                Principal = new ClaimsPrincipal(identity),
                Status = Statuses.Valid,
                Subject = subject,
                Type = AuthorizationTypes.Permanent,
            };
            // MF-2: the permanent authorization's Scopes must ALSO include offline_access —
            // FindAsync's optional `scopes` filter (unused above — we pass null and match on
            // the Space property instead) and any future scope-based lookup must see the same
            // grant the sign-in identity carries.
            descriptor.Scopes.Add(KoratOAuthConstants.McpScope);
            descriptor.Scopes.Add(Scopes.OfflineAccess);
            descriptor.Properties[KoratOAuthConstants.AuthorizationSpaceProperty] =
                System.Text.Json.JsonSerializer.SerializeToElement(consent.SpaceId.Value);
            authorization = await authorizations.CreateAsync(descriptor, ct);
        }
        identity.SetAuthorizationId(await authorizations.GetIdAsync(authorization, ct));

        // Everything on this identity is FOR the resource server; nothing is an OIDC identity
        // claim (openid can never be granted here — SF-7), so: access token only.
        identity.SetDestinations(static _ => [Destinations.AccessToken]);

        await auditLog.RecordAsync(new AuditEvent(
            Action: AuditActions.OAuthConsentGranted,
            TargetType: "oauth_client",
            TargetId: consent.ClientId,
            SpaceId: consent.SpaceId.Value,
            ActorType: AuditActorTypes.User,
            ActorId: owner.Value.Value.ToString()),
            required: true, ct);

        return Results.SignIn(
            new ClaimsPrincipal(identity),
            properties: null,
            authenticationScheme: OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
    }

    /// <summary>
    /// Consent identity comes from the browser session cookie ONLY — mirrors
    /// PolymorphicAuthResolver's cookie branch (PolymorphicAuthResolver.cs:27-46) but
    /// deliberately WITHOUT its bearer fallback: a CLI token must never mint OAuth consent.
    /// </summary>
    internal static async Task<UserId?> ResolveCookieOwnerAsync(
        HttpContext ctx, ISessionService sessions, CancellationToken ct)
    {
        if (!ctx.Request.Cookies.TryGetValue(CanonicalSigninHandler.SessionCookieName, out var raw)
            || !Guid.TryParse(raw, out var sessionId))
            return null;
        var bumped = await sessions.ValidateAndBumpAsync(sessionId, ct);
        return bumped?.UserId;
    }

    internal static IResult RedirectToSignin(HttpContext ctx) =>
        Results.Redirect("/app/signin?returnUrl=" +
            Uri.EscapeDataString(ctx.Request.Path + ctx.Request.QueryString));

    /// <summary>OpenIddict-shaped error: a Forbid against the OpenIddict scheme becomes a
    /// standards-compliant error redirect to the (already-validated) redirect_uri.</summary>
    internal static IResult OpenIddictError(string error, string description) =>
        Results.Forbid(
            properties: new AuthenticationProperties(new Dictionary<string, string?>
            {
                [OpenIddictServerAspNetCoreConstants.Properties.Error] = error,
                [OpenIddictServerAspNetCoreConstants.Properties.ErrorDescription] = description,
            }),
            authenticationSchemes: [OpenIddictServerAspNetCoreDefaults.AuthenticationScheme]);

    /// <summary>
    /// The full consent-request validation chain — run by GET (to render) AND re-run by POST
    /// (never trusting anything the page displayed). Order: scope policy (cheap, no I/O) →
    /// resource shape → Space resolution → ownership.
    /// </summary>
    internal static async Task<(IResult? Error, ConsentRequestContext? Context)> ValidateAsync(
        HttpContext ctx,
        OpenIddictRequest request,
        UserId owner,
        SpaceSlugService slugService,
        IMetadataRepository repository,
        IOpenIddictApplicationManager applications,
        CliOptions cliOptions,
        CancellationToken ct)
    {
        // SF-7: an MCP client MUST request korat:mcp, and MAY additionally request offline_access
        // — the standard refresh-token scope the real MCP SDKs (Claude Code, Cursor) auto-append
        // to the authorize request. We already grant offline_access SERVER-SIDE at sign-in (see
        // below), so honoring it in the request is consistent and issues nothing extra. Any OTHER
        // scope (openid/profile/email/…) is still rejected here. The client's scp: permissions
        // exclude identity scopes at the OpenIddict layer, but OpenIddict EXEMPTS openid AND
        // offline_access from that per-client check, so this semantic check is the real,
        // independent stop against identity-scope escalation (defense in depth — a future
        // misconfigured client must still be stopped). Regardless of the request, we only ever
        // ISSUE {korat:mcp, offline_access} with AccessToken-only destinations (below), so no
        // id_token / identity data is ever exposed.
        var scopes = request.GetScopes();
        if (scopes.IsDefaultOrEmpty
            || !scopes.Contains(KoratOAuthConstants.McpScope, StringComparer.Ordinal)
            || scopes.Any(s => !string.Equals(s, KoratOAuthConstants.McpScope, StringComparison.Ordinal)
                               && !string.Equals(s, Scopes.OfflineAccess, StringComparison.Ordinal)))
            return (OpenIddictError(Errors.InvalidScope,
                "Only the korat:mcp and offline_access scopes may be requested for the Korat MCP surface."), null);

        // RFC 8707: exactly ONE resource — the per-Space MCP URL this token will be usable at.
        var resources = request.GetResources();
        if (resources.Length != 1)
            return (OpenIddictError(Errors.InvalidTarget,
                "Exactly one resource (the per-Space MCP URL) must be requested."), null);
        var resource = resources[0];

        var origin = McpOAuthConnectActionBuilder.ResolveOrigin(cliOptions, ctx.Request);
        var prefix = $"{origin}/mcp/";
        if (!resource.StartsWith(prefix, StringComparison.Ordinal))
            return (OpenIddictError(Errors.InvalidTarget,
                "The resource must be this server's per-Space MCP URL."), null);
        var spaceSeg = resource[prefix.Length..];
        if (spaceSeg.Length == 0 || spaceSeg.Contains('/') || spaceSeg.Contains('?') || spaceSeg.Contains('#'))
            return (OpenIddictError(Errors.InvalidTarget,
                "The resource must be this server's per-Space MCP URL."), null);

        var spaceId = await slugService.ResolveSpaceSegmentAsync(spaceSeg, ct);
        if (spaceId is null)
            return (OpenIddictError(Errors.InvalidTarget, "Unknown resource."), null);

        // Owner-owns-Space (spec §Pillar C "Consent"; F45 analog). Cloaked: a signed-in
        // non-owner gets the same access_denied wording whether the Space exists or not.
        var space = await repository.GetSpaceAsync(spaceId.Value, ct);
        if (space is null || space.OwnerUserId != owner)
            return (OpenIddictError(Errors.AccessDenied, "You do not have access to this resource."), null);

        var application = await applications.FindByClientIdAsync(request.ClientId!, ct)
            ?? throw new InvalidOperationException("The application details cannot be found."); // OpenIddict pre-validated client_id
        var clientDisplayName = await applications.GetDisplayNameAsync(application, ct) ?? request.ClientId!;

        // Plan-review SF-3: client_name (⇒ DisplayName above) is attacker-controlled for a
        // DCR-registered client (e.g. it can claim to be "Korat Official") — HTML-encoded at
        // render time so this is never XSS, but it IS full visual impersonation at the human
        // consent gate, the ONLY defense DCR has. The korat:dcr marker Properties stamped by
        // DcrEndpoints (Task 4) is the sole signal available: its PRESENCE means this client
        // walked in through open, unauthenticated registration rather than being pre-registered
        // by the operator, so the consent page renders an "unverified / auto-registered" badge.
        var properties = await applications.GetPropertiesAsync(application, ct);
        var isDcrClient = properties.ContainsKey(KoratOAuthConstants.DcrMarkerProperty);

        return (null, new ConsentRequestContext(
            request, owner, spaceId.Value, spaceSeg, resource, request.ClientId!,
            application, clientDisplayName, space.DisplayName, isDcrClient));
    }

    /// <summary>
    /// Minimal self-contained consent page. The form POSTs back to /connect/authorize with
    /// EVERY original query parameter re-emitted as hidden inputs — OpenIddict reads a POST
    /// authorize request's parameters from the FORM body (verified grounding #11), so
    /// omitting them would make the POST an invalid_request. All interpolated values are
    /// HTML-encoded (client/space names are owner-controlled strings).
    /// </summary>
    private static string RenderConsentPage(HttpContext ctx, ConsentRequestContext consent, string antiforgeryToken)
    {
        var hiddenFields = string.Join("\n", ctx.Request.Query.Select(p =>
            $"<input type=\"hidden\" name=\"{WebUtility.HtmlEncode(p.Key)}\" value=\"{WebUtility.HtmlEncode(p.Value.ToString())}\" />"));
        var client = WebUtility.HtmlEncode(consent.ClientDisplayName);
        var space = WebUtility.HtmlEncode(consent.SpaceDisplayName);
        var resource = WebUtility.HtmlEncode(consent.Resource);
        // SF-3: an "unverified / auto-registered" badge for DCR clients — the client_name above
        // is attacker-controlled, so this is the one signal the page can show that is NOT
        // spoofable by the client itself (it is derived from the korat:dcr marker property, set
        // server-side by DcrEndpoints at registration, never from request data).
        var dcrBadge = consent.IsDcrClient
            ? """<p class="dcr-badge">⚠ Unverified · auto-registered via open client registration (DCR). This app was not manually reviewed by Korat.</p>"""
            : "";
        return $$"""
            <!DOCTYPE html>
            <html lang="en">
            <head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
            <title>Authorize {{client}} — Korat</title>
            <style>
              body { font-family: system-ui, sans-serif; max-width: 32rem; margin: 4rem auto; padding: 0 1rem; }
              .card { border: 1px solid #ddd; border-radius: 8px; padding: 1.5rem; }
              .scope { font-family: monospace; background: #f4f4f4; padding: 0 .3rem; border-radius: 4px; }
              .dcr-badge { background: #fff4e5; border: 1px solid #e8a33d; border-radius: 6px; padding: .6rem .8rem; font-size: .9rem; }
              .actions { display: flex; gap: .75rem; margin-top: 1.5rem; }
              button { padding: .6rem 1.4rem; border-radius: 6px; border: 1px solid #888; cursor: pointer; }
              button[value="allow"] { background: #1a7f37; color: #fff; border-color: #1a7f37; }
            </style></head>
            <body><div class="card">
            <h1>Authorize access</h1>
            {{dcrBadge}}
            <p><strong>{{client}}</strong> is requesting access to the MCP tools of your Space
               <strong>{{space}}</strong> (<code>{{resource}}</code>) with scope
               <span class="scope">korat:mcp</span>.</p>
            <p>It will be able to call every MCP tool you have granted (or later grant) to it in
               this Space. It will NOT get access to your Korat account or identity.</p>
            <form method="post" action="/connect/authorize">
            {{hiddenFields}}
            <input type="hidden" name="__RequestVerificationToken" value="{{antiforgeryToken}}" />
            <div class="actions">
              <button type="submit" name="submit" value="allow">Allow</button>
              <button type="submit" name="submit" value="deny">Deny</button>
            </div>
            </form>
            </div></body></html>
            """;
    }
}
