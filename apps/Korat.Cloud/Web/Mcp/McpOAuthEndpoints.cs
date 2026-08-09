using Korat.Cloud.Mcp.Oauth;
using Korat.Cloud.Security.Audit;
using Korat.Cloud.Web;
using Korat.Cloud.Web.Auth;
using Korat.Cloud.Web.Auth.Options;
using Korat.Cloud.Web.Auth.Security;
using Korat.Domain;
using Korat.Domain.Persistence;
using Microsoft.Extensions.Options;

namespace Korat.Cloud.Web.Mcp;

/// <summary>POST /api/mcp-servers, PATCH .../{id} (oauth branches, in Endpoints.cs), and
/// /reconnect (this file) all return this same shape — never token material, only an
/// authorizeUrl for the console to redirect the owner's browser to, or a safe error string.</summary>
internal sealed record McpOAuthConnectActionResult(string? AuthorizeUrl, string? Error);

/// <summary>
/// SHOULD-FIX 5 (fable plan-review): the OPTIONAL request body for POST .../{id}/reconnect. Both
/// fields are null when omitted entirely (the common case — reconnecting a server whose stored/DCR
/// client is still usable). Only meaningful for a manual-cred (no-DCR) oauth server whose stored
/// client credentials were cleared by a PATCH RemoteUrl (Endpoints.cs's edit-path invalidation
/// rule) — without this body, such a server had no stored client AND no way to supply one, so
/// /reconnect could never succeed for it.
/// </summary>
internal sealed class ReconnectMcpServerRequest
{
    public string? ClientId { get; set; }
    public string? ClientSecret { get; set; }
}

/// <summary>"Auto-detect auth mode" feature: the request body for POST
/// /api/mcp-servers/detect-auth.</summary>
internal sealed class DetectAuthModeRequest
{
    public string? RemoteUrl { get; set; }
}

/// <summary>
/// Increment 2 (HTTP MCP OAuth): the discovery→(DCR or reuse-stored-client or manual)→pending-
/// grain→authorizeUrl sequence, shared by POST /api/mcp-servers (oauth mode), PATCH .../{id}
/// (authMode switched to oauth), and POST .../{id}/reconnect — one implementation, so the three
/// call sites cannot drift on the redirect_uri or PKCE/state construction.
/// </summary>
internal static class McpOAuthConnectActionBuilder
{
    internal static readonly TimeSpan PendingOAuthTtl = TimeSpan.FromMinutes(15);

    public static string RedirectUriFor(string publicOrigin, string serverId) =>
        $"{publicOrigin.TrimEnd('/')}/api/mcp/oauth/callback/{serverId}";

    /// <summary>
    /// Finding 2 (Task 4 review, hardening): the ONE place that resolves the public origin used
    /// to build a redirect_uri. ALL FOUR call sites — POST create (oauth mode), PATCH (switch to
    /// oauth), POST .../reconnect, and the callback's token-exchange — must resolve identically:
    /// RFC 6749 §4.1.3 requires the redirect_uri sent at the authorize step and at the
    /// token-exchange step to be byte-identical, or the exchange fails invalid_grant. Before this
    /// helper existed, the three initiation sites used `!string.IsNullOrEmpty(PublicOrigin) ? ... :
    /// host` while the callback used `PublicOrigin ?? host` — these only diverge when PublicOrigin
    /// is "" (empty-but-not-null: unset in config, which binds to string.Empty, not null), which is
    /// latent in prod (PublicOrigin is always set there) but must never be allowed to drift further.
    /// </summary>
    public static string ResolveOrigin(CliOptions cliOptions, HttpRequest request) =>
        !string.IsNullOrEmpty(cliOptions.PublicOrigin)
            ? cliOptions.PublicOrigin!.TrimEnd('/')
            : $"{request.Scheme}://{request.Host}";

