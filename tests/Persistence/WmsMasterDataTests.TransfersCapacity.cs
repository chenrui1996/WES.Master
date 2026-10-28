using Microsoft.EntityFrameworkCore;
using Wes.Wms.Domain;
using Wes.Wms.Features.Ledger;
using Wes.Wms.Features.Transfers;
using Xunit;

namespace Wes.Persistence.Tests;

public sealed partial class WmsMasterDataTests
{
    [Fact]
    public async Task TransferActualHundredLinePlanReadsAndApprovesEveryLineWithoutTruncation()
    {
        var fixture = await TransferReadyAsync();
        var lines = new List<TransferDraftLineRequest>
        {
            new(fixture.A, fixture.SourceA, fixture.TargetA, "3", 0), new(fixture.B, fixture.SourceB, fixture.TargetB, "1", 0)
        };
        var sources = new List<StockBalance>();
        await using (var seed = Open(_tenant))
        {
            var source = await seed.Balances.AsNoTracking().SingleAsync(row => row.Id == fixture.SourceA);
            var template = await seed.Locations.AsNoTracking().SingleAsync(row => row.Id == source.LocationId);
            var locations = new List<(WarehouseLocation Source, WarehouseLocation Target)>();
            await using (var creation = await seed.Database.BeginTransactionAsync())
            {
                for (var index = 2; index < 100; index++)
                {
                    var from = new WarehouseLocation(_tenant, fixture.Warehouse, template.PackId, template.SchemaVersion,
                        $"TRANSFER-CAPACITY-S-{index}", "Capacity source", _clock.GetUtcNow());
                    var to = new WarehouseLocation(_tenant, fixture.Warehouse, template.PackId, template.SchemaVersion,
                        $"TRANSFER-CAPACITY-T-{index}", "Capacity target", _clock.GetUtcNow());
                    seed.AddRange(from, to);
                    seed.MasterDataChanges.AddRange(new(Guid.NewGuid(), Reader.ActorId, Guid.NewGuid(), from),
                        new(Guid.NewGuid(), Reader.ActorId, Guid.NewGuid(), to));
                    locations.Add((from, to));
                }
                await seed.SaveChangesAsync(); await creation.CommitAsync();
            }
            await using (var enabling = await seed.Database.BeginTransactionAsync())
            {
                foreach (var location in locations.SelectMany(row => new[] { row.Source, row.Target }))
                {
                    var before = location.Version; location.ChangeState(true);
                    seed.LocationStateChanges.Add(new(Guid.NewGuid(), Reader.ActorId, Guid.NewGuid(), location,
                        before, false, "Capacity fixture ready", _clock.GetUtcNow()));
                }
                await seed.SaveChangesAsync(); await enabling.CommitAsync();
            }
            seed.ChangeTracker.Clear();
            await using var transaction = await seed.Database.BeginTransactionAsync();
            for (var index = 2; index < 100; index++)
            {
                var container = new StockContainer(_tenant, Guid.NewGuid(), Reader.ActorId, Guid.NewGuid(),
                    $"TRANSFER-CAPACITY-{index}", "Capacity container", fixture.Warehouse, _clock.GetUtcNow());
                var from = new StockBalance(_tenant, fixture.Warehouse, locations[index - 2].Source.Id, source.ItemId, "", container.Id, source.UnitId, source.Precision);
                var to = new StockBalance(_tenant, fixture.Warehouse, locations[index - 2].Target.Id, source.ItemId, "", container.Id, source.UnitId, source.Precision);
                seed.AddRange(container, from, to);
                seed.DimensionRegistrations.AddRange(new(Guid.NewGuid(), Reader.ActorId, Guid.NewGuid(), from, _clock.GetUtcNow()),
                    new(Guid.NewGuid(), Reader.ActorId, Guid.NewGuid(), to, _clock.GetUtcNow()));
                sources.Add(from); lines.Add(new(Guid.NewGuid(), from.Id, to.Id, "1", 0));
            }
            await seed.SaveChangesAsync(); await transaction.CommitAsync();
        }
        await using (var seed = Open(_tenant))
            foreach (var source in sources)
            {
                Assert.Equal(StockPostingDecision.Posted, (await new PostStock(seed, _clock).ExecuteAsync(new(Guid.NewGuid(),
                    Reader.ActorId, Guid.NewGuid(), StockPostingKind.Receipt, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
                    null, source.Id, 1m, null, source.Version))).Decision);
                seed.ChangeTracker.Clear();
            }
        TransferResult created;
        await using (var owner = Open(_tenant))
            created = await Transfers(owner).CreateAsync(Reader, new(Guid.NewGuid(), Guid.NewGuid(), "TRANSFER-CAPACITY",
                fixture.Warehouse, lines), CancellationToken.None);
        Assert.Equal(TransferDecision.Committed, created.Decision);
        var detail = await TransferDetailAsync(created.Id!.Value);
        Assert.Equal(100, detail.Lines.Count); Assert.Equal(lines.Select(row => row.Id), detail.Lines.Select(row => row.Id));
        await using (var owner = Open(_tenant))
            Assert.Equal(TransferDecision.Committed, (await Transfers(owner).ApproveAsync(Reader, detail.Id, Approval(detail),
                Guid.NewGuid(), Guid.NewGuid(), CancellationToken.None)).Decision);
        var approved = await TransferDetailAsync(detail.Id);
        Assert.Equal(100, approved.Lines.Count); Assert.All(approved.Lines, row => Assert.Equal(row.PlannedQuantity, row.ReservedQuantity));
        await using var verify = Open(_tenant);
        Assert.Equal(100, await verify.Reservations.CountAsync()); Assert.Equal(102m, await verify.Reservations.SumAsync(row => row.RemainingQuantity));
    }
}
