using System.Data;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using MySql.Data.MySqlClient;
using Wes.Wms.Domain;
using Wes.Wms.Features.Ledger;
using Wes.Wms.Infrastructure.Persistence;

namespace Wes.Wms.Features.MasterData;

internal sealed partial class StockResourceAccess(WmsDbContext database, IMasterDataAuthorization authorization,
    IWarehouseLocationSchema schema, TimeProvider clock, ILogger<StockResourceAccess> logger)
{
    private static readonly string[] WarehouseRead = ["Id", "Code", "Name", "Enabled", "Version", "CreatedAt"];
    private static readonly string[] LocationRead = [.. WarehouseRead, "WarehouseId", "PackId", "SchemaVersion"];
    private static readonly string[] UnitRead = ["Id", "Code", "Name", "Countable", "Precision", "Version", "CreatedAt"];
    private static readonly string[] ItemRead = ["Id", "Code", "Name", "BaseUnitId", "BatchRequired", "Version", "CreatedAt"];
    private static readonly string[] ContainerRead = ["Id", "Code", "Name", "WarehouseId", "Version", "CreatedAt"];
    private static readonly string[] UnitWrite = ["Code", "Name", "Countable", "Precision"];
    private static readonly string[] ItemWrite = ["Code", "Name", "BaseUnitId", "BatchRequired"];
    private static readonly string[] ContainerWrite = ["Code", "Name", "WarehouseId"];
    private static readonly string[] DimensionWrite = ["WarehouseId", "LocationId", "ItemId", "Batch", "ContainerId"];

    internal Task<StockResourceResult> UnitAsync(MasterDataSubject subject, RegisterUnit request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return RunAsync(subject, request.OperationId, request.CorrelationId, async () =>
        {
            if (!ValidName(request.Code, request.Name) || request.Precision is < 0 or > 6 || request.Countable && request.Precision != 0)
                return Reject(StockResourceDecision.Invalid);
            Task<bool> Allowed() => FieldsAsync(subject, "wms.units.write", subject.ActorId, UnitWrite, ct);
            if (!await Allowed()) return Reject(StockResourceDecision.Forbidden);
            var old = await database.StockUnits.AsNoTracking().SingleOrDefaultAsync(row => row.OperationId == request.OperationId, ct);
            if (old is not null)
                return old.ActorId != subject.ActorId || old.Code != MasterDataValue.Code(request.Code) || old.Name != request.Name
                    || old.Countable != request.Countable || old.Precision != request.Precision
                    ? Reject(StockResourceDecision.Conflict) : await ReplayAsync(old.Id, old.Version, Allowed);
            if (await database.StockUnits.AnyAsync(row => row.Code == MasterDataValue.Code(request.Code), ct))
                return Reject(StockResourceDecision.Conflict);
            var unit = new StockUnit(database.TenantId, request.OperationId, subject.ActorId, request.CorrelationId,
                request.Code, request.Name, request.Countable, request.Precision, clock.GetUtcNow());
            database.StockUnits.Add(unit);
            return await SaveAsync(unit.Id, unit.Version, Allowed, ct);
        }, ct);
    }

    internal Task<StockResourceResult> ItemAsync(MasterDataSubject subject, RegisterItem request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return RunAsync(subject, request.OperationId, request.CorrelationId, async () =>
        {
            if (!ValidName(request.Code, request.Name) || request.BaseUnitId == Guid.Empty) return Reject(StockResourceDecision.Invalid);
            var unit = await database.StockUnits.AsNoTracking().SingleOrDefaultAsync(row => row.Id == request.BaseUnitId, ct);
            if (unit is null) return Reject(StockResourceDecision.NotFound);
            async Task<bool> Allowed() => await FieldsAsync(subject, "wms.items.write", subject.ActorId, ItemWrite, ct)
                && await FieldsAsync(subject, "wms.units.read", unit.ActorId, UnitRead, ct);
            if (!await Allowed()) return Reject(StockResourceDecision.Forbidden);
            var old = await database.StockItems.AsNoTracking().SingleOrDefaultAsync(row => row.OperationId == request.OperationId, ct);
            if (old is not null)
                return old.ActorId != subject.ActorId || old.Code != MasterDataValue.Code(request.Code) || old.Name != request.Name
                    || old.BaseUnitId != request.BaseUnitId || old.BatchRequired != request.BatchRequired
                    ? Reject(StockResourceDecision.Conflict) : await ReplayAsync(old.Id, old.Version, Allowed);
            if (await database.StockItems.AnyAsync(row => row.Code == MasterDataValue.Code(request.Code), ct))
                return Reject(StockResourceDecision.Conflict);
            var item = new StockItem(database.TenantId, request.OperationId, subject.ActorId, request.CorrelationId,
                request.Code, request.Name, request.BaseUnitId, request.BatchRequired, clock.GetUtcNow());
            database.StockItems.Add(item);
            return await SaveAsync(item.Id, item.Version, Allowed, ct);
        }, ct);
    }

    internal Task<StockResourceResult> ContainerAsync(MasterDataSubject subject, RegisterContainer request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return RunAsync(subject, request.OperationId, request.CorrelationId, async () =>
        {
            if (!ValidName(request.Code, request.Name) || request.WarehouseId == Guid.Empty) return Reject(StockResourceDecision.Invalid);
            var creator = await ParentCreatorAsync(request.WarehouseId, ct);
            if (creator is null) return Reject(StockResourceDecision.NotFound);
            async Task<bool> Allowed() => await FieldsAsync(subject, "wms.containers.write", subject.ActorId, ContainerWrite, ct)
                && await FieldsAsync(subject, "wms.warehouses.read", creator.Value, WarehouseRead, ct);
            if (!await Allowed()) return Reject(StockResourceDecision.Forbidden);
            var old = await database.StockContainers.AsNoTracking().SingleOrDefaultAsync(row => row.OperationId == request.OperationId, ct);
            if (old is not null)
                return old.ActorId != subject.ActorId || old.Code != MasterDataValue.Code(request.Code) || old.Name != request.Name
                    || old.WarehouseId != request.WarehouseId
                    ? Reject(StockResourceDecision.Conflict) : await ReplayAsync(old.Id, old.Version, Allowed);
            if (await database.StockContainers.AnyAsync(row => row.Code == MasterDataValue.Code(request.Code), ct))
                return Reject(StockResourceDecision.Conflict);
            var container = new StockContainer(database.TenantId, request.OperationId, subject.ActorId, request.CorrelationId,
                request.Code, request.Name, request.WarehouseId, clock.GetUtcNow());
            database.StockContainers.Add(container);
            return await SaveAsync(container.Id, container.Version, Allowed, ct);
        }, ct);
    }

    private async Task<StockResourceResult> RunAsync(MasterDataSubject subject, Guid operation, Guid correlation,
        Func<Task<StockResourceResult>> execute, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(subject); ct.ThrowIfCancellationRequested();
        if (database.ChangeTracker.Entries().Any() || database.Database.CurrentTransaction is not null
            || System.Transactions.Transaction.Current is not null)
            throw new InvalidOperationException("Resource registration requires a fresh independent WMS owner.");
        if (subject.TenantId != database.TenantId || subject.ActorId == Guid.Empty || subject.SessionId == Guid.Empty)
            return Reject(StockResourceDecision.Forbidden);
        if (operation == Guid.Empty || correlation == Guid.Empty) return Reject(StockResourceDecision.Invalid);
        try
        {
            // ReadCommitted avoids gap-lock upgrades on concurrent absent-code/operation probes.
            // Dimensions acquire explicit parent/location/container fences before their probes.
            for (var attempt = 0; attempt < 2; ++attempt)
            {
                await using var transaction = await database.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
                try
                {
                    var result = await execute();
                    ct.ThrowIfCancellationRequested();
                    if (result.Decision is StockResourceDecision.Created or StockResourceDecision.Replayed) await transaction.CommitAsync(ct);
                    else await transaction.RollbackAsync(ct);
                    return result;
                }
                catch (DbUpdateException error) when (error.InnerException is MySqlException { Number: 1062 } && attempt == 0)
                {
                    await transaction.RollbackAsync(ct);
                    // Re-resolve the committed winner once; never invent a receipt from a uniqueness failure.
                }
                finally { database.ChangeTracker.Clear(); }
            }
            throw new InvalidOperationException("Resource conflict resolution exhausted.");
        }
        catch (DbUpdateException error) when (error.InnerException is DbException failure)
        {
            ct.ThrowIfCancellationRequested(); logger.LogError("WMS resource registration failed {Code}", "DatabaseUnavailable");
            throw WmsDatabaseFailures.Safe(failure);
        }
        catch (DbException error)
        {
            ct.ThrowIfCancellationRequested(); logger.LogError("WMS resource registration failed {Code}", "DatabaseUnavailable");
            throw WmsDatabaseFailures.Safe(error);
        }
    }

    private async Task<StockResourceResult> SaveAsync(Guid id, string version, Func<Task<bool>> allowed, CancellationToken ct)
    {
        await database.SaveChangesAsync(ct);
        return await allowed() ? new(StockResourceDecision.Created, id, version) : Reject(StockResourceDecision.Forbidden);
    }

    private async Task<StockResourceResult> ReplayAsync(Guid id, string version, Func<Task<bool>> allowed) =>
        await allowed() ? new(StockResourceDecision.Replayed, id, version) : Reject(StockResourceDecision.Forbidden);

    private async Task<bool> FieldsAsync(MasterDataSubject subject, string permission, Guid creator, string[] required, CancellationToken ct)
    {
        var fields = await authorization.ReadFieldsAsync(subject, permission, creator, ct);
        ct.ThrowIfCancellationRequested();
        if (fields is null) throw new InvalidOperationException("Resource authority returned no decision.");
        var exact = fields.ToHashSet(StringComparer.Ordinal);
        return required.All(exact.Contains);
    }

    private Task<Guid?> ParentCreatorAsync(Guid warehouse, CancellationToken ct) => database.MasterDataChanges.AsNoTracking()
        .Where(row => row.WarehouseId == warehouse && row.LocationId == null).Select(row => (Guid?)row.ActorId).SingleOrDefaultAsync(ct);

    private static bool ValidName(string code, string name) => MasterDataValue.IsCode(code) && MasterDataValue.IsName(name);
    private StockResourceResult Reject(StockResourceDecision decision)
    {
        logger.LogWarning("WMS resource registration rejected {Code}", decision.ToString()); return new(decision);
    }

    private async Task FenceAsync(string table, Guid id, CancellationToken ct)
    {
        await using var command = database.Database.GetDbConnection().CreateCommand();
        command.Transaction = database.Database.CurrentTransaction!.GetDbTransaction();
        command.CommandText = $"SELECT Id FROM {table} WHERE TenantId = @tenant AND Id = @id FOR UPDATE";
        var tenant = command.CreateParameter(); tenant.ParameterName = "@tenant"; tenant.Value = database.TenantId.ToString("D"); command.Parameters.Add(tenant);
        var resource = command.CreateParameter(); resource.ParameterName = "@id"; resource.Value = id.ToString("D"); command.Parameters.Add(resource);
        if (await command.ExecuteScalarAsync(ct) is not string)
            throw new InvalidOperationException("Immutable resource provenance lost its registered identity.");
    }
}
