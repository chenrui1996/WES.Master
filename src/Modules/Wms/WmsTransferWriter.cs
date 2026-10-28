using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Wes.Wms.Features.MasterData;
using Wes.Wms.Features.Transfers;
using Wes.Wms.Infrastructure.Persistence;

namespace Wes.Wms;

/// <summary>Independent WMS owner for indivisible multi-line transfer approval and recoverable per-unit posting.</summary>
public sealed class WmsTransferWriter : IAsyncDisposable
{
    private readonly WmsDbContext _database;
    private readonly TransferAccess _access;
    /// <summary>Requires trusted tenant, current authority and an explicit schema adapter.</summary>
    public WmsTransferWriter(string connection, Guid tenant, IMasterDataAuthorization authorization,
        IWarehouseLocationSchema schema, TimeProvider clock, ILoggerFactory logs)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connection);
        ArgumentNullException.ThrowIfNull(authorization);
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(logs);
        _database = new(new DbContextOptionsBuilder<WmsDbContext>().UseMySQL(connection,
            mysql => mysql.MigrationsHistoryTable("__WesWmsMigrations")).Options, tenant);
        _access = new(_database, authorization, schema, clock, logs.CreateLogger<TransferAccess>());
    }
    /// <summary>Creates an immutable unreserved draft.</summary>
    public Task<TransferResult> CreateAsync(MasterDataSubject subject, TransferDraftRequest request, CancellationToken ct = default) =>
        _access.CreateAsync(subject, request, ct);
    /// <summary>Reserves every whole-container source or none.</summary>
    public Task<TransferResult> ApproveAsync(MasterDataSubject subject, Guid id, TransferApprovalRequest request,
        Guid operation, Guid correlation, CancellationToken ct = default) => _access.ApproveAsync(subject, id, request, operation, correlation, ct);
    /// <summary>Explicitly dispatches one manual unit and retains protection.</summary>
    public Task<TransferResult> DispatchAsync(MasterDataSubject subject, Guid id, TransferLineRequest request,
        Guid operation, Guid correlation, CancellationToken ct = default) => _access.DispatchAsync(subject, id, request, operation, correlation, ct);
    /// <summary>Commits exact arrival evidence before attempting ledger application.</summary>
    public Task<TransferResult> ArriveAsync(MasterDataSubject subject, Guid id, TransferArrivalRequest request,
        Guid operation, Guid correlation, CancellationToken ct = default) => _access.ArriveAsync(subject, id, request, operation, correlation, ct);
    /// <summary>Applies only a durable arrived unit, without initiating movement.</summary>
    public Task<TransferResult> SyncAsync(MasterDataSubject subject, Guid id, TransferLineRequest request,
        Guid operation, Guid correlation, CancellationToken ct = default) => _access.SyncAsync(subject, id, request, operation, correlation, ct);
    /// <summary>Records unresolved physical effects without releasing any protection.</summary>
    public Task<TransferResult> UnknownAsync(MasterDataSubject subject, Guid id, TransferUnknownRequest request,
        Guid operation, Guid correlation, CancellationToken ct = default) => _access.UnknownAsync(subject, id, request, operation, correlation, ct);
    /// <summary>Closes only safe unstarted remainder, preserving all moved units.</summary>
    public Task<TransferResult> CloseAsync(MasterDataSubject subject, Guid id, TransferClosureRequest request,
        Guid operation, Guid correlation, CancellationToken ct = default) => _access.CloseAsync(subject, id, request, operation, correlation, ct);
    /// <summary>Reads one complete currently authorized consistent projection.</summary>
    public Task<TransferReadResult> ReadAsync(MasterDataSubject subject, Guid id, CancellationToken ct = default) =>
        _access.ReadAsync(subject, id, ct);
    /// <summary>Disposes the private owner, retaining permanent facts.</summary>
    public ValueTask DisposeAsync() => _database.DisposeAsync();
}
