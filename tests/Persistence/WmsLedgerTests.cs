using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Wes.Wms.Domain;
using Wes.Wms.Features.Ledger;
using Wes.Wms.Features.Reservations;
using Wes.Wms.Infrastructure.Persistence;
using Xunit;

namespace Wes.Persistence.Tests;

[Collection("PlatformPersistence")]
[Trait("Provider", "MySQL")]
public sealed class WmsLedgerTests : IAsyncLifetime
{
    private readonly Guid _tenant = Guid.NewGuid();
    private readonly Guid _other = Guid.NewGuid();
    private readonly Guid _actor = Guid.NewGuid();
    private readonly Guid _document = Guid.NewGuid();
    private readonly Guid _line = Guid.NewGuid();
    private readonly Clock _clock = new();
    private string _connection = "";
    private Guid _source;
    private Guid _target;
    private bool _ready;

    public async Task InitializeAsync()
    {
        _connection = MySqlTestConnection.LoadAuthorizedTestConnection();
        await using var database = Open(_tenant);
        await database.Database.MigrateAsync();
        _ready = true;
        var source = new StockBalance(_tenant, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "",
            Guid.NewGuid(), Guid.NewGuid(), 6);
        var target = new StockBalance(_tenant, source.WarehouseId, Guid.NewGuid(), source.ItemId, "",
            source.ContainerId, source.UnitId, 6);
        database.Balances.AddRange(source, target);
        await database.SaveChangesAsync();
        _source = source.Id;
        _target = target.Id;
    }

    [Fact]
    public async Task ReceiptAndShipmentAreExplainedByExactImmutableEntries()
    {
        var receipt = await CommandAsync(StockPostingKind.Receipt, null, _source, 10.125001m);
        Assert.Equal(StockPostingDecision.Posted, (await ExecuteAsync(receipt)).Decision);
        var shipment = await CommandAsync(StockPostingKind.Shipment, _source, null, 3.001001m);
        Assert.Equal(StockPostingDecision.Posted, (await ExecuteAsync(shipment)).Decision);
        await using var verify = Open(_tenant);
        Assert.Equal(7.124000m, (await verify.Balances.SingleAsync(item => item.Id == _source)).Quantity);
        var entries = await verify.Entries.OrderBy(item => item.Before).ToListAsync();
        Assert.Equal(2, entries.Count);
        Assert.Equal((0m, 10.125001m, 10.125001m), (entries[0].Before, entries[0].After, entries[0].Quantity));
        Assert.Equal((10.125001m, 7.124000m, -3.001001m), (entries[1].Before, entries[1].After, entries[1].Quantity));
        Assert.All(entries, item => Assert.Equal(_clock.GetUtcNow(), item.OccurredAt));
    }

