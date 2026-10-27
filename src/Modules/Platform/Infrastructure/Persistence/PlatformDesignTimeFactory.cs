using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Wes.Platform.Infrastructure.Persistence;

/// <summary>EF migration tooling only; no connection is configured or opened and no user data is seeded.</summary>
public sealed class PlatformDesignTimeFactory : IDesignTimeDbContextFactory<PlatformDbContext>
{
    /// <summary>Builds provider metadata for migration generation; applying migrations needs an authorized runtime connection.</summary>
    public PlatformDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<PlatformDbContext>()
            .UseMySQL(mysql => mysql.MigrationsHistoryTable("__WesPlatformMigrations")).Options;
        return new PlatformDbContext(options, new Guid("00000000-0000-0000-0000-000000000001"));
    }
}
