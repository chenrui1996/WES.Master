using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Wes.Wms.Features.MasterData;
using Wes.Wms.Infrastructure.Persistence;

namespace Wes.Wms;

/// <summary>Independent logical warehouse state owner; never enables locations, changes inventory or issues physical commands.</summary>
public sealed class WmsWarehouseStateWriter : IAsyncDisposable
{
    private readonly WmsDbContext _database;
    private readonly WarehouseStateAccess _access;

    /// <summary>Requires explicit deployment connection, tenant, live authority and clock; never applies migrations.</summary>
    public WmsWarehouseStateWriter(string connection, Guid tenant, IMasterDataAuthorization authority, TimeProvider clock, ILoggerFactory logs)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connection);
        ArgumentNullException.ThrowIfNull(authority); ArgumentNullException.ThrowIfNull(clock); ArgumentNullException.ThrowIfNull(logs);
        _database = new(new DbContextOptionsBuilder<WmsDbContext>().UseMySQL(connection,
            mysql => mysql.MigrationsHistoryTable("__WesWmsMigrations")).Options, tenant);
        _access = new(_database, authority, clock, logs.CreateLogger<WarehouseStateAccess>());
    }

    /// <summary>
    /// Changes a versioned logical state with current resource/field authority. After uncertainty, recover the same original tuple
    /// with a fresh owner; cancellation or lost response does not prove rollback. Read current detail separately.
    /// </summary>
    public Task<WarehouseStateResult> ChangeAsync(MasterDataSubject subject, Guid id, WarehouseStateCommand command,
        CancellationToken cancellationToken = default) => _access.ChangeAsync(subject, id, command, cancellationToken);

    /// <summary>Disposes the private owner without deleting retained identities or state history.</summary>
    public ValueTask DisposeAsync() => _database.DisposeAsync();
}
