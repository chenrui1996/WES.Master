using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Wes.Wms.Features.MasterData;
using Wes.Wms.Infrastructure.Persistence;

namespace Wes.Wms;

/// <summary>Independent logical location state owner; never changes stock, occupancy or physical control.</summary>
public sealed class WmsLocationStateWriter : IAsyncDisposable
{
    private readonly WmsDbContext _database;
    private readonly LocationStateAccess _access;

    /// <summary>Requires explicit deployment connection, tenant, live authority and schema; never migrates or seeds.</summary>
    public WmsLocationStateWriter(string connection, Guid tenant, IMasterDataAuthorization authority, IWarehouseLocationSchema schema,
        TimeProvider clock, ILoggerFactory logs)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connection);
        ArgumentNullException.ThrowIfNull(authority); ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(clock); ArgumentNullException.ThrowIfNull(logs);
        _database = new(new DbContextOptionsBuilder<WmsDbContext>().UseMySQL(connection,
            mysql => mysql.MigrationsHistoryTable("__WesWmsMigrations")).Options, tenant);
        _access = new(_database, authority, schema, clock, logs.CreateLogger<LocationStateAccess>());
    }

    /// <summary>Versioned, currently authorized transition. Recover uncertainty with the same original tuple and a fresh owner; read current state separately.</summary>
    public Task<LocationStateResult> ChangeAsync(MasterDataSubject subject, Guid id, LocationStateCommand command,
        CancellationToken cancellationToken = default) => _access.ChangeAsync(subject, id, command, cancellationToken);

    /// <summary>Disposes the private owner without deleting retained facts or identities.</summary>
    public ValueTask DisposeAsync() => _database.DisposeAsync();
}
