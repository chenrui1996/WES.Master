using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using System.Data;
using System.Data.Common;
using Microsoft.Extensions.Logging.Abstractions;
using Wes.Wms.Domain;
using Wes.Wms.Features.Inbound;
using Wes.Wms.Features.MasterData;
using Wes.Wms.Features.Outbound;
using Xunit;

namespace Wes.Persistence.Tests;

public sealed partial class WmsMasterDataTests
{
    [Fact]
    [Trait("CaseId", "TC-T10-BASIC-02")]
    public async Task OutboundPickReleaseShipmentAndPartialClosePreserveExactInventoryAndOriginalReceipts()
    {
        var fixture = await OutboundReadyAsync();
        var line = Guid.NewGuid();
        var create = new OutboundDraftRequest(Guid.NewGuid(), Guid.NewGuid(), "OUT-BASE", fixture.Warehouse,
            [new(line, fixture.Balance, "7", 0)]);
        OutboundResult created;
        await using (var owner = Open(_tenant))
        {
            created = await OutboundAccess(owner).CreateAsync(Reader, create, CancellationToken.None);
            Assert.Equal(OutboundDecision.Committed, created.Decision);
            Assert.Equal(created with { Decision = OutboundDecision.Replayed },
                await OutboundAccess(owner).CreateAsync(Reader, create with { CorrelationId = Guid.NewGuid() }, CancellationToken.None));
        }

        OutboundResult approved;
        await using (var owner = Open(_tenant))
            approved = await OutboundAccess(owner).ApproveAsync(Reader, created.Id!.Value,
                new(created.Version!, [new(fixture.Balance, fixture.BalanceVersion)]), Guid.NewGuid(), Guid.NewGuid(), CancellationToken.None);
        Assert.Equal(OutboundDecision.Committed, approved.Decision);
        await AssertOutboundStateAsync(created.Id.Value, OutboundOrderState.Approved, 10m, 7m);

        var pick = new OutboundPickRequest(approved.Version!, line, "5", "2", "Short pick");
        OutboundResult picked;
        await using (var owner = Open(_tenant))
        {
            picked = await OutboundAccess(owner).PickAsync(Reader, created.Id.Value, pick, Guid.NewGuid(), Guid.NewGuid(), CancellationToken.None);
            Assert.Equal(OutboundDecision.Committed, picked.Decision);
        }
        await AssertOutboundStateAsync(created.Id.Value, OutboundOrderState.Executing, 10m, 7m);

        var reviewer = new MasterDataSubject(_tenant, Guid.NewGuid(), Guid.NewGuid());
        OutboundResult released;
        await using (var owner = Open(_tenant))
            released = await OutboundAccess(owner).ReleaseShortageAsync(reviewer, created.Id.Value,
                new(picked.Version!, line, "2", "Confirmed unexecuted"), Guid.NewGuid(), Guid.NewGuid(), CancellationToken.None);
        Assert.Equal(OutboundDecision.Committed, released.Decision);
        await AssertOutboundStateAsync(created.Id.Value, OutboundOrderState.Executing, 10m, 5m);

        OutboundResult shipped;
        var ship = new OutboundShipmentRequest(released.Version!, line, "5");
        var shipOperation = Guid.NewGuid();
        var shipCorrelation = Guid.NewGuid();
        await using (var owner = Open(_tenant))
        {
            shipped = await OutboundAccess(owner).ShipAsync(Reader, created.Id.Value, ship, shipOperation, shipCorrelation, CancellationToken.None);
            Assert.Equal(OutboundDecision.Committed, shipped.Decision);
            Assert.Equal(shipped with { Decision = OutboundDecision.Replayed },
                await OutboundAccess(owner).ShipAsync(Reader, created.Id.Value, ship, shipOperation, shipCorrelation, CancellationToken.None));
        }
        await AssertOutboundStateAsync(created.Id.Value, OutboundOrderState.Executing, 5m, 0m);

        OutboundResult closed;
        await using (var owner = Open(_tenant))
            closed = await OutboundAccess(owner).CloseRemainderAsync(Reader, created.Id.Value,
                new(shipped.Version!, "Close unshipped remainder"), Guid.NewGuid(), Guid.NewGuid(), CancellationToken.None);
        Assert.Equal(OutboundDecision.Committed, closed.Decision);
        await using var verify = Open(_tenant);
        var detail = await OutboundAccess(verify).ReadAsync(Reader, created.Id.Value, CancellationToken.None);
        Assert.Equal(OutboundReadDecision.Success, detail.Decision);
        Assert.Equal((int)OutboundOrderState.ClosedPartial, detail.Record!.State);
        var actual = Assert.Single(detail.Record.Lines);
        Assert.Equal(("7.000000", "0.000000", "5.000000", "2.000000", "2.000000", "5.000000"),
            (actual.PlannedQuantity, actual.ReservedQuantity, actual.PickedQuantity, actual.ShortageQuantity,
                actual.ReleasedQuantity, actual.ShippedQuantity));
        Assert.Equal(5m, (await verify.Balances.SingleAsync(row => row.Id == fixture.Balance)).Quantity);
        Assert.Single(await verify.Postings.Where(row => row.Kind == StockPostingKind.Shipment).ToArrayAsync());
        Assert.Equal(6, await verify.OutboundFacts.CountAsync(row => row.OrderId == created.Id));
    }

