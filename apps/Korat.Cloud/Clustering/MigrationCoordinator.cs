namespace Korat.Cloud.Clustering;

/// <summary>
/// Keeps application and Orleans schema migration in one ordered operation. Environment and
/// topology decisions belong to the caller; in particular, the migration-only job does not
/// need an advertised silo IP in order to prepare the database.
/// </summary>
internal static class MigrationCoordinator
{
    internal static async Task RunAsync(
        string connectionString,
        Func<CancellationToken, Task> ensureApplicationSchemaAsync,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentNullException.ThrowIfNull(ensureApplicationSchemaAsync);

        await ensureApplicationSchemaAsync(cancellationToken);
        await OrleansAdoNetSchema.EnsureAsync(connectionString, cancellationToken);
    }
}
