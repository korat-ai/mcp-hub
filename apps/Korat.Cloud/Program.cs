using System.Net;
using System.Reflection;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json.Serialization;
using Korat.Cloud;
using Korat.Cloud.Gateways;
using Korat.Cloud.Orleans;
using Microsoft.AspNetCore.Antiforgery;
using Korat.Cloud.Web;
using Korat.Cloud.Web.Auth;
using Korat.Cloud.Web.Auth.Endpoints;
using Korat.Cloud.Web.Auth.Options;
using Korat.Cloud.Web.Auth.Security;
using Korat.Cloud.Web.Auth.Services;
using Korat.Cloud.Web.Spaces;
using Korat.Cloud.Web.Mcp;
using Korat.Cloud.Web.Mcp.Space;
using Korat.Cloud.Web.Meta;
using Korat.Cloud.Web.Oauth;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Korat.Domain;
using Korat.Domain.Entities;
using Korat.Domain.Persistence;
using Korat.Persistence;
using Microsoft.EntityFrameworkCore;
using OpenIddict.Abstractions;
using Orleans.Serialization;
using NATS.Client.Core;
using Korat.Cloud.Observability;
using Korat.Cloud.Push;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

var builder = WebApplication.CreateBuilder(args);
var isTesting = builder.Environment.IsEnvironment("Testing");

// ── Error tracking ─────────────────────────────────────────────────────────
// Sentry / GlitchTip integration. DSN-gated: when SENTRY_DSN is unset or empty
// the SDK is a complete no-op — local dev and CI stay silent. Never hardcode DSN.
//
// Release: reuse the same KORAT_GIT_SHA / AssemblyInformationalVersion logic that
// /api/version uses. Environment: SENTRY_ENVIRONMENT env (explicit) falling back to
// ASPNETCORE_ENVIRONMENT. ILogger errors become Sentry events via MinimumEventLevel.
// BeforeSend scrubs auth headers and secrets before any event leaves the process.
// Tracing is disabled (TracesSampleRate = 0) — errors only.
{
    static string? Env(string key) => Environment.GetEnvironmentVariable(key);

    // Derive the same release identifier as /api/version.
    var sentryRelease = Env("KORAT_GIT_SHA");
    if (string.IsNullOrWhiteSpace(sentryRelease))
    {
        var informational = Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion;
        if (informational is not null && informational.Contains('+'))
            sentryRelease = informational.Split('+', 2)[1];
    }

    var sentryEnvironment = Env("SENTRY_ENVIRONMENT")
        ?? builder.Configuration["SENTRY_ENVIRONMENT"]
        ?? builder.Environment.EnvironmentName;

    builder.WebHost.UseSentry(o =>
    {
        // DSN from env / config only — never hardcoded. SDK no-ops when empty.
        o.Dsn = Env("SENTRY_DSN") ?? builder.Configuration["SENTRY_DSN"] ?? string.Empty;

        o.Release = sentryRelease;
        o.Environment = sentryEnvironment;

        // ILogger errors → Sentry events. Captures Orleans grain faults and gRPC
        // NodeGatewayService errors that surface through the logging pipeline.
        o.MinimumEventLevel = Microsoft.Extensions.Logging.LogLevel.Error;

        // No tracing / performance (GlitchTip errors-only).
        o.TracesSampleRate = 0;

        // Do not attach user email, username, IP, or other PII.
        o.SendDefaultPii = false;

        // Also suppress server name (instance identity).
        o.ServerName = null;

        // Scrub secrets/PII from every outgoing event before it leaves the process:
        // auth headers, bearer/invite/magic-link tokens, DSNs, connection-string
        // passwords, and emails — from request data AND message/exception/breadcrumb
        // text (multi-tenant: an exception message can carry another user's email).
        // Extracted to the statically-testable SentryScrub.
        o.SetBeforeSend((@event, _) => Korat.Cloud.Observability.SentryScrub.ScrubEvent(@event));
        o.SetBeforeBreadcrumb(Korat.Cloud.Observability.SentryScrub.ScrubBreadcrumb);
    });
}

// ── Horizontal scaling backplane (010-drop-redis-to-postgres) ───────────────
// Multi-instance behaviour is gated on ONE fact: can the host tell us an address that
// peers can dial? That address is what a silo writes into the membership table, so without
// it a second instance is unreachable and "clustering" would be a claim with nothing behind
// it. Hence the gate is the address itself, not the name of the hosting provider.
//
//   Fly        → FLY_PRIVATE_IP      (6PN IPv6, injected by the Fly runtime)
//   Kubernetes → KORAT_ADVERTISED_IP (status.podIP via the downward API)
//   local / CI → neither → UseLocalhostClustering + filesystem DP, unchanged
//
// When clustered, Orleans cluster membership AND Data Protection keys live in Postgres
// (ADO.NET clustering + EF). Postgres is the single durable backplane; Redis is gone
// (relay uses NATS below).
var advertisedAddressRaw = Environment.GetEnvironmentVariable("KORAT_ADVERTISED_IP")
    ?? Environment.GetEnvironmentVariable("FLY_PRIVATE_IP");
var clustered = IPAddress.TryParse(advertisedAddressRaw, out var advertisedIp);

// Orleans ADO.NET clustering resolves its DB provider by invariant name — register Npgsql.
if (clustered)
{
    System.Data.Common.DbProviderFactories.RegisterFactory("Npgsql", Npgsql.NpgsqlFactory.Instance);
}

// ── Relay data plane (009-nats-relay-backplane) ─────────────────────────────
// Gate the cross-machine relay on NATS_URL. Absent → NullRelayBackplane = the original
// in-process relay (single-machine fallback, kept ≥6 months). Present → Core NATS carries
// frames to whichever machine holds the peer node, so co-location is no longer required.
// Orleans stays the control plane (session topology); NATS is byte transport only.
//
// 031-relay-confidentiality (N-1a): NATS_NKEY_SEED is the Ed25519 NKey seed (SU... format)
// for authenticating to the authz-enabled broker.  Absent ⇒ anonymous connect (backward-
// compatible with the pre-authz broker; dev/local/CI setups without the Fly secret keep
// working).  Rollout: set the secret on cloud apps BEFORE deploying the authz broker —
// see deploy/korat-nats/README.md §Rollout order.
var natsUrl = builder.Configuration["NATS_URL"] ?? Environment.GetEnvironmentVariable("NATS_URL");
var natsNkeySeed = builder.Configuration["NATS_NKEY_SEED"] ?? Environment.GetEnvironmentVariable("NATS_NKEY_SEED");
var hasNats = !string.IsNullOrWhiteSpace(natsUrl);
if (hasNats)
{
    // Pass nkeySeed when available; NatsUrl.ToOpts falls back to anonymous when null/empty.
    var natsOpts = NatsUrl.ToOpts(natsUrl!, name: "korat-cloud", nkeySeed: natsNkeySeed);
    builder.Services.AddSingleton<INatsConnection>(_ => new NatsConnection(natsOpts));
    builder.Services.AddSingleton<IRelayBackplane, NatsRelayBackplane>();
}
else
{
    builder.Services.AddSingleton<IRelayBackplane, NullRelayBackplane>();
}
builder.Services.AddSingleton<ISessionRouteResolver, OrleansSessionRouteResolver>();
builder.Services.AddSingleton<IMcpToolCallSink, OpenTelemetryToolCallSink>();
builder.Services.AddSingleton<McpToolCallInspector>();

// OpenTelemetry — emit MCP tool-call metadata (which tool of which MCP server was called).
// Send-only: the OTLP exporter is wired only when OTEL_EXPORTER_OTLP_ENDPOINT is set, so the
// app runs fine with no collector. Instrumentation always emits (cheap no-op when unobserved).
var otlpEndpoint = builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]
    ?? Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT");
var hasOtlp = !string.IsNullOrWhiteSpace(otlpEndpoint);
builder.Services.AddOpenTelemetry()
    .ConfigureResource(r => r.AddService("korat-cloud"))
    .WithTracing(t =>
    {
        t.AddSource(KoratTelemetry.SourceName);
        if (hasOtlp) t.AddOtlpExporter();
    })
    .WithMetrics(m =>
    {
        m.AddMeter(KoratTelemetry.SourceName);
        if (hasOtlp) m.AddOtlpExporter();
    });

// 006-cli-stdio-bridge: gRPC requires HTTP/2. By default Kestrel only negotiates
// HTTP/2 over TLS — over plain http it advertises HTTP/1.1 and rejects gRPC
// clients with HTTP_1_1_REQUIRED. We expose a second plaintext endpoint dedicated
// to HTTP/2 prior-knowledge so the browser UI + REST stay on HTTP/1.1:
//
//   ASPNETCORE_URLS (default http://localhost:5191) → HTTP/1.1, browser/UI/REST
//   Korat:Cloud:GrpcPort (default 5192)             → HTTP/2 only, gRPC gateway
//
// The CLI's gRPC client connects to the GrpcPort; the REST HttpClient stays on
// the main URL. Production runs on TLS, where Fly's edge proxy terminates TLS
// and presents both protocols to the world while the app continues to expose
// two separate cleartext ports internally.
//
// Production binding (Fly): set ASPNETCORE_URLS=http://0.0.0.0:8080 (REST) and
// KORAT_GRPC_PORT=8081 (gRPC). KORAT_BIND_ALL_INTERFACES=1 forces the gRPC
// listener onto 0.0.0.0 instead of loopback so the Fly proxy can reach it.
var grpcPort = int.TryParse(
        builder.Configuration["Korat:Cloud:GrpcPort"] ?? Environment.GetEnvironmentVariable("KORAT_GRPC_PORT"),
        out var parsed)
    ? parsed
    : 5192;
// Resolve the REST URL the host would otherwise listen on so we can preserve it
// alongside the gRPC port. `ConfigureKestrel.Listen(...)` replaces URL-based
// config, so we must re-add the REST URL explicitly when we add the gRPC URL.
var restUrls = (builder.Configuration["urls"]
    ?? Environment.GetEnvironmentVariable("ASPNETCORE_URLS")
    ?? "http://localhost:5191")
    .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
// In non-Development environments (or when KORAT_BIND_ALL_INTERFACES=1) the gRPC
// listener must bind to 0.0.0.0 so external proxies can reach it. Dev keeps the
// safer loopback default to avoid silently exposing gRPC on a developer's LAN.
var bindAll = Environment.GetEnvironmentVariable("KORAT_BIND_ALL_INTERFACES") == "1"
    || !builder.Environment.IsDevelopment() && !isTesting;
var grpcBindAddress = bindAll ? System.Net.IPAddress.Any : System.Net.IPAddress.Loopback;
builder.WebHost.ConfigureKestrel(options =>
{
    foreach (var url in restUrls)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u))
            continue;
        var addr = System.Net.IPAddress.TryParse(u.Host, out var ip)
            ? ip
            : System.Net.IPAddress.Loopback;
        options.Listen(addr, u.Port, listen =>
        {
            // REST/UI traffic — leave protocols at the Kestrel default (Http1AndHttp2,
            // which over plain HTTP resolves to HTTP/1.1).
        });
    }
    options.Listen(
        grpcBindAddress,
        grpcPort,
        listen => listen.Protocols = Microsoft.AspNetCore.Server.Kestrel.Core.HttpProtocols.Http2);
});

