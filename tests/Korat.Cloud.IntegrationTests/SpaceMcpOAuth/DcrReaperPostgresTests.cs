using System.Text.Json;
using Korat.Cloud.Maintenance;
using Korat.Cloud.Web.Oauth;
using Korat.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using OpenIddict.Abstractions;
using Testcontainers.PostgreSql;
using Xunit.Abstractions;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Korat.Cloud.IntegrationTests.SpaceMcpOAuth;

/// <summary>
/// Postgres-only regression guard for a multiple-active-reader failure:
/// <see cref="DcrRegistrationReaperService.SweepCoreAsync"/> used to run
/// <c>authorizations.FindAsync(...)</c> INSIDE the <c>applications.ListAsync(...)</c>
/// enumeration. <c>ListAsync</c> streams — one open <c>NpgsqlDataReader</c> for the whole
/// enumeration — and Npgsql does NOT support MARS (multiple active result sets), so the nested
/// query threw <c>Npgsql.NpgsqlOperationInProgressException</c> ("A command is already in
/// progress") on EVERY sweep against real Postgres. The sibling InMemory suite
/// (<see cref="DcrRegistrationReaperTests"/>, same folder) NEVER caught this: EF Core's InMemory
/// provider materializes <c>ListAsync</c> eagerly (no open reader to collide with), so the nested
/// query was always legal there. Only a real ADO.NET connection against real Postgres can
/// reproduce the collision — hence this test is Docker-gated and skips (not fails) when Docker
/// is unavailable, same pattern as <see cref="AuditChainPostgresTests"/> /
/// <see cref="CliTokenCapPostgresTests"/> in this project.
///
/// Stands up a minimal DI container wiring <c>IOpenIddictApplicationManager</c> /
/// <c>IOpenIddictAuthorizationManager</c> to a <see cref="KoratDbContext"/> backed by the
/// container's Postgres — mirroring <c>apps/Korat.Cloud/Program.cs</c>'s
/// <c>AddOpenIddict().AddCore().UseEntityFrameworkCore().UseDbContext&lt;KoratDbContext&gt;()</c>
/// registration. Deliberately does NOT call <c>DisableBulkOperations()</c>: that call is
/// Testing-only in Program.cs (EF Core InMemory can't do bulk <c>ExecuteDelete</c>) — real
/// Postgres supports it natively, and <c>SweepCoreAsync</c>'s <c>applications.DeleteAsync</c> is
/// exactly the call that exercises OpenIddict's bulk-delete store path in production, so this
/// test leaves it enabled to match prod.
/// </summary>
[Trait("Category", "DcrReaperPostgres")]
public sealed class DcrReaperPostgresTests : IAsyncLifetime
{
    private readonly ITestOutputHelper _output;
    private PostgreSqlContainer? _postgres;
    private string? _connectionString;
    private ServiceProvider? _services;

    public DcrReaperPostgresTests(ITestOutputHelper output) => _output = output;

    public async Task InitializeAsync()
    {
        try
        {
            _postgres = new PostgreSqlBuilder()
                .WithImage("postgres:16-alpine")
                .WithDatabase("korat_dcr_reaper_test")
                .WithUsername("korat")
                .WithPassword("korat")
                .Build();
            await _postgres.StartAsync();
            _connectionString = _postgres.GetConnectionString();
        }
        catch (Exception ex)
        {
            // Docker unavailable — container stays null; the test skips via SkipIfNoDocker().
            _postgres = null;
            _connectionString = null;
            Console.Error.WriteLine(
                $"[SKIP] SKIPPED: Docker unavailable — MARS/no-nested-query-during-ListAsync " +
                $"invariant not checked (DcrReaperPostgresTests: postgres:16-alpine container " +
                $"failed to start: {ex.Message})");
            return;
        }

        var migrationOptions = new DbContextOptionsBuilder<KoratDbContext>()
            .UseNpgsql(_connectionString)
            .Options;
        await using (var migrator = new KoratDbContext(migrationOptions))
        {
            await migrator.Database.MigrateAsync();
        }

        // Minimal DI: KoratDbContext + OpenIddict Core/EF store, mirroring Program.cs's
        // AddOpenIddict().AddCore(opts => opts.UseEntityFrameworkCore(ef =>
        // ef.UseDbContext<KoratDbContext>())) registration (Testing-only DisableBulkOperations()
        // deliberately OMITTED — see class doc comment).
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<KoratDbContext>(o => o.UseNpgsql(_connectionString));
        services.AddOpenIddict()
            .AddCore(opts => opts.UseEntityFrameworkCore(ef => ef.UseDbContext<KoratDbContext>()));
        _services = services.BuildServiceProvider();
    }

    public async Task DisposeAsync()
    {
        if (_services is not null)
            await _services.DisposeAsync();
        if (_postgres is not null)
            await _postgres.DisposeAsync();
    }

