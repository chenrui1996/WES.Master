using Microsoft.EntityFrameworkCore;
using Wes.Wms.Domain;

namespace Wes.Wms.Features.Ledger;

internal sealed partial class PostStock
{
    internal async Task<bool> StageCountAdjustmentAsync(StockCount plan, Guid actor, Guid correlation, CancellationToken ct)
    {
        if (database.Database.CurrentTransaction is null || plan.TenantId != database.TenantId || plan.Generation is null
            || plan.State != StockCountState.Ready || database.Entry(plan).State != EntityState.Unchanged
            || plan.Lines.Length is < 1 or > 100 || plan.Lines.Any(row => row.Quantity is null))
            throw new InvalidOperationException("Only a fenced current ready generation can compose count adjustments.");
        var current = await database.Counts.AsNoTracking().SingleAsync(row => row.Id == plan.Id, ct);
        if (current.Generation != plan.Generation || current.Version != plan.Version || current.LinesJson != plan.LinesJson
            || current.State != StockCountState.Ready || current.LockOwnerId is null) return false;
        var actual = await database.Balances.Where(row => row.WarehouseId == plan.WarehouseId && row.LocationId == plan.LocationId)
            .OrderBy(row => row.Id).ToArrayAsync(ct);
        if (actual.Length != plan.Lines.Length) return false;
        foreach (var line in plan.Lines)
        {
            var balance = actual.SingleOrDefault(row => row.Id == line.BalanceId);
            if (balance is null || balance.Quantity != line.BookQuantity || balance.Version != line.BookVersion
                || balance.ItemId != line.ItemId || balance.UnitId != line.UnitId || balance.ContainerId != line.ContainerId
                || balance.Precision != line.Precision || balance.Batch != line.Batch) return false;
            if (await database.Reservations.AnyAsync(row => row.BalanceId == balance.Id && row.RemainingQuantity > 0m, ct)) return false;
        }
        foreach (var line in plan.Lines.Where(row => row.Quantity != row.BookQuantity))
        {
            var balance = actual.Single(row => row.Id == line.BalanceId);
            var difference = line.Quantity!.Value - line.BookQuantity;
            var quantity = StockMovementQuantity.Create(Math.Abs(difference), QuantityPrecision.Create(line.Precision));
            StagePosting(new(Guid.NewGuid(), actor, correlation,
                    difference > 0 ? StockPostingKind.AdjustmentIn : StockPostingKind.AdjustmentOut, plan.Id, line.Id,
                    line.Id, difference < 0 ? balance.Id : null, difference > 0 ? balance.Id : null, quantity.Value,
                    difference < 0 ? line.BookVersion : null, difference > 0 ? line.BookVersion : null),
                difference < 0 ? balance : null, difference > 0 ? balance : null, quantity);
        }
        return true;
    }
}