builder.Services.Configure<GitHubAuthOptions>(
    builder.Configuration.GetSection(GitHubAuthOptions.SectionName));

builder.Services.Configure<GoogleAuthOptions>(
    builder.Configuration.GetSection(GoogleAuthOptions.SectionName));

builder.Services.Configure<CliOptions>(
    builder.Configuration.GetSection(CliOptions.SectionName));

builder.Services.Configure<BootstrapOptions>(
    builder.Configuration.GetSection(BootstrapOptions.SectionName));

// 030 (push-to-wake): APNs silent-push sender.
// Gate on KeyId presence: NullPushWakeSender when unconfigured so the wake path degrades
// gracefully to today's immediate ServerUnavailable (nothing breaks while secrets are not yet set).
builder.Services.Configure<ApnsOptions>(builder.Configuration.GetSection(ApnsOptions.SectionName));
var apnsKeyId = builder.Configuration["Korat:Apns:KeyId"];
if (!string.IsNullOrWhiteSpace(apnsKeyId))
{
    // FIX (stale-DNS): register a NAMED HttpClient instead of typed so that
    // ApnsTransport (singleton) can call IHttpClientFactory.CreateClient("apns")
    // per-send. The typed AddHttpClient<TService, TImpl>() pattern captures a single
    // HttpClient into the singleton constructor — bypassing the handler pool's
    // PooledConnectionLifetime DNS refresh. Using the factory per-send keeps the
    // singleton sender but lets the handler pool recycle connections on schedule.
    builder.Services.AddHttpClient(Korat.Cloud.Push.ApnsTransport.HttpClientName)
        .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
        {
            // Keep connections alive — APNs expects persistent HTTP/2 connections.
            // Pool rotates (for DNS refresh) every 10 min.
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
        });
    // 031 (mobile-push increment 2): ApnsTransport is the shared ES256/JWT + HTTP/2 plumbing —
    // ONE instance (ONE JWT cache) consumed by BOTH the silent-wake sender and the new alert
    // sender (Task 2), so Apple only ever sees one provider-token rotation cadence.
    builder.Services.AddSingleton<Korat.Cloud.Push.ApnsTransport>();
    builder.Services.AddSingleton<IPushWakeSender, Korat.Cloud.Push.ApnsPushWakeSender>();
}
else
{
    builder.Services.AddSingleton<IPushWakeSender, NullPushWakeSender>();
}

// 031 (mobile-push increment 2): FCM (Android) config — the client is only registered below
// when BOTH ProjectId and ServiceAccountJson are present. Per-platform no-op: a missing FCM
// config must not affect APNs and vice-versa (§4a).
builder.Services.Configure<Korat.Cloud.Push.FcmOptions>(
    builder.Configuration.GetSection(Korat.Cloud.Push.FcmOptions.SectionName));
var fcmProjectId = builder.Configuration["Korat:Fcm:ProjectId"];
var fcmServiceAccountJson = builder.Configuration["Korat:Fcm:ServiceAccountJson"];
if (!string.IsNullOrWhiteSpace(fcmProjectId) && !string.IsNullOrWhiteSpace(fcmServiceAccountJson))
{
    builder.Services.AddSingleton<Korat.Cloud.Push.IFcmMessagingClient, Korat.Cloud.Push.FirebaseFcmMessagingClient>();
}

// 031: IAlertPushSender — the single entry point AccessRequestNotifier calls (Task 6). Routes by
// platform to either the real APNs/FCM sender or a NullAlertPushSender when that platform's
// secrets are absent, so each platform degrades independently (§4a, §6).
builder.Services.AddSingleton<Korat.Cloud.Push.IAlertPushSender>(sp =>
{
    IAlertPushSender apnsAlert = !string.IsNullOrWhiteSpace(apnsKeyId)
        ? ActivatorUtilities.CreateInstance<Korat.Cloud.Push.ApnsAlertSender>(sp)
        : new Korat.Cloud.Push.NullAlertPushSender();
    IAlertPushSender fcmAlert = !string.IsNullOrWhiteSpace(fcmProjectId) && !string.IsNullOrWhiteSpace(fcmServiceAccountJson)
        ? ActivatorUtilities.CreateInstance<Korat.Cloud.Push.FcmAlertSender>(sp)
        : new Korat.Cloud.Push.NullAlertPushSender();
    return new Korat.Cloud.Push.RoutingAlertPushSender(apnsAlert, fcmAlert);
});

// SEC-HIGH-3: in any non-Development / non-Testing environment both GitHub OAuth credentials
// must be set. Without them, federation fails at token exchange with an opaque GitHub error
// ("invalid_client") and the only user-visible symptom is a generic OAuth failure page —
// hard to diagnose without log access. Mirror the SEC-HIGH-1 connection-string + SEC-HIGH-2
// signing-key fail-fast pattern.
var ghClientIdCheck = builder.Configuration["GitHubAuth:ClientId"];
var ghClientSecretCheck = builder.Configuration["GitHubAuth:ClientSecret"];
if (!builder.Environment.IsDevelopment() && !isTesting
    && (string.IsNullOrEmpty(ghClientIdCheck) || string.IsNullOrEmpty(ghClientSecretCheck)))
{
    throw new InvalidOperationException(
        "GitHubAuth:ClientId and GitHubAuth:ClientSecret must both be set in production. " +
        "Federation will fail at token exchange with an opaque 'invalid_client' error otherwise. " +
        $"ClientId present: {!string.IsNullOrEmpty(ghClientIdCheck)}, " +
        $"ClientSecret present: {!string.IsNullOrEmpty(ghClientSecretCheck)}.");
}

// SEC-HIGH-4: in any non-Development / non-Testing environment both Google OAuth credentials
// must be set. Same fail-shape as SEC-HIGH-3 — opaque token-exchange failure otherwise.
var googleClientIdCheck = builder.Configuration["GoogleAuth:ClientId"];
var googleClientSecretCheck = builder.Configuration["GoogleAuth:ClientSecret"];
if (!builder.Environment.IsDevelopment() && !isTesting
    && (string.IsNullOrEmpty(googleClientIdCheck) || string.IsNullOrEmpty(googleClientSecretCheck)))
{
    throw new InvalidOperationException(
        "GoogleAuth:ClientId and GoogleAuth:ClientSecret must both be set in production. " +
        "Federation will fail at token exchange otherwise. " +
        $"ClientId present: {!string.IsNullOrEmpty(googleClientIdCheck)}, " +
        $"ClientSecret present: {!string.IsNullOrEmpty(googleClientSecretCheck)}.");
}

// Persist Data Protection keys so OAuth `state`, antiforgery tokens, and session cookies
// survive app restarts / redeploys AND validate across instances. Two paths:
//
//   on Fly  → Postgres via EF (DbContextXmlRepository over the DbContext factory). All
//             instances share one key ring in the DataProtectionKeys table.
//   off Fly → PersistKeysToFileSystem at Korat:DataProtectionKeyPath (local dev) when set.
//
// SetApplicationName("Korat.Cloud") is set in BOTH branches so antiforgery tokens and
// session cookies minted on one instance are valid on any other instance — the app name
// is mixed into the key-derivation function, so mismatched names silently break cookies.
var dpKeyPath = builder.Configuration["Korat:DataProtectionKeyPath"];
// 032 C7 (#57 Leg 3 item 4): OPTIONAL key-ring protection certificate. Inert until the
// KORAT__DATAPROTECTION__CERTPFXBASE64 secret is set (post-rollout, #55-KEK-style activation).
// When present, NEW ring keys are written cert-encrypted → a DB dump alone can no longer forge
// session cookies once the ring rotates. Old plaintext keys stay readable (no data migration).
// Fail-fast: an unloadable PFX throws here, before the host serves traffic.
var dpProtectionCert = Korat.Cloud.DataProtection.DpCertLoader.TryLoadFromConfig(builder.Configuration);
if (clustered)
{
    // The app registers IDbContextFactory<KoratDbContext> (not a scoped context), so the
    // built-in PersistKeysToDbContext can't resolve a context — wire our factory-based
    // IXmlRepository into KeyManagementOptions instead.
    builder.Services.AddSingleton<Korat.Cloud.DataProtection.DbContextXmlRepository>();
    var dpBuilder = builder.Services.AddDataProtection().SetApplicationName("Korat.Cloud");
    if (dpProtectionCert is not null)
        dpBuilder.ProtectKeysWithCertificate(dpProtectionCert);
    builder.Services.AddOptions<Microsoft.AspNetCore.DataProtection.KeyManagement.KeyManagementOptions>()
        .Configure<Korat.Cloud.DataProtection.DbContextXmlRepository>((options, repository) =>
            options.XmlRepository = repository);
}
else if (!string.IsNullOrWhiteSpace(dpKeyPath))
{
    // Single-instance / local path: persist to the mounted /data volume (Fly) or a local dir.
    Directory.CreateDirectory(dpKeyPath);
    var dpBuilder = builder.Services.AddDataProtection()
        .PersistKeysToFileSystem(new DirectoryInfo(dpKeyPath))
        .SetApplicationName("Korat.Cloud");
    if (dpProtectionCert is not null)
        dpBuilder.ProtectKeysWithCertificate(dpProtectionCert);
}

var authBuilder = builder.Services.AddAuthentication(opts =>
    {
        opts.DefaultScheme = CookieAuthenticationDefaults.AuthenticationScheme;
        opts.DefaultChallengeScheme = CookieAuthenticationDefaults.AuthenticationScheme;
    })
    .AddCookie(opts =>
    {
        // Ephemeral cookie bridging the OAuth callback to CanonicalSigninHandler (Task 14).
        // The final long-lived session cookie '__Host-korat_session' is set by Task 14's flow.
        opts.Cookie.Name = Korat.Cloud.Web.Auth.CanonicalSigninHandler.IntermediateSessionCookieName;
        opts.Cookie.HttpOnly = true;
        opts.Cookie.SecurePolicy = CookieSecurePolicy.Always;
        opts.Cookie.SameSite = SameSiteMode.Lax;
        opts.Cookie.Path = "/";
        opts.LoginPath = "/app/signin";
        opts.ExpireTimeSpan = TimeSpan.FromMinutes(10);
    });

// Gate each external OAuth handler on the presence of its credentials.
// In dev / testing the credentials are typically absent (no secrets in repo), so we skip
// registration — OAuthOptions.Validate() would throw "Provide ClientId" on the first
// request if we registered with an empty ClientId. The prod security property is preserved
// by the SEC-HIGH-3 / SEC-HIGH-4 startup guards above, which throw before any request
// reaches the middleware when running in a non-Dev / non-Testing environment with missing
// credentials.
var githubClientId = builder.Configuration["GitHubAuth:ClientId"];
var githubClientSecret = builder.Configuration["GitHubAuth:ClientSecret"];
if (!string.IsNullOrEmpty(githubClientId) && !string.IsNullOrEmpty(githubClientSecret))
{
    authBuilder.AddGitHubOAuth(opts =>
    {
        opts.ClientId = githubClientId;
        opts.ClientSecret = githubClientSecret;
        // TODO(SP4-hardening): tighten Action<OAuthOptions> contract to Action<GitHubAuthOptions>
        // so callers can't accidentally clobber Events.OnCreatingTicket or Scope list.
        // Today the only caller is this lambda; future callers might silently break email verification.
    });
}

