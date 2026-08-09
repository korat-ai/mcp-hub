using Korat.Cloud.Security.Envelope;
using Korat.Cloud.Web.Auth;
using Korat.Cloud.Web.Auth.Services;
using Korat.Cloud.Web.Security;
using Korat.Domain;
using Korat.Domain.Auth;
using Korat.Domain.Entities;
using Korat.Domain.Persistence;
using Korat.Persistence;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Orleans.Serialization;
using Orleans.TestingHost;
using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace Korat.Cloud.IntegrationTests;

/// <summary>
/// Shared state for the InMemory EF database used by integration tests.
///
/// IMPORTANT: <see cref="Root"/> and <see cref="Name"/> are static so that the
/// Orleans TestingHost silo configurator (instantiated by the framework via a
/// default constructor) can reach the same database root that the web host uses.
/// This static coupling is safe ONLY because <see cref="AssemblyInfo.cs"/> sets
/// <c>DisableTestParallelization = true</c> for this assembly — running fixtures
/// in parallel would cause them to overwrite each other's Name and corrupt data.
///
/// TODO (Phase 3): refactor to pass database config via Orleans configurator options
/// (ITestSiloBuilder.Properties bag) so state can move into KoratIntegrationFixture
/// as instance fields and remove the global-state dependency.
/// See tests/FOLLOWUPS.md: "static-state smell in KoratTestDatabase".
/// </summary>
public static class KoratTestDatabase
{
    // Shared root lets silo and web host see the same in-memory Postgres substitute.
    public static readonly InMemoryDatabaseRoot Root = new();
    // Unique name per fixture avoids cross-test pollution when silos recycle.
    public static string Name { get; set; } = "korat-integration-test";
}

/// <summary>
/// Increment 1 (HTTP MCP direct-to-Space), Finding 16 B4b: lets the Orleans TestingHost silo's
/// OWN DI container (SiloConfigurator.Configure, a SEPARATE IServiceCollection from the web
/// host's — see KoratTestDatabase's doc comment for the identical static-bridge precedent)
/// reach two things it does not register today but a grain activating inside it (HttpMcpProxyGrain)
/// needs: (1) the WEB HOST's actual SessionRoutingTable instance — not a fresh one — because
/// live agent bridge streams are registered against THAT instance by NodeGatewayService's gRPC
/// gateway (which runs in the web host), and a push from a grain in the silo must reach the SAME
/// instance to ever be deliverable; (2) a working IOutboundHttpClientFactory so the grain's
/// outbound HTTP calls to an in-process localhost stub MCP server succeed.
///
/// Ordering: KoratIntegrationFixture.InitializeAsync() builds+deploys the TestCluster (which is
/// when SiloConfigurator.Configure's service registrations are evaluated) BEFORE it constructs
/// the web host (`Factory = new KoratTestHost(_cluster.Client)`) — `RoutingTable` below is
/// therefore null at silo-registration time. This is safe ONLY because the registration below is
/// a lazy factory delegate (`services.AddSingleton&lt;T&gt;(sp => ...)`), not an eager instance —
/// the delegate is not INVOKED until something first resolves SessionRoutingTable, which for a
/// grain only happens on first activation, i.e. when a TEST METHOD calls into
/// IHttpMcpProxyGrain — always well after KoratIntegrationFixture.InitializeAsync() has finished
/// building both hosts and populated this bridge. Do not eagerly resolve SessionRoutingTable
/// anywhere in fixture setup — doing so would invoke the delegate before RoutingTable is set.
/// </summary>
internal static class HttpMcpProxyGrainTestBridge
{
    /// <summary>Set once, right after the web host is built, in KoratIntegrationFixture.InitializeAsync().</summary>
    public static Korat.Cloud.Gateways.SessionRoutingTable? RoutingTable { get; set; }
}

/// <summary>
/// Minimal IHostEnvironment stub for the Orleans test silo's DI container, which (unlike the
/// ASP.NET Core web host) has no ambient "Testing" environment name — SsrfGuardedHttpClientFactory
/// needs one to honor its AllowPrivateNetworks escape hatch. Deliberately a small local type
/// instead of Microsoft.Extensions.Hosting.Internal.HostingEnvironment (public, but documented as
/// "supports infrastructure ... not intended to be used directly").
/// </summary>
internal sealed class TestSiloHostEnvironment : Microsoft.Extensions.Hosting.IHostEnvironment
{
    public string EnvironmentName { get; set; } = "Testing";
    public string ApplicationName { get; set; } = "Korat.Cloud.IntegrationTests";
    public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
    public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } =
        new Microsoft.Extensions.FileProviders.NullFileProvider();
}

