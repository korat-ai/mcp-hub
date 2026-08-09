using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Korat.Cloud.Security.Audit;
using Korat.Cloud.Web.Auth.Security;
using OpenIddict.Abstractions;

namespace Korat.Cloud.Web.Oauth;

/// <summary>
/// Space-MCP inc-2b, Task 4 (spec §Pillar C DCR bullet, SF-7): the open, bounded RFC 7591
/// Dynamic Client Registration endpoint. Unauthenticated by protocol (an MCP client has no
/// credential yet); ALL abuse mitigation is by bounds, not auth:
///   • per-IP rate limit (DcrRegisterPolicy, applied on the mapped endpoint);
///   • request-body cap (SpaceMcpDcrOptions.MaxRequestBytes) enforced via a length-capped
///     stream — NOT a Request.ContentLength check, which is null (and silently skipped) under
///     Transfer-Encoding: chunked (plan-review MF-1);
///   • redirect_uris count cap + client_name length cap (SpaceMcpDcrOptions.MaxRedirectUris /
///     .MaxClientNameLength — plan-review MF-1's second half: a fat-but-under-the-byte-cap row);
///   • UNCONSENTED-only cap (SpaceMcpDcrOptions.MaxUnconsentedClients → bounded 503, PRIMARY
///     gate, registration-flood-DoS hardening) + total-rows cap (SpaceMcpDcrOptions.MaxClients →
///     bounded 503, SECONDARY backstop, Task 5) — see the two checks below for why order matters;
///   • redirect-URI policy (DcrRedirectUriPolicy — the anti-open-redirect core, Task 3);
///   • least-privilege shape reused verbatim from the pre-registered client
///     (SpaceMcpOAuthClientSeeder.BuildDescriptor: public, PKCE, korat:mcp ONLY — SF-7);
///   • TTL sweep of never-consented rows (Task 6, via the two Properties stamped here).
/// The client CANNOT self-grant more than korat:mcp: BuildDescriptor grants only scp:korat:mcp,
/// and even a client requesting openid is stopped at consent (KoratAuthorizeEndpoints — the same
/// gate the pre-registered client hits; OpenIddict's scope-permission check exempts openid, so
/// the consent policy is the real stop — proven in DcrEndToEndTests).
/// </summary>
public static class DcrEndpoints
{
    /// <summary>RFC 7591 client-metadata request. Only redirect_uris (required) and client_name
    /// (optional, display) are honored; requested scope/grant_types are IGNORED and forced to the
    /// least-privilege shape (SF-7) — the response reflects what was GRANTED, per RFC 7591 §3.2.1.</summary>
    private sealed record DcrRequest(
        [property: JsonPropertyName("redirect_uris")] string[]? RedirectUris,
        [property: JsonPropertyName("client_name")] string? ClientName);

    public static void MapDcrEndpoints(this WebApplication app)
    {
        // Fable holistic review FIX 2: create the category-named logger ONCE at endpoint-mapping
        // time (not per-request) — DcrEndpoints is a static class, so it cannot itself be used as
        // an ILogger<T> category type; ILoggerFactory.CreateLogger(string) gives the exact
        // "Korat.Cloud.Web.Oauth.DcrEndpoints" category without introducing a marker type.
        var logger = app.Services.GetRequiredService<ILoggerFactory>()
            .CreateLogger("Korat.Cloud.Web.Oauth.DcrEndpoints");
        app.MapPost(KoratOAuthConstants.RegistrationEndpointPath,
                (HttpContext ctx, IOpenIddictApplicationManager applications, IUnconsentedDcrClientCounter unconsentedCounter,
                    SpaceMcpDcrOptions options, IAuditLog auditLog, DcrCapWarningThrottle capWarningThrottle, CancellationToken ct) =>
                    HandleRegisterAsync(ctx, applications, unconsentedCounter, options, auditLog, capWarningThrottle, logger, ct))
            .RequireRateLimiting(RateLimiterRegistration.DcrRegisterPolicy);
    }

