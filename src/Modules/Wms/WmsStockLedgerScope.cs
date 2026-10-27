using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Wes.Wms.Features.Inventory;
using Wes.Wms.Features.Ledger;
using Wes.Wms.Infrastructure.Persistence;

namespace Wes.Wms;

/// <summary>Independent read-only WMS ledger owner without exposed persistence, migrations or posting commands.</summary>
public sealed class WmsStockLedgerScope : IAsyncDisposable
{
    private readonly WmsDbContext _database;
    private readonly StockLedgerAccess _access;

    /// <summary>Requires explicit deployment connection, tenant, live ledger All-field authority and safe owner logging.</summary>
    public WmsStockLedgerScope(string connection, Guid tenant, IStockLedgerAuthorization authorization, ILoggerFactory logs)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connection);
        ArgumentNullException.ThrowIfNull(authorization);
        ArgumentNullException.ThrowIfNull(logs);
        _database = new(new DbContextOptionsBuilder<WmsDbContext>().UseMySQL(connection,
            mysql => mysql.MigrationsHistoryTable("__WesWmsMigrations")).Options, tenant);
        _access = new(_database, authorization, logs.CreateLogger<StockLedgerAccess>());
    }

    /// <summary>Reads one bounded immutable history page under current tenant-wide authority.</summary>
    public Task<StockLedgerPage> QueryAsync(InventorySubject subject, StockLedgerQuery query, CancellationToken cancellationToken = default) =>
        _access.QueryAsync(subject, query, cancellationToken);

    /// <summary>Disposes the private owner without modifying retained inventory facts.</summary>
    public ValueTask DisposeAsync() => _database.DisposeAsync();
}