// ── Вход через провайдер входа Korat ────────────────────────────────────────
// Тот же шов, что у GitHub и Google: обработчик кладёт результат в промежуточную cookie,
// а решение «кто это и что с ним делать» принимает CanonicalSigninHandler. Так вход через
// SSO получает ровно те же связывание, заведение учётки с пространством по умолчанию и
// экран подтверждения привязки, что и остальные способы, — а не свою копию всего этого.
//
// Клиента и секрет отдельно от Sso:Issuer: издатель нужен и валидатору токенов, который
// работает без всякого клиента, — он только проверяет чужие подписи.
var ssoIssuer = builder.Configuration["Sso:Issuer"];
var ssoClientId = builder.Configuration["Sso:ClientId"];
var ssoClientSecret = builder.Configuration["Sso:ClientSecret"];
if (!string.IsNullOrWhiteSpace(ssoIssuer)
    && !string.IsNullOrWhiteSpace(ssoClientId)
    && !string.IsNullOrWhiteSpace(ssoClientSecret))
{
    authBuilder.AddOpenIdConnect(Korat.Cloud.Web.Auth.KoratSsoDefaults.Scheme, opts =>
    {
        opts.Authority = ssoIssuer;
        opts.ClientId = ssoClientId;
        opts.ClientSecret = ssoClientSecret;
        opts.CallbackPath = "/signin/korat/callback";
        opts.ResponseType = "code";
        opts.UsePkce = true;
        opts.SaveTokens = false;
        opts.GetClaimsFromUserInfoEndpoint = false;

        opts.Scope.Clear();
        opts.Scope.Add("openid");
        opts.Scope.Add("email");
        opts.Scope.Add("profile");

        // Как у остальных провайдеров: возврат — межсайтовый переход верхнего уровня,
        // и при значении по умолчанию correlation-cookie не приходит обратно.
        opts.CorrelationCookie.SameSite = SameSiteMode.None;
        opts.CorrelationCookie.SecurePolicy = CookieSecurePolicy.Always;
        opts.NonceCookie.SameSite = SameSiteMode.None;
        opts.NonceCookie.SecurePolicy = CookieSecurePolicy.Always;

        // email_verified приходит в токене личности, но в утверждения принципала штатно
        // не переносится. Без него любой вход через SSO считался бы неподтверждённым и
        // не мог бы присоединиться к учётке — то же место, на котором спотыкались Google
        // и GitHub.
        opts.ClaimActions.MapJsonKey("email_verified", "email_verified");

        opts.Events.OnRemoteFailure = ctx =>
        {
            var log = ctx.HttpContext.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("KoratSso");
            log.LogWarning("Korat SSO federation failed: {Failure}", ctx.Failure?.Message);
            ctx.Response.Redirect("/app/signin?error=sso");
            ctx.HandleResponse();
            return Task.CompletedTask;
        };
    });
}

var googleClientId = builder.Configuration["GoogleAuth:ClientId"];
var googleClientSecret = builder.Configuration["GoogleAuth:ClientSecret"];
if (!string.IsNullOrEmpty(googleClientId) && !string.IsNullOrEmpty(googleClientSecret))
{
    authBuilder.AddGoogle(opts =>
    {
        opts.ClientId = googleClientId;
        opts.ClientSecret = googleClientSecret;
        opts.CallbackPath = "/signin/google/callback";
        opts.UsePkce = true;
        // Correlation cookie must survive Google's cross-site redirect back to the callback.
        opts.CorrelationCookie.SameSite = SameSiteMode.None;
        opts.CorrelationCookie.SecurePolicy = CookieSecurePolicy.Always;
        opts.SaveTokens = false;
        opts.Scope.Add("openid");
        opts.Scope.Add("email");
        opts.Scope.Add("profile");
        // Native email_verified claim from Google's OIDC ID token — map onto principal so
        // CanonicalSigninHandler (Task 14) can read it identically to the GitHub-handler-set claim.
        opts.ClaimActions.MapJsonKey("email_verified", "email_verified");
        opts.Events.OnRemoteFailure = ctx =>
        {
            var log = ctx.HttpContext.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("GoogleOAuth");
            log.LogWarning("Google federation failed: {Failure}", ctx.Failure?.Message);
            ctx.Response.Redirect("/app/signin?error=google");
            ctx.HandleResponse();
            return Task.CompletedTask;
        };
    });
}

// Serialize all enums as their string names across every HTTP endpoint so the
// TS client receives "Disabled" / "Active" / "Closed" instead of raw integers.
// Fixes previously-silent mismatches in vanilla JS comparisons (e.g. server.status === 'Disabled').
builder.Services.ConfigureHttpJsonOptions(opts =>
{
    opts.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
});

// Order of precedence:
// 1. DATABASE_URL env var (Fly Postgres / MPG / Supabase / Neon all expose this).
//    Wins over appsettings to make production wiring reliable — Fly attach sets DATABASE_URL,
//    but appsettings.json carries a template (no password) that would otherwise short-circuit.
// 2. ConnectionStrings:Korat from config (appsettings + env var ConnectionStrings__Korat).
//    Used by local dev (appsettings.Development.json supplies password).
// 3. Hardcoded localhost fallback for ad-hoc dev runs.
var connectionString = ConvertDatabaseUrlIfPresent(Environment.GetEnvironmentVariable("DATABASE_URL"))
    ?? builder.Configuration.GetConnectionString("Korat")
    ?? "Host=localhost;Database=korat;Username=korat;Password=korat";

// SEC-HIGH-1: in any non-Development / non-Testing environment the connection string
// must carry a Password (or use OS auth / integrated security). The committed
// appsettings.json carries a TEMPLATE without a password — operator must override via
// the standard ConnectionStrings__Korat env var. Fail-fast so a misconfigured production
// instance does not boot up effectively unauthenticated against the DB.
if (!builder.Environment.IsDevelopment() && !isTesting)
{
    var lower = connectionString.ToLowerInvariant();
    var hasPassword = lower.Contains("password=") || lower.Contains("pwd=") || lower.Contains("integrated security=");
    if (!hasPassword)
    {
        throw new InvalidOperationException(
            "ConnectionStrings:Korat is missing a password. " +
            "Set ConnectionStrings__Korat in the environment with a full connection string. " +
            "The committed appsettings.json carries a template only.");
    }
}

if (isTesting)
{
    builder.Services.AddDbContextFactory<KoratDbContext>(options =>
        options.UseInMemoryDatabase("korat-test"));
}
else
{
    builder.Services.AddDbContextFactory<KoratDbContext>(options =>
        options.UseNpgsql(connectionString));
}

// Scoped KoratDbContext alongside the factory — required by:
//   (a) OpenIddict's EF Core store (UseDbContext<T>() resolves T from request scope)
//   (b) Task 4-8 auth services (SessionService / MagicLinkService)
//       which constructor-inject KoratDbContext directly.
// AddDbContextFactory was chosen first for grain code with explicit lifecycle control;
// this scoped registration adds per-request DbContext for the HTTP request pipeline.
// Resolves the context from the factory so both registrations share the same connection
// string + options (no drift if connection string changes).
builder.Services.AddScoped(sp => sp.GetRequiredService<IDbContextFactory<KoratDbContext>>().CreateDbContext());

// SEC-HIGH-2: in any non-Development / non-Testing environment the OpenIddict signing
// certificate MUST be configured via one of two sources (in precedence order):
//   1. OpenIddict:SigningKeyPath — path to a PKCS#12 (.pfx) file on disk.
//   2. OpenIddict:SigningKeyBase64 — base64-encoded PKCS#12 bytes (Fly secret name
//      OpenIddict__SigningKeyBase64), suitable for secret-manager environments where
//      mounting a certificate file is impractical.
// Falling through to AddDevelopmentSigningCertificate() in prod would mint ephemeral
// keys regenerated every boot — every JWT invalidates on restart and the only symptom
// is "users keep getting logged out". Fail-fast.
var openIddictHasKey = OpenIddictSigningKey.IsAvailable(builder.Configuration);
if (!builder.Environment.IsDevelopment() && !isTesting && !openIddictHasKey)
{
    var configuredPath = builder.Configuration["OpenIddict:SigningKeyPath"];
    var hasBase64 = !string.IsNullOrEmpty(builder.Configuration["OpenIddict:SigningKeyBase64"]);
    throw new InvalidOperationException(
        "No OpenIddict signing key is configured. " +
        "Set either 'OpenIddict:SigningKeyPath' (path to a .pfx file) or " +
        "'OpenIddict:SigningKeyBase64' (base64-encoded .pfx bytes, Fly secret: OpenIddict__SigningKeyBase64). " +
        $"Configured path: '{configuredPath ?? "(null)"}', base64 present: {hasBase64}. " +
        "Production must use a persisted signing certificate — ephemeral dev certs would " +
        "invalidate every JWT on restart.");
}