/// <summary>
/// PR-2 Task 3: a deterministic test KEK for the Orleans TEST SILO's own <c>IEnvelopeCrypto</c>
/// registration (see <c>KoratIntegrationFixture.SiloConfigurator</c>). Grains (ThreadGrain) run
/// inside a SEPARATE DI container from the ASP.NET Core web host (KoratTestHost) — the web
/// host's per-test KEK overrides (see EnvelopeEncryptionIntegrationTests.CreateEnvelopeFactory)
/// do NOT reach the silo, so grain-side envelope crypto needs its own always-on KEK here.
/// Isolated from prod/dev: this key never leaves the test process.
/// </summary>
/// <remarks>Public (not internal): Korat.Cloud.ContractTests' TelegramWebhookTests gives its
/// derived WEB HOST this same KEK so web-host-written ciphers (bot token) and grain-written
/// ciphers (thread messages) share one per-space DEK.</remarks>
public static class ThreadGrainTestKek
{
    public const string KekId = "test-thread-k1";
    public static readonly string KekBase64 = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
}

public sealed class KoratIntegrationFixture : IAsyncLifetime
{
    private TestCluster? _cluster;

    public KoratTestHost Factory { get; private set; } = default!;
    public IClusterClient ClusterClient => _cluster!.Client;
    public TestCluster Cluster => _cluster!;

    /// <summary>Convenience forward to the web host's DI container — Task 4/5's grain tests
    /// resolve IEnvelopeCrypto/IMetadataRepository/SessionRoutingTable through this.</summary>
    public IServiceProvider Services => Factory.Services;

    public async Task InitializeAsync()
    {
        KoratTestDatabase.Name = Guid.NewGuid().ToString("N");
        var builder = new TestClusterBuilder();
        builder.AddSiloBuilderConfigurator<SiloConfigurator>();
        builder.AddClientBuilderConfigurator<ClientConfigurator>();
        _cluster = builder.Build();
        await _cluster.DeployAsync();

        Factory = new KoratTestHost(_cluster.Client);
        _ = Factory.CreateClient();
        // Finding 16, B4b: populate the bridge now that the web host's SessionRoutingTable
        // singleton exists — see HttpMcpProxyGrainTestBridge's doc comment for why this exact
        // ordering (web host built, THEN bridge populated, BEFORE any test can reach a grain) is
        // both necessary and sufficient.
        HttpMcpProxyGrainTestBridge.RoutingTable =
            Factory.Services.GetRequiredService<Korat.Cloud.Gateways.SessionRoutingTable>();

        // Seed a default Space for the dev-fixture owner (DevSpaceOwnerUserId) so that
        // existing integration tests that authenticate via session as that user resolve
        // to a real SpaceId.
        await SeedLegacyOwnerSpaceAsync();
    }

    /// <summary>
    /// Inserts a SpaceRecord with Id="default" for the integration-test fixture owner
    /// (DevSpaceOwnerUserId = 00000000-0000-0000-0000-000000000001) so that
    /// SpaceResolver returns SpaceId("default") for those requests — preserving
    /// the historical behaviour for tests that authenticate as that user via session.
    ///
    /// This bridge row is intentionally NOT created through IUserProvisioningService
    /// because the dev owner is a synthetic sentinel, not a real provisioned user.
    /// </summary>
    private async Task SeedLegacyOwnerSpaceAsync()
    {
        // Must match DevSpaceOwnerUserId exactly.
        const string legacyOwnerUserIdN = "00000000000000000000000000000001";
        var now = DateTimeOffset.UtcNow;
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<KoratDbContext>();
        // Idempotent — if already seeded (e.g. RecycleSilosAsync), skip.
        if (!await db.Spaces.AnyAsync(s => s.OwnerUserId == legacyOwnerUserIdN && s.IsDefault))
        {
            db.Spaces.Add(new SpaceRecord
            {
                Id = "default",
                OwnerUserId = legacyOwnerUserIdN,
                DisplayName = "Legacy Dev Space",
                IsDefault = true,
                CreatedAt = now,
                UpdatedAt = now,
            });
            await db.SaveChangesAsync();
        }
    }

    public async Task RecycleSilosAsync()
    {
        // Simulates cloud restart: grains lose memory, DB rows survive.
        await Cluster.StopAllSilosAsync();
        await Cluster.DeployAsync();
        if (Factory is not null)
            await Factory.DisposeAsync();
        Factory = new KoratTestHost(Cluster.Client);
        _ = Factory.CreateClient();
    }

    // ── Task-1 fixture-A seeding helpers ─────────────────────────────────────

    /// <summary>
    /// Well-known UserId for the integration-test dev fixture owner. This is a stable
    /// sentinel used to authenticate test HTTP clients and grain calls in tests that
    /// have not yet been migrated to <see cref="SeedUserAsync"/>.
    /// The corresponding default Space is seeded in <see cref="SeedLegacyOwnerSpaceAsync"/>.
    /// </summary>
    public static readonly UserId DevSpaceOwnerUserId = new(Guid.Parse("00000000-0000-0000-0000-000000000001"));

