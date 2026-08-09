using System.Text.Json;
using Korat.Cloud.Security.Audit;
using Korat.Cloud.Web.Auth;
using Korat.Cloud.Web.Auth.Security;
using Korat.Cloud.Web.Spaces;
using Korat.Domain;
using Korat.Domain.Contracts;
using Korat.Domain.Auth;
using Korat.Domain.Persistence;
using Korat.GrainInterfaces;

namespace Korat.Cloud.Web;

public static class SpaceOverviewEndpoints
{
    public static void MapSpaceOverviewEndpoints(this WebApplication app)
    {
        // F1 + F2 + F4 (Task 6): resolve the caller's identity → default SpaceId → grain.
        // All Space-scoped content (nodes, servers, access requests) flows through ISpaceGrain
        // whose key IS the SpaceId (design §3.4).
        // Reading SpaceRecord.DisplayName directly is allowed — it is Space *metadata* (name),
        // not Space *content* (nodes / members / grants), per design §3.3.
        app.MapGet("/api/space", async (
            HttpContext ctx,
            IClusterClient clusterClient,
            SpaceResolver spaceResolver,
            CancellationToken ct) =>
        {
            var userId = (UserId)ctx.Items[KoratHttpContextItems.UserIdKey]!;
            // Single DB round-trip: returns both SpaceId and DisplayName (no second query).
            var resolved = await spaceResolver.ResolveDefaultSpaceAsync(userId, ct);
            if (resolved is null)
            {
                // Invariant broken: authenticated user has no default Space.
                // SpaceResolver has already logged an error — return 403 (not 404: the resource
                // IS the caller's own Space; 404 would be misleading).
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }

            var (spaceId, displayName) = resolved.Value;
            var spaceGrain = clusterClient.GetGrain<ISpaceGrain>(spaceId.Value);
            // Fetch nodes + servers + access-requests in parallel for one round-trip per grain.
            var nodesTask   = spaceGrain.ListNodesAsync();
            var serversTask = spaceGrain.ListMcpServersAsync();
            var requestsTask = spaceGrain.ListAccessRequestsAsync();
            await Task.WhenAll(nodesTask, serversTask, requestsTask).WaitAsync(ct);
            var nodes = nodesTask.Result;
            var servers = serversTask.Result;
            var pendingRequests = requestsTask.Result;

            // 021: build a quick nodeId → node map so each server DTO can resolve its publisher's
            // name and live presence without a separate grain call per server. The node list is
            // already fetched above (ListNodesAsync fans out to NodeGrains for live LastSeenAt).
            var nodeById = nodes.ToDictionary(n => n.Id);

            // 028: resolve agent display names for pending access requests using the same
            // FriendlyNameHelpers pattern as grants/sessions — one grain call per distinct
            // agentClientId, never N calls per row. Server names come from the already-fetched
            // servers list (no extra grain calls).
            var nodeNames = nodes.ToDictionary(n => n.Id.Value, n => n.DisplayName);
            var serverNames = servers.ToDictionary(s => s.Id.Value, s => s.DisplayName);
            // Finding 16, S3: keyed by the same already-fetched `servers` list — no extra grain
            // call — so pendingAccessRequests below can tell an http_cloud server's request apart
            // from a stdio_node one without re-fetching anything.
            var serverTransports = servers.ToDictionary(s => s.Id.Value, s => s.Transport);
            var pendingOnly = pendingRequests.Where(r => r.Status == AccessRequestStatus.Pending).ToList();
            var agentNames = await FriendlyNameHelpers.ResolveAgentNamesAsync(
                pendingOnly.Select(r => r.ConsumerId.Value).Distinct(),
                clusterClient,
                nodeNames,
                ct);

            // 019: serverTime + presenceStaleSeconds let the frontend compute online/offline
            // against server time (clock-skew safe). Node status is the RAW stored value —
            // the frontend derives the effective presence indicator from lastSeenAt age.
            return Results.Ok(new
            {
                id = spaceId,
                displayName,
                serverTime = DateTimeOffset.UtcNow,
                presenceStaleSeconds = (int)NodePresenceRules.StaleThreshold.TotalSeconds,
                nodes = nodes.Select(n => new
                {
                    n.Id,
                    n.DisplayName,
                    // 019: raw stored Status — do NOT pre-apply NodePresenceRules.EffectiveStatus.
                    // The frontend derives the effective indicator from lastSeenAt age vs serverTime.
                    Status = n.Status.ToString(),
                    n.LastSeenAt,
                    // #167 review (fix 1): expose CreatedAt so callers can mirror
                    // PruneAgentNodesAsync's never-seen fallback (LastSeenAt ?? CreatedAt) client-side
                    // — e.g. `korat nodes prune`'s preview needs the SAME cutoff the cloud applies.
                    n.CreatedAt,
                    // 017: lowercase string so the SPA can compare without case-folding.
                    kind = n.Kind.ToString().ToLowerInvariant(),
                    // node-visibility-doctor (2026-07-02): host metadata (from NodeHello, refreshed
                    // on every hello) + owner-editable Note (set only via PATCH /api/nodes/{id}).
                    // All nullable — legacy CLIs / never-set notes surface as null, not absent.
                    n.Hostname,
                    n.Os,
                    n.Arch,
                    n.CliVersion,
                    n.Note
                }),
                mcpServers = servers.Select(s =>
                {
                    // Increment 1 (Task 6, Crux Finding 8): an http_cloud server's PublisherNodeId
                    // is "" by design (no relay node owns it) — do NOT attempt the node lookup/
                    // short-id fallback for it (previously produced a silently-blank
                    // publisherNodeName for a server that fundamentally has none).
                    var isHttpCloud = Korat.Domain.McpServerTransports.IsHttpCloud(s.Transport);
                    // 021: resolve publisher node presence from the already-fetched node list.
                    // Do NOT pre-compute availability (Disabled wins over Unavailable, ticking is
                    // done on the frontend with useNow — mirrors 019 node presence approach).
                    Domain.Entities.Node? publisherNode = isHttpCloud
                        ? null
                        : (nodeById.TryGetValue(s.PublisherNodeId, out var n) ? n : null);
                    return new
                    {
                        s.Id,
                        s.DisplayName,
                        s.Status,
                        // 021: set-membership bit — false = soft-retired (omitted from last sync).
                        s.IsAsserted,
                        s.LastSeenAt,
                        // 021: publisher node identity + live presence so the frontend can derive
                        // availability = Published && IsAsserted && publisherNode.online.
                        // Increment 1: null for http_cloud (no publisher node exists at all).
                        // Task-6-gate MEDIUM fix: serialize the full NodeId struct (→ {value:…}),
                        // not (string?)s.PublisherNodeId.Value — the latter silently changed the
                        // wire shape of this field for EVERY stdio_node row too (struct → bare
                        // string), the only pre-existing field this http_cloud change touched.
                        publisherNodeId = isHttpCloud ? (NodeId?)null : s.PublisherNodeId,
                        publisherNodeName = isHttpCloud ? null : (publisherNode?.DisplayName
                            ?? s.PublisherNodeId.Value[..Math.Min(8, s.PublisherNodeId.Value.Length)]),
                        publisherNodeLastSeenAt = isHttpCloud ? null : publisherNode?.LastSeenAt,
                        publisherNodeStatus = isHttpCloud ? null : publisherNode?.Status.ToString(),
                        // Increment 1: http_cloud-only fields — null for stdio_node.
                        transport = s.Transport,
                        remoteUrl = s.RemoteUrl,
                        authMode = s.AuthMode,
                        authHeaderName = s.AuthHeaderName,
                        hasSecret = s.SecretHint is not null,
                        secretHint = s.SecretHint
                    };
                }),
                // 028: include display names so the console can render "agent → server" instead
                // of raw GUIDs. Raw ids are preserved alongside names (SPA may show them as
                // muted suffixes for debugging). Falls back to short id when name is unavailable.
                pendingAccessRequests = pendingOnly.Select(r => new
                {
                    r.Id,
                    r.ConsumerId,
                    consumerDisplayName = agentNames.GetValueOrDefault(
                        r.ConsumerId.Value,
                        r.ConsumerId.Value[..Math.Min(8, r.ConsumerId.Value.Length)]),
                    r.McpServerId,
                    mcpServerDisplayName = serverNames.GetValueOrDefault(
                        r.McpServerId.Value,
                        r.McpServerId.Value[..Math.Min(8, r.McpServerId.Value.Length)]),
                    // O2: publisher node display name — mirrors the detail endpoint's
                    // AccessRequestDto.publisherNodeName (Endpoints.cs GetAccessRequestAsync
                    // projection below) so both surfaces agree on how to label the publisher.
                    // Finding 16, S3: an http_cloud server's PublisherNodeId is "" by design —
                    // null it out instead of falling through to the blank short-id fallback.
                    publisherNodeName = serverTransports.TryGetValue(r.McpServerId.Value, out var reqTransport)
                        && Korat.Domain.McpServerTransports.IsHttpCloud(reqTransport)
                        ? null
                        : nodeNames.GetValueOrDefault(
                            r.PublisherNodeId.Value,
                            r.PublisherNodeId.Value[..Math.Min(8, r.PublisherNodeId.Value.Length)]),
                    r.Status,
                    r.RequestedAt
                })
            });
        }).RequireSpaceOwner()
          .RequireRateLimiting(RateLimiterRegistration.OwnerManagementPolicy);
    }
}

