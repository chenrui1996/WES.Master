using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using MySql.Data.MySqlClient;
using Wes.Wms.Domain;
using Wes.Wms.Features.Inbound;
using Wes.Wms.Features.Ledger;
using Wes.Wms.Features.MasterData;
using Wes.Wms.Infrastructure.Persistence;

namespace Wes.Wms.Features.Counts;

internal sealed partial class CountAccess(WmsDbContext database, IMasterDataAuthorization authorization,
    IWarehouseLocationSchema schema, TimeProvider clock)
{
    internal const string Policy = "WES-RULES-1.0/RULE-06/1800s";
    private static readonly string[] ReadFields = ["Id", "Code", "WarehouseId", "LocationId", "State", "Version", "Lines"];
    private static readonly Regex QuantityPattern = new("^(?:0|[1-9][0-9]*)(?:\\.[0-9]{1,6})?$", RegexOptions.CultureInvariant);

    internal Task<CountResult> CreateAsync(MasterDataSubject subject, CountCreateRequest request, Guid operation, Guid correlation,
        CancellationToken ct = default) =>
        RunAsync(subject, null, request, operation, correlation, StockCountAction.Create, "create",
            ["Code", "WarehouseId", "LocationId"], null, ct);
    internal Task<CountResult> StartAsync(MasterDataSubject subject, Guid id, CountStartRequest request, Guid operation, Guid correlation,
        CancellationToken ct = default) =>
        RunAsync(subject, id, request, operation, correlation, StockCountAction.Start, "start", ["State"], request.ExpectedVersion, ct);
    internal Task<CountResult> RecordAsync(MasterDataSubject subject, Guid id, CountRecordRequest request, Guid operation, Guid correlation,
        CancellationToken ct = default) =>
        RunAsync(subject, id, request, operation, correlation, StockCountAction.Record, "record",
            ["LineId", "Quantity", "LocationId", "ItemId", "ContainerId", "Reason"], request.ExpectedVersion, ct);
    internal Task<CountResult> AdjustAsync(MasterDataSubject subject, Guid id, CountStateRequest request, Guid operation, Guid correlation,
        CancellationToken ct = default) =>
        RunAsync(subject, id, request, operation, correlation, StockCountAction.Adjust, "adjust", ["State", "Reason"], request.ExpectedVersion, ct);
    internal Task<CountResult> InvalidateAsync(MasterDataSubject subject, Guid id, CountStateRequest request, Guid operation, Guid correlation,
        CancellationToken ct = default) =>
        RunAsync(subject, id, request, operation, correlation, StockCountAction.Invalidate, "invalidate", ["State", "Reason"], request.ExpectedVersion, ct);

    private async Task<CountResult> RunAsync(MasterDataSubject subject, Guid? id, object request, Guid operation, Guid correlation,
        StockCountAction action, string permission, string[] fields, string? version, CancellationToken ct)
    {
        Clean(ct);
        if (!ValidSubject(subject)) return new(CountDecision.Forbidden);
        if (operation == Guid.Empty || correlation == Guid.Empty || id == Guid.Empty
            || action != StockCountAction.Create && !ValidVersion(version)
            || request is CountCreateRequest create && (!MasterDataValue.IsCode(create.Code)
                || create.WarehouseId == Guid.Empty || create.LocationId == Guid.Empty)
            || request is CountStateRequest state && !WarehouseStateChange.ValidReason(state.Reason)
            || request is CountRecordRequest record && (!WarehouseStateChange.ValidReason(record.Reason)
                || new[] { record.LineId, record.LocationId, record.ItemId, record.ContainerId }.Contains(Guid.Empty)
                || !TryQuantity(record.Quantity, out _)))
            return new(CountDecision.Invalid);
        var input = JsonSerializer.Serialize(request, request.GetType());
        try
        {
            // A committed tuple is recovered before current lifecycle/resource protection is evaluated.
            var old = await database.CountFacts.AsNoTracking().SingleOrDefaultAsync(row => row.OperationId == operation, ct);
            if (old is not null)
                return await ReplayAsync(subject, id, old, action, input, permission, fields, ct);
            var plan = id.HasValue ? await database.Counts.AsNoTracking().SingleOrDefaultAsync(row => row.Id == id, ct) : null;
            if (id.HasValue && plan is null) return new(CountDecision.NotFound);
            if (plan is null)
            {
                var body = (CountCreateRequest)request;
                plan = new(database.TenantId, subject.ActorId, new(body.Code, body.WarehouseId, body.LocationId), clock.GetUtcNow());
            }
            if (!await AllowedAsync(subject, plan, permission, fields, ct)) return new(CountDecision.Forbidden);
            await using var transaction = await database.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
            await WmsResourceLocks.AcquireCountAsync(database, plan, ct);
            old = await database.CountFacts.AsNoTracking().SingleOrDefaultAsync(row => row.OperationId == operation, ct);
            if (old is not null) return await ReplayAsync(subject, id, old, action, input, permission, fields, ct);
            if (id.HasValue)
            {
                plan = await database.Counts.SingleAsync(row => row.Id == id, ct);
                await ValidateHistoryAsync(plan, ct);
                if (!await AllowedAsync(subject, plan, permission, fields, ct)) return new(CountDecision.Forbidden);
                if (plan.Version != version) return new(CountDecision.Conflict);
            }
            else
            {
                if (await database.Counts.AnyAsync(row => row.Code == plan.Code, ct)) return new(CountDecision.Conflict);
                database.Counts.Add(plan);
            }
            CountDecision? rejected;
            switch (action)
            {
                case StockCountAction.Create:
                    rejected = await OperationalAsync(plan, ct);
                    break;
                case StockCountAction.Start:
                    rejected = await StartCoreAsync(plan, subject, ct);
                    break;
                case StockCountAction.Record:
                    rejected = RecordCore(plan, subject, (CountRecordRequest)request);
                    break;
                case StockCountAction.Invalidate:
                    rejected = !plan.Active ? CountDecision.Conflict : null;
                    if (rejected is null) plan.End(false);
                    break;
                case StockCountAction.Adjust:
                    rejected = await AdjustCoreAsync(plan, subject, correlation, ct);
                    break;
                default: throw new InvalidOperationException("Unknown count action.");
            }
            if (rejected is not null) return new(rejected.Value);
            plan.Advance();
            database.CountFacts.Add(new(plan, operation, subject.ActorId, correlation, action, input, clock.GetUtcNow()));
            await database.SaveChangesAsync(ct);
            if (!await AllowedAsync(subject, plan, permission, fields, ct))
            {
                await transaction.RollbackAsync(ct);
                return new(CountDecision.Forbidden);
            }
            ct.ThrowIfCancellationRequested();
            await transaction.CommitAsync(ct);
            return new(CountDecision.Committed, plan.Id, plan.Version);
        }
        catch (DbUpdateConcurrencyException) { return await RecoverAsync(); }
        catch (DbUpdateException error) when (error.InnerException is MySqlException { Number: 1062 }) { return await RecoverAsync(); }
        catch (DbUpdateException error) when (error.InnerException is DbException failure) { throw WmsDatabaseFailures.Safe(failure); }
        catch (DbException error) { ct.ThrowIfCancellationRequested(); throw WmsDatabaseFailures.Safe(error); }
        finally { database.ChangeTracker.Clear(); }

        async Task<CountResult> RecoverAsync()
        {
            database.ChangeTracker.Clear();
            var old = await database.CountFacts.AsNoTracking().SingleOrDefaultAsync(row => row.OperationId == operation, ct);
            return old is null ? new(CountDecision.Conflict)
                : await ReplayAsync(subject, id, old, action, input, permission, fields, ct);
        }
    }

    private async Task<CountDecision?> StartCoreAsync(StockCount plan, MasterDataSubject subject, CancellationToken ct)
    {
        if (plan.State is not (StockCountState.Draft or StockCountState.Invalidated)) return CountDecision.Conflict;
        var operational = await OperationalAsync(plan, ct);
        if (operational is not null) return operational;
        var balances = await database.Balances.AsNoTracking().Where(row => row.WarehouseId == plan.WarehouseId
            && row.LocationId == plan.LocationId).OrderBy(row => row.Id).ToArrayAsync(ct);
        if (balances.Length is < 1 or > 100) return CountDecision.Blocked;
        foreach (var balance in balances)
            if (!await LegalAsync(balance, ct)) return CountDecision.Blocked;
        var containers = balances.Select(row => row.ContainerId).ToHashSet();
        if (await WmsResourceLocks.CountConflictAsync(database, plan.WarehouseId, [plan.LocationId], containers, ct))
            return CountDecision.Blocked;
        var protectedBalances = await (from reservation in database.Reservations.AsNoTracking()
                                       join balance in database.Balances.AsNoTracking() on reservation.BalanceId equals balance.Id
                                       where reservation.RemainingQuantity > 0m
                                       select balance).ToArrayAsync(ct);
        if (protectedBalances.Any(row => row.LocationId == plan.LocationId || containers.Contains(row.ContainerId)))
            return CountDecision.Blocked;
        var pending = await InboundPendingProtection.ReadAsync(database, plan.WarehouseId, ct);
        if (pending.Any(row => row.ReceiptLocationId == plan.LocationId || row.TargetLocationId == plan.LocationId
            || containers.Contains(row.ContainerId))) return CountDecision.Blocked;
        plan.Start(subject.ActorId, clock.GetUtcNow(), balances);
        return null;
    }

    private CountDecision? RecordCore(StockCount plan, MasterDataSubject subject, CountRecordRequest request)
    {
        if (!plan.Active) return CountDecision.Conflict;
        var line = plan.Lines.SingleOrDefault(row => row.Id == request.LineId);
        if (line is null || request.LocationId != plan.LocationId || request.ItemId != line.ItemId
            || request.ContainerId != line.ContainerId) return CountDecision.Conflict;
        if (!TryQuantity(request.Quantity, out var quantity) || !QuantityPrecision.Create(line.Precision).CanRepresent(quantity))
            return CountDecision.Invalid;
        plan.Record(line.Id, quantity, subject.ActorId, clock.GetUtcNow());
        return null;
    }

    private async Task<CountDecision?> AdjustCoreAsync(StockCount plan, MasterDataSubject subject, Guid correlation, CancellationToken ct)
    {
        if (plan.State != StockCountState.Ready || plan.Lines.Any(row => row.Quantity is null)) return CountDecision.Conflict;
        if (plan.Lines.Any(row => row.Quantity != row.BookQuantity && row.CountedBy == subject.ActorId))
            return CountDecision.Forbidden;
        if (plan.Lines.Any(row => Math.Abs(row.Quantity!.Value - row.BookQuantity) > StockMovementQuantity.MaximumValue))
            return CountDecision.Invalid;
        var operational = await OperationalAsync(plan, ct);
        if (operational is not null) return operational;
        if (!await new PostStock(database, clock).StageCountAdjustmentAsync(plan, subject.ActorId, correlation, ct))
            return CountDecision.Blocked;
        plan.End(true);
        return null;
    }

    private async Task<CountResult> ReplayAsync(MasterDataSubject subject, Guid? id, StockCountFact old,
        StockCountAction action, string input, string permission, string[] fields, CancellationToken ct)
    {
        var plan = await database.Counts.AsNoTracking().SingleOrDefaultAsync(row => row.Id == old.PlanId, ct)
            ?? throw new InvalidOperationException("Count receipt lost its plan.");
        if (!await AllowedAsync(subject, plan, permission, fields, ct)) return new(CountDecision.Forbidden);
        return old.ActorId == subject.ActorId && old.Action == action && old.Input == input
            && (!id.HasValue || old.PlanId == id)
            ? new(CountDecision.Replayed, old.PlanId, old.AfterVersion) : new(CountDecision.Conflict);
    }

    private async Task ValidateHistoryAsync(StockCount plan, CancellationToken ct)
    {
        var facts = await database.CountFacts.AsNoTracking().Where(row => row.PlanId == plan.Id).OrderBy(row => row.Sequence).ToArrayAsync(ct);
        if (facts.Length != plan.Sequence || facts.Length == 0
            || facts.Where((row, index) => row.Sequence != index + 1).Any()
            || facts[^1].AfterVersion != plan.Version || facts[^1].LinesJson != plan.LinesJson
            || facts[^1].Generation != plan.Generation || facts[^1].State != plan.State)
            throw new InvalidOperationException("The persisted count history is inconsistent.");
    }

    private bool ValidSubject(MasterDataSubject subject) => subject.TenantId == database.TenantId
        && subject.ActorId != Guid.Empty && subject.SessionId != Guid.Empty;
    private static bool ValidVersion(string? value) => Guid.TryParseExact(value, "D", out var id) && id != Guid.Empty;
    private static bool TryQuantity(string? text, out decimal quantity)
    {
        quantity = 0;
        return text is not null && text.Length <= 40 && QuantityPattern.IsMatch(text)
            && decimal.TryParse(text, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out quantity)
            && quantity <= InventoryQuantity.MaximumValue;
    }
    private void Clean(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (database.ChangeTracker.Entries().Any() || database.Database.CurrentTransaction is not null
            || System.Transactions.Transaction.Current is not null)
            throw new InvalidOperationException("Counts require a fresh independent WMS owner.");
    }
    private static string Exact(decimal value) => value.ToString("F6", CultureInfo.InvariantCulture);
}
