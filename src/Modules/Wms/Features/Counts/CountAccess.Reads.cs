using System.Data;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Wes.Wms.Domain;
using Wes.Wms.Features.MasterData;
using Wes.Wms.Infrastructure.Persistence;

namespace Wes.Wms.Features.Counts;

internal sealed partial class CountAccess
{
    internal async Task<CountReadResult> ReadAsync(MasterDataSubject subject, Guid id, CancellationToken ct = default)
    {
        var read = await ReadCoreAsync(subject, id, false, ct);
        if (read.Plan is null) return new(read.Decision);
        var plan = read.Plan;
        return new(CountDecision.Committed, new(plan.Id, plan.Code, plan.WarehouseId, plan.LocationId, (int)plan.State,
            plan.Version, plan.CreatedAt, plan.SnapshotAt, plan.LockOwnerId, Reminder(plan), Policy,
            plan.Lines.Select(row => new CountLineView(row.Id, row.ItemId, row.UnitId, row.Batch, row.Precision,
                row.ContainerId, row.Quantity.HasValue ? Exact(row.Quantity.Value) : null, row.CountedBy, row.CountedAt)).ToArray()));
    }

    internal async Task<CountReviewResult> ReviewAsync(MasterDataSubject subject, Guid id, CancellationToken ct = default)
    {
        var read = await ReadCoreAsync(subject, id, true, ct);
        if (read.Plan is null) return new(read.Decision);
        var plan = read.Plan;
        return new(CountDecision.Committed, new(plan.Id, plan.Code, plan.WarehouseId, plan.LocationId, (int)plan.State,
            plan.Version, plan.CreatedAt, plan.SnapshotAt, plan.LockOwnerId, Reminder(plan), Policy,
            plan.Lines.Select(row => new CountReviewLineView(row.Id, row.ItemId, row.UnitId, row.Batch, row.Precision,
                row.ContainerId, row.Quantity.HasValue ? Exact(row.Quantity.Value) : null, row.CountedBy, row.CountedAt,
                Exact(row.BookQuantity), row.Quantity.HasValue ? Exact(row.Quantity.Value - row.BookQuantity) : null)).ToArray()));
    }

    private bool Reminder(StockCount plan) => plan.Active && plan.SnapshotAt.HasValue
        && clock.GetUtcNow() >= plan.SnapshotAt.Value.AddSeconds(1800);

    private async Task<(CountDecision Decision, StockCount? Plan)> ReadCoreAsync(MasterDataSubject subject, Guid id, bool review, CancellationToken ct)
    {
        Clean(ct);
        if (!ValidSubject(subject)) return (CountDecision.Forbidden, null);
        if (id == Guid.Empty) return (CountDecision.Invalid, null);
        try
        {
            await using var snapshot = await database.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
            var plan = await database.Counts.AsNoTracking().SingleOrDefaultAsync(row => row.Id == id, ct);
            if (plan is null) return (CountDecision.NotFound, null);
            await ValidateHistoryAsync(plan, ct);
            var permission = review ? "review" : "read";
            string[] fields = review ? ["BookQuantity", "Difference"] : ReadFields;
            if (!await AllowedAsync(subject, plan, permission, fields, ct)) return (CountDecision.Forbidden, null);
            await snapshot.CommitAsync(ct);
            await snapshot.DisposeAsync();
            if (!await AllowedAsync(subject, plan, permission, fields, ct)) return (CountDecision.Forbidden, null);
            return (CountDecision.Committed, plan);
        }
        catch (DbException error) { ct.ThrowIfCancellationRequested(); throw WmsDatabaseFailures.Safe(error); }
        finally { database.ChangeTracker.Clear(); }
    }

