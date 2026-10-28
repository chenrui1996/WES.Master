using Microsoft.EntityFrameworkCore;
using Wes.Wms.Domain;
using Wes.Wms.Infrastructure.Persistence;

namespace Wes.Wms.Features.MasterData;

internal sealed partial class StockResourceAccess
{
    internal Task<StockResourceResult> DimensionAsync(MasterDataSubject subject, RegisterStockDimension request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return RunAsync(subject, request.OperationId, request.CorrelationId, async () =>
        {
            if (new[] { request.WarehouseId, request.LocationId, request.ItemId, request.ContainerId }.Contains(Guid.Empty)
                || request.Batch is null || request.Batch.Length > 128 || request.Batch != request.Batch.Trim() || request.Batch.Any(char.IsControl))
                return Reject(StockResourceDecision.Invalid);
            var item = await database.StockItems.AsNoTracking().SingleOrDefaultAsync(row => row.Id == request.ItemId, ct);
            var container = await database.StockContainers.AsNoTracking().SingleOrDefaultAsync(row => row.Id == request.ContainerId, ct);
            var location = await database.Locations.AsNoTracking().SingleOrDefaultAsync(row => row.Id == request.LocationId, ct);
            var warehouseCreator = await ParentCreatorAsync(request.WarehouseId, ct);
            var locationCreator = await database.MasterDataChanges.AsNoTracking().Where(row => row.LocationId == request.LocationId)
                .Select(row => (Guid?)row.ActorId).SingleOrDefaultAsync(ct);
            if (item is null || container is null || location is null || warehouseCreator is null || locationCreator is null)
                return Reject(StockResourceDecision.NotFound);
            var unit = await database.StockUnits.AsNoTracking().SingleAsync(row => row.Id == item.BaseUnitId, ct);
            async Task<bool> Allowed() =>
                await FieldsAsync(subject, "wms.dimensions.write", subject.ActorId, DimensionWrite, ct)
                && await FieldsAsync(subject, "wms.items.read", item.ActorId, ItemRead, ct)
                && await FieldsAsync(subject, "wms.units.read", unit.ActorId, UnitRead, ct)
                && await FieldsAsync(subject, "wms.containers.read", container.ActorId, ContainerRead, ct)
                && await FieldsAsync(subject, "wms.warehouses.read", warehouseCreator.Value, WarehouseRead, ct)
                && await FieldsAsync(subject, "wms.locations.read", locationCreator.Value, LocationRead, ct);
            if (!await Allowed()) return Reject(StockResourceDecision.Forbidden);
            if (container.WarehouseId != request.WarehouseId || location.WarehouseId != request.WarehouseId)
                return Reject(StockResourceDecision.Blocked);
            await WmsResourceLocks.AcquireAsync(database, request.WarehouseId, [request.LocationId], request.ContainerId, ct);
            var old = await database.DimensionRegistrations.AsNoTracking().SingleOrDefaultAsync(row => row.OperationId == request.OperationId, ct);
            if (old is not null)
                return old.ActorId != subject.ActorId || old.WarehouseId != request.WarehouseId || old.LocationId != request.LocationId
                    || old.ItemId != request.ItemId || old.Batch != request.Batch || old.ContainerId != request.ContainerId
                    ? Reject(StockResourceDecision.Conflict) : await ReplayAsync(old.BalanceId, old.AfterVersion, Allowed);
            if (item.BatchRequired != (request.Batch.Length > 0)) return Reject(StockResourceDecision.Invalid);
            if (!await database.Warehouses.AnyAsync(row => row.Id == request.WarehouseId && row.Enabled, ct)
                || !await database.Locations.AnyAsync(row => row.Id == request.LocationId && row.Enabled, ct))
                return Reject(StockResourceDecision.Blocked);
            var evidence = await schema.ValidateAsync(database.TenantId, location.WarehouseId, location.PackId, location.SchemaVersion, location.Code, ct);
            ct.ThrowIfCancellationRequested();
            if (evidence != LocationSchemaDecision.Valid) return Reject(evidence switch
            {
                LocationSchemaDecision.Rejected => StockResourceDecision.SchemaRejected,
                LocationSchemaDecision.Unavailable => StockResourceDecision.SchemaUnavailable,
                _ => throw new InvalidOperationException("Unknown location schema decision.")
            });
            if (await database.Balances.AnyAsync(row => row.WarehouseId == request.WarehouseId && row.LocationId == request.LocationId
                && row.ItemId == request.ItemId && row.Batch == request.Batch && row.ContainerId == request.ContainerId && row.UnitId == unit.Id, ct))
                return Reject(StockResourceDecision.Conflict);
            var balance = new StockBalance(database.TenantId, request.WarehouseId, request.LocationId, request.ItemId,
                request.Batch, request.ContainerId, unit.Id, unit.Precision);
            database.Balances.Add(balance);
            database.DimensionRegistrations.Add(new(request.OperationId, subject.ActorId, request.CorrelationId, balance, clock.GetUtcNow()));
            return await SaveAsync(balance.Id, balance.Version, Allowed, ct);
        }, ct);
    }
}