/// <summary>Increment 1 (HTTP MCP direct-to-Space): owner-supplied create body. Mirrors
/// CreateOutboundInferencePointRequest/PatchOutboundInferencePointRequest.</summary>
internal sealed class CreateHttpMcpServerRequest
{
    public string DisplayName { get; set; } = string.Empty;
    public string RemoteUrl { get; set; } = string.Empty;
    public string AuthMode { get; set; } = Korat.Domain.McpServerAuthModes.None;
    public string? AuthHeaderName { get; set; }
    public string? Secret { get; set; } // plaintext, write-only
    // Increment 2 (oauth manual-client-credentials fallback): only consumed when AuthMode ==
    // "oauth" AND the authorization server has no DCR registration_endpoint. Ignored otherwise.
    public string? ClientId { get; set; }
    public string? ClientSecret { get; set; }
}

internal sealed class PatchHttpMcpServerRequest
{
    public string? RemoteUrl { get; set; }
    public string? AuthMode { get; set; }
    public string? AuthHeaderName { get; set; }
    public bool ClearAuthHeaderName { get; set; }
    public string? Secret { get; set; } // null = keep, "" = clear
    public string? ClientId { get; set; }
    public string? ClientSecret { get; set; }
}

public static class McpServerEndpoints
{
    public static void MapMcpServerEndpoints(this WebApplication app)
    {
        // Increment 1 (HTTP MCP direct-to-Space, Task 3): owner registers a new http_cloud
        // server. SSRF-validated at registration (Crux Finding 6); the plaintext secret is
        // envelope-encrypted here and never returned (only a masked secretHint).
        app.MapPost("/api/mcp-servers", async (
            CreateHttpMcpServerRequest body,
            HttpContext ctx,
            IClusterClient clusterClient,
            SpaceResolver spaceResolver,
            Korat.Domain.Persistence.IEnvelopeCrypto envelopeCrypto,
            IMetadataRepository repository,
            IAuditLog auditLog,
            Korat.Cloud.Mcp.Oauth.McpOAuthDiscoveryService discovery,
            Korat.Cloud.Mcp.Oauth.McpOAuthClientRegistrar registrar,
            Microsoft.Extensions.Options.IOptions<Korat.Cloud.Web.Auth.Options.CliOptions> cliOptions,
            CancellationToken ct) =>
        {
            var userId = (UserId)ctx.Items[KoratHttpContextItems.UserIdKey]!;
            var spaceId = await spaceResolver.ResolveDefaultSpaceIdAsync(userId, ct);
            if (spaceId is null)
                return Results.StatusCode(StatusCodes.Status403Forbidden);

            if (string.IsNullOrWhiteSpace(body.DisplayName))
                return Results.Json(new { error = "displayName must not be empty." }, statusCode: 400);
            if (!Korat.Domain.McpServerAuthModes.IsValid(body.AuthMode))
                return Results.Json(new { error = "authMode must be 'none', 'bearer', 'header', or 'oauth'." }, statusCode: 400);
            if (body.AuthMode == Korat.Domain.McpServerAuthModes.Header && string.IsNullOrWhiteSpace(body.AuthHeaderName))
                return Results.Json(new { error = "authHeaderName is required when authMode is 'header'." }, statusCode: 400);

            // Security gate BLOCKER fix: authHeaderName is SSRF-untrusted input at the same trust
            // level as remoteUrl — Task 4's HttpMcpProxyGrain injects it via
            // Headers.TryAddWithoutValidation, which bypasses .NET's header-name validation.
            // Validate it here (the sole owner-input enforcement point) whenever it is supplied,
            // REGARDLESS of authMode (a header name persisted alongside none/bearer is still
            // dead-storage today but must not be allowed to smuggle a forbidden/malformed value
            // in ahead of a later authMode switch). Shared with byo_endpoint inference points —
            // do not duplicate the regex/blocklist here.
            if (body.AuthHeaderName is not null)
            {
                var headerNameError = Korat.Domain.OutboundInferenceValidation.ValidateHeaderName(body.AuthHeaderName);
                if (headerNameError is not null)
                    return Results.Json(new { error = $"authHeaderName: {headerNameError}" }, statusCode: 400);
            }

            var ssrfError = SsrfGuard.ValidateUrl(body.RemoteUrl);
            if (ssrfError is not null)
                return Results.Json(new { error = $"remoteUrl: {ssrfError}" }, statusCode: 400);

            var spaceGrain = clusterClient.GetGrain<ISpaceGrain>(spaceId.Value.Value);
            Domain.Entities.McpServer server;
            try
            {
                server = await spaceGrain.CreateHttpMcpServerAsync(
                    body.DisplayName, body.RemoteUrl, body.AuthMode, body.AuthHeaderName, secretHint: null);
            }
            catch (KoratDomainException ex) when (ex.Code == KoratErrorCode.DuplicateServerName)
            {
                return Results.Json(new { error = "An MCP server with this name already exists." }, statusCode: 409);
            }

            object? connect = null;
            if (Korat.Domain.McpServerAuthModes.IsOAuth(body.AuthMode))
            {
                var publicOrigin = Korat.Cloud.Web.Mcp.McpOAuthConnectActionBuilder.ResolveOrigin(cliOptions.Value, ctx.Request);
                connect = await Korat.Cloud.Web.Mcp.McpOAuthConnectActionBuilder.BuildAsync(
                    server, body.ClientId, body.ClientSecret, userId.Value, publicOrigin,
                    discovery, registrar, repository, envelopeCrypto, clusterClient, ct);
            }

            string? hint = null;
            if (!string.IsNullOrEmpty(body.Secret))
            {
                var ciphertext = await envelopeCrypto.EncryptAsync(
                    server.SpaceId, Korat.Cloud.Security.Envelope.McpServerSecretCrypto.Aad(server.Id), body.Secret, ct);
                hint = body.Secret.Length >= 8 ? $"…{body.Secret[^4..]}" : "…";
                await repository.SetMcpServerSecretAsync(server.Id, ciphertext, hint, ct);
                await clusterClient.GetGrain<IMcpServerGrain>(server.Id.Value)
                    .UpdateHttpCloudConfigAsync(remoteUrl: null, authMode: null, authHeaderName: null, secretHint: hint);
            }

            await auditLog.RecordAsync(new AuditEvent(
                Action: AuditActions.McpServerCreate,
                TargetType: "mcp_server",
                TargetId: server.Id.Value,
                SpaceId: server.SpaceId.Value,
                ActorType: AuditActorTypes.User,
                ActorId: userId.Value.ToString(),
                DetailsJson: AuditDetails.Json(new { transport = server.Transport, authMode = body.AuthMode, hasSecret = hint is not null })),
                required: true, ct);

            return Results.Ok(new
            {
                id = server.Id.Value,
                displayName = server.DisplayName,
                transport = server.Transport,
                remoteUrl = server.RemoteUrl,
                authMode = server.AuthMode,
                authHeaderName = server.AuthHeaderName,
                hasSecret = hint is not null,
                secretHint = hint,
                status = server.Status.ToString(),
                createdAt = server.CreatedAt,
                connect = connect
            });
        }).RequireSpaceOwner()
          .RequireRateLimiting(RateLimiterRegistration.OwnerManagementPolicy);

        // Increment 1 (HTTP MCP direct-to-Space, Task 3): owner edits an http_cloud server's
        // non-secret config and/or rotates/clears its secret. BOLA: fetch-first via the
        // repository, same idiom as PATCH /api/inference-points.
        app.MapPatch("/api/mcp-servers/{serverId}", async (
            string serverId,
            PatchHttpMcpServerRequest body,
            HttpContext ctx,
            IClusterClient clusterClient,
            SpaceResolver spaceResolver,
            Korat.Domain.Persistence.IEnvelopeCrypto envelopeCrypto,
            IMetadataRepository repository,
            IAuditLog auditLog,
            Korat.Cloud.Mcp.Oauth.McpOAuthDiscoveryService discovery,
            Korat.Cloud.Mcp.Oauth.McpOAuthClientRegistrar registrar,
            Microsoft.Extensions.Options.IOptions<Korat.Cloud.Web.Auth.Options.CliOptions> cliOptions,
            CancellationToken ct) =>
        {
            var userId = (UserId)ctx.Items[KoratHttpContextItems.UserIdKey]!;
            var spaceId = await spaceResolver.ResolveDefaultSpaceIdAsync(userId, ct);
            if (spaceId is null)
                return Results.StatusCode(StatusCodes.Status403Forbidden);

            // BOLA: fetch-first via the repository directly, same idiom as PATCH /api/inference-points.
            var mcpServerId = new McpServerId(serverId);
            var server = await repository.GetMcpServerAsync(mcpServerId, ct);
            if (server is null || server.SpaceId != spaceId.Value)
                return Results.NotFound();
            if (!Korat.Domain.McpServerTransports.IsHttpCloud(server.Transport))
                return Results.Json(new { error = "Only http_cloud servers can be patched via this endpoint." }, statusCode: 400);

            if (body.RemoteUrl is not null)
            {
                var ssrfError = SsrfGuard.ValidateUrl(body.RemoteUrl);
                if (ssrfError is not null)
                    return Results.Json(new { error = $"remoteUrl: {ssrfError}" }, statusCode: 400);
            }
            if (body.AuthMode is not null && !Korat.Domain.McpServerAuthModes.IsValid(body.AuthMode))
                return Results.Json(new { error = "authMode must be 'none', 'bearer', 'header', or 'oauth'." }, statusCode: 400);

            // Security gate BLOCKER fix: same authHeaderName validation as POST — mirrors the
            // SsrfGuard.ValidateUrl guard above. Shared with byo_endpoint inference points; do
            // not duplicate the regex/blocklist here.
            if (body.AuthHeaderName is not null)
            {
                var headerNameError = Korat.Domain.OutboundInferenceValidation.ValidateHeaderName(body.AuthHeaderName);
                if (headerNameError is not null)
                    return Results.Json(new { error = $"authHeaderName: {headerNameError}" }, statusCode: 400);
            }

            // Security gate MEDIUM fix: POST rejects authMode="header" with no authHeaderName;
            // PATCH must enforce the same invariant on the EFFECTIVE post-patch state, else a
            // PATCH can strand a server in header mode with no header to send (Task 4 guards
            // `AuthHeaderName is not null` so this fails closed today, but the asymmetry is a
            // latent trap). Effective auth mode = the patched value, or the existing value when
            // AuthMode is omitted. Effective header name = the patched value, or the existing
            // value when AuthHeaderName is omitted AND it is not being explicitly cleared.
            var effectiveAuthMode = body.AuthMode ?? server.AuthMode;
            if (effectiveAuthMode == Korat.Domain.McpServerAuthModes.Header)
            {
                var effectiveHeaderName = body.ClearAuthHeaderName
                    ? null
                    : (body.AuthHeaderName ?? server.AuthHeaderName);
                if (string.IsNullOrWhiteSpace(effectiveHeaderName))
                    return Results.Json(new { error = "authHeaderName is required when authMode is 'header'." }, statusCode: 400);
            }

            var wasOAuth = Korat.Domain.McpServerAuthModes.IsOAuth(server.AuthMode);
            var effectiveAuthModeAfterPatch = body.AuthMode ?? server.AuthMode;
            var willBeOAuth = Korat.Domain.McpServerAuthModes.IsOAuth(effectiveAuthModeAfterPatch);
            var remoteUrlChanging = body.RemoteUrl is not null && body.RemoteUrl != server.RemoteUrl;

            string? hint = null;
            var secretChanged = false;
            // Finding 16, M4: clearSecretHint must be threaded through as its OWN flag, not
            // inferred from `hint is null` — omitting the secret field entirely (body.Secret ==
            // null: "keep") and explicitly clearing it (body.Secret == "": "clear") both leave
            // `hint` null here, so without a distinct flag UpdateHttpCloudConfigAsync could not
            // tell "keep the existing hint" from "the ciphertext was just cleared, null the hint
            // too" — the bug this finding describes: a cleared secret's hasSecret would silently
            // keep reporting true forever.
            var clearSecretHint = false;
            if (body.Secret is { Length: > 0 })
            {
                var ciphertext = await envelopeCrypto.EncryptAsync(
                    server.SpaceId, Korat.Cloud.Security.Envelope.McpServerSecretCrypto.Aad(mcpServerId), body.Secret, ct);
                hint = body.Secret.Length >= 8 ? $"…{body.Secret[^4..]}" : "…";
                await repository.SetMcpServerSecretAsync(mcpServerId, ciphertext, hint, ct);
                secretChanged = true;
            }
            else if (body.Secret is { Length: 0 })
            {
                await repository.ClearMcpServerSecretAsync(mcpServerId, ct);
                clearSecretHint = true;
                secretChanged = true;
            }

            // Increment 2 (oauth edit-path invalidation, spec §"Edit path"): a token minted with
            // resource=<old RemoteUrl> must never be dialed against a new host — and switching
            // AWAY from oauth must destroy the stored token/DCR-client ciphertext, not leave it as
            // dead storage a future switch-back-to-oauth could accidentally resurrect.
            if (wasOAuth && (remoteUrlChanging || (body.AuthMode is not null && !willBeOAuth)))
                await repository.ClearMcpServerOAuthTokenAsync(mcpServerId, ct);

            var serverGrain = clusterClient.GetGrain<IMcpServerGrain>(serverId);
            var updated = await serverGrain.UpdateHttpCloudConfigAsync(
                body.RemoteUrl, body.AuthMode, body.AuthHeaderName, hint,
                clearAuthHeaderName: body.ClearAuthHeaderName, clearSecretHint: clearSecretHint);

            object? connect = null;
            if (willBeOAuth && (!wasOAuth || remoteUrlChanging))
            {
                // Switching TO oauth, or an oauth server's RemoteUrl just changed: (re-)establish
                // consent — the row must not keep serving a token minted for a different host/mode.
                updated = await serverGrain.MarkNeedsReauthAsync();
                var publicOrigin = Korat.Cloud.Web.Mcp.McpOAuthConnectActionBuilder.ResolveOrigin(cliOptions.Value, ctx.Request);
                connect = await Korat.Cloud.Web.Mcp.McpOAuthConnectActionBuilder.BuildAsync(
                    updated, body.ClientId, body.ClientSecret, userId.Value, publicOrigin,
                    discovery, registrar, repository, envelopeCrypto, clusterClient, ct);
            }
            else if (wasOAuth && !willBeOAuth && updated.Status == McpServerStatus.NeedsReauth)
            {
                // Finding 1 (Task 4 review, SHOULD-FIX): a freshly-created oauth server starts
                // NeedsReauth. If the owner PATCHes authMode away from oauth (none/bearer/header)
                // BEFORE completing consent, UpdateHttpCloudConfigAsync above already updated
                // AuthMode but never touches Status — the server would otherwise be permanently
                // stuck NeedsReauth (both HttpMcpProxyGrain's Status!=Published dispatch gate and
                // NodeGatewayService's session-open ServerNeedsReauth gate reject it forever; only
                // a disable→enable round-trip recovered it before this fix, a re-PATCH did not).
                // EnableAsync is the correct recovery entry point, not a hand-rolled Status flip:
                // for a NON-oauth AuthMode (guaranteed here — willBeOAuth is false, and _state.AuthMode
                // was already mutated to the new value by UpdateHttpCloudConfigAsync above, same grain
                // activation), StateTransitions.EnableMcpServer's target status is unconditionally
                // Published — the hasUsableOAuthToken branch only ever applies when
                // McpServerAuthModes.IsOAuth(server.AuthMode) is true. EnableAsync mutates the
                // grain's OWN _state and returns only a bool (the idempotency flag, not the
                // record), so re-fetch via GetAsync() to reflect the new Published status in
                // `updated` (returned to the caller) instead of the stale NeedsReauth snapshot
                // already captured above.
                await serverGrain.EnableAsync(userId);
                updated = await serverGrain.GetAsync();
            }

            // Finding 16, M2: evict the proxy grain's cached RemoteUrl/AuthMode/AuthHeaderName/
            // decrypted secret (loaded ONCE at OnActivateAsync, never reloaded) so the NEXT
            // consumer frame re-activates against the freshly-updated config — otherwise a URL
            // fix, an auth-mode switch, or a leaked-secret rotation would keep being served from
            // the stale activation indefinitely. Applied to every successful PATCH, not just a
            // secret change, since a stale cached RemoteUrl is the identical staleness bug.
            await clusterClient.GetGrain<IHttpMcpProxyGrain>(serverId).EvictAsync();

            await auditLog.RecordAsync(new AuditEvent(
                Action: AuditActions.McpServerPatch,
                TargetType: "mcp_server",
                TargetId: serverId,
                SpaceId: spaceId.Value.Value,
                ActorType: AuditActorTypes.User,
                ActorId: userId.Value.ToString(),
                DetailsJson: AuditDetails.Json(new
                {
                    remoteUrlChanged = body.RemoteUrl is not null,
                    authModeChanged = body.AuthMode is not null,
                    secretChanged
                })),
                required: true, ct);

            return Results.Ok(new
            {
                id = updated.Id.Value,
                displayName = updated.DisplayName,
                transport = updated.Transport,
                remoteUrl = updated.RemoteUrl,
                authMode = updated.AuthMode,
                authHeaderName = updated.AuthHeaderName,
                hasSecret = updated.SecretHint is not null,
                secretHint = updated.SecretHint,
                status = updated.Status.ToString(),
                connect = connect
            });
        }).RequireSpaceOwner()
          .RequireRateLimiting(RateLimiterRegistration.OwnerManagementPolicy);

        // F2, F4 (Task 8): resolve identity → default SpaceId → grain.
        // GetMcpServerAsync on ISpaceGrain returns null when the server is not a member of
        // the caller's Space (cross-Space → 404).
        app.MapGet("/api/mcp-servers/{serverId}", async (
            string serverId,
            HttpContext ctx,
            IClusterClient clusterClient,
            SpaceResolver spaceResolver,
            CancellationToken ct) =>
        {
            var userId = (UserId)ctx.Items[KoratHttpContextItems.UserIdKey]!;
            var spaceId = await spaceResolver.ResolveDefaultSpaceIdAsync(userId, ct);
            if (spaceId is null)
                return Results.StatusCode(StatusCodes.Status403Forbidden);

            var grain = clusterClient.GetGrain<ISpaceGrain>(spaceId.Value.Value);
            var server = await grain.GetMcpServerAsync(new McpServerId(serverId));
            return server is null ? Results.NotFound() : Results.Ok(server);
        }).RequireSpaceOwner()
          .RequireRateLimiting(RateLimiterRegistration.OwnerManagementPolicy);

        app.MapPost("/api/mcp-servers/{serverId}/disable", async (
            string serverId,
            HttpContext ctx,
            IClusterClient clusterClient,
            SpaceResolver spaceResolver,
            IAuditLog auditLog,
            CancellationToken ct) =>
        {
            var userId = (UserId)ctx.Items[KoratHttpContextItems.UserIdKey]!;
            var spaceId = await spaceResolver.ResolveDefaultSpaceIdAsync(userId, ct);
            if (spaceId is null)
                return Results.StatusCode(StatusCodes.Status403Forbidden);

            // Verify the server belongs to the caller's Space before disabling.
            var grain = clusterClient.GetGrain<ISpaceGrain>(spaceId.Value.Value);
            var server = await grain.GetMcpServerAsync(new McpServerId(serverId));
            if (server is null)
                return Results.NotFound(); // cross-Space attempt → 404

            try
            {
                var changed = await clusterClient.GetGrain<IMcpServerGrain>(serverId)
                    .DisableAsync(userId);
                // Idempotency guard: an already-Disabled server is a no-op — skip the audit
                // write and UpdatedAt bump (McpServerGrain.DisableAsync already skipped the
                // repository write). Still 200/204 on the no-op path.
                if (changed)
                {
                    // 032 C1: privileged mutation — audited fail-closed (after grain success;
                    // an audit-write failure surfaces 500, see plan §1.4).
                    await auditLog.RecordAsync(new AuditEvent(
                        Action: AuditActions.McpServerDisable,
                        TargetType: "mcp_server",
                        TargetId: serverId,
                        SpaceId: spaceId.Value.Value,
                        ActorType: AuditActorTypes.User,
                        ActorId: userId.Value.ToString()),
                        required: true, ct);
                }
                return Results.NoContent();
            }
            catch (Exception ex)
            {
                var error = ex.ToDomainErrorResult();
                if (error is not null)
                    return error;
                throw;
            }
        }).RequireSpaceOwner()
          .RequireRateLimiting(RateLimiterRegistration.OwnerManagementPolicy);

        // Symmetric re-enable — mirrors the inference-point enable pattern (see
        // InferenceManagementEndpoints "── Enable a point ──"). Same auth/validation/persistence
        // path as /disable above: BOLA check via GetMcpServerAsync, then grain mutation + audit.
        app.MapPost("/api/mcp-servers/{serverId}/enable", async (
            string serverId,
            HttpContext ctx,
            IClusterClient clusterClient,
            SpaceResolver spaceResolver,
            IAuditLog auditLog,
            CancellationToken ct) =>
        {
            var userId = (UserId)ctx.Items[KoratHttpContextItems.UserIdKey]!;
            var spaceId = await spaceResolver.ResolveDefaultSpaceIdAsync(userId, ct);
            if (spaceId is null)
                return Results.StatusCode(StatusCodes.Status403Forbidden);

            // Verify the server belongs to the caller's Space before enabling.
            var grain = clusterClient.GetGrain<ISpaceGrain>(spaceId.Value.Value);
            var server = await grain.GetMcpServerAsync(new McpServerId(serverId));
            if (server is null)
                return Results.NotFound(); // cross-Space attempt → 404

            try
            {
                var changed = await clusterClient.GetGrain<IMcpServerGrain>(serverId)
                    .EnableAsync(userId);
                // Idempotency guard: an already-Published server is a no-op — skip the audit
                // write and UpdatedAt bump (McpServerGrain.EnableAsync already skipped the
                // repository write). Still 200/204 on the no-op path.
                if (changed)
                {
                    // 032 C1: privileged mutation — audited fail-closed (after grain success;
                    // an audit-write failure surfaces 500, see plan §1.4).
                    await auditLog.RecordAsync(new AuditEvent(
                        Action: AuditActions.McpServerEnable,
                        TargetType: "mcp_server",
                        TargetId: serverId,
                        SpaceId: spaceId.Value.Value,
                        ActorType: AuditActorTypes.User,
                        ActorId: userId.Value.ToString()),
                        required: true, ct);
                }
                return Results.NoContent();
            }
            catch (Exception ex)
            {
                var error = ex.ToDomainErrorResult();
                if (error is not null)
                    return error;
                throw;
            }
        }).RequireSpaceOwner()
          .RequireRateLimiting(RateLimiterRegistration.OwnerManagementPolicy);

        // 021 (Layer 3): owner-initiated hard delete. Lets the owner purge an orphan even when
        // the daemon will never send UnpublishMcpServer (e.g. node permanently offline).
        // Distinct from POST /disable which keeps the row as Disabled — this removes it entirely.
        app.MapDelete("/api/mcp-servers/{serverId}", async (
            string serverId,
            HttpContext ctx,
            IClusterClient clusterClient,
            SpaceResolver spaceResolver,
            Korat.Cloud.Gateways.SessionTerminator terminator,
            IAuditLog auditLog,
            CancellationToken ct) =>
        {
            var userId = (UserId)ctx.Items[KoratHttpContextItems.UserIdKey]!;
            var spaceId = await spaceResolver.ResolveDefaultSpaceIdAsync(userId, ct);
            if (spaceId is null)
                return Results.StatusCode(StatusCodes.Status403Forbidden);

            var grain = clusterClient.GetGrain<ISpaceGrain>(spaceId.Value.Value);
            try
            {
                var result = await grain.DeleteMcpServerAsync(new McpServerId(serverId), userId);
                if (!result.Deleted)
                    return Results.NotFound(); // not in Space → 404
                foreach (var sessionId in result.AffectedSessionIds)
                    await terminator.TerminateSessionAsync(sessionId, SessionCloseReason.ServerUnavailable, ct);
                // 032 C1: audited fail-closed; the terminated-session count doubles as the
                // session.force_close record for this teardown (plan §1.3).
                await auditLog.RecordAsync(new AuditEvent(
                    Action: AuditActions.McpServerDelete,
                    TargetType: "mcp_server",
                    TargetId: serverId,
                    SpaceId: spaceId.Value.Value,
                    ActorType: AuditActorTypes.User,
                    ActorId: userId.Value.ToString(),
                    DetailsJson: AuditDetails.Json(new { terminatedSessions = result.AffectedSessionIds.Count })),
                    required: true, ct);
                return Results.NoContent();
            }
            catch (Exception ex)
            {
                var error = ex.ToDomainErrorResult();
                if (error is not null)
                    return error;
                throw;
            }
        }).RequireSpaceOwner()
          .RequireRateLimiting(RateLimiterRegistration.OwnerManagementPolicy);
    }
}

