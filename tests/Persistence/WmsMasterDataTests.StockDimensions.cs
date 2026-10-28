using Microsoft.EntityFrameworkCore;
using Wes.Wms.Domain;
using Wes.Wms.Features.MasterData;
using Wes.Wms.Features.Ledger;
using Xunit;

namespace Wes.Persistence.Tests;

public sealed partial class WmsMasterDataTests
{
    public static TheoryData<string> DimensionAuthorityFields
    {
        get
        {
            var data = new TheoryData<string>();
            foreach (var (permission, fields) in new (string, string[])[]
            {
                ("wms.dimensions.write", ["WarehouseId", "LocationId", "ItemId", "Batch", "ContainerId"]),
                ("wms.units.read", ["Id", "Code", "Name", "Countable", "Precision", "Version", "CreatedAt"]),
                ("wms.items.read", ["Id", "Code", "Name", "BaseUnitId", "BatchRequired", "Version", "CreatedAt"]),
                ("wms.containers.read", ["Id", "Code", "Name", "WarehouseId", "Version", "CreatedAt"]),
                ("wms.warehouses.read", ["Id", "Code", "Name", "Enabled", "Version", "CreatedAt"]),
                ("wms.locations.read", ["Id", "Code", "Name", "Enabled", "Version", "CreatedAt", "WarehouseId", "PackId", "SchemaVersion"])
            })
                foreach (var name in fields) data.Add($"{permission}:{name}");
            return data;
        }
    }

    [Theory]
    [MemberData(nameof(DimensionAuthorityFields))]
    public async Task LegalResourceDimensionRequiresEveryCurrentFieldOnNewAndOriginalReplay(string missing)
    {
        var command = await LegalDimensionReadyAsync();
        await using var owner = Open(_tenant);
        Assert.Equal(new(StockResourceDecision.Forbidden),
            await ResourceAccess(owner, new ResourceAuthority { Missing = missing }).DimensionAsync(Reader, command));
        Assert.Empty(await owner.Balances.ToListAsync()); owner.ChangeTracker.Clear();
        Assert.Equal(StockResourceDecision.Created, (await ResourceAccess(owner).DimensionAsync(Reader, command)).Decision);
        Assert.Equal(new(StockResourceDecision.Forbidden),
            await ResourceAccess(owner, new ResourceAuthority { Missing = missing }).DimensionAsync(Reader, command));
    }

    [Theory]
    [InlineData(true, "", false)]
    [InlineData(true, "LOT-1", true)]
    [InlineData(false, "", true)]
    [InlineData(false, "LOT-1", false)]
    [InlineData(true, " LOT-1", false)]
    [InlineData(true, "LOT\n1", false)]
    public async Task LegalResourceDimensionEnforcesExactMaterialBatchPolicy(bool required, string batch, bool valid)
    {
        var command = await LegalDimensionReadyAsync(required);
        await using var owner = Open(_tenant);
        Assert.Equal(valid ? StockResourceDecision.Created : StockResourceDecision.Invalid,
            (await ResourceAccess(owner).DimensionAsync(Reader, command with { Batch = batch })).Decision);
        Assert.Equal(valid ? 1 : 0, await owner.Balances.CountAsync());
    }

    [Theory]
    [InlineData(LocationSchemaDecision.Rejected)]
    [InlineData(LocationSchemaDecision.Unavailable)]
    public async Task LegalResourceDimensionNeedsCurrentPinnedSchemaBeforeAnyBalance(LocationSchemaDecision evidence)
    {
        var command = await LegalDimensionReadyAsync();
        var schema = new Schema(evidence);
        await using var owner = Open(_tenant);
        var result = await ResourceAccess(owner, schema: schema).DimensionAsync(Reader, command);
        Assert.Equal(evidence == LocationSchemaDecision.Rejected ? StockResourceDecision.SchemaRejected : StockResourceDecision.SchemaUnavailable, result.Decision);
        var location = await owner.Locations.AsNoTracking().SingleAsync();
        Assert.Equal((_tenant, location.WarehouseId, location.PackId, location.SchemaVersion, location.Code), Assert.Single(schema.Inputs));
        Assert.Empty(await owner.Balances.ToListAsync()); Assert.Empty(await owner.DimensionRegistrations.ToListAsync());
    }

    [Fact]
    public async Task LegalResourceDimensionDisabledLocationBlocksNewButNotOriginalRecovery()
    {
        var command = await LegalDimensionReadyAsync();
        await using var owner = Open(_tenant);
        var created = await ResourceAccess(owner).DimensionAsync(Reader, command);
        var version = await owner.Locations.AsNoTracking().Select(row => row.Version).SingleAsync();
        Assert.Equal(LocationStateDecision.Changed,
            (await LocationStateAsync(command.LocationId, new(Guid.NewGuid(), version, false, "Stop"))).Decision);
        var schema = new Schema(LocationSchemaDecision.Unavailable);
        Assert.Equal(created with { Decision = StockResourceDecision.Replayed }, await ResourceAccess(owner, schema: schema).DimensionAsync(Reader, command));
        Assert.Empty(schema.Inputs);
        Assert.Equal(new(StockResourceDecision.Blocked),
            await ResourceAccess(owner).DimensionAsync(Reader, command with { OperationId = Guid.NewGuid() }));
        Assert.Single(await owner.Balances.ToListAsync());
    }

