using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Threading.RateLimiting;
using Korat.Cloud.Web.Oauth;

namespace Korat.Cloud.Web.Auth.Security;

public static class RateLimiterRegistration
{
    public const string InvitePreviewPolicy    = "invite-preview";
    public const string MagicLinkRequestPolicy = "magic-link-request";
    public const string MagicLinkConsumePolicy = "magic-link-consume";
    public const string SigninProviderPolicy   = "signin-provider";
    public const string SignoutPolicy          = "signout";
    public const string AuthMePolicy           = "auth-me";
    public const string AuthDefaultPolicy      = "auth-default";

    // CLI device-flow policies (RFC 8628 §5.1 mandates rate-limiting user_code submission).
    // /device-code: anonymous, per-IP — mitigates grain-spawn DoS.
    // /token: anonymous, per-IP — limits polling storm from a single CLI client.
    // /approve and /deny: per-session — throttles brute-force of other users' pending user_codes.
    public const string CliDeviceCodePolicy     = "cli-device-code";
    public const string CliTokenPollPolicy      = "cli-token-poll";
    public const string CliApprovePolicy        = "cli-approve";
    /// <summary>
    /// ASP.NET-level rate limit for POST /api/auth/email/change — caps the HTTP request
    /// rate per session as a first-line defence. Application-level per-user rate-limiting
    /// (max 5 requests per hour) is enforced inside EmailChangeService.RequestAsync.
    /// </summary>
    public const string EmailChangeRequestPolicy = "email-change-request";

    /// <summary>
    /// M1 pre-auth DoS guard for the public /inference/* routes.
    /// Applied BEFORE Bearer validation so a flood of requests with bogus API keys is
    /// throttled without activating Orleans grains or hitting the database.
    /// 200 req/min per IP is generous for legitimate usage (the per-key limit is 60/min)
    /// but caps amplification from a single IP sending random tokens.
    /// </summary>
    public const string InferencePreAuthPolicy = "inference-pre-auth";

    /// <summary>
    /// Space-MCP inc-2b: per-IP throttle on the anonymous open RFC 7591 DCR endpoint
    /// (POST /connect/register). Unlike /connect/token (handled inside UseAuthentication, so no
    /// endpoint policy attaches — an inc-2a Known Limitation), /connect/register is a mapped
    /// minimal API and CAN carry a policy. Permit is config-driven (SpaceMcpDcrOptions
    /// .RegisterRateLimitPerMinute) so the boundary test can dial it low on an isolated host.
    ///
    /// Registration-flood-DoS hardening item 3: this per-IP policy is defeated by an attacker
    /// rotating source IPs WITHIN one subnet (a /24 = 256 IPs x this permit). A SECOND, per-
    /// subnet window (<see cref="Korat.Cloud.Web.Oauth.SpaceMcpDcrOptions.RegisterSubnetRateLimitPerMinute"/>)
    /// is layered on the SAME endpoint via <see cref="RateLimiterOptions.GlobalLimiter"/> below —
    /// an endpoint can only carry one named <c>.RequireRateLimiting</c> policy, so the second
    /// dimension has to live in the GlobalLimiter, the same mechanism the Telegram webhook's
    /// per-IP pre-auth guard already uses. The GlobalLimiter composes with this endpoint policy
    /// by AND: a request must pass BOTH the per-IP and the per-subnet window.
    /// </summary>
    public const string DcrRegisterPolicy = "dcr-register";

    /// <summary>
    /// 032 C3 (#57 Leg 3 item 7): per-principal ceiling on the authenticated owner-management
    /// surface (space / servers / access-requests / grants / sessions / inference management).
    /// Closes the "stolen CLI 'full' token can hammer grain calls without a per-principal
    /// ceiling" gap. 300/min is far above any legitimate SPA/CLI usage but caps amplification.
    /// Partitioned by session cookie, else SHA-256 of the Authorization header (CLI Bearer),
    /// else client IP — see <see cref="KeyForPrincipal"/>.
    /// </summary>
    public const string OwnerManagementPolicy = "owner-management";

    /// <summary>
    /// 032 C2/C3: strict ceiling for the /api/admin/* operational mutations (KEK rewrap,
    /// crypto-shred). These are rare, heavyweight, irreversible ops — 10/min per principal.
    /// </summary>
    public const string AdminOpsPolicy = "admin-ops";