// ACTIVE since inc-2a (Space-MCP OAuth AS; decision history below): the OpenIddict server
// configured below now serves /connect/authorize + /connect/token for the pre-registered
// Space-MCP client (spec §Pillar C). Today's cookie-session / CLI-bearer auth
// (PolymorphicAuthResolver) is UNCHANGED — this is a NEW, additional inbound auth surface.
// The identity-provider side (openid/email/profile clients) remains future work — nothing
// here narrows it ([[project_openiddict_keep]]): this scaffolding is the foundation for
// Korat becoming its own OpenID Connect provider (authorization-code flow, signing-key
// infra, and the OpenIddict{Applications,Authorizations,Scopes,Tokens} tables are already
// in place). Decision: keep (2026-06-02); activate for Space-MCP inc-2a (2026-07-11).
//
// DataProtection persistence is already configured above (lines ~195–214): on Fly,
// keys are stored in the DataProtectionKeys Postgres table via DbContextXmlRepository;
// off Fly, keys are persisted to the filesystem path in Korat:DataProtectionKeyPath.
builder.Services.AddOpenIddict()
    .AddCore(opts =>
    {
        opts.UseEntityFrameworkCore(ef =>
        {
            ef.UseDbContext<KoratDbContext>();
            // Space-MCP inc-2b (Task 6): the EF Core InMemory provider (Testing env only) does
            // NOT support EF Core's bulk ExecuteDelete/ExecuteUpdate — verified via decompile:
            // OpenIddict 7.5.0's net10.0-targeted EF Core application/authorization stores use
            // ExecuteDeleteAsync by default (OpenIddictEntityFrameworkCoreOptions.
            // DisableBulkOperations == false), which throws InvalidOperationException against
            // UseInMemoryDatabase. IOpenIddictApplicationManager.DeleteAsync (the DCR
            // TTL-reaper's hard-delete, Task 6) is the first caller in this codebase to exercise
            // that path — prior code only used TryRevokeAsync (an UPDATE). Postgres
            // (non-Testing) supports bulk delete natively, so this fallback to the row-by-row
            // Remove()+SaveChangesAsync() path is Testing-only; production keeps the faster
            // bulk-delete path unchanged.
            if (isTesting)
                ef.DisableBulkOperations();
        });
    })
    .AddServer(opts =>
    {
        // Sub-project 1 scaffolding, ACTIVATED by Space-MCP inc-2a (spec §Pillar C):
        // authorization-code + PKCE + refresh for the per-Space MCP surface. The identity-
        // provider side (openid/email/profile clients) remains future work — nothing here
        // narrows it ([[project_openiddict_keep]]).
        opts.AllowAuthorizationCodeFlow();
        opts.AllowRefreshTokenFlow();                     // SF-7: long-lived MCP connections
        opts.RequireProofKeyForCodeExchange();            // OAuth 2.1 / MCP 2025-06-18: PKCE always

        // Space-MCP inc-2b (Task 1): S256-only PKCE. OpenIddict 7.5.0 seeds
        // OpenIddictServerOptions.CodeChallengeMethods with { "plain", "S256" } (verified
        // grounding #4); removing "plain" narrows BOTH the RFC 8414 advertisement
        // (Discovery.AttachCodeChallengeMethods reads this set) AND the authorization-request
        // validator (ValidateProofKeyForCodeExchangeParameters rejects a plain — or a
        // method-less — code_challenge once "plain" is gone). MCP 2025-06-18 / OAuth 2.1
        // mandate S256; the live-dev smoke caught the default advertising "plain".
        // PostConfigure (OpenIddictServerConfiguration) never re-touches this set, so the
        // removal is final.
        opts.Configure(serverOptions =>
            serverOptions.CodeChallengeMethods.Remove(OpenIddictConstants.CodeChallengeMethods.Plain));

        opts.SetAuthorizationEndpointUris("/connect/authorize")
            .SetTokenEndpointUris("/connect/token");

        // Inc-2a dynamic per-Space audience (RFC 8707, BLOCKER-1): the audience is the exact
        // per-Space /mcp/{spaceSeg} URL, which cannot be statically RegisterAudiences/
        // RegisterResources'd — disable 7.x's static validation; the consent handler
        // (KoratAuthorizeEndpoints) is the SINGLE writer of identity.SetResources(...), and
        // the load-bearing audience==path-Space check runs at the resource server
        // (SpaceMcpAuth, Task 6) on every request.
        opts.DisableAudienceValidation();
        opts.DisableResourceValidation();

        // Task 3 build-time correction (the plan's "verified grounding" #2 claim that
        // IgnoreAudiencePermissions()/IgnoreResourcePermissions() are "NOT needed" does not
        // hold under actual runtime behavior — TDD caught it): DisableResourceValidation/
        // DisableAudienceValidation above only turn off the STATIC RegisterAudiences/
        // RegisterResources allow-list. OpenIddict ALSO runs a SEPARATE, always-on
        // per-CLIENT permission check (ValidateResourcePermissions at /connect/authorize,
        // ValidateResourcePermissions + ValidateAudiencePermissions at /connect/token) that
        // rejects any "resource"/audience the client was never explicitly granted an
        // "rsrc:"/"aud:" permission for (ID2192/ID2191) — and a per-Space resource URL can
        // never be statically granted (there are unboundedly many Spaces). Without ignoring
        // these, EVERY authorize/token request carrying a resource parameter is rejected
        // before this app's code ever runs — the whole inc-2a flow is undeliverable. This
        // does NOT weaken BLOCKER-1: the load-bearing per-Space checks are the consent
        // handler's owner-owns-Space check (Task 3) and the resource server's
        // audience==path-Space + consent-Space==path-Space checks (Task 6), neither of which
        // this client-level permission system replaces — it is a coarser, static per-client
        // allow-list orthogonal to the dynamic per-Space authorization this plan implements.
        // Scope permissions (scp:) are DELIBERATELY left enabled — that check is what makes
        // the pre-registered client's korat:mcp-only registration bite at the OpenIddict layer.
        opts.IgnoreResourcePermissions();
        opts.IgnoreAudiencePermissions();

        // SF-6: reference (DB-backed) access + refresh tokens — a console consent-revoke
        // kills them IMMEDIATELY (next /mcp request 401s), instead of self-contained JWTs
        // living out their lifetime. Per-request DB read is cost-parity with the inc-1
        // CliToken path (ValidateWithScopeAsync also hits the DB every request).
        opts.UseReferenceAccessTokens();
        opts.UseReferenceRefreshTokens();
        opts.SetAccessTokenLifetime(TimeSpan.FromHours(1));
        opts.SetRefreshTokenLifetime(TimeSpan.FromDays(14));
        // Strict rotation: rolling refresh tokens are the OpenIddict default; zero reuse
        // leeway means ANY replay of a rotated refresh token trips reuse detection and
        // revokes the authorization's whole token chain (tested in Task 5). MCP clients are
        // single-holder; a tripped client simply re-runs the 401→OAuth flow.
        opts.SetRefreshTokenReuseLeeway(TimeSpan.Zero);

        // Single fixed signing key from configuration (rotation deferred to issue #3).
        // For Sub-project 1 scaffolding the same cert is used for both signing and encryption;
        // splitting into separate certs is a #3 production-hardening item.
        // Key source resolved by OpenIddictSigningKey.Resolve(): SigningKeyPath takes
        // precedence over SigningKeyBase64 (Fly secret). See SEC-HIGH-2 guard above.
        var signingCert = OpenIddictSigningKey.Resolve(builder.Configuration);
        if (signingCert is not null)
        {
            opts.AddSigningCertificate(signingCert);
            opts.AddEncryptionCertificate(signingCert);
        }
        else
        {
            // Dev / test path: ephemeral keys regenerated each boot. Prod is guarded above.
            opts.AddDevelopmentSigningCertificate();
            opts.AddDevelopmentEncryptionCertificate();
        }

        // Identity scopes stay registered for the future OIDC provider; korat:mcp is the
        // inc-2a resource scope (SF-7). offline_access (MF-2) is registered here so it CAN be
        // granted server-side by the consent handler (Task 4) — OpenIddict only issues a
        // refresh token when the sign-in principal carries offline_access; it is NEVER
        // requested by the client, so Task 3's request-scope policy still rejects
        // openid/email/profile. scopes_supported advertises all five — MCP clients ignore
        // the extras, and the consent handler rejects identity scopes for MCP clients.
        opts.RegisterScopes(
            OpenIddictConstants.Scopes.OpenId,
            OpenIddictConstants.Scopes.Email,
            OpenIddictConstants.Scopes.Profile,
            OpenIddictConstants.Scopes.OfflineAccess,
            Korat.Cloud.Web.Oauth.KoratOAuthConstants.McpScope);

        // Pin the issuer to the canonical public origin when configured (Fly deploys always
        // set Korat:Cli:PublicOrigin — the SEC-MED guard below enforces it). Off-config
        // (dev/tests) the issuer derives from the request host, matching
        // McpOAuthConnectActionBuilder.ResolveOrigin's fallback.
        var openIddictIssuer = builder.Configuration["Korat:Cli:PublicOrigin"];
        if (!string.IsNullOrWhiteSpace(openIddictIssuer))
            opts.SetIssuer(new Uri(openIddictIssuer, UriKind.Absolute));

        // Space-MCP inc-2b (Task 2): advertise the RFC 7591 registration_endpoint in the RFC 8414
        // AS-metadata document so a DCR-capable MCP client auto-discovers /connect/register.
        // OpenIddict has no built-in registration endpoint, so we inject it via the discovery
        // pipeline's Metadata bag (verified grounding #5). Gated on the DCR kill switch: when
        // disabled, the field is omitted (and the endpoint 404s), so discovery and enforcement
        // stay consistent. Built from context.Issuer so it matches every other endpoint URL in
        // the same document byte-for-byte.
        {
            var dcrEnabled = (builder.Configuration.GetSection("Korat:Cloud:SpaceMcpDcr")
                .Get<Korat.Cloud.Web.Oauth.SpaceMcpDcrOptions>() ?? new Korat.Cloud.Web.Oauth.SpaceMcpDcrOptions()).Enabled;
            if (dcrEnabled)
            {
                // Plan-review correction N4: an explicit .SetOrder(...) below is REQUIRED, not
                // cosmetic. Without it, a custom-added handler defaults to Order=0 (verified by
                // decompiling OpenIddictServerHandlerDescriptor.Builder — the private _order
                // field's default). OpenIddict's own built-in Discovery handlers for this SAME
                // context run at Order ~2.1 BILLION (AttachIssuer = 2_147_383_647, stepping
                // +1000 per handler up to AttachAdditionalMetadata = 2_147_396_647 — verified via
                // a throwaway net10.0 probe against the installed 7.5.0 package). Ascending order
                // runs first, so an unordered handler at 0 would run BEFORE AttachIssuer
                // populates context.Issuer, silently skipping the `if (context.Issuer is { }
                // issuer)` guard below and omitting registration_endpoint from every response.
                // Ordering after the LAST built-in attach handler guarantees Issuer (and
                // everything else in the document) is already populated when this handler runs.
                // Р31: refresh-token reuse detection — see RefreshTokenReuseDetector for what the
                // resulting audit record does and does not prove.
                opts.AddEventHandler(Korat.Cloud.Web.Oauth.RefreshTokenReuseDetector.Descriptor);

                opts.AddEventHandler<OpenIddict.Server.OpenIddictServerEvents.HandleConfigurationRequestContext>(handler =>
                    handler.UseInlineHandler(context =>
                    {
                        if (context.Issuer is { } issuer)
                            // Literal "registration_endpoint": OpenIddict 7.5.0 ships NO DCR, so
                            // OpenIddictConstants.Metadata has NO RegistrationEndpoint constant
                            // (verified against the decompiled constants). The wire key is the
                            // RFC 7591/8414 standard string, pinned by the test's EndsWith assertion.
                            context.Metadata["registration_endpoint"] =
                                new OpenIddict.Abstractions.OpenIddictParameter(
                                    new Uri(issuer, Korat.Cloud.Web.Oauth.KoratOAuthConstants.RegistrationEndpointPath).AbsoluteUri);
                        return default;
                    }).SetOrder(OpenIddict.Server.OpenIddictServerHandlers.Discovery.AttachAdditionalMetadata.Descriptor.Order + 1000));
            }
        }

        var openIddictAspNetCore = opts.UseAspNetCore();
        // Inc-2a: /connect/authorize flows through to KoratAuthorizeEndpoints' minimal-API
        // consent handler (Task 3). The TOKEN endpoint stays passthrough-FREE: OpenIddict's
        // built-in Exchange.AttachPrincipal reuses the code/refresh principal (verified
        // grounding #3), and the consent identity is already complete.
        openIddictAspNetCore.EnableAuthorizationEndpointPassthrough();
        if (builder.Environment.IsDevelopment() || isTesting)
        {
            // The integration-test HttpClient (and local Kestrel) runs plain HTTP; OpenIddict
            // rejects non-HTTPS endpoints by default. Prod stays HTTPS-only (flag not set).
            openIddictAspNetCore.DisableTransportSecurityRequirement();
        }
    })
    .AddValidation(opts =>
    {
        // Inc-2a resource-server side (spec §Pillar C "UseLocalServer"): shares the local
        // server's signing/encryption credentials + EF token stores, so reference access
        // tokens validate (and die on revocation) in-process. Consumed DIRECTLY via
        // OpenIddictValidationService.ValidateAccessTokenAsync in SpaceMcpAuth (Task 6) —
        // no ASP.NET authentication-scheme registration (this app does manual auth;
        // UseAspNetCore() is deliberately omitted). No AddAudiences(): the audience check is
        // dynamic per-Space and OURS (BLOCKER-1), not a static allow-list.
        opts.UseLocalServer();
    });