    [Theory]
    [InlineData("item")]
    [InlineData("container")]
    [InlineData("location")]
    [InlineData("warehouse")]
    public async Task LegalResourceDimensionCannotInferMissingOrForeignIdentities(string missing)
    {
        var command = await LegalDimensionReadyAsync();
        var changed = missing switch
        {
            "item" => command with { ItemId = Guid.NewGuid() },
            "container" => command with { ContainerId = Guid.NewGuid() },
            "location" => command with { LocationId = Guid.NewGuid() },
            _ => command with { WarehouseId = Guid.NewGuid() }
        };
        await using var owner = Open(_tenant);
        Assert.Equal(new(StockResourceDecision.NotFound), await ResourceAccess(owner).DimensionAsync(Reader, changed));
        await using var foreign = Open(_other);
        Assert.Equal(new(StockResourceDecision.NotFound),
            await ResourceAccess(foreign).DimensionAsync(Reader with { TenantId = _other }, command));
        Assert.Empty(await owner.Balances.ToListAsync());
    }

    [Fact]
    public async Task LegalResourceDimensionRejectsDifferentRealWarehouseBeforeLockingForeignLocation()
    {
        var command = await LegalDimensionReadyAsync();
        var warehouse = await ExecuteAsync(Command("OTHER"));
        await using var owner = Open(_tenant);
        Assert.Equal(new(StockResourceDecision.Blocked), await ResourceAccess(owner).DimensionAsync(Reader, command with { WarehouseId = warehouse.Id!.Value }));
        Assert.Empty(await owner.DimensionRegistrations.ToListAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LegalResourceDimensionConcurrentOriginalOrDifferentKeysCreateOneEmptyBalance(bool same)
    {
        var command = await LegalDimensionReadyAsync();
        async Task<StockResourceResult> Run(RegisterStockDimension request)
        {
            await using var owner = Open(_tenant); return await ResourceAccess(owner).DimensionAsync(Reader, request);
        }
        var results = await Task.WhenAll(Run(command), Run(same ? command : command with { OperationId = Guid.NewGuid() }));
        Assert.Single(results, row => row.Decision == StockResourceDecision.Created);
        Assert.Single(results, row => row.Decision == (same ? StockResourceDecision.Replayed : StockResourceDecision.Conflict));
        await using var verify = Open(_tenant);
        Assert.Single(await verify.Balances.ToListAsync()); Assert.Single(await verify.DimensionRegistrations.ToListAsync());
    }

    [Fact]
    public async Task LegalResourceDimensionRechecksAuthorityAfterSaveAndRollsBackBalanceAndReceipt()
    {
        var command = await LegalDimensionReadyAsync();
        await using var owner = Open(_tenant);
        Assert.Equal(new(StockResourceDecision.Forbidden),
            await ResourceAccess(owner, new ResourceAuthority { RevokeAfterFirstWrite = true }).DimensionAsync(Reader, command));
        Assert.Empty(await owner.Balances.ToListAsync()); Assert.Empty(await owner.DimensionRegistrations.ToListAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LegalResourceDimensionPostSaveFaultOrCancellationRollsBackBothRows(bool cancel)
    {
        var command = await LegalDimensionReadyAsync();
        using var cancellation = new CancellationTokenSource();
        await using (var owner = Open(_tenant, new AfterSaveFault(cancel, cancellation)))
        {
            Task<StockResourceResult> Execute() => ResourceAccess(owner).DimensionAsync(Reader, command, cancellation.Token);
            if (cancel) await Assert.ThrowsAnyAsync<OperationCanceledException>(Execute);
            else await Assert.ThrowsAsync<InvalidOperationException>(Execute);
        }
        await using var verify = Open(_tenant);
        Assert.Empty(await verify.Balances.ToListAsync()); Assert.Empty(await verify.DimensionRegistrations.ToListAsync());
    }

    [Fact]
    public async Task LegalResourceDimensionRegistrationCannotBeDetachedFromNewBalance()
    {
        var command = await LegalDimensionReadyAsync();
        await using var owner = Open(_tenant);
        var created = await ResourceAccess(owner).DimensionAsync(Reader, command);
        var existing = await owner.Balances.SingleAsync(row => row.Id == created.Id);
        await using var transaction = await owner.Database.BeginTransactionAsync();
        owner.DimensionRegistrations.Add(new(Guid.NewGuid(), _actor, Guid.NewGuid(), existing, _clock.GetUtcNow()));
        await Assert.ThrowsAsync<InvalidOperationException>(() => owner.SaveChangesAsync());
    }

    [Fact]
    public async Task LegalResourceDimensionRetainsOriginalVersionAfterRealLedgerPosting()
    {
        var command = await LegalDimensionReadyAsync();
        await using var owner = Open(_tenant);
        var created = await ResourceAccess(owner).DimensionAsync(Reader, command);
        var posting = await new PostStock(owner, _clock).ExecuteAsync(new(Guid.NewGuid(), _actor, Guid.NewGuid(), StockPostingKind.Receipt,
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), null, created.Id, 0.001m, null, created.Version));
        Assert.Equal(StockPostingDecision.Posted, posting.Decision);
        owner.ChangeTracker.Clear();
        Assert.Equal(created with { Decision = StockResourceDecision.Replayed }, await ResourceAccess(owner).DimensionAsync(Reader, command));
        var balance = await owner.Balances.SingleAsync();
        Assert.Equal(0.001m, balance.Quantity); Assert.NotEqual(created.Version, balance.Version);
        Assert.Single(await owner.DimensionRegistrations.ToListAsync()); Assert.Single(await owner.Entries.ToListAsync());
    }

    [Theory]
    [InlineData("cancel")]
    [InlineData("fault")]
    [InlineData("unknown")]
    public async Task LegalResourceDimensionSchemaExceptionsDoNotCreateAnyBalance(string mode)
    {
        var command = await LegalDimensionReadyAsync();
        using var cancellation = new CancellationTokenSource();
        var schema = new Schema(mode == "unknown" ? (LocationSchemaDecision)99 : LocationSchemaDecision.Valid, () =>
        {
            if (mode == "cancel") cancellation.Cancel();
            if (mode == "fault") throw new InvalidOperationException("Schema failed.");
        });
        await using (var owner = Open(_tenant))
        {
            Task<StockResourceResult> Execute() => ResourceAccess(owner, schema: schema).DimensionAsync(Reader, command, cancellation.Token);
            if (mode == "cancel") await Assert.ThrowsAnyAsync<OperationCanceledException>(Execute);
            else await Assert.ThrowsAsync<InvalidOperationException>(Execute);
        }
        await using var verify = Open(_tenant); Assert.Empty(await verify.Balances.ToListAsync());
    }

    [Fact]
    public async Task LegalResourceDimensionUsesEachActualCreatorRatherThanRequestActor()
    {
        var (location, warehouse) = await LocationReadyAsync();
        await LocationStateAsync(location.Id!.Value, new(Guid.NewGuid(), location.Version!, true, "Ready"));
        var itemActor = Guid.NewGuid(); var containerActor = Guid.NewGuid();
        await using var owner = Open(_tenant);
        var access = ResourceAccess(owner);
        var unit = await access.UnitAsync(Reader with { ActorId = _creator }, new(Guid.NewGuid(), Guid.NewGuid(), "EA", "Each", true, 0));
        var item = await access.ItemAsync(Reader with { ActorId = itemActor }, new(Guid.NewGuid(), Guid.NewGuid(), "I", "Item", unit.Id!.Value, false));
        var container = await access.ContainerAsync(Reader with { ActorId = containerActor }, new(Guid.NewGuid(), Guid.NewGuid(), "C", "Container", warehouse.Id!.Value));
        var command = new RegisterStockDimension(Guid.NewGuid(), Guid.NewGuid(), warehouse.Id.Value, location.Id.Value, item.Id!.Value, "", container.Id!.Value);
        var authority = new ResourceAuthority
        {
            Creators = new()
            {
                ["wms.dimensions.write"] = _actor,
                ["wms.units.read"] = _creator,
                ["wms.items.read"] = itemActor,
                ["wms.containers.read"] = containerActor,
                ["wms.warehouses.read"] = _actor,
                ["wms.locations.read"] = _actor
            }
        };
        Assert.Equal(StockResourceDecision.Created, (await ResourceAccess(owner, authority).DimensionAsync(Reader, command)).Decision);
        Assert.Equal(_actor, (await owner.DimensionRegistrations.SingleAsync()).ActorId);
    }

    private async Task<RegisterStockDimension> LegalDimensionReadyAsync(bool batchRequired = false)
    {
        var (location, warehouse) = await LocationReadyAsync();
        await LocationStateAsync(location.Id!.Value, new(Guid.NewGuid(), location.Version!, true, "Ready"));
        await using var owner = Open(_tenant);
        var access = ResourceAccess(owner);
        var unit = await access.UnitAsync(Reader, new(Guid.NewGuid(), Guid.NewGuid(), "KG", "Kilogram", false, 3));
        var item = await access.ItemAsync(Reader, new(Guid.NewGuid(), Guid.NewGuid(), "ITEM", "Item", unit.Id!.Value, batchRequired));
        var container = await access.ContainerAsync(Reader, new(Guid.NewGuid(), Guid.NewGuid(), "PALLET", "Pallet", warehouse.Id!.Value));
        return new(Guid.NewGuid(), Guid.NewGuid(), warehouse.Id.Value, location.Id.Value, item.Id!.Value, batchRequired ? "LOT" : "", container.Id!.Value);
    }
}
