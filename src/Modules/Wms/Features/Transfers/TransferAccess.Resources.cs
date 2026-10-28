using System.Data;
using System.Data.Common;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Wes.Wms.Domain;
using Wes.Wms.Features.Inbound;
using Wes.Wms.Features.MasterData;
using Wes.Wms.Infrastructure.Persistence;

namespace Wes.Wms.Features.Transfers;

internal sealed partial class TransferAccess
{
    private Task<Dimension[]> DimensionsAsync(IReadOnlyList<TransferPlanLine> lines, CancellationToken ct) =>
        DimensionsAsync(lines.SelectMany(row => new[] { row.SourceBalanceId, row.TargetBalanceId }), ct);

    private async Task<Dimension[]> DimensionsAsync(IEnumerable<Guid> ids, CancellationToken ct)
    {
        var balances = await database.Balances.AsNoTracking().Where(WmsIdentityPredicate.MatchesAnyId<StockBalance>(ids.ToArray(), row => row.Id))
            .ToArrayAsync(ct);
        var dimensions = new List<Dimension>();
        foreach (var balance in balances)
        {
            var registration = await database.DimensionRegistrations.AsNoTracking().SingleOrDefaultAsync(row => row.BalanceId == balance.Id, ct);
            var warehouse = await database.Warehouses.AsNoTracking().SingleOrDefaultAsync(row => row.Id == balance.WarehouseId, ct);
            var location = await database.Locations.AsNoTracking().SingleOrDefaultAsync(row => row.Id == balance.LocationId, ct);
            var item = await database.StockItems.AsNoTracking().SingleOrDefaultAsync(row => row.Id == balance.ItemId, ct);
            var unit = await database.StockUnits.AsNoTracking().SingleOrDefaultAsync(row => row.Id == balance.UnitId, ct);
            var container = await database.StockContainers.AsNoTracking().SingleOrDefaultAsync(row => row.Id == balance.ContainerId, ct);
            if (registration is null || warehouse is null || location is null || item is null || unit is null || container is null) continue;
            var warehouseCreator = await database.MasterDataChanges.AsNoTracking().Where(row => row.WarehouseId == warehouse.Id
                && row.LocationId == null).Select(row => (Guid?)row.ActorId).SingleOrDefaultAsync(ct);
            var locationCreator = await database.MasterDataChanges.AsNoTracking().Where(row => row.LocationId == location.Id)
                .Select(row => (Guid?)row.ActorId).SingleOrDefaultAsync(ct);
            if (warehouseCreator is null || locationCreator is null) continue;
            dimensions.Add(new(balance, registration, warehouse, location, item, unit, container, warehouseCreator.Value, locationCreator.Value));
        }
        return dimensions.ToArray();
    }

    private async Task<bool> AllowedAsync(MasterDataSubject subject, Guid creator, string action, Dimension[] dimensions, CancellationToken ct)
    {
        string[] fields = action switch
        {
            "create" => ["Code", "WarehouseId", "Lines"],
            "approve" or "dispatch" or "sync" => ["State"],
            "arrive" => ["LineId", "SourceLocationId", "TargetLocationId", "ContainerId", "Quantity", "Reason"],
            "unknown" or "close" => ["State", "Reason"],
            "read" => ReadFields,
            _ => throw new InvalidOperationException("Unknown transfer permission.")
        };
        if (!await FieldsAsync(subject, "wms.transfers.read", creator, ReadFields, ct)
            || !await FieldsAsync(subject, $"wms.transfers.{action}", creator, fields, ct)) return false;
        foreach (var row in dimensions)
        {
            if (!await FieldsAsync(subject, "wms.warehouses.read", row.WarehouseCreator,
                    ["Id", "Code", "Name", "Enabled", "Version", "CreatedAt"], ct)
                || !await FieldsAsync(subject, "wms.locations.read", row.LocationCreator,
                    ["Id", "Code", "Name", "Enabled", "Version", "CreatedAt", "WarehouseId", "PackId", "SchemaVersion"], ct)
                || !await FieldsAsync(subject, "wms.items.read", row.Item.ActorId,
                    ["Id", "Code", "Name", "BaseUnitId", "BatchRequired", "Version", "CreatedAt"], ct)
                || !await FieldsAsync(subject, "wms.units.read", row.Unit.ActorId,
                    ["Id", "Code", "Name", "Countable", "Precision", "Version", "CreatedAt"], ct)
                || !await FieldsAsync(subject, "wms.containers.read", row.Container.ActorId,
                    ["Id", "Code", "Name", "WarehouseId", "Version", "CreatedAt"], ct)) return false;
        }
        return true;
    }

