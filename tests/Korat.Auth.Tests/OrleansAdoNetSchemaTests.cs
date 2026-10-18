using System.Diagnostics;
using Korat.Cloud.Clustering;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Korat.Auth.Tests;

/// <summary>
/// Exercises the production Npgsql path against PostgreSQL 16. When
/// KORAT_TEST_POSTGRES is set, every test creates and drops its own uniquely named database;
/// otherwise Testcontainers supplies the server and the same isolation rule still applies.
/// </summary>
public sealed class OrleansAdoNetSchemaTests
{
    private static readonly string[] ExpectedQueryKeys =
    [
        "CleanupDefunctSiloEntriesKey",
        "DeleteMembershipTableEntriesKey",
        "GatewaysQueryKey",
        "InsertMembershipKey",
        "InsertMembershipVersionKey",
        "MembershipReadAllKey",
        "MembershipReadRowKey",
        "UpdateIAmAlivetimeKey",
        "UpdateMembershipKey",
    ];

    [Fact]
    public Task MigrationCoordinator_FreshDatabaseWithoutAdvertisedIp_PreparesSchema_AndRepeatIsSafe() =>
        WithFreshDatabaseAsync(async connectionString =>
        {
            var applicationMigrationCalls = 0;

            async Task EnsureApplicationSchemaAsync(CancellationToken cancellationToken)
            {
                applicationMigrationCalls++;
                await using var connection = new NpgsqlConnection(connectionString);
                await connection.OpenAsync(cancellationToken);
                await using var command = new NpgsqlCommand(
                    "CREATE TABLE IF NOT EXISTS ApplicationMigrationMarker (Id integer PRIMARY KEY)",
                    connection);
                await command.ExecuteNonQueryAsync(cancellationToken);
            }

            // The coordinator deliberately has no advertised-IP/topology input. This is the
            // shape used by the Kubernetes migrate job, whose environment has no pod IP.
            await MigrationCoordinator.RunAsync(connectionString, EnsureApplicationSchemaAsync);
            var firstSnapshot = await AssertCompleteSchemaAsync(connectionString);

            await MigrationCoordinator.RunAsync(connectionString, EnsureApplicationSchemaAsync);
            var secondSnapshot = await AssertCompleteSchemaAsync(connectionString);

            Assert.Equal(2, applicationMigrationCalls);
            Assert.Equal(firstSnapshot, secondSnapshot);
        });

    [Fact]
    public Task CloudMigrateEntrypoint_WithoutAdvertisedIp_AppliesRealEfAndOrleansSchemas_AndRepeatIsSafe() =>
        WithFreshDatabaseAsync(async connectionString =>
        {
            var processDirectory = Path.Combine(
                Path.GetTempPath(),
                $"korat-cloud-migrate-{Guid.NewGuid():N}");
            Directory.CreateDirectory(processDirectory);
            try
            {
                var first = await RunCloudMigrationProcessAsync(connectionString, processDirectory);
                Assert.True(first.ExitCode == 0, FormatProcessFailure("first", first));
                var firstSnapshot = await AssertEntrypointSchemaAsync(connectionString);

                var second = await RunCloudMigrationProcessAsync(connectionString, processDirectory);
                Assert.True(second.ExitCode == 0, FormatProcessFailure("second", second));
                var secondSnapshot = await AssertEntrypointSchemaAsync(connectionString);

                Assert.Equal(firstSnapshot, secondSnapshot);
            }
            finally
            {
                DeleteProcessDirectory(processDirectory);
            }
        });

    [Fact]
    public Task EnsureAsync_PartialBaseSchema_RejectsTheOldSingleTableFalsePositive() =>
        WithFreshDatabaseAsync(async connectionString =>
        {
            await ExecuteAsync(connectionString, """
                CREATE TABLE OrleansMembershipVersionTable
                (
                    DeploymentId varchar(150) PRIMARY KEY,
                    Timestamp timestamptz(3) NOT NULL DEFAULT now(),
                    Version integer NOT NULL DEFAULT 0
                );
                """);

            var error = await Assert.ThrowsAsync<InvalidOperationException>(
                () => OrleansAdoNetSchema.EnsureAsync(connectionString));

            Assert.Contains("schema is partial", error.Message, StringComparison.Ordinal);
            Assert.Contains("orleansquery", error.Message, StringComparison.Ordinal);
            Assert.Contains("orleansmembershiptable", error.Message, StringComparison.Ordinal);
        });