    /// <summary>
    /// PR-2 Task 9 (review fix): anonymous Telegram webhook
    /// (POST /api/channels/telegram/webhook/{id}). Spec §7 requires a per-BINDING inbound
    /// rate limit — and since ALL legitimate webhook traffic originates from Telegram's small
    /// IP ranges, a per-IP window would effectively be one shared global bucket: a spammed
    /// bound chat could monopolize it and 429 every other binding (making Telegram
    /// retry-amplify). So the policy partitions on the <c>{bindingId}</c> route segment
    /// (Guid-"N" shape-checked so junk ids can't mint unbounded partitions — those fall back
    /// to the per-IP bucket). 120/min per binding is far above real Telegram delivery cadence
    /// for the personal-assistant use case but caps per-binding spend amplification.
    /// The pre-auth per-IP DoS guard (bogus binding ids throttled before DB lookups amplify)
    /// is provided in addition by the <see cref="RateLimiterOptions.GlobalLimiter"/> below.
    /// </summary>
    public const string TelegramWebhookPolicy = "telegram-webhook";

    /// <summary>Path prefix of the anonymous Telegram webhook — used by the global per-IP
    /// pre-auth guard registered in <see cref="AddKoratRateLimiting"/>.</summary>
    internal const string TelegramWebhookPathPrefix = "/api/channels/telegram/webhook";

    /// <summary>
    /// Anonymous <c>GET /health</c> runs a real Postgres <c>CanConnectAsync()</c> per request
    /// with no auth gate — an unbounded flood turns into DB-connection-amplification. 30/min
    /// per IP is far above any legitimate uptime-monitor cadence (typically 10-60s) but caps
    /// the blast radius of a flood to one DB round-trip every ~2s per source IP.
    /// </summary>
    public const string HealthPolicy = "health";