    [Fact]
    [Trait("CaseId", "TC-T10-BASIC-01")]
    public async Task OutboundApprovalInsufficientSecondLineLeavesNoPartialReservation()
    {
        var fixture = await OutboundReadyAsync();
        Guid secondBalance;
        string secondBalanceVersion;
        await using (var owner = Open(_tenant))
        {
            var container = await ResourceAccess(owner).ContainerAsync(Reader,
                new(Guid.NewGuid(), Guid.NewGuid(), "PALLET-2", "Second pallet", fixture.Warehouse));
            Assert.Equal(StockResourceDecision.Created, container.Decision);
            owner.ChangeTracker.Clear();
            var item = await owner.StockItems.AsNoTracking().SingleAsync();
            var dimension = await ResourceAccess(owner).DimensionAsync(Reader,
                new(Guid.NewGuid(), Guid.NewGuid(), fixture.Warehouse, fixture.Location, item.Id, "", container.Id!.Value));
            Assert.Equal(StockResourceDecision.Created, dimension.Decision);
            secondBalance = dimension.Id!.Value;
            secondBalanceVersion = dimension.Version!;
        }
        var firstLine = Guid.NewGuid(); var secondLine = Guid.NewGuid();
        var created = await CreateOutboundAsync(fixture.Warehouse, "OUT-ATOMIC",
            [new(firstLine, fixture.Balance, "7", 0), new(secondLine, secondBalance, "7", 0)]);
        OutboundResult result;
        await using (var owner = Open(_tenant))
            result = await OutboundAccess(owner).ApproveAsync(Reader, created.Id!.Value,
                new(created.Version!, [new(fixture.Balance, fixture.BalanceVersion), new(secondBalance, secondBalanceVersion)]),
                Guid.NewGuid(), Guid.NewGuid(), CancellationToken.None);
        Assert.Equal(OutboundDecision.Blocked, result.Decision);
        await using var verify = Open(_tenant);
        Assert.Empty(await verify.Reservations.ToArrayAsync());
        Assert.Equal(OutboundOrderState.Draft, (await verify.OutboundDocuments.SingleAsync()).State);
        Assert.Equal(10m, (await verify.Balances.SingleAsync(row => row.Id == fixture.Balance)).Quantity);
    }