builder.Services.AddSingleton<IMetadataRepository, EfMetadataRepository>();

// Auth services (Tasks 4-8 implementations + Task 14 canonical handler + Task 9 security utils).
builder.Services.AddScoped<IUserProvisioningService, UserProvisioningService>();
builder.Services.AddScoped<ISessionService, SessionService>();
builder.Services.AddScoped<ICliTokenService, CliTokenService>();
// Проверка токенов, выданных провайдером входа. Singleton: держит кеш ключей JWKS и
// обновляет его сам, поэтому создавать его на запрос значило бы ходить за документом
// обнаружения на каждое обращение.
builder.Services.AddSingleton<ISsoTokenValidator, SsoTokenValidator>();
// Связь «субъект SSO → человек здесь». Scoped: ходит в базу через KoratDbContext.
builder.Services.AddScoped<ISsoIdentityResolver, SsoIdentityResolver>();
builder.Services.AddScoped<IDeviceCodeStore, GrainDeviceCodeStore>();
builder.Services.AddScoped<IMagicLinkService, MagicLinkService>();
builder.Services.AddSingleton<IPendingLinkService, PendingLinkService>();
builder.Services.AddSingleton<IOAuthStateProtector, OAuthStateProtector>();
builder.Services.AddScoped<CanonicalSigninHandler>();
builder.Services.AddHttpClient<IEmailSender, ResendEmailSender>();
builder.Services.AddHttpClient<IEmailChangeEmailSender, ResendEmailChangeEmailSender>();
builder.Services.AddScoped<IEmailChangeService, EmailChangeService>();
builder.Services.Configure<ResendOptions>(
    builder.Configuration.GetSection(ResendOptions.SectionName));

// SEC-HIGH: in any non-Development / non-Testing environment the Resend API key must be
// present; without it no email-change verification links are sent, breaking the feature.
// Fail-fast mirrors the SEC-HIGH patterns for connection-string and OAuth credentials.
var resendApiKeyCheck = builder.Configuration["Resend:ApiKey"];
var resendFromEmailCheck = builder.Configuration["Resend:FromEmail"];
if (!builder.Environment.IsDevelopment() && !isTesting
    && (string.IsNullOrWhiteSpace(resendApiKeyCheck)
        || string.IsNullOrWhiteSpace(resendFromEmailCheck)))
{
    throw new InvalidOperationException(
        "Resend:ApiKey and Resend:FromEmail must be set in non-Development / non-Testing environments. " +
        "Without them no verification emails (magic-link, email-change) are sent. " +
        "Set Resend__ApiKey and Resend__FromEmail on the host.");
}

// Antiforgery — pre-session POST /signin/magic-link uses it for defence-in-depth.
// Header name + cookie names configured to match the SPA's X-XSRF-TOKEN convention.
builder.Services.AddAntiforgery(opts =>
{
    opts.HeaderName = "X-XSRF-TOKEN";
    opts.Cookie.Name = "__Secure-korat_xsrf";
    // This cookie holds the COOKIE token — the SPA must NOT read it. The REQUEST token is
    // exposed separately in the readable XSRF-TOKEN cookie (see IssueAntiforgery).
    opts.Cookie.HttpOnly = true;
    opts.Cookie.SecurePolicy = CookieSecurePolicy.Always;
    // Lax, NOT Strict: the OAuth consent page (GET /connect/authorize) is opened as a top-level
    // navigation initiated by an EXTERNAL application (an MCP client like Claude Code/Cursor
    // spawns the browser). Browsers withhold SameSite=Strict cookies on externally-initiated
    // navigations, so with Strict this cookie is NOT sent on the consent GET → GetAndStoreTokens
    // sees no cookie token, mints a fresh one and Set-Cookies it EVERY attempt → each new attempt
    // rotates the stored token and silently invalidates the hidden __RequestVerificationToken of
    // every older consent tab (→ antiforgery-failure, the "click Allow several times" bug). Lax IS
    // sent on top-level GET navigations, so the token stays stable across attempts. Security is
    // unchanged: the synchronizer (double-submit) token is the CSRF defense — an attacker still
    // cannot read the request token to forge a matching pair — SameSite only mitigates automatic
    // cookie attachment, which Lax already covers for the state-changing POST (a cross-site POST
    // does NOT carry a Lax cookie).
    opts.Cookie.SameSite = SameSiteMode.Lax;
});

// SEC-H1/L1: TrustForwardedIp gates two behaviours:
//   1. app.UseForwardedHeaders() (below in the pipeline) — rewrites
//      RemoteIpAddress + Request.Scheme from X-Forwarded-For + X-Forwarded-Proto.
//   2. RateLimiterRegistration.ResolveClientIp — reads Fly-Client-IP only when
//      this flag is true; otherwise falls back to RemoteIpAddress.
//
// Set to true ONLY on deployments where all inbound traffic is forced through
// Fly's edge proxy. In Development / direct-host scenarios this flag must stay
// false; spoofing Fly-Client-IP from outside the proxy is trivial otherwise.
//
// appsettings.json: "Korat": { "Cloud": { "TrustForwardedIp": false } }
// Fly deploy: set via KORAT__CLOUD__TRUSTFORWARDEDIP=true in fly.toml [env].
var trustForwardedIp = builder.Configuration.GetValue<bool>("Korat:Cloud:TrustForwardedIp");

if (trustForwardedIp)
{
    builder.Services.Configure<ForwardedHeadersOptions>(opts =>
    {
        opts.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
        // Accept exactly one hop — the Fly edge proxy.
        opts.ForwardLimit = 1;
        // Clear the broad ASP.NET default (any RFC1918 range); only trust Fly's
        // internal IPv6-mapped range (::ffff:172.16.0.0/12 covers the Fly edge
        // addresses that appear as RemoteIpAddress on the loopback interface).
        opts.KnownIPNetworks.Clear();
        opts.KnownProxies.Clear();
        opts.KnownIPNetworks.Add(new System.Net.IPNetwork(IPAddress.Parse("::ffff:172.16.0.0"), 108)); // /108 = /12 IPv4 mapped
        opts.KnownIPNetworks.Add(new System.Net.IPNetwork(IPAddress.Parse("172.16.0.0"), 12));          // plain IPv4 fallback
    });
}

// Space-MCP inc-2b (Task 4) + registration-flood-DoS hardening item 3: thread the DCR per-IP
// permit (SpaceMcpDcrOptions.RegisterRateLimitPerMinute) AND the per-subnet permit
// (RegisterSubnetRateLimitPerMinute) into the rate-limiter registration so isolated test hosts
// can dial either via config (WithWebHostBuilder + ConfigureAppConfiguration) — same pattern as
// every other DCR bound. NOTE (see DcrBoundsTests.PerIpRateLimit_Exceeded_Returns429's "Reality
// note"): this is an EAGER read against builder.Configuration, executed BEFORE builder.Build()
// runs — a WithWebHostBuilder ConfigureAppConfiguration override never reaches it, because that
// override is only merged into configuration when Build() executes, after this line has already
// run. Both permits below inherit that same, already-accepted limitation.
var spaceMcpDcrRateLimitOptions =
    builder.Configuration.GetSection("Korat:Cloud:SpaceMcpDcr").Get<Korat.Cloud.Web.Oauth.SpaceMcpDcrOptions>()
        ?? new Korat.Cloud.Web.Oauth.SpaceMcpDcrOptions();
var dcrRegisterPerMinute = spaceMcpDcrRateLimitOptions.RegisterRateLimitPerMinute;
var dcrRegisterSubnetPerMinute = spaceMcpDcrRateLimitOptions.RegisterSubnetRateLimitPerMinute;
builder.Services.AddKoratRateLimiting(
    trustForwardedIp, builder.Environment.IsEnvironment("Testing"), dcrRegisterPerMinute, dcrRegisterSubnetPerMinute);

// 005-mvp-relay-minimal: in-process registry that routes RelayFrames between agent and
// publisher nodes for the lifetime of a session. Singleton because both NodeGatewayService
// instances (one per Connect call) must share the same routing state.
builder.Services.AddSingleton<Korat.Cloud.Gateways.SessionRoutingTable>();
// Step-A: shared teardown service (revoke/delete → close live sessions). Singleton so it is
// resolvable from both the gRPC gateway and the minimal-API endpoints over the same routing state.
builder.Services.AddSingleton<Korat.Cloud.Gateways.SessionTerminator>();
// 030 (push-to-wake): wake coordinator — singleton next to SessionRoutingTable.
// Holds per-silo dedup state. Sends APNs silent pushes and polls NodeGrain.Status.
// Degrades gracefully when APNs is unconfigured (NullPushWakeSender → no added latency).
// ClusterNodeGrainLocator is the production adapter that wraps IClusterClient.GetGrain;
// test environments can substitute a lightweight stub instead of mocking IClusterClient.
builder.Services.AddSingleton<Korat.Cloud.Push.INodeGrainLocator, Korat.Cloud.Push.ClusterNodeGrainLocator>();
builder.Services.AddSingleton<Korat.Cloud.Push.NodeWakeCoordinator>();
// Space-MCP increment 1, Task 2 (BLOCKER-3): the shared session-admission gauntlet extracted from
// NodeGatewayService.HandleRequestSessionAsync. Singleton — stateless aside from its injected
// singletons (SessionRoutingTable / NodeWakeCoordinator), shared by the gRPC gateway (NodeTofu)
// and, from Task 4 onward, the Space-MCP aggregator grain (ServerMinted).
builder.Services.AddSingleton<Korat.Cloud.Gateways.Admission.ISessionAdmission, Korat.Cloud.Gateways.Admission.SessionAdmission>();

// 031 (mobile-push increment 2): access-request push notify — fan out to the Space owner's
// push-enabled devices when CreateAccessRequestWithStatusAsync produces a NEW pending request
// (never the idempotent replay). ClusterAccessRequestGrainLocator mirrors
// ClusterNodeGrainLocator — production adapter over IClusterClient; tests substitute a fake.
builder.Services.Configure<Korat.Cloud.Push.AccessRequestNotifyOptions>(
    builder.Configuration.GetSection(Korat.Cloud.Push.AccessRequestNotifyOptions.SectionName));
builder.Services.AddSingleton<Korat.Cloud.Push.IAccessRequestGrainLocator, Korat.Cloud.Push.ClusterAccessRequestGrainLocator>();
builder.Services.AddSingleton<Korat.Cloud.Push.AccessRequestNotifier>();

builder.Services.AddScoped<Korat.Cloud.Web.Spaces.SpaceSlugService>();
builder.Services.AddSingleton<Korat.Cloud.Web.Spaces.SsrfGuardedHttpClientFactory>();
// IOutboundHttpClientFactory: seam over the SSRF-guarded factory (PointInvoker testability).
builder.Services.AddSingleton<Korat.Domain.IOutboundHttpClientFactory>(
    sp => sp.GetRequiredService<Korat.Cloud.Web.Spaces.SsrfGuardedHttpClientFactory>());
