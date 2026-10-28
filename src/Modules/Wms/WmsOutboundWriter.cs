using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Wes.Wms.Features.MasterData;
using Wes.Wms.Features.Outbound;
using Wes.Wms.Infrastructure.Persistence;

namespace Wes.Wms;

/// <summary>Independent WMS owner for durable outbound documents, reservations, picking and direct shipment.</summary>
public sealed class WmsOutboundWriter : IAsyncDisposable
{
    private readonly WmsDbContext _database;
    private readonly OutboundAccess _access;

    /// <summary>Requires an explicit connection, trusted tenant, current authority and location-schema adapter.</summary>
    public WmsOutboundWriter(string connection, Guid tenant, IMasterDataAuthorization authorization,
        IWarehouseLocationSchema schema, TimeProvider clock, ILoggerFactory logs)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connection);
        ArgumentNullException.ThrowIfNull(authorization);
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(logs);
        _database = new(new DbContextOptionsBuilder<WmsDbContext>().UseMySQL(connection,
            mysql => mysql.MigrationsHistoryTable("__WesWmsMigrations")).Options, tenant);
        _access = new(_database, authorization, schema, clock, logs.CreateLogger<OutboundAccess>());
    }

    /// <summary>Creates a draft from explicit legal source balances; this never changes inventory.</summary>
    public Task<OutboundResult> CreateAsync(MasterDataSubject subject, OutboundDraftRequest request, CancellationToken ct = default) =>
        _access.CreateAsync(subject, request, ct);
    /// <summary>Atomically reserves every planned source balance or none of them.</summary>
    public Task<OutboundResult> ApproveAsync(MasterDataSubject subject, Guid id, OutboundApprovalRequest request,
        Guid operation, Guid correlation, CancellationToken ct = default) =>
        _access.ApproveAsync(subject, id, request, operation, correlation, ct);
    /// <summary>Records incremental picked and shortage quantities without changing stock.</summary>
    public Task<OutboundResult> PickAsync(MasterDataSubject subject, Guid id, OutboundPickRequest request,
        Guid operation, Guid correlation, CancellationToken ct = default) =>
        _access.PickAsync(subject, id, request, operation, correlation, ct);
    /// <summary>Releases only a reviewed, owner-confirmed unexecuted shortage.</summary>
    public Task<OutboundResult> ReleaseShortageAsync(MasterDataSubject subject, Guid id, OutboundShortageReleaseRequest request,
        Guid operation, Guid correlation, CancellationToken ct = default) =>
        _access.ReleaseShortageAsync(subject, id, request, operation, correlation, ct);
    /// <summary>Posts one direct shipment per line against its exact approved reservation.</summary>
    public Task<OutboundResult> ShipAsync(MasterDataSubject subject, Guid id, OutboundShipmentRequest request,
        Guid operation, Guid correlation, CancellationToken ct = default) =>
        _access.ShipAsync(subject, id, request, operation, correlation, ct);
    /// <summary>Cancels only work with no picked, shipped, in-transit or unknown execution.</summary>
    public Task<OutboundResult> CancelAsync(MasterDataSubject subject, Guid id, OutboundStateRequest request,
        Guid operation, Guid correlation, CancellationToken ct = default) =>
        _access.CancelAsync(subject, id, request, operation, correlation, ct);
    /// <summary>Closes only the remaining open quantity after at least one actual shipment.</summary>
    public Task<OutboundResult> CloseRemainderAsync(MasterDataSubject subject, Guid id, OutboundStateRequest request,
        Guid operation, Guid correlation, CancellationToken ct = default) =>
        _access.CloseRemainderAsync(subject, id, request, operation, correlation, ct);
    /// <summary>Reads one complete current projection after current creator-based authorization.</summary>
    public Task<OutboundReadResult> ReadAsync(MasterDataSubject subject, Guid id, CancellationToken ct = default) =>
        _access.ReadAsync(subject, id, ct);
    /// <summary>Disposes the private owner without deleting retained outbound facts.</summary>
    public ValueTask DisposeAsync() => _database.DisposeAsync();
}
