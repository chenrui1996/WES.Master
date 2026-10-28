using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using System.Text.Json;
using Wes.Wms;
using Wes.Wms.Domain;
using Wes.Wms.Features.Inbound;
using Wes.Wms.Features.Ledger;
using Wes.Wms.Features.MasterData;
using Wes.Wms.Features.Reservations;
using Wes.Wms.Infrastructure.Persistence;
using Xunit;

namespace Wes.Persistence.Tests;

public sealed partial class WmsMasterDataTests
{
    [Fact]
    public async Task InboundTransportReceiptStagesOneIntentAndInTransitProtection()
    {
        var fixture = await ExecutionReadyAsync();
        var request = await TransportReceiptAsync(fixture);
        await using var owner = Open(_tenant);
        Assert.Equal(InboundExecutionDecision.Committed,
            (await ExecutionAccess(owner).ReceiveAsync(Reader, fixture.Order, request)).Decision);
        var intent = Assert.Single(await owner.InboundTransportIntents.AsNoTracking().ToListAsync());
        Assert.Equal(request.ExecutionId, intent.ExecutionId);
        Assert.Equal(request.TargetLocationId, intent.Request().TargetLocationId);
        var reservation = Assert.Single(await owner.Reservations.AsNoTracking().ToListAsync());
        Assert.Equal(5m, reservation.RemainingQuantity);
        Assert.True(reservation.Kind.HasFlag(StockProtectionKind.InTransit));
        Assert.Equal(5m, (await owner.Balances.AsNoTracking().SingleAsync()).Quantity);
        Assert.Single(await owner.Postings.AsNoTracking().ToListAsync());
        Assert.Equal(InboundExecutionDecision.Replayed,
            (await ExecutionAccess(owner).ReceiveAsync(Reader, fixture.Order, request)).Decision);
        Assert.Single(await owner.InboundTransportIntents.AsNoTracking().ToListAsync());
        var available = StockAvailability.Calculate(InventoryQuantity.Create(5m),
            [new StockProtection(reservation.ExecutionUnitId, InventoryQuantity.Create(reservation.RemainingQuantity), reservation.Kind)]);
        Assert.Equal(0m, available.Available.Value);
        Assert.Equal(5m, available.InTransit.Value);
    }

    [Fact]
    public async Task InboundTransportSameLocationNeverCreatesIntentOrProtection()
    {
        var fixture = await ExecutionReadyAsync();
        var request = await ExecutionReceiptAsync(fixture);
        await using var owner = Open(_tenant);
        Assert.Equal(InboundExecutionDecision.Committed,
            (await ExecutionAccess(owner).ReceiveAsync(Reader, fixture.Order, request)).Decision);
        Assert.Empty(await owner.InboundTransportIntents.AsNoTracking().ToListAsync());
        Assert.Empty(await owner.Reservations.AsNoTracking().ToListAsync());
        Assert.Equal(InboundTransportDecision.NotFound,
            (await TransportWorker(owner, new ContractTransportAdapter()).DispatchAsync(request.ExecutionId)).Decision);
    }

    [Fact]
    public async Task InboundTransportHistoricalPrimitiveReceiptIsNotAdoptedOrBackfilled()
    {
        var fixture = await ExecutionReadyAsync();
        var request = await TransportReceiptAsync(fixture);
        await using var owner = Open(_tenant);
        var dimension = await owner.DimensionRegistrations.AsNoTracking().SingleAsync();
        var result = await new InboundWriter(owner, _clock).ReceiveAsync(fixture.Order,
            new(request.OperationId, Reader.ActorId, request.ExecutionId, fixture.Line, fixture.Item, fixture.Unit,
                dimension.Batch, fixture.Container, fixture.Location, request.TargetLocationId, 5m),
            request.CorrelationId, request.ExpectedVersion, fixture.Balance, request.ExpectedBalanceVersion);
        Assert.Equal(InboundWriteDecision.Committed, result.Decision);
        owner.ChangeTracker.Clear();
        Assert.Empty(await owner.InboundTransportIntents.AsNoTracking().ToListAsync());
        Assert.Empty(await owner.Reservations.AsNoTracking().ToListAsync());
        Assert.Equal(InboundTransportDecision.NotFound,
            (await TransportWorker(owner, new ContractTransportAdapter()).DispatchAsync(request.ExecutionId)).Decision);
        Assert.Single(await InboundPendingProtection.ReadAsync(owner, fixture.Warehouse, default));
    }

