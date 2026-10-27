using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Wes.Wms.Features.Inventory;
using Wes.Wms.Infrastructure.Persistence;

namespace Wes.Wms;

/// <summary>Independent read-only WMS inventory owner; never exposes a DbContext, applies migrations or writes book state.</summary>
public sealed class WmsInventoryScope : IAsyncDisposable
{
    private readonly WmsDbContext _database;
    private readonly InventoryAccess _access;

    /// <summary>Requires explicit deployment connection, tenant, live All-field authority and safe owner logging.</summary>
    public WmsInventoryScope(string connection, Guid tenant, IInventoryAuthorization authorization, ILoggerFactory logs)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connection);
        ArgumentNullException.ThrowIfNull(authorization);
        ArgumentNullException.ThrowIfNull(logs);
        _database = new(new DbContextOptionsBuilder<WmsDbContext>().UseMySQL(connection,
            mysql => mysql.MigrationsHistoryTable("__WesWmsMigrations")).Options, tenant);
        _access = new(_database, authorization, logs.CreateLogger<InventoryAccess>());
    }

    /// <summary>Reads one consistently observed page with current tenant-wide authority; cancellation never returns a partial page.</summary>
    public Task<InventoryPage> QueryAsync(InventorySubject subject, InventoryQuery query, CancellationToken cancellationToken = default) =>
        _access.QueryAsync(subject, query, cancellationToken);

    /// <summary>Disposes the private owner without deleting stock, ledger or protection identities.</summary>
    public ValueTask DisposeAsync() => _database.DisposeAsync();
}