    [Fact]
    public Task EnsureAsync_StoredQueryTextWasCorrupted_FailsExactEmbeddedSqlVerification() =>
        WithFreshDatabaseAsync(async connectionString =>
        {
            await OrleansAdoNetSchema.EnsureAsync(connectionString);
            await ExecuteAsync(connectionString, """
                UPDATE OrleansQuery
                SET QueryText = 'SELECT 1;'
                WHERE QueryKey = 'UpdateMembershipKey';
                """);

            var error = await Assert.ThrowsAsync<InvalidOperationException>(
                () => OrleansAdoNetSchema.EnsureAsync(connectionString));

            Assert.Contains("queries with unexpected SQL", error.Message, StringComparison.Ordinal);
            Assert.Contains("UpdateMembershipKey", error.Message, StringComparison.Ordinal);
        });

    [Fact]
    public Task EnsureAsync_RequiredFunctionAndQueryAreMissing_ReportsBoth() =>
        WithFreshDatabaseAsync(async connectionString =>
        {
            await OrleansAdoNetSchema.EnsureAsync(connectionString);
            await ExecuteAsync(connectionString, """
                DELETE FROM OrleansQuery WHERE QueryKey = 'MembershipReadAllKey';
                DROP FUNCTION update_membership(
                    character varying,
                    character varying,
                    integer,
                    integer,
                    integer,
                    character varying,
                    timestamp with time zone,
                    integer);
                """);

            var error = await Assert.ThrowsAsync<InvalidOperationException>(
                () => OrleansAdoNetSchema.EnsureAsync(connectionString));

            Assert.Contains("missing functions: update_membership", error.Message, StringComparison.Ordinal);
            Assert.Contains("missing queries: MembershipReadAllKey", error.Message, StringComparison.Ordinal);
        });

    [Theory]
    [InlineData(
        "ALTER TABLE OrleansMembershipVersionTable ALTER COLUMN Version DROP DEFAULT;",
        "SQLSTATE 23502")]
    [InlineData(
        "ALTER TABLE OrleansMembershipVersionTable ALTER COLUMN Timestamp DROP DEFAULT;",
        "SQLSTATE 23502")]
    [InlineData(
        "ALTER TABLE OrleansMembershipTable DROP CONSTRAINT PK_MembershipTable_DeploymentId;",
        "SQLSTATE 42P10")]
    [InlineData("""
        CREATE OR REPLACE FUNCTION update_i_am_alive_time(
            deployment_id varchar, address_arg varchar, port_arg integer,
            generation_arg integer, i_am_alive_time timestamptz)
        RETURNS void LANGUAGE plpgsql AS $$ BEGIN RETURN; END $$;
        """, "MembershipReadRowKey returned an unexpected result")]
    public Task EnsureAsync_MetadataExistsButMembershipIsBroken_FailsAndRollsBack(
        string corruptionSql,
        string expectedError) =>
        WithFreshDatabaseAsync(async connectionString =>
        {
            await OrleansAdoNetSchema.EnsureAsync(connectionString);
            Assert.Empty(await ReadMembershipSnapshotAsync(connectionString));
            await ExecuteAsync(connectionString, corruptionSql);

            var error = await Assert.ThrowsAsync<InvalidOperationException>(
                () => OrleansAdoNetSchema.EnsureAsync(connectionString));

            Assert.Contains("membership probe", error.Message, StringComparison.Ordinal);
            Assert.Contains(expectedError, error.Message, StringComparison.Ordinal);
            // In the PK/no-op cases the probe has already inserted rows before failing.
            // Neither those rows nor the version row may escape the finally rollback.
            Assert.Empty(await ReadMembershipSnapshotAsync(connectionString));
        });

    [Fact]
    public Task EnsureAsync_MembershipProbeIsRepeatable_LeavesExistingDeploymentUnchanged() =>
        WithFreshDatabaseAsync(async connectionString =>
        {
            await OrleansAdoNetSchema.EnsureAsync(connectionString);
            Assert.Empty(await ReadMembershipSnapshotAsync(connectionString));
            await ExecuteAsync(connectionString, """
                INSERT INTO OrleansMembershipVersionTable (DeploymentId, Timestamp, Version)
                VALUES ('existing-test-deployment', '2026-01-01T00:00:00Z', 17);
                INSERT INTO OrleansMembershipTable
                    (DeploymentId, Address, Port, Generation, SiloName, HostName, Status,
                     ProxyPort, SuspectTimes, StartTime, IAmAliveTime)
                VALUES ('existing-test-deployment', '127.0.0.1', 11111, 123, 'existing-silo',
                        'existing-host', 3, 30000, NULL,
                        '2026-01-01T00:00:00Z', '2026-01-01T00:00:01Z');
                """);
            var before = await ReadMembershipSnapshotAsync(connectionString);
            Assert.Equal(2, before.Count);

            await OrleansAdoNetSchema.EnsureAsync(connectionString);
            Assert.Equal(before, await ReadMembershipSnapshotAsync(connectionString));
            await OrleansAdoNetSchema.EnsureAsync(connectionString);
            Assert.Equal(before, await ReadMembershipSnapshotAsync(connectionString));
        });