/// <summary>#165 (`korat nodes prune`): request body for POST /api/nodes/prune.</summary>
/// <summary>Node-visibility-doctor design (2026-07-02): owner-editable Note on a node.</summary>
public static class NodeEndpoints
{
    public static void MapNodeEndpoints(this WebApplication app)
    {
        // Owner-scoped, mirrors the McpServerEndpoints BOLA pattern above: resolve identity →
        // default SpaceId → grain, then let the grain's cached membership check decide 404 vs 200
        // (foreign/unknown node → same 404, no existence oracle).
        app.MapPatch("/api/nodes/{nodeId}", async (
            string nodeId,
            HttpRequest httpRequest,
            HttpContext ctx,
            IClusterClient clusterClient,
            SpaceResolver spaceResolver,
            IAuditLog auditLog,
            CancellationToken ct) =>
        {
            var userId = (UserId)ctx.Items[KoratHttpContextItems.UserIdKey]!;
            var spaceId = await spaceResolver.ResolveDefaultSpaceIdAsync(userId, ct);
            if (spaceId is null)
                return Results.StatusCode(StatusCodes.Status403Forbidden);

            // Final-review fix: model-binding straight into PatchNodeRequest(string? Note)
            // collapses an ABSENT "note" property and an explicit {"note":null} to the same C#
            // null, so `PATCH {}` silently cleared the note. Parse the raw body instead so
            // "absent" (400 — usage error) is distinguishable from "explicit null" (clears).
            JsonDocument body;
            try
            {
                body = await JsonDocument.ParseAsync(httpRequest.Body, cancellationToken: ct);
            }
            catch (JsonException)
            {
                return Results.Json(new { error = "invalid JSON body." }, statusCode: 400);
            }

            using (body)
            {
                if (body.RootElement.ValueKind != JsonValueKind.Object)
                    return Results.Json(new { error = "invalid JSON body." }, statusCode: 400);

                // Case-insensitive on purpose: the CLI's source-generated JSON context has no
                // naming policy and sends PascalCase ("Note"); a hand-rolled/camelCase client
                // sends "note". Accept either.
                if (!body.RootElement.TryGetProperty("note", out var noteProp) &&
                    !body.RootElement.TryGetProperty("Note", out noteProp))
                {
                    return Results.Json(new { error = "note property required." }, statusCode: 400);
                }

                string? note;
                switch (noteProp.ValueKind)
                {
                    case JsonValueKind.Null:
                        note = null;
                        break;
                    case JsonValueKind.String:
                        note = noteProp.GetString();
                        break;
                    default:
                        return Results.Json(new { error = "note: must be a string or null." }, statusCode: 400);
                }

                if (note is not null && note.Length > 500)
                    return Results.Json(new { error = "note: must be 500 characters or fewer." }, statusCode: 400);

                var grain = clusterClient.GetGrain<ISpaceGrain>(spaceId.Value.Value);
                var updated = await grain.SetNodeNoteAsync(new NodeId(nodeId), note);
                if (updated is null)
                    return Results.NotFound(); // not in this Space → 404 (BOLA-safe)

                // 032 C1: privileged mutation — audited fail-closed (mirrors McpServerDisable/Enable).
                await auditLog.RecordAsync(new AuditEvent(
                    Action: AuditActions.NodeNoteSet,
                    TargetType: "node",
                    TargetId: nodeId,
                    SpaceId: spaceId.Value.Value,
                    ActorType: AuditActorTypes.User,
                    ActorId: userId.Value.ToString(),
                    DetailsJson: AuditDetails.Json(new { cleared = updated.Note is null })),
                    required: true, ct);

                return Results.Ok(new
                {
                    id = updated.Id,
                    displayName = updated.DisplayName,
                    note = updated.Note,
                    updatedAt = updated.UpdatedAt
                });
            }
        }).RequireSpaceOwner()
          .RequireRateLimiting(RateLimiterRegistration.OwnerManagementPolicy);

        // #165 (`korat nodes prune`): owner-scoped bulk GC of stale agent-kind nodes — the
        // one-shot `korat connect --agent` identities that pile up over time (--agent codex-smoke,
        // wake-test, ...). v1 restricts kind to "agent" — Publisher nodes host MCP servers and
        // stay explicit-delete-only (DELETE /api/mcp-servers/{id} / `korat mcp remove`).
        app.MapPost("/api/nodes/prune", async (
            PruneNodesRequest body,
            HttpContext ctx,
            IClusterClient clusterClient,
            SpaceResolver spaceResolver,
            Korat.Cloud.Gateways.SessionTerminator terminator,
            IAuditLog auditLog,
            CancellationToken ct) =>
        {
            var userId = (UserId)ctx.Items[KoratHttpContextItems.UserIdKey]!;
            var spaceId = await spaceResolver.ResolveDefaultSpaceIdAsync(userId, ct);
            if (spaceId is null)
                return Results.StatusCode(StatusCodes.Status403Forbidden);

            if (!string.Equals(body.Kind, "agent", StringComparison.OrdinalIgnoreCase))
                return Results.Json(
                    new { error = "kind: only 'agent' is prunable in v1 — publisher nodes are never bulk-deleted." },
                    statusCode: 400);

            var olderThanDays = body.OlderThanDays ?? 30;
            if (olderThanDays < 1)
                return Results.Json(new { error = "olderThanDays must be at least 1." }, statusCode: 400);

            var cutoff = DateTimeOffset.UtcNow.AddDays(-olderThanDays);

            var grain = clusterClient.GetGrain<ISpaceGrain>(spaceId.Value.Value);
            try
            {
                var result = await grain.PruneAgentNodesAsync(userId, cutoff);

                // Grant revocation (inside PruneAgentNodesAsync) may have left live sessions
                // pointing at a revoked grant — tear them down, same as GrantRevoke/McpServerDelete.
                foreach (var sessionId in result.AffectedSessionIds)
                    await terminator.TerminateSessionAsync(sessionId, SessionCloseReason.Revoked, ct);

                // 032 C1: audited fail-closed, even on a zero-match prune (mirrors McpServerDelete —
                // "nothing matched" is still an owner-initiated action worth a durable record).
                await auditLog.RecordAsync(new AuditEvent(
                    Action: AuditActions.NodesPrune,
                    TargetType: "node",
                    TargetId: spaceId.Value.Value,
                    SpaceId: spaceId.Value.Value,
                    ActorType: AuditActorTypes.User,
                    ActorId: userId.Value.ToString(),
                    DetailsJson: AuditDetails.Json(new
                    {
                        kind = body.Kind,
                        olderThanDays,
                        prunedCount = result.PrunedNames.Count,
                        terminatedSessions = result.AffectedSessionIds.Count
                    })),
                    required: true, ct);

                return Results.Ok(new
                {
                    prunedCount = result.PrunedNames.Count,
                    prunedNames = result.PrunedNames
                });
            }
            catch (Exception ex)
            {
                var error = ex.ToDomainErrorResult();
                if (error is not null)
                    return error;
                throw;
            }
        }).RequireSpaceOwner()
          .RequireRateLimiting(RateLimiterRegistration.OwnerManagementPolicy);
    }
}