    /// <summary>
    /// The SpaceId of the bridge Space seeded for <see cref="DevSpaceOwnerUserId"/> in
    /// <see cref="SeedLegacyOwnerSpaceAsync"/>. Tests that use grain-direct calls use
    /// this constant instead of the literal string "default".
    /// </summary>
    public string LegacyOwnerSpaceId => "default";

    /// <summary>
    /// Return type for SeedUserAsync: a freshly-provisioned user with its default Space.
    /// </summary>
    public readonly record struct SeededUser(UserId UserId, string SpaceId);

    /// <summary>
    /// Seeds a new user (and a default Space for that user) via the production
    /// <see cref="IUserProvisioningService.CreateUserWithDefaultSpaceAsync"/> seam (Task 2).
    /// This guarantees the user's default Space is provisioned by the same atomic path
    /// that production uses — tests cannot create a user without a Space and mask isolation bugs.
    ///
    /// InMemory race-safety disclaimer: EF Core InMemory does not support raw SQL and
    /// cannot serialise concurrent writes. This helper is safe for sequential test use
    /// only. Production uses the Postgres branch with FK + filtered-unique-index guarantees.
    /// </summary>
    public async Task<SeededUser> SeedUserAsync(string email, string displayName)
    {
        using var scope = Factory.Services.CreateScope();
        var provisioning = scope.ServiceProvider.GetRequiredService<IUserProvisioningService>();
        var (user, space) = await provisioning.CreateUserWithDefaultSpaceAsync(email, displayName, default);
        return new SeededUser(user.Id, space.Id);
    }

    /// <summary>
    /// Space-MCP inc-2a (SF-2): seeds a SECOND Space for an EXISTING owner (a fresh SpaceId,
    /// the SAME UserId) via a direct <see cref="SpaceRecord"/> insert — the same pattern
    /// <c>SpaceGrainCrossSpaceGuardTests.SeedSpaceAsync</c> uses. This is the sharpest
    /// BLOCKER-1 shape for the cross-tenant OAuth resource-server test: the owner-owns-Space
    /// re-check in <c>SpaceMcpAuth.AuthenticateOAuthAsync</c> passes for EITHER Space (same
    /// owner on both), so ONLY the audience + consent-Space-claim checks stand between a
    /// Space-A token and Space-B.
    /// </summary>
    public async Task<string> SeedAdditionalSpaceForOwnerAsync(UserId owner, string tag)
    {
        var spaceId = SpaceId.New();
        var now = DateTimeOffset.UtcNow;
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<KoratDbContext>();
        db.Spaces.Add(new SpaceRecord
        {
            Id = spaceId.Value,
            OwnerUserId = owner.Value.ToString("N"),
            DisplayName = $"Second-Space-{tag}",
            IsDefault = false,
            CreatedAt = now,
            UpdatedAt = now,
        });
        await db.SaveChangesAsync();
        return spaceId.Value;
    }