    private async Task<bool> FieldsAsync(MasterDataSubject subject, string permission, Guid creator, string[] fields, CancellationToken ct)
    {
        var granted = await authorization.ReadFieldsAsync(subject, permission, creator, ct);
        ct.ThrowIfCancellationRequested();
        return granted is not null && fields.All(granted.Contains);
    }

    private async Task<TransferDecision?> OperationalAsync(Dimension[] dimensions, CancellationToken ct)
    {
        if (dimensions.Any(row => !row.IsLegal)) return TransferDecision.Invalid;
        if (dimensions.Any(row => !row.Warehouse.Enabled || !row.Location.Enabled)) return TransferDecision.Blocked;
        foreach (var row in dimensions)
        {
            var decision = await schema.ValidateAsync(database.TenantId, row.Warehouse.Id, row.Location.PackId,
                row.Location.SchemaVersion, row.Location.Code, ct);
            ct.ThrowIfCancellationRequested();
            if (decision == LocationSchemaDecision.Rejected) return TransferDecision.SchemaRejected;
            if (decision == LocationSchemaDecision.Unavailable) return TransferDecision.SchemaUnavailable;
            if (decision != LocationSchemaDecision.Valid) throw new InvalidOperationException("Unknown schema outcome.");
        }
        return null;
    }

    private async Task FencesAsync(Guid warehouse, Dimension[] dimensions, Guid? ownOrder, CancellationToken ct)
    {
        var locations = dimensions.Select(row => row.Location.Id).ToArray();
        var containers = dimensions.Select(row => row.Container.Id).ToArray();
        await WmsResourceLocks.AcquireFencesAsync(database, warehouse, locations, containers, ct);
        if (await WmsResourceLocks.CountConflictAsync(database, warehouse, locations, containers, ct)
            || await TransferProtection.ConflictsAsync(database, warehouse, locations, containers, ct, ownOrder))
            throw new CountLockConflictException();
    }

    private async Task<bool> HasForeignProtectionAsync(TransferDocument document, TransferPlanLine line, CancellationToken ct)
    {
        var pending = await InboundPendingProtection.ReadAsync(database, document.WarehouseId, ct);
        if (pending.Any(row => row.ContainerId == line.ContainerId || row.ReceiptLocationId == line.SourceLocationId
            || row.ReceiptLocationId == line.TargetLocationId || row.TargetLocationId == line.SourceLocationId
            || row.TargetLocationId == line.TargetLocationId)) return true;
        var protections = await (from reservation in database.Reservations
                                 join balance in database.Balances on reservation.BalanceId equals balance.Id
                                 where reservation.RemainingQuantity > 0m && balance.WarehouseId == document.WarehouseId
                                 select new { reservation.Kind, balance.LocationId, balance.ContainerId }).AsNoTracking().ToArrayAsync(ct);
        return protections.Any(row => row.ContainerId == line.ContainerId || row.LocationId == line.TargetLocationId
            || (row.Kind & (StockProtectionKind.InTransit | StockProtectionKind.Unknown)) != 0
                && (row.LocationId == line.SourceLocationId || row.LocationId == line.TargetLocationId));
    }

    private async Task<bool> WholeUnitAsync(Guid warehouse, TransferPlanLine line, CancellationToken ct)
    {
        var positive = await database.Balances.AsNoTracking().Where(row => row.ContainerId == line.ContainerId
            && row.Quantity > 0m).ToArrayAsync(ct);
        return positive.Length == 1 && positive[0].Id == line.SourceBalanceId && positive[0].Quantity == line.PlannedQuantity
            && !await database.Balances.AnyAsync(row => row.WarehouseId == warehouse && row.LocationId == line.TargetLocationId
                && row.Quantity > 0m, ct);
    }