    private const string DockerSkipReason =
        "SKIPPED: Docker unavailable — MARS/no-nested-query-during-ListAsync invariant not " +
        "checked (DcrReaperPostgresTests requires a running Docker daemon with postgres:16-alpine).";

    private void SkipIfNoDocker()
    {
        if (_connectionString is null || _services is null)
        {
            _output.WriteLine(DockerSkipReason);
            Console.Error.WriteLine($"[SKIP] {DockerSkipReason}");
            throw Xunit.Sdk.SkipException.ForSkip(DockerSkipReason);
        }
    }

    // ── Seed helper (mirrors DcrRegistrationReaperTests.CreateDcrClientAsync) ──────────────────

    private static async Task<string> CreateDcrClientAsync(
        IOpenIddictApplicationManager apps, DateTimeOffset registeredAt, CancellationToken ct)
    {
        var clientId = KoratOAuthConstants.DcrClientIdPrefix + Guid.NewGuid().ToString("N");
        var descriptor = SpaceMcpOAuthClientSeeder.BuildDescriptor(new SpaceMcpOAuthOptions
        {
            ClientId = clientId,
            DisplayName = "dcr-reaper-postgres-test",
            RedirectUris = ["http://127.0.0.1:5000/cb"],
        });
        descriptor.Properties[KoratOAuthConstants.DcrMarkerProperty] = JsonSerializer.SerializeToElement("1");
        descriptor.Properties[KoratOAuthConstants.DcrRegisteredAtProperty] =
            JsonSerializer.SerializeToElement(registeredAt.ToString("O"));
        await apps.CreateAsync(descriptor, ct);
        return clientId;
    }

    private static async Task<string> CreateNonDcrClientAsync(IOpenIddictApplicationManager apps, CancellationToken ct)
    {
        var clientId = "non-dcr-" + Guid.NewGuid().ToString("N");
        var descriptor = SpaceMcpOAuthClientSeeder.BuildDescriptor(new SpaceMcpOAuthOptions
        {
            ClientId = clientId,
            DisplayName = "non-dcr-control",
            RedirectUris = ["http://127.0.0.1:5000/cb"],
        });
        // Deliberately NO DcrMarkerProperty — proves the sweep never touches non-DCR clients.
        await apps.CreateAsync(descriptor, ct);
        return clientId;
    }

    // ── Test ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The whole point of this test: on the OLD nested-query shape this throws
    /// <c>NpgsqlOperationInProgressException</c> on EVERY real-Postgres sweep (never reaping
    /// anything); on the fixed two-pass shape it must complete cleanly and reap exactly the one
    /// unconsented, past-TTL DCR client.
    /// </summary>
    [Fact]
    public async Task SweepCoreAsync_AgainstRealPostgres_DoesNotThrow_AndReapsOnlyUnconsentedDcrClient()
    {
        SkipIfNoDocker();

        var options = new SpaceMcpDcrOptions { UnconsentedTtlMinutes = 5 };
        var ct = CancellationToken.None;

        using var scope = _services!.CreateScope();
        var apps = scope.ServiceProvider.GetRequiredService<IOpenIddictApplicationManager>();
        var auths = scope.ServiceProvider.GetRequiredService<IOpenIddictAuthorizationManager>();

        var old = DateTimeOffset.UtcNow - TimeSpan.FromMinutes(6); // TTL(5m) + 1m ago ⇒ reaped

        var unconsentedDcr = await CreateDcrClientAsync(apps, old, ct);
        var consentedDcr = await CreateDcrClientAsync(apps, old, ct);
        var nonDcr = await CreateNonDcrClientAsync(apps, ct);

        // Give the second DCR client a Valid authorization ⇒ consented ⇒ must be kept.
        var consentedApp = await apps.FindByClientIdAsync(consentedDcr, ct);
        var consentedAppId = (await apps.GetIdAsync(consentedApp!, ct))!;
        await auths.CreateAsync(new OpenIddictAuthorizationDescriptor
        {
            ApplicationId = consentedAppId,
            Status = Statuses.Valid,
            Subject = Guid.NewGuid().ToString("N"),
            Type = AuthorizationTypes.Permanent,
        }, ct);

        // Act — against real Postgres this is the exact call that threw
        // NpgsqlOperationInProgressException on the old nested-query shape.
        var deleted = await DcrRegistrationReaperService.SweepCoreAsync(
            apps, auths, options, NullLogger.Instance, ct);

        Assert.Equal(1, deleted);
        Assert.Null(await apps.FindByClientIdAsync(unconsentedDcr, ct));   // swept
        Assert.NotNull(await apps.FindByClientIdAsync(consentedDcr, ct));  // consented — kept
        Assert.NotNull(await apps.FindByClientIdAsync(nonDcr, ct));        // never DCR-marked — untouched
    }
}
