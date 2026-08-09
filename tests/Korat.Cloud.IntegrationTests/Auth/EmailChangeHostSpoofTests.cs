using System.Net;
using System.Net.Http.Json;
using Korat.Cloud.Web.Auth;
using Korat.Cloud.Web.Auth.Services;
using Korat.Domain.Auth;
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

namespace Korat.Cloud.IntegrationTests.Auth;

/// <summary>
/// Host-spoof test for POST /api/auth/email/change (fix #6).
///
/// Verifies that when <c>Korat:Cli:PublicOrigin</c> is configured, the verification
/// link embedded in the email uses the trusted origin — NOT the value from the
/// request <c>Host</c> header (host-header injection defence).
/// </summary>
public sealed class EmailChangeHostSpoofTests : IAsyncLifetime
{
    private TestCluster? _cluster;
    private HostSpoofTestFactory? _factory;

    public async Task InitializeAsync()
    {
        KoratTestDatabase.Name = $"EmailChangeHostSpoof-{Guid.NewGuid():N}";

        var builder = new TestClusterBuilder();
        builder.AddSiloBuilderConfigurator<HostSpoofSiloConfigurator>();
        builder.AddClientBuilderConfigurator<HostSpoofClientConfigurator>();
        _cluster = builder.Build();
        await _cluster.DeployAsync();

        _factory = new HostSpoofTestFactory(_cluster.Client, "https://trusted.example.com");
        _ = _factory.CreateClient();

        await SeedLegacyOwnerSpaceAsync();
    }

    private async Task SeedLegacyOwnerSpaceAsync()
    {
        const string ownerKey = "00000000000000000000000000000001";
        var now = DateTimeOffset.UtcNow;
        using var scope = _factory!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<KoratDbContext>();
        if (!await db.Spaces.AnyAsync(s => s.OwnerUserId == ownerKey && s.IsDefault))
        {
            db.Spaces.Add(new SpaceRecord
            {
                Id = "default",
                OwnerUserId = ownerKey,
                DisplayName = "Test Space",
                IsDefault = true,
                CreatedAt = now,
                UpdatedAt = now,
            });
            await db.SaveChangesAsync();
        }
    }

    public async Task DisposeAsync()
    {
        if (_factory is not null) await _factory.DisposeAsync();
        if (_cluster is not null) await _cluster.StopAllSilosAsync();
    }