// SSRF guard: SystemSsrfDnsResolver is a thin DNS wrapper; SsrfGuardedHttpClientFactory above
// creates SSRF-guarded HttpClients with ConnectCallback rebinding defense.
builder.Services.AddSingleton<ISsrfDnsResolver, SystemSsrfDnsResolver>();
builder.Services.AddMemoryCache();

// ── Space-MCP: endpoint, OAuth client config, DCR bounds ────────────────────
// SpaceMcpOptions binds "Korat:Cloud:SpaceMcp:AllowedOrigins" (Origin allow-list, S3) as a plain
// singleton record rather than a full IOptions<T> ceremony.
builder.Services.AddSingleton(
    builder.Configuration.GetSection("Korat:Cloud:SpaceMcp").Get<SpaceMcpOptions>()
        ?? new SpaceMcpOptions());
// The Streamable-HTTP responder (POST/GET/DELETE /mcp/{spaceSeg}). Scoped: it depends on the
// same scoped SpaceSlugService/ICliTokenService per-request lifetime.
builder.Services.AddScoped<SpaceMcpDispatcher>();
// The pre-registered MCP OAuth client config. Same plain-singleton binding style as above.
builder.Services.AddSingleton(
    builder.Configuration.GetSection("Korat:Cloud:SpaceMcpOAuth").Get<SpaceMcpOAuthOptions>()
        ?? new SpaceMcpOAuthOptions());
// DCR bounds. Registered as a FACTORY (not an eagerly-evaluated `builder.Configuration.Get<T>()`
// expression like the plain singletons above) so it resolves against the DI-registered
// IConfiguration — the fully-merged configuration — rather than a snapshot of
// `builder.Configuration` taken at this exact line, which runs BEFORE WithWebHostBuilder's
// ConfigureAppConfiguration overrides are appended. An eager read here is invisible to
// `fixture.Factory.WithWebHostBuilder(b => b.ConfigureAppConfiguration(...))`, the isolated-host
// pattern the DCR bounds tests depend on. Identical result in production (one build, no
// override) — this is purely a testability fix.
builder.Services.AddSingleton(sp =>
    sp.GetRequiredService<IConfiguration>().GetSection("Korat:Cloud:SpaceMcpDcr")
        .Get<SpaceMcpDcrOptions>()
        ?? new SpaceMcpDcrOptions());
// Registration-flood-DoS hardening: the PRIMARY /connect/register capacity gate counts
// UNCONSENTED DCR clients only (KoratDbContext is request-scoped, so this counter is scoped too:
// one EF query per request/handler invocation).
builder.Services.AddScoped<IUnconsentedDcrClientCounter, UnconsentedDcrClientCounter>();
// Throttles the operator-visible LogWarning DcrEndpoints emits when a cap gate trips. Singleton —
// its whole purpose is one shared "last logged" clock per gate across every /connect/register.
builder.Services.AddSingleton<DcrCapWarningThrottle>();

// ── HTTP MCP OAuth: discovery + dynamic client registration ─────────────────
// RFC 9728/8414 discovery — SsrfGuard-validated at use time on every fetched URL (discovery can
// name a host different from RemoteUrl).
builder.Services.AddSingleton<Korat.Cloud.Mcp.Oauth.McpOAuthDiscoveryService>();
// RFC 7591 dynamic client registration against the discovered registration_endpoint (or manual
// client_id/client_secret fallback, handled by the caller).
builder.Services.AddSingleton<Korat.Cloud.Mcp.Oauth.McpOAuthClientRegistrar>();

// ── Envelope encryption (per-Space DEK + AES-256-GCM) ───────────────────────
// STARTUP VALIDATION: ValidateOnStart calls EnvelopeOptionsValidator before the host accepts
// traffic, failing fast on bad base64 / wrong key length / dotted kekId / ActiveKekId not in Keks.
builder.Services
    .AddOptions<Korat.Cloud.Security.Envelope.EnvelopeOptions>()
    .Bind(builder.Configuration.GetSection(Korat.Cloud.Security.Envelope.EnvelopeOptions.SectionKey))
    .ValidateOnStart();
builder.Services.AddSingleton<
    Microsoft.Extensions.Options.IValidateOptions<Korat.Cloud.Security.Envelope.EnvelopeOptions>,
    Korat.Cloud.Security.Envelope.EnvelopeOptionsValidator>();
// KEK custody seam — ConfigKekProvider (KEK bytes from Fly-secret config) is the default; an
// external-KMS provider becomes a pure DI swap.
builder.Services.AddSingleton<Korat.Cloud.Security.Envelope.IKekProvider, Korat.Cloud.Security.Envelope.ConfigKekProvider>();
builder.Services.AddSingleton<Korat.Cloud.Security.Envelope.SpaceDekProvider>();
// The generalized envelope primitive (per-space DEK + AES-256-GCM, caller-supplied record AAD).
// The interface lives in Korat.Domain.Persistence so Korat.Grains can consume it too, without
// depending on this Cloud host app. Singleton: stateless — shared state lives in SpaceDekProvider.
builder.Services.AddSingleton<IEnvelopeCrypto, Korat.Cloud.Web.Security.EnvelopeCrypto>();

// 032 C1 (#57 Leg 3): tamper-evident audit log. AuditLogger is a SINGLETON (IDbContextFactory
// + IHttpContextAccessor only — no scoped deps) so singleton services (SpaceDekProvider) and
// scoped endpoints/services share one implementation. Fail policy: fail-CLOSED for privileged
// mutations, fail-open + GlitchTip alarm for the hot-path secret.decrypt (032 plan §1.4).
builder.Services.AddHttpContextAccessor();
builder.Services.AddSingleton<Korat.Cloud.Security.Audit.IAuditLog, Korat.Cloud.Security.Audit.AuditLogger>();
builder.Services.AddScoped<Korat.Cloud.Security.Audit.AuditVerifier>();

// F1: gRPC MaxReceiveMessageSize must be at least the advertised per-message relay limit so
// legitimate large frames are not transport-rejected below the policy cap. The default gRPC
// receive limit is 4 MB, which is smaller than the advertised 16 MB; frames between 4 MB and
// 16 MB would be ungracefully rejected by the transport before the enforcement path can emit
// PayloadLimitExceeded. Set to the same constant used in PayloadLimitPolicy so the values stay
// in sync. gRPC overhead (framing, metadata) is negligible; this constant is the payload floor.
const int GrpcMaxReceiveMessageSize = (int)PayloadLimitPolicy.DefaultPerMessageBytes;

if (isTesting)
{
    builder.Services.AddGrpc(o => o.MaxReceiveMessageSize = GrpcMaxReceiveMessageSize);
}
else
{
    builder.Services.AddGrpc(o => o.MaxReceiveMessageSize = GrpcMaxReceiveMessageSize);
    // Skipped when IClusterClient is pre-registered (a test host may inject a TestCluster client for isolation).
    if (!builder.Services.Any(d => d.ServiceType == typeof(Orleans.IClusterClient)))
    {
        builder.Host.UseOrleans(siloBuilder =>
        {
            // ── Clustering ───────────────────────────────────────────────────────
            // Gated on FLY_PRIVATE_IP:
            //
            //   on Fly  → ADO.NET (PostgreSQL) cluster membership + 6PN endpoint advertisement.
            //   off Fly → localhost clustering (local dev, CI tests).
            //
            // FLY_PRIVATE_IP is the Fly private IPv6 6PN address (fdaa:…) injected by the
            // Fly runtime. Ports 11111 (silo-to-silo) and 30000 (client gateway) ride the
            // private 6PN network — they are NOT exposed via fly.toml [services] blocks.
            if (clustered)
            {
                siloBuilder.UseAdoNetClustering(o =>
                {
                    o.Invariant = "Npgsql";
                    o.ConnectionString = connectionString;
                });
                siloBuilder.Configure<Orleans.Configuration.ClusterOptions>(o =>
                {
                    // ClusterId and ServiceId must be identical on every silo instance
                    // so Orleans treats them as members of the same cluster. ClusterId is the
                    // ADO.NET membership DeploymentId; dev and prod use separate databases so
                    // each environment MUST carry a distinct ClusterId (e.g. "korat-dev" vs
                    // the Fly app name for prod). Derive from config first, fall back to the
                    // Fly app name (FLY_APP_NAME), then a safe local default.
                    o.ClusterId = builder.Configuration["Korat:Cloud:ClusterId"]
                        ?? Environment.GetEnvironmentVariable("KORAT_CLUSTER_ID")
                        ?? Environment.GetEnvironmentVariable("FLY_APP_NAME")
                        ?? "korat-dev";
                    o.ServiceId = builder.Configuration["Korat:Cloud:ServiceId"] ?? "korat";
                });
                // The listener MUST bind the same address family we advertise. Orleans'
                // ConfigureEndpoints(listenOnAnyHostAddress: true) binds IPv4 Any (0.0.0.0)
                // regardless of the advertised family, so a peer dialling our advertised
                // address finds nothing listening → the cluster connectivity check fails and
                // the joining silo crashes.
                //
                // Fly's 6PN is IPv6-only (fdaa:…); a Kubernetes podIP is normally IPv4. Deriving
                // the bind address from the advertised one covers both without a host-specific
                // branch. Note this failure mode is invisible on a single silo: one instance
                // never dials itself, so a wrong family only surfaces when the SECOND one joins.
                var listenAny = advertisedIp!.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6
                    ? IPAddress.IPv6Any
                    : IPAddress.Any;
                siloBuilder.Configure<Orleans.Configuration.EndpointOptions>(o =>
                {
                    o.AdvertisedIPAddress = advertisedIp!;
                    o.SiloPort = 11111;
                    o.GatewayPort = 30000;
                    o.SiloListeningEndpoint = new System.Net.IPEndPoint(listenAny, 11111);
                    o.GatewayListeningEndpoint = new System.Net.IPEndPoint(listenAny, 30000);
                });
            }
            else
            {
                // Local dev / testing / single-instance: unchanged behaviour.
                siloBuilder.UseLocalhostClustering();
            }

            // In-memory grain storage on every environment (Fly + local). No grain
            // declares [PersistentState]/IPersistentState against the "korat" store:
            // device-flow attempts are intentionally ephemeral (a pending attempt lives
            // in grain instance state and is fine to lose on a rolling deploy), and the
            // authorized outcome is persisted via EF (AgentClients table), NOT grain
            // storage. AddAdoNetGrainStorage was tried here but its Init() runs for every
            // registered provider at silo startup and hard-fails (.Single() over an empty
            // OrleansQuery) because the Orleans persistence schema is never applied to the
            // DB — crashing the silo before Kestrel binds. Memory storage avoids that and
            // matches the actual (zero) durable-state requirement.
            siloBuilder.AddMemoryGrainStorage("korat");
            // Rolling-deploy safety: during a rolling restart both old and new silos are live,
            // so a grain call can hit either. BackwardCompatible + AllCompatibleVersions lets calls
            // resolve against any compatible activation (these match Orleans' current defaults, but
            // we set them explicitly to document the contract and stay safe if defaults change).
            // See .github/workflows/DEPLOY.md "Orleans cluster update rule".
            siloBuilder.Configure<Orleans.Configuration.GrainVersioningOptions>(o =>
            {
                o.DefaultCompatibilityStrategy = nameof(Orleans.Versions.Compatibility.BackwardCompatible);
                o.DefaultVersionSelectorStrategy = nameof(Orleans.Versions.Selector.AllCompatibleVersions);
            });
            siloBuilder.ConfigureServices(services =>
            {
                services.AddSerializer(serializerBuilder =>
                    serializerBuilder.AddJsonSerializer(isSupported: type => type.Namespace?.StartsWith("Korat") == true));
                services.AddSingleton<IMetadataRepository, EfMetadataRepository>();
            });
            // GlitchTip CodecNotFoundException fix: wrap EVERY grain invocation so a third-party
            // data-store exception (Npgsql/DbException/EF DbUpdateException) escaping a grain on a
            // DB blip is translated to a serializable KoratDomainException instead of making Orleans
            // throw CodecNotFoundException trying to serialize the foreign type. The filter logs the
            // original exception once (as itself) and is registered last so it wraps all grain calls.
            siloBuilder.AddIncomingGrainCallFilter<DataExceptionTranslationFilter>();
        });
        // G2: register stable gateway on startup so IGatewayGrain is pre-registered before
        // any node connects and RequestSession tries to look it up.
        builder.Services.AddHostedService<Korat.Cloud.Gateways.GatewayRegistrationService>();
        // 024: background reaper that hard-deletes Published servers whose owner node has been
        // offline beyond the purge horizon (catalog hygiene). Idempotent + best-effort.
        builder.Services.AddHostedService<Korat.Cloud.Maintenance.McpServerReaperService>();
        // Space-MCP inc-2b (Task 6): TTL sweep of never-consented (and revoked/expired-only,
        // MF-3) DCR registrations (open-DCR row-growth bound). Best-effort + idempotent;
        // SweepAsync is invoked directly by tests.
        builder.Services.AddHostedService<Korat.Cloud.Maintenance.DcrRegistrationReaperService>();
        // Step-C: background reaper that persists Closed for long-stale Active/Opening sessions
        // whose client/publisher node has been offline beyond the grace horizon (hygiene).
        builder.Services.AddHostedService<Korat.Cloud.Maintenance.SessionReaperService>();
        // 032 C1: audit chain-head external anchoring (6 h + shutdown) and 400-day retention
        // prune (daily, writes a chained checkpoint so verification survives pruning).
        builder.Services.AddHostedService<Korat.Cloud.Maintenance.AuditAnchorService>();
        builder.Services.AddHostedService<Korat.Cloud.Maintenance.AuditPruneService>();
    }
}