    private async Task<bool> AllowedAsync(MasterDataSubject subject, StockCount plan, string permission, string[] fields, CancellationToken ct)
    {
        if (!await FieldsAsync(subject, "wms.counts.read", plan.CreatorId, ReadFields, ct)
            || !await FieldsAsync(subject, $"wms.counts.{permission}", plan.CreatorId, fields, ct)) return false;
        var warehouseCreator = await database.MasterDataChanges.AsNoTracking()
            .Where(row => row.WarehouseId == plan.WarehouseId && row.LocationId == null).Select(row => (Guid?)row.ActorId).SingleOrDefaultAsync(ct);
        var locationCreator = await database.MasterDataChanges.AsNoTracking()
            .Where(row => row.LocationId == plan.LocationId && row.WarehouseId == plan.WarehouseId)
            .Select(row => (Guid?)row.ActorId).SingleOrDefaultAsync(ct);
        if (warehouseCreator is null || locationCreator is null) return false;
        string[] warehouseFields = ["Id", "Code", "Name", "Enabled", "Version", "CreatedAt"];
        if (!await FieldsAsync(subject, "wms.warehouses.read", warehouseCreator.Value, warehouseFields, ct)
            || !await FieldsAsync(subject, "wms.locations.read", locationCreator.Value,
                [.. warehouseFields, "WarehouseId", "PackId", "SchemaVersion"], ct)) return false;
        var balances = await database.Balances.AsNoTracking().Where(row => row.WarehouseId == plan.WarehouseId
            && row.LocationId == plan.LocationId).ToArrayAsync(ct);
        foreach (var balance in balances)
        {
            var item = await database.StockItems.AsNoTracking().SingleOrDefaultAsync(row => row.Id == balance.ItemId, ct);
            var unit = await database.StockUnits.AsNoTracking().SingleOrDefaultAsync(row => row.Id == balance.UnitId, ct);
            var container = await database.StockContainers.AsNoTracking().SingleOrDefaultAsync(row => row.Id == balance.ContainerId, ct);
            if (item is null || unit is null || container is null) return false;
            if (!await FieldsAsync(subject, "wms.items.read", item.ActorId,
                    ["Id", "Code", "Name", "BaseUnitId", "BatchRequired", "Version", "CreatedAt"], ct)
                || !await FieldsAsync(subject, "wms.units.read", unit.ActorId,
                    ["Id", "Code", "Name", "Countable", "Precision", "Version", "CreatedAt"], ct)
                || !await FieldsAsync(subject, "wms.containers.read", container.ActorId,
                    ["Id", "Code", "Name", "WarehouseId", "Version", "CreatedAt"], ct)) return false;
        }
        return true;
    }

    private async Task<bool> FieldsAsync(MasterDataSubject subject, string permission, Guid creator, string[] fields, CancellationToken ct)
    {
        var allowed = await authorization.ReadFieldsAsync(subject, permission, creator, ct)
            ?? throw new InvalidOperationException("Count authorization returned no decision.");
        ct.ThrowIfCancellationRequested();
        return fields.All(allowed.ToHashSet(StringComparer.Ordinal).Contains);
    }

    private async Task<CountDecision?> OperationalAsync(StockCount plan, CancellationToken ct)
    {
        var warehouse = await database.Warehouses.AsNoTracking().SingleOrDefaultAsync(row => row.Id == plan.WarehouseId, ct);
        var location = await database.Locations.AsNoTracking().SingleOrDefaultAsync(row => row.Id == plan.LocationId, ct);
        if (warehouse is null || location is null || location.WarehouseId != plan.WarehouseId) return CountDecision.NotFound;
        if (!warehouse.Enabled || !location.Enabled) return CountDecision.Blocked;
        var evidence = await schema.ValidateAsync(database.TenantId, warehouse.Id, location.PackId, location.SchemaVersion, location.Code, ct);
        ct.ThrowIfCancellationRequested();
        return evidence switch
        {
            LocationSchemaDecision.Valid => null,
            LocationSchemaDecision.Rejected => CountDecision.SchemaRejected,
            LocationSchemaDecision.Unavailable => CountDecision.SchemaUnavailable,
            _ => throw new InvalidOperationException("Unknown count schema decision.")
        };
    }

    private async Task<bool> LegalAsync(StockBalance balance, CancellationToken ct)
    {
        var item = await database.StockItems.AsNoTracking().SingleOrDefaultAsync(row => row.Id == balance.ItemId, ct);
        var unit = await database.StockUnits.AsNoTracking().SingleOrDefaultAsync(row => row.Id == balance.UnitId, ct);
        var container = await database.StockContainers.AsNoTracking().SingleOrDefaultAsync(row => row.Id == balance.ContainerId, ct);
        return item is not null && unit is not null && container is not null && container.WarehouseId == balance.WarehouseId
            && item.BaseUnitId == unit.Id && unit.Precision == balance.Precision
            && item.BatchRequired == (balance.Batch.Length > 0)
            && await database.DimensionRegistrations.AnyAsync(row => row.BalanceId == balance.Id, ct);
    }
}