    [Fact]
    [Trait("CaseId", "TC-T10-BASIC-01")]
    public async Task ConcurrentOutboundApprovalsCompeteForTheSameQtyTenBalanceWithoutOverReservation()
    {
        var fixture = await OutboundReadyAsync();
        var first = await CreateOutboundAsync(fixture.Warehouse, "OUT-COMPETE-1",
            [new(Guid.NewGuid(), fixture.Balance, "7", 0)]);
        var second = await CreateOutboundAsync(fixture.Warehouse, "OUT-COMPETE-2",
            [new(Guid.NewGuid(), fixture.Balance, "7", 0)]);
        async Task<OutboundResult> Approve(OutboundResult order)
        {
            await using var owner = Open(_tenant);
            return await OutboundAccess(owner).ApproveAsync(Reader, order.Id!.Value,
                new(order.Version!, [new(fixture.Balance, fixture.BalanceVersion)]), Guid.NewGuid(), Guid.NewGuid(), CancellationToken.None);
        }
        var results = await Task.WhenAll(Task.Run(() => Approve(first)), Task.Run(() => Approve(second)));
        Assert.Single(results, row => row.Decision == OutboundDecision.Committed);
        Assert.Single(results, row => row.Decision is OutboundDecision.Conflict or OutboundDecision.Blocked);
        await using var verify = Open(_tenant);
        Assert.Equal(10m, (await verify.Balances.SingleAsync(row => row.Id == fixture.Balance)).Quantity);
        Assert.Single(await verify.Reservations.Where(row => row.BalanceId == fixture.Balance).ToArrayAsync());
        Assert.Equal(7m, await verify.Reservations.Where(row => row.BalanceId == fixture.Balance)
            .SumAsync(row => row.RemainingQuantity));
    }

    [Fact]
    [Trait("CaseId", "TC-T10-BASIC-05")]
    public async Task RequiresTransportOutboundCannotBeManuallyShippedOrCanceledAfterPicking()
    {
        var fixture = await OutboundReadyAsync();
        var line = Guid.NewGuid();
        var created = await CreateOutboundAsync(fixture.Warehouse, "OUT-TRANSPORT",
            [new(line, fixture.Balance, "7", 1)]);
        OutboundResult approved;
        await using (var owner = Open(_tenant))
            approved = await OutboundAccess(owner).ApproveAsync(Reader, created.Id!.Value,
                new(created.Version!, [new(fixture.Balance, fixture.BalanceVersion)]), Guid.NewGuid(), Guid.NewGuid(), CancellationToken.None);
        var pickedRequest = new OutboundPickRequest(approved.Version!, line, "5", "2", "Short pick");
        OutboundResult picked;
        await using (var owner = Open(_tenant))
            picked = await OutboundAccess(owner).PickAsync(Reader, created.Id.Value, pickedRequest, Guid.NewGuid(), Guid.NewGuid(), CancellationToken.None);
        await using (var owner = Open(_tenant))
        {
            Assert.Equal(OutboundDecision.Unsupported, (await OutboundAccess(owner).ShipAsync(Reader, created.Id.Value,
                new(picked.Version!, line, "5"), Guid.NewGuid(), Guid.NewGuid(), CancellationToken.None)).Decision);
            Assert.Equal(OutboundDecision.Conflict, (await OutboundAccess(owner).CancelAsync(Reader, created.Id.Value,
                new(picked.Version!, "No longer unexecuted"), Guid.NewGuid(), Guid.NewGuid(), CancellationToken.None)).Decision);
        }
        await using var verify = Open(_tenant);
        Assert.Empty(await verify.Postings.Where(row => row.Kind == StockPostingKind.Shipment).ToArrayAsync());
        Assert.Equal(10m, (await verify.Balances.SingleAsync(row => row.Id == fixture.Balance)).Quantity);
    }