builder.Services.AddScoped<IAuthResolver, PolymorphicAuthResolver>();
// G3: maps identity-resolved UserId to the user's default SpaceId (design §3.3).
builder.Services.AddScoped<SpaceResolver>();

// SEC-MED: startup guard for CLI device-flow host-header phishing.
// When Korat:Cli:PublicOrigin is unset in a non-Dev / non-Testing environment,
// /api/auth/cli/device-code builds verification_uri from the client-supplied Host header.
// A poisoned Host header then makes the CLI print, and auto-open, an attacker-controlled URL
// as the approval page — allowing a phishing attack on the user_code confirmation step.
// Mitigate: require PublicOrigin to be configured in production-like environments.
var cliPublicOrigin = builder.Configuration["Korat:Cli:PublicOrigin"];
if (!builder.Environment.IsDevelopment() && !isTesting
    && string.IsNullOrWhiteSpace(cliPublicOrigin))
{
    throw new InvalidOperationException(
        "Korat:Cli:PublicOrigin must be set in non-Development / non-Testing environments. " +
        "Set it to the canonical public base URL of this Korat Cloud instance " +
        "(e.g. 'https://my.korat.ai') to prevent host-header injection in the CLI device flow. " +
        "See the deploy runbook for details.");
}

var app = builder.Build();

// Половина настройки SSO должна ронять ВЫКЛАДКУ, а не каждый запрос. Проверка живёт в
// конструкторе валидатора, но он singleton и на старте никем не резолвится: приложение
// поднималось бы с Sso:Issuer без Sso:AllowedClients, выкладка проходила бы зелёной, а
// падал бы каждый аутентифицируемый запрос пятисоткой. У GitHub и Google такие
// предохранители на старте есть — у входа, который станет главным, не было.
_ = app.Services.GetRequiredService<Korat.Cloud.Web.Auth.Services.ISsoTokenValidator>();

// ── Migration safety ────────────────────────────────────────────────────────
// Run migrations only when it is safe to do so:
//
//   not clustered (local / CI)      → migrate on every boot — one process, no race.
//   clustered, KORAT_RUN_MIGRATIONS=1 → migrate (the one designated release step/job).
//   clustered, flag absent          → skip (normal instances; the release step already ran).
//
// The gate is `clustered`, not the hosting provider: what makes an unattended migration
// unsafe is more than one process racing to apply it, and that is exactly what clustering
// means. The one designated migrator is a Kubernetes Job running this same image with the
// `migrate` argument; the Deployment waits on that Job before rolling.
var migrateOnly = args.Contains("migrate");
var runMigrations = migrateOnly
    || !clustered
    || Environment.GetEnvironmentVariable("KORAT_RUN_MIGRATIONS") == "1";

if (runMigrations)
{
    using var scope = app.Services.CreateScope();
    var repository = scope.ServiceProvider.GetRequiredService<IMetadataRepository>();
    await repository.EnsureCreatedAsync();

    // Orleans ADO.NET membership tables live outside the EF model — apply the official
    // PostgreSQL clustering schema idempotently. Must exist before any silo joins; runs in
    // the same operator-controlled migration window as EF to avoid a multi-instance race.
    if (clustered)
        await Korat.Cloud.Clustering.OrleansAdoNetSchema.EnsureAsync(connectionString);
}

// Exit before binding any listener. Without this the migration Job would go on to serve
// traffic and never reach Completed, so the Deployment that waits on it would never roll.
if (migrateOnly)
{
    app.Logger.LogInformation("migrate: schema applied, exiting without starting listeners");
    return;
}

// Space-MCP inc-2a (Task 1): idempotent upsert of the single pre-registered MCP OAuth client.
// Runs every boot (not only when migrations run) so a config change to the redirect URIs
// converges on the next deploy; skips with a warning when no redirect URIs are configured.
await Korat.Cloud.Web.Oauth.SpaceMcpOAuthClientSeeder.SeedAsync(
    app.Services, app.Logger, CancellationToken.None);

app.Use(async (context, next) =>
{
    try
    {
        await next();
    }
    catch (KoratDomainException ex)
    {
        context.Response.StatusCode = ex.Code == KoratErrorCode.NotFound ? StatusCodes.Status404NotFound : StatusCodes.Status400BadRequest;
        await context.Response.WriteAsync(KoratError.Message(ex.Code));
    }
});
// SEC-H1/L1: if the flag is on, rewrite RemoteIpAddress and Request.Scheme from
// X-Forwarded-For / X-Forwarded-Proto before any auth or rate-limit middleware
// inspects them. MUST run before UseRateLimiter, UseAuthentication, and any
// middleware that reads ctx.Connection.RemoteIpAddress or ctx.Request.Scheme.
if (trustForwardedIp)
{
    app.UseForwardedHeaders();
    // Belt-and-suspenders for the OAuth callback path. UseForwardedHeaders
    // intermittently does NOT apply X-Forwarded-Proto on /signin/{provider}/callback
    // — its KnownIPNetworks/ForwardLimit check rejects the Fly edge hop on that request,
    // so Request.Scheme stays "http" even though the header says "https". The OAuth
    // handler then builds an http:// redirect_uri at token-exchange while the authorize
    // step used https://, so GitHub rejects the exchange with redirect_uri_mismatch.
    // (Observed in production: authorize Scheme=https, callback Scheme=http with
    // XFProto=https present-but-unconsumed.) Force the scheme from the forwarded header
    // unconditionally; safe because this block only runs behind the trusted Fly proxy.
    app.Use((ctx, next) =>
    {
        var proto = ctx.Request.Headers["X-Forwarded-Proto"].ToString();
        if (proto.StartsWith("https", StringComparison.OrdinalIgnoreCase))
        {
            ctx.Request.Scheme = "https";
        }
        return next();
    });
}

// SEC-MED-1: emit baseline security response headers (X-Content-Type-Options, X-Frame-Options,
// Referrer-Policy, Content-Security-Policy) on all responses — including the static-files pipeline.
app.UseKoratSecurityHeaders();

// SEC-I3: serve static files BEFORE UseRateLimiter and UseAntiforgery.
// Static assets (/app/assets/*.js, *.css, fonts) are intentionally public and
// unauthenticated — running rate-limiter or antiforgery middleware on them
// wastes rate-limit budget on page-load asset bursts and adds unnecessary latency.
// There are no auth-protected static file paths, so early exit is safe.
app.UseDefaultFiles();
// SPA static assets: direct file-serving middleware for /app/* requests.
// UseStaticFiles() alone is insufficient because the default WebRootFileProvider in
// .NET 10 SDK projects is backed by the StaticWebAssets manifest
// (Korat.Cloud.staticwebassets.endpoints.json), which only lists files explicitly
// registered via MSBuild. Vite-emitted assets (index-*.js, index-*.css, fonts) are
// produced by an out-of-band BuildKoratApp target and are NOT in the manifest.
// As a result, the default file provider returns 404 for all SPA assets, causing
// MapFallback("/app/{*path}") to serve index.html with content-type: text/html.
//
// Fix: intercept /app/* requests early (before endpoint routing), resolve the physical
// path under wwwroot/app/, and serve the file directly if it exists.
// Guard: WebRootPath is null in test environments (WebApplicationFactory without UseWebRoot).
// In that case spaWebRoot is empty and the middleware becomes a no-op.
var spaWebRoot = app.Environment.WebRootPath is { } wr ? Path.Combine(wr, "app") : "";
var spaIndexPath = spaWebRoot.Length > 0 ? Path.Combine(spaWebRoot, "index.html") : "";
app.Use(async (context, next) =>
{
    var path = context.Request.Path.Value ?? "";
    if (spaWebRoot.Length > 0 && path.StartsWith("/app/", StringComparison.OrdinalIgnoreCase) && path.Length > 5)
    {
        // Strip the /app prefix to get the relative sub-path within wwwroot/app/.
        var subPath = path[4..]; // e.g. "/assets/index-Dk7bcs9i.js"
        var physicalPath = Path.Combine(spaWebRoot, subPath.TrimStart('/').Replace('/', Path.DirectorySeparatorChar));
        // SPA path containment guard: ensure the resolved path stays under spaWebRoot.
        // GetFullPath resolves any ".." segments so an attacker-crafted path like
        // "/app/../../etc/passwd" collapses before the prefix check.
        // Require the separator suffix so a sibling directory named spaWebRoot+"extra"
        // is not matched by the prefix check (sibling-prefix hardening).
        var resolvedPath = Path.GetFullPath(physicalPath);
        var containmentPrefix = spaWebRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!resolvedPath.StartsWith(containmentPrefix, StringComparison.OrdinalIgnoreCase)
            && !resolvedPath.Equals(spaWebRoot, StringComparison.OrdinalIgnoreCase))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }
        if (File.Exists(physicalPath))
        {
            var ext = Path.GetExtension(physicalPath).ToLowerInvariant();
            var contentType = ext switch
            {
                ".js"    => "application/javascript; charset=utf-8",
                ".css"   => "text/css; charset=utf-8",
                ".html"  => "text/html; charset=utf-8",
                ".svg"   => "image/svg+xml",
                ".woff"  => "font/woff",
                ".woff2" => "font/woff2",
                ".ico"   => "image/x-icon",
                ".png"   => "image/png",
                ".jpg"   => "image/jpeg",
                ".json"  => "application/json",
                _        => "application/octet-stream",
            };
            context.Response.ContentType = contentType;
            await context.Response.SendFileAsync(physicalPath);
            return;
        }
    }
    await next();
});
app.UseStaticFiles();

