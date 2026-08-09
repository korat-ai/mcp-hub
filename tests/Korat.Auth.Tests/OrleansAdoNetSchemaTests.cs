using Korat.Cloud.Clustering;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Korat.Auth.Tests;

/// <summary>
/// 010-drop-redis-to-postgres: verifies the Orleans ADO.NET (PostgreSQL) clustering schema
/// applies against a REAL Postgres via Npgsql. This is the prod-only code path (skipped by the
/// Testing-env integration suite), and it's correctness-critical: the script embeds '@Name'
/// tokens inside string literals and plpgsql $func$ blocks, so we confirm Npgsql does NOT
/// mis-parse them as parameters and that application is idempotent.
///
/// Skips when Docker is unavailable.
/// </summary>
public class OrleansAdoNetSchemaTests
{
    [SkippableFact]
    public async Task AppliesSchema_AndIsIdempotent()
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
                throw new SkipException($"Docker/Postgres container unavailable: {ex.GetType().Name}");
            }

            var connectionString = pg.GetConnectionString();

            // First application — creates the membership schema from the embedded scripts.
            await OrleansAdoNetSchema.EnsureAsync(connectionString);

            await AssertSchemaPresentAsync(connectionString);

            // Second application — must be a safe no-op (idempotency guard), not throw.
            await OrleansAdoNetSchema.EnsureAsync(connectionString);

            await AssertSchemaPresentAsync(connectionString);
        }
        finally
        {
            if (pg is not null)
                await pg.DisposeAsync();
        }
    }

    private static async Task AssertSchemaPresentAsync(string connectionString)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();

        // Membership tables exist.
        foreach (var table in new[] { "orleansquery", "orleansmembershipversiontable", "orleansmembershiptable" })
        {
            await using var cmd = new NpgsqlCommand($"SELECT to_regclass('public.{table}')::text", conn);
            var result = await cmd.ExecuteScalarAsync();
            Assert.False(result is null or DBNull, $"Expected table {table} to exist.");
        }

        // The named queries Orleans relies on were inserted (e.g. the @-token-bearing ones).
        await using (var count = new NpgsqlCommand("SELECT COUNT(*) FROM OrleansQuery", conn))
        {
            var n = Convert.ToInt64(await count.ExecuteScalarAsync());
            Assert.True(n >= 9, $"Expected Orleans query rows to be registered, got {n}.");
        }

        // Regression guard: the 9.x runtime REQUIRES CleanupDefunctSiloEntriesKey, which the
        // base PostgreSQL-Clustering.sql omits — back-filled by our 3.7.0 migration. Its
        // absence crash-loops the silo ("Not all required queries found").
        await using (var cleanup = new NpgsqlCommand("SELECT COUNT(*) FROM OrleansQuery WHERE QueryKey = 'CleanupDefunctSiloEntriesKey'", conn))
        {
            var n = Convert.ToInt64(await cleanup.ExecuteScalarAsync());
            Assert.Equal(1, n);
        }

        // A representative query text containing @parameters survived insertion intact.
        await using (var q = new NpgsqlCommand("SELECT QueryText FROM OrleansQuery WHERE QueryKey = 'InsertMembershipKey'", conn))
        {
            var text = (string?)await q.ExecuteScalarAsync();
            Assert.NotNull(text);
            Assert.Contains("@DeploymentId", text!);
        }
    }
}
