using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
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
    [InlineData("dispatch")]
    [InlineData("arrival")]
    [InlineData("unknown")]
    public async Task TransferLineActionsRejectEmptyExecutionSelectionBeforeStateMutation(string action)
    {
        var fixture = await TransferReadyAsync(); var created = await CreateTransferAsync(fixture);
        await using var owner = Open(_tenant); var access = Transfers(owner); var id = created.Id!.Value;
        var operation = Guid.NewGuid(); var correlation = Guid.NewGuid();
        var result = action switch
        {
            "dispatch" => await access.DispatchAsync(Reader, id, new(created.Version!, Guid.Empty), operation, correlation, CancellationToken.None),
            "arrival" => await access.ArriveAsync(Reader, id, new(created.Version!, Guid.Empty, Guid.NewGuid(), Guid.NewGuid(),
                Guid.NewGuid(), "1", "Scans"), operation, correlation, CancellationToken.None),
            _ => await access.UnknownAsync(Reader, id, new(created.Version!, Guid.Empty, "Unknown"), operation, correlation, CancellationToken.None)
        };
        Assert.Equal(TransferDecision.Invalid, result.Decision);
        Assert.Single(await owner.TransferFacts.ToArrayAsync()); Assert.Empty(await owner.Reservations.ToArrayAsync());
    }

    [Fact]
    public async Task TransferApprovalRejectsAnotherPositiveContainerAtItsDestination()
    {
        var fixture = await TransferReadyAsync();
        await using (var seed = Open(_tenant))
        {
            var target = await seed.Balances.AsNoTracking().SingleAsync(row => row.Id == fixture.TargetA);
            var container = await ResourceAccess(seed).ContainerAsync(Reader,
                new(Guid.NewGuid(), Guid.NewGuid(), "TRANSFER-OCCUPANT", "Existing occupant", fixture.Warehouse));
            Assert.Equal(StockResourceDecision.Created, container.Decision);
            var dimension = await ResourceAccess(seed).DimensionAsync(Reader,
                new(Guid.NewGuid(), Guid.NewGuid(), fixture.Warehouse, target.LocationId, target.ItemId, "", container.Id!.Value));
            Assert.Equal(StockResourceDecision.Created, dimension.Decision);
            Assert.Equal(StockPostingDecision.Posted, (await new PostStock(seed, _clock).ExecuteAsync(new(Guid.NewGuid(), _actor,
                Guid.NewGuid(), StockPostingKind.Receipt, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), null,
                dimension.Id, 1m, null, dimension.Version))).Decision);
        }
        var created = await CreateTransferAsync(fixture); var detail = await TransferDetailAsync(created.Id!.Value);
        await using var owner = Open(_tenant);
        Assert.Equal(TransferDecision.Blocked, (await Transfers(owner).ApproveAsync(Reader, detail.Id, Approval(detail),
            Guid.NewGuid(), Guid.NewGuid(), CancellationToken.None)).Decision);
        Assert.Empty(await owner.Reservations.ToArrayAsync());
    }

    [Fact]
    public async Task TransferPlanCannotPromiseTwoContainersTheSameDestination()
    {
        var fixture = await TransferReadyAsync(); Guid otherTarget;
        await using (var seed = Open(_tenant))
        {
            var target = await seed.Balances.AsNoTracking().SingleAsync(row => row.Id == fixture.TargetA);
            var source = await seed.Balances.AsNoTracking().SingleAsync(row => row.Id == fixture.SourceB);
            var dimension = await ResourceAccess(seed).DimensionAsync(Reader,
                new(Guid.NewGuid(), Guid.NewGuid(), fixture.Warehouse, target.LocationId, source.ItemId, "", source.ContainerId));
            Assert.Equal(StockResourceDecision.Created, dimension.Decision); otherTarget = dimension.Id!.Value;
        }
        await using var owner = Open(_tenant);
        Assert.Equal(TransferDecision.Invalid, (await Transfers(owner).CreateAsync(Reader,
            new(Guid.NewGuid(), Guid.NewGuid(), "TRANSFER-DUPLICATE-TARGET", fixture.Warehouse,
                [new(fixture.A, fixture.SourceA, fixture.TargetA, "3", 0), new(fixture.B, fixture.SourceB, otherTarget, "1", 0)]),
            CancellationToken.None)).Decision);
        Assert.Empty(await owner.TransferDocuments.ToArrayAsync());
    }

    [Theory]
    [InlineData(LocationSchemaDecision.Rejected, TransferDecision.SchemaRejected)]
    [InlineData(LocationSchemaDecision.Unavailable, TransferDecision.SchemaUnavailable)]
    public async Task TransferSchemaFailureNeverApprovesOrFallsBack(LocationSchemaDecision schema, TransferDecision expected)
    {
        var fixture = await TransferReadyAsync(); var created = await CreateTransferAsync(fixture);
        var detail = await TransferDetailAsync(created.Id!.Value);
        await using var owner = Open(_tenant);
        Assert.Equal(expected, (await Transfers(owner, schema: new Schema(schema)).ApproveAsync(Reader, detail.Id, Approval(detail),
            Guid.NewGuid(), Guid.NewGuid(), CancellationToken.None)).Decision);
        Assert.Empty(await owner.Reservations.ToArrayAsync());
        Assert.Equal(TransferState.Draft, (await owner.TransferDocuments.SingleAsync()).State);
    }

    [Fact]
    public async Task TransferReadsNeverCrossTrustedTenantOrAcceptClientTenantSubstitution()
    {
        var fixture = await TransferReadyAsync(); var created = await CreateTransferAsync(fixture);
        await using var foreign = Open(_other);
        Assert.Equal(TransferReadDecision.NotFound, (await Transfers(foreign).ReadAsync(Reader with { TenantId = _other },
            created.Id!.Value, CancellationToken.None)).Decision);
        await using var own = Open(_tenant);
        Assert.Equal(TransferReadDecision.Invalid, (await Transfers(own).ReadAsync(Reader with { TenantId = _other },
            created.Id.Value, CancellationToken.None)).Decision);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TransferAllUnstartedClosureCancelsDraftOrApprovedWithoutChangingPlan(bool approve)
    {
        var fixture = await TransferReadyAsync(); var created = await CreateTransferAsync(fixture);
        var detail = await TransferDetailAsync(created.Id!.Value);
        if (approve)
        {
            await using var owner = Open(_tenant);
            Assert.Equal(TransferDecision.Committed, (await Transfers(owner).ApproveAsync(Reader, detail.Id, Approval(detail),
                Guid.NewGuid(), Guid.NewGuid(), CancellationToken.None)).Decision);
            detail = await TransferDetailAsync(detail.Id);
        }
        await using (var owner = Open(_tenant))
            Assert.Equal(TransferDecision.Committed, (await Transfers(owner).CloseAsync(Reader, detail.Id,
                new(detail.Version, "Confirmed no dispatch"), Guid.NewGuid(), Guid.NewGuid(), CancellationToken.None)).Decision);
        var closed = await TransferDetailAsync(detail.Id); Assert.Equal(4, closed.State);
        Assert.All(closed.Lines, row => Assert.Equal((4, "0.000000", row.PlannedQuantity),
            (row.ExecutionStatus, row.ReservedQuantity, row.ClosedQuantity)));
        await using var verify = Open(_tenant); Assert.Equal(0m, await verify.Reservations.SumAsync(row => row.RemainingQuantity));
        Assert.Empty(await verify.TransferOutbox.ToArrayAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TransferInTransitAndUnknownNeverCloseSyncOrReleaseProtection(bool unknown)
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
        detail = await TransferDetailAsync(detail.Id);
        if (unknown)
        {
            await using var owner = Open(_tenant);
            Assert.Equal(TransferDecision.Committed, (await Transfers(owner).UnknownAsync(Reader, detail.Id,
                new(detail.Version, fixture.A, "Execution location unresolved"), Guid.NewGuid(), Guid.NewGuid(), CancellationToken.None)).Decision);
            detail = await TransferDetailAsync(detail.Id);
        }
        await using (var owner = Open(_tenant))
        {
            Assert.Equal(TransferDecision.Blocked, (await Transfers(owner).CloseAsync(Reader, detail.Id,
                new(detail.Version, "Unsafe closure"), Guid.NewGuid(), Guid.NewGuid(), CancellationToken.None)).Decision);
            Assert.Equal(TransferDecision.Conflict, (await Transfers(owner).SyncAsync(Reader, detail.Id,
                new(detail.Version, fixture.A), Guid.NewGuid(), Guid.NewGuid(), CancellationToken.None)).Decision);
        }
        await using var verify = Open(_tenant);
        Assert.Equal(4m, await verify.Reservations.SumAsync(row => row.RemainingQuantity));
        Assert.Empty(await verify.Postings.Where(row => row.Kind == StockPostingKind.Transfer).ToArrayAsync());
        Assert.True(await TransferProtection.ConflictsAsync(verify, fixture.Warehouse, [], [detail.Lines[0].ContainerId], CancellationToken.None));
    }

    [Theory]
    [InlineData("quantity")]
    [InlineData("source")]
    [InlineData("target")]
    [InlineData("container")]
    public async Task TransferArrivalRejectsEveryMismatchedExactScan(string field)
    {
        var fixture = await TransferReadyAsync(); var created = await CreateTransferAsync(fixture);
        var detail = await TransferDetailAsync(created.Id!.Value);
        await using (var owner = Open(_tenant))
            await Transfers(owner).ApproveAsync(Reader, detail.Id, Approval(detail), Guid.NewGuid(), Guid.NewGuid(), CancellationToken.None);
        detail = await TransferDetailAsync(detail.Id);
        await using (var owner = Open(_tenant))
            await Transfers(owner).DispatchAsync(Reader, detail.Id, new(detail.Version, fixture.A),
                Guid.NewGuid(), Guid.NewGuid(), CancellationToken.None);
        detail = await TransferDetailAsync(detail.Id); var line = detail.Lines[0];
        var request = new TransferArrivalRequest(detail.Version, fixture.A,
            field == "source" ? Guid.NewGuid() : line.SourceLocationId, field == "target" ? Guid.NewGuid() : line.TargetLocationId,
            field == "container" ? Guid.NewGuid() : line.ContainerId, field == "quantity" ? "2" : "3", "Final scans");
        await using (var owner = Open(_tenant))
            Assert.Equal(TransferDecision.Invalid, (await Transfers(owner).ArriveAsync(Reader, detail.Id, request,
                Guid.NewGuid(), Guid.NewGuid(), CancellationToken.None)).Decision);
        await using var verify = Open(_tenant);
        Assert.Empty(await verify.TransferFacts.Where(row => row.Kind == TransferFactKind.Arrived).ToArrayAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TransferPartialOrMultiplePositiveContainerDimensionsCannotApprove(bool multiple)
    {
        var fixture = await TransferReadyAsync(multiple ? 3m : 10m);
        if (multiple)
        {
            await using var owner = Open(_tenant); var target = await owner.Balances.AsNoTracking().SingleAsync(row => row.Id == fixture.TargetA);
            Assert.Equal(StockPostingDecision.Posted, (await new PostStock(owner, _clock).ExecuteAsync(new(Guid.NewGuid(), _actor,
                Guid.NewGuid(), StockPostingKind.Receipt, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), null, target.Id, 1m, null, target.Version))).Decision);
        }
        var created = await CreateTransferAsync(fixture); var detail = await TransferDetailAsync(created.Id!.Value);
        await using (var owner = Open(_tenant))
            Assert.Equal(TransferDecision.Blocked, (await Transfers(owner).ApproveAsync(Reader, detail.Id, Approval(detail),
                Guid.NewGuid(), Guid.NewGuid(), CancellationToken.None)).Decision);
        await using var verify = Open(_tenant); Assert.Empty(await verify.Reservations.ToArrayAsync());
        Assert.Equal(TransferState.Draft, (await verify.TransferDocuments.SingleAsync()).State);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task TransferConcurrentApprovalChecksWinningOriginalTupleAfterFence(bool sameKey)
    {
        var fixture = await TransferReadyAsync(); var created = await CreateTransferAsync(fixture);
        var detail = await TransferDetailAsync(created.Id!.Value); var request = Approval(detail); var operation = Guid.NewGuid();
        async Task<TransferResult> Approve(Guid key)
        {
            await using var owner = Open(_tenant);
            return await Transfers(owner).ApproveAsync(Reader, detail.Id, request, key, Guid.NewGuid(), CancellationToken.None);
        }
        var results = await Task.WhenAll(Task.Run(() => Approve(operation)),
            Task.Run(() => Approve(sameKey ? operation : Guid.NewGuid())));
        Assert.Single(results, row => row.Decision == TransferDecision.Committed);
        Assert.Single(results, row => row.Decision == (sameKey ? TransferDecision.Replayed : TransferDecision.Conflict));
        await using var verify = Open(_tenant);
        Assert.Equal(2, await verify.Reservations.CountAsync()); Assert.Equal(2, await verify.TransferFacts.CountAsync());
    }

    [Fact]
    public async Task TransferPostSaveRevocationRollsBackAllReservationsAndProgress()
    {
        var fixture = await TransferReadyAsync(); var created = await CreateTransferAsync(fixture);
        var detail = await TransferDetailAsync(created.Id!.Value); var authority = new TransferAuthority();
        await using (var owner = Open(_tenant, new RevokeTransferAfterSave(authority)))
            Assert.Equal(TransferDecision.Forbidden, (await Transfers(owner, authority).ApproveAsync(Reader, detail.Id,
                Approval(detail), Guid.NewGuid(), Guid.NewGuid(), CancellationToken.None)).Decision);
        await using var verify = Open(_tenant); Assert.Empty(await verify.Reservations.ToArrayAsync());
        Assert.Single(await verify.TransferFacts.ToArrayAsync()); Assert.Equal(TransferState.Draft, (await verify.TransferDocuments.SingleAsync()).State);
    }

    [Theory]
    [InlineData("wms.warehouses.read")]
    [InlineData("wms.locations.read")]
    [InlineData("wms.items.read")]
    [InlineData("wms.units.read")]
    [InlineData("wms.containers.read")]
    public async Task TransferChecksRealCreatorOfEveryResourceInsteadOfSubstitutingDocumentCreator(string permission)
    {
        var fixture = await TransferReadyAsync();
        TransferResult created;
        var documentCreator = Reader with { ActorId = Guid.NewGuid() };
        await using (var creation = Open(_tenant))
            created = await Transfers(creation).CreateAsync(documentCreator, new(Guid.NewGuid(), Guid.NewGuid(), "TRANSFER-MULTI-CREATOR",
                fixture.Warehouse, [new(fixture.A, fixture.SourceA, fixture.TargetA, "3", 0),
                    new(fixture.B, fixture.SourceB, fixture.TargetB, "1", 0)]), CancellationToken.None);
        Assert.Equal(TransferDecision.Committed, created.Decision);
        var detail = await TransferDetailAsync(created.Id!.Value);
        Guid creator;
        await using (var verify = Open(_tenant))
            creator = permission switch
            {
                "wms.warehouses.read" => (await verify.MasterDataChanges.SingleAsync(row => row.WarehouseId == fixture.Warehouse && row.LocationId == null)).ActorId,
                "wms.locations.read" => (await verify.MasterDataChanges.SingleAsync(row => row.LocationId == detail.Lines[0].TargetLocationId)).ActorId,
                "wms.items.read" => (await verify.StockItems.SingleAsync()).ActorId,
                "wms.units.read" => (await verify.StockUnits.SingleAsync()).ActorId,
                _ => (await verify.StockContainers.SingleAsync(row => row.Id == detail.Lines[0].ContainerId)).ActorId
            };
        Assert.NotEqual(documentCreator.ActorId, creator);
        var authority = new TransferAuthority { Permit = (requested, actualCreator) => requested != permission || actualCreator != creator };
        await using var owner = Open(_tenant);
        Assert.Equal(TransferReadDecision.Forbidden, (await Transfers(owner, authority).ReadAsync(Reader, detail.Id, CancellationToken.None)).Decision);
        Assert.Equal(TransferDecision.Forbidden, (await Transfers(owner, authority).ApproveAsync(Reader, detail.Id,
            Approval(detail), Guid.NewGuid(), Guid.NewGuid(), CancellationToken.None)).Decision);
    }

    [Fact]
    public async Task TransferUnsupportedTransportNeverOffersHumanDispatchOrArrival()
    {
        var fixture = await TransferReadyAsync(); var created = await CreateTransferAsync(fixture, branch: 1);
        var detail = await TransferDetailAsync(created.Id!.Value);
        await using var owner = Open(_tenant);
        Assert.Equal(TransferDecision.Committed, (await Transfers(owner).ApproveAsync(Reader, detail.Id,
            Approval(detail), Guid.NewGuid(), Guid.NewGuid(), CancellationToken.None)).Decision);
        detail = await TransferDetailAsync(detail.Id);
        Assert.Equal(TransferDecision.Unsupported, (await Transfers(owner).DispatchAsync(Reader, detail.Id,
            new(detail.Version, fixture.A), Guid.NewGuid(), Guid.NewGuid(), CancellationToken.None)).Decision);
        var line = detail.Lines[0];
        Assert.Equal(TransferDecision.Unsupported, (await Transfers(owner).ArriveAsync(Reader, detail.Id,
            new(detail.Version, line.Id, line.SourceLocationId, line.TargetLocationId, line.ContainerId, "3", "No fake device"),
            Guid.NewGuid(), Guid.NewGuid(), CancellationToken.None)).Decision);
    }

    private sealed class RevokeTransferAfterSave(TransferAuthority authority) : SaveChangesInterceptor
    {
        public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result,
            CancellationToken cancellationToken = default)
        { authority.Revoked = true; return ValueTask.FromResult(result); }
    }
}
