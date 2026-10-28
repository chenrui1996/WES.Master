using Microsoft.EntityFrameworkCore;
using Wes.Wms.Domain;
using Wes.Wms.Features.MasterData;

namespace Wes.Wms.Features.Inbound;

internal sealed partial class InboundExecutionAccess
{
    private async Task<InboundExecutionDecision?> OperationalAsync(Guid warehouse, WarehouseLocation[] locations,
        InboundReceiptRegistration? request, CancellationToken ct)
    {
        if (!await database.Warehouses.AnyAsync(row => row.Id == warehouse && row.Enabled, ct)) return InboundExecutionDecision.Blocked;
        foreach (var location in locations.OrderBy(location => location.Id))
        {
            if (!await database.Locations.AnyAsync(row => row.Id == location.Id && row.Enabled, ct)) return InboundExecutionDecision.Blocked;
            var evidence = await schema.ValidateAsync(database.TenantId, warehouse, location.PackId, location.SchemaVersion, location.Code, ct);
            ct.ThrowIfCancellationRequested();
            if (evidence != LocationSchemaDecision.Valid) return evidence switch
            {
                LocationSchemaDecision.Rejected => InboundExecutionDecision.SchemaRejected,
                LocationSchemaDecision.Unavailable => InboundExecutionDecision.SchemaUnavailable,
                _ => throw new InvalidOperationException("Unknown operational schema decision.")
            };
        }
        if (request is null) return null;
        if (await database.Balances.AnyAsync(row => row.WarehouseId == warehouse && row.Quantity > 0m
            && (row.ContainerId == request.ContainerId && row.LocationId != request.ReceiptLocationId
                || row.ContainerId != request.ContainerId && (row.LocationId == request.ReceiptLocationId || row.LocationId == request.TargetLocationId)), ct))
            return InboundExecutionDecision.Blocked;
        if (await (from reservation in database.Reservations
                   join balance in database.Balances on reservation.BalanceId equals balance.Id
                   where balance.WarehouseId == warehouse && reservation.RemainingQuantity > 0m
                       && (balance.ContainerId == request.ContainerId || balance.LocationId == request.ReceiptLocationId || balance.LocationId == request.TargetLocationId)
                   select reservation.ExecutionUnitId).AnyAsync(ct)) return InboundExecutionDecision.Blocked;
        var pending = await InboundPendingProtection.ReadAsync(database, warehouse, ct);
        if (pending.Any(receipt => receipt.ContainerId == request.ContainerId
            || receipt.ReceiptLocationId == request.ReceiptLocationId || receipt.TargetLocationId == request.ReceiptLocationId
            || receipt.ReceiptLocationId == request.TargetLocationId || receipt.TargetLocationId == request.TargetLocationId))
            return InboundExecutionDecision.Blocked;
        return null;
    }
}
