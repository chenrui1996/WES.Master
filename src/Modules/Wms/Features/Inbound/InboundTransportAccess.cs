using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using MySql.Data.MySqlClient;
using Wes.Wms.Domain;
using Wes.Wms.Features.Ledger;
using Wes.Wms.Infrastructure.Persistence;

namespace Wes.Wms.Features.Inbound;

internal sealed partial class InboundTransportAccess(WmsDbContext database, IInboundTransportAdapter adapter,
    Guid serviceActor, TimeProvider clock, ILogger logger)
{
    internal Task<InboundTransportResult> DispatchAsync(Guid executionId, CancellationToken ct) => OwnAsync(async () =>
    {
        if (executionId == Guid.Empty) return new(InboundTransportDecision.Rejected);
        var intent = await database.InboundTransportIntents.AsNoTracking().SingleOrDefaultAsync(row => row.ExecutionId == executionId, ct);
        if (intent is null) return new(InboundTransportDecision.NotFound);
        if (intent.TransportId is not null) return new(InboundTransportDecision.Replayed, intent.TransportId);
        var request = intent.Request();
        if (serviceActor == request.ActorId)
            throw new InvalidOperationException("Transport facts require a service actor distinct from the original operator.");
        await using (var preflight = await database.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct))
        {
            await ResourcesAsync(request, ct);
            if (!await WholeContainerAsync(intent, ct)) return new(InboundTransportDecision.Blocked);
            await preflight.CommitAsync(ct);
        }
        // The adapter must reconcile the fixed logical intent, including acceptance whose response was lost.
        var acceptance = await adapter.AcceptAsync(request, ct)
            ?? throw new InvalidOperationException("The transport adapter returned no acceptance.");
        ct.ThrowIfCancellationRequested();
        var verified = Verification(acceptance.Verification);
        if (verified is not null) return new(verified.Value);
        if (acceptance.TenantId != database.TenantId || acceptance.IntentId != intent.Id
            || acceptance.ExecutionId != intent.ExecutionId || acceptance.TransportId == Guid.Empty
            || acceptance.SchemaVersion != request.SchemaVersion || acceptance.Producer != "Orchestration")
            return new(InboundTransportDecision.Rejected);
        await using var transaction = await database.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        await ResourcesAsync(request, ct);
        await ParentFenceAsync(intent.OrderId, ct);
        intent = await database.InboundTransportIntents.SingleAsync(row => row.Id == intent.Id, ct);
        if (intent.TransportId is not null)
            return new(intent.TransportId == acceptance.TransportId ? InboundTransportDecision.Replayed
                : InboundTransportDecision.Conflict, intent.TransportId);
        if (!await WholeContainerAsync(intent, ct)) return new(InboundTransportDecision.Blocked);
        var document = await database.InboundDocuments.SingleAsync(row => row.Id == intent.OrderId, ct);
        var domain = document.Restore(await HistoryAsync(document.Id, ct));
        var version = document.Version;
        domain.LinkTransport(executionId, acceptance.TransportId);
        var operation = Guid.NewGuid();
        intent.Associate(acceptance.TransportId, operation);
        document.Advance(domain.State);
        database.InboundFacts.Add(new(document, operation, serviceActor, request.CorrelationId,
            InboundFactKind.TransportLinked, clock.GetUtcNow(), transport: new(version, intent.Id, executionId,
                acceptance.TransportId, request.ContainerId, request.SourceLocationId, request.TargetLocationId, Acceptance: acceptance)));
        await database.SaveChangesAsync(ct);
        ct.ThrowIfCancellationRequested();
        await transaction.CommitAsync(ct);
        return new(InboundTransportDecision.Accepted, acceptance.TransportId);
    }, ct);

    private async Task<bool> WholeContainerAsync(InboundTransportIntent intent, CancellationToken ct)
    {
        var request = intent.Request();
        var quantity = decimal.Parse(request.Quantity, CultureInfo.InvariantCulture);
        var stocks = await database.Balances.AsNoTracking().Where(row => row.ContainerId == request.ContainerId && row.Quantity > 0m).ToArrayAsync(ct);
        if (stocks.Length != 1 || stocks[0].Id != intent.SourceBalanceId || stocks[0].Quantity != quantity
            || stocks[0].WarehouseId != request.WarehouseId || stocks[0].LocationId != request.SourceLocationId
            || stocks[0].ItemId != request.ItemId || stocks[0].UnitId != request.UnitId || stocks[0].Batch != request.Batch
            || stocks[0].Precision != request.Precision) return false;
        // Resource locks cover both endpoints: another container's stock or execution also owns the location.
        if (await database.Balances.AnyAsync(row => row.WarehouseId == request.WarehouseId
            && row.ContainerId != request.ContainerId && row.Quantity > 0m
            && (row.LocationId == request.SourceLocationId || row.LocationId == request.TargetLocationId), ct))
            return false;
        if (await (from reservation in database.Reservations
                   join balance in database.Balances on reservation.BalanceId equals balance.Id
                   where balance.WarehouseId == request.WarehouseId && reservation.RemainingQuantity > 0m
                       && reservation.ExecutionUnitId != intent.ExecutionId
                       && (balance.LocationId == request.SourceLocationId || balance.LocationId == request.TargetLocationId)
                   select reservation.ExecutionUnitId).AnyAsync(ct))
            return false;
        var protections = await (from reservation in database.Reservations
                                 join balance in database.Balances on reservation.BalanceId equals balance.Id
                                 where balance.ContainerId == request.ContainerId && reservation.RemainingQuantity > 0m
                                 select reservation).AsNoTracking().ToArrayAsync(ct);
        if (protections.Length != 1 || protections[0].ExecutionUnitId != intent.ExecutionId
            || protections[0].SourceDocumentId != intent.OrderId || protections[0].SourceLineId != request.LineId
            || protections[0].BalanceId != intent.SourceBalanceId || protections[0].RemainingQuantity != quantity
            || protections[0].Kind != (StockProtectionKind.Reserved | StockProtectionKind.InTransit)) return false;
        return !(await InboundPendingProtection.ReadAsync(database, request.WarehouseId, ct))
            .Any(receipt => receipt.ExecutionId != intent.ExecutionId
                && (receipt.ContainerId == request.ContainerId
                    || receipt.ReceiptLocationId == request.SourceLocationId || receipt.TargetLocationId == request.SourceLocationId
                    || receipt.ReceiptLocationId == request.TargetLocationId || receipt.TargetLocationId == request.TargetLocationId));
    }

    private Task ResourcesAsync(InboundTransportRequest request, CancellationToken ct) =>
        WmsResourceLocks.AcquireAsync(database, request.WarehouseId,
            [request.SourceLocationId, request.TargetLocationId], request.ContainerId, ct);

    private async Task ParentFenceAsync(Guid order, CancellationToken ct)
    {
        await using var command = database.Database.GetDbConnection().CreateCommand();
        command.Transaction = database.Database.CurrentTransaction!.GetDbTransaction();
        command.CommandText = "SELECT Version FROM wms_inbound_documents WHERE TenantId = @tenant AND Id = @id FOR UPDATE";
        var tenant = command.CreateParameter(); tenant.ParameterName = "@tenant"; tenant.Value = database.TenantId.ToString("D"); command.Parameters.Add(tenant);
        var id = command.CreateParameter(); id.ParameterName = "@id"; id.Value = order.ToString("D"); command.Parameters.Add(id);
        if (await command.ExecuteScalarAsync(ct) is not string)
            throw new InvalidOperationException("The transport parent identity is missing.");
    }

    private Task<List<InboundFact>> HistoryAsync(Guid order, CancellationToken ct) =>
        database.InboundFacts.AsNoTracking().Where(row => row.OrderId == order).OrderBy(row => row.Sequence).ToListAsync(ct);

    private static InboundTransportDecision? Verification(InboundTransportVerification verification) => verification switch
    {
        InboundTransportVerification.Verified => null,
        InboundTransportVerification.Rejected => InboundTransportDecision.Rejected,
        InboundTransportVerification.Unavailable => InboundTransportDecision.Unavailable,
        _ => throw new InvalidOperationException("The transport adapter returned an unsupported verification result.")
    };

    private async Task<InboundTransportResult> OwnAsync(Func<Task<InboundTransportResult>> action, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (database.ChangeTracker.Entries().Any() || database.Database.CurrentTransaction is not null
            || System.Transactions.Transaction.Current is not null)
            throw new InvalidOperationException("Inbound transport requires an independent clean owner.");
        try { return await action(); }
        catch (CountLockConflictException) { return new(InboundTransportDecision.Blocked); }
        catch (DbUpdateException error) when (error.InnerException is MySqlException { Number: 1062 })
        {
            ct.ThrowIfCancellationRequested();
            return new(InboundTransportDecision.Conflict);
        }
        catch (DbUpdateException error) when (error.InnerException is DbException failure)
        {
            ct.ThrowIfCancellationRequested();
            logger.LogError("WMS inbound transport failed {Code}", "DatabaseUnavailable");
            throw WmsDatabaseFailures.Safe(failure);
        }
        catch (DbException error)
        {
            ct.ThrowIfCancellationRequested();
            logger.LogError("WMS inbound transport failed {Code}", "DatabaseUnavailable");
            throw WmsDatabaseFailures.Safe(error);
        }
        catch (TimeoutException)
        {
            ct.ThrowIfCancellationRequested();
            logger.LogWarning("WMS inbound transport pending {Code}", "DependencyUnavailable");
            return new(InboundTransportDecision.Unavailable);
        }
        catch (JsonException)
        {
            logger.LogError("WMS inbound transport failed {Code}", "TransportHistoryInvalid");
            throw new InvalidOperationException("The persisted inbound transport history is invalid.");
        }
        finally { database.ChangeTracker.Clear(); }
    }
}
