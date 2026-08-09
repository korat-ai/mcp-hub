using Korat.Cloud.DataProtection;
using Korat.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;

namespace Korat.Auth.Tests;

/// <summary>
/// 010-drop-redis-to-postgres: proves Data Protection keys are SHARED across instances via
/// Postgres — the exact two-machine scenario that antiforgery needs. Two independent service
/// providers (= two Fly machines) over one Postgres DB: a payload protected by provider A must
/// unprotect on provider B. Replicates the Program.cs onFly DP wiring.
///
/// Skips when Docker is unavailable.
/// </summary>
public class DataProtectionPostgresTests
{
    [SkippableFact]
    public async Task Keys_AreShared_AcrossProviders_ViaPostgres()
    {
        PostgreSqlContainer? pg = null;
        try
        {
            try
            {
                pg = new PostgreSqlBuilder().WithImage("postgres:16-alpine").Build();
                await pg.StartAsync();
            }
            catch (Exception ex)
            {
                throw new SkipException($"Docker/Postgres unavailable: {ex.GetType().Name}");
            }

            var connectionString = pg.GetConnectionString();

            // Create the schema (incl. DataProtectionKeys) once.
            var options = new DbContextOptionsBuilder<KoratDbContext>().UseNpgsql(connectionString).Options;
            await using (var ctx = new KoratDbContext(options))
                await ctx.Database.EnsureCreatedAsync();

            // Provider A (machine A) protects a payload.
            await using var spA = BuildProvider(connectionString);
            var protectorA = spA.GetRequiredService<IDataProtectionProvider>().CreateProtector("antiforgery-like");
            var ciphertext = protectorA.Protect("payload-from-machine-A");

            // Provider B (a SEPARATE container = machine B) must unprotect it.
            await using var spB = BuildProvider(connectionString);
            var protectorB = spB.GetRequiredService<IDataProtectionProvider>().CreateProtector("antiforgery-like");
            var roundTripped = protectorB.Unprotect(ciphertext);

            Assert.Equal("payload-from-machine-A", roundTripped);

            // And the key was actually persisted to the shared table (not ephemeral/per-machine).
            await using (var ctx = new KoratDbContext(options))
            {
                var keyCount = await ctx.DataProtectionKeys.CountAsync();
                Assert.True(keyCount >= 1, $"Expected ≥1 persisted DP key, found {keyCount} (keys are ephemeral → not shared).");
            }
        }
        finally
        {
            if (pg is not null)
                await pg.DisposeAsync();
        }
    }

    // Mirrors the onFly Data Protection wiring in Program.cs.
    private static ServiceProvider BuildProvider(string connectionString)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContextFactory<KoratDbContext>(o => o.UseNpgsql(connectionString));
        services.AddSingleton<DbContextXmlRepository>();
        services.AddDataProtection().SetApplicationName("Korat.Cloud");
        services.AddOptions<KeyManagementOptions>()
            .Configure<DbContextXmlRepository>((opts, repo) => opts.XmlRepository = repo);
        return services.BuildServiceProvider();
    }
}
