using System.Data;
using System.Data.Common;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Wes.Wms.Domain;
using Wes.Wms.Features.Ledger;
using Wes.Wms.Features.MasterData;
using Wes.Wms.Infrastructure.Persistence;

namespace Wes.Wms.Features.Inbound;

internal sealed partial class InboundExecutionAccess(WmsDbContext database, IMasterDataAuthorization authorization,
    IWarehouseLocationSchema schema, TimeProvider clock, ILogger<InboundExecutionAccess> logger)
{
    private static readonly string[] ApprovalFields = ["State"];
    private static readonly string[] ReceiptFields = ["LineId", "ExecutionId", "Quantity", "ContainerId", "ReceiptLocationId", "TargetLocationId", "BalanceId"];
    private static readonly string[] WarehouseFields = ["Id", "Code", "Name", "Enabled", "Version", "CreatedAt"];
    private static readonly string[] LocationFields = [.. WarehouseFields, "WarehouseId", "PackId", "SchemaVersion"];
    private static readonly string[] UnitFields = ["Id", "Code", "Name", "Countable", "Precision", "Version", "CreatedAt"];
    private static readonly string[] ItemFields = ["Id", "Code", "Name", "BaseUnitId", "BatchRequired", "Version", "CreatedAt"];
    private static readonly string[] ContainerFields = ["Id", "Code", "Name", "WarehouseId", "Version", "CreatedAt"];

    internal Task<InboundExecutionResult> ApproveAsync(MasterDataSubject subject, Guid id, InboundApproval request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return ExecuteAsync(subject, id, request.OperationId, request.CorrelationId, request.ExpectedVersion,
            JsonSerializer.Serialize(new { request.ExpectedVersion }), null, ct);
    }

    internal Task<InboundExecutionResult> ReceiveAsync(MasterDataSubject subject, Guid id, InboundReceiptRegistration request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return ExecuteAsync(subject, id, request.OperationId, request.CorrelationId, request.ExpectedVersion,
            JsonSerializer.Serialize(new
            {
                request.ExpectedVersion,
                request.ExpectedBalanceVersion,
                request.LineId,
                request.ExecutionId,
                request.Quantity,
                request.ContainerId,
                request.ReceiptLocationId,
                request.TargetLocationId,
                request.BalanceId
            }), request, ct);
    }

    private async Task<InboundExecutionResult> ExecuteAsync(MasterDataSubject subject, Guid id, Guid operation, Guid correlation,
        string version, string input, InboundReceiptRegistration? receipt, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(subject); ct.ThrowIfCancellationRequested();
        if (database.ChangeTracker.Entries().Any() || database.Database.CurrentTransaction is not null
            || System.Transactions.Transaction.Current is not null)
            throw new InvalidOperationException("Inbound execution requires an independent clean owner.");
        if (subject.TenantId != database.TenantId || subject.ActorId == Guid.Empty || subject.SessionId == Guid.Empty)
            return Reject(InboundExecutionDecision.Forbidden);
        var quantity = 0m;
        if (new[] { id, operation, correlation }.Contains(Guid.Empty) || !IsVersion(version)
            || receipt is not null && (!IsVersion(receipt.ExpectedBalanceVersion)
                || new[] { receipt.LineId, receipt.ExecutionId, receipt.ContainerId, receipt.ReceiptLocationId,
                    receipt.TargetLocationId, receipt.BalanceId }.Contains(Guid.Empty)
                || !InboundDraftAccess.TryQuantity(receipt.Quantity, out quantity) || quantity > StockMovementQuantity.MaximumValue))
            return Reject(InboundExecutionDecision.Invalid);
        var kind = receipt is null ? InboundFactKind.Approved : InboundFactKind.Received;
        try
        {
            var creator = await database.InboundDocuments.AsNoTracking().Where(row => row.Id == id)
                .Select(row => (Guid?)row.CreatorId).SingleOrDefaultAsync(ct);
            if (creator is null) return Reject(InboundExecutionDecision.NotFound);
            if (!await DocumentAllowedAsync(subject, creator.Value, receipt is not null, ct)) return Reject(InboundExecutionDecision.Forbidden);
            InboundDocument document;
            InboundOrder domain;
            await using (var snapshot = await database.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct))
            {
                document = await database.InboundDocuments.AsNoTracking().SingleAsync(row => row.Id == id, ct);
                domain = document.Restore(await database.InboundFacts.AsNoTracking().Where(row => row.OrderId == id)
                    .OrderBy(row => row.Sequence).ToListAsync(ct));
                await snapshot.CommitAsync(ct);
            }
            var itemIds = domain.Lines.Select(line => line.ItemId).Distinct().ToArray();
            var items = await database.StockItems.AsNoTracking().Where(row => itemIds.Contains(row.Id)).ToArrayAsync(ct);
            var unitIds = items.Select(item => item.BaseUnitId).Distinct().ToArray();
            var units = await database.StockUnits.AsNoTracking().Where(row => unitIds.Contains(row.Id)).ToArrayAsync(ct);
            if (domain.Lines.Any(line => !items.Any(item => item.Id == line.ItemId && item.BaseUnitId == line.UnitId
                && item.BatchRequired == (line.Batch.Length > 0) && units.Any(unit => unit.Id == item.BaseUnitId && unit.Precision == line.Precision))))
                return Reject(InboundExecutionDecision.Invalid);
            var parentCreator = await database.MasterDataChanges.AsNoTracking().Where(row => row.WarehouseId == document.WarehouseId && row.LocationId == null)
                .Select(row => (Guid?)row.ActorId).SingleOrDefaultAsync(ct);
            if (parentCreator is null) return Reject(InboundExecutionDecision.NotFound);
            var locations = receipt is null ? [] : await database.Locations.AsNoTracking()
                .Where(row => row.Id == receipt.ReceiptLocationId || row.Id == receipt.TargetLocationId).ToArrayAsync(ct);
            var locationCreators = receipt is null ? [] : await database.MasterDataChanges.AsNoTracking()
                .Where(row => row.LocationId == receipt.ReceiptLocationId || row.LocationId == receipt.TargetLocationId)
                .Select(row => new { row.LocationId, row.ActorId }).ToArrayAsync(ct);
            var container = receipt is null ? null : await database.StockContainers.AsNoTracking().SingleOrDefaultAsync(row => row.Id == receipt.ContainerId, ct);
            var line = receipt is null ? null : domain.Lines.SingleOrDefault(row => row.Id == receipt.LineId);
            if (receipt is not null && (container is null || line is null
                || locations.Length != new[] { receipt.ReceiptLocationId, receipt.TargetLocationId }.Distinct().Count()
                || locationCreators.Length != locations.Length)) return Reject(InboundExecutionDecision.NotFound);
            async Task<bool> Allowed(CancellationToken token)
            {
                if (!await DocumentAllowedAsync(subject, creator.Value, receipt is not null, token)
                    || !await FieldsAsync(subject, "wms.warehouses.read", parentCreator.Value, WarehouseFields, token)) return false;
                foreach (var item in items)
                    if (!await FieldsAsync(subject, "wms.items.read", item.ActorId, ItemFields, token)) return false;
                foreach (var unit in units)
                    if (!await FieldsAsync(subject, "wms.units.read", unit.ActorId, UnitFields, token)) return false;
                if (container is not null && !await FieldsAsync(subject, "wms.containers.read", container.ActorId, ContainerFields, token)) return false;
                foreach (var locationCreator in locationCreators)
                    if (!await FieldsAsync(subject, "wms.locations.read", locationCreator.ActorId, LocationFields, token)) return false;
                return true;
            }
            if (!await Allowed(ct)) return Reject(InboundExecutionDecision.Forbidden);
            var old = await ReplayAsync(subject, id, operation, kind, input, ct);
            if (old is not null) return await Allowed(ct) ? old : Reject(InboundExecutionDecision.Forbidden);
            if (await database.InboundFacts.AnyAsync(row => row.OperationId == operation
                || receipt != null && row.ExecutionId == receipt.ExecutionId, ct)) return Reject(InboundExecutionDecision.Conflict);
            if (document.Version != version) return Reject(InboundExecutionDecision.Conflict);
            if (receipt is not null)
            {
                if (container!.WarehouseId != document.WarehouseId || locations.Any(location => location.WarehouseId != document.WarehouseId))
                    return Reject(InboundExecutionDecision.Blocked);
                var registered = await database.DimensionRegistrations.AsNoTracking().SingleOrDefaultAsync(row => row.BalanceId == receipt.BalanceId, ct);
                if (registered is null || registered.WarehouseId != document.WarehouseId || registered.LocationId != receipt.ReceiptLocationId
                    || registered.ContainerId != receipt.ContainerId || registered.ItemId != line!.ItemId
                    || registered.UnitId != line.UnitId || registered.Batch != line.Batch || registered.Precision != line.Precision)
                    return Reject(InboundExecutionDecision.Invalid);
            }
            var rejected = InboundExecutionDecision.Blocked;
            var guards = new InboundWriteGuards(async token =>
            {
                await WmsResourceLocks.AcquireAsync(database, document.WarehouseId, locations.Select(location => location.Id),
                    receipt?.ContainerId, token);
                var invalid = await OperationalAsync(document.WarehouseId, locations, receipt, token);
                if (invalid is not null) { rejected = invalid.Value; return false; }
                if (await Allowed(token)) return true;
                rejected = InboundExecutionDecision.Forbidden; return false;
            }, token =>
            {
                token.ThrowIfCancellationRequested();
                var fact = database.ChangeTracker.Entries<InboundFact>().Single(entry => entry.State == EntityState.Added
                    && entry.Entity.OperationId == operation).Entity;
                database.InboundExecutionOperations.Add(new(fact, input, clock.GetUtcNow()));
                if (receipt is not null && receipt.ReceiptLocationId != receipt.TargetLocationId)
                {
                    var header = database.ChangeTracker.Entries<InboundDocument>().Single(entry => entry.Entity.Id == id).Entity;
                    database.InboundTransportIntents.Add(new(header, fact, receipt.BalanceId, line!.Precision));
                    var protection = new StockReservation(database.TenantId, receipt.ExecutionId, receipt.BalanceId,
                        id, receipt.LineId, subject.ActorId, quantity, fact.OccurredAt);
                    protection.TryChange(ReservationAction.MarkInTransit, 0m);
                    database.Reservations.Add(protection);
                    database.ReservationChanges.Add(new(database.TenantId, operation, subject.ActorId, correlation,
                        ReservationAction.Reserve, protection, quantity, 0m, 0, fact.OccurredAt));
                }
                return Task.CompletedTask;
            }, async token =>
            {
                if (await Allowed(token)) return true;
                rejected = InboundExecutionDecision.Forbidden; return false;
            });
            var writer = new InboundWriter(database, clock);
            var result = receipt is null ? await writer.ApproveAsync(id, operation, subject.ActorId, correlation, version, ct, guards)
                : await writer.ReceiveAsync(id, new(operation, subject.ActorId, receipt.ExecutionId, line!.Id, line.ItemId, line.UnitId, line.Batch,
                    receipt.ContainerId, receipt.ReceiptLocationId, receipt.TargetLocationId, quantity),
                    correlation, version, receipt.BalanceId, receipt.ExpectedBalanceVersion, ct, guards);
            // A concurrent winner can establish pending protection after our first replay query.
            if (result.Decision is InboundWriteDecision.Replayed or InboundWriteDecision.SourceRejected)
                return !await Allowed(ct) ? Reject(InboundExecutionDecision.Forbidden)
                    : await ReplayAsync(subject, id, operation, kind, input, ct)
                        ?? Reject(result.Decision == InboundWriteDecision.SourceRejected ? rejected : InboundExecutionDecision.Conflict);
            return result.Decision switch
            {
                InboundWriteDecision.Committed => new(InboundExecutionDecision.Committed, result.Id, result.Version),
                InboundWriteDecision.Forbidden => Reject(InboundExecutionDecision.Forbidden),
                InboundWriteDecision.Invalid or InboundWriteDecision.DimensionMismatch => Reject(InboundExecutionDecision.Invalid),
                InboundWriteDecision.NotFound => Reject(InboundExecutionDecision.NotFound),
                InboundWriteDecision.Conflict or InboundWriteDecision.VersionConflict => Reject(InboundExecutionDecision.Conflict),
                _ => throw new InvalidOperationException("Unknown inbound execution outcome.")
            };
        }
        catch (CountLockConflictException) { return Reject(InboundExecutionDecision.Blocked); }
        catch (DbException error)
        {
            ct.ThrowIfCancellationRequested(); logger.LogError("WMS inbound execution failed {Code}", "DatabaseUnavailable");
            throw WmsDatabaseFailures.Safe(error);
        }
        catch (WmsDatabaseException)
        {
            logger.LogError("WMS inbound execution failed {Code}", "DatabaseUnavailable"); throw;
        }
        catch (JsonException)
        {
            logger.LogError("WMS inbound execution rejected invalid history {Code}", "InboundHistoryInvalid");
            throw new InvalidOperationException("The persisted inbound history is invalid.");
        }
        finally { database.ChangeTracker.Clear(); }
    }

    private async Task<InboundExecutionResult?> ReplayAsync(MasterDataSubject subject, Guid id, Guid operation,
        InboundFactKind kind, string input, CancellationToken ct)
    {
        var old = await database.InboundExecutionOperations.AsNoTracking().SingleOrDefaultAsync(row => row.OperationId == operation, ct);
        return old is null ? null : old.ActorId != subject.ActorId || old.OrderId != id || old.Kind != kind || old.Input != input
            ? Reject(InboundExecutionDecision.Conflict) : new(InboundExecutionDecision.Replayed, old.ResultId, old.AfterVersion);
    }
    private async Task<bool> DocumentAllowedAsync(MasterDataSubject subject, Guid creator, bool receiving, CancellationToken ct) =>
        await FieldsAsync(subject, receiving ? "wms.inbound.receive" : "wms.inbound.approve", creator, receiving ? ReceiptFields : ApprovalFields, ct)
        && await FieldsAsync(subject, "wms.inbound.read", creator, InboundAccess.Fields, ct);
    private async Task<bool> FieldsAsync(MasterDataSubject subject, string permission, Guid creator, string[] fields, CancellationToken ct)
    {
        var allowed = await authorization.ReadFieldsAsync(subject, permission, creator, ct);
        ct.ThrowIfCancellationRequested();
        if (allowed is null) throw new InvalidOperationException("Inbound execution authority returned no decision.");
        return fields.All(allowed.ToHashSet(StringComparer.Ordinal).Contains);
    }
    private static bool IsVersion(string value) => Guid.TryParseExact(value, "D", out var id) && id != Guid.Empty;
    private InboundExecutionResult Reject(InboundExecutionDecision decision)
    {
        logger.LogWarning("WMS inbound execution rejected {Code}", decision.ToString()); return new(decision);
    }
}
