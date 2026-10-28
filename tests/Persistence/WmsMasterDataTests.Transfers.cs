using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Wes.Wms.Domain;
using Wes.Wms.Features.Inbound;
using Wes.Wms.Features.MasterData;
using Wes.Wms.Features.Transfers;
using Xunit;

namespace Wes.Persistence.Tests;

public sealed partial class WmsMasterDataTests
{
    [Fact]
    [Trait("CaseId", "TC-T11-TRANSFER-03")]
    public async Task TransferAThreeRemainsPostedWhenBOneArrivalPostingFailsAndRestartSyncsExactlyOnce()
    {
        var fixture = await TransferReadyAsync();
        var created = await CreateTransferAsync(fixture);
        var detail = await TransferDetailAsync(created.Id!.Value);
        await using (var owner = Open(_tenant))
            Assert.Equal(TransferDecision.Committed, (await Transfers(owner).ApproveAsync(Reader, detail.Id, Approval(detail),
                Guid.NewGuid(), Guid.NewGuid(), CancellationToken.None)).Decision);
        async Task<TransferResult> Arrive(Guid lineId, bool fail)
        {
            var before = await TransferDetailAsync(detail.Id);
            await using (var owner = Open(_tenant))
                Assert.Equal(TransferDecision.Committed, (await Transfers(owner).DispatchAsync(Reader, detail.Id,
                    new(before.Version, lineId), Guid.NewGuid(), Guid.NewGuid(), CancellationToken.None)).Decision);
            before = await TransferDetailAsync(detail.Id);
            var line = before.Lines.Single(row => row.Id == lineId);
            var request = new TransferArrivalRequest(before.Version, lineId, line.SourceLocationId, line.TargetLocationId,
                line.ContainerId, line.PlannedQuantity, "Exact final scans");
            var operation = Guid.NewGuid();
            TransferResult arrival;
            await using (var owner = fail ? Open(_tenant, new FailTransferPosting()) : Open(_tenant))
                arrival = await Transfers(owner).ArriveAsync(Reader, detail.Id, request, operation, Guid.NewGuid(), CancellationToken.None);
            Assert.Equal(TransferDecision.Committed, arrival.Decision);
            await using (var restarted = Open(_tenant))
                Assert.Equal(arrival with { Decision = TransferDecision.Replayed }, await Transfers(restarted).ArriveAsync(Reader,
                    detail.Id, request, operation, Guid.NewGuid(), CancellationToken.None));
            return arrival;
        }
        await Arrive(fixture.A, false);
        var afterA = await TransferDetailAsync(detail.Id);
        Assert.Equal(2, afterA.State);
        Assert.Equal("3.000000", afterA.Lines.Single(row => row.Id == fixture.A).MovedQuantity);
        await Arrive(fixture.B, true);
        var pending = await TransferDetailAsync(detail.Id);
        Assert.Equal(2, pending.State);
        Assert.Equal((2, 1, "1.000000"), (pending.Lines[1].ExecutionStatus, pending.Lines[1].PostingStatus, pending.Lines[1].ReservedQuantity));
        await using (var verify = Open(_tenant))
        {
            Assert.Single(await verify.Postings.Where(row => row.Kind == StockPostingKind.Transfer).ToArrayAsync());
            Assert.Equal(3m, (await verify.Balances.SingleAsync(row => row.Id == fixture.TargetA)).Quantity);
            Assert.Equal(1m, (await verify.Balances.SingleAsync(row => row.Id == fixture.SourceB)).Quantity);
            Assert.True(await TransferProtection.ConflictsAsync(verify, fixture.Warehouse, [], [pending.Lines[1].ContainerId], CancellationToken.None));
        }
        await using (var owner = Open(_tenant))
            Assert.Equal(TransferDecision.Blocked, (await Transfers(owner).CloseAsync(Reader, detail.Id,
                new(pending.Version, "Cannot close arrived"), Guid.NewGuid(), Guid.NewGuid(), CancellationToken.None)).Decision);
        var sync = new TransferLineRequest(pending.Version, fixture.B); var syncOperation = Guid.NewGuid();
        await using (var owner = Open(_tenant))
        {
            var synced = await Transfers(owner).SyncAsync(Reader, detail.Id, sync, syncOperation, Guid.NewGuid(), CancellationToken.None);
            Assert.Equal(TransferDecision.Committed, synced.Decision);
            Assert.Equal(synced with { Decision = TransferDecision.Replayed }, await Transfers(owner).SyncAsync(Reader, detail.Id,
                sync, syncOperation, Guid.NewGuid(), CancellationToken.None));
        }
        var completed = await TransferDetailAsync(detail.Id);
        Assert.Equal(3, completed.State);
        Assert.All(completed.Lines, line => Assert.Equal(2, line.PostingStatus));
        await using var final = Open(_tenant);
        Assert.Equal(2, await final.Postings.CountAsync(row => row.Kind == StockPostingKind.Transfer));
        Assert.Equal(2, await final.TransferOutbox.CountAsync());
        Assert.Equal(0m, await final.Reservations.SumAsync(row => row.RemainingQuantity));
        Assert.False(await TransferProtection.ConflictsAsync(final, fixture.Warehouse,
            completed.Lines.Select(row => row.TargetLocationId), completed.Lines.Select(row => row.ContainerId), CancellationToken.None));
    }

