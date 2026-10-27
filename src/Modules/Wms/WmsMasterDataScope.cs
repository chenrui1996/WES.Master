using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Wes.Wms.Features.MasterData;
using Wes.Wms.Infrastructure.Persistence;

namespace Wes.Wms;

/// <summary>Clean WMS master-data read boundary; no persistence handle, default authority, migration or device control.</summary>
public sealed class WmsMasterDataScope : IAsyncDisposable
{
    private readonly WmsDbContext _database;
    private readonly MasterDataAccess _access;

    /// <summary>Requires deployment-only connection material and current authority; observation timestamps use the supplied clock or system UTC. No migrations.</summary>
    public WmsMasterDataScope(string connection, Guid tenant, IMasterDataAuthorization authorization, ILoggerFactory logs, TimeProvider? clock = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connection);
        ArgumentNullException.ThrowIfNull(authorization);
        ArgumentNullException.ThrowIfNull(logs);
        _database = new WmsDbContext(new DbContextOptionsBuilder<WmsDbContext>().UseMySQL(connection,
            mysql => mysql.MigrationsHistoryTable("__WesWmsMigrations")).Options, tenant);
        _access = new MasterDataAccess(_database, authorization, logs.CreateLogger<MasterDataAccess>(), clock);
    }

    /// <summary>Returns a full warehouse projection only after current provenance-based field authorization; cancellation propagates.</summary>
    public Task<MasterDataReadResult> ReadWarehouseAsync(MasterDataSubject subject, Guid id, CancellationToken cancellationToken = default) =>
        _access.ReadWarehouseAsync(subject, id, cancellationToken);

    /// <summary>Returns a full pinned location projection only after current provenance-based field authorization; no stock is read or changed.</summary>
    public Task<MasterDataReadResult> ReadLocationAsync(MasterDataSubject subject, Guid id, CancellationToken cancellationToken = default) =>
        _access.ReadLocationAsync(subject, id, cancellationToken);

    /// <summary>Checks full warehouse read plus exact current Attach/Detach write authority against WMS-owned provenance.</summary>
    public Task<MasterDataReadDecision> AuthorizeWarehouseFileAsync(MasterDataSubject subject, Guid id,
        MasterDataAttachmentChange action, CancellationToken cancellationToken = default) =>
        _access.AuthorizeFilesAsync(subject, id, location: false, action, cancellationToken);

    /// <summary>Checks full location read plus exact current Attach/Detach write authority; no reference or resource is mutated.</summary>
    public Task<MasterDataReadDecision> AuthorizeLocationFileAsync(MasterDataSubject subject, Guid id,
        MasterDataAttachmentChange action, CancellationToken cancellationToken = default) =>
        _access.AuthorizeFilesAsync(subject, id, location: true, action, cancellationToken);

    /// <summary>Returns realtime warehouses filtered by current complete-field creator scope before bounded stable paging; no total.</summary>
    public Task<MasterDataCatalogResult> QueryWarehousesAsync(MasterDataSubject subject, MasterDataQuery query, CancellationToken cancellationToken = default) =>
        _access.QueryWarehousesAsync(subject, query, cancellationToken);

    /// <summary>Returns realtime locations filtered by current authority and exact optional warehouse/code/status; no stock or writes.</summary>
    public Task<MasterDataCatalogResult> QueryLocationsAsync(MasterDataSubject subject, MasterDataQuery query, CancellationToken cancellationToken = default) =>
        _access.QueryLocationsAsync(subject, query, cancellationToken);

    /// <summary>Returns safe creation-event metadata and durable delivery state after current message-field and creator authorization.</summary>
    public Task<MasterDataEventPage> QueryCreationEventsAsync(MasterDataSubject subject, MasterDataEventQuery query, CancellationToken cancellationToken = default) =>
        _access.QueryCreationEventsAsync(subject, query, cancellationToken);

    /// <summary>Counts pending creation events in the current complete-field creator scope; errors/cancellation never become zero.</summary>
    public Task<MasterDataEventSummaryResult> ReadCreationEventSummaryAsync(MasterDataSubject subject, CancellationToken cancellationToken = default) =>
        _access.ReadCreationEventSummaryAsync(subject, cancellationToken);

    /// <summary>Disposes this owner; no authority, connection or business state is retained by a global cache.</summary>
    public ValueTask DisposeAsync() => _database.DisposeAsync();
}