    public static async Task<McpOAuthConnectActionResult> BuildAsync(
        Domain.Entities.McpServer server,
        string? ownerClientId,
        string? ownerClientSecret,
        Guid ownerUserId,
        string publicOrigin,
        McpOAuthDiscoveryService discovery,
        McpOAuthClientRegistrar registrar,
        IMetadataRepository repository,
        IEnvelopeCrypto envelopeCrypto,
        IClusterClient clusterClient,
        CancellationToken ct)
    {
        var redirectUri = RedirectUriFor(publicOrigin, server.Id.Value);

        McpOAuthServerMetadata metadata;
        try
        {
            metadata = await discovery.DiscoverAsync(server.RemoteUrl!, ct);
        }
        catch (McpOAuthDiscoveryException ex)
        {
            return new McpOAuthConnectActionResult(null, ex.Message);
        }

        string clientId;
        string? clientSecret;
        if (!string.IsNullOrEmpty(ownerClientId))
        {
            // Manual client-credentials fallback — supplied by the owner at create/patch time.
            clientId = ownerClientId;
            clientSecret = ownerClientSecret;
        }
        else
        {
            // Reconnect (and any BuildAsync call with no manual credentials): reuse the stored
            // client if a token document already exists, even a stale/invalid one — only run
            // DCR fresh when nothing is stored yet (spec: "reconnect reuses the stored client").
            McpOAuthTokenDocument? existing = null;
            var existingCiphertext = await repository.GetMcpServerOAuthTokenCiphertextAsync(server.Id, ct);
            if (existingCiphertext is not null)
            {
                try
                {
                    var json = await envelopeCrypto.DecryptAsync(server.SpaceId, Korat.Cloud.Security.Envelope.McpServerSecretCrypto.OAuthAad(server.Id), existingCiphertext, ct);
                    existing = McpOAuthTokenDocument.Deserialize(json);
                }
                catch (Exception)
                {
                    existing = null; // fail closed to a fresh DCR attempt below
                }
            }

            if (existing is not null)
            {
                clientId = existing.ClientId;
                clientSecret = existing.ClientSecret;
            }
            else if (metadata.RegistrationEndpoint is not null)
            {
                try
                {
                    var registration = await registrar.RegisterAsync(metadata.RegistrationEndpoint, redirectUri, ct);
                    clientId = registration.ClientId;
                    clientSecret = registration.ClientSecret;
                }
                catch (McpOAuthDiscoveryException ex)
                {
                    return new McpOAuthConnectActionResult(null, ex.Message);
                }
            }
            else
            {
                return new McpOAuthConnectActionResult(null,
                    "This authorization server does not support dynamic client registration; supply clientId/clientSecret manually.");
            }
        }

        // The browser follows this URL directly — SsrfGuardedHttpClientFactory can't guard it
        // (nothing dials it server-side), so it is explicitly validated here before being handed
        // to the console (spec §"Security → SSRF, no exceptions").
        var authorizeEndpointError = Korat.Cloud.Web.Spaces.SsrfGuard.ValidateUrl(metadata.AuthorizationEndpoint);
        if (authorizeEndpointError is not null)
            return new McpOAuthConnectActionResult(null, $"authorization_endpoint: {authorizeEndpointError}");

        var verifier = McpOAuthPkce.GenerateVerifier();
        var challenge = McpOAuthPkce.Challenge(verifier);
        var state = McpOAuthPkce.GenerateState();

        var pending = new Korat.GrainInterfaces.PendingOAuthState(
            server.Id.Value, ownerUserId, server.SpaceId.Value, verifier,
            metadata.Issuer, metadata.AuthorizationEndpoint, metadata.TokenEndpoint, clientId, clientSecret);
        await clusterClient.GetGrain<Korat.GrainInterfaces.IPendingOAuthGrain>(state).InitializeAsync(pending, PendingOAuthTtl);
        await clusterClient.GetGrain<Korat.GrainInterfaces.IPendingOAuthPointerGrain>(server.Id.Value).SetCurrentStateAsync(state, PendingOAuthTtl);

        var authorizeUrl = McpOAuthPkce.BuildAuthorizeUrl(
            metadata.AuthorizationEndpoint, clientId, redirectUri, state, challenge, server.RemoteUrl!);
        return new McpOAuthConnectActionResult(authorizeUrl, null);
    }
}

