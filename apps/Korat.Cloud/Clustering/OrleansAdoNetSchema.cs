using System.Reflection;
using Npgsql;

namespace Korat.Cloud.Clustering;

/// <summary>
/// 010-drop-redis-to-postgres: applies Orleans' official ADO.NET (PostgreSQL) clustering
/// schema. These membership tables (OrleansQuery / OrleansMembershipVersionTable /
/// OrleansMembershipTable + plpgsql functions) live OUTSIDE the EF model — Orleans manages
/// them via named queries — so they can't ride EF migrations and are applied here from the
/// embedded official scripts.
///
/// Idempotent: guarded by a to_regclass check, so it runs once and is a no-op thereafter.
/// Must run BEFORE the silo starts (the silo reads the membership table on join). Gated by
/// the same KORAT_RUN_MIGRATIONS window as EF migrations to avoid a multi-instance race.
/// </summary>
public static class OrleansAdoNetSchema
{
    public static async Task EnsureAsync(string connectionString, CancellationToken cancellationToken = default)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        // Apply the base schema only if the membership tables don't exist yet.
        // Cast to text — Npgsql has no default CLR mapping for the regclass OID type.
        bool baseApplied;
        await using (var check = new NpgsqlCommand("SELECT to_regclass('public.orleansmembershipversiontable')::text", connection))
        {
            var existing = await check.ExecuteScalarAsync(cancellationToken);
            baseApplied = existing is not null and not DBNull;
        }

        if (!baseApplied)
        {
            var main = ReadEmbeddedSql("PostgreSQL-Main.sql");
            var clustering = ReadEmbeddedSql("PostgreSQL-Clustering.sql");

            await using var tx = await connection.BeginTransactionAsync(cancellationToken);
            await using (var cmd = new NpgsqlCommand(main, connection, tx))
                await cmd.ExecuteNonQueryAsync(cancellationToken);
            await using (var cmd = new NpgsqlCommand(clustering, connection, tx))
                await cmd.ExecuteNonQueryAsync(cancellationToken);
            await tx.CommitAsync(cancellationToken);
        }

        // ALWAYS back-fill the 3.7.0 CleanupDefunctSiloEntries query (idempotent upsert).
        // The base PostgreSQL-Clustering.sql omits it though the runtime requires it; this
        // runs both on a fresh schema and on a previously-created base that predates the fix.
        var cleanupMigration = ReadEmbeddedSql("PostgreSQL-Clustering-3.7.0.sql");
        await using (var cmd = new NpgsqlCommand(cleanupMigration, connection))
            await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    private static string ReadEmbeddedSql(string fileName)
    {
        var assembly = Assembly.GetExecutingAssembly();
        var resourceName = assembly.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith(fileName, StringComparison.Ordinal))
            ?? throw new InvalidOperationException($"Embedded SQL resource not found: {fileName}");

        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded SQL resource stream null: {resourceName}");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
