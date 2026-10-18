using System.Reflection;
using System.Text.RegularExpressions;
using Npgsql;
using NpgsqlTypes;

namespace Korat.Cloud.Clustering;

/// <summary>
/// 010-drop-redis-to-postgres: applies Orleans' official ADO.NET (PostgreSQL) clustering
/// schema. These membership tables (OrleansQuery / OrleansMembershipVersionTable /
/// OrleansMembershipTable + plpgsql functions) live OUTSIDE the EF model — Orleans manages
/// them via named queries — so they can't ride EF migrations and are applied here from the
/// embedded official scripts.
///
/// Idempotent: guarded by a complete base-table check, so the non-idempotent official scripts
/// run once and the additive cleanup migration can safely run on every invocation.
/// Must run BEFORE the silo starts (the silo reads the membership table on join). Gated by
/// the same KORAT_RUN_MIGRATIONS window as EF migrations to avoid a multi-instance race.
/// </summary>
public static class OrleansAdoNetSchema
{
    private const string SchemaName = "public";

    private static readonly string[] RequiredTables =
    [
        "orleansquery",
        "orleansmembershipversiontable",
        "orleansmembershiptable",
    ];

    // These are the four plpgsql functions created by the embedded Orleans PostgreSQL
    // clustering script. Checking only the tables misses interrupted/manual partial schemas.
    private static readonly string[] RequiredFunctions =
    [
        "update_i_am_alive_time",
        "insert_membership_version",
        "insert_membership",
        "update_membership",
    ];

    private static readonly RequiredColumn[] RequiredColumns =
    [
        new("orleansquery", "querykey", "character varying", 64, false),
        new("orleansquery", "querytext", "character varying", 8000, false),
        new("orleansmembershipversiontable", "deploymentid", "character varying", 150, false),
        new("orleansmembershipversiontable", "timestamp", "timestamp with time zone", null, false),
        new("orleansmembershipversiontable", "version", "integer", null, false),
        new("orleansmembershiptable", "deploymentid", "character varying", 150, false),
        new("orleansmembershiptable", "address", "character varying", 45, false),
        new("orleansmembershiptable", "port", "integer", null, false),
        new("orleansmembershiptable", "generation", "integer", null, false),
        new("orleansmembershiptable", "siloname", "character varying", 150, false),
        new("orleansmembershiptable", "hostname", "character varying", 150, false),
        new("orleansmembershiptable", "status", "integer", null, false),
        new("orleansmembershiptable", "proxyport", "integer", null, true),
        new("orleansmembershiptable", "suspecttimes", "character varying", 8000, true),
        new("orleansmembershiptable", "starttime", "timestamp with time zone", null, false),
        new("orleansmembershiptable", "iamalivetime", "timestamp with time zone", null, false),
    ];

    // Mirrors DbStoredQueries in Microsoft.Orleans.Clustering.AdoNet 10.2.1. The exact query
    // text is read from the embedded official scripts below, so verification catches both a
    // missing row and stale/corrupted SQL under an otherwise valid QueryKey.
    private static readonly string[] RequiredQueryKeys =
    [
        "GatewaysQueryKey",
        "MembershipReadRowKey",
        "MembershipReadAllKey",
        "InsertMembershipVersionKey",
        "UpdateIAmAlivetimeKey",
        "InsertMembershipKey",
        "UpdateMembershipKey",
        "DeleteMembershipTableEntriesKey",
        "CleanupDefunctSiloEntriesKey",
    ];