    [Fact]
    public void MigrationProcessEnvironment_RemovesInheritedHostFlagsAndApplicationSettings()
    {
        var environment = new Dictionary<string, string?>
        {
            ["PATH"] = "/test/runtime",
            ["DATABASE_URL"] = "must-not-be-used",
            ["ConnectionStrings__Korat"] = "must-not-be-used",
            ["DOTNET_FORWARDEDHEADERS_ENABLED"] = "true",
            ["ForwardedHeaders_Enabled"] = "true",
            ["DOTNET_STARTUP_HOOKS"] = "must-not-be-loaded",
            ["ASPNETCORE_FORWARDEDHEADERS_ENABLED"] = "true",
            ["KORAT_ADVERTISED_IP"] = "192.0.2.1",
            ["NATS_URL"] = "must-not-be-used",
            ["Sso__Authority"] = "must-not-be-used",
            ["OTEL_EXPORTER_OTLP_ENDPOINT"] = "must-not-be-used",
        };

        ConfigureMigrationEnvironment(environment, "isolated-test-database", "isolated-test-directory");

        Assert.Equal(new[]
        {
            "ASPNETCORE_ENVIRONMENT", "ConnectionStrings__Korat", "DOTNET_ENVIRONMENT",
            "Korat__DataProtectionKeyPath", "PATH",
        }, environment.Keys.OrderBy(key => key, StringComparer.Ordinal));
        Assert.Equal("Testing", environment["ASPNETCORE_ENVIRONMENT"]);
        Assert.Equal("Testing", environment["DOTNET_ENVIRONMENT"]);
        Assert.Equal("isolated-test-database", environment["ConnectionStrings__Korat"]);
    }

    [Fact]
    public async Task MigrationCoordinator_ApplicationMigrationFails_DoesNotReportSuccessOrTouchOrleans()
    {
        var expected = new ApplicationException("application migration failed");
        var actual = await Assert.ThrowsAsync<ApplicationException>(() =>
            MigrationCoordinator.RunAsync(
                "Host=127.0.0.1;Port=1;Database=must_not_be_contacted;Username=nobody",
                _ => Task.FromException(expected)));

        Assert.Same(expected, actual);
    }

    private static async Task<IReadOnlyList<string>> AssertCompleteSchemaAsync(string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();

        foreach (var table in new[]
                 {
                     "orleansquery",
                     "orleansmembershipversiontable",
                     "orleansmembershiptable",
                     "applicationmigrationmarker",
                 })
        {
            await using var command = new NpgsqlCommand(
                $"SELECT to_regclass('public.{table}')::text",
                connection);
            var result = await command.ExecuteScalarAsync();
            Assert.False(result is null or DBNull, $"Expected table {table} to exist.");
        }

        var functions = new List<string>();
        await using (var command = new NpgsqlCommand("""
            SELECT p.proname
            FROM pg_catalog.pg_proc p
            JOIN pg_catalog.pg_namespace n ON n.oid = p.pronamespace
            WHERE n.nspname = 'public'
              AND p.proname = ANY(ARRAY[
                  'update_i_am_alive_time',
                  'insert_membership_version',
                  'insert_membership',
                  'update_membership'])
            ORDER BY p.proname;
            """, connection))
        await using (var reader = await command.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
                functions.Add(reader.GetString(0));
        }

        Assert.Equal(
            new[] { "insert_membership", "insert_membership_version", "update_i_am_alive_time", "update_membership" },
            functions);

        var queries = new Dictionary<string, string>(StringComparer.Ordinal);
        await using (var command = new NpgsqlCommand(
            "SELECT QueryKey, QueryText FROM OrleansQuery ORDER BY QueryKey",
            connection))
        await using (var reader = await command.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
                queries.Add(reader.GetString(0), reader.GetString(1));
        }

        Assert.Equal(ExpectedQueryKeys, queries.Keys);
        Assert.Contains("@DeploymentId", queries["InsertMembershipKey"], StringComparison.Ordinal);
        Assert.Contains("@Version", queries["InsertMembershipKey"], StringComparison.Ordinal);
        Assert.Contains("@IAmAliveTime", queries["CleanupDefunctSiloEntriesKey"], StringComparison.Ordinal);

        return queries.Select(pair => $"{pair.Key}\n{pair.Value}").ToArray();
    }