    /// <summary>
    /// Registers all Korat rate-limit policies.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="trustForwardedIp">
    /// When <c>true</c>, <see cref="ResolveClientIp"/> will read the <c>Fly-Client-IP</c>
    /// header before falling back to <see cref="ConnectionInfo.RemoteIpAddress"/>.
    /// Must only be <c>true</c> when all inbound traffic is forced through Fly's edge proxy
    /// and <c>app.UseForwardedHeaders()</c> has already been registered in the pipeline.
    /// When <c>false</c>, the header is ignored even if present — preventing any client
    /// from trivially spoofing its own IP to bypass per-IP rate limits.
    /// </param>
    /// <param name="dcrRegisterSubnetPerMinute">
    /// Registration-flood-DoS hardening item 3: per-/24-IPv4 (/48-IPv6) permit for
    /// <c>POST /connect/register</c>, layered into <see cref="RateLimiterOptions.GlobalLimiter"/>
    /// alongside <paramref name="dcrRegisterPerMinute"/>'s per-IP policy. See
    /// <see cref="DcrRegisterPolicy"/>'s doc comment and
    /// <see cref="Korat.Cloud.Web.Oauth.SpaceMcpDcrOptions.RegisterSubnetRateLimitPerMinute"/> for
    /// the full rationale and trade-offs.
    /// </param>
    public static IServiceCollection AddKoratRateLimiting(
        this IServiceCollection services, bool trustForwardedIp = false, bool isTesting = false,
        int dcrRegisterPerMinute = 20, int dcrRegisterSubnetPerMinute = 60)
    {
        services.AddRateLimiter(opts =>
        {
            opts.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            // G2 (MCP best-practice): emit a Retry-After header on 429 so agents/clients back off
            // for the right interval instead of retrying blindly. The fixed-window limiters populate
            // MetadataName.RetryAfter (time until the window replenishes). Covers the inference 429s
            // too (same limiter), matching the OpenAI "retry after the indicated interval" message.
            opts.OnRejected = (context, _) =>
            {
                if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
                    context.HttpContext.Response.Headers.RetryAfter =
                        ((int)Math.Ceiling(retryAfter.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
                return ValueTask.CompletedTask;
            };
            opts.AddPolicy(InvitePreviewPolicy,    KeyForIp(60, TimeSpan.FromMinutes(1), trustForwardedIp));
            opts.AddPolicy(MagicLinkRequestPolicy, KeyForIp(5,  TimeSpan.FromHours(1),   trustForwardedIp));
            opts.AddPolicy(MagicLinkConsumePolicy, KeyForIp(30, TimeSpan.FromMinutes(1), trustForwardedIp));
            opts.AddPolicy(SigninProviderPolicy,   KeyForIp(20, TimeSpan.FromMinutes(1), trustForwardedIp));
            opts.AddPolicy(SignoutPolicy,          KeyForSession(20,  TimeSpan.FromMinutes(1), trustForwardedIp));
            opts.AddPolicy(AuthMePolicy,           KeyForSession(600, TimeSpan.FromMinutes(1), trustForwardedIp));
            opts.AddPolicy(AuthDefaultPolicy,      KeyForSession(60,  TimeSpan.FromMinutes(1), trustForwardedIp));
            // CLI device-flow: per-IP for anonymous endpoints, per-session for authenticated ones.
            // /device-code: 20/min prevents grain-spawn DoS from a single IP.
            // /token: 60/min covers normal polling (interval=5s = 12/min) with headroom for retries.
            // /approve and /deny: 5/min per-session — hard throttle on user_code brute-force.
            opts.AddPolicy(CliDeviceCodePolicy, KeyForIp(20, TimeSpan.FromMinutes(1), trustForwardedIp));
            opts.AddPolicy(CliTokenPollPolicy,  KeyForIp(60, TimeSpan.FromMinutes(1), trustForwardedIp));
            opts.AddPolicy(CliApprovePolicy,    KeyForSession(5, TimeSpan.FromMinutes(1), trustForwardedIp));
            // Email-change: 10/min per-session (application layer enforces stricter per-hour cap).
            opts.AddPolicy(EmailChangeRequestPolicy, KeyForSession(10, TimeSpan.FromMinutes(1), trustForwardedIp));
            // M1 pre-auth throttle: 200/min per-IP on public inference routes.
            // This fires BEFORE Bearer validation to prevent DoS amplification via grain/DB hits.
            opts.AddPolicy(InferencePreAuthPolicy, KeyForIp(200, TimeSpan.FromMinutes(1), trustForwardedIp));
            // Space-MCP inc-2b: per-IP throttle on open DCR (config-driven permit).
            opts.AddPolicy(DcrRegisterPolicy, KeyForIp(dcrRegisterPerMinute, TimeSpan.FromMinutes(1), trustForwardedIp));
            // 032 C3: authenticated owner-management surface — per-principal (cookie / bearer-hash / IP).
            opts.AddPolicy(OwnerManagementPolicy, KeyForPrincipal(300, TimeSpan.FromMinutes(1), trustForwardedIp));
            // 032 C2: admin operational mutations (rewrap / crypto-shred) — rare by nature.
            opts.AddPolicy(AdminOpsPolicy, KeyForPrincipal(10, TimeSpan.FromMinutes(1), trustForwardedIp));
            // PR-2 Task 9 (review fix): anonymous Telegram webhook — per-BINDING window
            // (spec §7 spend/abuse guard); junk-shaped binding ids fall back to per-IP.
            opts.AddPolicy(TelegramWebhookPolicy, KeyForTelegramBinding(120, TimeSpan.FromMinutes(1), trustForwardedIp));
            // Defence-in-depth for the same webhook: the pre-auth per-IP cap the per-binding
            // partition no longer provides (an attacker rotating random valid-shaped binding
            // ids would otherwise get unbounded DB lookups + fresh partitions). GlobalLimiter
            // composes with the endpoint policy (AND — both must grant a lease); every
            // non-special path gets a shared no-op.
            //
            // Registration-flood-DoS hardening item 3: the SAME GlobalLimiter mechanism now
            // also carries the per-SUBNET window for the open DCR endpoint, alongside
            // DcrRegisterPolicy's per-IP endpoint policy above — an endpoint can only carry one
            // named .RequireRateLimiting policy, so this second dimension (subnet, not IP) has
            // to live here rather than a second named policy. See DcrRegisterPolicy's doc
            // comment for why: a per-IP-only limit is defeated by an attacker rotating source
            // IPs within one subnet.
            opts.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(ctx =>
            {
                if (ctx.Request.Path.StartsWithSegments(TelegramWebhookPathPrefix, StringComparison.OrdinalIgnoreCase))
                    return RateLimitPartition.GetFixedWindowLimiter(
                        "tg-ip:" + ResolveClientIp(ctx, trustForwardedIp),
                        _ => new FixedWindowRateLimiterOptions { PermitLimit = 600, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 });

                if (ctx.Request.Path.StartsWithSegments(KoratOAuthConstants.RegistrationEndpointPath, StringComparison.OrdinalIgnoreCase))
                    return RateLimitPartition.GetFixedWindowLimiter(
                        ResolveClientSubnet(ctx, trustForwardedIp),
                        _ => new FixedWindowRateLimiterOptions { PermitLimit = dcrRegisterSubnetPerMinute, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 });

                return RateLimitPartition.GetNoLimiter("global-none");
            });
            // Anonymous shallow readiness probe — per-IP cap against DB-amplification floods.
            opts.AddPolicy(HealthPolicy, KeyForIp(30, TimeSpan.FromMinutes(1), trustForwardedIp));
        });
        return services;
    }

    /// <summary>
    /// Resolves the real client IP for rate-limit partitioning.
    /// </summary>
    /// <param name="ctx">The current HTTP context.</param>
    /// <param name="trustForwardedIp">
    /// When <c>true</c>, reads <c>Fly-Client-IP</c> first (set by Fly's edge proxy to
    /// the actual client address). When <c>false</c>, the header is ignored to prevent
    /// spoofing on non-Fly or misconfigured deployments — <see cref="ConnectionInfo.RemoteIpAddress"/>
    /// is the only trusted source in that case.
    /// </param>
    /// <remarks>
    /// When <c>trustForwardedIp=true</c>, <c>app.UseForwardedHeaders()</c> must be registered
    /// first in the pipeline so that <c>RemoteIpAddress</c> and <c>Request.Scheme</c> are also
    /// correctly rewritten for all other middleware (audit logs, magic-link Origin check, etc.).
    /// </remarks>
    internal static string ResolveClientIp(HttpContext ctx, bool trustForwardedIp) =>
        (trustForwardedIp ? ctx.Request.Headers["Fly-Client-IP"].FirstOrDefault() : null)
            ?? ctx.Connection.RemoteIpAddress?.ToString()
            ?? "anon";

    /// <summary>
    /// Registration-flood-DoS hardening item 3: resolves the client's containing SUBNET —
    /// /24 for IPv4, /48 for IPv6 — for the DCR per-subnet rate limit wired into
    /// <see cref="RateLimiterOptions.GlobalLimiter"/> above. Defeats an attacker who rotates
    /// source IPs WITHIN one subnet to defeat the per-IP <see cref="DcrRegisterPolicy"/> (a /24
    /// would otherwise get 256x the per-IP allowance).
    /// </summary>
    /// <remarks>
    /// An IPv4-mapped-IPv6 address (<c>::ffff:a.b.c.d</c>) is normalized to plain IPv4 FIRST,
    /// before masking. This is load-bearing: without it, EVERY v4-mapped address masks to the
    /// same all-zero first-6-bytes → they ALL collapse into the single bucket
    /// <c>dcr-subnet6:000000000000::/48</c>, so one attacker spending the per-subnet permit there
    /// would 429 every other dual-stack v4 client of the endpoint (dual-stack sockets hand Kestrel
    /// exactly these addresses in the direct, <c>trustForwardedIp=false</c> deployment). A
    /// non-parseable IP (e.g. the
    /// <c>"anon"</c> fallback <see cref="ResolveClientIp"/> returns when neither the trusted
    /// header nor <see cref="ConnectionInfo.RemoteIpAddress"/> is available) falls back to a
    /// fixed, deterministic bucket keyed on the raw string — never an unbounded partition, and
    /// never throws.
    /// </remarks>
    internal static string ResolveClientSubnet(HttpContext ctx, bool trustForwardedIp)
    {
        var ip = ResolveClientIp(ctx, trustForwardedIp);
        if (!IPAddress.TryParse(ip, out var addr))
            return "dcr-subnet-ip:" + ip;

        // Normalize v4-mapped-v6 to plain v4 BEFORE masking (see remarks above).
        if (addr.IsIPv4MappedToIPv6)
            addr = addr.MapToIPv4();

        switch (addr.AddressFamily)
        {
            case AddressFamily.InterNetwork:
            {
                var bytes = addr.GetAddressBytes();
                return $"dcr-subnet4:{bytes[0]}.{bytes[1]}.{bytes[2]}.0/24";
            }
            case AddressFamily.InterNetworkV6:
            {
                var bytes = addr.GetAddressBytes();
                var prefix = Convert.ToHexString(bytes.AsSpan(0, 6));
                return "dcr-subnet6:" + prefix + "::/48";
            }
            default:
                // No other AddressFamily is reachable from IPAddress.TryParse in practice —
                // defensive fallback keyed on the raw string, mirroring the unparseable case.
                return "dcr-subnet-ip:" + ip;
        }
    }

    private static Func<HttpContext, RateLimitPartition<string>> KeyForIp(int permit, TimeSpan window, bool trustForwardedIp) =>
        ctx => RateLimitPartition.GetFixedWindowLimiter(
            ResolveClientIp(ctx, trustForwardedIp),
            _ => new FixedWindowRateLimiterOptions { PermitLimit = permit, Window = window, QueueLimit = 0 });

    /// <summary>
    /// PR-2 Task 9 (review fix): per-BINDING partition for the anonymous Telegram webhook,
    /// keyed on the <c>{bindingId}</c> route segment. Binding ids are server-minted Guid-"N"
    /// strings (<c>ChannelBindingId.New()</c>) — anything else is attacker-crafted and falls
    /// back to the per-IP bucket so junk ids can't mint unbounded partitions.
    /// </summary>
    private static Func<HttpContext, RateLimitPartition<string>> KeyForTelegramBinding(int permit, TimeSpan window, bool trustForwardedIp) =>
        ctx => RateLimitPartition.GetFixedWindowLimiter(
            ResolveTelegramBindingKey(ctx, trustForwardedIp),
            _ => new FixedWindowRateLimiterOptions { PermitLimit = permit, Window = window, QueueLimit = 0 });

    internal static string ResolveTelegramBindingKey(HttpContext ctx, bool trustForwardedIp) =>
        ctx.Request.RouteValues["bindingId"] is string id && IsGuidNShape(id)
            ? "tb:" + id
            : "tb-ip:" + ResolveClientIp(ctx, trustForwardedIp);

    /// <summary>32 lowercase-insensitive hex chars, Guid "N" format — the only shape the server
    /// ever issues for session cookies and channel-binding ids.</summary>
    internal static bool IsGuidNShape(string? value) =>
        value is { Length: 32 } && Guid.TryParseExact(value, "N", out _);

    // Reference CanonicalSigninHandler.SessionCookieName as single source of truth — avoids the
    // silent drift hazard if Task 16 or later renames the cookie. "__Host-korat_session".
    private static Func<HttpContext, RateLimitPartition<string>> KeyForSession(int permit, TimeSpan window, bool trustForwardedIp) =>
        ctx => RateLimitPartition.GetFixedWindowLimiter(
            ResolveSessionKey(ctx, trustForwardedIp),
            _ => new FixedWindowRateLimiterOptions { PermitLimit = permit, Window = window, QueueLimit = 0 });

    /// <summary>
    /// Deferred-fix (SECURITY MINOR, cookie mirror of the bearer fix in
    /// <see cref="ResolvePrincipalKey"/>): partition by the session cookie ONLY when its value
    /// has the server-issued shape; otherwise fall back to the client IP. Without the shape
    /// check, a client sending arbitrary crafted cookie values could mint an unbounded number
    /// of fresh rate-limit partitions (memory growth + evasion of the per-IP bucket).
    /// </summary>
    internal static string ResolveSessionKey(HttpContext ctx, bool trustForwardedIp) =>
        ctx.Request.Cookies[CanonicalSigninHandler.SessionCookieName] is { } cookie
            && IsValidSessionCookieShape(cookie)
            ? cookie
            : ResolveClientIp(ctx, trustForwardedIp);

    /// <summary>
    /// Shape check for the <c>__Host-korat_session</c> cookie value. CanonicalSigninHandler
    /// issues it as <c>session.Id.ToString("N")</c> — a Guid in "N" format (32 hex chars, no
    /// dashes). Anything else (empty, truncated, attacker-crafted junk) is not a value the
    /// server could ever have issued and must not become a rate-limit partition key.
    /// </summary>
    internal static bool IsValidSessionCookieShape(string? value) => IsGuidNShape(value);

    /// <summary>
    /// 032 C3: per-principal partition for surfaces reachable with EITHER a session cookie
    /// (SPA) or an Authorization Bearer token (CLI "full" tokens, admin ops).
    ///
    /// Partition strategy:
    /// <list type="bullet">
    ///   <item>Login-session cookie present AND valid-shaped (Guid "N" — the only shape the server
    ///   ever issues) → <c>"s:{cookie-value}"</c>.  The cookie is server-issued and not
    ///   rotatable by the client, so this uniquely identifies the session.  A malformed /
    ///   crafted cookie value falls through to the IP bucket below (mirror of the bearer
    ///   prefix check).</item>
    ///   <item>Authorization header present whose value starts with our token prefix
    ///   (<c>"Bearer korat_cli_"</c>) → <c>"b:{SHA256(authz)}"</c>.  The token hash is stable
    ///   per issued credential so legitimate CLI callers get their own bucket.  An attacker
    ///   that rotates the random suffix portion still costs a DB round-trip for each attempt
    ///   because the token is looked up by hash before any grain/business-logic runs.</item>
    ///   <item>All other cases (no cookie, absent or unrecognised Authorization header, or a
    ///   header that does NOT start with our prefix) → <c>"ip:{client-ip}"</c>.  This closes
    ///   the SECURITY MINOR finding: an unauthenticated attacker rotating arbitrary
    ///   Authorization values can no longer mint an unbounded number of fresh rate-limit
    ///   partitions; every such request is bucketed by the client's IP address instead.</item>
    /// </list>
    /// </summary>
    private static Func<HttpContext, RateLimitPartition<string>> KeyForPrincipal(int permit, TimeSpan window, bool trustForwardedIp) =>
        ctx => RateLimitPartition.GetFixedWindowLimiter(
            ResolvePrincipalKey(ctx, trustForwardedIp),
            _ => new FixedWindowRateLimiterOptions { PermitLimit = permit, Window = window, QueueLimit = 0 });

    internal static string ResolvePrincipalKey(HttpContext ctx, bool trustForwardedIp)
    {
        // Deferred-fix (SECURITY MINOR): only partition on the cookie when it has the
        // server-issued Guid-"N" shape — the exact cookie mirror of the bearer-prefix check
        // below. A crafted cookie value (wrong length / non-hex) falls through to the IP
        // bucket instead of minting an attacker-controlled partition key.
        if (ctx.Request.Cookies[CanonicalSigninHandler.SessionCookieName] is { Length: > 0 } session
            && IsValidSessionCookieShape(session))
            return "s:" + session;

        // Only hash the Authorization header when it carries a Korat CLI token — any other
        // value (including attacker-rotated junk) falls through to the IP bucket below.
        // This prevents an unauthenticated flood with varying Authorization headers from
        // creating an unbounded number of per-bearer partitions in memory.
        if (ctx.Request.Headers.Authorization.FirstOrDefault() is { Length: > 0 } authz
            && (authz.StartsWith("Bearer korat_cli_", StringComparison.OrdinalIgnoreCase)
                || IsBearerJwtShape(authz)))
        {
            return "b:" + System.Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(authz)));
        }

        return "ip:" + ResolveClientIp(ctx, trustForwardedIp);
    }