    [Fact]
    [Trait("CaseId", "TC-T11-TRANSFER-01")]
    public async Task TransferApprovalInsufficientSecondWholeContainerLeavesDraftAndNoReservations()
    {
        var fixture = await TransferReadyAsync(3m, 2m);
        await using var owner = Open(_tenant);
        var access = Transfers(owner);
        var created = await access.CreateAsync(Reader,
            new(Guid.NewGuid(), Guid.NewGuid(), "TRANSFER-ATOMIC", fixture.Warehouse,
                [new(fixture.A, fixture.SourceA, fixture.TargetA, "3", 0),
                    new(fixture.B, fixture.SourceB, fixture.TargetB, "3", 0)]), CancellationToken.None);
        Assert.Equal(TransferDecision.Committed, created.Decision);
        var detail = (await access.ReadAsync(Reader, created.Id!.Value, CancellationToken.None)).Record!;
        var result = await access.ApproveAsync(Reader, detail.Id, Approval(detail), Guid.NewGuid(), Guid.NewGuid(), CancellationToken.None);
        Assert.Contains(result.Decision, new[] { TransferDecision.Blocked, TransferDecision.Conflict });
        var after = (await access.ReadAsync(Reader, detail.Id, CancellationToken.None)).Record!;
        Assert.Equal(0, after.State);
        await using var verify = Open(_tenant);
        Assert.Empty(await verify.Reservations.ToArrayAsync());
    }

    private TransferAccess Transfers(Wes.Wms.Infrastructure.Persistence.WmsDbContext database,
        IMasterDataAuthorization? authority = null, IWarehouseLocationSchema? schema = null) =>
        new(database, authority ?? new TransferAuthority(), schema ?? new Schema(LocationSchemaDecision.Valid), _clock,
            NullLogger<TransferAccess>.Instance);

    private async Task<TransferResult> CreateTransferAsync(TransferFixture fixture, string code = "TRANSFER-BASE", int branch = 0)
    {
        await using var owner = Open(_tenant);
        var result = await Transfers(owner).CreateAsync(Reader, new(Guid.NewGuid(), Guid.NewGuid(), code, fixture.Warehouse,
            [new(fixture.A, fixture.SourceA, fixture.TargetA, "3", branch), new(fixture.B, fixture.SourceB, fixture.TargetB, "1", branch)]),
            CancellationToken.None);
        Assert.Equal(TransferDecision.Committed, result.Decision);
        return result;
    }

    private async Task<TransferRecord> TransferDetailAsync(Guid id)
    {
        await using var owner = Open(_tenant);
        var result = await Transfers(owner).ReadAsync(Reader, id, CancellationToken.None);
        Assert.Equal(TransferReadDecision.Success, result.Decision);
        return result.Record!;
    }

