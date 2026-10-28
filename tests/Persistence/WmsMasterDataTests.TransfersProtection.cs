using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Wes.Wms.Domain;
using Wes.Wms.Features.Counts;
using Wes.Wms.Features.Ledger;
using Wes.Wms.Features.MasterData;
using Wes.Wms.Features.Reservations;
using Wes.Wms.Features.Transfers;
using Xunit;

namespace Wes.Persistence.Tests;

public sealed partial class WmsMasterDataTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task TransferApprovalCannotAcquireSourceOrTargetInsideActiveCount(bool source)
    {
        var fixture = await TransferReadyAsync(); var created = await CreateTransferAsync(fixture);
        var detail = await TransferDetailAsync(created.Id!.Value);
        await CreateAndStartCountAsync(fixture.Warehouse, source ? detail.Lines[0].SourceLocationId : detail.Lines[0].TargetLocationId,
            "TRANSFER-COUNT-FIRST");
        await using var owner = Open(_tenant);
        Assert.Equal(TransferDecision.Blocked, (await Transfers(owner).ApproveAsync(Reader, detail.Id, Approval(detail),
            Guid.NewGuid(), Guid.NewGuid(), CancellationToken.None)).Decision);
        Assert.Empty(await owner.Reservations.ToArrayAsync());
    }

    [Fact]
    public async Task TransferApprovalRequiresTargetStillOperationalAfterDraftPreview()
    {
        var fixture = await TransferReadyAsync(); var created = await CreateTransferAsync(fixture);
        var detail = await TransferDetailAsync(created.Id!.Value); var targetId = detail.Lines[0].TargetLocationId;
        await using (var owner = Open(_tenant))
        {
            var target = await owner.Locations.AsNoTracking().SingleAsync(row => row.Id == targetId);
            Assert.Equal(LocationStateDecision.Changed, (await new LocationStateAccess(owner, new TransferAuthority(),
                new Schema(LocationSchemaDecision.Valid), _clock, NullLogger<LocationStateAccess>.Instance).ChangeAsync(Reader,
                targetId, new(Guid.NewGuid(), target.Version, false, "Target no longer operational"))).Decision);
        }
        await using var approval = Open(_tenant);
        Assert.Equal(TransferDecision.Blocked, (await Transfers(approval).ApproveAsync(Reader, detail.Id, Approval(detail),
            Guid.NewGuid(), Guid.NewGuid(), CancellationToken.None)).Decision);
        Assert.Empty(await approval.Reservations.ToArrayAsync());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task TransferProtectionCannotBeBypassedByRawLedgerOrReservationWrites(bool reservationWrite)
    {
        var fixture = await TransferReadyAsync(); var created = await CreateTransferAsync(fixture);
        var detail = await TransferDetailAsync(created.Id!.Value);
        await using (var approval = Open(_tenant))
            Assert.Equal(TransferDecision.Committed, (await Transfers(approval).ApproveAsync(Reader, detail.Id, Approval(detail),
                Guid.NewGuid(), Guid.NewGuid(), CancellationToken.None)).Decision);
        await using var owner = Open(_tenant);
        await using var transaction = await owner.Database.BeginTransactionAsync();
        if (reservationWrite)
        {
            var reservation = await owner.Reservations.SingleAsync(row => row.SourceLineId == fixture.A);
            var before = reservation.RemainingQuantity;
            Assert.True(reservation.TryChange(ReservationAction.ReleaseUnstarted, before));
            owner.ReservationChanges.Add(new(_tenant, Guid.NewGuid(), _actor, Guid.NewGuid(), ReservationAction.ReleaseUnstarted,
                reservation, before, before, StockProtectionKind.Reserved, _clock.GetUtcNow()));
            (await owner.Balances.SingleAsync(row => row.Id == fixture.SourceA)).TouchReservationFence();
        }
        else
        {
            var target = await owner.Balances.SingleAsync(row => row.Id == fixture.TargetA);
            var posting = new StockPosting(_tenant, Guid.NewGuid(), _actor, Guid.NewGuid(), StockPostingKind.Receipt,
                Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), null, target.Id, 1m, _clock.GetUtcNow());
            owner.Postings.Add(posting);
            var effect = target.Credit(StockMovementQuantity.Create(1m, QuantityPrecision.Create(target.Precision)));
            owner.Entries.Add(new(posting, target, effect.Before, effect.After));
        }
        await Assert.ThrowsAsync<InvalidOperationException>(() => owner.SaveChangesAsync());
        await transaction.RollbackAsync();
        await using var verify = Open(_tenant); Assert.Equal(4m, await verify.Reservations.SumAsync(row => row.RemainingQuantity));
        Assert.Equal(0m, (await verify.Balances.SingleAsync(row => row.Id == fixture.TargetA)).Quantity);
    }

    [Theory]
    [InlineData("approved")]
    [InlineData("transit")]
    [InlineData("unknown")]
    [InlineData("arrived")]
    public async Task TransferDestinationContainerAndSourceRemainProtectedAcrossEveryStockBoundary(string stage)
    {
        var fixture = await TransferReadyAsync(); var created = await CreateTransferAsync(fixture);
        var detail = await TransferDetailAsync(created.Id!.Value);
        CountResult count;
        await using (var owner = Open(_tenant))
        {
            count = await Counts(owner).CreateAsync(Reader, new("TRANSFER-PROTECTED-COUNT", fixture.Warehouse, detail.Lines[0].TargetLocationId),
                Guid.NewGuid(), Guid.NewGuid());
            Assert.Equal(CountDecision.Committed, count.Decision);
        }
        await using (var owner = Open(_tenant))
            Assert.Equal(TransferDecision.Committed, (await Transfers(owner).ApproveAsync(Reader, detail.Id, Approval(detail),
                Guid.NewGuid(), Guid.NewGuid(), CancellationToken.None)).Decision);
        detail = await TransferDetailAsync(detail.Id);
        if (stage != "approved")
        {
            await using var owner = Open(_tenant);
            Assert.Equal(TransferDecision.Committed, (await Transfers(owner).DispatchAsync(Reader, detail.Id,
                new(detail.Version, fixture.A), Guid.NewGuid(), Guid.NewGuid(), CancellationToken.None)).Decision);
            detail = await TransferDetailAsync(detail.Id);
        }
        var line = detail.Lines[0];
        if (stage == "unknown")
        {
            await using var owner = Open(_tenant);
            Assert.Equal(TransferDecision.Committed, (await Transfers(owner).UnknownAsync(Reader, detail.Id,
                new(detail.Version, fixture.A, "Physical effect unresolved"), Guid.NewGuid(), Guid.NewGuid(), CancellationToken.None)).Decision);
        }
        if (stage == "arrived")
        {
            await using var owner = Open(_tenant, new FailTransferPosting());
            Assert.Equal(TransferDecision.Committed, (await Transfers(owner).ArriveAsync(Reader, detail.Id,
                new(detail.Version, fixture.A, line.SourceLocationId, line.TargetLocationId, line.ContainerId, "3", "Final scans"),
                Guid.NewGuid(), Guid.NewGuid(), CancellationToken.None)).Decision);
        }
        foreach (var balanceId in new[] { fixture.SourceA, fixture.TargetA })
        {
            await using var owner = Open(_tenant);
            var balance = await owner.Balances.AsNoTracking().SingleAsync(row => row.Id == balanceId);
            Assert.Equal(StockPostingDecision.ProtectedStock, (await new PostStock(owner, _clock).ExecuteAsync(new(Guid.NewGuid(),
                _actor, Guid.NewGuid(), StockPostingKind.Receipt, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), null,
                balance.Id, 1m, null, balance.Version))).Decision);
            Assert.Equal(ReservationDecision.StateConflict, (await new ChangeStockReservation(owner, _clock).ExecuteAsync(new(Guid.NewGuid(),
                _actor, Guid.NewGuid(), ReservationAction.Reserve, Guid.NewGuid(), balance.Id, Guid.NewGuid(), Guid.NewGuid(),
                1m, balance.Version))).Decision);
        }
        await using (var owner = Open(_tenant))
            Assert.Equal(StockResourceDecision.Blocked, (await ResourceAccess(owner).DimensionAsync(Reader,
                new(Guid.NewGuid(), Guid.NewGuid(), fixture.Warehouse, line.TargetLocationId, line.ItemId, "", line.ContainerId))).Decision);
        await using (var owner = Open(_tenant))
        {
            var location = await owner.Locations.AsNoTracking().SingleAsync(row => row.Id == line.TargetLocationId);
            Assert.Equal(LocationStateDecision.Blocked, (await new LocationStateAccess(owner, new TransferAuthority(),
                new Schema(LocationSchemaDecision.Valid), _clock, NullLogger<LocationStateAccess>.Instance).ChangeAsync(Reader,
                location.Id, new(Guid.NewGuid(), location.Version, false, "Cannot disable incoming target"))).Decision);
        }
        await using (var owner = Open(_tenant))
        {
            Assert.Equal(CountDecision.Blocked, (await Counts(owner).StartAsync(Reader, count.Id!.Value,
                new(count.Version!), Guid.NewGuid(), Guid.NewGuid())).Decision);
        }
        await using var verify = Open(_tenant);
        Assert.Equal(4m, await verify.Reservations.SumAsync(row => row.RemainingQuantity));
        Assert.Equal(2, await verify.Reservations.CountAsync()); Assert.Empty(await verify.TransferOutbox.ToArrayAsync());
    }

    [Theory]
    [InlineData("plan")]
    [InlineData("state")]
    [InlineData("fact")]
    [InlineData("delete")]
    [InlineData("fake-dispatch")]
    [InlineData("fake-sync")]
    public async Task TransferPrivateEntityRawMutationsCannotBypassHistoryOrLedgerGuards(string mutation)
    {
        var fixture = await TransferReadyAsync(); var created = await CreateTransferAsync(fixture);
        await using var owner = Open(_tenant);
        await using var transaction = await owner.Database.BeginTransactionAsync();
        var document = await owner.TransferDocuments.SingleAsync(row => row.Id == created.Id);
        switch (mutation)
        {
            case "plan": owner.Entry(document).Property(row => row.Plan).CurrentValue = "[]"; break;
            case "state": document.Advance(TransferState.Completed); break;
            case "fact":
                var fact = await owner.TransferFacts.SingleAsync();
                owner.Entry(fact).Property(row => row.Input).CurrentValue = "{}"; break;
            case "delete": owner.Remove(document); break;
            default:
                document.Advance(mutation == "fake-sync" ? TransferState.Completed : TransferState.Executing);
                owner.TransferFacts.Add(new(document, Guid.NewGuid(), _actor, Guid.NewGuid(),
                    mutation == "fake-sync" ? TransferFactKind.Synced : TransferFactKind.Dispatched,
                    document.Lines[0].Id, "{}", _clock.GetUtcNow()));
                break;
        }
        await Assert.ThrowsAsync<InvalidOperationException>(() => owner.SaveChangesAsync());
        await transaction.RollbackAsync();
        await using var verify = Open(_tenant);
        Assert.Equal(TransferState.Draft, (await verify.TransferDocuments.SingleAsync()).State);
        Assert.Single(await verify.TransferFacts.ToArrayAsync()); Assert.Empty(await verify.Reservations.ToArrayAsync());
    }
}