    private static async Task<IReadOnlyList<string>> AssertEntrypointSchemaAsync(string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();

        var tables = new List<string>();
        await using (var command = new NpgsqlCommand("""
            SELECT table_name
            FROM information_schema.tables
            WHERE table_schema = 'public'
              AND table_name = ANY(ARRAY[
                  'User',
                  '__EFMigrationsHistory',
                  'orleansquery',
                  'orleansmembershipversiontable',
                  'orleansmembershiptable'])
            ORDER BY table_name;
            """, connection))
        await using (var reader = await command.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
                tables.Add(reader.GetString(0));
        }

        var expectedTables = new[]
        {
            "User",
            "__EFMigrationsHistory",
            "orleansmembershiptable",
            "orleansmembershipversiontable",
            "orleansquery",
        };
        Assert.Equal(
            expectedTables.OrderBy(value => value, StringComparer.Ordinal),
            tables.OrderBy(value => value, StringComparer.Ordinal));

        var migrationIds = new List<string>();
        await using (var command = new NpgsqlCommand(
            "SELECT \"MigrationId\" FROM \"__EFMigrationsHistory\" ORDER BY \"MigrationId\"",
            connection))
        await using (var reader = await command.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
                migrationIds.Add(reader.GetString(0));
        }

        Assert.NotEmpty(migrationIds);
        return [.. tables.OrderBy(value => value, StringComparer.Ordinal), .. migrationIds];
    }

    private static async Task<MigrationProcessResult> RunCloudMigrationProcessAsync(
        string connectionString,
        string processDirectory)
    {
        var cloudAssembly = Path.Combine(AppContext.BaseDirectory, "Korat.Cloud.dll");
        Assert.True(File.Exists(cloudAssembly), $"Cloud entrypoint not copied to test output: {cloudAssembly}");

        var startInfo = new ProcessStartInfo
        {
            FileName = ResolveDotnetHost(),
            WorkingDirectory = processDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add(cloudAssembly);
        startInfo.ArgumentList.Add("migrate");

        ConfigureMigrationEnvironment(startInfo.Environment, connectionString, processDirectory);

        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
            throw new InvalidOperationException("Failed to start the Korat.Cloud migration process.");

        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            await KillProcessAsync(process);
            throw new TimeoutException(
                "Korat.Cloud migrate did not exit within 60 seconds.\n" +
                $"stdout:\n{Tail(await standardOutput)}\n" +
                $"stderr:\n{Tail(await standardError)}");
        }
        finally
        {
            if (!process.HasExited)
                await KillProcessAsync(process);
        }

        return new MigrationProcessResult(
            process.ExitCode,
            await standardOutput,
            await standardError);
    }

    private static string ResolveDotnetHost()
    {
        var configured = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
        if (!string.IsNullOrWhiteSpace(configured))
            return configured;

        var currentProcess = Environment.ProcessPath;
        if (currentProcess is not null
            && string.Equals(Path.GetFileNameWithoutExtension(currentProcess), "dotnet", StringComparison.OrdinalIgnoreCase))
        {
            return currentProcess;
        }

        return "dotnet";
    }

    private static void ConfigureMigrationEnvironment(
        IDictionary<string, string?> environment,
        string connectionString,
        string processDirectory)
    {
        // Do not let a developer/CI host's production services, secrets, redirects, startup
        // hooks, or topology leak into this child. Add back only explicit test settings.
        foreach (var variable in environment.Keys.ToArray())
        {
            if (IsExternalConfiguration(variable))
                environment.Remove(variable);
        }

        environment["ASPNETCORE_ENVIRONMENT"] = "Testing";
        environment["DOTNET_ENVIRONMENT"] = "Testing";
        environment["ConnectionStrings__Korat"] = connectionString;
        environment["Korat__DataProtectionKeyPath"] = Path.Combine(processDirectory, "data-protection");
    }

