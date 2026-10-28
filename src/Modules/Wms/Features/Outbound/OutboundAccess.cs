using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using MySql.Data.MySqlClient;
using Wes.Wms.Domain;
using Wes.Wms.Features.Inbound;
using Wes.Wms.Features.Ledger;
using Wes.Wms.Features.MasterData;
using Wes.Wms.Infrastructure.Persistence;

namespace Wes.Wms.Features.Outbound;

internal sealed class OutboundAccess(WmsDbContext database, IMasterDataAuthorization authorization,
    IWarehouseLocationSchema schema, TimeProvider clock, ILogger<OutboundAccess> logger)
{
    private static readonly string[] ReadFields = ["Id", "Code", "WarehouseId", "State", "Version", "CreatedAt", "Lines"];
    private static readonly string[] WarehouseFields = ["Id", "Code", "Name", "Enabled", "Version", "CreatedAt"];
    private static readonly string[] LocationFields = [.. WarehouseFields, "WarehouseId", "PackId", "SchemaVersion"];
    private static readonly string[] UnitFields = ["Id", "Code", "Name", "Countable", "Precision", "Version", "CreatedAt"];
    private static readonly string[] ItemFields = ["Id", "Code", "Name", "BaseUnitId", "BatchRequired", "Version", "CreatedAt"];
    private static readonly string[] ContainerFields = ["Id", "Code", "Name", "WarehouseId", "Version", "CreatedAt"];
    private static readonly Regex QuantityPattern = new("^(?:0|[1-9][0-9]*)(?:\\.[0-9]{1,6})?$", RegexOptions.CultureInvariant);
    private static readonly Regex VersionPattern = new("^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$", RegexOptions.CultureInvariant);
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.General);

    internal async Task<OutboundResult> CreateAsync(MasterDataSubject subject, OutboundDraftRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(subject);
        ArgumentNullException.ThrowIfNull(request);
        Clean(ct);
        if (!ValidSubject(subject) || request.OperationId == Guid.Empty || request.CorrelationId == Guid.Empty
            || request.WarehouseId == Guid.Empty || request.Lines is null || request.Lines.Count is < 1 or > 100
            || request.Lines.Any(line => line is null || line.Id == Guid.Empty || line.BalanceId == Guid.Empty
                || line.Branch is < 0 or > 1 || !TryQuantity(line.Quantity, out var quantity) || quantity <= 0m
                || quantity > StockMovementQuantity.MaximumValue)
            || request.Lines.Select(line => line.Id).Distinct().Count() != request.Lines.Count
            || request.Lines.Select(line => line.BalanceId).Distinct().Count() != request.Lines.Count
            || !MasterDataValue.IsCode(request.Code))
            return Reject(OutboundDecision.Invalid);
        var input = JsonSerializer.Serialize(new { request.Code, request.WarehouseId, request.Lines });
        try
        {
            var old = await database.OutboundFacts.AsNoTracking().SingleOrDefaultAsync(row => row.OperationId == request.OperationId, ct);
            if (old is not null)
            {
                var existing = await LoadOrderAsync(old.OrderId, ct);
                if (existing is null) return Reject(OutboundDecision.Conflict);
                var dimensions = await LoadDimensionsAsync(existing.Document.Lines, ct);
                if (!await AllowedAsync(subject, existing.Document.CreatorId, "wms.outbound.create",
                        ["Code", "WarehouseId", "Lines"], dimensions, ct))
                    return Reject(OutboundDecision.Forbidden);
                return old.ActorId == subject.ActorId && old.Kind == OutboundFactKind.Created && old.Input == input
                    && old.OrderId == existing.Document.Id
                    ? new(OutboundDecision.Replayed, old.OrderId, old.AfterVersion) : Reject(OutboundDecision.Conflict);
            }

            var requestedBalanceIds = request.Lines.Select(line => line.BalanceId).ToArray();
            var balances = await database.Balances.AsNoTracking()
                .Where(WmsIdentityPredicate.MatchesAnyId<StockBalance>(requestedBalanceIds, row => row.Id)).ToArrayAsync(ct);
            if (balances.Length != request.Lines.Count) return Reject(OutboundDecision.NotFound);
            var dimensionsByBalance = new Dictionary<Guid, Dimension>();
            foreach (var balance in balances)
            {
                var dimension = await LoadDimensionAsync(balance, ct);
                if (dimension is null) return Reject(OutboundDecision.NotFound);
                dimensionsByBalance.Add(balance.Id, dimension);
            }
            var plan = new List<OutboundPlanLine>(request.Lines.Count);
            foreach (var line in request.Lines)
            {
                if (!TryQuantity(line.Quantity, out var quantity)) return Reject(OutboundDecision.Invalid);
                var dimension = dimensionsByBalance[line.BalanceId];
                if (dimension.Warehouse.Id != request.WarehouseId
                    || !dimension.IsLegal
                    || !QuantityPrecision.Create(dimension.Balance.Precision).CanRepresent(quantity))
                    return Reject(OutboundDecision.Invalid);
                plan.Add(new(line.Id, Guid.NewGuid(), line.BalanceId, dimension.Balance.ItemId, dimension.Balance.UnitId,
                    dimension.Balance.Batch, dimension.Balance.Precision, dimension.Balance.ContainerId,
                    dimension.Balance.LocationId, (OutboundBranch)line.Branch, quantity));
            }
            var resources = dimensionsByBalance.Values.ToArray();
            if (!await AllowedAsync(subject, subject.ActorId, "wms.outbound.create", ["Code", "WarehouseId", "Lines"], resources, ct))
                return Reject(OutboundDecision.Forbidden);

            await using var transaction = await database.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
            await WmsResourceLocks.AcquireAsync(database, request.WarehouseId, resources.Select(row => row.Location.Id),
                resources.Select(row => row.Container.Id), ct);
            var operational = await OperationalAsync(resources, ct);
            if (operational is not null) return Reject(operational.Value);
            if (!await AllowedAsync(subject, subject.ActorId, "wms.outbound.create", ["Code", "WarehouseId", "Lines"], resources, ct))
                return Reject(OutboundDecision.Forbidden);

            var document = new OutboundDocument(database.TenantId, request.OperationId, subject.ActorId,
                request.WarehouseId, request.Code, plan, clock.GetUtcNow());
            document.Advance(OutboundOrderState.Draft);
            database.OutboundDocuments.Add(document);
            database.OutboundFacts.Add(new(document, request.OperationId, subject.ActorId, request.CorrelationId,
                OutboundFactKind.Created, input, clock.GetUtcNow()));
            await database.SaveChangesAsync(ct);
            operational = await OperationalAsync(resources, ct);
            if (operational is not null || !await AllowedAsync(subject, subject.ActorId, "wms.outbound.create",
                    ["Code", "WarehouseId", "Lines"], resources, ct))
            {
                await transaction.RollbackAsync(ct);
                database.ChangeTracker.Clear();
                return Reject(operational ?? OutboundDecision.Forbidden);
            }
            await transaction.CommitAsync(ct);
            return new(OutboundDecision.Committed, document.Id, document.Version);
        }
        catch (CountLockConflictException) { return Reject(OutboundDecision.Blocked); }
        catch (DbUpdateException error) when (error.InnerException is MySqlException { Number: 1062 })
        {
            database.ChangeTracker.Clear();
            var old = await FindOperationAsync(request.OperationId, ct);
            if (old is null) return Reject(OutboundDecision.Conflict);
            var existing = await LoadOrderAsync(old.OrderId, ct);
            if (existing is null) return Reject(OutboundDecision.Conflict);
            var resources = await LoadDimensionsAsync(existing.Document.Lines, ct);
            if (!await AllowedAsync(subject, existing.Document.CreatorId, "wms.outbound.create",
                    ["Code", "WarehouseId", "Lines"], resources, ct))
                return Reject(OutboundDecision.Forbidden);
            return Original(old, subject.ActorId, existing.Document.Id, OutboundFactKind.Created, input);
        }
        catch (DbUpdateException error) when (error.InnerException is DbException failure)
        {
            ct.ThrowIfCancellationRequested();
            throw WmsDatabaseFailures.Safe(failure);
        }
        catch (DbException error) { ct.ThrowIfCancellationRequested(); throw WmsDatabaseFailures.Safe(error); }
        catch (DbUpdateConcurrencyException) { return Reject(OutboundDecision.Conflict); }
        catch (JsonException)
        {
            logger.LogError("WMS outbound rejected invalid retained history {Code}", "OutboundHistoryInvalid");
            throw new InvalidOperationException("The persisted outbound history is invalid.");
        }
        finally { database.ChangeTracker.Clear(); }
    }

    internal Task<OutboundResult> ApproveAsync(MasterDataSubject subject, Guid id, OutboundApprovalRequest request,
        Guid operation, Guid correlation, CancellationToken ct) =>
        ExecuteAsync(subject, id, operation, correlation, OutboundFactKind.Approved, "wms.outbound.approve", ["State"],
            request?.ExpectedVersion ?? "", Serialize(request), async (document, facts, dimensions, balances, token) =>
            {
                var lines = document.Lines;
                if (document.State != OutboundOrderState.Draft || request!.Balances is null
                    || request.Balances.Count != lines.Count
                    || request.Balances.Select(row => row.BalanceId).Distinct().Count() != lines.Count
                    || request.Balances.Select(row => row.BalanceId).Except(lines.Select(row => row.BalanceId)).Any()
                    || request.Balances.Any(row => !ValidVersion(row.ExpectedVersion)))
                    return Mutation.Reject(OutboundDecision.Invalid);
                foreach (var line in lines)
                {
                    var balance = balances.SingleOrDefault(row => row.Id == line.BalanceId);
                    var expected = request.Balances.Single(row => row.BalanceId == line.BalanceId).ExpectedVersion;
                    if (balance is null) return Mutation.Reject(OutboundDecision.NotFound);
                    if (balance.Version != expected) return Mutation.Reject(OutboundDecision.Conflict);
                    if (!QuantityPrecision.Create(balance.Precision).CanRepresent(line.PlannedQuantity))
                        return Mutation.Reject(OutboundDecision.Invalid);
                    if (await database.Reservations.AnyAsync(row => row.ExecutionUnitId == line.ExecutionId, token))
                        return Mutation.Reject(OutboundDecision.Conflict);
                    var pending = await InboundPendingProtection.ReadAsync(database, balance.WarehouseId, token);
                    if (pending.Any(row => row.ContainerId == balance.ContainerId || row.ReceiptLocationId == balance.LocationId))
                        return Mutation.Reject(OutboundDecision.Blocked);
                    var reservations = await database.Reservations.AsNoTracking()
                        .Where(row => row.BalanceId == balance.Id && row.RemainingQuantity > 0m).ToArrayAsync(token);
                    var available = StockAvailability.Calculate(InventoryQuantity.Create(balance.Quantity),
                        reservations.Select(row => new StockProtection(row.ExecutionUnitId,
                            InventoryQuantity.Create(row.RemainingQuantity), row.Kind))).Available.Value;
                    if (line.PlannedQuantity > available) return Mutation.Reject(OutboundDecision.Blocked);
                }
                foreach (var line in lines)
                {
                    var balance = balances.Single(row => row.Id == line.BalanceId);
                    var reservation = new StockReservation(database.TenantId, line.ExecutionId, line.BalanceId,
                        document.Id, line.Id, subject.ActorId, line.PlannedQuantity, clock.GetUtcNow());
                    database.Reservations.Add(reservation);
                    database.ReservationChanges.Add(new(database.TenantId, TransitionId(operation, line.Id), subject.ActorId,
                        correlation, ReservationAction.Reserve, reservation, line.PlannedQuantity, 0m, 0, clock.GetUtcNow()));
                    balance.TouchReservationFence();
                }
                return Mutation.Commit(OutboundOrderState.Approved);
            }, ct);

    internal Task<OutboundResult> PickAsync(MasterDataSubject subject, Guid id, OutboundPickRequest request,
        Guid operation, Guid correlation, CancellationToken ct) =>
        ExecuteAsync(subject, id, operation, correlation, OutboundFactKind.Picked, "wms.outbound.pick",
            ["LineId", "PickedQuantity", "ShortageQuantity", "Reason"], request?.ExpectedVersion ?? "", Serialize(request),
            async (document, facts, dimensions, balances, token) =>
            {
                token.ThrowIfCancellationRequested();
                if (document.State is not (OutboundOrderState.Approved or OutboundOrderState.Executing)
                    || request!.LineId == Guid.Empty || !TryQuantity(request.PickedQuantity, out var picked)
                    || !TryQuantity(request.ShortageQuantity, out var shortage) || picked < 0m || shortage < 0m
                    || picked + shortage <= 0m || picked > StockMovementQuantity.MaximumValue
                    || shortage > StockMovementQuantity.MaximumValue
                    || (shortage > 0m && !ValidReason(request.Reason)))
                    return Mutation.Reject(OutboundDecision.Invalid);
                var line = document.Lines.SingleOrDefault(row => row.Id == request.LineId);
                if (line is null) return Mutation.Reject(OutboundDecision.NotFound);
                if (!QuantityPrecision.Create(line.Precision).CanRepresent(picked)
                    || !QuantityPrecision.Create(line.Precision).CanRepresent(shortage))
                    return Mutation.Reject(OutboundDecision.Invalid);
                var prior = ProgressFor(facts, line.Id);
                if (prior.Shipped != 0m || prior.Picked + prior.Shortage + picked + shortage > line.PlannedQuantity)
                    return Mutation.Reject(OutboundDecision.Conflict);
                var reservation = await database.Reservations.AsNoTracking()
                    .SingleOrDefaultAsync(row => row.ExecutionUnitId == line.ExecutionId, token);
                if (reservation is null || reservation.BalanceId != line.BalanceId
                    || reservation.Kind != StockProtectionKind.Reserved)
                    return Mutation.Reject(OutboundDecision.Blocked);
                return Mutation.Commit(OutboundOrderState.Executing);
            }, ct);

    internal Task<OutboundResult> ReleaseShortageAsync(MasterDataSubject subject, Guid id, OutboundShortageReleaseRequest request,
        Guid operation, Guid correlation, CancellationToken ct) =>
        ExecuteAsync(subject, id, operation, correlation, OutboundFactKind.ShortageReleased, "wms.outbound.release",
            ["LineId", "Quantity", "Reason"], request?.ExpectedVersion ?? "", Serialize(request),
            async (document, facts, dimensions, balances, token) =>
            {
                if (document.State is not (OutboundOrderState.Approved or OutboundOrderState.Executing)
                    || request!.LineId == Guid.Empty || !TryQuantity(request.Quantity, out var quantity)
                    || quantity <= 0m || !ValidReason(request.Reason))
                    return Mutation.Reject(OutboundDecision.Invalid);
                var line = document.Lines.SingleOrDefault(row => row.Id == request.LineId);
                if (line is null) return Mutation.Reject(OutboundDecision.NotFound);
                var progress = ProgressFor(facts, line.Id);
                if (progress.Shipped != 0m) return Mutation.Reject(OutboundDecision.Conflict);
                var sourcePickers = facts.Where(row => row.Kind == OutboundFactKind.Picked)
                    .Select(row => (Fact: row, Pick: Deserialize<OutboundPickRequest>(row.Input)))
                    .Where(row => row.Pick.LineId == line.Id && TryQuantity(row.Pick.ShortageQuantity, out var amount) && amount > 0m)
                    .ToArray();
                if (sourcePickers.Length == 0 || sourcePickers.Any(row => row.Fact.ActorId == subject.ActorId))
                    return Mutation.Reject(OutboundDecision.Forbidden);
                if (quantity > progress.Shortage - progress.Released)
                    return Mutation.Reject(OutboundDecision.Conflict);
                var reservation = await database.Reservations.SingleOrDefaultAsync(row => row.ExecutionUnitId == line.ExecutionId, token);
                var balance = balances.Single(row => row.Id == line.BalanceId);
                if (reservation is null || reservation.BalanceId != line.BalanceId
                    || !reservation.TryChange(ReservationAction.ReleaseUnstarted, quantity))
                    return Mutation.Reject(OutboundDecision.Blocked);
                database.ReservationChanges.Add(new(database.TenantId, TransitionId(operation, line.Id), subject.ActorId,
                    correlation, ReservationAction.ReleaseUnstarted, reservation, quantity,
                    reservation.RemainingQuantity + quantity, reservation.Kind, clock.GetUtcNow()));
                balance.TouchReservationFence();
                return Mutation.Commit(document.State);
            }, ct);

    internal Task<OutboundResult> CancelAsync(MasterDataSubject subject, Guid id, OutboundStateRequest request,
        Guid operation, Guid correlation, CancellationToken ct) =>
        ExecuteAsync(subject, id, operation, correlation, OutboundFactKind.Cancelled, "wms.outbound.cancel",
            ["State", "Reason"], request?.ExpectedVersion ?? "", Serialize(request),
            async (document, facts, dimensions, balances, token) =>
            {
                if (document.State is not (OutboundOrderState.Draft or OutboundOrderState.Approved or OutboundOrderState.Executing)
                    || !ValidReason(request!.Reason)) return Mutation.Reject(OutboundDecision.Conflict);
                if (document.Lines.Any(line =>
                    {
                        var progress = ProgressFor(facts, line.Id);
                        return progress.Picked != 0m || progress.Shipped != 0m || progress.Shortage != progress.Released;
                    }))
                    return Mutation.Reject(OutboundDecision.Conflict);
                if (document.State is OutboundOrderState.Approved or OutboundOrderState.Executing)
                {
                    foreach (var line in document.Lines)
                    {
                        var reservation = await database.Reservations.SingleOrDefaultAsync(row => row.ExecutionUnitId == line.ExecutionId, token);
                        var balance = balances.Single(row => row.Id == line.BalanceId);
                        if (reservation is null || reservation.Kind != StockProtectionKind.Reserved)
                            return Mutation.Reject(OutboundDecision.Blocked);
                        var amount = reservation.RemainingQuantity;
                        if (amount > 0m && !reservation.TryChange(ReservationAction.ReleaseUnstarted, amount))
                            return Mutation.Reject(OutboundDecision.Blocked);
                        if (amount > 0m)
                        {
                            database.ReservationChanges.Add(new(database.TenantId, TransitionId(operation, line.Id), subject.ActorId,
                                correlation, ReservationAction.ReleaseUnstarted, reservation, amount, amount, reservation.Kind, clock.GetUtcNow()));
                            balance.TouchReservationFence();
                        }
                    }
                }
                return Mutation.Commit(OutboundOrderState.Cancelled);
            }, ct);

    internal Task<OutboundResult> CloseRemainderAsync(MasterDataSubject subject, Guid id, OutboundStateRequest request,
        Guid operation, Guid correlation, CancellationToken ct) =>
        ExecuteAsync(subject, id, operation, correlation, OutboundFactKind.ClosedPartial, "wms.outbound.close",
            ["State", "Reason"], request?.ExpectedVersion ?? "", Serialize(request),
            async (document, facts, dimensions, balances, token) =>
            {
                if (document.State != OutboundOrderState.Executing || !ValidReason(request!.Reason)
                    || !facts.Any(row => row.Kind == OutboundFactKind.Shipped))
                    return Mutation.Reject(OutboundDecision.Conflict);
                foreach (var line in document.Lines)
                {
                    var progress = ProgressFor(facts, line.Id);
                    if (progress.Shortage != progress.Released || progress.Picked != progress.Shipped)
                        return Mutation.Reject(OutboundDecision.Blocked);
                }
                foreach (var line in document.Lines)
                {
                    var reservation = await database.Reservations.SingleOrDefaultAsync(row => row.ExecutionUnitId == line.ExecutionId, token);
                    var balance = balances.Single(row => row.Id == line.BalanceId);
                    if (reservation is null || reservation.Kind != StockProtectionKind.Reserved)
                        return Mutation.Reject(OutboundDecision.Blocked);
                    var amount = reservation.RemainingQuantity;
                    if (amount > 0m && !reservation.TryChange(ReservationAction.ReleaseUnstarted, amount))
                        return Mutation.Reject(OutboundDecision.Blocked);
                    if (amount > 0m)
                    {
                        database.ReservationChanges.Add(new(database.TenantId, TransitionId(operation, line.Id), subject.ActorId,
                            correlation, ReservationAction.ReleaseUnstarted, reservation, amount, amount, reservation.Kind, clock.GetUtcNow()));
                        balance.TouchReservationFence();
                    }
                }
                return Mutation.Commit(OutboundOrderState.ClosedPartial);
            }, ct);

    internal async Task<OutboundReadResult> ReadAsync(MasterDataSubject subject, Guid id, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(subject);
        Clean(ct);
        if (!ValidSubject(subject) || id == Guid.Empty) return new(OutboundReadDecision.Invalid);
        try
        {
            var creator = await database.OutboundDocuments.AsNoTracking().Where(row => row.Id == id)
                .Select(row => (Guid?)row.CreatorId).SingleOrDefaultAsync(ct);
            if (creator is null) return new(OutboundReadDecision.NotFound);
            if (!await FieldsAsync(subject, "wms.outbound.read", creator.Value, ReadFields, ct))
                return new(OutboundReadDecision.Forbidden);
            OutboundRecord record;
            using (var transaction = await database.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct))
            {
                var loaded = await LoadOrderAsync(id, ct);
                if (loaded is null) return new(OutboundReadDecision.NotFound);
                var dimensions = await LoadDimensionsAsync(loaded.Document.Lines, ct);
                if (!await ResourcesAllowedAsync(subject, dimensions, ct)) return new(OutboundReadDecision.Forbidden);
                var executionIds = loaded.Document.Lines.Select(line => line.ExecutionId).ToArray();
                var reservations = await database.Reservations.AsNoTracking()
                    .Where(WmsIdentityPredicate.MatchesAnyId<StockReservation>(executionIds, row => row.ExecutionUnitId))
                    .ToDictionaryAsync(row => row.ExecutionUnitId, ct);
                record = BuildRecord(loaded.Document, loaded.Facts, reservations);
                await transaction.CommitAsync(ct);
            }
            var reloaded = await database.OutboundDocuments.AsNoTracking().Where(row => row.Id == id)
                .Select(row => row.CreatorId).SingleAsync(ct);
            return await FieldsAsync(subject, "wms.outbound.read", reloaded, ReadFields, ct)
                && await ResourcesAllowedAsync(subject, await LoadDimensionsAsync((await LoadOrderAsync(id, ct))!.Document.Lines, ct), ct)
                ? new(OutboundReadDecision.Success, record) : new(OutboundReadDecision.Forbidden);
        }
        catch (DbException error) { ct.ThrowIfCancellationRequested(); throw WmsDatabaseFailures.Safe(error); }
        catch (JsonException)
        {
            logger.LogError("WMS outbound read rejected invalid history {Code}", "OutboundHistoryInvalid");
            throw new InvalidOperationException("The persisted outbound history is invalid.");
        }
        finally { database.ChangeTracker.Clear(); }
    }

    private async Task<OutboundResult> ExecuteAsync(MasterDataSubject subject, Guid id, Guid operation, Guid correlation,
        OutboundFactKind kind, string permission, string[] fields, string version, string input,
        Func<OutboundDocument, IReadOnlyList<OutboundFact>, IReadOnlyList<Dimension>, IReadOnlyList<StockBalance>,
            CancellationToken, Task<Mutation>> mutate, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(subject);
        Clean(ct);
        if (!ValidSubject(subject) || new[] { id, operation, correlation }.Contains(Guid.Empty)
            || !ValidVersion(version) || string.IsNullOrEmpty(input) || input.Length > OutboundFact.MaximumInputLength)
            return Reject(OutboundDecision.Invalid);
        try
        {
            var loaded = await LoadOrderAsync(id, ct);
            if (loaded is null) return Reject(OutboundDecision.NotFound);
            var dimensions = await LoadDimensionsAsync(loaded.Document.Lines, ct);
            if (!await AllowedAsync(subject, loaded.Document.CreatorId, permission, fields, dimensions, ct))
                return Reject(OutboundDecision.Forbidden);
            var old = await FindOperationAsync(operation, ct);
            if (old is not null) return Original(old, subject.ActorId, id, kind, input);
            var decision = await ValidForNewOperationAsync(loaded.Document, dimensions, ct);
            if (decision is not null) return Reject(decision.Value);
            await using var transaction = await database.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
            await WmsResourceLocks.AcquireAsync(database, loaded.Document.WarehouseId,
                dimensions.Select(row => row.Location.Id), dimensions.Select(row => row.Container.Id), ct);
            await ParentFenceAsync(id, loaded.Document.Version, ct);
            var current = await database.OutboundDocuments.SingleAsync(row => row.Id == id, ct);
            var facts = await database.OutboundFacts.AsNoTracking().Where(row => row.OrderId == id)
                .OrderBy(row => row.Sequence).ToArrayAsync(ct);
            OutboundHistory.Validate(current, facts);
            dimensions = await LoadDimensionsAsync(current.Lines, ct);
            if (!await AllowedAsync(subject, current.CreatorId, permission, fields, dimensions, ct))
                return Reject(OutboundDecision.Forbidden);
            old = await FindOperationAsync(operation, ct);
            if (old is not null) return Original(old, subject.ActorId, id, kind, input);
            if (current.Version != version) return Reject(OutboundDecision.Conflict);
            decision = await ValidForNewOperationAsync(current, dimensions, ct);
            if (decision is not null) return Reject(decision.Value);
            var lineBalanceIds = current.Lines.Select(line => line.BalanceId).ToArray();
            var balances = await database.Balances.Where(WmsIdentityPredicate.MatchesAnyId<StockBalance>(lineBalanceIds, row => row.Id))
                .OrderBy(row => row.Id).ToArrayAsync(ct);
            if (balances.Length != current.Lines.Count) return Reject(OutboundDecision.NotFound);
            var change = await mutate(current, facts, dimensions, balances, ct);
            if (change.Decision is not null) return Reject(change.Decision.Value);
            current.Advance(change.State);
            database.OutboundFacts.Add(new(current, operation, subject.ActorId, correlation, kind, input, clock.GetUtcNow()));
            await database.SaveChangesAsync(ct);
            var postSaveOperational = await OperationalAsync(dimensions, ct);
            var postSaveAllowed = await AllowedAsync(subject, current.CreatorId, permission, fields, dimensions, ct);
            if (!postSaveAllowed || postSaveOperational is not null)
            {
                await transaction.RollbackAsync(ct);
                database.ChangeTracker.Clear();
                return Reject(postSaveOperational ?? OutboundDecision.Forbidden);
            }
            await transaction.CommitAsync(ct);
            return new(OutboundDecision.Committed, id, current.Version);
        }
        catch (CountLockConflictException) { return Reject(OutboundDecision.Blocked); }
        catch (DbUpdateConcurrencyException)
        {
            return await ReplayActionAsync(subject, id, operation, kind, input, permission, fields, ct);
        }
        catch (DbUpdateException error) when (error.InnerException is MySqlException { Number: 1062 })
        {
            return await ReplayActionAsync(subject, id, operation, kind, input, permission, fields, ct);
        }
        catch (DbUpdateException error) when (error.InnerException is DbException failure)
        {
            ct.ThrowIfCancellationRequested();
            throw WmsDatabaseFailures.Safe(failure);
        }
        catch (DbException error) { ct.ThrowIfCancellationRequested(); throw WmsDatabaseFailures.Safe(error); }
        catch (JsonException)
        {
            logger.LogError("WMS outbound rejected invalid retained history {Code}", "OutboundHistoryInvalid");
            throw new InvalidOperationException("The persisted outbound history is invalid.");
        }
        finally { database.ChangeTracker.Clear(); }
    }

    private async Task<OutboundResult> ReplayActionAsync(MasterDataSubject subject, Guid id, Guid operation,
        OutboundFactKind kind, string input, string permission, IReadOnlyCollection<string> fields, CancellationToken ct)
    {
        database.ChangeTracker.Clear();
        var old = await FindOperationAsync(operation, ct);
        if (old is null) return Reject(OutboundDecision.Conflict);
        var order = await LoadOrderAsync(old.OrderId, ct);
        if (order is null || order.Document.Id != id) return Reject(OutboundDecision.Conflict);
        var dimensions = await LoadDimensionsAsync(order.Document.Lines, ct);
        if (!await AllowedAsync(subject, order.Document.CreatorId, permission, fields, dimensions, ct))
            return Reject(OutboundDecision.Forbidden);
        return Original(old, subject.ActorId, id, kind, input);
    }

    private async Task<OutboundOrder?> LoadOrderAsync(Guid id, CancellationToken ct)
    {
        await using var snapshot = database.Database.CurrentTransaction is null
            ? await database.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct) : null;
        var document = await database.OutboundDocuments.AsNoTracking().SingleOrDefaultAsync(row => row.Id == id, ct);
        if (document is null) return null;
        var facts = await database.OutboundFacts.AsNoTracking().Where(row => row.OrderId == id)
            .OrderBy(row => row.Sequence).ToArrayAsync(ct);
        OutboundHistory.Validate(document, facts);
        if (snapshot is not null) await snapshot.CommitAsync(ct);
        return new(document, facts);
    }

    private async Task<Dimension[]> LoadDimensionsAsync(IReadOnlyList<OutboundPlanLine> lines, CancellationToken ct)
    {
        var dimensions = new List<Dimension>(lines.Count);
        foreach (var line in lines)
        {
            var balance = await database.Balances.AsNoTracking().SingleOrDefaultAsync(row => row.Id == line.BalanceId, ct);
            var dimension = balance is null ? null : await LoadDimensionAsync(balance, ct);
            if (dimension is null) throw new InvalidOperationException("An outbound source dimension is missing.");
            dimensions.Add(dimension);
        }
        return dimensions.ToArray();
    }

    private async Task<Dimension?> LoadDimensionAsync(StockBalance balance, CancellationToken ct)
    {
        var registration = await database.DimensionRegistrations.AsNoTracking()
            .SingleOrDefaultAsync(row => row.BalanceId == balance.Id, ct);
        var warehouse = await database.Warehouses.AsNoTracking().SingleOrDefaultAsync(row => row.Id == balance.WarehouseId, ct);
        var location = await database.Locations.AsNoTracking().SingleOrDefaultAsync(row => row.Id == balance.LocationId, ct);
        var item = await database.StockItems.AsNoTracking().SingleOrDefaultAsync(row => row.Id == balance.ItemId, ct);
        var unit = await database.StockUnits.AsNoTracking().SingleOrDefaultAsync(row => row.Id == balance.UnitId, ct);
        var container = await database.StockContainers.AsNoTracking().SingleOrDefaultAsync(row => row.Id == balance.ContainerId, ct);
        if (registration is null || warehouse is null || location is null || item is null || unit is null || container is null)
            return null;
        var warehouseCreator = await database.MasterDataChanges.AsNoTracking()
            .Where(row => row.WarehouseId == warehouse.Id && row.LocationId == null)
            .Select(row => (Guid?)row.ActorId).SingleOrDefaultAsync(ct);
        var locationCreator = await database.MasterDataChanges.AsNoTracking()
            .Where(row => row.LocationId == location.Id).Select(row => (Guid?)row.ActorId).SingleOrDefaultAsync(ct);
        if (warehouseCreator is null || locationCreator is null) return null;
        return new(balance, registration, warehouse, location, item, unit, container, warehouseCreator.Value, locationCreator.Value);
    }

    private async Task<OutboundFact?> FindOperationAsync(Guid operation, CancellationToken ct) =>
        await database.OutboundFacts.AsNoTracking().SingleOrDefaultAsync(row => row.OperationId == operation, ct);

    private async Task<bool> AllowedAsync(MasterDataSubject subject, Guid documentCreator, string actionPermission,
        IReadOnlyCollection<string> actionFields, IReadOnlyList<Dimension> dimensions, CancellationToken ct)
    {
        if (!await FieldsAsync(subject, "wms.outbound.read", documentCreator, ReadFields, ct)
            || !await FieldsAsync(subject, actionPermission, documentCreator, actionFields, ct)
            || !await ResourcesAllowedAsync(subject, dimensions, ct)) return false;
        return true;
    }

    private async Task<bool> ResourcesAllowedAsync(MasterDataSubject subject, IReadOnlyList<Dimension> dimensions, CancellationToken ct)
    {
        foreach (var row in dimensions)
        {
            if (!await FieldsAsync(subject, "wms.warehouses.read", row.WarehouseCreator, WarehouseFields, ct)
                || !await FieldsAsync(subject, "wms.locations.read", row.LocationCreator, LocationFields, ct)
                || !await FieldsAsync(subject, "wms.items.read", row.Item.ActorId, ItemFields, ct)
                || !await FieldsAsync(subject, "wms.units.read", row.Unit.ActorId, UnitFields, ct)
                || !await FieldsAsync(subject, "wms.containers.read", row.Container.ActorId, ContainerFields, ct))
                return false;
        }
        return true;
    }

    private async Task<bool> FieldsAsync(MasterDataSubject subject, string permission, Guid creator,
        IReadOnlyCollection<string> required, CancellationToken ct)
    {
        var fields = await authorization.ReadFieldsAsync(subject, permission, creator, ct);
        ct.ThrowIfCancellationRequested();
        return fields is not null && required.All(fields.Contains);
    }

    private async Task<OutboundDecision?> ValidForNewOperationAsync(OutboundDocument document,
        IReadOnlyList<Dimension> dimensions, CancellationToken ct)
    {
        if (dimensions.Any(row => row.Warehouse.Id != document.WarehouseId || !row.IsLegal))
            return OutboundDecision.Invalid;
        return await OperationalAsync(dimensions, ct);
    }

    private async Task<OutboundDecision?> OperationalAsync(IReadOnlyList<Dimension> dimensions, CancellationToken ct)
    {
        if (dimensions.Any(row => !row.Warehouse.Enabled || !row.Location.Enabled))
            return OutboundDecision.Blocked;
        foreach (var row in dimensions)
        {
            var result = await schema.ValidateAsync(database.TenantId, row.Warehouse.Id, row.Location.PackId,
                row.Location.SchemaVersion, row.Location.Code, ct);
            ct.ThrowIfCancellationRequested();
            if (result == LocationSchemaDecision.Rejected) return OutboundDecision.SchemaRejected;
            if (result == LocationSchemaDecision.Unavailable) return OutboundDecision.SchemaUnavailable;
            if (result != LocationSchemaDecision.Valid) throw new InvalidOperationException("Unknown location schema outcome.");
        }
        foreach (var warehouse in dimensions.Select(row => row.Warehouse.Id).Distinct())
        {
            var pending = await InboundPendingProtection.ReadAsync(database, warehouse, ct);
            if (dimensions.Any(row => row.Warehouse.Id == warehouse
                && pending.Any(receipt => receipt.ContainerId == row.Container.Id || receipt.ReceiptLocationId == row.Location.Id
                    || receipt.TargetLocationId == row.Location.Id)))
                return OutboundDecision.Blocked;
            var unresolved = await (from reservation in database.Reservations
                                    join balance in database.Balances on reservation.BalanceId equals balance.Id
                                    where balance.WarehouseId == warehouse && reservation.RemainingQuantity > 0m
                                    select new { reservation.Kind, balance.ContainerId, balance.LocationId })
                .AsNoTracking().ToArrayAsync(ct);
            if (dimensions.Any(row => row.Warehouse.Id == warehouse && unresolved.Any(protection =>
                    (protection.Kind & (StockProtectionKind.InTransit | StockProtectionKind.Unknown)) != 0
                    && (protection.ContainerId == row.Container.Id || protection.LocationId == row.Location.Id))))
                return OutboundDecision.Blocked;
        }
        return null;
    }

    private async Task ParentFenceAsync(Guid orderId, string expectedVersion, CancellationToken ct)
    {
        await using var command = database.Database.GetDbConnection().CreateCommand();
        command.Transaction = database.Database.CurrentTransaction!.GetDbTransaction();
        command.CommandText = "SELECT Version FROM wms_outbound_documents WHERE TenantId = @tenant AND Id = @id FOR UPDATE";
        var tenant = command.CreateParameter(); tenant.ParameterName = "@tenant";
        tenant.Value = database.TenantId.ToString("D"); command.Parameters.Add(tenant);
        var id = command.CreateParameter(); id.ParameterName = "@id";
        id.Value = orderId.ToString("D"); command.Parameters.Add(id);
        var version = await command.ExecuteScalarAsync(ct);
        if (version is not string current || current != expectedVersion)
            throw new DbUpdateConcurrencyException("Outbound order version changed before the operation.");
    }

    private Progress ProgressFor(IReadOnlyList<OutboundFact> facts, Guid lineId)
    {
        decimal picked = 0m, shortage = 0m, released = 0m, shipped = 0m;
        foreach (var fact in facts)
        {
            switch (fact.Kind)
            {
                case OutboundFactKind.Picked:
                    var pick = Deserialize<OutboundPickRequest>(fact.Input);
                    if (pick.LineId == lineId)
                    {
                        if (!TryQuantity(pick.PickedQuantity, out var pickedPart)
                            || !TryQuantity(pick.ShortageQuantity, out var shortagePart))
                            throw new InvalidOperationException("Outbound pick history has an invalid quantity.");
                        picked += pickedPart; shortage += shortagePart;
                    }
                    break;
                case OutboundFactKind.ShortageReleased:
                    var release = Deserialize<OutboundShortageReleaseRequest>(fact.Input);
                    if (release.LineId == lineId && TryQuantity(release.Quantity, out var releasedPart)) released += releasedPart;
                    break;
                case OutboundFactKind.Shipped:
                    var shipment = Deserialize<OutboundShipmentRequest>(fact.Input);
                    if (shipment.LineId == lineId && TryQuantity(shipment.Quantity, out var shippedPart)) shipped += shippedPart;
                    break;
            }
        }
        return new(picked, shortage, released, shipped);
    }

    private OutboundRecord BuildRecord(OutboundDocument document, IReadOnlyList<OutboundFact> facts,
        IReadOnlyDictionary<Guid, StockReservation> reservations) =>
        new(document.Id, document.Code, document.WarehouseId, (int)document.State, document.Version, document.CreatedAt,
            document.Lines.Select(line =>
            {
                var progress = ProgressFor(facts, line.Id);
                var reserved = reservations.TryGetValue(line.ExecutionId, out var reservation) ? reservation.RemainingQuantity : 0m;
                return new OutboundLineRecord(line.Id, line.ExecutionId, line.BalanceId, line.ItemId, line.UnitId,
                    line.Batch, line.Precision, line.ContainerId, line.LocationId, (int)line.Branch,
                    Amount(line.PlannedQuantity), Amount(reserved), Amount(progress.Picked), Amount(progress.Shortage),
                    Amount(progress.Released), Amount(progress.Shipped));
            }).ToArray());

    private OutboundResult Original(OutboundFact fact, Guid actor, Guid orderId, OutboundFactKind kind, string input) =>
        fact.ActorId == actor && fact.OrderId == orderId && fact.Kind == kind && fact.Input == input
            ? new(OutboundDecision.Replayed, orderId, fact.AfterVersion) : Reject(OutboundDecision.Conflict);

    private bool ValidSubject(MasterDataSubject subject) => subject.TenantId == database.TenantId
        && subject.TenantId != Guid.Empty && subject.ActorId != Guid.Empty && subject.SessionId != Guid.Empty;

    private static bool ValidVersion(string? value) => value is not null && VersionPattern.IsMatch(value)
        && Guid.TryParseExact(value, "D", out var id) && id != Guid.Empty;

    private static bool TryQuantity(string? text, out decimal quantity)
    {
        quantity = 0m;
        return text is not null && QuantityPattern.IsMatch(text)
            && decimal.TryParse(text, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out quantity)
            && quantity <= InventoryQuantity.MaximumValue;
    }

    private static bool ValidReason(string? reason) => reason is { Length: > 0 and <= 256 }
        && reason == reason.Trim() && !reason.Any(char.IsControl);

    private string Serialize<T>(T value) => JsonSerializer.Serialize(value, _json);
    private T Deserialize<T>(string value) => JsonSerializer.Deserialize<T>(value, _json)
        ?? throw new InvalidOperationException("Outbound operation input is missing.");
    private static string Amount(decimal value) => value.ToString("F6", CultureInfo.InvariantCulture);

    private static Guid TransitionId(Guid operation, Guid line)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"{operation:D}:{line:D}"));
        return new Guid(bytes.AsSpan(0, 16));
    }

    private OutboundResult Reject(OutboundDecision decision)
    {
        logger.LogWarning("WMS outbound operation rejected {Code}", decision.ToString());
        return new(decision);
    }

    private void Clean(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (database.ChangeTracker.Entries().Any() || database.Database.CurrentTransaction is not null
            || System.Transactions.Transaction.Current is not null)
            throw new InvalidOperationException("Outbound operations require an independent clean WMS owner.");
    }

    private sealed record OutboundOrder(OutboundDocument Document, OutboundFact[] Facts);
    private sealed record Progress(decimal Picked, decimal Shortage, decimal Released, decimal Shipped);
    private sealed record Mutation(OutboundDecision? Decision, OutboundOrderState State)
    {
        internal static Mutation Reject(OutboundDecision decision) => new(decision, default);
        internal static Mutation Commit(OutboundOrderState state) => new(null, state);
    }
    private sealed record Dimension(StockBalance Balance, StockDimensionRegistration Registration, Warehouse Warehouse,
        WarehouseLocation Location, StockItem Item, StockUnit Unit, StockContainer Container, Guid WarehouseCreator, Guid LocationCreator)
    {
        internal bool IsLegal => Registration.BalanceId == Balance.Id && Registration.WarehouseId == Balance.WarehouseId
            && Registration.LocationId == Balance.LocationId && Registration.ItemId == Balance.ItemId
            && Registration.UnitId == Balance.UnitId && Registration.ContainerId == Balance.ContainerId
            && Registration.Batch == Balance.Batch && Registration.Precision == Balance.Precision
            && Location.WarehouseId == Balance.WarehouseId && Container.WarehouseId == Balance.WarehouseId
            && Item.BaseUnitId == Balance.UnitId && Unit.Precision == Balance.Precision
            && Item.BatchRequired == (Balance.Batch.Length > 0);
    }

    internal async Task<OutboundResult> ShipAsync(MasterDataSubject subject, Guid id, OutboundShipmentRequest request,
        Guid operation, Guid correlation, CancellationToken ct)
    {
        Clean(ct);
        if (!ValidSubject(subject) || new[] { id, operation, correlation, request.LineId }.Contains(Guid.Empty)
            || !ValidVersion(request.ExpectedVersion) || !TryQuantity(request.Quantity, out var quantity) || quantity <= 0m)
            return Reject(OutboundDecision.Invalid);
        var input = Serialize(request);
        try
        {
            var loaded = await LoadOrderAsync(id, ct);
            if (loaded is null) return Reject(OutboundDecision.NotFound);
            var dimensions = await LoadDimensionsAsync(loaded.Document.Lines, ct);
            if (!await AllowedAsync(subject, loaded.Document.CreatorId, "wms.outbound.ship", ["LineId", "Quantity"], dimensions, ct))
                return Reject(OutboundDecision.Forbidden);
            var old = await FindOperationAsync(operation, ct);
            if (old is not null) return Original(old, subject.ActorId, id, OutboundFactKind.Shipped, input);
            var line = loaded.Document.Lines.SingleOrDefault(row => row.Id == request.LineId);
            if (line is null) return Reject(OutboundDecision.NotFound);
            if (line.Branch != OutboundBranch.DirectShipment) return Reject(OutboundDecision.Unsupported);
            var progress = ProgressFor(loaded.Facts, line.Id);
            if (loaded.Document.State is not (OutboundOrderState.Approved or OutboundOrderState.Executing)
                || progress.Shipped != 0m || quantity != progress.Picked || progress.Picked <= 0m
                || progress.Shortage != progress.Released || !QuantityPrecision.Create(line.Precision).CanRepresent(quantity))
                return Reject(OutboundDecision.Conflict);
            var balance = await database.Balances.AsNoTracking().SingleOrDefaultAsync(row => row.Id == line.BalanceId, ct);
            if (balance is null) return Reject(OutboundDecision.NotFound);
            var command = new StockPostingCommand(operation, subject.ActorId, correlation, StockPostingKind.Shipment,
                id, line.Id, line.ExecutionId, line.BalanceId, null, quantity, balance.Version, null);
            var rejected = OutboundDecision.Blocked;
            var stage = async (CancellationToken token) =>
            {
                var document = await database.OutboundDocuments.SingleAsync(row => row.Id == id, token);
                var facts = await database.OutboundFacts.AsNoTracking().Where(row => row.OrderId == id)
                    .OrderBy(row => row.Sequence).ToArrayAsync(token);
                OutboundHistory.Validate(document, facts);
                var fresh = document.Lines.Single(row => row.Id == line.Id);
                if (document.Version != request.ExpectedVersion || document.State is not (OutboundOrderState.Approved or OutboundOrderState.Executing)
                    || fresh.Branch != OutboundBranch.DirectShipment)
                    throw new DbUpdateConcurrencyException();
                var current = ProgressFor(facts, line.Id);
                if (current.Shipped != 0m || current.Picked != quantity || current.Shortage != current.Released)
                    throw new DbUpdateConcurrencyException();
                var completes = document.Lines.All(candidate =>
                {
                    var state = ProgressFor(facts, candidate.Id);
                    return state.Shipped + (candidate.Id == line.Id ? quantity : 0m) == candidate.PlannedQuantity;
                });
                document.Advance(completes ? OutboundOrderState.Completed : OutboundOrderState.Executing);
                database.OutboundFacts.Add(new(document, operation, subject.ActorId, correlation, OutboundFactKind.Shipped, input, clock.GetUtcNow()));
            };
            var posting = new PostStock(database, clock);
            var result = await posting.ExecuteComposedAsync(command, stage, ct,
                async token =>
                {
                    await WmsResourceLocks.AcquireAsync(database, loaded.Document.WarehouseId,
                        dimensions.Select(row => row.Location.Id), dimensions.Select(row => row.Container.Id), token);
                    await ParentFenceAsync(id, request.ExpectedVersion, token);
                    var fresh = await LoadOrderAsync(id, token);
                    if (fresh is null) { rejected = OutboundDecision.NotFound; return false; }
                    dimensions = await LoadDimensionsAsync(fresh.Document.Lines, token);
                    if (!await AllowedAsync(subject, fresh.Document.CreatorId, "wms.outbound.ship", ["LineId", "Quantity"], dimensions, token))
                    { rejected = OutboundDecision.Forbidden; return false; }
                    var operational = await ValidForNewOperationAsync(fresh.Document, dimensions, token);
                    if (operational is not null) { rejected = operational.Value; return false; }
                    var reservation = await database.Reservations.AsNoTracking()
                        .SingleOrDefaultAsync(row => row.ExecutionUnitId == line.ExecutionId, token);
                    if (reservation is null || reservation.BalanceId != line.BalanceId
                        || reservation.Kind != StockProtectionKind.Reserved)
                    { rejected = OutboundDecision.Blocked; return false; }
                    var replay = await FindOperationAsync(operation, token);
                    if (replay is not null) { rejected = Original(replay, subject.ActorId, id, OutboundFactKind.Shipped, input).Decision; return false; }
                    var current = ProgressFor(fresh.Facts, line.Id);
                    if (current.Shipped != 0m || current.Picked != quantity || current.Shortage != current.Released
                        || fresh.Document.State is not (OutboundOrderState.Approved or OutboundOrderState.Executing)
                        || line.Branch != OutboundBranch.DirectShipment)
                    { rejected = OutboundDecision.Conflict; return false; }
                    return true;
                },
                async token =>
                {
                    var current = await LoadOrderAsync(id, token);
                    if (current is null) { rejected = OutboundDecision.NotFound; return false; }
                    dimensions = await LoadDimensionsAsync(current.Document.Lines, token);
                    if (!await AllowedAsync(subject, current.Document.CreatorId,
                            "wms.outbound.ship", ["LineId", "Quantity"], dimensions, token))
                    { rejected = OutboundDecision.Forbidden; return false; }
                    var result = await OperationalAsync(dimensions, token);
                    if (result is not null) { rejected = result.Value; return false; }
                    return true;
                });
            if (result.Decision == StockPostingDecision.Posted)
            {
                var receipt = await database.OutboundFacts.AsNoTracking().SingleAsync(row => row.OperationId == operation, ct);
                return new(OutboundDecision.Committed, id, receipt.AfterVersion);
            }
            if (result.Decision == StockPostingDecision.Replayed)
            {
                database.ChangeTracker.Clear();
                return await ReplayActionAsync(subject, id, operation, OutboundFactKind.Shipped, input,
                    "wms.outbound.ship", ["LineId", "Quantity"], ct);
            }
            if (result.Decision == StockPostingDecision.SourceRejected && rejected == OutboundDecision.Replayed)
                return await ReplayActionAsync(subject, id, operation, OutboundFactKind.Shipped, input,
                    "wms.outbound.ship", ["LineId", "Quantity"], ct);
            if (result.Decision == StockPostingDecision.VersionConflict)
                return await ReplayActionAsync(subject, id, operation, OutboundFactKind.Shipped, input,
                    "wms.outbound.ship", ["LineId", "Quantity"], ct);
            return Reject(result.Decision switch
            {
                StockPostingDecision.SourceRejected => rejected,
                StockPostingDecision.Invalid => OutboundDecision.Invalid,
                StockPostingDecision.NotFound => OutboundDecision.NotFound,
                StockPostingDecision.VersionConflict or StockPostingDecision.IdempotencyConflict => OutboundDecision.Conflict,
                StockPostingDecision.InsufficientStock or StockPostingDecision.ProtectedStock => OutboundDecision.Blocked,
                StockPostingDecision.DimensionMismatch => OutboundDecision.Invalid,
                _ => OutboundDecision.Conflict
            });
        }
        catch (DbUpdateConcurrencyException)
        {
            return await ReplayActionAsync(subject, id, operation, OutboundFactKind.Shipped, input,
                "wms.outbound.ship", ["LineId", "Quantity"], ct);
        }
        catch (DbException error) { ct.ThrowIfCancellationRequested(); throw WmsDatabaseFailures.Safe(error); }
        finally { database.ChangeTracker.Clear(); }
    }
}