// Auth + rate-limiting middleware runs only for requests that static files did NOT serve.
// Dynamic API + SPA HTML routes (including /app/signin) run through this protected pipeline.
//
// UseAuthentication / UseAuthorization are called EXPLICITLY here rather than left to
// WebApplication's auto-registration. Auto-registered auth middleware runs EARLY in the
// pipeline — before app.UseForwardedHeaders() above — so the OAuth callback handler
// (RemoteAuthenticationHandler intercepts /signin/{provider}/callback) ran the token
// exchange before the forwarded X-Forwarded-Proto was applied, saw Request.Scheme=http,
// and built an http:// redirect_uri while the authorize step had used https://. GitHub
// then rejected the exchange with redirect_uri_mismatch. Calling them explicitly here —
// after the scheme fix — makes the callback handler observe the correct https scheme.
// Only UseAuthentication is needed — the OAuth RemoteAuthenticationHandler runs here and
// the callback handler observes the (now https) scheme. UseAuthorization is intentionally
// NOT called: this app does manual authorization (no AddAuthorization / policy services
// are registered), and calling UseAuthorization without them throws at startup.
//
// Space-MCP inc-2a (Task 2): permissive CORS for the public OAuth discovery documents
// (RFC 9728 PRM + RFC 8414 AS metadata) — browser-based MCP clients fetch them cross-origin
// (spec §Confidentiality "CORS on the well-known endpoints"). Hand-rolled middleware rather
// than a UseCors policy because the AS-metadata documents are produced INSIDE
// UseAuthentication (OpenIddict's ASP.NET host), which endpoint-scoped CORS never reaches.
// Public, credential-free, cache-safe JSON — '*' is correct here and nowhere else.
//
// Space-MCP inc-2b (Task 4, plan-review SF-1): extend the SAME permissive, credential-free CORS
// to POST /connect/register. This hand-rolled middleware is now the only CORS in the app — the
// single named policy (`waitlist`) went with the waitlist endpoint, so AddCors/UseCors are gone.
// /connect/register is a mapped minimal API, but it is unauthenticated by protocol
// (RFC 7591; no bearer/cookie) so a distinct named CORS policy would be pure ceremony. A
// browser-context DCR client (e.g. web claude.ai) preflights with Content-Type: application/json
// (not CORS-safelisted), so the allow-headers list gains "content-type" and the allow-methods
// list gains "POST" for this path only.
app.Use(async (ctx, next) =>
{
    var isWellKnown = ctx.Request.Path.StartsWithSegments("/.well-known");
    var isDcrRegister = ctx.Request.Path.StartsWithSegments(Korat.Cloud.Web.Oauth.KoratOAuthConstants.RegistrationEndpointPath);
    if (isWellKnown || isDcrRegister)
    {
        ctx.Response.Headers.AccessControlAllowOrigin = "*";
        if (HttpMethods.IsOptions(ctx.Request.Method))
        {
            ctx.Response.Headers.AccessControlAllowMethods = isDcrRegister ? "POST, OPTIONS" : "GET, OPTIONS";
            ctx.Response.Headers.AccessControlAllowHeaders = "authorization, mcp-protocol-version, content-type";
            ctx.Response.StatusCode = StatusCodes.Status204NoContent;
            return;
        }
    }
    await next();
});
app.UseAuthentication();
app.UseAntiforgery();
app.UseRateLimiter();
app.MapGet("/", () => Results.Redirect("/app"));

// Antiforgery for the SPA. GetAndStoreTokens sets the COOKIE token in __Secure-korat_xsrf
// (HttpOnly). The distinct REQUEST token must come back in the X-XSRF-TOKEN header — expose it
// to JS via a readable XSRF-TOKEN cookie. Echoing the cookie token (as the SPA did before)
// fails validation with "the cookie token and the request token were swapped".
void IssueAntiforgery(HttpContext ctx, IAntiforgery antiforgery)
{
    var tokens = antiforgery.GetAndStoreTokens(ctx);
    if (!string.IsNullOrEmpty(tokens.RequestToken))
    {
        // web-M4 minor: __Host- prefix hardens the XSRF cookie — the browser enforces
        // Secure + Path=/ + no Domain, preventing sub-domain injection even if a sub-domain
        // were ever compromised.  All three __Host- prerequisites are already satisfied here.
        ctx.Response.Cookies.Append("__Host-XSRF-TOKEN", tokens.RequestToken, new CookieOptions
        {
            HttpOnly = false, // SPA reads this to set the X-XSRF-TOKEN header
            Secure = true,
            SameSite = SameSiteMode.Strict,
            Path = "/",
        });
    }
}

// /app/signin: serve the SPA shell AND issue the antiforgery tokens so the sign-in page can
// attach X-XSRF-TOKEN to its POST /signin/magic-link call.
// Must be registered BEFORE MapFallback so it takes precedence over the catch-all.
app.MapGet("/app/signin", (HttpContext ctx, IAntiforgery antiforgery) =>
{
    IssueAntiforgery(ctx, antiforgery);
    // Guard: WebRootPath is null in test environments.
    var path = app.Environment.WebRootPath is { } webRoot
        ? Path.Combine(webRoot, "app", "index.html")
        : spaIndexPath;
    return Results.File(path, "text/html");
});

// SPA client-side routing fallback: any /app/* path that the static-files
// middleware did not match (no physical file) returns index.html so
// tanstack-router can handle client-side navigation (e.g. /app/grants).
app.MapFallback("/app/{*path}", (HttpContext ctx, IAntiforgery antiforgery) =>
{
    // Re-issue the antiforgery tokens on EVERY SPA shell load so the token is bound to the
    // CURRENT identity (the user lands on /app/* shell routes after login, not /app/signin).
    IssueAntiforgery(ctx, antiforgery);
    return File.Exists(spaIndexPath)
        ? Results.File(spaIndexPath, "text/html")
        : Results.NotFound("SPA not built. Run `dotnet build -c Release` or `npm run build` in apps/Korat.App.");
});
app.MapMagicLinkEndpoints();
app.MapAuthApiEndpoints();
// G1: comprehensive /health with per-component status (Postgres/NATS/Orleans).
Korat.Cloud.Web.HealthEndpoints.MapKoratHealth(app);
// 032 C2: admin ops — envelope rewrap / crypto-shred / audit verify+query (IsAdmin-gated).
Korat.Cloud.Web.Admin.AdminOpsEndpoints.MapAdminOpsEndpoints(app);
app.MapPendingLinkEndpoints();
app.MapSigninInitiationEndpoints();
app.MapSpaceOverviewEndpoints();
app.MapMetaEndpoints();
app.MapMcpServerEndpoints();
// Increment 2 (HTTP MCP OAuth), Task 4: POST .../{id}/reconnect + GET .../oauth/callback/{serverId}.
app.MapMcpOAuthEndpoints();
// Node-visibility-doctor design (2026-07-02): owner-editable note on a node.
app.MapNodeEndpoints();
app.MapAccessRequestEndpoints();
app.MapGrantEndpoints();
app.MapSessionEndpoints();
app.MapCliDeviceEndpoints();
app.MapCliTokenManagementEndpoints();
// Space-MCP increment 1, Task 7: POST/GET/DELETE /mcp/{spaceSeg} Streamable-HTTP responder.
// Anonymous at ASP.NET level — SpaceMcpDispatcher
// (via SpaceMcpAuth) validates the Bearer inside the handler on every request.
app.MapSpaceMcpEndpoints();
// Space-MCP inc-2a (Task 2): RFC 9728 protected-resource metadata, path-scoped per Space —
// anonymous, DB-free/anti-enumeration (see ProtectedResourceMetadataEndpoints' class doc).
app.MapProtectedResourceMetadataEndpoints();
// Space-MCP inc-2b (Task 4): open, bounded RFC 7591 DCR — POST /connect/register. Anonymous
// per protocol; per-IP rate-limited + row-capped + body/redirect-count/name-length-capped +
// redirect-URI-policed + korat:mcp-only.
app.MapDcrEndpoints();
// Space-MCP inc-2a (Task 3): /connect/authorize GET (validation chain + consent page) + POST
// (Task 4 accept/deny stub) — the OpenIddict authorization-endpoint passthrough handler.
app.MapKoratAuthorizeEndpoints();
// Space-MCP inc-2a (Task 8): owner console — list/revoke OAuth consents (token death +
// SF-6 live-session teardown + audit).
app.MapOAuthConsentEndpoints();
app.MapGrpcService<NodeGatewayService>();

app.Run();

// Fly's `flyctl postgres attach` (and many managed-PG providers) expose the
// connection as a URI: postgres://user:pass@host:5432/db. Npgsql expects key/value
// form. We convert lazily and only when the standard ConnectionStrings:Korat key
// isn't set — production wiring on Fly should use this fall-through.
static string? ConvertDatabaseUrlIfPresent(string? databaseUrl)
{
    if (string.IsNullOrWhiteSpace(databaseUrl))
        return null;
    if (!Uri.TryCreate(databaseUrl, UriKind.Absolute, out var uri))
        return null;
    if (uri.Scheme != "postgres" && uri.Scheme != "postgresql")
        return null;
    var userInfo = uri.UserInfo.Split(':', 2);
    var user = Uri.UnescapeDataString(userInfo[0]);
    var password = userInfo.Length > 1 ? Uri.UnescapeDataString(userInfo[1]) : string.Empty;
    var database = uri.AbsolutePath.TrimStart('/');
    var builder = new System.Text.StringBuilder();
    builder.Append("Host=").Append(uri.Host).Append(';');
    if (uri.Port > 0) builder.Append("Port=").Append(uri.Port).Append(';');
    builder.Append("Database=").Append(database).Append(';');
    builder.Append("Username=").Append(user).Append(';');
    builder.Append("Password=").Append(password).Append(';');
    // Fly MPG / Fly Postgres both require SSL on the public proxy hostname.
    // SSL Mode=Require + Trust Server Certificate=true keeps things working when the
    // managed cert chain isn't in the container's default trust store.
    builder.Append("SSL Mode=Require;Trust Server Certificate=true;");
    return builder.ToString();
}

public partial class Program;