    [Fact]
    public async Task TransferCommitsOnePostingTwoEntriesAndBothBalances()
    {
        await ReceiveAsync(10m);
        var transfer = await CommandAsync(StockPostingKind.Transfer, _source, _target, 7m);
        var result = await ExecuteAsync(transfer);
        Assert.Equal(StockPostingDecision.Posted, result.Decision);
        await using var verify = Open(_tenant);
        Assert.Equal(3m, (await verify.Balances.SingleAsync(item => item.Id == _source)).Quantity);
        Assert.Equal(7m, (await verify.Balances.SingleAsync(item => item.Id == _target)).Quantity);
        var pair = await verify.Entries.Where(item => item.PostingId == result.PostingId).ToListAsync();
        Assert.Equal(2, pair.Count);
        Assert.Equal(0m, pair.Sum(item => item.Quantity));
        Assert.Equal(new[] { -7m, 7m }, pair.Select(item => item.Quantity).Order().ToArray());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OperationAndExecutionIdentitySurviveOldVersionsWithoutDoublePosting(bool newOperation)
    {
        var command = await CommandAsync(StockPostingKind.Receipt, null, _source, 2m);
        var initial = await ExecuteAsync(command);
        var repeated = await ExecuteAsync(newOperation ? command with { OperationId = Guid.NewGuid() } : command);
        Assert.Equal(StockPostingDecision.Replayed, repeated.Decision);
        Assert.Equal(initial.PostingId, repeated.PostingId);
        await using var verify = Open(_tenant);
        Assert.Equal(2m, (await verify.Balances.SingleAsync(item => item.Id == _source)).Quantity);
        Assert.Single(await verify.Postings.ToListAsync());
        Assert.Single(await verify.Entries.ToListAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ChangedSemanticPayloadCannotReuseOperationOrExecutionIdentity(bool newOperation)
    {
        var command = await CommandAsync(StockPostingKind.Receipt, null, _source, 2m);
        await ExecuteAsync(command);
        var changed = command with { Quantity = 3m, OperationId = newOperation ? Guid.NewGuid() : command.OperationId };
        Assert.Equal(StockPostingDecision.IdempotencyConflict, (await ExecuteAsync(changed)).Decision);
        await using var verify = Open(_tenant);
        Assert.Equal(2m, (await verify.Balances.SingleAsync(item => item.Id == _source)).Quantity);
        Assert.Single(await verify.Entries.ToListAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EitherStaleTransferVersionRejectsWithoutHalfPosting(bool staleTarget)
    {
        await ReceiveAsync(10m);
        var transfer = await CommandAsync(StockPostingKind.Transfer, _source, _target, 7m);
        await ExecuteAsync(await CommandAsync(StockPostingKind.Receipt, null, staleTarget ? _target : _source, 1m));
        Assert.Equal(StockPostingDecision.VersionConflict, (await ExecuteAsync(transfer)).Decision);
        await using var verify = Open(_tenant);
        Assert.Equal(staleTarget ? 10m : 11m, (await verify.Balances.SingleAsync(item => item.Id == _source)).Quantity);
        Assert.Equal(staleTarget ? 1m : 0m, (await verify.Balances.SingleAsync(item => item.Id == _target)).Quantity);
        Assert.DoesNotContain(await verify.Postings.ToListAsync(), item => item.Kind == StockPostingKind.Transfer);
    }

    [Fact]
    public async Task InsufficientSourceAndMismatchedDimensionLeaveNoPosting()
    {
        var command = await CommandAsync(StockPostingKind.Transfer, _source, _target, 1m);
        Assert.Equal(StockPostingDecision.InsufficientStock, (await ExecuteAsync(command)).Decision);
        await ReceiveAsync(10m);
        await using (var seed = Open(_tenant))
        {
            var source = await seed.Balances.SingleAsync(item => item.Id == _source);
            var foreignItem = new StockBalance(_tenant, source.WarehouseId, Guid.NewGuid(), Guid.NewGuid(), "",
                source.ContainerId, source.UnitId, 6);
            seed.Balances.Add(foreignItem);
            await seed.SaveChangesAsync();
            command = await CommandAsync(StockPostingKind.Transfer, _source, foreignItem.Id, 1m);
        }
        Assert.Equal(StockPostingDecision.DimensionMismatch, (await ExecuteAsync(command)).Decision);
        await using var verify = Open(_tenant);
        Assert.Single(await verify.Postings.ToListAsync());
    }

    [Fact]
    public async Task EmptyBatchDimensionsAreUniqueButSameIdentityInAnotherTenantIsIndependent()
    {
        await using var database = Open(_tenant);
        var original = await database.Balances.AsNoTracking().SingleAsync(item => item.Id == _source);
        var duplicate = new StockBalance(_tenant, original.WarehouseId, original.LocationId, original.ItemId, "",
            original.ContainerId, original.UnitId, original.Precision);
        database.Balances.Add(duplicate);
        await Assert.ThrowsAsync<DbUpdateException>(() => database.SaveChangesAsync());
        await using var other = Open(_other);
        other.Balances.Add(new(_other, original.WarehouseId, original.LocationId, original.ItemId, "",
            original.ContainerId, original.UnitId, original.Precision));
        await other.SaveChangesAsync();
        Assert.Empty(await other.Postings.ToListAsync());
        Assert.Single(await other.Balances.ToListAsync());
    }

    [Fact]
    public async Task CrossTenantReadsAndWritesCannotReachAnotherBalance()
    {
        var command = await CommandAsync(StockPostingKind.Receipt, null, _source, 1m);
        await using var other = Open(_other);
        Assert.Empty(await other.Balances.ToListAsync());
        Assert.Equal(StockPostingDecision.NotFound,
            (await new PostStock(other, _clock).ExecuteAsync(command)).Decision);
        other.Balances.Add(new(_tenant, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "", Guid.NewGuid(), Guid.NewGuid(), 0));
        await Assert.ThrowsAsync<InvalidOperationException>(() => other.SaveChangesAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailureOrCancellationAfterSaveRollsBackBothTransferLegsAndPosting(bool cancel)
    {
        await ReceiveAsync(10m);
        var command = await CommandAsync(StockPostingKind.Transfer, _source, _target, 7m);
        using var cancellation = new CancellationTokenSource();
        await using (var broken = Open(_tenant, new AfterSaveFault(cancel, cancellation)))
        {
            if (cancel)
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                    new PostStock(broken, _clock).ExecuteAsync(command, cancellation.Token));
            else
                await Assert.ThrowsAsync<InvalidOperationException>(() => new PostStock(broken, _clock).ExecuteAsync(command));
        }
        await using var verify = Open(_tenant);
        Assert.Equal(10m, (await verify.Balances.SingleAsync(item => item.Id == _source)).Quantity);
        Assert.Equal(0m, (await verify.Balances.SingleAsync(item => item.Id == _target)).Quantity);
        Assert.Single(await verify.Postings.ToListAsync());
        Assert.Single(await verify.Entries.ToListAsync());
        Assert.Equal(StockPostingDecision.Posted, (await ExecuteAsync(command)).Decision);
    }

    [Fact]
    public async Task HistoricalPostingAndEntryCannotBeEditedOrDeleted()
    {
        await ReceiveAsync(1m);
        await using (var database = Open(_tenant))
        {
            database.Postings.Remove(await database.Postings.SingleAsync());
            await Assert.ThrowsAsync<InvalidOperationException>(() => database.SaveChangesAsync());
        }
        await using (var database = Open(_tenant))
        {
            var entry = await database.Entries.SingleAsync();
            database.Entry(entry).Property(item => item.After).CurrentValue = 99m;
            await Assert.ThrowsAsync<InvalidOperationException>(() => database.SaveChangesAsync());
        }
        await using (var database = Open(_tenant))
        {
            var balance = await database.Balances.SingleAsync(item => item.Id == _source);
            database.Entry(balance).Property(item => item.LocationId).CurrentValue = Guid.NewGuid();
            await Assert.ThrowsAsync<InvalidOperationException>(() => database.SaveChangesAsync());
        }
    }

    [Fact]
    public async Task RealConcurrentReceiptCommandsCannotBothUseOneBalanceVersion()
    {
        var first = await CommandAsync(StockPostingKind.Receipt, null, _source, 2m);
        var second = await CommandAsync(StockPostingKind.Receipt, null, _source, 3m);
        var results = await Task.WhenAll(ExecuteAsync(first), ExecuteAsync(second));
        Assert.Single(results, item => item.Decision == StockPostingDecision.Posted);
        Assert.Single(results, item => item.Decision == StockPostingDecision.VersionConflict);
        await using var verify = Open(_tenant);
        Assert.Single(await verify.Postings.ToListAsync());
        Assert.Single(await verify.Entries.ToListAsync());
        Assert.Contains((await verify.Balances.SingleAsync(item => item.Id == _source)).Quantity, new[] { 2m, 3m });
    }

    [Fact]
    public async Task ExactBatchIdentitiesDoNotCollapseUnderDatabaseDefaultCollation()
    {
        await using var database = Open(_tenant);
        var original = await database.Balances.SingleAsync(item => item.Id == _source);
        foreach (var batch in new[] { "LOT-A", "lot-a", "LÓT-A" })
            database.Balances.Add(new(_tenant, original.WarehouseId, original.LocationId, original.ItemId,
                batch, original.ContainerId, original.UnitId, 6));
        await database.SaveChangesAsync();
        Assert.Single(await database.Balances.Where(item => item.Batch == "LOT-A").ToListAsync());
        Assert.Single(await database.Balances.Where(item => item.Batch == "lot-a").ToListAsync());
        Assert.Single(await database.Balances.Where(item => item.Batch == "LÓT-A").ToListAsync());
    }

    [Fact]
    public async Task UnexplainedEntryCannotBeAppendedToHistoricalPosting()
    {
        await ReceiveAsync(2m);
        await using var database = Open(_tenant);
        await using var transaction = await database.Database.BeginTransactionAsync();
        var posting = await database.Postings.SingleAsync();
        var target = await database.Balances.SingleAsync(item => item.Id == _target);
        database.Entries.Add(new StockEntry(posting, target, 0m, 2m));
        await Assert.ThrowsAsync<InvalidOperationException>(() => database.SaveChangesAsync());
    }

    [Fact]
    public async Task PostingIdentityCannotBeStoredWithoutItsLedgerEffects()
    {
        await using var database = Open(_tenant);
        await using var transaction = await database.Database.BeginTransactionAsync();
        database.Postings.Add(new StockPosting(_tenant, Guid.NewGuid(), _actor, Guid.NewGuid(),
            StockPostingKind.Receipt, _document, _line, Guid.NewGuid(), null, _source, 1m, _clock.GetUtcNow()));
        await Assert.ThrowsAsync<InvalidOperationException>(() => database.SaveChangesAsync());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(1000000000)]
    public async Task InvalidMovementCannotCreateAnIdentityOrChangeBalance(decimal amount)
    {
        var command = await CommandAsync(StockPostingKind.Receipt, null, _source, amount);
        Assert.Equal(StockPostingDecision.Invalid, (await ExecuteAsync(command)).Decision);
        await using var database = Open(_tenant);
        Assert.Empty(await database.Postings.ToListAsync());
        Assert.Equal(0m, (await database.Balances.SingleAsync(item => item.Id == _source)).Quantity);
    }

    [Fact]
    public async Task MissingVersionAndCancelledCallDoNotPost()
    {
        var command = await CommandAsync(StockPostingKind.Receipt, null, _source, 1m);
        Assert.Equal(StockPostingDecision.VersionRequired,
            (await ExecuteAsync(command with { ExpectedTargetVersion = null })).Decision);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await using var database = Open(_tenant);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new PostStock(database, _clock).ExecuteAsync(command, cancellation.Token));
        Assert.Empty(await database.Postings.ToListAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DatabaseQueryFaultIsSanitizedAndNeverReturnsSuccess(bool transient)
    {
        var command = await CommandAsync(StockPostingKind.Receipt, null, _source, 1m);
        var fault = new QueryFault(transient);
        await using var database = Open(_tenant, fault);
        var error = await Assert.ThrowsAsync<WmsDatabaseException>(() =>
            new PostStock(database, _clock).ExecuteAsync(command));
        Assert.True(fault.Reached);
        Assert.Equal(transient, error.IsTransient);
        Assert.Null(error.InnerException);
        Assert.DoesNotContain("sensitive-sentinel", error.ToString());
    }

    [Fact]
    public async Task ConcurrentDuplicateOperationCreatesExactlyOneEffect()
    {
        var command = await CommandAsync(StockPostingKind.Receipt, null, _source, 2m);
        var results = await Task.WhenAll(ExecuteAsync(command), ExecuteAsync(command));
        Assert.Single(results, item => item.Decision == StockPostingDecision.Posted);
        Assert.Single(results, item => item.Decision == StockPostingDecision.Replayed);
        Assert.Equal(results[0].PostingId, results[1].PostingId);
        await using var verify = Open(_tenant);
        Assert.Equal(2m, (await verify.Balances.SingleAsync(item => item.Id == _source)).Quantity);
        Assert.Single(await verify.Entries.ToListAsync());
    }

    [Fact]
    public async Task DurableProtectionUnionLeavesThreeAvailableAndBlocksUnrelatedShipment()
    {
        await ReceiveAsync(10m);
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        Assert.Equal(ReservationDecision.Changed, (await ReserveAsync(first, 5m)).Decision);
        Assert.Equal(ReservationDecision.Changed, (await ReserveAsync(second, 2m)).Decision);
        Assert.Equal(ReservationDecision.Changed,
            (await ChangeReservationAsync(first, ReservationAction.MarkInTransit, 0m)).Decision);
        await using (var verify = Open(_tenant))
        {
            var balance = await verify.Balances.SingleAsync(item => item.Id == _source);
            var protections = await verify.Reservations.ToListAsync();
            var projection = StockAvailability.Calculate(InventoryQuantity.Create(balance.Quantity),
                protections.Where(item => item.RemainingQuantity > 0m).Select(item =>
                    new StockProtection(item.ExecutionUnitId, InventoryQuantity.Create(item.RemainingQuantity), item.Kind)));
            Assert.Equal(7m, projection.Reserved.Value);
            Assert.Equal(5m, projection.InTransit.Value);
            Assert.Equal(3m, projection.Available.Value);
        }
        Assert.Equal(StockPostingDecision.InsufficientStock,
            (await ExecuteAsync(await CommandAsync(StockPostingKind.Shipment, _source, null, 4m))).Decision);
        Assert.Equal(StockPostingDecision.Posted,
            (await ExecuteAsync(await CommandAsync(StockPostingKind.Shipment, _source, null, 3m))).Decision);
    }

    [Fact]
    public async Task PartialReleaseIsPermanentOperationIdempotentAndInTransitCannotRelease()
    {
        await ReceiveAsync(10m);
        var execution = Guid.NewGuid();
        await ReserveAsync(execution, 7m);
        var release = await ReservationCommandAsync(execution, ReservationAction.ReleaseUnstarted, 2m);
        Assert.Equal(ReservationDecision.Changed, (await ExecuteReservationAsync(release)).Decision);
        Assert.Equal(ReservationDecision.Replayed, (await ExecuteReservationAsync(release)).Decision);
        await ChangeReservationAsync(execution, ReservationAction.MarkInTransit, 0m);
        Assert.Equal(ReservationDecision.StateConflict,
            (await ChangeReservationAsync(execution, ReservationAction.ReleaseUnstarted, 1m)).Decision);
        await using var verify = Open(_tenant);
        Assert.Equal(5m, (await verify.Reservations.SingleAsync()).RemainingQuantity);
        Assert.Equal(10m, (await verify.Balances.SingleAsync(item => item.Id == _source)).Quantity);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MatchingReservationIsConsumedExactlyOnceWithLedgerButUnknownIsBlocked(bool unknown)
    {
        await ReceiveAsync(10m);
        var execution = Guid.NewGuid();
        await ReserveAsync(execution, 7m);
        await ChangeReservationAsync(execution, unknown ? ReservationAction.MarkUnknown : ReservationAction.MarkInTransit, 0m);
        var command = (await CommandAsync(StockPostingKind.Shipment, _source, null, 5m)) with { ExecutionUnitId = execution };
        var result = await ExecuteAsync(command);
        Assert.Equal(unknown ? StockPostingDecision.ProtectedStock : StockPostingDecision.Posted, result.Decision);
        await using var verify = Open(_tenant);
        Assert.Equal(unknown ? 10m : 5m, (await verify.Balances.SingleAsync(item => item.Id == _source)).Quantity);
        Assert.Equal(unknown ? 7m : 2m, (await verify.Reservations.SingleAsync()).RemainingQuantity);
        if (!unknown)
            Assert.Equal(StockPostingDecision.Replayed, (await ExecuteAsync(command)).Decision);
        else
            Assert.Equal(ReservationDecision.StateConflict,
                (await ChangeReservationAsync(execution, ReservationAction.ReleaseUnstarted, 7m)).Decision);
    }

    [Fact]
    public async Task ReservationConsumptionRollsBackWithFailedLedgerCommit()
    {
        await ReceiveAsync(10m);
        var execution = Guid.NewGuid();
        await ReserveAsync(execution, 7m);
        var command = (await CommandAsync(StockPostingKind.Transfer, _source, _target, 5m)) with { ExecutionUnitId = execution };
        using var cancellation = new CancellationTokenSource();
        await using (var broken = Open(_tenant, new AfterSaveFault(false, cancellation)))
            await Assert.ThrowsAsync<InvalidOperationException>(() => new PostStock(broken, _clock).ExecuteAsync(command));
        await using var verify = Open(_tenant);
        Assert.Equal(7m, (await verify.Reservations.SingleAsync()).RemainingQuantity);
        Assert.Equal(10m, (await verify.Balances.SingleAsync(item => item.Id == _source)).Quantity);
        Assert.Equal(0m, (await verify.Balances.SingleAsync(item => item.Id == _target)).Quantity);
    }

    [Fact]
    public async Task ConcurrentReservationsCannotBothAllocateSevenFromTen()
    {
        await ReceiveAsync(10m);
        var first = await ReservationCommandAsync(Guid.NewGuid(), ReservationAction.Reserve, 7m);
        var second = await ReservationCommandAsync(Guid.NewGuid(), ReservationAction.Reserve, 7m);
        var results = await Task.WhenAll(ExecuteReservationAsync(first), ExecuteReservationAsync(second));
        Assert.Single(results, item => item.Decision == ReservationDecision.Changed);
        Assert.Single(results, item => item.Decision == ReservationDecision.VersionConflict);
        await using var verify = Open(_tenant);
        Assert.Equal(7m, await verify.Reservations.SumAsync(item => item.RemainingQuantity));
    }

    [Fact]
    public async Task ExecutionReservedOnAnotherBalanceCannotMasqueradeAsUnreservedShipment()
    {
        await ReceiveAsync(10m);
        await ExecuteAsync(await CommandAsync(StockPostingKind.Receipt, null, _target, 5m));
        var execution = Guid.NewGuid();
        await ReserveAsync(execution, 7m);
        var command = (await CommandAsync(StockPostingKind.Shipment, _target, null, 5m)) with { ExecutionUnitId = execution };
        Assert.Equal(StockPostingDecision.ProtectedStock, (await ExecuteAsync(command)).Decision);
        await using var verify = Open(_tenant);
        Assert.Equal(5m, (await verify.Balances.SingleAsync(item => item.Id == _target)).Quantity);
        Assert.Equal(7m, (await verify.Reservations.SingleAsync()).RemainingQuantity);
    }

    [Fact]
    public async Task ConsumedReservationIdentityCannotBeRecreatedByAnotherReserveOperation()
    {
        await ReceiveAsync(10m);
        var execution = Guid.NewGuid();
        var reserve = await ReservationCommandAsync(execution, ReservationAction.Reserve, 7m);
        await ExecuteReservationAsync(reserve);
        var shipment = (await CommandAsync(StockPostingKind.Shipment, _source, null, 7m)) with { ExecutionUnitId = execution };
        Assert.Equal(StockPostingDecision.Posted, (await ExecuteAsync(shipment)).Decision);
        Assert.Equal(ReservationDecision.Replayed,
            (await ExecuteReservationAsync(reserve with { OperationId = Guid.NewGuid() })).Decision);
        Assert.Equal(ReservationDecision.IdempotencyConflict,
            (await ExecuteReservationAsync(reserve with { OperationId = Guid.NewGuid(), Quantity = 1m })).Decision);
        await using var verify = Open(_tenant);
        Assert.Equal(0m, (await verify.Reservations.SingleAsync()).RemainingQuantity);
        Assert.Single(await verify.ReservationChanges.ToListAsync());
    }

    [Fact]
    public async Task ReusedReleaseOperationWithChangedQuantityCannotReleaseAgain()
    {
        await ReceiveAsync(10m);
        var execution = Guid.NewGuid();
        await ReserveAsync(execution, 7m);
        var release = await ReservationCommandAsync(execution, ReservationAction.ReleaseUnstarted, 2m);
        await ExecuteReservationAsync(release);
        Assert.Equal(ReservationDecision.IdempotencyConflict,
            (await ExecuteReservationAsync(release with { Quantity = 3m })).Decision);
        Assert.Equal(ReservationDecision.InsufficientStock, (await ReserveAsync(Guid.NewGuid(), 6m)).Decision);
        await using var verify = Open(_tenant);
        Assert.Equal(5m, (await verify.Reservations.SingleAsync()).RemainingQuantity);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReservationFailureOrCancellationAfterSaveLeavesNoProtectionOrIdentity(bool cancel)
    {
        await ReceiveAsync(10m);
        var command = await ReservationCommandAsync(Guid.NewGuid(), ReservationAction.Reserve, 7m);
        using var cancellation = new CancellationTokenSource();
        await using (var broken = Open(_tenant, new AfterSaveFault(cancel, cancellation)))
        {
            if (cancel)
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                    new ChangeStockReservation(broken, _clock).ExecuteAsync(command, cancellation.Token));
            else
                await Assert.ThrowsAsync<InvalidOperationException>(() =>
                    new ChangeStockReservation(broken, _clock).ExecuteAsync(command));
        }
        await using var verify = Open(_tenant);
        Assert.Empty(await verify.Reservations.ToListAsync());
        Assert.Empty(await verify.ReservationChanges.ToListAsync());
        Assert.Equal(command.ExpectedVersion, (await verify.Balances.SingleAsync(item => item.Id == _source)).Version);
        Assert.Equal(ReservationDecision.Changed, (await ExecuteReservationAsync(command)).Decision);
    }

    [Fact]
    public async Task ReservationFactsAndBusinessIdentityAreImmutableAcrossTenantBoundaries()
    {
        await ReceiveAsync(10m);
        var execution = Guid.NewGuid();
        var command = await ReservationCommandAsync(execution, ReservationAction.Reserve, 7m);
        await ExecuteReservationAsync(command);
        await using (var other = Open(_other))
        {
            Assert.Empty(await other.Reservations.ToListAsync());
            Assert.Empty(await other.ReservationChanges.ToListAsync());
            Assert.Equal(ReservationDecision.NotFound,
                (await new ChangeStockReservation(other, _clock).ExecuteAsync(command)).Decision);
        }
        await using (var database = Open(_tenant))
        {
            await using var transaction = await database.Database.BeginTransactionAsync();
            database.Reservations.Remove(await database.Reservations.SingleAsync());
            await Assert.ThrowsAsync<InvalidOperationException>(() => database.SaveChangesAsync());
        }
        await using (var database = Open(_tenant))
        {
            database.ReservationChanges.Remove(await database.ReservationChanges.SingleAsync());
            await Assert.ThrowsAsync<InvalidOperationException>(() => database.SaveChangesAsync());
        }
        await using (var database = Open(_tenant))
        {
            await using var transaction = await database.Database.BeginTransactionAsync();
            var reservation = await database.Reservations.SingleAsync();
            database.Entry(reservation).Property(item => item.SourceLineId).CurrentValue = Guid.NewGuid();
            await Assert.ThrowsAsync<InvalidOperationException>(() => database.SaveChangesAsync());
        }
    }

    [Fact]
    public async Task ReservationAndUnrelatedShipmentCompeteOnTheSameVersionFence()
    {
        await ReceiveAsync(10m);
        var reserve = await ReservationCommandAsync(Guid.NewGuid(), ReservationAction.Reserve, 7m);
        var shipment = await CommandAsync(StockPostingKind.Shipment, _source, null, 7m);
        var reserving = ExecuteReservationAsync(reserve);
        var shipping = ExecuteAsync(shipment);
        await Task.WhenAll(reserving, shipping);
        var reservationResult = await reserving;
        var shipmentResult = await shipping;
        Assert.True((reservationResult.Decision == ReservationDecision.Changed
                && shipmentResult.Decision is StockPostingDecision.VersionConflict or StockPostingDecision.InsufficientStock)
            || (reservationResult.Decision == ReservationDecision.VersionConflict && shipmentResult.Decision == StockPostingDecision.Posted));
        await using var verify = Open(_tenant);
        var balance = await verify.Balances.SingleAsync(item => item.Id == _source);
        var protectedQuantity = await verify.Reservations.SumAsync(item => item.RemainingQuantity);
        Assert.True(balance.Quantity >= protectedQuantity);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(1000000000)]
    [InlineData(0.0000001)]
    public async Task InvalidReservationQuantityDoesNotAcquireAnOperationOrBalanceFence(decimal quantity)
    {
        await ReceiveAsync(10m);
        var command = await ReservationCommandAsync(Guid.NewGuid(), ReservationAction.Reserve, quantity);
        Assert.Equal(ReservationDecision.Invalid, (await ExecuteReservationAsync(command)).Decision);
        await using var verify = Open(_tenant);
        Assert.Empty(await verify.Reservations.ToListAsync());
        Assert.Empty(await verify.ReservationChanges.ToListAsync());
        Assert.Equal(command.ExpectedVersion, (await verify.Balances.SingleAsync(item => item.Id == _source)).Version);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReservationDatabaseFaultIsSanitizedAndNeverBecomesAReplay(bool transient)
    {
        var command = await ReservationCommandAsync(Guid.NewGuid(), ReservationAction.Reserve, 1m);
        var fault = new QueryFault(transient, "wms_reservation_changes");
        await using var broken = Open(_tenant, fault);
        var error = await Assert.ThrowsAsync<WmsDatabaseException>(() => new ChangeStockReservation(broken, _clock).ExecuteAsync(command));
        Assert.True(fault.Reached);
        Assert.Equal(transient, error.IsTransient);
        Assert.DoesNotContain("sensitive-sentinel", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnknownProtectionAndMismatchedDocumentCannotBeReleasedOrConsumed()
    {
        await ReceiveAsync(10m);
        var execution = Guid.NewGuid();
        await ReserveAsync(execution, 7m);
        var shipment = (await CommandAsync(StockPostingKind.Shipment, _source, null, 1m))
            with
        { ExecutionUnitId = execution, SourceLineId = Guid.NewGuid() };
        Assert.Equal(StockPostingDecision.ProtectedStock, (await ExecuteAsync(shipment)).Decision);
        await ChangeReservationAsync(execution, ReservationAction.MarkUnknown, 0m);
        Assert.Equal(ReservationDecision.StateConflict,
            (await ChangeReservationAsync(execution, ReservationAction.ReleaseUnstarted, 7m)).Decision);
        await using var verify = Open(_tenant);
        Assert.Equal(7m, (await verify.Reservations.SingleAsync()).RemainingQuantity);
        Assert.Equal(10m, (await verify.Balances.SingleAsync(item => item.Id == _source)).Quantity);
    }

    [Fact]
    public async Task PartialConfirmedTransferConsumesOnlyItsPortionWithBothLedgerLegs()
    {
        await ReceiveAsync(10m);
        var execution = Guid.NewGuid();
        await ReserveAsync(execution, 7m);
        await ChangeReservationAsync(execution, ReservationAction.MarkInTransit, 0m);
        var transfer = (await CommandAsync(StockPostingKind.Transfer, _source, _target, 3m)) with { ExecutionUnitId = execution };
        var posted = await ExecuteAsync(transfer);
        Assert.Equal(StockPostingDecision.Posted, posted.Decision);
        Assert.Equal(StockPostingDecision.Replayed, (await ExecuteAsync(transfer)).Decision);
        await using var verify = Open(_tenant);
        Assert.Equal(4m, (await verify.Reservations.SingleAsync()).RemainingQuantity);
        Assert.Equal(7m, (await verify.Balances.SingleAsync(item => item.Id == _source)).Quantity);
        Assert.Equal(3m, (await verify.Balances.SingleAsync(item => item.Id == _target)).Quantity);
        Assert.Equal(2, await verify.Entries.CountAsync(item => item.PostingId == posted.PostingId));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NewProtectionAgainstNewIncomeCannotBeCalculatedAgainstAnOldBalance(bool posting)
    {
        await ReceiveAsync(5m);
        var reserve = await ReservationCommandAsync(Guid.NewGuid(), ReservationAction.Reserve, 1m);
        var shipment = await CommandAsync(StockPostingKind.Shipment, _source, null, 1m);
        var interleaving = new BeforeProtectionRead(async () =>
        {
            await ReceiveAsync(5m);
            Assert.Equal(ReservationDecision.Changed, (await ReserveAsync(Guid.NewGuid(), 7m)).Decision);
        });
        await using var stale = Open(_tenant, interleaving);
        if (posting)
            Assert.Equal(StockPostingDecision.VersionConflict, (await new PostStock(stale, _clock).ExecuteAsync(shipment)).Decision);
        else
            Assert.Equal(ReservationDecision.VersionConflict, (await new ChangeStockReservation(stale, _clock).ExecuteAsync(reserve)).Decision);
        Assert.True(interleaving.Reached);
        await using var verify = Open(_tenant);
        Assert.Equal(10m, (await verify.Balances.SingleAsync(item => item.Id == _source)).Quantity);
        Assert.Equal(7m, await verify.Reservations.SumAsync(item => item.RemainingQuantity));
    }

    private async Task<ReservationCommand> ReservationCommandAsync(Guid execution, ReservationAction action, decimal quantity)
    {
        await using var database = Open(_tenant);
        var balance = await database.Balances.SingleAsync(item => item.Id == _source);
        return new(Guid.NewGuid(), _actor, Guid.NewGuid(), action, execution, _source, _document, _line, quantity, balance.Version);
    }

    private async Task<ReservationResult> ReserveAsync(Guid execution, decimal quantity) =>
        await ExecuteReservationAsync(await ReservationCommandAsync(execution, ReservationAction.Reserve, quantity));

    private async Task<ReservationResult> ChangeReservationAsync(Guid execution, ReservationAction action, decimal quantity) =>
        await ExecuteReservationAsync(await ReservationCommandAsync(execution, action, quantity));

    private async Task<ReservationResult> ExecuteReservationAsync(ReservationCommand command)
    {
        await using var database = Open(_tenant);
        return await new ChangeStockReservation(database, _clock).ExecuteAsync(command);
    }

    private async Task ReceiveAsync(decimal quantity) =>
        Assert.Equal(StockPostingDecision.Posted,
            (await ExecuteAsync(await CommandAsync(StockPostingKind.Receipt, null, _source, quantity))).Decision);

    private async Task<StockPostingCommand> CommandAsync(StockPostingKind kind, Guid? source, Guid? target, decimal quantity)
    {
        await using var database = Open(_tenant);
        return new(Guid.NewGuid(), _actor, Guid.NewGuid(), kind, _document, _line, Guid.NewGuid(),
            source, target, quantity,
            source.HasValue ? (await database.Balances.SingleAsync(item => item.Id == source)).Version : null,
            target.HasValue ? (await database.Balances.SingleAsync(item => item.Id == target)).Version : null);
    }

    private async Task<StockPostingResult> ExecuteAsync(StockPostingCommand command)
    {
        await using var database = Open(_tenant);
        return await new PostStock(database, _clock).ExecuteAsync(command);
    }

    private WmsDbContext Open(Guid tenant, IInterceptor? interceptor = null)
    {
        MySqlTestConnection.ValidateTarget(_connection);
        var options = new DbContextOptionsBuilder<WmsDbContext>().UseMySQL(_connection,
            mysql => mysql.MigrationsHistoryTable("__WesWmsMigrations"));
        if (interceptor is not null) options.AddInterceptors(interceptor);
        return new(options.Options, tenant);
    }

    public async Task DisposeAsync()
    {
        if (!_ready) return;
        foreach (var tenant in new[] { _tenant, _other })
        {
            await using var database = Open(tenant);
            await database.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM wms_reservation_changes WHERE TenantId = {tenant.ToString("D")}");
            await database.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM wms_stock_reservations WHERE TenantId = {tenant.ToString("D")}");
            await database.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM wms_stock_entries WHERE TenantId = {tenant.ToString("D")}");
            await database.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM wms_stock_postings WHERE TenantId = {tenant.ToString("D")}");
            await database.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM wms_stock_balances WHERE TenantId = {tenant.ToString("D")}");
        }
    }

    private sealed class Clock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new DateTimeOffset(2026, 10, 26, 16, 0, 0, TimeSpan.Zero).AddTicks(1234560);
    }

    private sealed class AfterSaveFault(bool cancel, CancellationTokenSource cancellation) : SaveChangesInterceptor
    {
        public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result,
            CancellationToken cancellationToken = default)
        {
            if (cancel) cancellation.Cancel();
            else throw new InvalidOperationException("Injected failure after persisted changes but before transaction commit.");
            return ValueTask.FromResult(result);
        }
    }

    private sealed class QueryFault(bool transient, string table = "wms_stock_postings") : DbCommandInterceptor
    {
        public bool Reached { get; private set; }
        public override ValueTask<InterceptionResult<System.Data.Common.DbDataReader>> ReaderExecutingAsync(
            System.Data.Common.DbCommand command, CommandEventData eventData,
            InterceptionResult<System.Data.Common.DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains(table, StringComparison.Ordinal))
            {
                Reached = true;
                throw new DatabaseFault(transient);
            }
            return ValueTask.FromResult(result);
        }
    }

    private sealed class DatabaseFault(bool transient) : System.Data.Common.DbException("sensitive-sentinel")
    {
        public override bool IsTransient => transient;
    }

    private sealed class BeforeProtectionRead(Func<Task> change) : DbCommandInterceptor
    {
        private bool _readBalance;
        public bool Reached { get; private set; }
        public override async ValueTask<InterceptionResult<System.Data.Common.DbDataReader>> ReaderExecutingAsync(
            System.Data.Common.DbCommand command, CommandEventData eventData,
            InterceptionResult<System.Data.Common.DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("wms_stock_balances", StringComparison.Ordinal))
                _readBalance = true;
            if (_readBalance && !Reached && command.CommandText.Contains("wms_stock_reservations", StringComparison.Ordinal))
            {
                Reached = true;
                await change();
            }
            return result;
        }
    }
}
