using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Wes.Wms.Infrastructure.Persistence;

/// <summary>Connectionless WMS migration metadata only; no runtime tenant discovery or seed.</summary>
public sealed class WmsDesignTimeFactory : IDesignTimeDbContextFactory<WmsDbContext>
{
    /// <inheritdoc />
    public WmsDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<WmsDbContext>().UseMySQL().Options,
            new Guid("00000000-0000-0000-0000-000000000001"));
}
