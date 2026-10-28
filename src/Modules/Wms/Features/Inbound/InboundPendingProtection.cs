using Microsoft.EntityFrameworkCore;
using Wes.Wms.Domain;
using Wes.Wms.Infrastructure.Persistence;

namespace Wes.Wms.Features.Inbound;

internal static class InboundPendingProtection
{
    internal static async Task<InboundReceiptCommand[]> ReadAsync(WmsDbContext database, Guid warehouse, CancellationToken ct)
    {
        var facts = await (from fact in database.InboundFacts
                           join header in database.InboundDocuments on fact.OrderId equals header.Id
                           where header.WarehouseId == warehouse && fact.Kind == InboundFactKind.Received
                           select fact).AsNoTracking().ToListAsync(ct);
        // Only a future durable final-arrival/ledger fact may discharge this protection.
        return facts.Select(fact => fact.Receipt()).Where(receipt => receipt.ReceiptLocationId != receipt.TargetLocationId).ToArray();
    }
}