public static class McpOAuthEndpoints
{
    public static void MapMcpOAuthEndpoints(this WebApplication app)
    {
        // "Auto-detect auth mode" feature: the Add-HTTP-MCP-server form probes this on the Remote
        // URL field's onBlur to pre-select the Auth dropdown. Same trust level as POST
        // /api/mcp-servers (an outbound dial to a caller-supplied URL) — owner-gated + rate-limited
        // identically, SSRF-validated before any dial. Best-effort: DetectAuthModeAsync never
        // throws, so this handler never 500s — every failure mode surfaces as {authMode:"unknown"}.
        app.MapPost("/api/mcp-servers/detect-auth", async (
            DetectAuthModeRequest body,
            HttpContext ctx,
            SpaceResolver spaceResolver,
            McpOAuthDiscoveryService discovery,
            CancellationToken ct) =>
        {
            var userId = (Korat.Domain.Auth.UserId)ctx.Items[KoratHttpContextItems.UserIdKey]!;
            var spaceId = await spaceResolver.ResolveDefaultSpaceIdAsync(userId, ct);
            if (spaceId is null)
                return Results.StatusCode(StatusCodes.Status403Forbidden);

            var ssrfError = Korat.Cloud.Web.Spaces.SsrfGuard.ValidateUrl(body.RemoteUrl);
            if (ssrfError is not null)
                return Results.Json(new { error = $"remoteUrl: {ssrfError}" }, statusCode: 400);

            var mode = await discovery.DetectAuthModeAsync(body.RemoteUrl!, ct);
            return Results.Ok(new { authMode = McpAuthModeStrings.ToWireString(mode) });
        }).RequireSpaceOwner()
          .RequireRateLimiting(RateLimiterRegistration.OwnerManagementPolicy);

        app.MapPost("/api/mcp-servers/{serverId}/reconnect", async (
            string serverId, HttpContext ctx, IClusterClient clusterClient,
            SpaceResolver spaceResolver, IMetadataRepository repository,
            IEnvelopeCrypto envelopeCrypto, McpOAuthDiscoveryService discovery,
            McpOAuthClientRegistrar registrar, IOptions<CliOptions> cliOptions, IAuditLog auditLog,
            // SHOULD-FIX 5 (fable plan-review): OPTIONAL body — nullable, so a bare
            // `POST .../reconnect` with no body (the common case) still binds fine. Only a
            // manual-cred server whose stored client was cleared by a PATCH RemoteUrl needs to
            // actually supply {clientId, clientSecret} here.
            ReconnectMcpServerRequest? body,
            CancellationToken ct) =>
        {
            var userId = (Korat.Domain.Auth.UserId)ctx.Items[KoratHttpContextItems.UserIdKey]!;
            var spaceId = await spaceResolver.ResolveDefaultSpaceIdAsync(userId, ct);
            if (spaceId is null)
                return Results.StatusCode(StatusCodes.Status403Forbidden);

            var mcpServerId = new McpServerId(serverId);
            var server = await repository.GetMcpServerAsync(mcpServerId, ct);
            if (server is null || server.SpaceId != spaceId.Value)
                return Results.NotFound();
            if (!McpServerAuthModes.IsOAuth(server.AuthMode))
                return Results.Json(new { error = "Only oauth servers can be reconnected." }, statusCode: 400);

            var publicOrigin = McpOAuthConnectActionBuilder.ResolveOrigin(cliOptions.Value, ctx.Request);

            var connect = await McpOAuthConnectActionBuilder.BuildAsync(
                server, ownerClientId: body?.ClientId, ownerClientSecret: body?.ClientSecret, userId.Value, publicOrigin,
                discovery, registrar, repository, envelopeCrypto, clusterClient, ct);

            await auditLog.RecordAsync(new AuditEvent(
                Action: AuditActions.McpServerOAuthReconnectRequested,
                TargetType: "mcp_server", TargetId: serverId, SpaceId: spaceId.Value.Value,
                ActorType: AuditActorTypes.User, ActorId: userId.Value.ToString()),
                required: true, ct);

            return Results.Ok(connect);
        }).RequireSpaceOwner()
          .RequireRateLimiting(RateLimiterRegistration.OwnerManagementPolicy);

        // Implementation note (SHOULD-FIX 5): a nullable complex-type minimal-API parameter
        // (`ReconnectMcpServerRequest? body`) binds from the JSON request body when present, and
        // binds to `null` — NOT a 415/400 — when the request has no body at all, exactly matching
        // this plan's existing `client.PostAsync(".../reconnect", null)` test calls.

        // Increment 2: the OAuth authorize redirect lands here. Requires the owner's authenticated
        // browser session — works because __Host-korat_session is SameSite=Lax, which rides the
        // top-level cross-site GET redirect from the authorization server (pinned; a future switch
        // to Strict would silently break every consent). code/state/iss are never logged verbatim
        // past this handler.
        app.MapGet("/api/mcp/oauth/callback/{serverId}", async (
            string serverId, string? code, string? state, string? iss, HttpContext ctx,
            IClusterClient clusterClient, IEnvelopeCrypto envelopeCrypto,
            IMetadataRepository repository, IAuditLog auditLog, ILogger<McpOAuthConnectActionResult> logger,
            IOptions<CliOptions> cliOptions,
            CancellationToken ct) =>
        {
            var userId = (Korat.Domain.Auth.UserId)ctx.Items[KoratHttpContextItems.UserIdKey]!;

            if (string.IsNullOrEmpty(code) || string.IsNullOrEmpty(state))
                return Results.Redirect($"/app/servers/{serverId}?connected=false&reason=missing_code_or_state");

            // Blocker 2 fix (fable plan-review): PEEK first (non-consuming) — every validation
            // below runs on the peeked value, and NONE of them may burn the pending state. Only
            // once every check has passed do we actually consume (burn) it. This is the reason a
            // NEW PeekAsync method exists on IPendingOAuthGrain (Task 4, Step 3): the previous
            // ordering called ConsumeAsync (a burn) BEFORE the path-serverId mismatch check, which
            // made the per-server-callback-URI mix-up defense (the spec's OWN named, load-bearing
            // defense — see Grounding Note 1) dead code, because the supersession check (keyed by
            // the PATH's serverId, which for an attacker's replay is the WRONG server and so never
            // has a matching pointer) always fired first and returned "superseded" before the
            // mismatch check could ever be reached.
            var pending = await clusterClient.GetGrain<Korat.GrainInterfaces.IPendingOAuthGrain>(state).PeekAsync();
            if (pending is null)
                return Results.Redirect($"/app/servers/{serverId}?connected=false&reason=expired_or_replayed");

            // Anti-CSRF: state is bound to the authenticated owner who started this flow.
            if (pending.OwnerUserId != userId.Value)
                return Results.Redirect($"/app/servers/{serverId}?connected=false&reason=wrong_owner");

            // Mix-up defense (RFC 9700 §4.4, spec §"Security → Mix-up") — THE load-bearing,
            // spec-named defense, now actually reachable: the path serverId — bound to THIS AS's
            // own registered per-server redirect_uri — must equal the pending flow's own serverId.
            // A real AS can only ever redirect to its OWN registered client's URI, so an attacker
            // replaying one server's callback at a DIFFERENT server's path is rejected here, before
            // any supersession check or consumption.
            if (!string.Equals(pending.ServerId, serverId, StringComparison.Ordinal))
            {
                logger.LogWarning("OAuth callback mix-up rejected: pathServerId={PathServerId} pendingServerId={PendingServerId}", serverId, pending.ServerId);
                return Results.Redirect($"/app/servers/{serverId}?connected=false&reason=mismatch");
            }

            // Plan-time decision (a): supersession check, keyed by the PENDING flow's OWN serverId
            // (== the path serverId, now that the mismatch check above has passed) — an older,
            // still-unconsumed state is rejected once a NEWER authorize/reconnect action has
            // overwritten this server's pointer. Deliberately keyed by pending.ServerId, not the
            // raw path serverId, so this check only ever runs once the mix-up check has already
            // proven the two are the same value.
            var pointerState = await clusterClient.GetGrain<Korat.GrainInterfaces.IPendingOAuthPointerGrain>(pending.ServerId).GetCurrentStateAsync();
            if (pointerState != state)
                return Results.Redirect($"/app/servers/{serverId}?connected=false&reason=superseded");

            // RFC 9207 iss, when the AS emits it. 2025-06-18 does NOT mandate it (Context7-pinned,
            // see the increment-2 plan's Grounding Note 1) — defense-in-depth, not load-bearing.
            if (!string.IsNullOrEmpty(iss) && !string.Equals(iss, pending.Issuer, StringComparison.Ordinal))
                return Results.Redirect($"/app/servers/{serverId}?connected=false&reason=issuer_mismatch");

            // Every check passed on the PEEKED data — NOW burn it. ConsumeAsync can still
            // independently return null here if a concurrent request for the SAME state won a
            // race between this request's peek and this consume — single-use is still enforced,
            // this is not a new hole, just the ordering that makes the mix-up defense reachable.
            var confirmed = await clusterClient.GetGrain<Korat.GrainInterfaces.IPendingOAuthGrain>(state).ConsumeAsync();
            if (confirmed is null)
                return Results.Redirect($"/app/servers/{serverId}?connected=false&reason=expired_or_replayed");

            var mcpServerId = new McpServerId(serverId);
            var server = await repository.GetMcpServerAsync(mcpServerId, ct);
            if (server is null || server.SpaceId.Value != confirmed.SpaceId)
                return Results.Redirect($"/app/servers/{serverId}?connected=false&reason=not_found");

            // Finding 3 (Task 4 review, hardening): if the owner PATCHed the server AWAY from
            // oauth (e.g. authMode:"none") while this consent was in flight, an old callback must
            // not be allowed to complete it — that would store a fresh OAuth token ciphertext as
            // dead storage on a now-non-oauth server and flip it to Published via
            // MarkOAuthConnectedAsync. Only complete a consent for a server that is STILL oauth.
            if (!McpServerAuthModes.IsOAuth(server.AuthMode))
                return Results.Redirect($"/app/servers/{serverId}?connected=false&reason=not_oauth");

            McpOAuthTokenResult tokenResult;
            try
            {
                tokenResult = await McpOAuthTokenExchange.ExchangeAuthorizationCodeAsync(
                    ctx.RequestServices.GetRequiredService<IOutboundHttpClientFactory>(),
                    confirmed.TokenEndpoint, code, confirmed.PkceVerifier,
                    McpOAuthConnectActionBuilder.RedirectUriFor(
                        McpOAuthConnectActionBuilder.ResolveOrigin(cliOptions.Value, ctx.Request), serverId),
                    confirmed.ClientId, confirmed.ClientSecret, server.RemoteUrl!, ct);
            }
            catch (Exception ex) when (ex is McpOAuthInvalidGrantException or McpOAuthTransientTokenException or McpOAuthDiscoveryException)
            {
                logger.LogWarning("OAuth token exchange failed serverId={ServerId} reason={Reason}", serverId, ex.Message);
                return Results.Redirect($"/app/servers/{serverId}?connected=false&reason=token_exchange_failed");
            }

            var doc = new McpOAuthTokenDocument(
                tokenResult.AccessToken, tokenResult.RefreshToken, tokenResult.AccessExpiry,
                confirmed.TokenEndpoint, confirmed.Issuer, confirmed.ClientId, confirmed.ClientSecret);
            var ciphertext = await envelopeCrypto.EncryptAsync(
                server.SpaceId, Korat.Cloud.Security.Envelope.McpServerSecretCrypto.OAuthAad(mcpServerId),
                McpOAuthTokenDocument.Serialize(doc), ct);
            await repository.SetMcpServerOAuthTokenAsync(mcpServerId, ciphertext, ct);

            await clusterClient.GetGrain<Korat.GrainInterfaces.IMcpServerGrain>(serverId).MarkOAuthConnectedAsync();

            await auditLog.RecordAsync(new AuditEvent(
                Action: AuditActions.McpServerOAuthConnected,
                TargetType: "mcp_server", TargetId: serverId, SpaceId: server.SpaceId.Value,
                ActorType: AuditActorTypes.User, ActorId: userId.Value.ToString()),
                required: true, ct);

            return Results.Redirect($"/app/servers/{serverId}?connected=true");
        }).RequireSpaceOwner();
    }
}
