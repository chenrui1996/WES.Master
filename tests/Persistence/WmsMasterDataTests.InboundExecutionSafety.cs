using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Wes.Wms.Domain;
using Wes.Wms.Features.Inbound;
using Wes.Wms.Features.MasterData;
using Xunit;

namespace Wes.Persistence.Tests;

public sealed partial class WmsMasterDataTests
{
    public static TheoryData<string> ExecutionApprovalFields
    {
        get
        {
            var result = new TheoryData<string>();
            foreach (var (permission, fields) in new (string, string[])[]
            {
                ("wms.inbound.approve", ["State"]),
                ("wms.inbound.read", ["Id", "Code", "WarehouseId", "State", "Version", "CreatedAt", "Lines", "Receipts"]),
                ("wms.warehouses.read", ["Id", "Code", "Name", "Enabled", "Version", "CreatedAt"]),
                ("wms.items.read", ["Id", "Code", "Name", "BaseUnitId", "BatchRequired", "Version", "CreatedAt"]),
                ("wms.units.read", ["Id", "Code", "Name", "Countable", "Precision", "Version", "CreatedAt"])
            })
                foreach (var name in fields) result.Add($"{permission}:{name}");
            return result;
        }
    }

    [Theory]
    [MemberData(nameof(ExecutionApprovalFields))]
    public async Task InboundExecutionApprovalRequiresEveryFieldOnNewAndOriginalReplay(string missing)
    {
        var fixture = await ExecutionReadyAsync(); var request = new InboundApproval(Guid.NewGuid(), Guid.NewGuid(), fixture.OrderVersion);
        await using var owner = Open(_tenant);
        var authority = new ExecutionAuthority { Missing = missing };
        Assert.Equal(new(InboundExecutionDecision.Forbidden), await ExecutionAccess(owner, authority).ApproveAsync(Reader, fixture.Order, request));
        Assert.Empty(await owner.InboundFacts.ToListAsync()); owner.ChangeTracker.Clear();
        Assert.Equal(InboundExecutionDecision.Committed, (await ExecutionAccess(owner).ApproveAsync(Reader, fixture.Order, request)).Decision);
        Assert.Equal(new(InboundExecutionDecision.Forbidden), await ExecutionAccess(owner, authority).ApproveAsync(Reader, fixture.Order, request));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InboundExecutionSaveTimeRevocationRollsBackEveryEffect(bool receiving)
    {
        var fixture = await ExecutionReadyAsync();
        var request = receiving ? await ExecutionReceiptAsync(fixture) : null;
        var authority = new ExecutionAuthority();
        await using (var owner = Open(_tenant, new ExecutionSaveAction(() => authority.Revoked = true)))
        {
            var result = receiving ? await ExecutionAccess(owner, authority).ReceiveAsync(Reader, fixture.Order, request!)
                : await ExecutionAccess(owner, authority).ApproveAsync(Reader, fixture.Order, new(Guid.NewGuid(), Guid.NewGuid(), fixture.OrderVersion));
            Assert.Equal(new(InboundExecutionDecision.Forbidden), result);
        }
        await AssertExecutionUnchangedAsync(receiving);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task InboundExecutionFaultOrCancellationAfterActualSaveIsAtomic(bool receiving, bool cancel)
    {
        var fixture = await ExecutionReadyAsync(); var receipt = receiving ? await ExecutionReceiptAsync(fixture) : null;
        using var cancellation = new CancellationTokenSource();
        await using (var owner = Open(_tenant, new AfterSaveFault(cancel, cancellation)))
        {
            Task Execute() => receiving ? ExecutionAccess(owner).ReceiveAsync(Reader, fixture.Order, receipt!, cancellation.Token)
                : ExecutionAccess(owner).ApproveAsync(Reader, fixture.Order, new(Guid.NewGuid(), Guid.NewGuid(), fixture.OrderVersion), cancellation.Token);
            if (cancel) await Assert.ThrowsAnyAsync<OperationCanceledException>(Execute);
            else await Assert.ThrowsAsync<InvalidOperationException>(Execute);
        }
        await AssertExecutionUnchangedAsync(receiving);
    }

    [Theory]
    [InlineData(LocationSchemaDecision.Rejected)]
    [InlineData(LocationSchemaDecision.Unavailable)]
    public async Task InboundExecutionReceiptNeedsCurrentPinnedSchemaBeforeAnyEffects(LocationSchemaDecision evidence)
    {
        var fixture = await ExecutionReadyAsync(); var request = await ExecutionReceiptAsync(fixture);
        await using var owner = Open(_tenant); var schema = new Schema(evidence);
        Assert.Equal(new(evidence == LocationSchemaDecision.Rejected ? InboundExecutionDecision.SchemaRejected : InboundExecutionDecision.SchemaUnavailable),
            await ExecutionAccess(owner, schema: schema).ReceiveAsync(Reader, fixture.Order, request));
        Assert.Single(schema.Inputs); await AssertExecutionUnchangedAsync(true);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("5.001")]
    [InlineData("1.0001")]
    [InlineData("1e0")]
    [InlineData(" 1")]
    [InlineData("01")]
    public async Task InboundExecutionReceiptDoesNotRoundOrOverreceive(string quantity)
    {
        var fixture = await ExecutionReadyAsync(); var request = await ExecutionReceiptAsync(fixture);
        await using var owner = Open(_tenant);
        Assert.Equal(new(InboundExecutionDecision.Invalid), await ExecutionAccess(owner).ReceiveAsync(Reader, fixture.Order, request with { Quantity = quantity }));
        await AssertExecutionUnchangedAsync(true);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InboundExecutionConcurrentSameOrDifferentKeysCannotDuplicateReceipt(bool same)
    {
        var fixture = await ExecutionReadyAsync(); var request = await ExecutionReceiptAsync(fixture);
        async Task<InboundExecutionResult> Execute(InboundReceiptRegistration proposed)
        {
            await using var owner = Open(_tenant); return await ExecutionAccess(owner).ReceiveAsync(Reader, fixture.Order, proposed);
        }
        var results = await Task.WhenAll(Execute(request), Execute(same ? request : request with { OperationId = Guid.NewGuid(), ExecutionId = Guid.NewGuid() }));
        Assert.Single(results, result => result.Decision == InboundExecutionDecision.Committed);
        Assert.Single(results, result => result.Decision == (same ? InboundExecutionDecision.Replayed : InboundExecutionDecision.Conflict));
        await using var verify = Open(_tenant);
        Assert.Equal(5m, (await verify.Balances.SingleAsync()).Quantity);
        Assert.Single(await verify.Postings.ToListAsync()); Assert.Equal(2, await verify.InboundExecutionOperations.CountAsync());
    }

    [Theory]
    [InlineData("other-container")]
    [InlineData("other-location")]
    [InlineData("reservation")]
    [InlineData("zero")]
    public async Task InboundExecutionReceiptChecksActualOccupancyAndProtection(string activity)
    {
        var fixture = await ExecutionReadyAsync(); var request = await ExecutionReceiptAsync(fixture);
        await using (var seed = Open(_tenant))
        {
            var other = new StockBalance(_tenant, fixture.Warehouse, activity == "other-location" ? Guid.NewGuid() : fixture.Location,
                fixture.Item, "", activity == "other-location" ? fixture.Container : Guid.NewGuid(), fixture.Unit, 3);
            seed.Balances.Add(other); await seed.SaveChangesAsync();
            if (activity is "other-container" or "other-location")
                await seed.Database.ExecuteSqlInterpolatedAsync($"UPDATE wms_stock_balances SET Quantity = 1 WHERE TenantId = {_tenant.ToString("D")} AND Id = {other.Id.ToString("D")}");
            if (activity == "reservation")
            {
                await seed.Database.ExecuteSqlInterpolatedAsync($"UPDATE wms_stock_balances SET Quantity = 1 WHERE TenantId = {_tenant.ToString("D")} AND Id = {fixture.Balance.ToString("D")}");
                await seed.Database.ExecuteSqlInterpolatedAsync($@"INSERT INTO wms_stock_reservations
                    (TenantId, ExecutionUnitId, BalanceId, SourceDocumentId, SourceLineId, ActorId, OriginalQuantity, RemainingQuantity, Kind, CreatedAt)
                    VALUES ({_tenant.ToString("D")}, {Guid.NewGuid().ToString("D")}, {fixture.Balance.ToString("D")}, {fixture.Order.ToString("D")},
                    {fixture.Line.ToString("D")}, {_actor.ToString("D")}, 1, 1, 1, {_clock.GetUtcNow().UtcDateTime})");
            }
        }
        await using var owner = Open(_tenant);
        Assert.Equal(activity == "zero" ? InboundExecutionDecision.Committed : InboundExecutionDecision.Blocked,
            (await ExecutionAccess(owner).ReceiveAsync(Reader, fixture.Order, request)).Decision);
    }

    [Fact]
    public async Task InboundExecutionOriginalReceiptDoesNotReexecuteSchemaButStillRequiresCurrentAuthority()
    {
        var fixture = await ExecutionReadyAsync(); var request = await ExecutionReceiptAsync(fixture);
        await using var owner = Open(_tenant);
        var result = await ExecutionAccess(owner).ReceiveAsync(Reader, fixture.Order, request);
        var schema = new Schema(LocationSchemaDecision.Unavailable);
        Assert.Equal(result with { Decision = InboundExecutionDecision.Replayed },
            await ExecutionAccess(owner, schema: schema).ReceiveAsync(Reader, fixture.Order, request));
        Assert.Empty(schema.Inputs);
        Assert.Equal(new(InboundExecutionDecision.Forbidden),
            await ExecutionAccess(owner, new ExecutionAuthority { Revoked = true }).ReceiveAsync(Reader, fixture.Order, request));
    }

    private async Task<InboundReceiptRegistration> ExecutionReceiptAsync(ExecutionFixture fixture)
    {
        await using var owner = Open(_tenant);
        var approved = await ExecutionAccess(owner).ApproveAsync(Reader, fixture.Order, new(Guid.NewGuid(), Guid.NewGuid(), fixture.OrderVersion));
        Assert.Equal(InboundExecutionDecision.Committed, approved.Decision);
        return new(Guid.NewGuid(), Guid.NewGuid(), approved.Version!, fixture.BalanceVersion, fixture.Line, Guid.NewGuid(),
            "5.000", fixture.Container, fixture.Location, fixture.Location, fixture.Balance);
    }
    private async Task AssertExecutionUnchangedAsync(bool approved)
    {
        await using var verify = Open(_tenant);
        Assert.Equal(approved ? InboundOrderState.Approved : InboundOrderState.Draft, (await verify.InboundDocuments.SingleAsync()).State);
        Assert.Equal(approved ? 1 : 0, await verify.InboundFacts.CountAsync());
        Assert.Equal(approved ? 1 : 0, await verify.InboundExecutionOperations.CountAsync());
        Assert.Empty(await verify.Postings.ToListAsync()); Assert.Empty(await verify.Entries.ToListAsync());
        Assert.Equal(0m, (await verify.Balances.SingleAsync()).Quantity);
    }
    private sealed class ExecutionSaveAction(Action action) : SaveChangesInterceptor
    {
        public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
        { action(); return ValueTask.FromResult(result); }
    }
}