public static class AccessRequestEndpoints
{
    public static void MapAccessRequestEndpoints(this WebApplication app)
    {
        // F1 + F2 + F4 (Task 7): resolve identity → default SpaceId → grain for all
        // access-request reads and writes. All reads go through grain in-memory state.
        // Cross-Space access returns 404 — no existence oracle (design §5).

        app.MapGet("/api/access-requests", async (
            HttpContext ctx,
            IClusterClient clusterClient,
            SpaceResolver spaceResolver,
            CancellationToken ct) =>
        {
            var userId = (UserId)ctx.Items[KoratHttpContextItems.UserIdKey]!;
            var spaceId = await spaceResolver.ResolveDefaultSpaceIdAsync(userId, ct);
            if (spaceId is null)
                return Results.StatusCode(StatusCodes.Status403Forbidden);

            var grain = clusterClient.GetGrain<ISpaceGrain>(spaceId.Value.Value);
            // 028: fetch requests + lookup data in parallel — mirrors FetchGrantLookupDataAsync.
            var requestsTask = grain.ListAccessRequestsAsync();
            var serversTask  = grain.ListMcpServersAsync();
            var nodesTask    = grain.ListNodesAsync();
            await Task.WhenAll(requestsTask, serversTask, nodesTask).WaitAsync(ct);

            var pendingRequests = requestsTask.Result.Where(r => r.Status == AccessRequestStatus.Pending).ToList();
            var listServerNames = serversTask.Result.ToDictionary(s => s.Id.Value, s => s.DisplayName);
            // Finding 16, S3: same http_cloud detection as SpaceOverviewEndpoints.pendingAccessRequests
            // above — built from the already-fetched `serversTask` list, no extra grain call.
            var listServerTransports = serversTask.Result.ToDictionary(s => s.Id.Value, s => s.Transport);
            // Р27: the definition history of each server, so the approve screen can show WHAT
            // changed rather than merely that something did. An owner who cannot see that
            // `npx @modelcontextprotocol/server-filesystem ~/docs` became `bash -c …` will approve
            // reflexively, and Р26's protection is then bypassed through the human.
            var listServerDefinitions = serversTask.Result.ToDictionary(s => s.Id.Value, s => s);
            var listNodeNames   = nodesTask.Result.ToDictionary(n => n.Id.Value, n => n.DisplayName);
            var listAgentNames  = await FriendlyNameHelpers.ResolveAgentNamesAsync(
                pendingRequests.Select(r => r.ConsumerId.Value).Distinct(),
                clusterClient,
                listNodeNames,
                ct);

            return Results.Ok(pendingRequests.Select(r => new
            {
                r.Id,
                r.ConsumerId,
                consumerDisplayName = listAgentNames.GetValueOrDefault(
                    r.ConsumerId.Value,
                    r.ConsumerId.Value[..Math.Min(8, r.ConsumerId.Value.Length)]),
                r.McpServerId,
                mcpServerDisplayName = listServerNames.GetValueOrDefault(
                    r.McpServerId.Value,
                    r.McpServerId.Value[..Math.Min(8, r.McpServerId.Value.Length)]),
                // [info] parity fix: /api/space's pendingAccessRequests includes
                // publisherNodeName (see MapSpaceOverviewEndpoints above) — this standalone
                // list was missing it. Same resolution + short-id fallback so both
                // access-request surfaces agree on how to label the publisher.
                // Finding 16, S3: same fix as SpaceOverviewEndpoints.pendingAccessRequests above.
                publisherNodeName = listServerTransports.TryGetValue(r.McpServerId.Value, out var listReqTransport)
                    && Korat.Domain.McpServerTransports.IsHttpCloud(listReqTransport)
                    ? null
                    : listNodeNames.GetValueOrDefault(
                        r.PublisherNodeId.Value,
                        r.PublisherNodeId.Value[..Math.Min(8, r.PublisherNodeId.Value.Length)]),
                r.Status,
                r.RequestedAt,
                // Р27: null on the ordinary path (a first-time request for a server that never
                // changed). Non-null means the owner is being asked about a server whose launch
                // definition moved after a previous approval — the case that must never be a
                // one-click yes.
                definitionChange = listServerDefinitions.TryGetValue(r.McpServerId.Value, out var listReqServer)
                    && listReqServer.DefinitionChangedAt is not null
                    ? new
                    {
                        changedAt = listReqServer.DefinitionChangedAt,
                        previousCommand = listReqServer.PreviousLaunchCommand,
                        previousArguments = listReqServer.PreviousLaunchArguments,
                        currentCommand = listReqServer.LaunchCommand,
                        currentArguments = listReqServer.LaunchArguments,
                    }
                    : null
            }));
        }).RequireSpaceOwner()
          .RequireRateLimiting(RateLimiterRegistration.OwnerManagementPolicy);

        app.MapGet("/api/access-requests/{requestId}", async (
            string requestId,
            HttpContext ctx,
            IClusterClient clusterClient,
            SpaceResolver spaceResolver,
            CancellationToken ct) =>
        {
            var userId = (UserId)ctx.Items[KoratHttpContextItems.UserIdKey]!;
            var spaceId = await spaceResolver.ResolveDefaultSpaceIdAsync(userId, ct);
            if (spaceId is null)
                return Results.StatusCode(StatusCodes.Status403Forbidden);

            var grain = clusterClient.GetGrain<ISpaceGrain>(spaceId.Value.Value);
            var requests = await grain.ListAccessRequestsAsync();
            var request = requests.FirstOrDefault(r => r.Id.Value == requestId);
            if (request is null)
                return Results.NotFound(); // resource absent from caller's Space → 404

            // Resolve display names from the grain's in-memory state (G4: no repo injection).
            var servers = await grain.ListMcpServersAsync();
            var nodes = await grain.ListNodesAsync();
            var server = servers.FirstOrDefault(s => s.Id == request.McpServerId);
            var agentNode = nodes.FirstOrDefault(n => n.Id == request.RequestedByNodeId);
            var publisherNode = nodes.FirstOrDefault(n => n.Id == request.PublisherNodeId);
            var nodeNames = nodes.ToDictionary(n => n.Id.Value, n => n.DisplayName);
            var agentNames = await FriendlyNameHelpers.ResolveAgentNamesAsync(
                [request.ConsumerId.Value],
                clusterClient,
                nodeNames,
                ct);
            var agentDisplayName = agentNames.GetValueOrDefault(
                request.ConsumerId.Value,
                agentNode?.DisplayName ?? request.ConsumerId.Value[..Math.Min(8, request.ConsumerId.Value.Length)]);

            return Results.Ok(new
            {
                id = request.Id.Value,
                status = request.Status.ToString(),
                agentClientId = request.ConsumerId.Value,
                agentNodeId = request.RequestedByNodeId.Value,
                agentNodeName = agentDisplayName,
                mcpServerId = request.McpServerId.Value,
                mcpServerName = server?.DisplayName ?? request.McpServerId.Value,
                // Finding 16, S3: null out for http_cloud instead of surfacing the empty
                // PublisherNodeId.Value verbatim (no Math.Min slice here previously — same bug,
                // simpler pre-fix expression).
                publisherNodeId = server is not null && Korat.Domain.McpServerTransports.IsHttpCloud(server.Transport)
                    ? null : request.PublisherNodeId.Value,
                publisherNodeName = server is not null && Korat.Domain.McpServerTransports.IsHttpCloud(server.Transport)
                    ? null : (publisherNode?.DisplayName ?? request.PublisherNodeId.Value),
                requestedAt = request.RequestedAt
            });
        }).RequireSpaceOwner()
          .RequireRateLimiting(RateLimiterRegistration.OwnerManagementPolicy);

        app.MapPost("/api/access-requests/{requestId}/approve", async (
            string requestId,
            HttpContext ctx,
            IClusterClient clusterClient,
            SpaceResolver spaceResolver,
            IAuditLog auditLog,
            CancellationToken ct) =>
        {
            var userId = (UserId)ctx.Items[KoratHttpContextItems.UserIdKey]!;
            var spaceId = await spaceResolver.ResolveDefaultSpaceIdAsync(userId, ct);
            if (spaceId is null)
                return Results.StatusCode(StatusCodes.Status403Forbidden);

            var grain = clusterClient.GetGrain<ISpaceGrain>(spaceId.Value.Value);
            // Verify the request belongs to the caller's Space before approving.
            var requests = await grain.ListAccessRequestsAsync();
            if (!requests.Any(r => r.Id.Value == requestId))
                return Results.NotFound(); // cross-Space attempt → 404

            try
            {
                // Pass the resolved userId so the audit trail records a real identity.
                var grant = await grain.ApproveAccessRequestAsync(
                    new AccessRequestId(requestId),
                    userId);
                // 032 C1: privileged mutation — audited fail-closed (after grain success).
                await auditLog.RecordAsync(new AuditEvent(
                    Action: AuditActions.AccessRequestApprove,
                    TargetType: "access_request",
                    TargetId: requestId,
                    SpaceId: spaceId.Value.Value,
                    ActorType: AuditActorTypes.User,
                    ActorId: userId.Value.ToString(),
                    DetailsJson: AuditDetails.Json(new { grantId = grant.Id.Value })),
                    required: true, ct);
                // Return a slim DTO — full Grant graph hits Orleans/JSON serialization edges in tests.
                return Results.Ok(new { id = grant.Id.Value, status = grant.Status.ToString() });
            }
            catch (Exception ex)
            {
                var error = ex.ToDomainErrorResult();
                if (error is not null)
                    return error;
                throw;
            }
        }).RequireSpaceOwner()
          .RequireRateLimiting(RateLimiterRegistration.OwnerManagementPolicy);

        app.MapPost("/api/access-requests/{requestId}/deny", async (
            string requestId,
            HttpContext ctx,
            IClusterClient clusterClient,
            SpaceResolver spaceResolver,
            IAuditLog auditLog,
            CancellationToken ct) =>
        {
            var userId = (UserId)ctx.Items[KoratHttpContextItems.UserIdKey]!;
            var spaceId = await spaceResolver.ResolveDefaultSpaceIdAsync(userId, ct);
            if (spaceId is null)
                return Results.StatusCode(StatusCodes.Status403Forbidden);

            var grain = clusterClient.GetGrain<ISpaceGrain>(spaceId.Value.Value);
            // Verify the request belongs to the caller's Space before denying.
            var requests = await grain.ListAccessRequestsAsync();
            if (!requests.Any(r => r.Id.Value == requestId))
                return Results.NotFound(); // cross-Space attempt → 404

            try
            {
                await grain.DenyAccessRequestAsync(
                    new AccessRequestId(requestId),
                    userId);
                // 032 C1: privileged mutation — audited fail-closed (after grain success).
                await auditLog.RecordAsync(new AuditEvent(
                    Action: AuditActions.AccessRequestDeny,
                    TargetType: "access_request",
                    TargetId: requestId,
                    SpaceId: spaceId.Value.Value,
                    ActorType: AuditActorTypes.User,
                    ActorId: userId.Value.ToString()),
                    required: true, ct);
                return Results.NoContent();
            }
            catch (Exception ex)
            {
                var error = ex.ToDomainErrorResult();
                if (error is not null)
                    return error;
                throw;
            }
        }).RequireSpaceOwner()
          .RequireRateLimiting(RateLimiterRegistration.OwnerManagementPolicy);
    }
}