    private static async Task<IResult> HandleRegisterAsync(
        HttpContext ctx,
        IOpenIddictApplicationManager applications,
        IUnconsentedDcrClientCounter unconsentedCounter,
        SpaceMcpDcrOptions options,
        IAuditLog auditLog,
        DcrCapWarningThrottle capWarningThrottle,
        ILogger logger,
        CancellationToken ct)
    {
        // Kill switch: DCR disabled ⇒ endpoint is effectively absent (and metadata omits it).
        if (!options.Enabled)
            return Results.NotFound();

        // Registration-flood-DoS hardening — PRIMARY gate: cap UNCONSENTED DCR clients only
        // (dcr_-prefixed, zero currently-VALID authorizations). A junk-registration flood grows
        // ONLY this count — a client that completes consent (or was already consented) stops
        // counting the instant its authorization goes Valid — so the flood can never crowd out a
        // real client that is mid-consent or already consented, unlike a total-rows cap which a
        // flood fills indiscriminately. One cheap correlated-COUNT query (see
        // UnconsentedDcrClientCounter), NOT an O(N) enumerate-with-per-client-query — that shape
        // would itself be a query-amplification vector under the exact flood this defends against.
        var unconsentedCount = await unconsentedCounter.CountAsync(ct);
        if (unconsentedCount >= options.MaxUnconsentedClients)
        {
            // Fable holistic review FIX 2: an active flood produced ZERO operator-visible signal
            // before this — the Enabled kill switch has no trigger without one. Throttled to at
            // most one warning per ~60s (DcrCapWarningThrottle) so a sustained flood (which trips
            // this gate on every request) does not itself flood the logs. Internal-only: the
            // external 503 body below stays byte-identical to the backstop gate's — never leak
            // which bound tripped to the caller.
            if (capWarningThrottle.ShouldLog(DcrCapWarningThrottle.Gate.UnconsentedPrimary))
                logger.LogWarning(
                    "DCR /connect/register at capacity (unconsented primary gate): {Count}/{Limit} " +
                    "unconsented DCR clients. Rejecting with a bounded 503 until the count drops " +
                    "(consent or TTL sweep).", unconsentedCount, options.MaxUnconsentedClients);
            // Advisory back-off for well-behaved clients (fable holistic review FIX 5, NIT).
            ctx.Response.Headers["Retry-After"] = "120";
            return DcrError(StatusCodes.Status503ServiceUnavailable, "temporarily_unavailable",
                "Client registration capacity reached. Retry later.");
        }

        // SECONDARY backstop: absolute total-rows ceiling (Task 5). Defense-in-depth against a
        // bug in the unconsented-counting logic above — should in practice never be the gate that
        // actually fires, since MaxUnconsentedClients is always reached first for an unconsented
        // flood, and consented-row growth is bounded by real user volume, not attacker volume.
        // CountAsync is a cheap single COUNT; the handful of non-DCR rows (1 pre-registered +
        // future OIDC) never meaningfully erode the budget, and the TTL sweep keeps the DCR
        // population low. Bounded, retriable 503.
        var totalCount = await applications.CountAsync(ct);
        if (totalCount >= options.MaxClients)
        {
            // Same operator-signal reasoning as the primary gate above — this gate firing in
            // practice would mean the primary gate's counting logic has a bug, which is exactly
            // the scenario an operator needs paged on.
            if (capWarningThrottle.ShouldLog(DcrCapWarningThrottle.Gate.TotalBackstop))
                logger.LogWarning(
                    "DCR /connect/register at capacity (total backstop gate): {Count}/{Limit} " +
                    "total OAuth client rows. Rejecting with a bounded 503.",
                    totalCount, options.MaxClients);
            ctx.Response.Headers["Retry-After"] = "120";
            return DcrError(StatusCodes.Status503ServiceUnavailable, "temporarily_unavailable",
                "Client registration capacity reached. Retry later.");
        }

        // MF-1 (plan-review, load-bearing): the naive `if (ctx.Request.ContentLength is > 0 …)`
        // guard is a NO-OP under Transfer-Encoding: chunked (ContentLength is null — the client
        // never declared a length, so the check silently never fires) → ReadFromJsonAsync would
        // read up to Kestrel's ~30 MB global default. Instead, read through MaxLengthStream,
        // which caps the STREAM itself regardless of what (or whether) a length was declared.
        DcrRequest? request;
        try
        {
            await using var bounded = new MaxLengthStream(ctx.Request.Body, options.MaxRequestBytes);
            request = await JsonSerializer.DeserializeAsync<DcrRequest>(bounded, cancellationToken: ct);
        }
        catch (DcrBodyTooLargeException)
        {
            return DcrError(StatusCodes.Status400BadRequest, "invalid_client_metadata",
                $"Request body exceeds the {options.MaxRequestBytes}-byte limit.");
        }
        catch (JsonException)
        {
            return DcrError(StatusCodes.Status400BadRequest, "invalid_client_metadata", "Malformed JSON body.");
        }

        // MF-1 (second half): even a body that fits under MaxRequestBytes must not be allowed to
        // pack an unbounded redirect_uris array or an oversized client_name — both are stored
        // verbatim (one row per URI-worth of storage, client_name becomes DisplayName rendered on
        // every future consent page). Checked before per-URI policy validation so the response
        // distinguishes "too many" (invalid_client_metadata) from "one of them is bad"
        // (invalid_redirect_uri).
        var redirectUris = request?.RedirectUris ?? [];
        if (redirectUris.Length == 0)
            return DcrError(StatusCodes.Status400BadRequest, "invalid_redirect_uri",
                "At least one redirect_uri is required.");
        if (redirectUris.Length > options.MaxRedirectUris)
            return DcrError(StatusCodes.Status400BadRequest, "invalid_client_metadata",
                $"At most {options.MaxRedirectUris} redirect_uris are allowed.");
        if (request?.ClientName is { Length: > 0 } name && name.Length > options.MaxClientNameLength)
            return DcrError(StatusCodes.Status400BadRequest, "invalid_client_metadata",
                $"client_name exceeds the {options.MaxClientNameLength}-character limit.");

        // Anti-open-redirect: every URI must pass the policy; a single bad one rejects the whole
        // registration (never persist a partially-trusted client).
        foreach (var uri in redirectUris)
        {
            var reason = DcrRedirectUriPolicy.Validate(uri);
            if (reason is not null)
                return DcrError(StatusCodes.Status400BadRequest, "invalid_redirect_uri", reason);
        }

        // Server-assigned client_id (RFC 7591 §3.2.1) — 128-bit unguessable, dcr_-prefixed.
        var clientId = KoratOAuthConstants.DcrClientIdPrefix +
            Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

        // Reuse the pre-registered client's EXACT least-privilege descriptor (SF-7): public,
        // ConsentTypes.Explicit, korat:mcp ONLY, PKCE required. Then stamp the two DCR Properties
        // the TTL sweep keys on — the ONLY difference from the seeded client.
        var descriptor = SpaceMcpOAuthClientSeeder.BuildDescriptor(new SpaceMcpOAuthOptions
        {
            ClientId = clientId,
            DisplayName = string.IsNullOrWhiteSpace(request!.ClientName) ? "MCP client (DCR)" : request.ClientName!,
            RedirectUris = redirectUris,
        });
        descriptor.Properties[KoratOAuthConstants.DcrMarkerProperty] = JsonSerializer.SerializeToElement("1");
        descriptor.Properties[KoratOAuthConstants.DcrRegisteredAtProperty] =
            JsonSerializer.SerializeToElement(DateTimeOffset.UtcNow.ToString("O"));

        await applications.CreateAsync(descriptor, ct);

        var issuedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        // Audit (spec §Confidentiality "DCR registration"). Anonymous → actor=system; details
        // carry the assigned id + name + client IP for forensics. Best-effort (required:false):
        // an audit-store blip must not 500 an unauthenticated, rate-limited registration.
        await auditLog.RecordAsync(new AuditEvent(
            Action: AuditActions.OAuthClientRegistered,
            TargetType: "oauth_client",
            TargetId: clientId,
            ActorType: AuditActorTypes.System,
            ActorId: "system",
            DetailsJson: AuditDetails.Json(new
            {
                clientName = descriptor.DisplayName,
                redirectUris,
                ip = ctx.Connection.RemoteIpAddress?.ToString(),
            })),
            required: false, ct);

        // RFC 7591 §3.2.1 success: 201 with client_id + client_id_issued_at + echoed metadata,
        // and NO client_secret (public client). Explicit snake_case keys via a dictionary so the
        // app-wide camelCase JSON policy can't rename them.
        return Results.Json(new Dictionary<string, object?>
        {
            ["client_id"] = clientId,
            ["client_id_issued_at"] = issuedAt,
            ["client_name"] = descriptor.DisplayName,
            ["redirect_uris"] = redirectUris,
            ["grant_types"] = new[] { "authorization_code", "refresh_token" },
            ["response_types"] = new[] { "code" },
            ["token_endpoint_auth_method"] = "none",
            ["scope"] = KoratOAuthConstants.McpScope,
        }, statusCode: StatusCodes.Status201Created);
    }