    [Fact]
    public async Task EmailChangeRequest_WithPublicOriginConfigured_UsesPublicOriginNotForgedHost()
    {
        // Arrange: seed a user and get an authenticated+antiforgery client.
        var seeded = await SeedUserAsync($"spoof-{Guid.NewGuid():N}@example.com", "Spoof Test User");
        using var client = await CreateAuthenticatedClientWithAntiforgeryAsync(seeded.UserId);

        // Act: send the email-change request with a forged Host header.
        // AllowedHosts="*" in the test environment does not validate Host, so the
        // forged value reaches the endpoint handler.
        client.DefaultRequestHeaders.TryAddWithoutValidation("Host", "attacker.example.com");
        var newEmail = $"spoof-new-{Guid.NewGuid():N}@example.com";
        var resp = await client.PostAsJsonAsync("/api/auth/email/change", new { newEmail });

        // 202 Accepted (anti-enumeration path for unknown address, or 202 for unknown user).
        Assert.Equal(HttpStatusCode.Accepted, resp.StatusCode);

        // Assert: the verification link in the sent email uses the configured PublicOrigin,
        // NOT the forged Host header value.
        var sent = _factory!.EmailSender.SentVerifications.ToList();
        var mail = sent.SingleOrDefault(m => m.To.Equals(newEmail, StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(mail.To); // value type — check To is not null/empty

        // The link must reference the trusted origin, not the forged host.
        Assert.Contains("https://trusted.example.com", mail.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("attacker.example.com", mail.Body, StringComparison.OrdinalIgnoreCase);
    }

    // ── Seeding helpers ───────────────────────────────────────────────────────

    private async Task<(UserId UserId, string SpaceId)> SeedUserAsync(string email, string displayName)
    {
        using var scope = _factory!.Services.CreateScope();
        var provisioning = scope.ServiceProvider.GetRequiredService<IUserProvisioningService>();
        var (user, space) = await provisioning.CreateUserWithDefaultSpaceAsync(email, displayName, default);
        return (user.Id, space.Id);
    }

    private async Task<HttpClient> CreateAuthenticatedClientWithAntiforgeryAsync(UserId userId)
    {
        Guid sessionId;
        using (var scope = _factory!.Services.CreateScope())
        {
            var sessions = scope.ServiceProvider.GetRequiredService<ISessionService>();
            var session = await sessions.CreateAsync(userId, "test-agent", "127.0.0.1", default);
            sessionId = session.Id;
        }

        var client = _factory!.CreateClient();
        client.DefaultRequestHeaders.Add("Cookie",
            $"{CanonicalSigninHandler.SessionCookieName}={sessionId:N}");

        using var scope2 = _factory.Services.CreateScope();
        var antiforgery = scope2.ServiceProvider.GetRequiredService<IAntiforgery>();
        var ctx = new DefaultHttpContext { RequestServices = scope2.ServiceProvider };
        var tokens = antiforgery.GetAndStoreTokens(ctx);

        string? cookieToken = null;
        foreach (var setCookie in ctx.Response.Headers.SetCookie)
        {
            if (setCookie is not null && setCookie.StartsWith("__Secure-korat_xsrf=", StringComparison.Ordinal))
            {
                var start = "__Secure-korat_xsrf=".Length;
                var end = setCookie.IndexOf(';', start);
                cookieToken = end >= 0 ? setCookie[start..end] : setCookie[start..];
                break;
            }
        }
        if (cookieToken is not null)
            client.DefaultRequestHeaders.Add("Cookie", $"__Secure-korat_xsrf={cookieToken}");
        if (tokens.RequestToken is not null)
            client.DefaultRequestHeaders.Add("X-XSRF-TOKEN", tokens.RequestToken);

        return client;
    }

    // ── Orleans infrastructure ────────────────────────────────────────────────

    private sealed class HostSpoofSiloConfigurator : ISiloConfigurator
    {
        public void Configure(ISiloBuilder siloBuilder)
        {
            siloBuilder.AddMemoryGrainStorage("korat");
            siloBuilder.ConfigureServices(services =>
            {
                services.AddSerializer(sb =>
                    sb.AddJsonSerializer(t => t.Namespace?.StartsWith("Korat") == true));
                services.AddDbContextFactory<KoratDbContext>(opts =>
                    opts.UseInMemoryDatabase(KoratTestDatabase.Name, KoratTestDatabase.Root));
                services.AddSingleton<IMetadataRepository, EfMetadataRepository>();
                services.TryAddSingleton(TimeProvider.System);
            });
        }
    }

    private sealed class HostSpoofClientConfigurator : IClientBuilderConfigurator
    {
        public void Configure(IConfiguration configuration, IClientBuilder clientBuilder)
        {
            clientBuilder.ConfigureServices(services =>
                services.AddSerializer(sb =>
                    sb.AddJsonSerializer(t => t.Namespace?.StartsWith("Korat") == true)));
        }
    }
}

/// <summary>
/// WebApplicationFactory variant that configures a PublicOrigin so that the
/// email-change verification link is built from the trusted origin.
/// </summary>
public sealed class HostSpoofTestFactory : WebApplicationFactory<Program>
{
    private readonly IClusterClient _clusterClient;
    private readonly string _publicOrigin;

    public CapturingEmailChangeEmailSender EmailSender { get; } = new();

    public HostSpoofTestFactory(IClusterClient clusterClient, string publicOrigin)
    {
        _clusterClient = clusterClient;
        _publicOrigin = publicOrigin;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");

        // Set the trusted PublicOrigin — this is the key configuration under test.
        builder.UseSetting("Korat:Cli:PublicOrigin", _publicOrigin);

        builder.ConfigureServices(services =>
        {
            services.AddSingleton(_clusterClient);
            services.RemoveAll<IDbContextFactory<KoratDbContext>>();
            services.RemoveAll<IMetadataRepository>();
            services.AddDbContextFactory<KoratDbContext>(opts =>
                opts.UseInMemoryDatabase(KoratTestDatabase.Name, KoratTestDatabase.Root));
            services.AddSingleton<IMetadataRepository, EfMetadataRepository>();

            // Replace email sender with capturing stub.
            services.RemoveAll<IEmailChangeEmailSender>();
            services.AddSingleton<IEmailChangeEmailSender>(EmailSender);

            // Allow antiforgery over plain HTTP in tests.
            services.PostConfigure<AntiforgeryOptions>(opts =>
            {
                opts.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
            });
        });
    }
}