public static class GrantEndpoints
{
    public static void MapGrantEndpoints(this WebApplication app)
    {
        // F1 + F2 + F4 (Task 7): resolve identity → default SpaceId → grain.
        // Cross-Space grant access returns 404 — no existence oracle (design §5).

        app.MapGet("/api/grants", async (
            HttpContext ctx,
            IClusterClient clusterClient,
            SpaceResolver spaceResolver,
            CancellationToken ct) =>
        {
            var userId = (UserId)ctx.Items[KoratHttpContextItems.UserIdKey]!;
            var spaceId = await spaceResolver.ResolveDefaultSpaceIdAsync(userId, ct);
            if (spaceId is null)
                return Results.StatusCode(StatusCodes.Status403Forbidden);

            var grain = clusterClient.GetGrain<ISpaceGrain>(spaceId.Value.Value);

            // 020-B: fetch grants + lookup data in parallel; build maps once per request.
            var (grants, servers, nodes) = await FriendlyNameHelpers.FetchGrantLookupDataAsync(grain, ct);

            // serverName map: mcpServerId → DisplayName
            var serverNames = servers.ToDictionary(s => s.Id.Value, s => s.DisplayName);
            // nodeId → DisplayName (covers both publisher nodes and pre-017 agentClientId==nodeId grants)
            var nodeNames = nodes.ToDictionary(n => n.Id.Value, n => n.DisplayName);

            // agentClientId → agentName: resolve each unique agentClientId once.
            var agentNames = await FriendlyNameHelpers.ResolveAgentNamesAsync(
                grants.Select(g => g.ConsumerId.Value).Distinct(),
                clusterClient,
                nodeNames,
                ct);

            return Results.Ok(grants.Select(g => new
            {
                id = g.Id.Value,
                status = g.Status.ToString(),
                agentClientId = g.ConsumerId.Value,
                agentName = agentNames.GetValueOrDefault(g.ConsumerId.Value, g.ConsumerId.Value[..Math.Min(8, g.ConsumerId.Value.Length)]),
                mcpServerId = g.McpServerId.Value,
                serverName = serverNames.GetValueOrDefault(g.McpServerId.Value, g.McpServerId.Value[..Math.Min(8, g.McpServerId.Value.Length)]),
                approvedAt = g.ApprovedAt,
                revokedAt = g.RevokedAt
            }));
        }).RequireSpaceOwner()
          .RequireRateLimiting(RateLimiterRegistration.OwnerManagementPolicy);

        app.MapPost("/api/grants/{grantId}/revoke", async (
            string grantId,
            HttpContext ctx,
            IClusterClient clusterClient,
            SpaceResolver spaceResolver,
            Korat.Cloud.Gateways.SessionTerminator terminator,
            IAuditLog auditLog,
            CancellationToken ct) =>
        {
            var userId = (UserId)ctx.Items[KoratHttpContextItems.UserIdKey]!;
            var spaceId = await spaceResolver.ResolveDefaultSpaceIdAsync(userId, ct);
            if (spaceId is null)
                return Results.StatusCode(StatusCodes.Status403Forbidden);

            var grain = clusterClient.GetGrain<ISpaceGrain>(spaceId.Value.Value);
            // Verify the grant belongs to the caller's Space before revoking.
            var grants = await grain.ListGrantsAsync();
            if (!grants.Any(g => g.Id.Value == grantId))
                return Results.NotFound(); // cross-Space attempt → 404

            try
            {
                // 022/Step-A: flip the grant and terminate every live session it backed.
                var affected = await grain.RevokeGrantAsync(new GrantId(grantId), userId);
                foreach (var sessionId in affected)
                    await terminator.TerminateSessionAsync(sessionId, SessionCloseReason.Revoked, ct);
                // 032 C1: audited fail-closed; the terminated-session count doubles as the
                // session.force_close record for this teardown (plan §1.3).
                await auditLog.RecordAsync(new AuditEvent(
                    Action: AuditActions.GrantRevoke,
                    TargetType: "grant",
                    TargetId: grantId,
                    SpaceId: spaceId.Value.Value,
                    ActorType: AuditActorTypes.User,
                    ActorId: userId.Value.ToString(),
                    DetailsJson: AuditDetails.Json(new { terminatedSessions = affected.Count })),
                    required: true, ct);
                return Results.NoContent();
            }
            catch (Exception ex)
            {
                var error = ex.ToDomainErrorResult();
                if (error is not null)
                    return error;
                throw;
            }
        }).RequireSpaceOwner()
          .RequireRateLimiting(RateLimiterRegistration.OwnerManagementPolicy);
    }
}

