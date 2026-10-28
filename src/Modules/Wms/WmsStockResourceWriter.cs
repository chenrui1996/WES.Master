using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Wes.Wms.Features.MasterData;
using Wes.Wms.Infrastructure.Persistence;

namespace Wes.Wms;

/// <summary>Independent legal-resource registration owner. Never initializes nonzero stock or claims occupancy/physical readiness.</summary>
public sealed class WmsStockResourceWriter : IAsyncDisposable
{
    private readonly WmsDbContext _database;
    private readonly StockResourceAccess _access;

    /// <summary>Requires explicit tenant/current authority/schema. Does not migrate, seed, grant permissions or connect to production implicitly.</summary>
    public WmsStockResourceWriter(string connection, Guid tenant, IMasterDataAuthorization authority, IWarehouseLocationSchema schema,
        TimeProvider clock, ILoggerFactory logs)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connection);
        ArgumentNullException.ThrowIfNull(authority); ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(clock); ArgumentNullException.ThrowIfNull(logs);
        _database = new(new DbContextOptionsBuilder<WmsDbContext>().UseMySQL(connection,
            mysql => mysql.MigrationsHistoryTable("__WesWmsMigrations")).Options, tenant);
        _access = new(_database, authority, schema, clock, logs.CreateLogger<StockResourceAccess>());
    }

    /// <summary>Registers a base unit or recovers the exact original operation under current write authority.</summary>
    public Task<StockResourceResult> RegisterUnitAsync(MasterDataSubject subject, RegisterUnit request, CancellationToken ct = default) =>
        _access.UnitAsync(subject, request, ct);
    /// <summary>Registers an immutable material against a currently readable real unit.</summary>
    public Task<StockResourceResult> RegisterItemAsync(MasterDataSubject subject, RegisterItem request, CancellationToken ct = default) =>
        _access.ItemAsync(subject, request, ct);
    /// <summary>Registers an unpositioned container against a currently readable real warehouse.</summary>
    public Task<StockResourceResult> RegisterContainerAsync(MasterDataSubject subject, RegisterContainer request, CancellationToken ct = default) =>
        _access.ContainerAsync(subject, request, ct);
    /// <summary>Registers a new exact empty stock dimension; no quantity, occupancy, reservation or transport is created.</summary>
    public Task<StockResourceResult> RegisterDimensionAsync(MasterDataSubject subject, RegisterStockDimension request, CancellationToken ct = default) =>
        _access.DimensionAsync(subject, request, ct);
    /// <summary>Reads the complete unit projection under current actual creator/field authority.</summary>
    public Task<StockResourceReadResult<StockUnitDefinition>> ReadUnitAsync(MasterDataSubject subject, Guid id, CancellationToken ct = default) =>
        _access.ReadUnitAsync(subject, id, ct);
    /// <summary>Reads the complete material projection under current actual creator/field authority.</summary>
    public Task<StockResourceReadResult<StockItemDefinition>> ReadItemAsync(MasterDataSubject subject, Guid id, CancellationToken ct = default) =>
        _access.ReadItemAsync(subject, id, ct);
    /// <summary>Reads the complete container projection without claiming location or availability.</summary>
    public Task<StockResourceReadResult<StockContainerDefinition>> ReadContainerAsync(MasterDataSubject subject, Guid id, CancellationToken ct = default) =>
        _access.ReadContainerAsync(subject, id, ct);
    /// <summary>Disposes the private owner while retaining all permanent facts.</summary>
    public ValueTask DisposeAsync() => _database.DisposeAsync();
}