    private static readonly Regex StoredQueryRegex = new(
        "INSERT\\s+INTO\\s+OrleansQuery\\s*\\(\\s*QueryKey\\s*,\\s*QueryText\\s*\\)\\s*" +
        "VALUES\\s*\\(\\s*'(?<key>[^']+)'\\s*,\\s*'(?<text>(?:''|[^'])*)'\\s*\\)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static async Task EnsureAsync(string connectionString, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        var main = ReadEmbeddedSql("PostgreSQL-Main.sql");
        var clustering = ReadEmbeddedSql("PostgreSQL-Clustering.sql");
        var cleanupMigration = ReadEmbeddedSql("PostgreSQL-Clustering-3.7.0.sql");
        var expectedQueries = ReadExpectedQueries(clustering, cleanupMigration);

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        var existingTables = await ReadExistingTablesAsync(connection, cancellationToken);
        if (existingTables.Count is > 0 && existingTables.Count != RequiredTables.Length)
        {
            var missing = RequiredTables.Except(existingTables, StringComparer.Ordinal).Order().ToArray();
            throw new InvalidOperationException(
                "Orleans ADO.NET schema is partial; refusing to replay the non-idempotent " +
                $"official base scripts. Missing tables in {SchemaName}: {string.Join(", ", missing)}.");
        }

        if (existingTables.Count == 0)
        {
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
        await using (var cmd = new NpgsqlCommand(cleanupMigration, connection))
            await cmd.ExecuteNonQueryAsync(cancellationToken);

        await VerifyAsync(connection, expectedQueries, cancellationToken);
    }

    private static async Task<HashSet<string>> ReadExistingTablesAsync(
        NpgsqlConnection connection,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT lower(c.relname)
            FROM pg_catalog.pg_class c
            JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace
            WHERE n.nspname = @schema
              AND c.relkind IN ('r', 'p')
              AND lower(c.relname) = ANY(@names);
            """;

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("schema", SchemaName);
        command.Parameters.AddWithValue("names", RequiredTables);

        var result = new HashSet<string>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            result.Add(reader.GetString(0));
        return result;
    }

    private static async Task VerifyAsync(
        NpgsqlConnection connection,
        IReadOnlyDictionary<string, string> expectedQueries,
        CancellationToken cancellationToken)
    {
        var problems = new List<string>();

        var tables = await ReadExistingTablesAsync(connection, cancellationToken);
        var missingTables = RequiredTables.Except(tables, StringComparer.Ordinal).Order().ToArray();
        if (missingTables.Length > 0)
            problems.Add($"missing tables: {string.Join(", ", missingTables)}");

        var functions = await ReadExistingFunctionsAsync(connection, cancellationToken);
        var missingFunctions = RequiredFunctions.Except(functions, StringComparer.Ordinal).Order().ToArray();
        if (missingFunctions.Length > 0)
            problems.Add($"missing functions: {string.Join(", ", missingFunctions)}");

        var columns = await ReadExistingColumnsAsync(connection, cancellationToken);
        var invalidColumns = RequiredColumns
            .Where(required => !columns.TryGetValue(required.Key, out var actual)
                || !required.Matches(actual))
            .Select(required => required.Key)
            .Order()
            .ToArray();
        if (invalidColumns.Length > 0)
            problems.Add($"missing or incompatible columns: {string.Join(", ", invalidColumns)}");

        if (missingTables.Length == 0)
        {
            var actualQueries = await ReadStoredQueriesAsync(connection, cancellationToken);
            var missingQueries = RequiredQueryKeys.Except(actualQueries.Keys, StringComparer.Ordinal).Order().ToArray();
            if (missingQueries.Length > 0)
                problems.Add($"missing queries: {string.Join(", ", missingQueries)}");

            var mismatchedQueries = expectedQueries
                .Where(pair => actualQueries.TryGetValue(pair.Key, out var text)
                    && !string.Equals(pair.Value, text, StringComparison.Ordinal))
                .Select(pair => pair.Key)
                .Order()
                .ToArray();
            if (mismatchedQueries.Length > 0)
                problems.Add($"queries with unexpected SQL: {string.Join(", ", mismatchedQueries)}");
        }

        if (problems.Count > 0)
        {
            throw new InvalidOperationException(
                $"Orleans ADO.NET schema verification failed in {SchemaName}: {string.Join("; ", problems)}.");
        }

        await VerifyMembershipOperationsAsync(connection, expectedQueries, cancellationToken);
    }

    private static async Task VerifyMembershipOperationsAsync(
        NpgsqlConnection connection,
        IReadOnlyDictionary<string, string> queries,
        CancellationToken cancellationToken)
    {
        // Metadata cannot prove defaults, ON CONFLICT constraints, or function behavior.
        // Exercise the same stored queries as Orleans, with a private deployment which is
        // never committed or visible to a silo. Bound both execution and cleanup separately.
        var deploymentId = $"korat-schema-check-{Guid.NewGuid():N}";
        var now = DateTime.UtcNow;
        var startTime = now.AddTicks(-(now.Ticks % TimeSpan.TicksPerMillisecond));
        var heartbeatTime = startTime.AddSeconds(1);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        await using var transaction = await connection.BeginTransactionAsync(timeout.Token);
        var operation = "configure membership probe";

        NpgsqlCommand CreateCommand(string sql, int version = 0, int status = 3, DateTime? alive = null)
        {
            var command = new NpgsqlCommand(sql, connection, transaction) { CommandTimeout = 5 };
            command.Parameters.AddWithValue("DeploymentId", NpgsqlDbType.Varchar, deploymentId);
            command.Parameters.AddWithValue("Address", NpgsqlDbType.Varchar, "127.0.0.1");
            command.Parameters.AddWithValue("Port", 1);
            command.Parameters.AddWithValue("Generation", 1);
            command.Parameters.AddWithValue("SiloName", NpgsqlDbType.Varchar, "schema-verification");
            command.Parameters.AddWithValue("HostName", NpgsqlDbType.Varchar, "localhost");
            command.Parameters.AddWithValue("Status", status);
            command.Parameters.AddWithValue("ProxyPort", 0);
            command.Parameters.AddWithValue("StartTime", startTime);
            command.Parameters.AddWithValue("IAmAliveTime", alive ?? startTime);
            command.Parameters.AddWithValue("SuspectTimes", NpgsqlDbType.Varchar, "");
            command.Parameters.AddWithValue("Version", version);
            return command;
        }

        InvalidOperationException UnexpectedResult() => new(
            $"Orleans ADO.NET schema verification failed in {SchemaName}: " +
            $"membership probe {operation} returned an unexpected result.");

        async Task ExpectRowCountAsync(string queryKey, int expected, int version = 0, int status = 3)
        {
            operation = queryKey;
            await using var command = CreateCommand(queries[queryKey], version, status);
            var result = await command.ExecuteScalarAsync(timeout.Token);
            if (result is not int rowCount || rowCount != expected)
                throw UnexpectedResult();
        }

        async Task ExpectMembershipAsync(int version, int? status, DateTime? alive)
        {
            operation = "MembershipReadRowKey";
            await using var command = CreateCommand(queries[operation]);
            await using var reader = await command.ExecuteReaderAsync(timeout.Token);
            if (!await reader.ReadAsync(timeout.Token)
                || reader.GetInt32(reader.GetOrdinal("Version")) != version
                || (status.HasValue
                    ? reader.IsDBNull(reader.GetOrdinal("Status"))
                      || reader.GetInt32(reader.GetOrdinal("Status")) != status.Value
                      || reader.GetDateTime(reader.GetOrdinal("IAmAliveTime")) != alive
                    : !reader.IsDBNull(reader.GetOrdinal("Status")))
                || await reader.ReadAsync(timeout.Token))
            {
                throw UnexpectedResult();
            }
        }

        try
        {
            await using (var command = new NpgsqlCommand(
                "SET LOCAL statement_timeout = '5s'; SET LOCAL lock_timeout = '5s';",
                connection, transaction) { CommandTimeout = 5 })
            {
                await command.ExecuteNonQueryAsync(timeout.Token);
            }

            // The insert deliberately omits Timestamp and Version, just like silo startup.
            await ExpectRowCountAsync("InsertMembershipVersionKey", 1);
            await ExpectRowCountAsync("InsertMembershipVersionKey", 0);
            await ExpectMembershipAsync(0, null, null);

            // A stale insert must roll back its tentative membership row, not just return 0.
            await ExpectRowCountAsync("InsertMembershipKey", 0, version: -1);
            await ExpectMembershipAsync(0, null, null);
            await ExpectRowCountAsync("InsertMembershipKey", 1);
            await ExpectRowCountAsync("InsertMembershipKey", 0, version: 1);
            await ExpectMembershipAsync(1, 3, startTime);

            await ExpectRowCountAsync("UpdateMembershipKey", 0, version: 0, status: 6);
            await ExpectMembershipAsync(1, 3, startTime);
            await ExpectRowCountAsync("UpdateMembershipKey", 1, version: 1, status: 6);

            operation = "UpdateIAmAlivetimeKey";
            await using (var command = CreateCommand(queries[operation], alive: heartbeatTime))
                await command.ExecuteNonQueryAsync(timeout.Token);
            await ExpectMembershipAsync(2, 6, heartbeatTime);
        }
        catch (PostgresException error)
        {
            throw new InvalidOperationException(
                $"Orleans ADO.NET schema verification failed in {SchemaName}: " +
                $"membership probe {operation} failed (SQLSTATE {error.SqlState}).", error);
        }
        finally
        {
            // Do not reuse the caller/probe token: cancellation must still roll back.
            using var rollbackTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await transaction.RollbackAsync(rollbackTimeout.Token);
        }
    }

    private static async Task<HashSet<string>> ReadExistingFunctionsAsync(
        NpgsqlConnection connection,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT lower(p.proname)
            FROM pg_catalog.pg_proc p
            JOIN pg_catalog.pg_namespace n ON n.oid = p.pronamespace
            WHERE n.nspname = @schema
              AND lower(p.proname) = ANY(@names);
            """;

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("schema", SchemaName);
        command.Parameters.AddWithValue("names", RequiredFunctions);

        var result = new HashSet<string>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            result.Add(reader.GetString(0));
        return result;
    }

    private static async Task<Dictionary<string, ExistingColumn>> ReadExistingColumnsAsync(
        NpgsqlConnection connection,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT lower(table_name), lower(column_name), data_type,
                   character_maximum_length, is_nullable = 'YES'
            FROM information_schema.columns
            WHERE table_schema = @schema
              AND lower(table_name) = ANY(@tables);
            """;

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("schema", SchemaName);
        command.Parameters.AddWithValue("tables", RequiredTables);

        var result = new Dictionary<string, ExistingColumn>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var table = reader.GetString(0);
            var column = reader.GetString(1);
            result.Add(
                $"{table}.{column}",
                new ExistingColumn(
                    reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetInt32(3),
                    reader.GetBoolean(4)));
        }
        return result;
    }

    private static async Task<Dictionary<string, string>> ReadStoredQueriesAsync(
        NpgsqlConnection connection,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT QueryKey, QueryText
            FROM OrleansQuery
            WHERE QueryKey = ANY(@keys);
            """;

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("keys", RequiredQueryKeys);

        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            result.Add(reader.GetString(0), reader.GetString(1));
        return result;
    }

    private static IReadOnlyDictionary<string, string> ReadExpectedQueries(params string[] scripts)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var script in scripts)
        {
            foreach (Match match in StoredQueryRegex.Matches(script))
            {
                result.Add(
                    match.Groups["key"].Value,
                    match.Groups["text"].Value.Replace("''", "'", StringComparison.Ordinal));
            }
        }

        var missing = RequiredQueryKeys.Except(result.Keys, StringComparer.Ordinal).Order().ToArray();
        if (missing.Length > 0)
        {
            throw new InvalidOperationException(
                $"Embedded Orleans SQL is missing required queries: {string.Join(", ", missing)}.");
        }

        return result;
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

    private sealed record ExistingColumn(string DataType, int? MaximumLength, bool IsNullable);

    private sealed record RequiredColumn(
        string Table,
        string Column,
        string DataType,
        int? MaximumLength,
        bool IsNullable)
    {
        internal string Key => $"{Table}.{Column}";

        internal bool Matches(ExistingColumn actual) =>
            string.Equals(DataType, actual.DataType, StringComparison.Ordinal)
            && MaximumLength == actual.MaximumLength
            && IsNullable == actual.IsNullable;
    }
}