public static class SessionEndpoints
{
    public static void MapSessionEndpoints(this WebApplication app)
    {
        // F2, F4 (Task 8): resolve identity → default SpaceId → grain.
        // Sessions are read through ISpaceGrain.ListSessionsAsync which is scoped to this
        // grain's SpaceId — the grain key IS the isolation boundary.
        app.MapGet("/api/sessions", async (
            HttpContext ctx,
            IClusterClient clusterClient,
            SpaceResolver spaceResolver,
            CancellationToken ct) =>
        {
            var userId = (UserId)ctx.Items[KoratHttpContextItems.UserIdKey]!;
            var spaceId = await spaceResolver.ResolveDefaultSpaceIdAsync(userId, ct);
            if (spaceId is null)
                return Results.StatusCode(StatusCodes.Status403Forbidden);

            var grain = clusterClient.GetGrain<ISpaceGrain>(spaceId.Value.Value);

            // 020-B: fetch sessions + lookup data in parallel; build maps once per request.
            var (sessions, servers, nodes) = await FriendlyNameHelpers.FetchSessionLookupDataAsync(grain, ct);

            // serverName map: mcpServerId → DisplayName
            var serverNames = servers.ToDictionary(s => s.Id.Value, s => s.DisplayName);
            // nodeId → DisplayName (covers pre-017 agentClientId==nodeId sessions)
            var nodeNames = nodes.ToDictionary(n => n.Id.Value, n => n.DisplayName);
            // 025: nodeId → Node for liveness derivation (same grain data 019/021 use).
            var nodeById = nodes.ToDictionary(n => n.Id.Value);
            // Finding 16, S3: keyed lookup so each session row can tell whether its server is
            // http_cloud (PublisherNodeId always "" for those, by design — not a real gap).
            var serverById = servers.ToDictionary(s => s.Id.Value);

            // Effective session status is derived, never written back. Only participants backed
            // by real relay nodes require presence: http_cloud has no publisher node, and
            // Space-MCP uses an in-process consumer represented by cagg-sentinel rather than a
            // Nodes row. SessionPresenceRules keeps those transport details out of the UI layer.
            string EffectiveStatus(Domain.Entities.RelaySession s)
            {
                serverById.TryGetValue(s.McpServerId.Value, out var server);
                if (s.Status is SessionStatus.Active or SessionStatus.Opening &&
                    !SessionPresenceRules.RequiredParticipantsAreOnline(
                        s,
                        server,
                        nodeById.GetValueOrDefault(s.ClientNodeId.Value),
                        nodeById.GetValueOrDefault(s.PublisherNodeId.Value)))
                    return "Stale";
                return s.Status.ToString();
            }

            // agentClientId → agentName: resolve each unique agentClientId once.
            var agentNames = await FriendlyNameHelpers.ResolveAgentNamesAsync(
                sessions.Select(s => s.ConsumerId.Value).Distinct(),
                clusterClient,
                nodeNames,
                ct);

            return Results.Ok(sessions.Select(s =>
            {
                var isHttpCloudSession = serverById.TryGetValue(s.McpServerId.Value, out var srv)
                    && Korat.Domain.McpServerTransports.IsHttpCloud(srv.Transport);
                return new
                {
                    id = s.Id,
                    status = s.Status,
                    effectiveStatus = EffectiveStatus(s),
                    agentClientId = s.ConsumerId.Value,
                    agentName = agentNames.GetValueOrDefault(s.ConsumerId.Value, s.ConsumerId.Value[..Math.Min(8, s.ConsumerId.Value.Length)]),
                    mcpServerId = s.McpServerId.Value,
                    serverName = serverNames.GetValueOrDefault(s.McpServerId.Value, s.McpServerId.Value[..Math.Min(8, s.McpServerId.Value.Length)]),
                    // Agent-DX (Grants+Sessions parity): expose the publisher node so the SPA can
                    // render the -3 "agent · server · node" breadcrumb and cross-nav to /servers?node=.
                    // Finding 16, S3: null out for http_cloud instead of the blank short-id fallback.
                    publisherNodeId = isHttpCloudSession ? null : (string?)s.PublisherNodeId.Value,
                    publisherNodeName = isHttpCloudSession ? null : nodeNames.GetValueOrDefault(s.PublisherNodeId.Value, s.PublisherNodeId.Value[..Math.Min(8, s.PublisherNodeId.Value.Length)]),
                    startedAt = s.StartedAt,
                    endedAt = s.EndedAt,
                    bytesClientToServer = s.BytesClientToServer,
                    bytesServerToClient = s.BytesServerToClient,
                    closeReason = s.CloseReason
                };
            }));
        }).RequireSpaceOwner()
          .RequireRateLimiting(RateLimiterRegistration.OwnerManagementPolicy);
    }
}