    internal async Task<TransferReadResult> ReadAsync(MasterDataSubject subject, Guid id, CancellationToken ct)
    {
        Clean(ct);
        if (!ValidSubject(subject) || id == Guid.Empty) return new(TransferReadDecision.Invalid);
        try
        {
            var creator = await database.TransferDocuments.AsNoTracking().Where(row => row.Id == id)
                .Select(row => (Guid?)row.CreatorId).SingleOrDefaultAsync(ct);
            if (creator is null) return new(TransferReadDecision.NotFound);
            if (!await FieldsAsync(subject, "wms.transfers.read", creator.Value, ReadFields, ct)) return new(TransferReadDecision.Forbidden);
            TransferRecord record;
            await using (var snapshot = await database.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct))
            {
                var loaded = await LoadAsync(id, ct);
                if (loaded is null) return new(TransferReadDecision.NotFound);
                var (document, facts) = loaded;
                var dimensions = await DimensionsAsync(document.Lines, ct);
                if (dimensions.Length != document.Lines.Count * 2) throw new InvalidOperationException("Transfer resources are absent.");
                if (!await AllowedAsync(subject, document.CreatorId, "read", dimensions, ct)) return new(TransferReadDecision.Forbidden);
                var executionIds = document.Lines.Select(row => row.ExecutionId).ToArray();
                var reservations = await database.Reservations.AsNoTracking()
                    .Where(WmsIdentityPredicate.MatchesAnyId<StockReservation>(executionIds, row => row.ExecutionUnitId))
                    .ToDictionaryAsync(row => row.ExecutionUnitId, ct);
                record = new(document.Id, document.Code, document.WarehouseId, (int)document.State, document.Version, document.CreatedAt,
                    document.Lines.Select(line =>
                    {
                        var progress = TransferProgress.For(facts, line.Id);
                        return new TransferLineRecord(line.Id, line.ExecutionId, line.SourceBalanceId, line.TargetBalanceId,
                            line.ItemId, line.UnitId, line.Batch, line.Precision, line.ContainerId, line.SourceLocationId, line.TargetLocationId,
                            line.Branch, Amount(line.PlannedQuantity), Amount(reservations.GetValueOrDefault(line.ExecutionId)?.RemainingQuantity ?? 0m),
                            Amount(progress.Posting == TransferPostingStatus.Synced ? line.PlannedQuantity : 0m),
                            Amount(progress.Execution == TransferExecutionStatus.Closed ? line.PlannedQuantity : 0m),
                            (int)progress.Execution, (int)progress.Posting,
                            dimensions.Single(row => row.Balance.Id == line.SourceBalanceId).Balance.Version,
                            dimensions.Single(row => row.Balance.Id == line.TargetBalanceId).Balance.Version);
                    }).ToArray());
                await snapshot.CommitAsync(ct);
            }
            var fresh = await LoadAsync(id, ct);
            return fresh is not null && await AllowedAsync(subject, creator.Value, "read",
                await DimensionsAsync(fresh.Document.Lines, ct), ct)
                ? new(TransferReadDecision.Success, record) : new(TransferReadDecision.Forbidden);
        }
        catch (DbException error) { ct.ThrowIfCancellationRequested(); throw WmsDatabaseFailures.Safe(error); }
        finally { database.ChangeTracker.Clear(); }
    }

    private static string Amount(decimal value) => value.ToString("F6", CultureInfo.InvariantCulture);

    private sealed record Dimension(StockBalance Balance, StockDimensionRegistration Registration, Warehouse Warehouse,
        WarehouseLocation Location, StockItem Item, StockUnit Unit, StockContainer Container, Guid WarehouseCreator, Guid LocationCreator)
    {
        internal bool IsLegal => Registration.BalanceId == Balance.Id && Registration.WarehouseId == Balance.WarehouseId
            && Registration.LocationId == Balance.LocationId && Registration.ItemId == Balance.ItemId
            && Registration.UnitId == Balance.UnitId && Registration.ContainerId == Balance.ContainerId
            && Registration.Batch == Balance.Batch && Registration.Precision == Balance.Precision
            && Warehouse.Id == Balance.WarehouseId && Location.WarehouseId == Balance.WarehouseId
            && Container.WarehouseId == Balance.WarehouseId && Item.BaseUnitId == Balance.UnitId
            && Unit.Precision == Balance.Precision && Item.BatchRequired == (Balance.Batch.Length > 0);
    }
}