    /// <summary>Flips an existing seeded user to admin (IsAdmin = true).</summary>
    public async Task MakeAdminAsync(UserId userId)
    {
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<KoratDbContext>();
        var user = await db.Users.SingleAsync(u => u.Id == userId);
        // IsAdmin is init-only on the entity; bypass via EF change-tracker CurrentValue.
        db.Entry(user).Property(nameof(Korat.Domain.Auth.User.IsAdmin)).CurrentValue = true;
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// Seeds an active User row WITHOUT a default Space. Used by tests that need a validatable
    /// CLI token for a user whose provisioning invariant was broken (no Space row). This lets
    /// the Bearer token validate (user exists + Active), but SpaceResolver returns null — the
    /// gRPC Hello handler then correctly returns AccessDenied("No default Space for user").
    ///
    /// ValidateAsync now joins User to check Status, so a token for a user with no User row
    /// would not validate at all — this helper bridges that gap for the "broken provisioning"
    /// scenario test.
    /// </summary>
    public async Task<UserId> SeedUserWithoutSpaceAsync(string email)
    {
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<KoratDbContext>();
        var id = Guid.NewGuid();
        db.Users.Add(new Korat.Domain.Auth.User
        {
            Id = new UserId(id),
            PrimaryEmail = email,
            DisplayName = "No-Space User",
            CreatedAt = DateTimeOffset.UtcNow,
            Status = Korat.Domain.Auth.UserStatus.Active,
            IsAdmin = false,
        });
        await db.SaveChangesAsync();
        return new UserId(id);
    }

    /// <summary>
    /// Mints a valid session for the given user via the SP1 SessionService and returns
    /// an HttpClient with the __Host-korat_session cookie pre-set so every request is
    /// authenticated as that user.
    /// </summary>
    public async Task<HttpClient> CreateAuthenticatedClientAsync(
        UserId userId, WebApplicationFactory<Program>? factory = null)
    {
        // `factory` lets a caller point the client at a WithWebHostBuilder-derived host (e.g. one
        // configured with an envelope KEK) while still using the shared fixture's seeding.
        factory ??= Factory;
        Guid sessionId;
        using (var scope = factory.Services.CreateScope())
        {
            var sessions = scope.ServiceProvider.GetRequiredService<ISessionService>();
            var session = await sessions.CreateAsync(userId, "test-agent", "127.0.0.1", default);
            sessionId = session.Id;
        }

        var client = factory.CreateClient();
        // Set as a raw Cookie header — the test HttpClient runs over plain HTTP so the
        // browser's __Host- Secure-only restriction does not apply here. The server-side
        // PolymorphicAuthResolver reads the cookie by name regardless of the prefix rules.
        client.DefaultRequestHeaders.Add("Cookie", $"{CanonicalSigninHandler.SessionCookieName}={sessionId:N}");
        return client;
    }

    /// <summary>
    /// Returns an <see cref="HttpClient"/> that carries both a valid session cookie (for the
    /// given user) and a valid antiforgery token (X-XSRF-TOKEN header + __Secure-korat_xsrf cookie).
    ///
    /// Use this for tests that call cookie-authenticated, state-changing endpoints that have
    /// <c>RequireAntiforgeryValidation()</c> (e.g. /api/auth/cli/approve, /deny, /revoke-all).
    ///
    /// Implementation: <c>IAntiforgery.GetAndStoreTokens</c> writes both the cookie token (into
    /// <c>DefaultHttpContext.Response.Headers["Set-Cookie"]</c>) and returns the request token.
    /// We extract the cookie token from the Set-Cookie header and send both on the client so that
    /// <c>ValidateRequestAsync</c> can verify the matched pair server-side.
    /// </summary>
    public async Task<HttpClient> CreateAuthenticatedClientWithAntiforgeryAsync(
        UserId userId, WebApplicationFactory<Program>? factory = null)
    {
        factory ??= Factory;
        var client = await CreateAuthenticatedClientAsync(userId, factory);

        using var scope = factory.Services.CreateScope();
        var antiforgery = scope.ServiceProvider.GetRequiredService<IAntiforgery>();

        // GetAndStoreTokens on a DefaultHttpContext writes the cookie token to
        // Response.Headers["Set-Cookie"] and returns the request token.
        var ctx = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
        var tokens = antiforgery.GetAndStoreTokens(ctx);

        // Extract the cookie token value from the Set-Cookie header.
        // Format: "__Secure-korat_xsrf=<value>; path=/; samesite=strict; httponly"
        // (CookieSecurePolicy is overridden to SameAsRequest in the test host.)
        string? cookieToken = null;
        foreach (var setCookie in ctx.Response.Headers.SetCookie)
        {
            if (setCookie is not null && setCookie.StartsWith("__Secure-korat_xsrf=", StringComparison.Ordinal))
            {
                // Value is the segment between '=' and the first ';'.
                var start = "__Secure-korat_xsrf=".Length;
                var end = setCookie.IndexOf(';', start);
                cookieToken = end >= 0 ? setCookie[start..end] : setCookie[start..];
                break;
            }
        }

        // Attach both tokens — ValidateRequestAsync requires the matched pair.
        if (cookieToken is not null)
            client.DefaultRequestHeaders.Add("Cookie", $"__Secure-korat_xsrf={cookieToken}");
        if (tokens.RequestToken is not null)
            client.DefaultRequestHeaders.Add("X-XSRF-TOKEN", tokens.RequestToken);

        return client;
    }

    /// <summary>
    /// Issues a real CLI token for <paramref name="userId"/> via the production
    /// <see cref="ICliTokenService.IssueAsync"/> seam so gRPC Bearer integration
    /// tests can send a valid <c>Authorization: Bearer</c> header.
    /// </summary>
    public async Task<string> IssueCliTokenAsync(UserId userId)
    {
        using var scope = Factory.Services.CreateScope();
        var cliTokens = scope.ServiceProvider.GetRequiredService<ICliTokenService>();
        var result = await cliTokens.IssueAsync(userId.Value, "full", default);
        return result.RawToken;
    }

    /// <summary>
    /// Space-MCP (increment 1, Task 1): issues a Space-pinned <c>space-mcp:{spaceId}</c>
    /// scoped CLI token for <paramref name="userId"/> via the same production
    /// <see cref="ICliTokenService.IssueAsync"/> seam as <see cref="IssueCliTokenAsync"/> —
    /// used by <c>SpaceMcpAuthTests</c> to build the ONLY bearer <c>/mcp/{spaceSeg}</c>
    /// ever accepts (S5: rejects "full"/"bridge-only", and rejects use against any Space
    /// other than <paramref name="spaceId"/>).
    /// </summary>
    public async Task<string> IssueScopedCliTokenAsync(UserId userId, string spaceId)
    {
        using var scope = Factory.Services.CreateScope();
        var cliTokens = scope.ServiceProvider.GetRequiredService<ICliTokenService>();
        var result = await cliTokens.IssueAsync(userId.Value, $"space-mcp:{spaceId}", default);
        return result.RawToken;
    }

    /// <summary>
    /// Space-MCP inc-2a: like <see cref="CreateAuthenticatedClientAsync"/> but with
    /// AllowAutoRedirect disabled — the OAuth authorize/consent tests must OBSERVE 302s
    /// (to /app/signin or back to the client's redirect_uri) instead of following them.
    ///
    /// HandleCookies is explicitly OFF (the WebApplicationFactoryClientOptions default is
    /// true): the OAuth consent tests (Task 4+) manually combine the session cookie
    /// (DefaultRequestHeaders, set here) with the per-GET antiforgery cookie into one
    /// explicit Cookie header on the consent POST (OAuthFlowHelper.AuthorizeAndConsentAsync).
    /// With the built-in CookieContainer left on, two things break: (1) a Cookie header set
    /// on DefaultRequestHeaders and a DIFFERENT one set per-request do NOT merge on the wire —
    /// only the per-request value survives, silently dropping the session cookie; (2) once the
    /// container has captured an antiforgery cookie from an earlier GET on the SAME client, a
    /// later re-consent GET (Task 4's RepeatConsent_* test) auto-attaches it, so the server
    /// reuses the existing valid antiforgery cookie and never re-issues Set-Cookie, and the
    /// helper's regex extraction finds nothing. Manual-only cookie handling avoids both traps.
    /// </summary>
    public async Task<HttpClient> CreateAuthenticatedNoRedirectClientAsync(UserId userId)
    {
        Guid sessionId;
        using (var scope = Factory.Services.CreateScope())
        {
            var sessions = scope.ServiceProvider.GetRequiredService<ISessionService>();
            var session = await sessions.CreateAsync(userId, "test-agent", "127.0.0.1", default);
            sessionId = session.Id;
        }
        var client = Factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = false,
        });
        client.DefaultRequestHeaders.Add("Cookie", $"{CanonicalSigninHandler.SessionCookieName}={sessionId:N}");
        return client;
    }

    /// <summary>
    /// Space-MCP inc-2a: upserts the pre-registered MCP OAuth client with test-controlled
    /// redirect URIs, through the SAME descriptor builder production seeding uses
    /// (SpaceMcpOAuthClientSeeder) — tests never hand-roll a divergent client registration.
    /// </summary>
    public async Task EnsureOAuthClientAsync(params string[] redirectUris)
    {
        using var scope = Factory.Services.CreateScope();
        var manager = scope.ServiceProvider.GetRequiredService<OpenIddict.Abstractions.IOpenIddictApplicationManager>();
        var options = new Korat.Cloud.Web.Oauth.SpaceMcpOAuthOptions { RedirectUris = redirectUris };
        await Korat.Cloud.Web.Oauth.SpaceMcpOAuthClientSeeder.UpsertAsync(manager, options, CancellationToken.None);
    }

    /// <summary>
    /// Directly upserts a <see cref="Node"/> row into the repository, binding it to
    /// <paramref name="spaceId"/>. Used by security tests that need a pre-existing node
    /// registration without going through the gRPC Hello handshake.
    /// </summary>
    public async Task SeedNodeForSpaceAsync(string nodeId, string spaceId)
    {
        using var scope = Factory.Services.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IMetadataRepository>();
        var now = DateTimeOffset.UtcNow;
        await repository.UpsertNodeAsync(new Node
        {
            Id = new NodeId(nodeId),
            SpaceId = new SpaceId(spaceId),
            DisplayName = "seeded-node",
            Status = NodeStatus.Offline,
            CreatedAt = now,
            UpdatedAt = now,
        });
    }

    // ─────────────────────────────────────────────────────────────────────────

    public async Task DisposeAsync()
    {
        if (Factory is not null)
            await Factory.DisposeAsync();
        if (_cluster is not null)
            await _cluster.StopAllSilosAsync();
    }

    private sealed class SiloConfigurator : ISiloConfigurator
    {
        public void Configure(ISiloBuilder siloBuilder)
        {
            siloBuilder.AddMemoryGrainStorage("korat");
            // Space-MCP inc-2a, Task 7 (SF-1 test support, see SpaceMcpConsumerSessionsFaultInjector's
            // doc comment): a no-op for every call except an ARMED
            // ISpaceMcpConsumerSessionsGrain.RegisterAsync — nothing else in this fixture's
            // behavior changes.
            siloBuilder.AddIncomingGrainCallFilter<SpaceMcpConsumerSessionsFaultInjector.Filter>();
            siloBuilder.ConfigureServices(services =>
            {
                services.AddSerializer(serializerBuilder =>
                    serializerBuilder.AddJsonSerializer(isSupported: type => type.Namespace?.StartsWith("Korat") == true));
                services.AddDbContextFactory<KoratDbContext>(options =>
                    options.UseInMemoryDatabase(KoratTestDatabase.Name, KoratTestDatabase.Root));
                services.AddSingleton<IMetadataRepository, EfMetadataRepository>();
                // PR-2 Task 3: ThreadGrain needs IEnvelopeCrypto — the silo has its OWN DI
                // container (separate from the web host's), so it needs its own KEK. See
                // ThreadGrainTestKek's doc comment for why this can't reuse the web host's
                // per-test KEK overrides. Bind via IConfiguration (not a raw object initializer)
                // so ConfigKekProvider's IOptionsMonitor<EnvelopeOptions> dependency resolves,
                // mirroring exactly how Program.cs binds EnvelopeOptions for the real host.
                var envelopeConfig = new ConfigurationBuilder().AddInMemoryCollection(
                    new Dictionary<string, string?>
                    {
                        [$"{EnvelopeOptions.SectionKey}:ActiveKekId"] = ThreadGrainTestKek.KekId,
                        [$"{EnvelopeOptions.SectionKey}:Keks:{ThreadGrainTestKek.KekId}"] = ThreadGrainTestKek.KekBase64,
                    }).Build();
                services.AddOptions<EnvelopeOptions>().Bind(envelopeConfig.GetSection(EnvelopeOptions.SectionKey));
                services.AddSingleton<IKekProvider, ConfigKekProvider>();
                services.AddSingleton<SpaceDekProvider>();
                services.AddSingleton<IEnvelopeCrypto, EnvelopeCrypto>();
                // DeviceCodeGrain and DeviceCodeRegistryGrain inject TimeProvider for
                // deterministic TTL enforcement. Register TimeProvider.System here so that
                // the test silo can satisfy the grain constructor — the web host registers it
                // transitively via AddRateLimiter, but the test silo container is separate.
                services.TryAddSingleton(TimeProvider.System);
                services.AddScoped(sp => sp.GetRequiredService<IDbContextFactory<KoratDbContext>>().CreateDbContext());
                services.TryAddSingleton<TimeProvider>(TimeProvider.System);
                // Increment 1 (HTTP MCP direct-to-Space), Finding 16 B4b: HttpMcpProxyGrain
                // activates in THIS container and needs both of these — neither was registered
                // here before this feature. SessionRoutingTable is bridged from the web host (see
                // HttpMcpProxyGrainTestBridge's doc comment for why it must be the SAME instance,
                // not a fresh one). IOutboundHttpClientFactory reuses the REAL SSRF-guarded
                // factory + its already-shipped AllowPrivateNetworks(Testing) escape hatch so the
                // grain's outbound calls reach an in-process loopback Kestrel stub for real.
                services.AddSingleton<Korat.Cloud.Gateways.SessionRoutingTable>(_ =>
                    HttpMcpProxyGrainTestBridge.RoutingTable
                    ?? throw new InvalidOperationException(
                        "HttpMcpProxyGrainTestBridge.RoutingTable is not set. KoratIntegrationFixture.InitializeAsync() " +
                        "must finish building the web host (Factory.CreateClient()) — which populates this bridge — " +
                        "before any test exercises a grain that depends on SessionRoutingTable (e.g. HttpMcpProxyGrain). " +
                        "If you see this, a test resolved the grain too early, or InitializeAsync()'s ordering regressed."));
                // Space-MCP increment 1, Task 2 (plan correction S2): the SpaceMcpAggregatorGrain
                // (Task 4) activates in THIS container and injects ISessionAdmission — mirrors the
                // production registrations Program.cs makes on its single shared container (the
                // test silo has its own SEPARATE one, same reason as the SessionRoutingTable bridge
                // above). Built over the SAME bridged SessionRoutingTable so admission opens
                // sessions against the exact routing state the gRPC gateway (web host) shares.
                // IPushWakeSender/INodeGrainLocator complete NodeWakeCoordinator's dependency graph
                // — NullPushWakeSender mirrors Program.cs's unconfigured-APNs fallback (no added
                // latency, TryWakeAsync always returns false).
                services.AddSingleton<Korat.Cloud.Push.IPushWakeSender, Korat.Cloud.Push.NullPushWakeSender>();
                services.AddSingleton<Korat.Cloud.Push.INodeGrainLocator, Korat.Cloud.Push.ClusterNodeGrainLocator>();
                services.AddSingleton<Korat.Cloud.Push.NodeWakeCoordinator>();
                // 031 (mobile-push increment 2, relocated into SessionAdmission): the owner-push-
                // notify trigger now lives in SessionAdmission.AdmitAsync (not NodeGatewayService),
                // so AccessRequestNotifier is one of SessionAdmission's constructor dependencies.
                // This silo's own SessionAdmission (registered below) is consumed only by
                // SpaceMcpAggregatorGrain, but the generic Host validates every registered
                // service's full dependency graph eagerly at Build() time, so the graph must
                // resolve here regardless. NullAlertPushSender mirrors Program.cs's degrade-when-
                // unconfigured fallback (no Korat:Apns/Korat:Fcm secrets in test config) — the
                // notify itself is fire-and-forget best-effort either way.
                services.AddSingleton<Korat.Cloud.Push.IAccessRequestGrainLocator, Korat.Cloud.Push.ClusterAccessRequestGrainLocator>();
                services.AddSingleton<Korat.Cloud.Push.IAlertPushSender, Korat.Cloud.Push.NullAlertPushSender>();
                services.Configure<Korat.Cloud.Push.AccessRequestNotifyOptions>(_ => { });
                services.AddSingleton<Korat.Cloud.Push.AccessRequestNotifier>();
                // MUST-FIX F1 (adversarial review, second pass, BLOCKER): register the REAL
                // SessionAdmission concretely, then expose it through GatedSessionAdmission — a
                // passthrough decorator (zero added latency, zero behavior change) unless a test
                // explicitly arms a serverId via GatedSessionAdmission.Arm(...). This is the ONLY
                // way SpaceMcpTeardownTests' race test can hold AdmitAsync's return hostage AFTER
                // the real admission has already run (so the underlying relay session is genuinely
                // real) — reproducing the node-wake-takes-seconds window the fix targets. Safe to
                // install for the whole shared test silo: ISessionAdmission in THIS container is
                // consumed only by SpaceMcpAggregatorGrain — NodeGatewayService (the other
                // consumer) resolves its own ISessionAdmission from the separate WEB HOST
                // container (Program.cs's registration), never from here.
                services.AddSingleton<Korat.Cloud.Gateways.Admission.SessionAdmission>();
                services.AddSingleton<Korat.Cloud.Gateways.Admission.ISessionAdmission>(sp =>
                    new Korat.Cloud.IntegrationTests.SpaceMcp.GatedSessionAdmission(
                        sp.GetRequiredService<Korat.Cloud.Gateways.Admission.SessionAdmission>()));
                // MUST-FIX 1 (adversarial review, Tasks 4-6): SpaceMcpAggregatorGrain now injects
                // SessionTerminator to actually tear down backend relay sessions on every
                // teardown path (DELETE/deactivate/handshake-timeout), not just flip local grain
                // state. Program.cs registers this once on its single shared container; this test
                // silo is a SEPARATE container (same reason SessionRoutingTable/ISessionAdmission
                // are re-registered above) — resolves over the same bridged SessionRoutingTable
                // and the silo's own IMetadataRepository/IClusterClient so the grain's teardown
                // calls operate against the exact routing/repository state the gRPC gateway shares.
                services.AddSingleton<Korat.Cloud.Gateways.SessionTerminator>();
                services.AddSingleton<Korat.Cloud.Web.Spaces.ISsrfDnsResolver, Korat.Cloud.Web.Spaces.SystemSsrfDnsResolver>();
                // The silo's generic Host does not automatically carry an IHostEnvironment named
                // "Testing" the way the ASP.NET Core web host's UseEnvironment("Testing") does —
                // SsrfGuardedHttpClientFactory's AllowPrivateNetworks gate reads IHostEnvironment,
                // so provide one explicitly rather than relying on ambient defaults. A small local
                // stub rather than Microsoft.Extensions.Hosting.Internal.HostingEnvironment — that
                // type is public but its own doc explicitly says "supports infrastructure and is
                // not intended to be used directly from your code."
                services.AddSingleton<Microsoft.Extensions.Hosting.IHostEnvironment>(
                    new TestSiloHostEnvironment { EnvironmentName = "Testing" });
                var ssrfTestConfig = new ConfigurationBuilder().AddInMemoryCollection(
                    new Dictionary<string, string?>
                    {
                        ["Korat:Inference:Outbound:AllowPrivateNetworks"] = "true",
                    }).Build();
                services.AddSingleton<IConfiguration>(ssrfTestConfig);
                services.AddSingleton<Korat.Cloud.Web.Spaces.SsrfGuardedHttpClientFactory>();
                // Fable plan-review, Blocker 1: wrap the real factory so a registered OAuth façade host (see
                // Infrastructure/OAuthLoopbackFacade.cs) transparently reaches a stub server's real loopback
                // listener. A no-op passthrough for every host that was never registered — every pre-existing
                // silo-hosted outbound test keeps its exact current behavior.
                services.AddSingleton<Korat.Domain.IOutboundHttpClientFactory>(sp =>
                    new OAuthFacadeOutboundHttpClientFactory(sp.GetRequiredService<Korat.Cloud.Web.Spaces.SsrfGuardedHttpClientFactory>()));
            });
        }
    }

    private sealed class ClientConfigurator : IClientBuilderConfigurator
    {
        public void Configure(IConfiguration configuration, IClientBuilder clientBuilder)
        {
            clientBuilder.ConfigureServices(services =>
            {
                services.AddSerializer(serializerBuilder =>
                    serializerBuilder.AddJsonSerializer(isSupported: type => type.Namespace?.StartsWith("Korat") == true));
            });
        }
    }
}

