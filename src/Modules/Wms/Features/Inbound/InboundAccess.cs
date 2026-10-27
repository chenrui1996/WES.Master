using System.Collections.Immutable;
using System.Data;
using System.Data.Common;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Wes.Wms.Domain;
using Wes.Wms.Features.Ledger;
using Wes.Wms.Infrastructure.Persistence;

namespace Wes.Wms.Features.Inbound;

internal sealed class InboundAccess(WmsDbContext database, IInboundAuthorization authorization, ILogger<InboundAccess> logger)
{
    internal static readonly string[] Fields = ["Id", "Code", "WarehouseId", "State", "Version", "CreatedAt", "Lines", "Receipts"];

    internal async Task<InboundReadResult> ReadAsync(InboundSubject subject, Guid id, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(subject);
        ct.ThrowIfCancellationRequested();
        if (database.ChangeTracker.Entries().Any() || database.Database.CurrentTransaction is not null
            || System.Transactions.Transaction.Current is not null)
            throw new InvalidOperationException("Inbound reads require a clean independent WMS owner.");
        if (subject.TenantId != database.TenantId || subject.ActorId == Guid.Empty || subject.SessionId == Guid.Empty)
            return Rejected(InboundReadDecision.Forbidden);
        if (id == Guid.Empty) return Rejected(InboundReadDecision.Invalid);
        try
        {
            var creator = await database.InboundDocuments.AsNoTracking().Where(item => item.Id == id)
                .Select(item => (Guid?)item.CreatorId).SingleOrDefaultAsync(ct);
            if (creator is null) return Rejected(InboundReadDecision.NotFound);
            if (!await AuthorizedAsync(subject, creator.Value, ct)) return Rejected(InboundReadDecision.Forbidden);
            InboundView view;
            await using (var transaction = await database.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct))
            {
                var header = await database.InboundDocuments.AsNoTracking().SingleAsync(item => item.Id == id, ct);
                var facts = await database.InboundFacts.AsNoTracking().Where(item => item.OrderId == id)
                    .OrderBy(item => item.Sequence).ToListAsync(ct);
                var domain = header.Restore(facts);
                view = new(header.Id, header.Code, header.WarehouseId, State(domain.State), header.Version, header.CreatedAt,
                    domain.Lines.Select(line =>
                    {
                        var remaining = domain.Remaining(line.Id);
                        return new InboundLineView(line.Id, line.ItemId, line.UnitId, line.Batch, line.Precision,
                            Amount(line.PlannedQuantity), Amount(line.PlannedQuantity - remaining), Amount(remaining));
                    }).ToImmutableArray(),
                    domain.Receipts.Select(unit => new InboundReceiptView(unit.Id, unit.Original.LineId, unit.ContainerId,
                        unit.Original.ReceiptLocationId, unit.Original.TargetLocationId, Amount(unit.Original.Quantity),
                        unit.RequiresTransport, unit.Arrived, unit.TransportId)).ToImmutableArray());
                await transaction.CommitAsync(ct);
            }
            if (!await AuthorizedAsync(subject, creator.Value, ct)) return Rejected(InboundReadDecision.Forbidden);
            ct.ThrowIfCancellationRequested();
            return new(InboundReadDecision.Success, view);
        }
        catch (DbException error)
        {
            ct.ThrowIfCancellationRequested();
            logger.LogError("WMS inbound read failed {Code}", "DatabaseUnavailable");
            throw WmsDatabaseFailures.Safe(error);
        }
        catch (System.Text.Json.JsonException)
        {
            logger.LogError("WMS inbound read rejected malformed history {Code}", "InboundHistoryInvalid");
            throw new InvalidOperationException("The persisted inbound history is invalid.");
        }
        catch (InvalidOperationException)
        {
            logger.LogError("WMS inbound read rejected inconsistent state {Code}", "InboundReadInvalid");
            throw;
        }
    }

    private async Task<bool> AuthorizedAsync(InboundSubject subject, Guid creator, CancellationToken ct)
    {
        var fields = await authorization.ReadFieldsAsync(subject, creator, ct);
        ct.ThrowIfCancellationRequested();
        if (fields is null) throw new InvalidOperationException("Inbound authority returned no decision.");
        var exact = fields.ToHashSet(StringComparer.Ordinal);
        return Fields.All(exact.Contains);
    }

    private InboundReadResult Rejected(InboundReadDecision decision)
    {
        logger.LogWarning("WMS inbound read rejected {Code}", decision.ToString());
        return new(decision);
    }

    private static string Amount(decimal value) => value.ToString("F6", CultureInfo.InvariantCulture);
    private static InboundState State(InboundOrderState state) => state switch
    {
        InboundOrderState.Draft => InboundState.Draft,
        InboundOrderState.Approved => InboundState.Approved,
        InboundOrderState.Executing => InboundState.Executing,
        InboundOrderState.Completed => InboundState.Completed,
        _ => throw new InvalidOperationException("Unsupported inbound progress.")
    };
}
