using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using MySql.Data.MySqlClient;
using Wes.Wms.Domain;
using Wes.Wms.Features.Inbound;
using Wes.Wms.Features.Ledger;
using Wes.Wms.Features.MasterData;
using Wes.Wms.Infrastructure.Persistence;

namespace Wes.Wms.Features.Transfers;

internal sealed partial class TransferAccess(WmsDbContext database, IMasterDataAuthorization authorization,
    IWarehouseLocationSchema schema, TimeProvider clock, ILogger<TransferAccess> logger)
{
    private static readonly string[] ReadFields = ["Id", "Code", "WarehouseId", "State", "Version", "CreatedAt", "Lines"];
    private static readonly Regex QuantityPattern = new("^(?:0|[1-9][0-9]*)(?:\\.[0-9]{1,6})?$", RegexOptions.CultureInvariant);

    internal async Task<TransferResult> CreateAsync(MasterDataSubject subject, TransferDraftRequest request, CancellationToken ct)
    {
        Clean(ct);
        if (!ValidSubject(subject) || request.OperationId == Guid.Empty || request.CorrelationId == Guid.Empty
            || request.WarehouseId == Guid.Empty || !MasterDataValue.IsCode(request.Code)
            || request.Lines is null || request.Lines.Count is < 1 or > 100
            || request.Lines.Any(row => row is null || row.Id == Guid.Empty || row.SourceBalanceId == Guid.Empty
                || row.TargetBalanceId == Guid.Empty || row.Branch is < 0 or > 1 || !TryQuantity(row.Quantity, out _))
            || request.Lines.Select(row => row.Id).Distinct().Count() != request.Lines.Count
            || request.Lines.SelectMany(row => new[] { row.SourceBalanceId, row.TargetBalanceId }).Distinct().Count() != request.Lines.Count * 2)
            return Reject(TransferDecision.Invalid);
        var input = JsonSerializer.Serialize(new { request.Code, request.WarehouseId, request.Lines });
        try
        {
            var old = await FindAsync(request.OperationId, ct);
            if (old is not null)
            {
                var existing = await LoadAsync(old.OrderId, ct);
                if (existing is null) return Reject(TransferDecision.Conflict);
                var existingDimensions = await DimensionsAsync(existing.Document.Lines, ct);
                if (!await AllowedAsync(subject, existing.Document.CreatorId, "create", existingDimensions, ct))
                    return Reject(TransferDecision.Forbidden);
                return Original(old, subject, old.OrderId, TransferFactKind.Created, input);
            }
            var dimensions = await DimensionsAsync(request.Lines.SelectMany(row => new[] { row.SourceBalanceId, row.TargetBalanceId }), ct);
            if (dimensions.Length != request.Lines.Count * 2) return Reject(TransferDecision.NotFound);
            var plan = new List<TransferPlanLine>();
            foreach (var row in request.Lines)
            {
                var source = dimensions.Single(dimension => dimension.Balance.Id == row.SourceBalanceId);
                var target = dimensions.Single(dimension => dimension.Balance.Id == row.TargetBalanceId);
                _ = TryQuantity(row.Quantity, out var quantity);
                if (!source.IsLegal || !target.IsLegal || source.Balance.WarehouseId != request.WarehouseId
                    || !source.Balance.CanTransferTo(target.Balance)
                    || !QuantityPrecision.Create(source.Balance.Precision).CanRepresent(quantity))
                    return Reject(TransferDecision.Invalid);
                plan.Add(new(row.Id, Guid.NewGuid(), row.SourceBalanceId, row.TargetBalanceId, source.Item.Id,
                    source.Unit.Id, source.Balance.Batch, source.Balance.Precision, source.Container.Id,
                    source.Location.Id, target.Location.Id, row.Branch, quantity));
            }
            if (plan.Select(row => row.ContainerId).Distinct().Count() != plan.Count)
                return Reject(TransferDecision.Invalid);
            if (plan.Select(row => row.TargetLocationId).Distinct().Count() != plan.Count)
                return Reject(TransferDecision.Invalid);
            if (!await AllowedAsync(subject, subject.ActorId, "create", dimensions, ct)) return Reject(TransferDecision.Forbidden);
            await using var transaction = await database.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
            await FencesAsync(request.WarehouseId, dimensions, null, ct);
            // The warehouse fence serializes concurrent creations before the permanent operation is reread.
            old = await FindAsync(request.OperationId, ct);
            if (old is not null)
                return await AuthorizedOriginalAsync(old, subject, old.OrderId, TransferFactKind.Created, "create", input, ct);
            dimensions = await DimensionsAsync(plan, ct);
            var operational = await OperationalAsync(dimensions, ct);
            if (operational is not null) return Reject(operational.Value);
            if (!await AllowedAsync(subject, subject.ActorId, "create", dimensions, ct)) return Reject(TransferDecision.Forbidden);
            var document = new TransferDocument(database.TenantId, request.OperationId, subject.ActorId, request.WarehouseId,
                request.Code, plan, clock.GetUtcNow());
            document.Advance(TransferState.Draft);
            database.TransferDocuments.Add(document);
            database.TransferFacts.Add(new(document, request.OperationId, subject.ActorId, request.CorrelationId,
                TransferFactKind.Created, Guid.Empty, input, clock.GetUtcNow()));
            await database.SaveChangesAsync(ct);
            operational = await OperationalAsync(dimensions, ct);
            if (operational is not null || !await AllowedAsync(subject, document.CreatorId, "create", dimensions, ct))
            { await transaction.RollbackAsync(ct); return Reject(operational ?? TransferDecision.Forbidden); }
            await transaction.CommitAsync(ct);
            return new(TransferDecision.Committed, document.Id, document.Version);
        }
        catch (CountLockConflictException) { return Reject(TransferDecision.Blocked); }
        catch (DbUpdateException error) when (error.InnerException is MySqlException { Number: 1062 })
        { return Reject(TransferDecision.Conflict); }
        catch (DbUpdateException error) when (error.InnerException is DbException failure)
        { ct.ThrowIfCancellationRequested(); throw WmsDatabaseFailures.Safe(failure); }
        catch (DbException error) { ct.ThrowIfCancellationRequested(); throw WmsDatabaseFailures.Safe(error); }
        finally { database.ChangeTracker.Clear(); }
    }

    internal Task<TransferResult> ApproveAsync(MasterDataSubject subject, Guid id, TransferApprovalRequest request,
        Guid operation, Guid correlation, CancellationToken ct) =>
        ExecuteAsync(subject, id, operation, correlation, TransferFactKind.Approved, "approve", request.ExpectedVersion,
            Guid.Empty, JsonSerializer.Serialize(request), async (document, facts, balances, token) =>
            {
                if (document.State != TransferState.Draft) return TransferDecision.Conflict;
                var ids = document.Lines.SelectMany(row => new[] { row.SourceBalanceId, row.TargetBalanceId }).ToArray();
                if (request.Balances is null || request.Balances.Count != ids.Length
                    || request.Balances.Any(row => row is null || !Version(row.ExpectedVersion))
                    || request.Balances.Select(row => row.BalanceId).Distinct().Count() != ids.Length
                    || request.Balances.Select(row => row.BalanceId).Except(ids).Any())
                    return TransferDecision.Invalid;
                if (balances.Any(row => request.Balances.Single(version => version.BalanceId == row.Id).ExpectedVersion != row.Version))
                    return TransferDecision.Conflict;
                foreach (var line in document.Lines)
                {
                    var source = balances.Single(row => row.Id == line.SourceBalanceId);
                    var target = balances.Single(row => row.Id == line.TargetBalanceId);
                    if (!await WholeUnitAsync(document.WarehouseId, line, token)
                        || source.Quantity != line.PlannedQuantity || target.Quantity != 0m
                        || await database.Reservations.AnyAsync(row => row.RemainingQuantity > 0m
                            && (row.BalanceId == source.Id || row.BalanceId == target.Id), token))
                        return TransferDecision.Blocked;
                    if (await HasForeignProtectionAsync(document, line, token)) return TransferDecision.Blocked;
                }
                foreach (var line in document.Lines)
                {
                    var reservation = new StockReservation(database.TenantId, line.ExecutionId, line.SourceBalanceId,
                        document.Id, line.Id, subject.ActorId, line.PlannedQuantity, clock.GetUtcNow());
                    database.Reservations.Add(reservation);
                    database.ReservationChanges.Add(new(database.TenantId, TransitionId(operation, line.Id), subject.ActorId,
                        correlation, ReservationAction.Reserve, reservation, line.PlannedQuantity, 0m, 0, clock.GetUtcNow()));
                    balances.Single(row => row.Id == line.SourceBalanceId).TouchReservationFence();
                }
                return null;
            }, ct);

    internal Task<TransferResult> DispatchAsync(MasterDataSubject subject, Guid id, TransferLineRequest request,
        Guid operation, Guid correlation, CancellationToken ct) =>
        ExecuteAsync(subject, id, operation, correlation, TransferFactKind.Dispatched, "dispatch", request.ExpectedVersion,
            request.LineId, JsonSerializer.Serialize(request), async (document, facts, balances, token) =>
            {
                var line = document.Lines.SingleOrDefault(row => row.Id == request.LineId);
                if (line is null) return TransferDecision.NotFound;
                if (line.Branch != 0) return TransferDecision.Unsupported;
                if (document.State is not (TransferState.Approved or TransferState.Executing)
                    || TransferProgress.For(facts, line.Id).Execution != TransferExecutionStatus.Unstarted)
                    return TransferDecision.Conflict;
                return await ChangeReservationAsync(subject, document, line, balances, ReservationAction.MarkInTransit,
                    operation, correlation, token) ? null : TransferDecision.Blocked;
            }, ct);

    internal Task<TransferResult> UnknownAsync(MasterDataSubject subject, Guid id, TransferUnknownRequest request,
        Guid operation, Guid correlation, CancellationToken ct) =>
        ExecuteAsync(subject, id, operation, correlation, TransferFactKind.Unknown, "unknown", request.ExpectedVersion,
            request.LineId, JsonSerializer.Serialize(request), async (document, facts, balances, token) =>
            {
                var line = document.Lines.SingleOrDefault(row => row.Id == request.LineId);
                if (line is null) return TransferDecision.NotFound;
                if (!Reason(request.Reason)) return TransferDecision.Invalid;
                if (TransferProgress.For(facts, line.Id).Execution != TransferExecutionStatus.InTransit)
                    return TransferDecision.Conflict;
                return await ChangeReservationAsync(subject, document, line, balances, ReservationAction.MarkUnknown,
                    operation, correlation, token) ? null : TransferDecision.Blocked;
            }, ct);

    internal Task<TransferResult> CloseAsync(MasterDataSubject subject, Guid id, TransferClosureRequest request,
        Guid operation, Guid correlation, CancellationToken ct) =>
        ExecuteAsync(subject, id, operation, correlation, TransferFactKind.Closed, "close", request.ExpectedVersion,
            Guid.Empty, JsonSerializer.Serialize(request), async (document, facts, balances, token) =>
            {
                if (!Reason(request.Reason)) return TransferDecision.Invalid;
                if (document.State is TransferState.Completed or TransferState.Cancelled or TransferState.ClosedPartial)
                    return TransferDecision.Conflict;
                if (document.Lines.Any(line =>
                    {
                        var progress = TransferProgress.For(facts, line.Id);
                        return progress.Execution is TransferExecutionStatus.InTransit or TransferExecutionStatus.Unknown
                            || progress.Execution == TransferExecutionStatus.Arrived && progress.Posting != TransferPostingStatus.Synced;
                    })) return TransferDecision.Blocked;
                if (document.State != TransferState.Draft)
                    foreach (var line in document.Lines.Where(line =>
                        TransferProgress.For(facts, line.Id).Execution == TransferExecutionStatus.Unstarted))
                        if (!await ChangeReservationAsync(subject, document, line, balances, ReservationAction.ReleaseUnstarted,
                            operation, correlation, token)) return TransferDecision.Blocked;
                return null;
            }, ct);

    internal async Task<TransferResult> ArriveAsync(MasterDataSubject subject, Guid id, TransferArrivalRequest request,
        Guid operation, Guid correlation, CancellationToken ct)
    {
        var result = await ExecuteAsync(subject, id, operation, correlation, TransferFactKind.Arrived, "arrive",
            request.ExpectedVersion, request.LineId, JsonSerializer.Serialize(request), (document, facts, balances, token) =>
            {
                token.ThrowIfCancellationRequested();
                var line = document.Lines.SingleOrDefault(row => row.Id == request.LineId);
                TransferDecision? rejection = line is null ? TransferDecision.NotFound
                    : line.Branch != 0 ? TransferDecision.Unsupported
                    : !Reason(request.Reason) || !TryQuantity(request.Quantity, out var quantity)
                        || quantity != line.PlannedQuantity || request.SourceLocationId != line.SourceLocationId
                        || request.TargetLocationId != line.TargetLocationId || request.ContainerId != line.ContainerId
                        ? TransferDecision.Invalid
                    : TransferProgress.For(facts, line.Id).Execution != TransferExecutionStatus.InTransit
                        ? TransferDecision.Conflict : null;
                return Task.FromResult(rejection);
            }, ct);
        if (result.Decision == TransferDecision.Committed)
        {
            // This call is intentionally after the arrival transaction commits. A posting failure cannot erase arrival.
            try
            {
                _ = await PostArrivalAsync(subject, id, request.LineId, TransitionId(operation, request.LineId),
                    correlation, result.Version!, "arrive", JsonSerializer.Serialize(new TransferLineRequest(result.Version!, request.LineId)), ct);
            }
            catch (WmsDatabaseException error)
            {
                logger.LogWarning("Transfer arrival retained pending ledger application {Transient}", error.IsTransient);
            }
            catch (DbUpdateException)
            {
                logger.LogWarning("Transfer arrival retained pending ledger application {Code}", "TransferPostingFailed");
            }
        }
        return result;
    }

    private async Task<TransferResult> ExecuteAsync(MasterDataSubject subject, Guid id, Guid operation, Guid correlation,
        TransferFactKind kind, string permission, string version, Guid lineId, string input,
        Func<TransferDocument, TransferFact[], StockBalance[], CancellationToken, Task<TransferDecision?>> mutate, CancellationToken ct)
    {
        Clean(ct);
        if (!ValidSubject(subject) || id == Guid.Empty || operation == Guid.Empty || correlation == Guid.Empty
            || !Version(version) || input.Length > 65536
            || kind is TransferFactKind.Dispatched or TransferFactKind.Arrived or TransferFactKind.Unknown && lineId == Guid.Empty)
            return Reject(TransferDecision.Invalid);
        try
        {
            var loaded = await LoadAsync(id, ct);
            if (loaded is null) return Reject(TransferDecision.NotFound);
            var dimensions = await DimensionsAsync(loaded.Document.Lines, ct);
            if (!await AllowedAsync(subject, loaded.Document.CreatorId, permission, dimensions, ct)) return Reject(TransferDecision.Forbidden);
            var old = await FindAsync(operation, ct);
            if (old is not null) return Original(old, subject, id, kind, input);
            await using var transaction = await database.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
            await FencesAsync(loaded.Document.WarehouseId, dimensions, id, ct);
            // Do not compare the stale pre-lock version before checking the winning original tuple.
            old = await FindAsync(operation, ct);
            if (old is not null) return await AuthorizedOriginalAsync(old, subject, id, kind, permission, input, ct);
            var document = await database.TransferDocuments.SingleAsync(row => row.Id == id, ct);
            var facts = await database.TransferFacts.AsNoTracking().Where(row => row.OrderId == id)
                .OrderBy(row => row.Sequence).ToArrayAsync(ct);
            TransferProgress.Validate(document, facts);
            dimensions = await DimensionsAsync(document.Lines, ct);
            if (!await AllowedAsync(subject, document.CreatorId, permission, dimensions, ct)) return Reject(TransferDecision.Forbidden);
            if (document.Version != version) return Reject(TransferDecision.Conflict);
            var operational = await OperationalAsync(dimensions, ct);
            if (operational is not null) return Reject(operational.Value);
            var ids = document.Lines.SelectMany(row => new[] { row.SourceBalanceId, row.TargetBalanceId }).ToArray();
            var balances = await database.Balances.Where(WmsIdentityPredicate.MatchesAnyId<StockBalance>(ids, row => row.Id)).ToArrayAsync(ct);
            var rejection = await mutate(document, facts, balances, ct);
            if (rejection is not null) return Reject(rejection.Value);
            var state = kind switch
            {
                TransferFactKind.Approved => TransferState.Approved,
                TransferFactKind.Closed => document.Lines.Any(line => TransferProgress.For(facts, line.Id).Posting == TransferPostingStatus.Synced)
                    ? TransferState.ClosedPartial : TransferState.Cancelled,
                _ => TransferState.Executing
            };
            document.Advance(state);
            database.TransferFacts.Add(new(document, operation, subject.ActorId, correlation, kind, lineId, input, clock.GetUtcNow()));
            await database.SaveChangesAsync(ct);
            operational = await OperationalAsync(dimensions, ct);
            if (operational is not null || !await AllowedAsync(subject, document.CreatorId, permission, dimensions, ct))
            { await transaction.RollbackAsync(ct); return Reject(operational ?? TransferDecision.Forbidden); }
            await transaction.CommitAsync(ct);
            return new(TransferDecision.Committed, id, document.Version);
        }
        catch (CountLockConflictException) { return Reject(TransferDecision.Blocked); }
        catch (DbUpdateConcurrencyException) { return Reject(TransferDecision.Conflict); }
        catch (DbUpdateException error) when (error.InnerException is MySqlException { Number: 1062 })
        { return Reject(TransferDecision.Conflict); }
        catch (DbUpdateException error) when (error.InnerException is DbException failure)
        { ct.ThrowIfCancellationRequested(); throw WmsDatabaseFailures.Safe(failure); }
        catch (DbException error) { ct.ThrowIfCancellationRequested(); throw WmsDatabaseFailures.Safe(error); }
        finally { database.ChangeTracker.Clear(); }
    }

    private async Task<bool> ChangeReservationAsync(MasterDataSubject subject, TransferDocument document, TransferPlanLine line,
        StockBalance[] balances, ReservationAction action, Guid operation, Guid correlation, CancellationToken ct)
    {
        var reservation = await database.Reservations.SingleOrDefaultAsync(row => row.ExecutionUnitId == line.ExecutionId, ct);
        if (reservation is null || reservation.SourceDocumentId != document.Id || reservation.SourceLineId != line.Id
            || reservation.BalanceId != line.SourceBalanceId || reservation.RemainingQuantity != line.PlannedQuantity)
            return false;
        var before = reservation.RemainingQuantity; var kind = reservation.Kind;
        if (!reservation.TryChange(action, line.PlannedQuantity)) return false;
        database.ReservationChanges.Add(new(database.TenantId, TransitionId(operation, line.Id), subject.ActorId, correlation,
            action, reservation, action == ReservationAction.ReleaseUnstarted ? line.PlannedQuantity : 0m, before, kind, clock.GetUtcNow()));
        balances.Single(row => row.Id == line.SourceBalanceId).TouchReservationFence();
        return true;
    }

    private Task<TransferFact?> FindAsync(Guid operation, CancellationToken ct) =>
        database.TransferFacts.AsNoTracking().SingleOrDefaultAsync(row => row.OperationId == operation, ct);
    private sealed record Loaded(TransferDocument Document, TransferFact[] Facts);
    private async Task<Loaded?> LoadAsync(Guid id, CancellationToken ct)
    {
        await using var snapshot = database.Database.CurrentTransaction is null
            ? await database.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct) : null;
        var document = await database.TransferDocuments.AsNoTracking().SingleOrDefaultAsync(row => row.Id == id, ct);
        if (document is null) return null;
        var facts = await database.TransferFacts.AsNoTracking().Where(row => row.OrderId == id).OrderBy(row => row.Sequence).ToArrayAsync(ct);
        TransferProgress.Validate(document, facts);
        if (snapshot is not null) await snapshot.CommitAsync(ct);
        return new(document, facts);
    }
    private async Task<TransferResult> AuthorizedOriginalAsync(TransferFact old, MasterDataSubject subject, Guid id,
        TransferFactKind kind, string permission, string input, CancellationToken ct)
    {
        var original = await LoadAsync(old.OrderId, ct);
        if (original is null) return Reject(TransferDecision.Conflict);
        var dimensions = await DimensionsAsync(original.Document.Lines, ct);
        if (dimensions.Length != original.Document.Lines.Count * 2)
            return Reject(TransferDecision.Forbidden);
        if (!await AllowedAsync(subject, original.Document.CreatorId, permission, dimensions, ct))
            return Reject(TransferDecision.Forbidden);
        return Original(old, subject, id, kind, input);
    }

    private static TransferResult Original(TransferFact old, MasterDataSubject subject, Guid id, TransferFactKind kind, string input) =>
        old.ActorId == subject.ActorId && old.OrderId == id && old.Kind == kind && old.Input == input
            ? new(TransferDecision.Replayed, id, old.AfterVersion) : new(TransferDecision.Conflict);
    private TransferResult Reject(TransferDecision decision)
    { logger.LogWarning("WMS transfer operation rejected {Code}", decision); return new(decision); }
    private bool ValidSubject(MasterDataSubject subject) => subject.TenantId == database.TenantId
        && subject.ActorId != Guid.Empty && subject.SessionId != Guid.Empty;
    private static bool Version(string? value) => Guid.TryParseExact(value, "D", out var id) && id != Guid.Empty;
    private static bool Reason(string? value) => value is { Length: > 0 and <= 256 }
        && value == value.Trim() && !value.Any(char.IsControl);
    private static bool TryQuantity(string? text, out decimal quantity)
    {
        quantity = 0m;
        return text is not null && QuantityPattern.IsMatch(text)
            && decimal.TryParse(text, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out quantity)
            && quantity > 0m && quantity <= StockMovementQuantity.MaximumValue;
    }
    private static Guid TransitionId(Guid operation, Guid line) =>
        new(SHA256.HashData(Encoding.UTF8.GetBytes($"{operation:D}:{line:D}")).AsSpan(0, 16));
    private void Clean(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (database.ChangeTracker.Entries().Any() || database.Database.CurrentTransaction is not null
            || System.Transactions.Transaction.Current is not null)
            throw new InvalidOperationException("Transfers require an independent clean owner.");
    }
}