/// <summary>
/// A test-double for <see cref="IEmailChangeEmailSender"/> that captures sent messages
/// in-memory so integration tests can assert on their content.
/// </summary>
public sealed class CapturingEmailChangeEmailSender : IEmailChangeEmailSender
{
    /// <summary>All verification-link emails sent during the test run.</summary>
    public ConcurrentBag<(string To, string Body)> SentVerifications { get; } = new();
    /// <summary>All security-alert emails sent during the test run.</summary>
    public ConcurrentBag<(string To, string Body)> SentAlerts { get; } = new();

    public Task SendVerificationLinkAsync(string toEmail, Uri verifyUrl, TimeSpan ttl, CancellationToken ct)
    {
        SentVerifications.Add((toEmail, verifyUrl.ToString()));
        return Task.CompletedTask;
    }

    public Task SendSecurityAlertAsync(string toEmail, string newEmail, CancellationToken ct)
    {
        SentAlerts.Add((toEmail, $"security alert: email changed to {newEmail}"));
        return Task.CompletedTask;
    }
}

public sealed class KoratTestHost : WebApplicationFactory<Program>
{
    private readonly IClusterClient _clusterClient;

    /// <summary>
    /// Shared capturing email sender injected into the DI container so integration tests
    /// can assert on emails sent during the test.
    ///
    /// InMemory race-safety disclaimer: ConcurrentBag is used for thread safety within a
    /// single test run, but the fixture itself is sequential (DisableTestParallelization).
    /// </summary>
    public CapturingEmailChangeEmailSender EmailChangeEmailSender { get; } = new();

