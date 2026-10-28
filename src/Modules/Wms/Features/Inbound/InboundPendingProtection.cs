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
        var applied = await (from inbox in database.InboundTransportInbox
                             join intent in database.InboundTransportIntents on inbox.IntentId equals intent.Id
                             join header in database.InboundDocuments on intent.OrderId equals header.Id
                             join posting in database.Postings on inbox.PostingId equals posting.Id
                             join target in database.Balances on posting.TargetBalanceId equals target.Id
                             join arrival in database.InboundFacts on posting.Id equals arrival.PostingId
                             where header.WarehouseId == warehouse && inbox.Applied && arrival.Kind == InboundFactKind.Arrived
                                 && posting.Kind == StockPostingKind.Transfer && posting.ExecutionUnitId == inbox.ExecutionId
                                 && intent.ExecutionId == inbox.ExecutionId && posting.SourceDocumentId == intent.OrderId
                                 && posting.SourceBalanceId == intent.SourceBalanceId && arrival.OrderId == intent.OrderId
                             select new { Inbox = inbox, Intent = intent, Posting = posting, Arrival = arrival, Target = target })
            .AsNoTracking().ToListAsync(ct);
        return facts.Select(fact => fact.Receipt()).Where(receipt => receipt.ReceiptLocationId != receipt.TargetLocationId
            && !applied.Any(row =>
            {
                var transition = row.Arrival.Transport();
                var request = row.Intent.Request();
                return receipt.ExecutionId == row.Inbox.ExecutionId && transition.CompletionEventId == row.Inbox.EventId
                    && transition.IntentId == row.Intent.Id && transition.ExecutionId == receipt.ExecutionId
                    && transition.TransportId == row.Intent.TransportId && transition.ContainerId == receipt.ContainerId
                    && transition.SourceLocationId == receipt.ReceiptLocationId && transition.TargetLocationId == receipt.TargetLocationId
                    && request.ContainerId == receipt.ContainerId && request.ItemId == receipt.ItemId && request.UnitId == receipt.UnitId
                    && request.Batch == receipt.Batch && row.Posting.Quantity == receipt.Quantity
                    && row.Posting.SourceLineId == receipt.LineId && row.Arrival.OperationId == row.Inbox.EventId
                    && row.Posting.OperationId == row.Inbox.EventId && row.Posting.ActorId == row.Arrival.ActorId
                    && row.Posting.CorrelationId == request.CorrelationId
                    && row.Target.WarehouseId == request.WarehouseId && row.Target.LocationId == receipt.TargetLocationId
                    && row.Target.ContainerId == receipt.ContainerId && row.Target.ItemId == receipt.ItemId
                    && row.Target.Batch == receipt.Batch && row.Target.UnitId == receipt.UnitId && row.Target.Precision == request.Precision;
            })).ToArray();
    }
}
