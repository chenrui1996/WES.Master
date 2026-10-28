using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Wes.Wms.Features.Counts;
using Wes.Wms.Features.MasterData;
using Wes.Wms.Infrastructure.Persistence;

namespace Wes.Wms;

/// <summary>Independent WMS owner for blind counts, permanent local protection and authorized atomic ledger adjustment.</summary>
public sealed class WmsCountWriter : IAsyncDisposable
{
    private readonly WmsDbContext _database;
    private readonly CountAccess _access;
    /// <summary>Requires an explicit connection, trusted tenant, current authority, schema and UTC clock.</summary>
    public WmsCountWriter(string connection, Guid tenant, IMasterDataAuthorization authorization,
        IWarehouseLocationSchema schema, TimeProvider clock, ILoggerFactory logs)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connection);
        ArgumentNullException.ThrowIfNull(authorization); ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(clock); ArgumentNullException.ThrowIfNull(logs);
        _database = new(new DbContextOptionsBuilder<WmsDbContext>().UseMySQL(connection,
            mysql => mysql.MigrationsHistoryTable("__WesWmsMigrations")).Options, tenant);
        _access = new(_database, authorization, schema, clock);
    }
    /// <summary>Creates one fixed legal location scope without selecting count lines.</summary>
    public Task<CountResult> CreateAsync(MasterDataSubject subject, CountCreateRequest request, Guid operation, Guid correlation, CancellationToken ct = default) =>
        _access.CreateAsync(subject, request, operation, correlation, ct);
    /// <summary>Acquires durable protection and captures all lawful dimensions in a fresh generation.</summary>
    public Task<CountResult> StartAsync(MasterDataSubject subject, Guid id, CountStartRequest request, Guid operation, Guid correlation, CancellationToken ct = default) =>
        _access.StartAsync(subject, id, request, operation, correlation, ct);
    /// <summary>Appends a scanned measurement or revision against the current generation.</summary>
    public Task<CountResult> RecordAsync(MasterDataSubject subject, Guid id, CountRecordRequest request, Guid operation, Guid correlation, CancellationToken ct = default) =>
        _access.RecordAsync(subject, id, request, operation, correlation, ct);
    /// <summary>Confirms every difference in one ledger transaction and releases only this generation's lock.</summary>
    public Task<CountResult> AdjustAsync(MasterDataSubject subject, Guid id, CountStateRequest request, Guid operation, Guid correlation, CancellationToken ct = default) =>
        _access.AdjustAsync(subject, id, request, operation, correlation, ct);
    /// <summary>Invalidates all measurements while retaining history and explicitly releasing local protection.</summary>
    public Task<CountResult> InvalidateAsync(MasterDataSubject subject, Guid id, CountStateRequest request, Guid operation, Guid correlation, CancellationToken ct = default) =>
        _access.InvalidateAsync(subject, id, request, operation, correlation, ct);
    /// <summary>Reads blind measurements only; no private book quantity or balance version can be projected.</summary>
    public Task<CountReadResult> ReadAsync(MasterDataSubject subject, Guid id, CancellationToken ct = default) => _access.ReadAsync(subject, id, ct);
    /// <summary>Reads private differences under separate complete review authority.</summary>
    public Task<CountReviewResult> ReviewAsync(MasterDataSubject subject, Guid id, CancellationToken ct = default) => _access.ReviewAsync(subject, id, ct);
    /// <summary>Disposes the private owner without releasing durable count locks.</summary>
    public ValueTask DisposeAsync() => _database.DisposeAsync();
}