    private sealed class FailTransferPosting : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (eventData.Context!.ChangeTracker.Entries<StockPosting>().Any(row => row.State == EntityState.Added
                && row.Entity.Kind == StockPostingKind.Transfer)) throw new DbUpdateException("Injected transfer posting failure.");
            return ValueTask.FromResult(result);
        }
    }

    private static TransferApprovalRequest Approval(TransferRecord record) => new(record.Version,
        record.Lines.SelectMany(row => new[] { new TransferBalanceVersionRequest(row.SourceBalanceId, row.SourceVersion),
            new TransferBalanceVersionRequest(row.TargetBalanceId, row.TargetVersion) }).ToArray());

    private async Task<TransferFixture> TransferReadyAsync(decimal a = 3m, decimal b = 1m)
    {
        var registration = await LegalDimensionReadyAsync();
        async Task<Guid> Location(string code)
        {
            MasterDataResult location;
            await using (var owner = Open(_tenant))
                location = await new CreateMasterData(owner, _clock, new Schema(LocationSchemaDecision.Valid)).ExecuteAsync(
                    Command(code) with { WarehouseId = registration.WarehouseId, PackId = Guid.NewGuid(), SchemaVersion = "v1" });
            Assert.Equal(MasterDataDecision.Created, location.Decision);
            await using var state = Open(_tenant);
            Assert.Equal(LocationStateDecision.Changed, (await new LocationStateAccess(state, new TransferAuthority(),
                new Schema(LocationSchemaDecision.Valid), _clock, NullLogger<LocationStateAccess>.Instance).ChangeAsync(Reader,
                location.Id!.Value, new(Guid.NewGuid(), location.Version!, true, "Transfer fixture ready"))).Decision);
            return location.Id.Value;
        }
        var targetLocationA = await Location("TRANSFER-TARGET-A");
        var sourceLocationB = await Location("TRANSFER-SOURCE-B");
        var targetLocationB = await Location("TRANSFER-TARGET-B");
        Guid sourceA; Guid targetA; Guid sourceB; Guid targetB;
        async Task<(Guid Source, Guid Target)> Seed(Guid container, decimal quantity, Guid from, Guid to)
        {
            Guid source; Guid target;
            await using (var owner = Open(_tenant))
            {
                var registered = await ResourceAccess(owner).DimensionAsync(Reader, registration with { ContainerId = container, LocationId = from });
                Assert.Equal(StockResourceDecision.Created, registered.Decision);
                source = registered.Id!.Value;
                var destination = await ResourceAccess(owner).DimensionAsync(Reader,
                    registration with { OperationId = Guid.NewGuid(), ContainerId = container, LocationId = to });
                Assert.Equal(StockResourceDecision.Created, destination.Decision);
                target = destination.Id!.Value;
            }
            await using var posting = Open(_tenant);
            var balance = await posting.Balances.AsNoTracking().SingleAsync(row => row.Id == source);
            var result = await new Wes.Wms.Features.Ledger.PostStock(posting, _clock).ExecuteAsync(
                new(Guid.NewGuid(), Reader.ActorId, Guid.NewGuid(), StockPostingKind.Receipt, Guid.NewGuid(),
                    Guid.NewGuid(), Guid.NewGuid(), null, source, quantity, null, balance.Version));
            Assert.Equal(Wes.Wms.Features.Ledger.StockPostingDecision.Posted, result.Decision);
            return (source, target);
        }
        (sourceA, targetA) = await Seed(registration.ContainerId, a, registration.LocationId, targetLocationA);
        Guid secondContainer;
        await using (var owner = Open(_tenant))
        {
            var second = await ResourceAccess(owner).ContainerAsync(Reader,
                new(Guid.NewGuid(), Guid.NewGuid(), "TRANSFER-B", "Second transfer container", registration.WarehouseId));
            Assert.Equal(StockResourceDecision.Created, second.Decision);
            secondContainer = second.Id!.Value;
        }
        registration = registration with { OperationId = Guid.NewGuid() };
        (sourceB, targetB) = await Seed(secondContainer, b, sourceLocationB, targetLocationB);
        return new(registration.WarehouseId, Guid.NewGuid(), Guid.NewGuid(), sourceA, targetA, sourceB, targetB);
    }

    private sealed record TransferFixture(Guid Warehouse, Guid A, Guid B, Guid SourceA, Guid TargetA, Guid SourceB, Guid TargetB);

    private sealed class TransferAuthority : IMasterDataAuthorization
    {
        internal bool Revoked { get; set; }
        internal Func<string, Guid, bool>? Permit { get; set; }
        private static readonly HashSet<string> Fields =
        [
            "Id", "Code", "Name", "WarehouseId", "LocationId", "ItemId", "Batch", "ContainerId", "UnitId",
            "Precision", "Enabled", "PackId", "SchemaVersion", "Version", "CreatedAt", "State", "Lines",
            "BaseUnitId", "BatchRequired", "Countable", "Reason", "LineId", "Quantity", "SourceLocationId", "TargetLocationId"
        ];
        public Task<IReadOnlySet<string>> ReadFieldsAsync(MasterDataSubject subject, string permission, Guid creator,
            CancellationToken ct) => Task.FromResult<IReadOnlySet<string>>(
                Revoked || Permit?.Invoke(permission, creator) == false ? new HashSet<string>() : Fields);
        public Task<MasterDataCreatorAccess> ReadCreatorsAsync(MasterDataSubject subject, string permission,
            IReadOnlyCollection<string> requiredFields, CancellationToken ct) => throw new InvalidOperationException();
    }
}
