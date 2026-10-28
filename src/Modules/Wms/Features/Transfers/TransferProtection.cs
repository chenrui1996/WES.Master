using Microsoft.EntityFrameworkCore;
using Wes.Wms.Domain;
using Wes.Wms.Features.Ledger;
using Wes.Wms.Infrastructure.Persistence;

namespace Wes.Wms.Features.Transfers;

internal static class TransferProtection
{
    internal static async Task<bool> ConflictsAsync(WmsDbContext database, Guid warehouse, IEnumerable<Guid> locations,
        IEnumerable<Guid> containers, CancellationToken ct, Guid? ownOrder = null)
    {
        var locationIds = locations.ToHashSet(); var containerIds = containers.ToHashSet();
        var documents = await database.TransferDocuments.AsNoTracking().Where(row => row.WarehouseId == warehouse
            && (row.State == TransferState.Approved || row.State == TransferState.Executing)).ToArrayAsync(ct);
        foreach (var document in documents)
        {
            if (document.Id == ownOrder) continue;
            var facts = await database.TransferFacts.AsNoTracking().Where(row => row.OrderId == document.Id).ToArrayAsync(ct);
            TransferProgress.Validate(document, facts);
            if (document.Lines.Any(line =>
                {
                    var progress = TransferProgress.For(facts, line.Id);
                    return progress.Posting != TransferPostingStatus.Synced && progress.Execution != TransferExecutionStatus.Closed
                        && (locationIds.Contains(line.SourceLocationId) || locationIds.Contains(line.TargetLocationId)
                            || containerIds.Contains(line.ContainerId));
                })) return true;
        }
        return false;
    }

    internal static async Task<Guid?> PostingOwnerAsync(WmsDbContext database, StockPostingCommand command, CancellationToken ct)
    {
        var document = await database.TransferDocuments.AsNoTracking().SingleOrDefaultAsync(row => row.Id == command.SourceDocumentId, ct);
        if (document is null) return null;
        var line = document.Lines.SingleOrDefault(row => row.ExecutionId == command.ExecutionUnitId);
        if (line is null || command.Kind != StockPostingKind.Transfer || command.SourceLineId != line.Id
            || command.SourceBalanceId != line.SourceBalanceId || command.TargetBalanceId != line.TargetBalanceId
            || command.Quantity != line.PlannedQuantity) return null;
        var facts = await database.TransferFacts.AsNoTracking().Where(row => row.OrderId == document.Id).ToArrayAsync(ct);
        var progress = TransferProgress.For(facts, line.Id);
        return progress.Execution == TransferExecutionStatus.Arrived && progress.Posting == TransferPostingStatus.Pending
            ? document.Id : null;
    }
}
