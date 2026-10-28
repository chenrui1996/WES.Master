using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Wes.Wms.Domain;
using Wes.Wms.Features.Counts;
using Wes.Wms.Features.Inbound;
using Wes.Wms.Features.Ledger;
using Wes.Wms.Features.MasterData;
using Wes.Wms.Features.Reservations;
using Xunit;

namespace Wes.Persistence.Tests;

public sealed partial class WmsMasterDataTests
{
    [Fact]
    [Trait("CaseId", "TC-T11-COUNT-04")]
    public async Task AppliedTransportCompletionReplaysWhileLaterCountProtectsItsContainer()
    {
        var (fixture, request) = await TransportReadyAsync(registerTarget: true);
        var adapter = new ContractTransportAdapter();
        await using var owner = Open(_tenant);
        var worker = TransportWorker(owner, adapter);
        Assert.Equal(InboundTransportDecision.Accepted, (await worker.DispatchAsync(request.ExecutionId)).Decision);
        var completion = await TransportCompletionAsync(owner);
        var applied = await worker.ApplyCompletionAsync(completion);
        Assert.Equal(InboundTransportDecision.Applied, applied.Decision);
        await CreateAndStartCountAsync(fixture.Warehouse, request.TargetLocationId, "COUNT-AFTER-ARRIVAL");
        var replay = await worker.ApplyCompletionAsync(completion);
        Assert.Equal(InboundTransportDecision.Replayed, replay.Decision);
        Assert.Equal(applied.PostingId, replay.PostingId);
        Assert.Equal(2, await owner.Postings.CountAsync());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    [Trait("CaseId", "TC-T11-COUNT-03")]
    public async Task CountLocksProtectBothLegacyPostingLegsAndOriginalCommittedReplay(bool source)
    {
        var fixture = await OutboundReadyAsync();
        var count = await CreateAndStartCountAsync(fixture.Warehouse, fixture.Location);
        var command = new StockPostingCommand(Guid.NewGuid(), _actor, Guid.NewGuid(),
            source ? StockPostingKind.Shipment : StockPostingKind.Receipt, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            source ? fixture.Balance : null, source ? null : fixture.Balance, 1m,
            source ? fixture.BalanceVersion : null, source ? null : fixture.BalanceVersion);
        await using (var owner = Open(_tenant))
            Assert.Equal(StockPostingDecision.ProtectedStock, (await new PostStock(owner, _clock).ExecuteAsync(command)).Decision);
        await using (var owner = Open(_tenant))
            Assert.Equal(CountDecision.Committed, (await Counts(owner).InvalidateAsync(Reader, count.Id!.Value,
                new(count.Version!, "Explicit unlock"), Guid.NewGuid(), Guid.NewGuid())).Decision);
        await using (var owner = Open(_tenant))
            Assert.Equal(StockPostingDecision.Posted, (await new PostStock(owner, _clock).ExecuteAsync(command)).Decision);
        await CreateAndStartCountAsync(fixture.Warehouse, fixture.Location, "COUNT-REPLAY");
        await using (var owner = Open(_tenant))
            Assert.Equal(StockPostingDecision.Replayed, (await new PostStock(owner, _clock).ExecuteAsync(command)).Decision);
    }

    [Fact]
    [Trait("CaseId", "TC-T11-COUNT-03")]
    public async Task CountLockBlocksNewReservationsNewDimensionsAndCrossLocationContainerUse()
    {
        var fixture = await OutboundReadyAsync();
        await CreateAndStartCountAsync(fixture.Warehouse, fixture.Location);
        await using (var owner = Open(_tenant))
        {
            var result = await new ChangeStockReservation(owner, _clock).ExecuteAsync(new(Guid.NewGuid(), _actor, Guid.NewGuid(),
                ReservationAction.Reserve, Guid.NewGuid(), fixture.Balance, Guid.NewGuid(), Guid.NewGuid(), 1m, fixture.BalanceVersion));
            Assert.Equal(ReservationDecision.StateConflict, result.Decision);
        }
        await using (var owner = Open(_tenant))
        {
            var container = await ResourceAccess(owner).ContainerAsync(Reader,
                new(Guid.NewGuid(), Guid.NewGuid(), "NEW", "New container", fixture.Warehouse));
            Assert.Equal(StockResourceDecision.Created, container.Decision);
            Assert.Equal(StockResourceDecision.Blocked, (await ResourceAccess(owner).DimensionAsync(Reader,
                new(Guid.NewGuid(), Guid.NewGuid(), fixture.Warehouse, fixture.Location, fixture.Item, "", container.Id!.Value))).Decision);
        }
        await using (var owner = Open(_tenant))
        {
            var location = await new CreateMasterData(owner, _clock, new Schema(LocationSchemaDecision.Valid))
                .ExecuteAsync(Command("COUNT-OTHER") with { WarehouseId = fixture.Warehouse, PackId = Guid.NewGuid(), SchemaVersion = "v1" });
            Assert.Equal(MasterDataDecision.Created, location.Decision);
            owner.ChangeTracker.Clear();
            await LocationStateAsync(location.Id!.Value, new(Guid.NewGuid(), location.Version!, true, "Ready"));
            Assert.Equal(StockResourceDecision.Blocked, (await ResourceAccess(owner).DimensionAsync(Reader,
                new(Guid.NewGuid(), Guid.NewGuid(), fixture.Warehouse, location.Id.Value, fixture.Item, "", fixture.Container))).Decision);
        }
        await using var verify = Open(_tenant);
        Assert.Single(await verify.Balances.ToArrayAsync());
        Assert.Empty(await verify.Reservations.ToArrayAsync());
    }

    [Fact]
    [Trait("CaseId", "TC-T11-COUNT-04")]
    public async Task ReminderDoesNotUnlockAndInvalidationRestartNeverReusesMeasurements()
    {
        var fixture = await OutboundReadyAsync();
        var count = await CreateAndStartCountAsync(fixture.Warehouse, fixture.Location);
        CountLineView line;
        DateTimeOffset snapshot;
        await using (var owner = Open(_tenant))
        {
            var read = (await Counts(owner).ReadAsync(Reader, count.Id!.Value)).Record!;
            line = Assert.Single(read.Lines); snapshot = read.SnapshotAt!.Value;
            Assert.False((await Counts(owner, clock: new CountClock(snapshot.AddSeconds(1799))).ReadAsync(Reader, count.Id.Value)).Record!.ReminderDue);
            Assert.True((await Counts(owner, clock: new CountClock(snapshot.AddSeconds(1800))).ReadAsync(Reader, count.Id.Value)).Record!.ReminderDue);
            count = await Counts(owner).RecordAsync(Reader, count.Id.Value,
                new(count.Version!, line.Id, "12", fixture.Location, fixture.Item, fixture.Container, "Original scan"), Guid.NewGuid(), Guid.NewGuid());
        }
        var invalidOperation = Guid.NewGuid();
        var invalidRequest = new CountStateRequest(count.Version!, "Explicit unlock invalidates counts");
        CountResult invalidated;
        await using (var owner = Open(_tenant))
        {
            invalidated = await Counts(owner).InvalidateAsync(Reader, count.Id!.Value, invalidRequest, invalidOperation, Guid.NewGuid());
            Assert.Equal(CountDecision.Committed, invalidated.Decision);
            Assert.Null(Assert.Single((await Counts(owner).ReadAsync(Reader, count.Id.Value)).Record!.Lines).Quantity);
            count = await Counts(owner).StartAsync(Reader, count.Id.Value, new(invalidated.Version!), Guid.NewGuid(), Guid.NewGuid());
            Assert.Equal(CountDecision.Committed, count.Decision);
            var restarted = (await Counts(owner).ReadAsync(Reader, count.Id!.Value)).Record!;
            Assert.NotEqual(line.Id, Assert.Single(restarted.Lines).Id);
            Assert.Null(restarted.Lines[0].Quantity);
            Assert.Equal(invalidated with { Decision = CountDecision.Replayed }, await Counts(owner).InvalidateAsync(Reader,
                count.Id.Value, invalidRequest, invalidOperation, Guid.NewGuid()));
            Assert.Equal(CountDecision.Conflict, (await Counts(owner).RecordAsync(Reader, count.Id.Value,
                new(count.Version!, line.Id, "12", fixture.Location, fixture.Item, fixture.Container, "Old line"), Guid.NewGuid(), Guid.NewGuid())).Decision);
        }
    }

    [Fact]
    [Trait("CaseId", "TC-T11-COUNT-05")]
    public async Task TwoStartsAndReservationRaceSerializeOnTheSameResourceFence()
    {
        var fixture = await OutboundReadyAsync();
        async Task<CountResult> Draft(string code)
        {
            await using var owner = Open(_tenant);
            return await Counts(owner).CreateAsync(Reader, new(code, fixture.Warehouse, fixture.Location), Guid.NewGuid(), Guid.NewGuid());
        }
        var first = await Draft("COUNT-A"); var second = await Draft("COUNT-B");
        async Task<CountResult> Start(CountResult draft)
        {
            await using var owner = Open(_tenant);
            return await Counts(owner).StartAsync(Reader, draft.Id!.Value, new(draft.Version!), Guid.NewGuid(), Guid.NewGuid());
        }
        var results = await Task.WhenAll(Task.Run(() => Start(first)), Task.Run(() => Start(second)));
        Assert.Single(results, row => row.Decision == CountDecision.Committed);
        Assert.Single(results, row => row.Decision == CountDecision.Blocked);
        var winner = results.Single(row => row.Decision == CountDecision.Committed);
        await using (var owner = Open(_tenant))
            await Counts(owner).InvalidateAsync(Reader, winner.Id!.Value, new(winner.Version!, "Race next"), Guid.NewGuid(), Guid.NewGuid());
        var third = await Draft("COUNT-C");
        var startTask = Task.Run(() => Start(third));
        var reserveTask = Task.Run(async () =>
        {
            await using var owner = Open(_tenant);
            return await new ChangeStockReservation(owner, _clock).ExecuteAsync(new(Guid.NewGuid(), _actor, Guid.NewGuid(),
                ReservationAction.Reserve, Guid.NewGuid(), fixture.Balance, Guid.NewGuid(), Guid.NewGuid(), 1m, fixture.BalanceVersion));
        });
        await Task.WhenAll(startTask, reserveTask);
        Assert.NotEqual((await startTask).Decision == CountDecision.Committed, (await reserveTask).Decision == ReservationDecision.Changed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("CaseId", "TC-T11-COUNT-06")]
    public async Task CountSaveRevocationOrCancellationRollsBackEveryAdjustmentAndRetainsLock(bool cancellation)
    {
        var fixture = await OutboundReadyAsync();
        var count = await CreateAndStartCountAsync(fixture.Warehouse, fixture.Location);
        await using (var owner = Open(_tenant))
        {
            var line = Assert.Single((await Counts(owner).ReadAsync(Reader, count.Id!.Value)).Record!.Lines);
            count = await Counts(owner).RecordAsync(Reader, count.Id.Value,
                new(count.Version!, line.Id, "8", fixture.Location, fixture.Item, fixture.Container, "Scan"), Guid.NewGuid(), Guid.NewGuid());
        }
        var authority = new CountAuthority();
        using var cancel = new CancellationTokenSource();
        var interceptor = new CountAfterSave(() => { if (cancellation) cancel.Cancel(); else authority.Revoked = true; });
        await using (var owner = Open(_tenant, interceptor))
        {
            var approver = Reader with { ActorId = Guid.NewGuid() };
            if (cancellation)
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Counts(owner, authority).AdjustAsync(approver,
                    count.Id!.Value, new(count.Version!, "Review"), Guid.NewGuid(), Guid.NewGuid(), cancel.Token));
            else Assert.Equal(CountDecision.Forbidden, (await Counts(owner, authority).AdjustAsync(approver, count.Id!.Value,
                new(count.Version!, "Review"), Guid.NewGuid(), Guid.NewGuid())).Decision);
        }
        await using var verify = Open(_tenant);
        Assert.Equal(10m, (await verify.Balances.SingleAsync()).Quantity);
        Assert.Empty(await verify.Postings.Where(row => row.SourceDocumentId == count.Id).ToArrayAsync());
        Assert.True((await verify.Counts.SingleAsync()).Active);
        Assert.Equal(count.Version, (await verify.Counts.SingleAsync()).Version);
    }

    [Fact]
    public async Task CountReadReviewAndReceiptsRecheckEveryCurrentResourceFieldAndRealCreator()
    {
        var fixture = await OutboundReadyAsync();
        var count = await CreateAndStartCountAsync(fixture.Warehouse, fixture.Location);
        var authority = new CountAuthority();
        await using var owner = Open(_tenant);
        Assert.Equal(CountDecision.Committed, (await Counts(owner, authority).ReviewAsync(Reader, count.Id!.Value)).Decision);
        foreach (var permission in new[] { "wms.counts.read", "wms.counts.review", "wms.warehouses.read", "wms.locations.read",
                     "wms.items.read", "wms.units.read", "wms.containers.read" })
        {
            authority.DeniedPermission = permission;
            Assert.Equal(CountDecision.Forbidden, (await Counts(owner, authority).ReviewAsync(Reader, count.Id.Value)).Decision);
        }
        authority.DeniedPermission = "wms.counts.review";
        Assert.Equal(CountDecision.Committed, (await Counts(owner, authority).ReadAsync(Reader, count.Id.Value)).Decision);
        Assert.Equal(CountDecision.Forbidden, (await Counts(owner, authority).ReadAsync(Reader with { TenantId = _other }, count.Id.Value)).Decision);
        authority.DeniedPermission = null;
        authority.ExpectedCreator = Guid.NewGuid();
        Assert.Equal(CountDecision.Forbidden, (await Counts(owner, authority).ReadAsync(Reader, count.Id.Value)).Decision);
    }

    private async Task<CountResult> CreateAndStartCountAsync(Guid warehouse, Guid location, string code = "COUNT-LOCK")
    {
        await using var owner = Open(_tenant);
        var created = await Counts(owner).CreateAsync(Reader, new(code, warehouse, location), Guid.NewGuid(), Guid.NewGuid());
        Assert.Equal(CountDecision.Committed, created.Decision);
        var started = await Counts(owner).StartAsync(Reader, created.Id!.Value, new(created.Version!), Guid.NewGuid(), Guid.NewGuid());
        Assert.Equal(CountDecision.Committed, started.Decision);
        return started;
    }
    private sealed class CountClock(DateTimeOffset now) : TimeProvider { public override DateTimeOffset GetUtcNow() => now; }
    private sealed class CountAfterSave(Action callback) : SaveChangesInterceptor
    {
        public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData data, int result, CancellationToken ct = default)
        { callback(); return ValueTask.FromResult(result); }
    }
    private sealed class CountAuthority : IMasterDataAuthorization
    {
        internal bool Revoked { get; set; }
        internal string? DeniedPermission { get; set; }
        internal Guid? ExpectedCreator { get; set; }
        public Task<IReadOnlySet<string>> ReadFieldsAsync(MasterDataSubject subject, string permission, Guid creator, CancellationToken ct) =>
            Task.FromResult<IReadOnlySet<string>>(Revoked || permission == DeniedPermission || ExpectedCreator.HasValue && creator != ExpectedCreator
                ? new HashSet<string>() : new HashSet<string>(["Id", "Code", "Name", "WarehouseId", "LocationId", "ItemId", "Batch",
                    "ContainerId", "UnitId", "Precision", "Enabled", "PackId", "SchemaVersion", "Version", "CreatedAt",
                    "State", "Lines", "BaseUnitId", "BatchRequired", "Countable", "Reason", "LineId", "Quantity", "BookQuantity", "Difference"]));
        public Task<MasterDataCreatorAccess> ReadCreatorsAsync(MasterDataSubject subject, string permission,
            IReadOnlyCollection<string> requiredFields, CancellationToken ct) => throw new InvalidOperationException("Explicit count only.");
    }
}