/// <summary>
/// 020-B: shared name-resolution helpers for Grants and Sessions endpoints.
/// All lookup data is fetched once per request via parallel grain calls; names are resolved
/// from in-memory maps so there is at most one IConsumerGrain.GetAsync call per unique
/// agentClientId (not one per row).
/// </summary>
internal static class FriendlyNameHelpers
{
    /// <summary>
    /// Fetches grants, MCP servers, and nodes in parallel for the grants endpoint.
    /// </summary>
    public static async Task<(
        IReadOnlyList<Domain.Entities.Grant> Grants,
        IReadOnlyList<Domain.Entities.McpServer> Servers,
        IReadOnlyList<Domain.Entities.Node> Nodes)>
        FetchGrantLookupDataAsync(ISpaceGrain grain, CancellationToken ct)
    {
        var grantsTask  = grain.ListGrantsAsync();
        var serversTask = grain.ListMcpServersAsync();
        var nodesTask   = grain.ListNodesAsync();
        await Task.WhenAll(grantsTask, serversTask, nodesTask).WaitAsync(ct);
        return (grantsTask.Result, serversTask.Result, nodesTask.Result);
    }

    /// <summary>
    /// Fetches sessions, MCP servers, and nodes in parallel for the sessions endpoint.
    /// </summary>
    public static async Task<(
        IReadOnlyList<Domain.Entities.RelaySession> Sessions,
        IReadOnlyList<Domain.Entities.McpServer> Servers,
        IReadOnlyList<Domain.Entities.Node> Nodes)>
        FetchSessionLookupDataAsync(ISpaceGrain grain, CancellationToken ct)
    {
        var sessionsTask = grain.ListSessionsAsync(includeClosed: true);
        var serversTask  = grain.ListMcpServersAsync();
        var nodesTask    = grain.ListNodesAsync();
        await Task.WhenAll(sessionsTask, serversTask, nodesTask).WaitAsync(ct);
        return (sessionsTask.Result, serversTask.Result, nodesTask.Result);
    }