    [Theory]
    [InlineData("quantity")]
    [InlineData("kind")]
    [InlineData("audit")]
    [InlineData("missing-intent")]
    public async Task InboundTransportReceiptProtectionRequiresExactNewAtomicAllocation(string wrong)
    {
        var fixture = await ExecutionReadyAsync();
        var request = await TransportReceiptAsync(fixture);
        await using var owner = Open(_tenant);
        var dimension = await owner.DimensionRegistrations.AsNoTracking().SingleAsync();
        var command = new InboundReceiptCommand(request.OperationId, Reader.ActorId, request.ExecutionId, fixture.Line,
            fixture.Item, fixture.Unit, dimension.Batch, fixture.Container, fixture.Location, request.TargetLocationId, 5m);
        var guards = new InboundWriteGuards(_ => Task.FromResult(true), _ =>
        {
            var fact = owner.ChangeTracker.Entries<InboundFact>().Single(row => row.State == EntityState.Added).Entity;
            var header = owner.ChangeTracker.Entries<InboundDocument>().Single().Entity;
            owner.InboundExecutionOperations.Add(new(fact, "{}", _clock.GetUtcNow()));
            if (wrong != "missing-intent") owner.InboundTransportIntents.Add(new(header, fact, fixture.Balance, 3));
            var protection = new StockReservation(_tenant, request.ExecutionId, fixture.Balance, fixture.Order,
                fixture.Line, Reader.ActorId, wrong == "quantity" ? 4m : 5m, fact.OccurredAt);
            if (wrong != "kind") protection.TryChange(ReservationAction.MarkInTransit, 0m);
            owner.Reservations.Add(protection);
            owner.ReservationChanges.Add(new(_tenant, request.OperationId, Reader.ActorId, request.CorrelationId,
                ReservationAction.Reserve, protection, wrong == "audit" ? 3m : protection.OriginalQuantity, 0m, 0, fact.OccurredAt));
            return Task.CompletedTask;
        }, _ => Task.FromResult(true));
        await Assert.ThrowsAsync<InvalidOperationException>(() => new InboundWriter(owner, _clock).ReceiveAsync(fixture.Order,
            command, request.CorrelationId, request.ExpectedVersion, fixture.Balance, fixture.BalanceVersion, guards: guards));
        owner.ChangeTracker.Clear();
        Assert.Empty(await owner.InboundTransportIntents.AsNoTracking().ToListAsync());
        Assert.Empty(await owner.Reservations.AsNoTracking().ToListAsync());
        Assert.Empty(await owner.Postings.AsNoTracking().ToListAsync());
        Assert.Equal(0m, (await owner.Balances.AsNoTracking().SingleAsync()).Quantity);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InboundTransportReceiptSavedThenFailedRollsBackAndOriginalKeyCanCommit(bool cancel)
    {
        var fixture = await ExecutionReadyAsync();
        var request = await TransportReceiptAsync(fixture);
        using var cancellation = new CancellationTokenSource();
        await using (var broken = Open(_tenant, new AfterSaveFault(cancel, cancellation)))
        {
            if (cancel) await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                ExecutionAccess(broken).ReceiveAsync(Reader, fixture.Order, request, cancellation.Token));
            else await Assert.ThrowsAsync<InvalidOperationException>(() =>
                ExecutionAccess(broken).ReceiveAsync(Reader, fixture.Order, request));
            Assert.Empty(broken.ChangeTracker.Entries());
            Assert.Null(broken.Database.CurrentTransaction);
        }
        await using var owner = Open(_tenant);
        Assert.Empty(await owner.InboundTransportIntents.AsNoTracking().ToListAsync());
        Assert.Empty(await owner.Reservations.AsNoTracking().ToListAsync());
        Assert.Empty(await owner.ReservationChanges.AsNoTracking().ToListAsync());
        Assert.Empty(await owner.Postings.AsNoTracking().ToListAsync());
        Assert.Equal(InboundExecutionDecision.Committed,
            (await ExecutionAccess(owner).ReceiveAsync(Reader, fixture.Order, request)).Decision);
        Assert.Equal(InboundExecutionDecision.Replayed,
            (await ExecutionAccess(owner).ReceiveAsync(Reader, fixture.Order, request)).Decision);
        Assert.Single(await owner.InboundTransportIntents.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task InboundTransportReceiptAuthorityRevokedAfterSaveRollsBackWholeChain()
    {
        var fixture = await ExecutionReadyAsync();
        var request = await TransportReceiptAsync(fixture);
        var authority = new ExecutionAuthority();
        await using (var owner = Open(_tenant, new ExecutionSaveAction(() => authority.Revoked = true)))
        {
            Assert.Equal(InboundExecutionDecision.Forbidden,
                (await ExecutionAccess(owner, authority).ReceiveAsync(Reader, fixture.Order, request)).Decision);
            Assert.Empty(owner.ChangeTracker.Entries());
            Assert.Null(owner.Database.CurrentTransaction);
        }
        await using var verify = Open(_tenant);
        Assert.Empty(await verify.InboundTransportIntents.AsNoTracking().ToListAsync());
        Assert.Empty(await verify.Reservations.AsNoTracking().ToListAsync());
        Assert.Empty(await verify.ReservationChanges.AsNoTracking().ToListAsync());
        Assert.Empty(await verify.Postings.AsNoTracking().ToListAsync());
        Assert.Equal(0m, (await verify.Balances.AsNoTracking().SingleAsync()).Quantity);
    }

    [Fact]
    public async Task InboundTransportLostAcceptanceRetriesOriginalEnvelopeAndRestoresAssociationInDetail()
    {
        var (fixture, request) = await TransportReadyAsync();
        var adapter = new ContractTransportAdapter { LoseFirstAcceptance = true };
        await using var owner = Open(_tenant);
        var worker = TransportWorker(owner, adapter);
        Assert.Equal(InboundTransportDecision.Unavailable, (await worker.DispatchAsync(request.ExecutionId)).Decision);
        Assert.Null((await owner.InboundTransportIntents.AsNoTracking().SingleAsync()).TransportId);
        Assert.Equal(2, await owner.InboundFacts.CountAsync());
        var result = await worker.DispatchAsync(request.ExecutionId);
        Assert.Equal(InboundTransportDecision.Accepted, result.Decision);
        Assert.Equal(InboundTransportDecision.Replayed, (await worker.DispatchAsync(request.ExecutionId)).Decision);
        Assert.Equal(2, adapter.Requests.Count);
        Assert.Equal(adapter.Requests[0], adapter.Requests[1]);
        Assert.Single(adapter.Transports);
        var fact = await owner.InboundFacts.AsNoTracking().SingleAsync(row => row.Kind == InboundFactKind.TransportLinked);
        Assert.Equal(_transportServiceActor, fact.ActorId);
        Assert.NotEqual(Reader.ActorId, fact.ActorId);
        Assert.Null(fact.ExecutionId);
        Assert.Equal(adapter.Transports.Single().Value.Transport, fact.Transport().Acceptance!.TransportId);
        Assert.Equal(InboundTransportVerification.Verified, fact.Transport().Acceptance!.Verification);
        var detail = await TransportDetailAsync(owner, fixture.Order);
        Assert.Equal(InboundState.Executing, detail.State);
        Assert.Equal(result.TransportId, Assert.Single(detail.Receipts).TransportId);
        Assert.False(detail.Receipts[0].Arrived);
        Assert.Equal(fact.AfterVersion, detail.Version);
        Assert.Single(await InboundPendingProtection.ReadAsync(owner, fixture.Warehouse, default));
    }

    [Fact]
    public async Task InboundTransportFinalArrivalTransfersExactlyOnceAndRestoresCompletedDetail()
    {
        var (fixture, request) = await TransportReadyAsync(registerTarget: true);
        var adapter = new ContractTransportAdapter();
        await using var owner = Open(_tenant);
        var worker = TransportWorker(owner, adapter);
        Assert.Equal(InboundTransportDecision.Accepted, (await worker.DispatchAsync(request.ExecutionId)).Decision);
        var completion = await TransportCompletionAsync(owner);
        var applied = await worker.ApplyCompletionAsync(completion);
        Assert.Equal(InboundTransportDecision.Applied, applied.Decision);
        var header = await owner.InboundDocuments.AsNoTracking().SingleAsync();
        Assert.Equal(InboundTransportDecision.Replayed, (await worker.ApplyCompletionAsync(completion)).Decision);
        Assert.Equal(header.Version, (await owner.InboundDocuments.AsNoTracking().SingleAsync()).Version);
        await AssertTransportAppliedAsync(owner, fixture, completion, applied.PostingId);
    }

    [Fact]
    public async Task InboundTransportMissingTargetRetainsTrustedPendingSyncAndRepairsOriginalEvent()
    {
        var (fixture, request) = await TransportReadyAsync();
        var adapter = new ContractTransportAdapter();
        await using var owner = Open(_tenant);
        var worker = TransportWorker(owner, adapter);
        await worker.DispatchAsync(request.ExecutionId);
        var completion = await TransportCompletionAsync(owner);
        Assert.Equal(InboundTransportDecision.PendingSync, (await worker.ApplyCompletionAsync(completion)).Decision);
        var inbox = await owner.InboundTransportInbox.AsNoTracking().SingleAsync();
        Assert.False(inbox.Applied);
        Assert.Equal(JsonSerializer.Serialize(completion), inbox.Body);
        Assert.Equal(5m, (await owner.Reservations.AsNoTracking().SingleAsync()).RemainingQuantity);
        Assert.Single(await InboundPendingProtection.ReadAsync(owner, fixture.Warehouse, default));
        Assert.Single(await owner.Postings.AsNoTracking().ToListAsync());
        await RegisterTransportTargetAsync(fixture, request.TargetLocationId);
        var result = await worker.ApplyCompletionAsync(completion);
        Assert.Equal(InboundTransportDecision.Applied, result.Decision);
        await AssertTransportAppliedAsync(owner, fixture, completion, result.PostingId);
        Assert.Single(adapter.Requests);
    }

    [Theory]
    [InlineData("producer")]
    [InlineData("tenant")]
    [InlineData("schema")]
    [InlineData("intent")]
    [InlineData("execution")]
    [InlineData("transport")]
    [InlineData("container")]
    [InlineData("source")]
    [InlineData("location")]
    [InlineData("evidence")]
    [InlineData("untrusted-evidence")]
    [InlineData("future")]
    [InlineData("nonutc")]
    [InlineData("empty-event")]
    public async Task InboundTransportRejectsWrongFinalTupleBeforeIngestion(string wrong)
    {
        var (_, request) = await TransportReadyAsync(registerTarget: true);
        var adapter = new ContractTransportAdapter();
        await using var owner = Open(_tenant);
        var worker = TransportWorker(owner, adapter);
        await worker.DispatchAsync(request.ExecutionId);
        var completion = await TransportCompletionAsync(owner);
        completion = wrong switch
        {
            "producer" => completion with { Producer = "Operator" },
            "tenant" => completion with { TenantId = _other },
            "schema" => completion with { SchemaVersion = 2 },
            "intent" => completion with { IntentId = Guid.NewGuid() },
            "execution" => completion with { ExecutionId = Guid.NewGuid() },
            "transport" => completion with { TransportId = Guid.NewGuid() },
            "container" => completion with { ContainerId = Guid.NewGuid() },
            "source" => completion with { SourceLocationId = Guid.NewGuid() },
            "location" => completion with { FinalLocationId = request.ReceiptLocationId },
            "evidence" => completion with { EvidenceRefs = [] },
            "untrusted-evidence" => completion with { EvidenceRefs = ["operator-assertion"] },
            "future" => completion with { CompletedAt = _clock.GetUtcNow().AddMinutes(1) },
            "nonutc" => completion with { CompletedAt = _clock.GetUtcNow().ToOffset(TimeSpan.FromHours(8)) },
            _ => completion with { EventId = Guid.Empty }
        };
        Assert.Equal(InboundTransportDecision.Rejected, (await worker.ApplyCompletionAsync(completion)).Decision);
        Assert.Empty(await owner.InboundTransportInbox.AsNoTracking().ToListAsync());
        Assert.Single(await owner.Postings.AsNoTracking().ToListAsync());
        Assert.Equal(5m, (await owner.Reservations.AsNoTracking().SingleAsync()).RemainingQuantity);
    }

    [Theory]
    [InlineData("tenant")]
    [InlineData("intent")]
    [InlineData("execution")]
    [InlineData("transport")]
    [InlineData("schema")]
    [InlineData("producer")]
    [InlineData("rejected")]
    [InlineData("unavailable")]
    public async Task InboundTransportRequiresAuthenticatedExactAcceptance(string wrong)
    {
        var (_, request) = await TransportReadyAsync();
        var adapter = new ContractTransportAdapter
        {
            Acceptance = acceptance => wrong switch
            {
                "tenant" => acceptance with { TenantId = _other },
                "intent" => acceptance with { IntentId = Guid.NewGuid() },
                "execution" => acceptance with { ExecutionId = Guid.NewGuid() },
                "transport" => acceptance with { TransportId = Guid.Empty },
                "schema" => acceptance with { SchemaVersion = 2 },
                "producer" => acceptance with { Producer = "Operator" },
                "unavailable" => acceptance with { Verification = InboundTransportVerification.Unavailable },
                _ => acceptance with { Verification = InboundTransportVerification.Rejected }
            }
        };
        await using var owner = Open(_tenant);
        Assert.Equal(wrong == "unavailable" ? InboundTransportDecision.Unavailable : InboundTransportDecision.Rejected,
            (await TransportWorker(owner, adapter).DispatchAsync(request.ExecutionId)).Decision);
        Assert.Null((await owner.InboundTransportIntents.AsNoTracking().SingleAsync()).TransportId);
        Assert.Equal(2, await owner.InboundFacts.CountAsync());
    }

    [Theory]
    [InlineData(InboundTransportVerification.Rejected)]
    [InlineData(InboundTransportVerification.Unavailable)]
    public async Task InboundTransportExplicitVerificationFailureIsNotVerified(InboundTransportVerification verification)
    {
        var (_, request) = await TransportReadyAsync();
        var adapter = new ContractTransportAdapter { CompletionVerification = verification };
        await using var owner = Open(_tenant);
        var worker = TransportWorker(owner, adapter);
        await worker.DispatchAsync(request.ExecutionId);
        Assert.Equal(verification == InboundTransportVerification.Rejected ? InboundTransportDecision.Rejected : InboundTransportDecision.Unavailable,
            (await worker.ApplyCompletionAsync(await TransportCompletionAsync(owner))).Decision);
        Assert.Empty(await owner.InboundTransportInbox.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task InboundTransportDuplicateBodyConflictAndSecondCompletionCannotReplaceOriginal()
    {
        var (_, request) = await TransportReadyAsync();
        var adapter = new ContractTransportAdapter();
        await using var owner = Open(_tenant);
        var worker = TransportWorker(owner, adapter);
        await worker.DispatchAsync(request.ExecutionId);
        var completion = await TransportCompletionAsync(owner);
        Assert.Equal(InboundTransportDecision.PendingSync, (await worker.ApplyCompletionAsync(completion)).Decision);
        Assert.Equal(InboundTransportDecision.Conflict,
            (await worker.ApplyCompletionAsync(completion with { EvidenceRefs = ["fixture-final-2"] })).Decision);
        Assert.Equal(InboundTransportDecision.Conflict,
            (await worker.ApplyCompletionAsync(completion with { Producer = "Operator" })).Decision);
        Assert.Equal(InboundTransportDecision.Conflict,
            (await worker.ApplyCompletionAsync(completion with { ExecutionId = Guid.NewGuid() })).Decision);
        Assert.Equal(InboundTransportDecision.Conflict,
            (await worker.ApplyCompletionAsync(completion with { EventId = Guid.NewGuid(), CompletionId = Guid.NewGuid() })).Decision);
        Assert.Equal(JsonSerializer.Serialize(completion), (await owner.InboundTransportInbox.AsNoTracking().SingleAsync()).Body);
    }

    [Fact]
    public async Task InboundTransportConcurrentCompletionsRecoverOneWinnerWithoutTrackerContamination()
    {
        var (fixture, request) = await TransportReadyAsync(registerTarget: true);
        var adapter = new ContractTransportAdapter();
        await using var owner = Open(_tenant);
        await TransportWorker(owner, adapter).DispatchAsync(request.ExecutionId);
        var completion = await TransportCompletionAsync(owner);
        async Task<InboundTransportResult> Complete()
        {
            await using var scoped = Open(_tenant);
            var result = await TransportWorker(scoped, adapter).ApplyCompletionAsync(completion);
            Assert.Empty(scoped.ChangeTracker.Entries());
            Assert.Null(scoped.Database.CurrentTransaction);
            return result;
        }
        var results = await Task.WhenAll(Complete(), Complete(), Complete());
        Assert.Single(results, row => row.Decision == InboundTransportDecision.Applied);
        Assert.Equal(2, results.Count(row => row.Decision == InboundTransportDecision.Replayed));
        Assert.Single(results.Select(row => row.PostingId).Distinct());
        await AssertTransportAppliedAsync(owner, fixture, completion, results[0].PostingId);
    }

    [Fact]
    public async Task InboundTransportConcurrentAssociationsKeepOneHistoryTransition()
    {
        var (_, request) = await TransportReadyAsync();
        var adapter = new ContractTransportAdapter();
        async Task<InboundTransportResult> Dispatch()
        {
            await using var scoped = Open(_tenant);
            return await TransportWorker(scoped, adapter).DispatchAsync(request.ExecutionId);
        }
        var results = await Task.WhenAll(Dispatch(), Dispatch(), Dispatch());
        Assert.Single(results, row => row.Decision == InboundTransportDecision.Accepted);
        Assert.Equal(2, results.Count(row => row.Decision == InboundTransportDecision.Replayed));
        Assert.Single(results.Select(row => row.TransportId).Distinct());
        await using var owner = Open(_tenant);
        Assert.Equal(3, await owner.InboundFacts.CountAsync());
        Assert.Single(adapter.Transports);
    }

    [Fact]
    public async Task InboundTransportExtraStockAfterAcceptanceRetainsPendingSyncWithoutSplittingContainer()
    {
        var (fixture, request) = await TransportReadyAsync(registerTarget: true);
        var adapter = new ContractTransportAdapter();
        await using var owner = Open(_tenant);
        var worker = TransportWorker(owner, adapter);
        await worker.DispatchAsync(request.ExecutionId);
        var completion = await TransportCompletionAsync(owner);
        var target = await owner.Balances.AsNoTracking().SingleAsync(row => row.LocationId == request.TargetLocationId);
        Assert.Equal(StockPostingDecision.Posted, (await new PostStock(owner, _clock).ExecuteAsync(new(
            Guid.NewGuid(), Reader.ActorId, Guid.NewGuid(), StockPostingKind.Receipt, Guid.NewGuid(), Guid.NewGuid(),
            Guid.NewGuid(), null, target.Id, 1m, null, target.Version))).Decision);
        owner.ChangeTracker.Clear();
        Assert.Equal(InboundTransportDecision.PendingSync, (await worker.ApplyCompletionAsync(completion)).Decision);
        Assert.False((await owner.InboundTransportInbox.AsNoTracking().SingleAsync()).Applied);
        Assert.Empty(await owner.Postings.AsNoTracking().Where(row => row.Kind == StockPostingKind.Transfer).ToListAsync());
        Assert.Equal(5m, (await owner.Balances.AsNoTracking().SingleAsync(row => row.Id == fixture.Balance)).Quantity);
        Assert.Equal(5m, (await owner.Reservations.AsNoTracking().SingleAsync()).RemainingQuantity);
    }

    [Theory]
    [InlineData("positive")]
    [InlineData("reservation")]
    [InlineData("pending")]
    public async Task InboundTransportConflictingTargetAfterAcceptanceRetainsExactPendingInboxAndProtection(string conflict)
    {
        var (fixture, request) = await TransportReadyAsync(registerTarget: true);
        var adapter = new ContractTransportAdapter();
        await using var owner = Open(_tenant);
        var worker = TransportWorker(owner, adapter);
        Assert.Equal(InboundTransportDecision.Accepted, (await worker.DispatchAsync(request.ExecutionId)).Decision);
        var completion = await TransportCompletionAsync(owner);
        await SeedTransportLocationConflictAsync(fixture, request.TargetLocationId, conflict);
        var originalHeader = await owner.InboundDocuments.AsNoTracking().SingleAsync(row => row.Id == fixture.Order);
        var originalPostingCount = await owner.Postings.CountAsync();
        var originalEntryCount = await owner.Entries.CountAsync();
        Assert.Equal(InboundTransportDecision.PendingSync, (await worker.ApplyCompletionAsync(completion)).Decision);
        Assert.Equal(InboundTransportDecision.PendingSync, (await worker.ApplyCompletionAsync(completion)).Decision);
        var inbox = await owner.InboundTransportInbox.AsNoTracking().SingleAsync();
        Assert.Equal(JsonSerializer.Serialize(completion), inbox.Body);
        Assert.False(inbox.Applied);
        Assert.Null(inbox.PostingId);
        Assert.Equal(originalPostingCount, await owner.Postings.CountAsync());
        Assert.Equal(originalEntryCount, await owner.Entries.CountAsync());
        Assert.Empty(await owner.Postings.AsNoTracking().Where(row => row.Kind == StockPostingKind.Transfer).ToListAsync());
        Assert.Equal(originalHeader.Version, (await owner.InboundDocuments.AsNoTracking().SingleAsync(row => row.Id == fixture.Order)).Version);
        Assert.Equal(3, await owner.InboundFacts.CountAsync(row => row.OrderId == fixture.Order));
        Assert.Equal(5m, (await owner.Balances.AsNoTracking().SingleAsync(row => row.Id == fixture.Balance)).Quantity);
        Assert.Equal(0m, (await owner.Balances.AsNoTracking().SingleAsync(row => row.ContainerId == fixture.Container
            && row.LocationId == request.TargetLocationId)).Quantity);
        var protection = await owner.Reservations.AsNoTracking().SingleAsync(row => row.ExecutionUnitId == request.ExecutionId);
        Assert.Equal(5m, protection.RemainingQuantity);
        Assert.Equal(StockProtectionKind.Reserved | StockProtectionKind.InTransit, protection.Kind);
        Assert.False(Assert.Single((await TransportDetailAsync(owner, fixture.Order)).Receipts).Arrived);
        Assert.Contains(await InboundPendingProtection.ReadAsync(owner, fixture.Warehouse, default),
            receipt => receipt.ExecutionId == request.ExecutionId);
        Assert.Single(adapter.Requests);
        Assert.Empty(owner.ChangeTracker.Entries());
    }

    [Theory]
    [InlineData("positive", false)]
    [InlineData("reservation", false)]
    [InlineData("pending", false)]
    [InlineData("positive", true)]
    [InlineData("reservation", true)]
    [InlineData("pending", true)]
    public async Task InboundTransportConflictingSourceOrTargetBeforeDispatchBlocksAdapter(string conflict, bool source)
    {
        var (fixture, request) = await TransportReadyAsync(registerTarget: true);
        await SeedTransportLocationConflictAsync(fixture, source ? request.ReceiptLocationId : request.TargetLocationId, conflict);
        var adapter = new ContractTransportAdapter();
        await using var owner = Open(_tenant);
        Assert.Equal(InboundTransportDecision.Blocked,
            (await TransportWorker(owner, adapter).DispatchAsync(request.ExecutionId)).Decision);
        Assert.Empty(adapter.Requests);
        Assert.Null((await owner.InboundTransportIntents.AsNoTracking().SingleAsync()).TransportId);
        Assert.Equal(2, await owner.InboundFacts.CountAsync(row => row.OrderId == fixture.Order));
        Assert.Equal(5m, (await owner.Reservations.AsNoTracking().SingleAsync(row => row.ExecutionUnitId == request.ExecutionId)).RemainingQuantity);
    }

    private async Task SeedTransportLocationConflictAsync(ExecutionFixture fixture, Guid conflictingLocation, string conflict)
    {
        await using var owner = Open(_tenant);
        var container = await ResourceAccess(owner).ContainerAsync(Reader,
            new(Guid.NewGuid(), Guid.NewGuid(), "BLOCKING-CONTAINER", "Blocking container", fixture.Warehouse));
        Assert.Equal(StockResourceDecision.Created, container.Decision);
        var batch = await owner.DimensionRegistrations.AsNoTracking().Where(row => row.BalanceId == fixture.Balance)
            .Select(row => row.Batch).SingleAsync();
        var location = conflictingLocation;
        if (conflict == "pending")
        {
            var source = await ExecuteAsync(Command("BLOCKING-SOURCE") with
            { WarehouseId = fixture.Warehouse, PackId = Guid.NewGuid(), SchemaVersion = "v1" },
                new Schema(LocationSchemaDecision.Valid));
            Assert.Equal(LocationStateDecision.Changed,
                (await LocationStateAsync(source.Id!.Value, new(Guid.NewGuid(), source.Version!, true, "Ready"))).Decision);
            location = source.Id.Value;
        }
        var dimension = await ResourceAccess(owner).DimensionAsync(Reader,
            new(Guid.NewGuid(), Guid.NewGuid(), fixture.Warehouse, location, fixture.Item, batch, container.Id!.Value));
        Assert.Equal(StockResourceDecision.Created, dimension.Decision);
        var document = Guid.NewGuid();
        var line = Guid.NewGuid();
        var execution = Guid.NewGuid();
        if (conflict == "pending")
        {
            var created = await new InboundWriter(owner, _clock).CreateAsync(Guid.NewGuid(), Reader.ActorId,
                fixture.Warehouse, "BLOCKING-INBOUND", [new(line, fixture.Item, fixture.Unit, batch, 3, 1m)]);
            owner.ChangeTracker.Clear();
            var approved = await new InboundWriter(owner, _clock).ApproveAsync(created.Id!.Value, Guid.NewGuid(),
                Reader.ActorId, Guid.NewGuid(), created.Version!);
            owner.ChangeTracker.Clear();
            Assert.Equal(InboundWriteDecision.Committed, (await new InboundWriter(owner, _clock).ReceiveAsync(
                created.Id.Value, new(Guid.NewGuid(), Reader.ActorId, execution, line, fixture.Item, fixture.Unit, batch,
                    container.Id.Value, location, conflictingLocation, 1m), Guid.NewGuid(), approved.Version!,
                dimension.Id!.Value, dimension.Version!)).Decision);
        }
        else
        {
            Assert.Equal(StockPostingDecision.Posted, (await new PostStock(owner, _clock).ExecuteAsync(new(
                Guid.NewGuid(), Reader.ActorId, Guid.NewGuid(), StockPostingKind.Receipt, document, line, execution,
                null, dimension.Id, 1m, null, dimension.Version))).Decision);
            owner.ChangeTracker.Clear();
            if (conflict == "reservation")
            {
                var balance = await owner.Balances.AsNoTracking().SingleAsync(row => row.Id == dimension.Id);
                Assert.Equal(ReservationDecision.Changed, (await new ChangeStockReservation(owner, _clock).ExecuteAsync(
                    new(Guid.NewGuid(), Reader.ActorId, Guid.NewGuid(), ReservationAction.Reserve, execution,
                        balance.Id, document, line, 1m, balance.Version))).Decision);
            }
        }
    }

    [Theory]
    [InlineData("association", false)]
    [InlineData("association", true)]
    [InlineData("inbox", false)]
    [InlineData("inbox", true)]
    [InlineData("arrival", false)]
    [InlineData("arrival", true)]
    public async Task InboundTransportSavedThenFailedAssociationOrArrivalRollsBack(string phase, bool cancel)
    {
        var (fixture, request) = await TransportReadyAsync(registerTarget: true);
        var adapter = new ContractTransportAdapter();
        InboundTransportCompletion? completion = null;
        if (phase != "association")
        {
            await using var setup = Open(_tenant);
            await TransportWorker(setup, adapter).DispatchAsync(request.ExecutionId);
            completion = await TransportCompletionAsync(setup);
        }
        using var cancellation = new CancellationTokenSource();
        var fault = new TransportSaveFault(phase, () =>
        {
            if (cancel) cancellation.Cancel();
            else throw new InvalidOperationException("Injected transport save failure.");
        });
        await using (var broken = Open(_tenant, fault))
        {
            Task<InboundTransportResult> Run() => phase == "association"
                ? TransportWorker(broken, adapter).DispatchAsync(request.ExecutionId, cancellation.Token)
                : TransportWorker(broken, adapter).ApplyCompletionAsync(completion!, cancellation.Token);
            if (cancel) await Assert.ThrowsAnyAsync<OperationCanceledException>(Run);
            else await Assert.ThrowsAsync<InvalidOperationException>(Run);
            Assert.True(fault.Reached);
            Assert.Empty(broken.ChangeTracker.Entries());
            Assert.Null(broken.Database.CurrentTransaction);
        }
        await using var owner = Open(_tenant);
        Assert.Equal(5m, (await owner.Balances.AsNoTracking().SingleAsync(row => row.Id == fixture.Balance)).Quantity);
        Assert.Equal(5m, (await owner.Reservations.AsNoTracking().SingleAsync()).RemainingQuantity);
        Assert.Single(await owner.Postings.AsNoTracking().ToListAsync());
        if (phase == "association")
        {
            Assert.Null((await owner.InboundTransportIntents.AsNoTracking().SingleAsync()).TransportId);
            Assert.Equal(2, await owner.InboundFacts.CountAsync());
            Assert.Equal(InboundTransportDecision.Accepted,
                (await TransportWorker(owner, adapter).DispatchAsync(request.ExecutionId)).Decision);
        }
        else
        {
            if (phase == "arrival") Assert.False((await owner.InboundTransportInbox.AsNoTracking().SingleAsync()).Applied);
            else Assert.Empty(await owner.InboundTransportInbox.AsNoTracking().ToListAsync());
            Assert.Equal(3, await owner.InboundFacts.CountAsync());
            Assert.Equal(InboundTransportDecision.Applied,
                (await TransportWorker(owner, adapter).ApplyCompletionAsync(completion!)).Decision);
        }
        Assert.Single(adapter.Transports);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InboundTransportExtraPositiveStockBlocksWholeContainerDispatchAndReallocation(bool otherBalance)
    {
        var (fixture, request) = await TransportReadyAsync(registerTarget: true);
        await using var seed = Open(_tenant);
        var balance = await seed.Balances.AsNoTracking().SingleAsync(row =>
            row.LocationId == (otherBalance ? request.TargetLocationId : request.ReceiptLocationId));
        var receipt = await new PostStock(seed, _clock).ExecuteAsync(new(Guid.NewGuid(), Reader.ActorId, Guid.NewGuid(),
            StockPostingKind.Receipt, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), null, balance.Id, 1m, null, balance.Version));
        Assert.Equal(StockPostingDecision.Posted, receipt.Decision);
        seed.ChangeTracker.Clear();
        var adapter = new ContractTransportAdapter();
        Assert.Equal(InboundTransportDecision.Blocked,
            (await TransportWorker(seed, adapter).DispatchAsync(request.ExecutionId)).Decision);
        Assert.Empty(adapter.Requests);
        var current = await seed.Balances.AsNoTracking().SingleAsync(row => row.Id == balance.Id);
        Assert.Equal(ReservationDecision.StateConflict, (await new ChangeStockReservation(seed, _clock).ExecuteAsync(
            new(Guid.NewGuid(), Reader.ActorId, Guid.NewGuid(), ReservationAction.Reserve, Guid.NewGuid(), balance.Id,
                Guid.NewGuid(), Guid.NewGuid(), 1m, current.Version))).Decision);
        Assert.Equal(5m, (await seed.Reservations.AsNoTracking().SingleAsync()).RemainingQuantity);
        Assert.Single(await InboundPendingProtection.ReadAsync(seed, fixture.Warehouse, default));
    }

    [Fact]
    public async Task InboundTransportProtectionCannotBeReleasedOrConsumedByUnmatchedLedgerCommand()
    {
        var (fixture, request) = await TransportReadyAsync(registerTarget: true);
        await using var owner = Open(_tenant);
        var balance = await owner.Balances.AsNoTracking().SingleAsync(row => row.Id == fixture.Balance);
        Assert.Equal(ReservationDecision.StateConflict, (await new ChangeStockReservation(owner, _clock).ExecuteAsync(
            new(Guid.NewGuid(), Reader.ActorId, Guid.NewGuid(), ReservationAction.ReleaseUnstarted, request.ExecutionId,
                fixture.Balance, fixture.Order, fixture.Line, 5m, balance.Version))).Decision);
        owner.ChangeTracker.Clear();
        var target = await owner.Balances.AsNoTracking().SingleAsync(row => row.LocationId == request.TargetLocationId);
        await Assert.ThrowsAsync<InvalidOperationException>(() => new PostStock(owner, _clock).ExecuteAsync(
            new(Guid.NewGuid(), Reader.ActorId, Guid.NewGuid(), StockPostingKind.Transfer, fixture.Order, fixture.Line,
                request.ExecutionId, balance.Id, target.Id, 5m, balance.Version, target.Version)));
        owner.ChangeTracker.Clear();
        Assert.Equal(5m, (await owner.Balances.AsNoTracking().SingleAsync(row => row.Id == fixture.Balance)).Quantity);
        Assert.Equal(5m, (await owner.Reservations.AsNoTracking().SingleAsync()).RemainingQuantity);
    }

    [Theory]
    [InlineData("intent")]
    [InlineData("inbox")]
    [InlineData("applied")]
    public async Task InboundTransportImmutableWritesAndDetachedApplicationAreRejected(string kind)
    {
        var (_, request) = await TransportReadyAsync();
        var adapter = new ContractTransportAdapter();
        await using var owner = Open(_tenant);
        await TransportWorker(owner, adapter).DispatchAsync(request.ExecutionId);
        await TransportWorker(owner, adapter).ApplyCompletionAsync(await TransportCompletionAsync(owner));
        await using var transaction = await owner.Database.BeginTransactionAsync();
        if (kind == "intent")
        {
            var intent = await owner.InboundTransportIntents.SingleAsync();
            owner.Entry(intent).Property(row => row.Envelope).CurrentValue += " ";
        }
        else
        {
            var inbox = await owner.InboundTransportInbox.SingleAsync();
            if (kind == "inbox") owner.Entry(inbox).Property(row => row.Body).CurrentValue += " ";
            else inbox.Apply(Guid.NewGuid());
        }
        await Assert.ThrowsAsync<InvalidOperationException>(() => owner.SaveChangesAsync());
        await transaction.RollbackAsync();
    }

    [Fact]
    public async Task InboundTransportDetachedIntentCannotAdoptCommittedReceipt()
    {
        var (_, _) = await TransportReadyAsync();
        await using var owner = Open(_tenant);
        var fact = await owner.InboundFacts.AsNoTracking().SingleAsync(row => row.Kind == InboundFactKind.Received);
        var header = await owner.InboundDocuments.AsNoTracking().SingleAsync();
        var source = await owner.Balances.AsNoTracking().SingleAsync();
        await using var transaction = await owner.Database.BeginTransactionAsync();
        owner.InboundTransportIntents.Add(new(header, fact, source.Id, source.Precision));
        await Assert.ThrowsAsync<InvalidOperationException>(() => owner.SaveChangesAsync());
        await transaction.RollbackAsync();
    }

    [Fact]
    public async Task InboundTransportRequiresExplicitServicePrincipalAndAdapter()
    {
        var (_, request) = await TransportReadyAsync();
        await using var owner = Open(_tenant);
        var adapter = new ContractTransportAdapter();
        Assert.Throws<ArgumentNullException>(() => new WmsInboundTransportWorker(owner, null!, _transportServiceActor,
            _clock, NullLogger<WmsInboundTransportWorker>.Instance));
        Assert.Throws<ArgumentException>(() => new WmsInboundTransportWorker(owner, adapter, Guid.Empty,
            _clock, NullLogger<WmsInboundTransportWorker>.Instance));
        await Assert.ThrowsAsync<InvalidOperationException>(() => new WmsInboundTransportWorker(owner, adapter, Reader.ActorId,
            _clock, NullLogger<WmsInboundTransportWorker>.Instance).DispatchAsync(request.ExecutionId));
        Assert.Empty(adapter.Requests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InboundTransportDatabaseFailureIsSanitizedAndOwnerRemainsClean(bool transient)
    {
        var (_, request) = await TransportReadyAsync();
        await using var owner = Open(_tenant, new TransportQueryFailure(transient));
        var error = await Assert.ThrowsAsync<WmsDatabaseException>(() =>
            TransportWorker(owner, new ContractTransportAdapter()).DispatchAsync(request.ExecutionId));
        Assert.Equal(transient, error.IsTransient);
        Assert.DoesNotContain("sensitive-sentinel", error.ToString(), StringComparison.Ordinal);
        Assert.Empty(owner.ChangeTracker.Entries());
        Assert.Null(owner.Database.CurrentTransaction);
    }

    [Theory]
    [InlineData("dirty")]
    [InlineData("transaction")]
    [InlineData("ambient")]
    public async Task InboundTransportRequiresCleanIndependentOwner(string kind)
    {
        var (_, request) = await TransportReadyAsync();
        await using var owner = Open(_tenant);
        using var ambient = kind == "ambient" ? new System.Transactions.TransactionScope(System.Transactions.TransactionScopeAsyncFlowOption.Enabled) : null;
        await using var transaction = kind == "transaction" ? await owner.Database.BeginTransactionAsync() : null;
        if (kind == "dirty") _ = await owner.InboundDocuments.SingleAsync();
        var worker = TransportWorker(owner, new ContractTransportAdapter());
        await Assert.ThrowsAsync<InvalidOperationException>(() => worker.DispatchAsync(request.ExecutionId));
        if (kind == "dirty") Assert.Single(owner.ChangeTracker.Entries());
    }

    [Fact]
    public async Task InboundTransportEvidenceSnapshotCannotBeMutatedDuringVerification()
    {
        var (_, request) = await TransportReadyAsync();
        var evidence = new List<string> { "fixture-final" };
        var adapter = new ContractTransportAdapter { BeforeVerify = () => evidence[0] = "operator-assertion" };
        await using var owner = Open(_tenant);
        var worker = TransportWorker(owner, adapter);
        await worker.DispatchAsync(request.ExecutionId);
        var completion = (await TransportCompletionAsync(owner)) with { EvidenceRefs = evidence };
        Assert.Equal(InboundTransportDecision.PendingSync, (await worker.ApplyCompletionAsync(completion)).Decision);
        Assert.Equal("fixture-final", Assert.Single((await owner.InboundTransportInbox.AsNoTracking().SingleAsync()).Completion().EvidenceRefs));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InboundTransportUnsupportedAdapterResultsThrow(bool completionPhase)
    {
        var (_, request) = await TransportReadyAsync();
        var adapter = new ContractTransportAdapter();
        await using var owner = Open(_tenant);
        var worker = TransportWorker(owner, adapter);
        if (completionPhase)
        {
            await worker.DispatchAsync(request.ExecutionId);
            adapter.CompletionVerification = (InboundTransportVerification)999;
            var completion = await TransportCompletionAsync(owner);
            await Assert.ThrowsAsync<InvalidOperationException>(() => worker.ApplyCompletionAsync(completion));
        }
        else
        {
            adapter.Acceptance = value => value with { Verification = (InboundTransportVerification)999 };
            await Assert.ThrowsAsync<InvalidOperationException>(() => worker.DispatchAsync(request.ExecutionId));
        }
        Assert.Empty(owner.ChangeTracker.Entries());
    }

    private readonly Guid _transportServiceActor = Guid.NewGuid();

    private WmsInboundTransportWorker TransportWorker(WmsDbContext owner, ContractTransportAdapter adapter) =>
        new(owner, adapter, _transportServiceActor, _clock, NullLogger<WmsInboundTransportWorker>.Instance);

    private async Task<(ExecutionFixture Fixture, InboundReceiptRegistration Request)> TransportReadyAsync(bool registerTarget = false)
    {
        var fixture = await ExecutionReadyAsync();
        var request = await TransportReceiptAsync(fixture);
        if (registerTarget) await RegisterTransportTargetAsync(fixture, request.TargetLocationId);
        await using var owner = Open(_tenant);
        Assert.Equal(InboundExecutionDecision.Committed,
            (await ExecutionAccess(owner).ReceiveAsync(Reader, fixture.Order, request)).Decision);
        return (fixture, request);
    }

    private async Task RegisterTransportTargetAsync(ExecutionFixture fixture, Guid target)
    {
        await using var owner = Open(_tenant);
        var source = await owner.DimensionRegistrations.AsNoTracking().SingleAsync(row => row.BalanceId == fixture.Balance);
        Assert.Equal(StockResourceDecision.Created, (await ResourceAccess(owner).DimensionAsync(Reader,
            new(Guid.NewGuid(), Guid.NewGuid(), fixture.Warehouse, target, fixture.Item, source.Batch, fixture.Container))).Decision);
    }

    private async Task<InboundTransportCompletion> TransportCompletionAsync(WmsDbContext owner)
    {
        var intent = await owner.InboundTransportIntents.AsNoTracking().SingleAsync();
        var request = intent.Request();
        return new(Guid.NewGuid(), Guid.NewGuid(), 1, "Orchestration", _tenant, intent.Id, intent.ExecutionId,
            intent.TransportId!.Value, request.ContainerId, request.SourceLocationId, request.TargetLocationId,
            ["fixture-final"], _clock.GetUtcNow());
    }

    private async Task<InboundView> TransportDetailAsync(WmsDbContext owner, Guid order)
    {
        var result = await new InboundAccess(owner, new TransportReadAuthority(), NullLogger<InboundAccess>.Instance)
            .ReadAsync(new(_tenant, Reader.ActorId, Reader.SessionId), order);
        Assert.Equal(InboundReadDecision.Success, result.Decision);
        return result.Order!;
    }

    private async Task AssertTransportAppliedAsync(WmsDbContext owner, ExecutionFixture fixture,
        InboundTransportCompletion completion, Guid? postingId)
    {
        var balances = await owner.Balances.AsNoTracking().ToListAsync();
        Assert.Equal(0m, balances.Single(row => row.Id == fixture.Balance).Quantity);
        Assert.Equal(5m, balances.Single(row => row.LocationId == completion.FinalLocationId).Quantity);
        var posting = Assert.Single(await owner.Postings.AsNoTracking().Where(row => row.Kind == StockPostingKind.Transfer).ToListAsync());
        Assert.Equal(postingId, posting.Id);
        Assert.Equal(completion.ExecutionId, posting.ExecutionUnitId);
        Assert.Equal(_transportServiceActor, posting.ActorId);
        Assert.Equal(new[] { -5m, 5m }, (await owner.Entries.AsNoTracking().Where(row => row.PostingId == posting.Id)
            .Select(row => row.Quantity).ToArrayAsync()).Order().ToArray());
        Assert.Equal(0m, (await owner.Reservations.AsNoTracking().SingleAsync()).RemainingQuantity);
        Assert.Empty(await InboundPendingProtection.ReadAsync(owner, fixture.Warehouse, default));
        Assert.Equal(4, await owner.InboundFacts.CountAsync());
        Assert.Equal(1, await owner.InboundFacts.CountAsync(row => row.ExecutionId != null));
        var inbox = await owner.InboundTransportInbox.AsNoTracking().SingleAsync();
        Assert.True(inbox.Applied);
        Assert.Equal(posting.Id, inbox.PostingId);
        var detail = await TransportDetailAsync(owner, fixture.Order);
        Assert.Equal(InboundState.Completed, detail.State);
        Assert.True(Assert.Single(detail.Receipts).Arrived);
        Assert.Equal(completion.TransportId, detail.Receipts[0].TransportId);
    }

    private sealed class TransportReadAuthority : IInboundAuthorization
    {
        public Task<IReadOnlySet<string>> ReadFieldsAsync(InboundSubject subject, Guid creator, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlySet<string>>(InboundAccess.Fields.ToHashSet());
        public Task<InboundCreatorAccess> ReadCreatorsAsync(InboundSubject subject, IReadOnlyCollection<string> requiredFields,
            CancellationToken cancellationToken) => throw new InvalidOperationException("No transport catalog.");
    }

    // Contract-only logical acceptance/evidence fixture. It neither controls equipment nor enters production DI.
    private sealed class ContractTransportAdapter : IInboundTransportAdapter
    {
        internal List<string> Requests { get; } = [];
        internal Dictionary<Guid, (string Body, Guid Transport)> Transports { get; } = [];
        internal bool LoseFirstAcceptance { get; init; }
        internal Func<InboundTransportAcceptance, InboundTransportAcceptance>? Acceptance { get; set; }
        internal InboundTransportVerification CompletionVerification { get; set; } = InboundTransportVerification.Verified;
        internal Action? BeforeVerify { get; init; }

        public Task<InboundTransportAcceptance> AcceptAsync(InboundTransportRequest request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (Transports)
            {
                var body = JsonSerializer.Serialize(request);
                Requests.Add(body);
                if (!Transports.TryGetValue(request.IntentId, out var saved))
                    Transports.Add(request.IntentId, saved = (body, Guid.NewGuid()));
                Assert.Equal(saved.Body, body);
                if (LoseFirstAcceptance && Requests.Count == 1) throw new TimeoutException("Fixture lost acceptance response.");
                var result = new InboundTransportAcceptance(InboundTransportVerification.Verified, request.TenantId,
                    request.IntentId, request.ExecutionId, saved.Transport, request.SchemaVersion, "Orchestration");
                return Task.FromResult(Acceptance?.Invoke(result) ?? result);
            }
        }

        public Task<InboundTransportVerification> VerifyCompletionAsync(InboundTransportRequest request,
            InboundTransportCompletion completion, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            BeforeVerify?.Invoke();
            lock (Transports)
            {
                var accepted = Transports.TryGetValue(request.IntentId, out var saved) && saved.Body == JsonSerializer.Serialize(request)
                    && saved.Transport == completion.TransportId;
                return Task.FromResult(accepted && completion.EvidenceRefs.All(value => value.StartsWith("fixture-final", StringComparison.Ordinal))
                    ? CompletionVerification : InboundTransportVerification.Rejected);
            }
        }
    }

    private sealed class TransportSaveFault(string phase, Action action) : SaveChangesInterceptor
    {
        internal bool Reached { get; private set; }
        public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result,
            CancellationToken cancellationToken = default)
        {
            var kind = phase == "arrival" ? InboundFactKind.Arrived : InboundFactKind.TransportLinked;
            if (phase == "inbox" ? eventData.Context!.ChangeTracker.Entries<InboundTransportInbox>().Any(row => !row.Entity.Applied)
                : eventData.Context!.ChangeTracker.Entries<InboundFact>().Any(row => row.Entity.Kind == kind))
            {
                Reached = true;
                action();
            }
            return ValueTask.FromResult(result);
        }
    }

    private sealed class TransportQueryFailure(bool transient) : DbCommandInterceptor
    {
        public override ValueTask<InterceptionResult<System.Data.Common.DbDataReader>> ReaderExecutingAsync(
            System.Data.Common.DbCommand command, CommandEventData eventData,
            InterceptionResult<System.Data.Common.DbDataReader> result, CancellationToken cancellationToken = default) =>
            command.CommandText.Contains("wms_inbound_transport_intents", StringComparison.Ordinal)
                ? throw new DatabaseFailure(transient) : ValueTask.FromResult(result);
    }

    private async Task<InboundReceiptRegistration> TransportReceiptAsync(ExecutionFixture fixture)
    {
        var receipt = await ExecutionReceiptAsync(fixture);
        var target = await ExecuteAsync(Command("TRANSPORT-TARGET") with
        { WarehouseId = fixture.Warehouse, PackId = Guid.NewGuid(), SchemaVersion = "v1" },
            new Schema(Wes.Wms.Features.MasterData.LocationSchemaDecision.Valid));
        Assert.Equal(Wes.Wms.Features.MasterData.LocationStateDecision.Changed,
            (await LocationStateAsync(target.Id!.Value, new(Guid.NewGuid(), target.Version!, true, "Ready"))).Decision);
        return receipt with { TargetLocationId = target.Id.Value };
    }
}
