using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Wes.Wms.Domain;
using Wes.Wms.Features.Inbound;
using Wes.Wms.Features.MasterData;
using Xunit;

namespace Wes.Persistence.Tests;

public sealed partial class WmsLedgerTests
{
    [Fact]
    public async Task InboundPartialReceiptsAndPermanentReplayCommitWithExactLedgerFacts()
    {
        var (document, line) = await InboundAsync();
        var command = await InboundCommandAsync(line, 2m);
        var first = await ReceiveInboundAsync(document.Id, command);
        Assert.Equal(InboundWriteDecision.Committed, first.Decision);
        Assert.Equal(InboundWriteDecision.Replayed, (await ReceiveInboundAsync(document.Id, command)).Decision);
        Assert.Equal(InboundWriteDecision.Replayed,
            (await ReceiveInboundAsync(document.Id, command with { OperationId = Guid.NewGuid() })).Decision);
        Assert.Equal(InboundWriteDecision.Conflict,
            (await ReceiveInboundAsync(document.Id, command with { Quantity = 3m })).Decision);
        Assert.Equal(InboundWriteDecision.Invalid,
            (await ReceiveInboundAsync(document.Id, await InboundCommandAsync(line, 4m))).Decision);
        Assert.Equal(InboundWriteDecision.Committed,
            (await ReceiveInboundAsync(document.Id, await InboundCommandAsync(line, 3m))).Decision);
        Assert.Equal(first with { Decision = InboundWriteDecision.Replayed }, await ReceiveInboundAsync(document.Id, command));
        Assert.Equal(InboundWriteDecision.Conflict,
            (await ReceiveInboundAsync(document.Id, command with { ActorId = Guid.NewGuid() })).Decision);
        Assert.Equal(InboundWriteDecision.Conflict,
            (await ReceiveInboundAsync(document.Id, command with { ExecutionId = Guid.NewGuid() })).Decision);
        await using var verify = Open(_tenant);
        var saved = await verify.InboundDocuments.SingleAsync();
        Assert.Equal(InboundOrderState.Completed, saved.State);
        var facts = await verify.InboundFacts.OrderBy(item => item.Sequence).ToListAsync();
        Assert.Equal(3, facts.Count);
        Assert.Equal(InboundOrderState.Completed, saved.Restore(facts).State);
        Assert.Equal(5m, (await verify.Balances.SingleAsync(item => item.Id == _source)).Quantity);
        Assert.Equal(2, await verify.Postings.CountAsync());
        Assert.Equal(2, await verify.Entries.CountAsync());
        Assert.All(facts.Where(fact => fact.Kind == InboundFactKind.Received), fact => Assert.NotNull(fact.PostingId));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InboundSavedThenFaultRollsBackHeaderReceiptAndLedgerTogether(bool cancel)
    {
        var (document, line) = await InboundAsync();
        var command = await InboundCommandAsync(line, 5m);
        using var cancellation = new CancellationTokenSource();
        await using (var broken = Open(_tenant, new AfterSaveFault(cancel, cancellation)))
        {
            var balance = await broken.Balances.AsNoTracking().SingleAsync(item => item.Id == _source);
            Task<InboundWriteResult> Execute() => new InboundWriter(broken, _clock).ReceiveAsync(
                document.Id, command, Guid.NewGuid(), document.Version, _source, balance.Version, cancellation.Token);
            if (cancel) await Assert.ThrowsAnyAsync<OperationCanceledException>(Execute);
            else await Assert.ThrowsAsync<InvalidOperationException>(Execute);
        }
        await using (var verify = Open(_tenant))
        {
            Assert.Equal(document.Version, (await verify.InboundDocuments.SingleAsync()).Version);
            Assert.Single(await verify.InboundFacts.ToListAsync());
            Assert.Empty(await verify.Postings.ToListAsync());
            Assert.Empty(await verify.Entries.ToListAsync());
            Assert.Equal(0m, (await verify.Balances.SingleAsync(item => item.Id == _source)).Quantity);
        }
        Assert.Equal(InboundWriteDecision.Committed, (await ReceiveInboundAsync(document.Id, command)).Decision);
    }

    [Fact]
    public async Task InboundCannotReceiveIntoDifferentContainerAndOtherTenantCannotReadOrder()
    {
        var (document, line) = await InboundAsync();
        var command = await InboundCommandAsync(line, 1m);
        Assert.Equal(InboundWriteDecision.DimensionMismatch,
            (await ReceiveInboundAsync(document.Id, command with { ContainerId = Guid.NewGuid() })).Decision);
        await using var other = Open(_other);
        Assert.Empty(await other.InboundDocuments.ToListAsync());
        Assert.Empty(await other.InboundFacts.ToListAsync());
        Assert.Equal(InboundWriteDecision.NotFound, (await new InboundWriter(other, _clock).ReceiveAsync(
            document.Id, command, Guid.NewGuid(), document.Version, _source, Guid.NewGuid().ToString("D"))).Decision);
    }

    [Fact]
    public async Task ConcurrentInboundReceiptsCannotOverreceiveTheSameRemainingPlan()
    {
        var (document, line) = await InboundAsync();
        var first = await InboundCommandAsync(line, 3m);
        var second = await InboundCommandAsync(line, 3m);
        var results = await Task.WhenAll(ReceiveInboundAsync(document.Id, first), ReceiveInboundAsync(document.Id, second));
        Assert.Single(results, item => item.Decision == InboundWriteDecision.Committed);
        Assert.Single(results, item => item.Decision is InboundWriteDecision.VersionConflict or InboundWriteDecision.Invalid);
        await using var verify = Open(_tenant);
        Assert.Equal(3m, (await verify.Balances.SingleAsync(item => item.Id == _source)).Quantity);
        Assert.Single(await verify.Postings.ToListAsync());
        Assert.Equal(2, await verify.InboundFacts.CountAsync());
    }

    [Fact]
    public async Task DifferentContainersStillCompeteOnTheSameOrderPlanFence()
    {
        var (document, line) = await InboundAsync();
        var first = await InboundCommandAsync(line, 3m);
        Guid secondBalance; string secondVersion; InboundReceiptCommand second;
        await using (var owner = Open(_tenant))
        {
            var balance = new StockBalance(_tenant, document.WarehouseId, first.ReceiptLocationId,
                line.ItemId, line.Batch, Guid.NewGuid(), line.UnitId, line.Precision);
            owner.Balances.Add(balance); await owner.SaveChangesAsync();
            secondBalance = balance.Id; secondVersion = balance.Version;
            second = first with { OperationId = Guid.NewGuid(), ExecutionId = Guid.NewGuid(), ContainerId = balance.ContainerId };
        }
        await using var competitor = Open(_tenant);
        var results = await Task.WhenAll(ReceiveInboundAsync(document.Id, first),
            new InboundWriter(competitor, _clock).ReceiveAsync(document.Id, second, Guid.NewGuid(),
                document.Version, secondBalance, secondVersion));
        Assert.Single(results, item => item.Decision == InboundWriteDecision.Committed);
        Assert.Single(results, item => item.Decision is InboundWriteDecision.VersionConflict or InboundWriteDecision.Invalid);
        await using var verify = Open(_tenant);
        Assert.Equal(3m, await verify.Balances.Where(item => item.Id == _source || item.Id == secondBalance).SumAsync(item => item.Quantity));
        Assert.Single(await verify.Postings.ToListAsync());
    }

    [Fact]
    public async Task DifferentTargetPersistsReceiptAtSourceAndRestoresPendingTransportRatherThanFakeCompletion()
    {
        var (document, line) = await InboundAsync();
        var command = (await InboundCommandAsync(line, 5m)) with { TargetLocationId = Guid.NewGuid() };
        Assert.Equal(InboundWriteDecision.Committed, (await ReceiveInboundAsync(document.Id, command)).Decision);
        await using var verify = Open(_tenant);
        var header = await verify.InboundDocuments.SingleAsync();
        var domain = header.Restore(await verify.InboundFacts.OrderBy(item => item.Sequence).ToListAsync());
        var unit = Assert.Single(domain.Receipts);
        Assert.Equal(command.ExecutionId, unit.Id);
        Assert.True(unit.RequiresTransport); Assert.False(unit.Arrived); Assert.Null(unit.TransportId);
        Assert.Equal(InboundOrderState.Executing, domain.State);
        Assert.Equal(5m, (await verify.Balances.SingleAsync(item => item.Id == _source)).Quantity);
        Assert.Equal(StockPostingKind.Receipt, (await verify.Postings.SingleAsync()).Kind);
        Assert.Single(await verify.Entries.ToListAsync());
    }

    [Fact]
    public async Task InboundHistoryCannotBeEditedAndHeaderCannotAdvanceWithoutFact()
    {
        var (document, _) = await InboundAsync();
        await using (var owner = Open(_tenant))
        {
            var fact = await owner.InboundFacts.SingleAsync();
            owner.Entry(fact).Property(item => item.ActorId).CurrentValue = Guid.NewGuid();
            await Assert.ThrowsAsync<InvalidOperationException>(() => owner.SaveChangesAsync());
        }
        await using (var owner = Open(_tenant))
        {
            var header = await owner.InboundDocuments.SingleAsync();
            header.Advance(InboundOrderState.Completed);
            await Assert.ThrowsAsync<InvalidOperationException>(() => owner.SaveChangesAsync());
        }
        await using var verify = Open(_tenant);
        Assert.Equal(document.Version, (await verify.InboundDocuments.SingleAsync()).Version);
        Assert.Equal(_actor, (await verify.InboundFacts.SingleAsync()).ActorId);
    }

    [Fact]
    public async Task ReceiptCommittedAfterSourceReadReturnsOriginalReceiptWithoutASecondStage()
    {
        var (document, line) = await InboundAsync();
        var command = await InboundCommandAsync(line, 2m);
        InboundWriteResult? first = null;
        var interleave = new BeforeInboundPostingRead(async () => first = await ReceiveInboundAsync(document.Id, command));
        await using var owner = Open(_tenant, interleave);
        var balance = await owner.Balances.AsNoTracking().SingleAsync(item => item.Id == _source);
        var result = await new InboundWriter(owner, _clock).ReceiveAsync(document.Id, command,
            Guid.NewGuid(), document.Version, _source, balance.Version);
        Assert.True(interleave.Reached);
        Assert.NotNull(first);
        Assert.Equal(InboundWriteDecision.Committed, first.Decision);
        Assert.Equal(first with { Decision = InboundWriteDecision.Replayed }, result);
        await using var verify = Open(_tenant);
        Assert.Single(await verify.Postings.ToListAsync());
        Assert.Equal(2, await verify.InboundFacts.CountAsync());
        Assert.Equal(2m, (await verify.Balances.SingleAsync(item => item.Id == _source)).Quantity);
    }

    private sealed class BeforeInboundPostingRead(Func<Task> commit) : DbCommandInterceptor
    {
        public bool Reached { get; private set; }
        public override async ValueTask<InterceptionResult<System.Data.Common.DbDataReader>> ReaderExecutingAsync(
            System.Data.Common.DbCommand command, CommandEventData eventData,
            InterceptionResult<System.Data.Common.DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (!Reached && command.CommandText.Contains("wms_stock_postings", StringComparison.Ordinal))
            {
                Reached = true;
                await commit();
            }
            return result;
        }
    }

    [Fact]
    public async Task InboundDraftRequiresApprovalAndCreationAndApprovalHaveDurableReplay()
    {
        var (document, line) = await InboundAsync(false);
        var command = await InboundCommandAsync(line, 1m);
        Assert.Equal(InboundWriteDecision.Conflict, (await ReceiveInboundAsync(document.Id, command)).Decision);
        await using (var owner = Open(_tenant))
        {
            var replay = await new InboundWriter(owner, _clock).CreateAsync(document.CreationOperationId,
                _actor, document.WarehouseId, document.Code, [line]);
            Assert.Equal(new(InboundWriteDecision.Replayed, document.Id, document.Version), replay);
        }
        await using (var owner = Open(_tenant))
            Assert.Equal(InboundWriteDecision.Conflict, (await new InboundWriter(owner, _clock).CreateAsync(
                document.CreationOperationId, _actor, document.WarehouseId, document.Code, [line with { PlannedQuantity = 6m }])).Decision);
        var operation = Guid.NewGuid();
        InboundWriteResult approved;
        await using (var owner = Open(_tenant))
        {
            approved = await new InboundWriter(owner, _clock).ApproveAsync(
                document.Id, operation, _actor, Guid.NewGuid(), document.Version);
            Assert.Equal(InboundWriteDecision.Committed, approved.Decision);
        }
        Assert.Equal(InboundWriteDecision.Committed, (await ReceiveInboundAsync(document.Id, command)).Decision);
        await using (var owner = Open(_tenant))
            Assert.Equal(approved with { Decision = InboundWriteDecision.Replayed }, await new InboundWriter(owner, _clock).ApproveAsync(
                document.Id, operation, _actor, Guid.NewGuid(), document.Version));
        await using (var owner = Open(_tenant))
            Assert.Equal(InboundWriteDecision.Conflict, (await new InboundWriter(owner, _clock).ApproveAsync(
                document.Id, operation, Guid.NewGuid(), Guid.NewGuid(), document.Version)).Decision);
        await using var verify = Open(_tenant);
        Assert.Equal(2, await verify.InboundFacts.CountAsync());
        Assert.Single(await verify.Postings.ToListAsync());
    }

    [Fact]
    public async Task InboundFactCannotDisguiseCompletionAsApproval()
    {
        var (document, _) = await InboundAsync();
        document.Advance(InboundOrderState.Completed);
        Assert.Throws<ArgumentException>(() => new InboundFact(document, Guid.NewGuid(), _actor, Guid.NewGuid(),
            InboundFactKind.Approved, _clock.GetUtcNow()));
    }

    [Fact]
    public async Task InboundApprovalCannotPersistUnrelatedPayload()
    {
        var (document, _) = await InboundAsync(false);
        await using (var owner = Open(_tenant))
        {
            var header = await owner.InboundDocuments.SingleAsync(item => item.Id == document.Id);
            header.Advance(InboundOrderState.Approved);
            var fact = new InboundFact(header, Guid.NewGuid(), _actor, Guid.NewGuid(),
                InboundFactKind.Approved, _clock.GetUtcNow());
            owner.InboundFacts.Add(fact);
            owner.Entry(fact).Property(item => item.Payload).CurrentValue = "unrelated";
            await Assert.ThrowsAsync<InvalidOperationException>(() => owner.SaveChangesAsync());
        }
        await using var verify = Open(_tenant);
        Assert.Equal(document.Version, (await verify.InboundDocuments.SingleAsync()).Version);
        Assert.Empty(await verify.InboundFacts.ToListAsync());
        Assert.Empty(await verify.Postings.ToListAsync());
    }

    private async Task<(InboundDocument Document, InboundPlanLine Line)> InboundAsync(bool approve = true)
    {
        Guid warehouse;
        await using (var owner = Open(_tenant))
        {
            var result = await new CreateMasterData(owner, _clock, new InboundUnusedSchema()).ExecuteAsync(
                new(Guid.NewGuid(), _actor, Guid.NewGuid(), "IN-WH", "Inbound warehouse"));
            warehouse = result.Id!.Value;
        }
        var line = new InboundPlanLine(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "", 0, 5m);
        await using (var owner = Open(_tenant))
        {
            var balance = new StockBalance(_tenant, warehouse, Guid.NewGuid(), line.ItemId, "", Guid.NewGuid(), line.UnitId, 0);
            owner.Balances.Add(balance); await owner.SaveChangesAsync(); _source = balance.Id;
        }
        Guid id;
        await using (var owner = Open(_tenant))
        {
            var created = await new InboundWriter(owner, _clock).CreateAsync(Guid.NewGuid(), _actor, warehouse, "IN-1", [line]);
            Assert.Equal(InboundWriteDecision.Committed, created.Decision); id = created.Id!.Value;
        }
        if (approve)
        {
            await using var owner = Open(_tenant);
            var document = await owner.InboundDocuments.AsNoTracking().SingleAsync(item => item.Id == id);
            Assert.Equal(InboundWriteDecision.Committed, (await new InboundWriter(owner, _clock).ApproveAsync(
                id, Guid.NewGuid(), _actor, Guid.NewGuid(), document.Version)).Decision);
        }
        await using var verify = Open(_tenant);
        return (await verify.InboundDocuments.AsNoTracking().SingleAsync(item => item.Id == id), line);
    }

    private async Task<InboundReceiptCommand> InboundCommandAsync(InboundPlanLine line, decimal quantity)
    {
        await using var owner = Open(_tenant);
        var balance = await owner.Balances.SingleAsync(item => item.Id == _source);
        return new(Guid.NewGuid(), _actor, Guid.NewGuid(), line.Id, line.ItemId, line.UnitId, line.Batch,
            balance.ContainerId, balance.LocationId, balance.LocationId, quantity);
    }

    private async Task<InboundWriteResult> ReceiveInboundAsync(Guid id, InboundReceiptCommand command)
    {
        await using var owner = Open(_tenant);
        var document = await owner.InboundDocuments.AsNoTracking().SingleAsync(item => item.Id == id);
        var balance = await owner.Balances.AsNoTracking().SingleAsync(item => item.Id == _source);
        return await new InboundWriter(owner, _clock).ReceiveAsync(id, command, Guid.NewGuid(), document.Version, _source, balance.Version);
    }

    private sealed class InboundUnusedSchema : IWarehouseLocationSchema
    {
        public Task<LocationSchemaDecision> ValidateAsync(Guid tenantId, Guid warehouseId, Guid packId,
            string schemaVersion, string code, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Warehouse creation must not request a location schema.");
    }
}