    /// <summary>
    /// Resolves a set of agentClientIds to friendly names using a two-step lookup:
    ///   1. If the agentClientId matches a NodeId in <paramref name="nodeNames"/> (pre-017 grants
    ///      where agentClientId == publisher NodeId), return that node's DisplayName directly.
    ///   2. Otherwise call IConsumerGrain.GetAsync() to obtain the Consumer's NodeId,
    ///      then look up the node's DisplayName from the map. If no real node exists (for
    ///      example Space-MCP's cagg-sentinel), use Consumer.DisplayName instead.
    /// Falls back to the first 8 chars of the id when resolution fails.
    /// At most one grain call per distinct agentClientId — never N calls per row.
    /// </summary>
    public static async Task<Dictionary<string, string>> ResolveAgentNamesAsync(
        IEnumerable<string> agentClientIds,
        IClusterClient clusterClient,
        Dictionary<string, string> nodeNames,
        CancellationToken ct)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);

        // Fan-out grain calls in parallel for all unique ids not already resolved via nodeNames.
        var grainTasks = new List<(string ConsumerId, Task<Domain.Entities.Consumer> Task)>();

        foreach (var id in agentClientIds)
        {
            // Skip null/empty ids — a Dictionary keyed lookup throws on a null key, and there's
            // no name to resolve anyway (caller falls back to the raw/short id).
            if (string.IsNullOrEmpty(id) || result.ContainsKey(id))
                continue;

            // Pre-017: agentClientId is actually a NodeId — resolve directly from the node map.
            if (nodeNames.TryGetValue(id, out var directName))
            {
                result[id] = directName;
                continue;
            }

            // Post-017: need to look up Consumer → NodeId → DisplayName.
            var grain = clusterClient.GetGrain<IConsumerGrain>(id);
            grainTasks.Add((id, grain.GetAsync()));
        }

        if (grainTasks.Count > 0)
        {
            try
            {
                await Task.WhenAll(grainTasks.Select(t => t.Task)).WaitAsync(ct);
            }
            catch
            {
                // Individual failures are handled below per-task.
            }

            foreach (var (id, task) in grainTasks)
            {
                if (task.IsCompletedSuccessfully)
                {
                    var agentClient = task.Result;
                    var nodeId = agentClient.NodeId.Value;
                    if (!string.IsNullOrEmpty(nodeId) && nodeNames.TryGetValue(nodeId, out var name))
                    {
                        result[id] = name;
                    }
                    else if (!string.IsNullOrWhiteSpace(agentClient.DisplayName)
                        && !LooksLikeGeneratedConsumerName(agentClient.DisplayName, id))
                    {
                        result[id] = agentClient.DisplayName;
                    }
                    else if (id.StartsWith("cagg_", StringComparison.Ordinal))
                    {
                        result[id] = "Connected MCP client";
                    }
                    else
                    {
                        result[id] = id[..Math.Min(8, id.Length)];
                    }
                }
                else
                {
                    // Grain not found or faulted — fall back to short id.
                    result[id] = id[..Math.Min(8, id.Length)];
                }
            }
        }

        return result;
    }

    private static bool LooksLikeGeneratedConsumerName(string displayName, string id)
    {
        var shortId = id[..Math.Min(8, id.Length)];
        return string.Equals(displayName, $"agent-{shortId}", StringComparison.OrdinalIgnoreCase)
            || string.Equals(displayName, $"consumer-{shortId}", StringComparison.OrdinalIgnoreCase);
    }
}
