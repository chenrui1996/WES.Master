using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Wes.Wms.Domain;
using Wes.Wms.Features.Transfers;

namespace Wes.Wms.Infrastructure.Persistence;

internal static class WmsResourceLocks
{
    internal static async Task AcquireAsync(WmsDbContext database, Guid warehouse, IEnumerable<Guid> locations,
        Guid? container, CancellationToken ct) =>
        await AcquireAsync(database, warehouse, locations, container.HasValue ? [container.Value] : [], ct);

    internal static async Task AcquireAsync(WmsDbContext database, Guid warehouse, IEnumerable<Guid> locations,
        IEnumerable<Guid> containers, CancellationToken ct, Guid? transferOwner = null)
    {
        var locationIds = locations.Distinct().Order().ToArray();
        var containerIds = containers.Distinct().Order().ToArray();
        await AcquireFencesAsync(database, warehouse, locationIds, containerIds, ct);
        if (await CountConflictAsync(database, warehouse, locationIds, containerIds, ct)
            || await TransferProtection.ConflictsAsync(database, warehouse, locationIds, containerIds, ct, transferOwner))
            throw new CountLockConflictException();
    }

    internal static async Task AcquireFencesAsync(WmsDbContext database, Guid warehouse, IEnumerable<Guid> locations,
        IEnumerable<Guid> containers, CancellationToken ct)
    {
        await FenceAsync(database, "wms_warehouses", warehouse, ct);
        foreach (var location in locations.Distinct().Order()) await FenceAsync(database, "wms_locations", location, ct);
        foreach (var container in containers.Distinct().Order()) await FenceAsync(database, "wms_stock_containers", container, ct);
    }

    // Lifecycle ownership is not a public posting bypass. Count adjustment composition validates the exact generation separately.
    internal static async Task AcquireCountAsync(WmsDbContext database, StockCount plan, CancellationToken ct)
    {
        if (plan.TenantId != database.TenantId) throw new InvalidOperationException("Count tenant mismatch.");
        await FenceAsync(database, "wms_warehouses", plan.WarehouseId, ct);
        await FenceAsync(database, "wms_locations", plan.LocationId, ct);
        var containers = await database.Balances.AsNoTracking().Where(row => row.WarehouseId == plan.WarehouseId
            && row.LocationId == plan.LocationId).Select(row => row.ContainerId).Distinct().ToArrayAsync(ct);
        foreach (var container in containers.Order())
        {
            if (await database.StockContainers.AnyAsync(row => row.Id == container, ct))
                await FenceAsync(database, "wms_stock_containers", container, ct);
        }
        if (await TransferProtection.ConflictsAsync(database, plan.WarehouseId, [plan.LocationId], containers, ct))
            throw new CountLockConflictException();
    }

    internal static async Task<bool> CountConflictAsync(WmsDbContext database, Guid warehouse, IEnumerable<Guid> locations,
        IEnumerable<Guid> containers, CancellationToken ct)
    {
        var locationIds = locations.ToHashSet(); var containerIds = containers.ToHashSet();
        var plans = await database.Counts.AsNoTracking().Where(row =>
            row.State == StockCountState.Counting || row.State == StockCountState.Ready).ToArrayAsync(ct);
        return plans.Any(row => row.WarehouseId == warehouse && locationIds.Contains(row.LocationId)
            || row.Lines.Any(line => containerIds.Contains(line.ContainerId)));
    }

    internal static async Task AcquireBalanceResourcesAsync(WmsDbContext database, IEnumerable<StockBalance> balances, CancellationToken ct,
        Guid? transferOwner = null)
    {
        var dimensions = balances.ToArray();
        var locationIds = dimensions.Select(row => row.LocationId).ToArray();
        var containerIds = dimensions.Select(row => row.ContainerId).ToArray();
        var locations = await database.Locations.AsNoTracking()
            .Where(WmsIdentityPredicate.MatchesAnyId<WarehouseLocation>(locationIds, row => row.Id)).ToArrayAsync(ct);
        var containers = await database.StockContainers.AsNoTracking()
            .Where(WmsIdentityPredicate.MatchesAnyId<StockContainer>(containerIds, row => row.Id)).ToArrayAsync(ct);
        var warehouses = dimensions.Select(row => row.WarehouseId).Concat(locations.Select(row => row.WarehouseId))
            .Concat(containers.Select(row => row.WarehouseId)).Distinct().Order();
        foreach (var warehouse in warehouses)
        {
            if (!await database.Warehouses.AnyAsync(row => row.Id == warehouse, ct)) continue;
            await AcquireAsync(database, warehouse, locations.Where(row => row.WarehouseId == warehouse).Select(row => row.Id),
                containers.Where(row => row.WarehouseId == warehouse).Select(row => row.Id), ct, transferOwner);
        }
    }

    private static async Task FenceAsync(WmsDbContext database, string table, Guid id, CancellationToken ct)
    {
        await using var command = database.Database.GetDbConnection().CreateCommand();
        command.Transaction = database.Database.CurrentTransaction?.GetDbTransaction()
            ?? throw new InvalidOperationException("Operational resource fences require their owner transaction.");
        command.CommandText = $"SELECT Id FROM {table} WHERE TenantId = @tenant AND Id = @id FOR UPDATE";
        var tenant = command.CreateParameter(); tenant.ParameterName = "@tenant"; tenant.Value = database.TenantId.ToString("D"); command.Parameters.Add(tenant);
        var resource = command.CreateParameter(); resource.ParameterName = "@id"; resource.Value = id.ToString("D"); command.Parameters.Add(resource);
        if (await command.ExecuteScalarAsync(ct) is not string)
            throw new InvalidOperationException("Immutable resource provenance lost its registered identity.");
    }

}

internal sealed class CountLockConflictException : Exception;
