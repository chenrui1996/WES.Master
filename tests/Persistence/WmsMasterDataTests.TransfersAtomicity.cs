using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Wes.Wms.Domain;
using Wes.Wms.Features.Transfers;
using Xunit;

namespace Wes.Persistence.Tests;

public sealed partial class WmsMasterDataTests
{
    [Fact]
    public async Task TransferSafePartialClosureRetainsPostedAAndClosesOnlyUnstartedB()
    {
        var fixture = await TransferReadyAsync(); var created = await CreateTransferAsync(fixture);
        var detail = await TransferDetailAsync(created.Id!.Value);
        await using (var owner = Open(_tenant))
            Assert.Equal(TransferDecision.Committed, (await Transfers(owner).ApproveAsync(Reader, detail.Id, Approval(detail),
                Guid.NewGuid(), Guid.NewGuid(), CancellationToken.None)).Decision);
        detail = await TransferDetailAsync(detail.Id);
        await using (var owner = Open(_tenant))
            Assert.Equal(TransferDecision.Committed, (await Transfers(owner).DispatchAsync(Reader, detail.Id,
                new(detail.Version, fixture.A), Guid.NewGuid(), Guid.NewGuid(), CancellationToken.None)).Decision);
        detail = await TransferDetailAsync(detail.Id); var line = detail.Lines[0];
        await using (var owner = Open(_tenant))
            Assert.Equal(TransferDecision.Committed, (await Transfers(owner).ArriveAsync(Reader, detail.Id,
                new(detail.Version, fixture.A, line.SourceLocationId, line.TargetLocationId, line.ContainerId, "3", "Final scans"),
                Guid.NewGuid(), Guid.NewGuid(), CancellationToken.None)).Decision);
        detail = await TransferDetailAsync(detail.Id);
        Assert.Equal(2, detail.Lines[0].PostingStatus);
        await using (var owner = Open(_tenant))
            Assert.Equal(TransferDecision.Committed, (await Transfers(owner).CloseAsync(Reader, detail.Id,
                new(detail.Version, "B verified never dispatched"), Guid.NewGuid(), Guid.NewGuid(), CancellationToken.None)).Decision);
        var closed = await TransferDetailAsync(detail.Id); Assert.Equal(5, closed.State);
        Assert.Equal(("3.000000", "0.000000", 2), (closed.Lines[0].MovedQuantity, closed.Lines[0].ClosedQuantity, closed.Lines[0].PostingStatus));
        Assert.Equal(("0.000000", "1.000000", 4), (closed.Lines[1].MovedQuantity, closed.Lines[1].ClosedQuantity, closed.Lines[1].ExecutionStatus));
        await using var verify = Open(_tenant);
        Assert.Single(await verify.Postings.Where(row => row.Kind == StockPostingKind.Transfer).ToArrayAsync());
        Assert.Single(await verify.TransferOutbox.ToArrayAsync());
        Assert.Equal(0m, await verify.Reservations.SumAsync(row => row.RemainingQuantity));
        Assert.Equal(1m, (await verify.Balances.SingleAsync(row => row.Id == fixture.SourceB)).Quantity);
        Assert.False(await TransferProtection.ConflictsAsync(verify, fixture.Warehouse,
            closed.Lines.Select(row => row.TargetLocationId), closed.Lines.Select(row => row.ContainerId), CancellationToken.None));
    }

    [Fact]
    public async Task TransferTwoOrdersCompetingForSameContainersCannotLeavePartialHolds()
    {
        var fixture = await TransferReadyAsync();
        var first = await CreateTransferAsync(fixture, "TRANSFER-COMPETE-1");
        var second = await CreateTransferAsync(fixture, "TRANSFER-COMPETE-2");
        var details = new[] { await TransferDetailAsync(first.Id!.Value), await TransferDetailAsync(second.Id!.Value) };
        async Task<TransferResult> Approve(TransferRecord record)
        {
            await using var owner = Open(_tenant);
            return await Transfers(owner).ApproveAsync(Reader, record.Id, Approval(record), Guid.NewGuid(), Guid.NewGuid(), CancellationToken.None);
        }
        var results = await Task.WhenAll(Task.Run(() => Approve(details[0])), Task.Run(() => Approve(details[1])));
        Assert.Single(results, row => row.Decision == TransferDecision.Committed);
        Assert.Single(results, row => row.Decision == TransferDecision.Blocked);
        await using var verify = Open(_tenant);
        Assert.Equal(2, await verify.Reservations.CountAsync()); Assert.Equal(4m, await verify.Reservations.SumAsync(row => row.RemainingQuantity));
        Assert.Equal(1, await verify.TransferDocuments.CountAsync(row => row.State == TransferState.Approved));
        Assert.Equal(1, await verify.TransferDocuments.CountAsync(row => row.State == TransferState.Draft));
    }

    [Fact]
    public async Task TransferApprovalFaultAfterSavingRollsBackEverySourceFenceReservationAndFact()
    {
        var fixture = await TransferReadyAsync(); var created = await CreateTransferAsync(fixture);
        var detail = await TransferDetailAsync(created.Id!.Value);
        await using (var owner = Open(_tenant, new FailSavedTransferApproval()))
            await Assert.ThrowsAsync<DbUpdateException>(() => Transfers(owner).ApproveAsync(Reader, detail.Id, Approval(detail),
                Guid.NewGuid(), Guid.NewGuid(), CancellationToken.None));
        var after = await TransferDetailAsync(detail.Id);
        Assert.Equal(detail.Version, after.Version); Assert.Equal(detail.Lines, after.Lines);
        await using var verify = Open(_tenant); Assert.Empty(await verify.Reservations.ToArrayAsync());
        Assert.Single(await verify.TransferFacts.ToArrayAsync()); Assert.Empty(await verify.TransferOutbox.ToArrayAsync());
    }

    private sealed class FailSavedTransferApproval : SaveChangesInterceptor
    {
        public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result,
            CancellationToken cancellationToken = default) => throw new DbUpdateException("Injected post-save approval fault.");
    }
}
