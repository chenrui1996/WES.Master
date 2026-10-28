using Microsoft.EntityFrameworkCore;
using Wes.Wms.Domain;
using Wes.Wms.Features.Counts;
using Wes.Wms.Features.Ledger;
using Wes.Wms.Features.MasterData;
using Xunit;

namespace Wes.Persistence.Tests;

public sealed partial class WmsMasterDataTests
{
    [Theory]
    [InlineData(0, CountDecision.Blocked)]
    [InlineData(100, CountDecision.Committed)]
    [InlineData(101, CountDecision.Blocked)]
    public async Task CountStartCapturesEveryRegisteredZeroBalanceWithoutTruncation(int dimensions, CountDecision expected)
    {
        var fixture = await LegalDimensionReadyAsync();
        await using var owner = Open(_tenant);
        await using (var transaction = await owner.Database.BeginTransactionAsync())
        {
            var item = await owner.StockItems.AsNoTracking().SingleAsync();
            var unit = await owner.StockUnits.AsNoTracking().SingleAsync();
            for (var index = 0; index < dimensions; index++)
            {
                var container = new StockContainer(_tenant, Guid.NewGuid(), _actor, Guid.NewGuid(), $"COUNT-{index}",
                    "Count dimension", fixture.WarehouseId, _clock.GetUtcNow());
                var balance = new StockBalance(_tenant, fixture.WarehouseId, fixture.LocationId, item.Id, "", container.Id, unit.Id, unit.Precision);
                owner.AddRange(container, balance, new StockDimensionRegistration(Guid.NewGuid(), _actor, Guid.NewGuid(), balance, _clock.GetUtcNow()));
            }
            await owner.SaveChangesAsync(); await transaction.CommitAsync();
        }
        owner.ChangeTracker.Clear();
        var draft = await Counts(owner).CreateAsync(Reader, new("COUNT-BOUND", fixture.WarehouseId, fixture.LocationId), Guid.NewGuid(), Guid.NewGuid());
        var started = await Counts(owner).StartAsync(Reader, draft.Id!.Value, new(draft.Version!), Guid.NewGuid(), Guid.NewGuid());
        Assert.Equal(expected, started.Decision);
        var read = (await Counts(owner).ReadAsync(Reader, draft.Id.Value)).Record!;
        Assert.Equal(expected == CountDecision.Committed ? dimensions : 0, read.Lines.Count);
        Assert.All(read.Lines, row => Assert.Null(row.Quantity));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CountStartRejectsPendingInboundSourceAndTargetWithoutCancellingTransport(bool target)
    {
        var (fixture, receipt) = await TransportReadyAsync(registerTarget: true);
        var location = target ? receipt.TargetLocationId : receipt.ReceiptLocationId;
        await using var owner = Open(_tenant);
        var draft = await Counts(owner).CreateAsync(Reader, new("COUNT-PENDING", fixture.Warehouse, location), Guid.NewGuid(), Guid.NewGuid());
        Assert.Equal(CountDecision.Committed, draft.Decision);
        Assert.Equal(CountDecision.Blocked, (await Counts(owner).StartAsync(Reader, draft.Id!.Value,
            new(draft.Version!), Guid.NewGuid(), Guid.NewGuid())).Decision);
        Assert.False((await owner.Counts.AsNoTracking().SingleAsync()).Active);
        Assert.NotEmpty(await owner.Reservations.AsNoTracking().Where(row => row.RemainingQuantity > 0m).ToArrayAsync());
    }

    [Fact]
    public async Task CountStaleVersionScanMismatchAndChangedOperationTupleCannotChangeCurrentGeneration()
    {
        var fixture = await OutboundReadyAsync();
        var count = await CreateAndStartCountAsync(fixture.Warehouse, fixture.Location);
        await using var owner = Open(_tenant);
        var line = Assert.Single((await Counts(owner).ReadAsync(Reader, count.Id!.Value)).Record!.Lines);
        var request = new CountRecordRequest(count.Version!, line.Id, "12", fixture.Location, fixture.Item, fixture.Container, "Scanned");
        var key = Guid.NewGuid();
        var first = await Counts(owner).RecordAsync(Reader, count.Id.Value, request, key, Guid.NewGuid());
        Assert.Equal(CountDecision.Committed, first.Decision);
        Assert.Equal(first with { Decision = CountDecision.Replayed }, await Counts(owner).RecordAsync(Reader, count.Id.Value, request, key, Guid.NewGuid()));
        Assert.Equal(CountDecision.Conflict, (await Counts(owner).RecordAsync(Reader, count.Id.Value, request with { Quantity = "8" }, key, Guid.NewGuid())).Decision);
        Assert.Equal(CountDecision.Conflict, (await Counts(owner).RecordAsync(Reader, count.Id.Value, request, Guid.NewGuid(), Guid.NewGuid())).Decision);
        Assert.Equal(CountDecision.Conflict, (await Counts(owner).InvalidateAsync(Reader, count.Id.Value,
            new(request.ExpectedVersion, request.Reason), key, Guid.NewGuid())).Decision);
        foreach (var invalid in new[] { request with { LocationId = Guid.NewGuid() }, request with { ItemId = Guid.NewGuid() },
                     request with { ContainerId = Guid.NewGuid() }, request with { LineId = Guid.NewGuid() } })
            Assert.Equal(CountDecision.Conflict, (await Counts(owner).RecordAsync(Reader, count.Id.Value,
                invalid with { ExpectedVersion = first.Version! }, Guid.NewGuid(), Guid.NewGuid())).Decision);
        foreach (var quantity in new[] { "-1", "1e1", "1.0001", " 12", "01", "1.0000001" })
            Assert.Equal(CountDecision.Invalid, (await Counts(owner).RecordAsync(Reader, count.Id.Value,
                request with { ExpectedVersion = first.Version!, Quantity = quantity }, Guid.NewGuid(), Guid.NewGuid())).Decision);
        Assert.Equal(first.Version, (await Counts(owner).ReadAsync(Reader, count.Id.Value)).Record!.Version);
    }

    [Fact]
    public async Task CountLocksGuardLocationWarehouseStateAndDimensionOriginalReplay()
    {
        var fixture = await LegalDimensionReadyAsync();
        await using var owner = Open(_tenant);
        var dimension = await ResourceAccess(owner).DimensionAsync(Reader, fixture);
        var count = await CreateAndStartCountAsync(fixture.WarehouseId, fixture.LocationId);
        Assert.Equal(dimension with { Decision = StockResourceDecision.Replayed }, await ResourceAccess(owner).DimensionAsync(Reader, fixture));
        var location = await owner.Locations.AsNoTracking().SingleAsync();
        Assert.Equal(LocationStateDecision.Blocked, (await LocationStateAsync(location.Id,
            new(Guid.NewGuid(), location.Version, false, "Cannot disable count scope"))).Decision);
        var warehouse = await owner.Warehouses.AsNoTracking().SingleAsync();
        Assert.Equal(WarehouseStateDecision.Blocked, (await StateAsync(warehouse.Id,
            new(Guid.NewGuid(), warehouse.Version, false, "Cannot disable count parent"))).Decision);
        Assert.NotNull(count.Id);
    }

    [Fact]
    public async Task CountFactsAreAppendOnlyAndCountAdjustmentsCannotBeUsedAsOrdinaryPostingCommands()
    {
        var fixture = await OutboundReadyAsync();
        var count = await CreateAndStartCountAsync(fixture.Warehouse, fixture.Location);
        await using (var owner = Open(_tenant))
        {
            var fact = await owner.CountFacts.FirstAsync();
            owner.CountFacts.Remove(fact);
            await Assert.ThrowsAsync<InvalidOperationException>(() => owner.SaveChangesAsync());
        }
        await using (var owner = Open(_tenant))
            Assert.Equal(StockPostingDecision.Invalid, (await new PostStock(owner, _clock).ExecuteAsync(
                new(Guid.NewGuid(), _actor, Guid.NewGuid(), StockPostingKind.AdjustmentIn, count.Id!.Value,
                    Guid.NewGuid(), Guid.NewGuid(), null, fixture.Balance, 2m, null, fixture.BalanceVersion))).Decision);
    }
}