    [Fact]
    [Trait("CaseId", "TC-T10-BASIC-03")]
    public async Task OutboundRejectsInvalidPrecisionDuplicateBalancesAndForeignTenantSubject()
    {
        var fixture = await OutboundReadyAsync();
        await using var owner = Open(_tenant);
        var access = OutboundAccess(owner);
        var duplicate = new OutboundDraftRequest(Guid.NewGuid(), Guid.NewGuid(), "OUT-DUPLICATE", fixture.Warehouse,
            [new(Guid.NewGuid(), fixture.Balance, "1", 0), new(Guid.NewGuid(), fixture.Balance, "1", 0)]);
        Assert.Equal(OutboundDecision.Invalid, (await access.CreateAsync(Reader, duplicate, CancellationToken.None)).Decision);
        var precision = new OutboundDraftRequest(Guid.NewGuid(), Guid.NewGuid(), "OUT-PRECISION", fixture.Warehouse,
            [new(Guid.NewGuid(), fixture.Balance, "1.0001", 0)]);
        Assert.Equal(OutboundDecision.Invalid, (await access.CreateAsync(Reader, precision, CancellationToken.None)).Decision);
        var exponent = new OutboundDraftRequest(Guid.NewGuid(), Guid.NewGuid(), "OUT-EXPONENT", fixture.Warehouse,
            [new(Guid.NewGuid(), fixture.Balance, "1e0", 0)]);
        Assert.Equal(OutboundDecision.Invalid, (await access.CreateAsync(Reader, exponent, CancellationToken.None)).Decision);
        var foreignTenant = Reader with { TenantId = _other };
        var foreign = new OutboundDraftRequest(Guid.NewGuid(), Guid.NewGuid(), "OUT-FOREIGN", fixture.Warehouse,
            [new(Guid.NewGuid(), fixture.Balance, "1", 0)]);
        Assert.Equal(OutboundDecision.Invalid, (await access.CreateAsync(foreignTenant, foreign, CancellationToken.None)).Decision);
        Assert.Empty(await owner.OutboundDocuments.ToArrayAsync());
        Assert.Empty(await owner.OutboundFacts.ToArrayAsync());
    }