    public KoratTestHost(IClusterClient clusterClient) =>
        _clusterClient = clusterClient;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        // Fable holistic review FIX 3: Program.cs's `AddHostedService<DcrRegistrationReaperService>`
        // call (and every other background maintenance service registered alongside it) sits
        // behind a guard that skips it whenever an IClusterClient is already registered in
        // builder.Services BEFORE the host builds — which every WebApplicationFactory in this
        // test suite does (including this one, two lines below) specifically so tests get the
        // shared TestCluster's client instead of a real Orleans cluster. As of writing that means
        // DcrRegistrationReaperService never actually starts under any test host here (verified:
        // resolving IEnumerable<IHostedService> from this fixture's DI container lists only
        // TelemetryHostedService/DataProtectionHostedService/GenericWebHostService). Pin the sweep
        // interval to an effectively-infinite value here anyway, as defense-in-depth: it costs
        // nothing, and it means a FUTURE change to that gating logic (or a differently-wired test
        // host that does NOT pre-register IClusterClient) can't silently reintroduce a background
        // sweep landing inside a test's create→assert window and stealing its row — all reaper
        // behavior here is already covered by direct SweepCoreAsync calls (DcrRegistrationReaperTests),
        // so a live background instance would only ever add nondeterminism, never test coverage.
        builder.ConfigureAppConfiguration(c => c.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Korat:Cloud:SpaceMcpDcr:SweepIntervalMinutes"] = "100000",
        }));
        builder.ConfigureServices(services =>
        {
            services.AddSingleton(_clusterClient);
            services.RemoveAll<IDbContextFactory<KoratDbContext>>();
            services.RemoveAll<IMetadataRepository>();
            services.AddDbContextFactory<KoratDbContext>(options =>
                options.UseInMemoryDatabase(KoratTestDatabase.Name, KoratTestDatabase.Root));
            services.AddSingleton<IMetadataRepository, EfMetadataRepository>();

            // Replace the production IEmailChangeEmailSender with the capturing test double
            // so integration tests can assert on the content of sent emails without network
            // calls to the Resend API.
            services.RemoveAll<IEmailChangeEmailSender>();
            services.AddSingleton<IEmailChangeEmailSender>(EmailChangeEmailSender);

            // Antiforgery is configured with SecurePolicy=Always for production (HTTPS).
            // The test HTTP client runs over plain HTTP, so antiforgery's SSL guard fires
            // before the token check and throws InvalidOperationException instead of
            // AntiforgeryValidationException. Override to SameAsRequest so the filter
            // can reach the token-presence check and return 400 as expected.
            services.PostConfigure<AntiforgeryOptions>(opts =>
            {
                opts.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
            });

            // Fable plan-review, Blocker 1: same façade-host decorator as the silo's
            // SiloConfigurator.Configure above — the web host has no override for
            // IOutboundHttpClientFactory at all until now, so it uses whatever Program.cs
            // registers for production (the real SsrfGuardedHttpClientFactory); wrap that so a
            // registered OAuth façade host (Tasks 4/5/7's stub AS+resource servers) transparently
            // reaches the real loopback listener while SsrfGuard.ValidateUrl is genuinely
            // exercised against the pre-rewrite façade URL string.
            services.RemoveAll<Korat.Domain.IOutboundHttpClientFactory>();
            services.AddSingleton<Korat.Domain.IOutboundHttpClientFactory>(sp =>
                new OAuthFacadeOutboundHttpClientFactory(sp.GetRequiredService<Korat.Cloud.Web.Spaces.SsrfGuardedHttpClientFactory>()));
        });
    }

}