    /// <summary>
    /// A bearer that looks like a JWT, checked the same way and for the same reason as the
    /// <c>korat_cli_</c> prefix above: shape only, so junk cannot mint partitions.
    ///
    /// Tokens from the sign-in provider are JWTs and carry no prefix, so without this they fell
    /// into the shared IP bucket — several agents behind one NAT would share a limit that was
    /// designed to be per-credential. That is not a policy anyone chose; it is what happened
    /// when the credential changed shape and this function was not told.
    ///
    /// Deliberately no signature check. This decides which counter to increment, not whether to
    /// let anyone in — verification happens later and costs far more than a limiter may spend on
    /// every request, including the ones it is meant to throttle.
    /// </summary>
    private static bool IsBearerJwtShape(string authz)
    {
        const string prefix = "Bearer ";
        if (!authz.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;

        var token = authz.AsSpan(prefix.Length).Trim();
        // Three non-empty base64url segments. Bounds keep a megabyte of junk from being hashed
        // on every request; the upper one is generous next to our own tokens (~1 KB).
        if (token.Length is < 20 or > 8192) return false;

        var dots = 0;
        var segment = 0;
        foreach (var c in token)
        {
            if (c == '.') { if (segment == 0) return false; segment = 0; dots++; continue; }
            if (!(char.IsAsciiLetterOrDigit(c) || c is '-' or '_')) return false;
            segment++;
        }

        return dots == 2 && segment > 0;
    }
}