    private static bool IsExternalConfiguration(string variable) =>
        string.Equals(variable, "DATABASE_URL", StringComparison.OrdinalIgnoreCase)
        || string.Equals(variable, "FORWARDEDHEADERS_ENABLED", StringComparison.OrdinalIgnoreCase)
        || variable.StartsWith("ConnectionStrings__", StringComparison.OrdinalIgnoreCase)
        || variable.StartsWith("ASPNETCORE_", StringComparison.OrdinalIgnoreCase)
        || variable.StartsWith("DOTNET_", StringComparison.OrdinalIgnoreCase)
        || variable.StartsWith("KORAT_", StringComparison.OrdinalIgnoreCase)
        || variable.StartsWith("Korat__", StringComparison.OrdinalIgnoreCase)
        || variable.StartsWith("FLY_", StringComparison.OrdinalIgnoreCase)
        || variable.StartsWith("NATS_", StringComparison.OrdinalIgnoreCase)
        || variable.StartsWith("OTEL_", StringComparison.OrdinalIgnoreCase)
        || variable.StartsWith("SENTRY_", StringComparison.OrdinalIgnoreCase)
        || variable.StartsWith("Sso__", StringComparison.OrdinalIgnoreCase)
        || variable.StartsWith("GitHubAuth__", StringComparison.OrdinalIgnoreCase)
        || variable.StartsWith("GoogleAuth__", StringComparison.OrdinalIgnoreCase)
        || variable.StartsWith("OpenIddict__", StringComparison.OrdinalIgnoreCase)
        || variable.StartsWith("Resend__", StringComparison.OrdinalIgnoreCase);

    private static async Task KillProcessAsync(Process process)
    {
        if (process.HasExited)
            return;

        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            return;
        }

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await process.WaitForExitAsync(timeout.Token);
    }

    private static string FormatProcessFailure(string attempt, MigrationProcessResult result) =>
        $"Korat.Cloud migrate {attempt} run exited with {result.ExitCode}.\n" +
        $"stdout:\n{Tail(result.StandardOutput)}\n" +
        $"stderr:\n{Tail(result.StandardError)}";

    private static string Tail(string value) => value.Length <= 8_000 ? value : value[^8_000..];

    private static void DeleteProcessDirectory(string processDirectory)
    {
        var leaf = Path.GetFileName(processDirectory);
        if (!leaf.StartsWith("korat-cloud-migrate-", StringComparison.Ordinal)
            || !Path.GetFullPath(processDirectory).StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Refusing to delete unexpected process directory: {processDirectory}");
        }

        if (Directory.Exists(processDirectory))
            Directory.Delete(processDirectory, recursive: true);
    }

    private static async Task ExecuteAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<IReadOnlyList<string>> ReadMembershipSnapshotAsync(string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("""
            SELECT 'version:' || to_jsonb(v)::text FROM OrleansMembershipVersionTable v
            UNION ALL
            SELECT 'member:' || to_jsonb(m)::text FROM OrleansMembershipTable m
            ORDER BY 1;
            """, connection);
        await using var reader = await command.ExecuteReaderAsync();
        var result = new List<string>();
        while (await reader.ReadAsync())
            result.Add(reader.GetString(0));
        return result;
    }

    private static async Task WithFreshDatabaseAsync(Func<string, Task> test)
    {
        PostgreSqlContainer? postgres = null;
        var adminConnectionString = Environment.GetEnvironmentVariable("KORAT_TEST_POSTGRES");
        var databaseName = $"korat_orleans_{Guid.NewGuid():N}";
        NpgsqlConnectionStringBuilder? adminBuilder = null;
        var created = false;
        try
        {
            if (string.IsNullOrWhiteSpace(adminConnectionString))
            {
                postgres = new PostgreSqlBuilder("postgres:16-alpine").Build();
                await postgres.StartAsync();
                adminConnectionString = postgres.GetConnectionString();
            }

            adminBuilder = new NpgsqlConnectionStringBuilder(adminConnectionString)
            {
                Pooling = false,
            };
            var testBuilder = new NpgsqlConnectionStringBuilder(adminConnectionString)
            {
                Database = databaseName,
                Pooling = false,
            };

            await using (var connection = new NpgsqlConnection(adminBuilder.ConnectionString))
            {
                await connection.OpenAsync();
                await using var command = new NpgsqlCommand($"CREATE DATABASE {QuoteIdentifier(databaseName)}", connection);
                await command.ExecuteNonQueryAsync();
                created = true;
            }

            await test(testBuilder.ConnectionString);
        }
        finally
        {
            try
            {
                if (created && adminBuilder is not null)
                {
                    await using var connection = new NpgsqlConnection(adminBuilder.ConnectionString);
                    await connection.OpenAsync();
                    await using var command = new NpgsqlCommand(
                        $"DROP DATABASE {QuoteIdentifier(databaseName)} WITH (FORCE)",
                        connection);
                    await command.ExecuteNonQueryAsync();
                }
            }
            finally
            {
                if (postgres is not null)
                    await postgres.DisposeAsync();
            }
        }
    }

    private static string QuoteIdentifier(string value) =>
        $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";

    private sealed record MigrationProcessResult(int ExitCode, string StandardOutput, string StandardError);
}