    private static IResult DcrError(int status, string error, string description) =>
        Results.Json(new Dictionary<string, object?>
        {
            ["error"] = error,
            ["error_description"] = description,
        }, statusCode: status);

    /// <summary>
    /// Plan-review MF-1: a read-only wrapper around the request body that throws
    /// <see cref="DcrBodyTooLargeException"/> the instant more than <paramref name="maxBytes"/>
    /// bytes have been read — regardless of what (or whether) the client declared as
    /// Content-Length. This is the actual fix: the plan's original
    /// <c>if (ctx.Request.ContentLength is > 0 …)</c> guard never runs under
    /// <c>Transfer-Encoding: chunked</c> (ContentLength is null), letting
    /// <c>ReadFromJsonAsync</c> buffer up to Kestrel's ~30 MB global default. Every read this
    /// wrapper issues to the inner stream is itself capped to at most one byte past the
    /// remaining budget, so an oversized body is caught after reading at most
    /// <c><paramref name="maxBytes"/> + 1</c> bytes — never an unbounded amount.
    /// </summary>
    private sealed class MaxLengthStream(Stream inner, long maxBytes) : Stream
    {
        private long _totalRead;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer.AsMemory(offset, count), CancellationToken.None).AsTask().GetAwaiter().GetResult();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var allowed = maxBytes - _totalRead + 1;
            if (allowed <= 0)
                throw new DcrBodyTooLargeException();
            var slice = buffer.Length > allowed ? buffer[..(int)allowed] : buffer;
            var n = await inner.ReadAsync(slice, cancellationToken).ConfigureAwait(false);
            _totalRead += n;
            if (_totalRead > maxBytes)
                throw new DcrBodyTooLargeException();
            return n;
        }

        protected override void Dispose(bool disposing) { } // does not own `inner` (ASP.NET owns Request.Body)
    }

    private sealed class DcrBodyTooLargeException : Exception;
}