    [Fact]
    [Trait("CaseId", "TC-T10-BASIC-04")]
    public async Task OutboundCreateFailureAfterSaveRollsBackAndAllowsOriginalKeyRetry()
    {
        var fixture = await OutboundReadyAsync();
        var request = new OutboundDraftRequest(Guid.NewGuid(), Guid.NewGuid(), "OUT-RETRY", fixture.Warehouse,
            [new(Guid.NewGuid(), fixture.Balance, "7", 0)]);
        var fault = new AfterSaveFault(false, new CancellationTokenSource());
        await using (var owner = Open(_tenant, fault))
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                OutboundAccess(owner).CreateAsync(Reader, request, CancellationToken.None));
        Assert.True(fault.Reached);
        await using (var verify = Open(_tenant))
        {
            Assert.Empty(await verify.OutboundDocuments.ToArrayAsync());
            Assert.Empty(await verify.OutboundFacts.ToArrayAsync());
        }
        await using var retry = Open(_tenant);
        Assert.Equal(OutboundDecision.Committed,
            (await OutboundAccess(retry).CreateAsync(Reader, request, CancellationToken.None)).Decision);
    }

    [Fact]
    [Trait("CaseId", "TC-T10-BASIC-04")]
    public async Task OutboundCreateRechecksCurrentPermissionAfterSaveAndRollsBackOnRevocation()
    {
        var fixture = await OutboundReadyAsync();
        var authority = new OutboundAuthority();
        var request = new OutboundDraftRequest(Guid.NewGuid(), Guid.NewGuid(), "OUT-REVOKED", fixture.Warehouse,
            [new(Guid.NewGuid(), fixture.Balance, "7", 0)]);
        await using (var owner = Open(_tenant, new RevokeAfterSave(authority)))
            Assert.Equal(OutboundDecision.Forbidden, (await OutboundAccess(owner, authority)
                .CreateAsync(Reader, request, CancellationToken.None)).Decision);
        await using var verify = Open(_tenant);
        Assert.Empty(await verify.OutboundDocuments.ToArrayAsync());
        Assert.Empty(await verify.OutboundFacts.ToArrayAsync());
    }

    [Theory]
    [InlineData(ReservationAction.MarkInTransit)]
    [InlineData(ReservationAction.MarkUnknown)]
    [Trait("CaseId", "TC-T10-BASIC-06")]
    public async Task OutboundShortageReleaseCannotClearInTransitOrUnknownReservation(ReservationAction action)
    {
        var fixture = await OutboundReadyAsync();
        var line = Guid.NewGuid();
        var created = await CreateOutboundAsync(fixture.Warehouse, $"OUT-PROTECTED-{action}",
            [new(line, fixture.Balance, "7", 0)]);
        OutboundResult approved;
        await using (var owner = Open(_tenant))
            approved = await OutboundAccess(owner).ApproveAsync(Reader, created.Id!.Value,
                new(created.Version!, [new(fixture.Balance, fixture.BalanceVersion)]), Guid.NewGuid(), Guid.NewGuid(),
                CancellationToken.None);
        var picked = new OutboundPickRequest(approved.Version!, line, "5", "2", "Short pick");
        OutboundResult pickedResult;
        await using (var owner = Open(_tenant))
            pickedResult = await OutboundAccess(owner).PickAsync(Reader, created.Id.Value, picked, Guid.NewGuid(), Guid.NewGuid(),
                CancellationToken.None);
        await using (var owner = Open(_tenant))
        {
            await using var transaction = await owner.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted);
            var reservation = await owner.Reservations.SingleAsync(row => row.SourceDocumentId == created.Id);
            var before = reservation.RemainingQuantity;
            var beforeKind = reservation.Kind;
            Assert.True(reservation.TryChange(action, 0m));
            owner.ReservationChanges.Add(new(_tenant, Guid.NewGuid(), Reader.ActorId, Guid.NewGuid(), action,
                reservation, 0m, before, beforeKind, _clock.GetUtcNow()));
            (await owner.Balances.SingleAsync(row => row.Id == fixture.Balance)).TouchReservationFence();
            await owner.SaveChangesAsync();
            await transaction.CommitAsync();
        }
        var reviewer = new MasterDataSubject(_tenant, Guid.NewGuid(), Guid.NewGuid());
        await using (var owner = Open(_tenant))
            Assert.Equal(OutboundDecision.Blocked, (await OutboundAccess(owner).ReleaseShortageAsync(reviewer,
                created.Id.Value, new(pickedResult.Version!, line, "2", "Confirm release"), Guid.NewGuid(), Guid.NewGuid(),
                CancellationToken.None)).Decision);
        await using var verify = Open(_tenant);
        Assert.Equal(10m, (await verify.Balances.SingleAsync(row => row.Id == fixture.Balance)).Quantity);
        var retained = await verify.Reservations.SingleAsync(row => row.SourceDocumentId == created.Id);
        Assert.Equal(7m, retained.RemainingQuantity);
        Assert.True((retained.Kind & (action == ReservationAction.MarkInTransit
            ? StockProtectionKind.InTransit : StockProtectionKind.Unknown)) != 0);
    }

    private OutboundAccess OutboundAccess(Wes.Wms.Infrastructure.Persistence.WmsDbContext owner,
        IMasterDataAuthorization? authority = null, IWarehouseLocationSchema? schema = null) =>
        new(owner, authority ?? new OutboundAuthority(), schema ?? new Schema(LocationSchemaDecision.Valid),
            _clock, NullLogger<OutboundAccess>.Instance);

    [Theory]
    [InlineData(ReservationAction.MarkInTransit)]
    [InlineData(ReservationAction.MarkUnknown)]
    public async Task OutboundOtherUnresolvedProtectionBlocksResidualApproval(ReservationAction action)
    {
        var fixture = await OutboundReadyAsync();
        var first = await CreateOutboundAsync(fixture.Warehouse, "OUT-PROTECTION-A", [new(Guid.NewGuid(), fixture.Balance, "7", 0)]);
        var second = await CreateOutboundAsync(fixture.Warehouse, "OUT-PROTECTION-B", [new(Guid.NewGuid(), fixture.Balance, "2", 0)]);
        await using (var owner = Open(_tenant))
            Assert.Equal(OutboundDecision.Committed, (await OutboundAccess(owner).ApproveAsync(Reader, first.Id!.Value,
                new(first.Version!, [new(fixture.Balance, fixture.BalanceVersion)]), Guid.NewGuid(), Guid.NewGuid(), CancellationToken.None)).Decision);
        string version;
        await using (var owner = Open(_tenant))
        {
            await using var transaction = await owner.Database.BeginTransactionAsync();
            var reservation = await owner.Reservations.SingleAsync();
            var before = reservation.Kind;
            Assert.True(reservation.TryChange(action, 0m));
            owner.ReservationChanges.Add(new(_tenant, Guid.NewGuid(), Reader.ActorId, Guid.NewGuid(), action,
                reservation, 0m, 7m, before, _clock.GetUtcNow()));
            var balance = await owner.Balances.SingleAsync();
            balance.TouchReservationFence();
            version = balance.Version;
            await owner.SaveChangesAsync();
            await transaction.CommitAsync();
        }
        await using var approval = Open(_tenant);
        Assert.Equal(OutboundDecision.Blocked, (await OutboundAccess(approval).ApproveAsync(Reader, second.Id!.Value,
            new(second.Version!, [new(fixture.Balance, version)]), Guid.NewGuid(), Guid.NewGuid(), CancellationToken.None)).Decision);
    }

    [Fact]
    public async Task OutboundReviewedAllShortUnexecutedOrderCanCancel()
    {
        var fixture = await OutboundReadyAsync();
        var line = Guid.NewGuid();
        var created = await CreateOutboundAsync(fixture.Warehouse, "OUT-ALL-SHORT", [new(line, fixture.Balance, "7", 0)]);
        OutboundResult approved;
        await using (var owner = Open(_tenant))
            approved = await OutboundAccess(owner).ApproveAsync(Reader, created.Id!.Value,
                new(created.Version!, [new(fixture.Balance, fixture.BalanceVersion)]), Guid.NewGuid(), Guid.NewGuid(), CancellationToken.None);
        OutboundResult picked;
        await using (var owner = Open(_tenant))
            picked = await OutboundAccess(owner).PickAsync(Reader, created.Id!.Value, new(approved.Version!, line, "0", "7", "All short"),
                Guid.NewGuid(), Guid.NewGuid(), CancellationToken.None);
        OutboundResult released;
        await using (var owner = Open(_tenant))
            released = await OutboundAccess(owner).ReleaseShortageAsync(new(_tenant, Guid.NewGuid(), Guid.NewGuid()), created.Id!.Value,
                new(picked.Version!, line, "7", "Reviewed unexecuted"), Guid.NewGuid(), Guid.NewGuid(), CancellationToken.None);
        await using var cancel = Open(_tenant);
        Assert.Equal(OutboundDecision.Committed, (await OutboundAccess(cancel).CancelAsync(Reader, created.Id!.Value,
            new(released.Version!, "Cancel reviewed unexecuted"), Guid.NewGuid(), Guid.NewGuid(), CancellationToken.None)).Decision);
        Assert.Equal(10m, (await cancel.Balances.SingleAsync()).Quantity);
    }

    [Fact]
    public async Task OutboundOneHundredLinesCreateAndApproveAtomically()
    {
        var fixture = await OutboundReadyAsync();
        var lines = new List<OutboundDraftLineRequest> { new(Guid.NewGuid(), fixture.Balance, "1", 0) };
        var versions = new List<OutboundBalanceVersionRequest> { new(fixture.Balance, fixture.BalanceVersion) };
        for (var index = 1; index < 100; index++)
        {
            var location = await ExecuteAsync(Command($"OUT-LOC-{index}") with
            { WarehouseId = fixture.Warehouse, PackId = Guid.NewGuid(), SchemaVersion = "v1" },
                new Schema(LocationSchemaDecision.Valid));
            await LocationStateAsync(location.Id!.Value, new(Guid.NewGuid(), location.Version!, true, "Ready"));
            Guid balanceId;
            string balanceVersion;
            await using (var owner = Open(_tenant))
            {
                var container = await ResourceAccess(owner).ContainerAsync(Reader,
                    new(Guid.NewGuid(), Guid.NewGuid(), $"OUT-PALLET-{index}", $"Pallet {index}", fixture.Warehouse));
                owner.ChangeTracker.Clear();
                var dimension = await ResourceAccess(owner).DimensionAsync(Reader,
                    new(Guid.NewGuid(), Guid.NewGuid(), fixture.Warehouse, location.Id.Value, fixture.Item, "", container.Id!.Value));
                balanceId = dimension.Id!.Value;
                balanceVersion = dimension.Version!;
                owner.ChangeTracker.Clear();
                var inboundLine = Guid.NewGuid();
                var inbound = await new InboundWriter(owner, _clock).CreateAsync(Guid.NewGuid(), Reader.ActorId,
                    fixture.Warehouse, $"OUT-SEED-{index}", [new(inboundLine, fixture.Item, fixture.Unit, "", 3, 1m)]);
                owner.ChangeTracker.Clear();
                var approved = await ExecutionAccess(owner).ApproveAsync(Reader, inbound.Id!.Value,
                    new(Guid.NewGuid(), Guid.NewGuid(), inbound.Version!));
                var received = await ExecutionAccess(owner).ReceiveAsync(Reader, inbound.Id.Value,
                    new(Guid.NewGuid(), Guid.NewGuid(), approved.Version!, balanceVersion, inboundLine, Guid.NewGuid(),
                        "1", container.Id.Value, location.Id.Value, location.Id.Value, balanceId));
                Assert.Equal(InboundExecutionDecision.Committed, received.Decision);
                balanceVersion = (await owner.Balances.AsNoTracking().SingleAsync(row => row.Id == balanceId)).Version;
            }
            lines.Add(new(Guid.NewGuid(), balanceId, "1", 0));
            versions.Add(new(balanceId, balanceVersion));
        }
        var created = await CreateOutboundAsync(fixture.Warehouse, "OUT-HUNDRED", lines);
        Assert.Equal(OutboundDecision.Committed, created.Decision);
        await using var approval = Open(_tenant);
        Assert.Equal(OutboundDecision.Committed, (await OutboundAccess(approval).ApproveAsync(Reader, created.Id!.Value,
            new(created.Version!, versions), Guid.NewGuid(), Guid.NewGuid(), CancellationToken.None)).Decision);
        Assert.Equal(100, await approval.Reservations.CountAsync());
        Assert.Equal(100m, await approval.Reservations.SumAsync(row => row.RemainingQuantity));
    }

    [Fact]
    public async Task OutboundConcurrentApprovalBetweenHeaderAndFactsCannotLookLikeCorruption()
    {
        var fixture = await OutboundReadyAsync();
        var created = await CreateOutboundAsync(fixture.Warehouse, "OUT-SNAPSHOT", [new(Guid.NewGuid(), fixture.Balance, "7", 0)]);
        var interleave = new OutboundHeaderInterleave(async () =>
        {
            await using var writer = Open(_tenant);
            Assert.Equal(OutboundDecision.Committed, (await OutboundAccess(writer).ApproveAsync(Reader, created.Id!.Value,
                new(created.Version!, [new(fixture.Balance, fixture.BalanceVersion)]), Guid.NewGuid(), Guid.NewGuid(),
                CancellationToken.None)).Decision);
        });
        await using var reader = Open(_tenant, interleave);
        var replay = await OutboundAccess(reader).CancelAsync(Reader, created.Id!.Value,
            new(created.Version!, "Concurrent cancellation"), Guid.NewGuid(), Guid.NewGuid(), CancellationToken.None);
        Assert.True(interleave.Reached);
        Assert.Equal(OutboundDecision.Conflict, replay.Decision);
    }

    private sealed class OutboundHeaderInterleave(Func<Task> interleave) : DbCommandInterceptor
    {
        public bool Reached { get; private set; }
        public override async ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData,
            DbDataReader result, CancellationToken cancellationToken = default)
        {
            if (!Reached && command.CommandText.Contains("FROM `wms_outbound_documents`", StringComparison.Ordinal))
            {
                Reached = true;
                await interleave();
            }
            return result;
        }
    }

    private async Task<OutboundResult> CreateOutboundAsync(Guid warehouse, string code, IReadOnlyList<OutboundDraftLineRequest> lines)
    {
        await using var owner = Open(_tenant);
        return await OutboundAccess(owner).CreateAsync(Reader, new(Guid.NewGuid(), Guid.NewGuid(), code, warehouse, lines),
            CancellationToken.None);
    }

    private async Task<OutboundFixture> OutboundReadyAsync()
    {
        var registration = await LegalDimensionReadyAsync();
        await using var resources = Open(_tenant);
        var balance = await ResourceAccess(resources).DimensionAsync(Reader, registration);
        var unit = await resources.StockItems.AsNoTracking().Where(row => row.Id == registration.ItemId)
            .Select(row => row.BaseUnitId).SingleAsync();
        var inboundLine = Guid.NewGuid();
        InboundWriteResult draft;
        await using (var creation = Open(_tenant))
            draft = await new InboundWriter(creation, _clock).CreateAsync(Guid.NewGuid(), Reader.ActorId,
                registration.WarehouseId, "OUTBOUND-SEED", [new(inboundLine, registration.ItemId, unit, registration.Batch, 3, 10m)]);
        InboundExecutionResult approved;
        await using (var owner = Open(_tenant))
            approved = await ExecutionAccess(owner).ApproveAsync(Reader, draft.Id!.Value,
                new(Guid.NewGuid(), Guid.NewGuid(), draft.Version!));
        Assert.Equal(InboundExecutionDecision.Committed, approved.Decision);
        Assert.NotNull(approved.Version);
        InboundExecutionResult received;
        await using (var owner = Open(_tenant))
            received = await ExecutionAccess(owner).ReceiveAsync(Reader, draft.Id!.Value,
                new(Guid.NewGuid(), Guid.NewGuid(), approved.Version!, balance.Version!, inboundLine, Guid.NewGuid(),
                    "10", registration.ContainerId, registration.LocationId, registration.LocationId, balance.Id!.Value));
        Assert.Equal(InboundExecutionDecision.Committed, received.Decision);
        await using var verify = Open(_tenant);
        var actual = await verify.Balances.SingleAsync(row => row.Id == balance.Id);
        return new(registration.WarehouseId, registration.LocationId, actual.Id, actual.Version,
            actual.ItemId, actual.UnitId, actual.ContainerId);
    }

    private async Task AssertOutboundStateAsync(Guid id, OutboundOrderState state, decimal quantity, decimal reserved)
    {
        await using var verify = Open(_tenant);
        Assert.Equal(state, (await verify.OutboundDocuments.SingleAsync(row => row.Id == id)).State);
        Assert.Equal(quantity, (await verify.Balances.SingleAsync()).Quantity);
        Assert.Equal(reserved, await verify.Reservations.SumAsync(row => row.RemainingQuantity));
    }

    private sealed record OutboundFixture(Guid Warehouse, Guid Location, Guid Balance, string BalanceVersion,
        Guid Item, Guid Unit, Guid Container);

    private sealed class OutboundAuthority : IMasterDataAuthorization
    {
        private bool _revoked;
        private static readonly HashSet<string> Fields =
        [
            "Id", "Code", "Name", "WarehouseId", "LocationId", "ItemId", "Batch", "ContainerId", "UnitId",
            "Precision", "Enabled", "PackId", "SchemaVersion", "Version", "CreatedAt", "State", "Lines",
            "BaseUnitId", "BatchRequired", "Countable", "PickedQuantity", "ShortageQuantity", "Reason",
            "LineId", "Quantity"
        ];
        public Task<IReadOnlySet<string>> ReadFieldsAsync(MasterDataSubject subject, string permission, Guid creator,
            CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult<IReadOnlySet<string>>(_revoked ? new HashSet<string>() : Fields);
        }
        public void Revoke() => _revoked = true;
        public Task<MasterDataCreatorAccess> ReadCreatorsAsync(MasterDataSubject subject, string permission,
            IReadOnlyCollection<string> requiredFields, CancellationToken ct) =>
            throw new InvalidOperationException("Outbound tests use explicit single-order owner reads.");
    }

    private sealed class RevokeAfterSave(OutboundAuthority authority) : SaveChangesInterceptor
    {
        public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result,
            CancellationToken cancellationToken = default)
        {
            authority.Revoke();
            return ValueTask.FromResult(result);
        }
    }
}
