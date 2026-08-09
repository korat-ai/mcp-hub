using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Korat.Persistence.Tests.Infrastructure;

public sealed class PersistenceTestFixture : IDisposable
{
    public InMemoryDatabaseRoot Root { get; } = new();
    public string DatabaseName { get; } = Guid.NewGuid().ToString("N");

    public IDbContextFactory<KoratDbContext> CreateFactory() =>
        new TestDbContextFactory(Root, DatabaseName);

    public EfMetadataRepository CreateRepository() =>
        new(CreateFactory());

    public void Dispose() { }

    private sealed class TestDbContextFactory(InMemoryDatabaseRoot root, string name) : IDbContextFactory<KoratDbContext>
    {
        public KoratDbContext CreateDbContext() =>
            new(new DbContextOptionsBuilder<KoratDbContext>()
                .UseInMemoryDatabase(name, root)
                .Options);

        public Task<KoratDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());
    }
}
