using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Wes.Wms.Domain;
using Wes.Wms.Features.Inbound;
using Wes.Wms.Features.MasterData;
using Xunit;

namespace Wes.Persistence.Tests;

public sealed partial class WmsMasterDataTests
{
    [Fact]
    public async Task InboundExecutionApprovesOnlyLegalPlanAndRecoversExactOriginalRequest()
    {
        var fixture = await ExecutionReadyAsync();
        var request = new InboundApproval(Guid.NewGuid(), Guid.NewGuid(), fixture.OrderVersion);
        await using var owner = Open(_tenant);
        var created = await ExecutionAccess(owner).ApproveAsync(Reader, fixture.Order, request);
        Assert.Equal(InboundExecutionDecision.Committed, created.Decision); Assert.Equal(fixture.Order, created.Id);
        Assert.Equal(created with { Decision = InboundExecutionDecision.Replayed },
            await ExecutionAccess(owner).ApproveAsync(Reader, fixture.Order, request with { CorrelationId = Guid.NewGuid() }));
        Assert.Equal(new(InboundExecutionDecision.Conflict),
            await ExecutionAccess(owner).ApproveAsync(Reader, fixture.Order, request with { ExpectedVersion = Guid.NewGuid().ToString("D") }));
        await using var verify = Open(_tenant);
        Assert.Equal(InboundOrderState.Approved, (await verify.InboundDocuments.SingleAsync()).State);
        Assert.Single(await verify.InboundFacts.ToListAsync()); Assert.Single(await verify.InboundExecutionOperations.ToListAsync());
        Assert.Empty(await verify.Postings.ToListAsync()); Assert.Equal(0m, (await verify.Balances.SingleAsync()).Quantity);
    }

    [Fact]
    public async Task InboundExecutionSameLocationReceiptCommitsOneFactAndLedgerWithExactOriginalVersions()
    {
        var fixture = await ExecutionReadyAsync();
        await using var owner = Open(_tenant);
        var approved = await ExecutionAccess(owner).ApproveAsync(Reader, fixture.Order,
            new(Guid.NewGuid(), Guid.NewGuid(), fixture.OrderVersion));
        var request = new InboundReceiptRegistration(Guid.NewGuid(), Guid.NewGuid(), approved.Version!, fixture.BalanceVersion,
            fixture.Line, Guid.NewGuid(), "5.000", fixture.Container, fixture.Location, fixture.Location, fixture.Balance);
        var received = await ExecutionAccess(owner).ReceiveAsync(Reader, fixture.Order, request);
        Assert.Equal(InboundExecutionDecision.Committed, received.Decision); Assert.Equal(request.ExecutionId, received.Id);
        Assert.Equal(received with { Decision = InboundExecutionDecision.Replayed },
            await ExecutionAccess(owner).ReceiveAsync(Reader, fixture.Order, request));
        Assert.Equal(new(InboundExecutionDecision.Conflict),
            await ExecutionAccess(owner).ReceiveAsync(Reader, fixture.Order, request with { Quantity = "5" }));
        await using var verify = Open(_tenant);
        Assert.Equal(InboundOrderState.Completed, (await verify.InboundDocuments.SingleAsync()).State);
        Assert.Equal(5m, (await verify.Balances.SingleAsync()).Quantity);
        Assert.Single(await verify.Postings.ToListAsync()); Assert.Single(await verify.Entries.ToListAsync());
        Assert.Equal(2, await verify.InboundFacts.CountAsync()); Assert.Equal(2, await verify.InboundExecutionOperations.CountAsync());
    }

    private InboundExecutionAccess ExecutionAccess(Wes.Wms.Infrastructure.Persistence.WmsDbContext owner,
        IMasterDataAuthorization? authority = null, Schema? schema = null) =>
        new(owner, authority ?? new ExecutionAuthority(), schema ?? new Schema(LocationSchemaDecision.Valid),
            _clock, NullLogger<InboundExecutionAccess>.Instance);

    private async Task<ExecutionFixture> ExecutionReadyAsync()
    {
        var dimension = await LegalDimensionReadyAsync();
        await using var resources = Open(_tenant);
        var registered = await ResourceAccess(resources).DimensionAsync(Reader, dimension);
        var unit = await resources.StockItems.AsNoTracking().Where(row => row.Id == dimension.ItemId).Select(row => row.BaseUnitId).SingleAsync();
        var line = Guid.NewGuid();
        await using var creation = Open(_tenant);
        var order = await new InboundWriter(creation, _clock).CreateAsync(Guid.NewGuid(), Reader.ActorId, dimension.WarehouseId,
            "EXECUTION-IN", [new(line, dimension.ItemId, unit, dimension.Batch, 3, 5m)]);
        return new(order.Id!.Value, line, dimension.WarehouseId, dimension.LocationId, dimension.ItemId, unit,
            dimension.ContainerId, registered.Id!.Value, order.Version!, registered.Version!);
    }

    private sealed record ExecutionFixture(Guid Order, Guid Line, Guid Warehouse, Guid Location, Guid Item, Guid Unit,
        Guid Container, Guid Balance, string OrderVersion, string BalanceVersion);

    private sealed class ExecutionAuthority : IMasterDataAuthorization
    {
        internal string? Missing { get; init; }
        internal bool Revoked { get; set; }
        internal Dictionary<string, Guid> Creators { get; init; } = [];
        public Task<IReadOnlySet<string>> ReadFieldsAsync(MasterDataSubject subject, string permission, Guid creator, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (Creators.TryGetValue(permission, out var expected)) Assert.Equal(expected, creator);
            string[] fields =
            {
                "Id", "Code", "Name", "Version", "CreatedAt", "Countable", "Precision", "BaseUnitId", "BatchRequired",
                "WarehouseId", "LocationId", "ItemId", "Batch", "ContainerId", "Enabled", "PackId", "SchemaVersion",
                "State", "Lines", "Receipts", "LineId", "ExecutionId", "Quantity", "ReceiptLocationId", "TargetLocationId", "BalanceId"
            };
            return Task.FromResult<IReadOnlySet<string>>(Revoked ? new HashSet<string>()
                : fields.Where(field => $"{permission}:{field}" != Missing).ToHashSet());
        }
        public Task<MasterDataCreatorAccess> ReadCreatorsAsync(MasterDataSubject subject, string permission,
            IReadOnlyCollection<string> requiredFields, CancellationToken ct) => throw new InvalidOperationException("No execution catalog.");
    }
}
