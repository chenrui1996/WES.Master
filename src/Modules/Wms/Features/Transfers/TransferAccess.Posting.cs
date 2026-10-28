using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Wes.Wms.Domain;
using Wes.Wms.Features.Ledger;
using Wes.Wms.Features.MasterData;
using Wes.Wms.Infrastructure.Persistence;

namespace Wes.Wms.Features.Transfers;

internal sealed partial class TransferAccess
{
    internal Task<TransferResult> SyncAsync(MasterDataSubject subject, Guid id, TransferLineRequest request,
        Guid operation, Guid correlation, CancellationToken ct) =>
        PostArrivalAsync(subject, id, request.LineId, operation, correlation, request.ExpectedVersion, "sync",
            JsonSerializer.Serialize(request), ct);

    private async Task<TransferResult> PostArrivalAsync(MasterDataSubject subject, Guid id, Guid lineId, Guid operation,
        Guid correlation, string version, string permission, string input, CancellationToken ct)
    {
        Clean(ct);
        if (!ValidSubject(subject) || id == Guid.Empty || lineId == Guid.Empty || operation == Guid.Empty
            || correlation == Guid.Empty || !Version(version)) return Reject(TransferDecision.Invalid);
        try
        {
            var loaded = await LoadAsync(id, ct);
            if (loaded is null) return Reject(TransferDecision.NotFound);
            var (initial, history) = loaded;
            var dimensions = await DimensionsAsync(initial.Lines, ct);
            if (!await AllowedAsync(subject, initial.CreatorId, permission, dimensions, ct)) return Reject(TransferDecision.Forbidden);
            var old = await FindAsync(operation, ct);
            if (old is not null) return Original(old, subject, id, TransferFactKind.Synced, input);
            var line = initial.Lines.SingleOrDefault(row => row.Id == lineId);
            if (line is null) return Reject(TransferDecision.NotFound);
            if (line.Branch != 0) return Reject(TransferDecision.Unsupported);
            var progress = TransferProgress.For(history, line.Id);
            if (progress.Execution != TransferExecutionStatus.Arrived || progress.Posting != TransferPostingStatus.Pending)
                return Reject(TransferDecision.Conflict);
            var source = dimensions.Single(row => row.Balance.Id == line.SourceBalanceId).Balance;
            var target = dimensions.Single(row => row.Balance.Id == line.TargetBalanceId).Balance;
            var rejected = TransferDecision.Blocked;
            var command = new StockPostingCommand(operation, subject.ActorId, correlation, StockPostingKind.Transfer,
                id, line.Id, line.ExecutionId, source.Id, target.Id, line.PlannedQuantity, source.Version, target.Version);
            var result = await new PostStock(database, clock).ExecuteComposedAsync(command, async token =>
            {
                var document = await database.TransferDocuments.SingleAsync(row => row.Id == id, token);
                var facts = await database.TransferFacts.AsNoTracking().Where(row => row.OrderId == id)
                    .OrderBy(row => row.Sequence).ToArrayAsync(token);
                TransferProgress.Validate(document, facts);
                if (document.Version != version || TransferProgress.For(facts, line.Id) != progress)
                    throw new DbUpdateConcurrencyException();
                var complete = document.Lines.All(candidate => candidate.Id == line.Id
                    || TransferProgress.For(facts, candidate.Id).Posting == TransferPostingStatus.Synced);
                document.Advance(complete ? TransferState.Completed : TransferState.Executing);
                database.TransferFacts.Add(new(document, operation, subject.ActorId, correlation, TransferFactKind.Synced,
                    line.Id, input, clock.GetUtcNow()));
            }, ct, async token =>
            {
                await FencesAsync(initial.WarehouseId, dimensions, id, token);
                var fresh = await LoadAsync(id, token);
                if (fresh is null) { rejected = TransferDecision.NotFound; return false; }
                dimensions = await DimensionsAsync(fresh.Document.Lines, token);
                if (!await AllowedAsync(subject, initial.CreatorId, permission, dimensions, token))
                { rejected = TransferDecision.Forbidden; return false; }
                old = await FindAsync(operation, token);
                if (old is not null) { rejected = TransferDecision.Conflict; return false; }
                if (fresh.Document.Version != version) { rejected = TransferDecision.Conflict; return false; }
                var operational = await OperationalAsync(dimensions, token);
                if (operational is not null) { rejected = operational.Value; return false; }
                if (!await WholeUnitAsync(initial.WarehouseId, line, token))
                { rejected = TransferDecision.Blocked; return false; }
                return true;
            }, async token =>
            {
                if (!await AllowedAsync(subject, initial.CreatorId, permission, dimensions, token))
                { rejected = TransferDecision.Forbidden; return false; }
                var operational = await OperationalAsync(dimensions, token);
                if (operational is not null) { rejected = operational.Value; return false; }
                return true;
            });
            if (result.Decision is StockPostingDecision.Posted or StockPostingDecision.Replayed)
            {
                var fact = await FindAsync(operation, ct);
                return fact is null ? Reject(TransferDecision.Conflict)
                    : new(result.Decision == StockPostingDecision.Posted ? TransferDecision.Committed : TransferDecision.Replayed, id, fact.AfterVersion);
            }
            old = await FindAsync(operation, ct);
            if (old is not null && await AllowedAsync(subject, initial.CreatorId, permission, dimensions, ct))
                return Original(old, subject, id, TransferFactKind.Synced, input);
            return Reject(result.Decision switch
            {
                StockPostingDecision.SourceRejected => rejected,
                StockPostingDecision.VersionConflict or StockPostingDecision.IdempotencyConflict => TransferDecision.Conflict,
                StockPostingDecision.NotFound => TransferDecision.NotFound,
                StockPostingDecision.Invalid or StockPostingDecision.DimensionMismatch => TransferDecision.Invalid,
                _ => TransferDecision.Blocked
            });
        }
        catch (CountLockConflictException) { return Reject(TransferDecision.Blocked); }
        catch (DbUpdateConcurrencyException) { return Reject(TransferDecision.Conflict); }
        finally { database.ChangeTracker.Clear(); }
    }
}
