using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Korat.Persistence;

public sealed class KoratDbContextFactory : IDesignTimeDbContextFactory<KoratDbContext>
{
    public KoratDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<KoratDbContext>()
            .UseNpgsql("Host=localhost;Database=korat;Username=korat;Password=korat")
            .Options;
        return new KoratDbContext(options);
    }
}
