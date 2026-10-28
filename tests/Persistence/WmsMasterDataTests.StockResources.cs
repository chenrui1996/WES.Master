using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Logging.Abstractions;
using Wes.Wms.Domain;
using Wes.Wms.Features.Ledger;
using Wes.Wms.Features.MasterData;
using Xunit;

namespace Wes.Persistence.Tests;

public sealed partial class WmsMasterDataTests
{
    [Fact]
    public async Task LegalResourcesCreateAnExactZeroBalanceAndReplayOriginalReceipts()
    {
        var (location, warehouse) = await LocationReadyAsync();
        await LocationStateAsync(location.Id!.Value, new(Guid.NewGuid(), location.Version!, true, "Ready"));
        await using var database = Open(_tenant);
        var resources = new StockResourceAccess(database, new ResourceAuthority(), new Schema(LocationSchemaDecision.Valid),
            _clock, NullLogger<StockResourceAccess>.Instance);
        var unitCommand = new RegisterUnit(Guid.NewGuid(), Guid.NewGuid(), "ea", "Each", true, 0);
        var unit = await resources.UnitAsync(Reader, unitCommand);
        Assert.Equal(StockResourceDecision.Created, unit.Decision);
        database.ChangeTracker.Clear();
        Assert.Equal(unit with { Decision = StockResourceDecision.Replayed }, await resources.UnitAsync(Reader, unitCommand));
        var item = await resources.ItemAsync(Reader, new(Guid.NewGuid(), Guid.NewGuid(), "ITEM", "Item", unit.Id!.Value, false));
        database.ChangeTracker.Clear();
        var container = await resources.ContainerAsync(Reader, new(Guid.NewGuid(), Guid.NewGuid(), "PALLET", "Pallet", warehouse.Id!.Value));
        database.ChangeTracker.Clear();
        var command = new RegisterStockDimension(Guid.NewGuid(), Guid.NewGuid(), warehouse.Id.Value,
            location.Id.Value, item.Id!.Value, "", container.Id!.Value);
        var dimension = await resources.DimensionAsync(Reader, command);
        Assert.Equal(StockResourceDecision.Created, dimension.Decision);
        database.ChangeTracker.Clear();
        Assert.Equal(dimension with { Decision = StockResourceDecision.Replayed }, await resources.DimensionAsync(Reader, command));
        await using var verify = Open(_tenant);
        var balance = await verify.Balances.SingleAsync();
        Assert.Equal((0m, 0, unit.Id, item.Id, container.Id), (balance.Quantity, balance.Precision, (Guid?)balance.UnitId, (Guid?)balance.ItemId, (Guid?)balance.ContainerId));
        Assert.Empty(await verify.Entries.ToListAsync()); Assert.Empty(await verify.Postings.ToListAsync());
    }

    [Theory]
    [InlineData(true, 0, true)]
    [InlineData(true, 1, false)]
    [InlineData(false, 0, true)]
    [InlineData(false, 3, true)]
    [InlineData(false, 6, true)]
    [InlineData(false, 7, false)]
    [InlineData(false, -1, false)]
    public async Task LegalResourceUnitPrecisionIsExplicitAndNeverRounded(bool countable, int precision, bool valid)
    {
        var result = await ResourceUnitAsync(new(Guid.NewGuid(), Guid.NewGuid(), "ea", "Each", countable, precision));
        Assert.Equal(valid ? StockResourceDecision.Created : StockResourceDecision.Invalid, result.Decision);
        await using var verify = Open(_tenant);
        Assert.Equal(valid ? 1 : 0, await verify.StockUnits.CountAsync());
        Assert.Empty(await verify.Balances.ToListAsync());
    }

    [Theory]
    [InlineData("operation")]
    [InlineData("correlation")]
    [InlineData("code")]
    [InlineData("name")]
    public async Task LegalResourceInvalidUnitHasNoFact(string invalid)
    {
        var command = new RegisterUnit(Guid.NewGuid(), Guid.NewGuid(), "EA", "Each", true, 0);
        command = invalid switch
        {
            "operation" => command with { OperationId = Guid.Empty },
            "correlation" => command with { CorrelationId = Guid.Empty },
            "code" => command with { Code = "" },
            _ => command with { Name = "\n" }
        };
        Assert.Equal(new(StockResourceDecision.Invalid), await ResourceUnitAsync(command));
        await using var verify = Open(_tenant); Assert.Empty(await verify.StockUnits.ToListAsync());
    }

    [Theory]
    [InlineData("wms.units.write:Code")]
    [InlineData("wms.units.write:Name")]
    [InlineData("wms.units.write:Countable")]
    [InlineData("wms.units.write:Precision")]
    public async Task LegalResourceUnitEveryCurrentWriteFieldIsRequiredOnNewAndReplay(string missing)
    {
        var command = new RegisterUnit(Guid.NewGuid(), Guid.NewGuid(), "EA", "Each", true, 0);
        var denied = new ResourceAuthority { Missing = missing };
        Assert.Equal(new(StockResourceDecision.Forbidden), await ResourceUnitAsync(command, denied));
        Assert.Equal(StockResourceDecision.Created, (await ResourceUnitAsync(command)).Decision);
        Assert.Equal(new(StockResourceDecision.Forbidden), await ResourceUnitAsync(command, denied));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LegalResourceUnitsResolveConcurrentOriginalOrCanonicalCodeWithSingleEffect(bool sameOperation)
    {
        var command = new RegisterUnit(Guid.NewGuid(), Guid.NewGuid(), "ea", "Each", true, 0);
        var results = await Task.WhenAll(ResourceUnitAsync(command), ResourceUnitAsync(sameOperation
            ? command with { Code = "EA" } : command with { OperationId = Guid.NewGuid() }));
        Assert.Single(results, row => row.Decision == StockResourceDecision.Created);
        Assert.Single(results, row => row.Decision == (sameOperation ? StockResourceDecision.Replayed : StockResourceDecision.Conflict));
        await using var verify = Open(_tenant); Assert.Single(await verify.StockUnits.ToListAsync());
    }

    [Theory]
    [InlineData("Id")]
    [InlineData("Code")]
    [InlineData("Name")]
    [InlineData("Countable")]
    [InlineData("Precision")]
    [InlineData("Version")]
    [InlineData("CreatedAt")]
    public async Task LegalResourceUnitReadRequiresEveryCurrentField(string missing)
    {
        var unit = await ResourceUnitAsync(new(Guid.NewGuid(), Guid.NewGuid(), "EA", "Each", true, 0));
        await using var owner = Open(_tenant);
        Assert.Equal(new(MasterDataReadDecision.Forbidden),
            await ResourceAccess(owner, new ResourceAuthority { Missing = $"wms.units.read:{missing}" }).ReadUnitAsync(Reader, unit.Id!.Value, default));
    }

    [Fact]
    public async Task LegalResourceReadsReturnOnlyCompleteRealCreatorProjectionsAndRecheckAfterRead()
    {
        var command = await LegalDimensionReadyAsync();
        await using var owner = Open(_tenant);
        var item = await owner.StockItems.AsNoTracking().SingleAsync(); var unit = await owner.StockUnits.AsNoTracking().SingleAsync();
        var container = await owner.StockContainers.AsNoTracking().SingleAsync();
        var access = ResourceAccess(owner);
        Assert.Equal(new StockUnitDefinition(unit.Id, unit.Code, unit.Name, false, 3, unit.Version, unit.CreatedAt),
            (await access.ReadUnitAsync(Reader, unit.Id, default)).Definition);
        Assert.Equal(new StockItemDefinition(item.Id, item.Code, item.Name, item.BaseUnitId, false, item.Version, item.CreatedAt),
            (await access.ReadItemAsync(Reader, item.Id, default)).Definition);
        Assert.Equal(new StockContainerDefinition(container.Id, container.Code, container.Name, command.WarehouseId, container.Version, container.CreatedAt),
            (await access.ReadContainerAsync(Reader, container.Id, default)).Definition);
        var authority = new ResourceAuthority { RevokeAfterFirstRead = true };
        Assert.Equal(new(MasterDataReadDecision.Forbidden), await ResourceAccess(owner, authority).ReadItemAsync(Reader, item.Id, default));
        Assert.Equal(new(MasterDataReadDecision.Invalid), await access.ReadUnitAsync(Reader, Guid.Empty, default));
        Assert.Equal(new(MasterDataReadDecision.NotFound), await access.ReadContainerAsync(Reader, Guid.NewGuid(), default));
        await using var foreign = Open(_other);
        Assert.Equal(new(MasterDataReadDecision.NotFound),
            await ResourceAccess(foreign).ReadItemAsync(Reader with { TenantId = _other }, item.Id, default));
    }

    [Fact]
    public async Task LegalResourceOriginalTupleCannotBeRewrittenByAnotherActorOrInput()
    {
        var command = new RegisterUnit(Guid.NewGuid(), Guid.NewGuid(), "EA", "Each", true, 0);
        var first = await ResourceUnitAsync(command);
        foreach (var other in new[] { command with { Name = "Other" }, command with { Countable = false }, command with { Code = "KG" } })
            Assert.Equal(new(StockResourceDecision.Conflict), await ResourceUnitAsync(other));
        await using var owner = Open(_tenant);
        Assert.Equal(new(StockResourceDecision.Conflict), await ResourceAccess(owner).UnitAsync(Reader with { ActorId = Guid.NewGuid() }, command));
        Assert.Equal(first with { Decision = StockResourceDecision.Replayed }, await ResourceUnitAsync(command with { CorrelationId = Guid.NewGuid() }));
    }

    [Fact]
    public async Task LegalResourceItemAndContainerRequireActualRegisteredSameTenantParents()
    {
        await using var owner = Open(_tenant);
        Assert.Equal(new(StockResourceDecision.NotFound), await ResourceAccess(owner).ItemAsync(Reader,
            new(Guid.NewGuid(), Guid.NewGuid(), "I", "Item", Guid.NewGuid(), false)));
        Assert.Equal(new(StockResourceDecision.NotFound), await ResourceAccess(owner).ContainerAsync(Reader,
            new(Guid.NewGuid(), Guid.NewGuid(), "C", "Container", Guid.NewGuid())));
        var unit = await ResourceUnitAsync(new(Guid.NewGuid(), Guid.NewGuid(), "EA", "Each", true, 0));
        await using var foreign = Open(_other);
        Assert.Equal(new(StockResourceDecision.NotFound), await ResourceAccess(foreign).ItemAsync(Reader with { TenantId = _other },
            new(Guid.NewGuid(), Guid.NewGuid(), "I", "Item", unit.Id!.Value, false)));
    }

    [Fact]
    public async Task LegalResourceItemAndContainerRetainExactOriginalPoliciesAndCreators()
    {
        var warehouse = await ExecuteAsync(Command() with { ActorId = _creator });
        var unit = await ResourceUnitAsync(new(Guid.NewGuid(), Guid.NewGuid(), "KG", "Kilogram", false, 3));
        await using var owner = Open(_tenant);
        var authority = new ResourceAuthority { Creators = new() { ["wms.units.read"] = _actor, ["wms.warehouses.read"] = _creator } };
        var access = ResourceAccess(owner, authority);
        var itemCommand = new RegisterItem(Guid.NewGuid(), Guid.NewGuid(), "item", "Item", unit.Id!.Value, true);
        var item = await access.ItemAsync(Reader, itemCommand);
        Assert.Equal(item with { Decision = StockResourceDecision.Replayed }, await access.ItemAsync(Reader, itemCommand with { Code = "ITEM" }));
        Assert.Equal(new(StockResourceDecision.Conflict), await access.ItemAsync(Reader, itemCommand with { BatchRequired = false }));
        var containerCommand = new RegisterContainer(Guid.NewGuid(), Guid.NewGuid(), "pallet", "Pallet", warehouse.Id!.Value);
        var container = await access.ContainerAsync(Reader, containerCommand);
        Assert.Equal(container with { Decision = StockResourceDecision.Replayed }, await access.ContainerAsync(Reader, containerCommand with { Code = "PALLET" }));
        Assert.Equal(new(StockResourceDecision.Conflict), await access.ContainerAsync(Reader, containerCommand with { Name = "Other" }));
        Assert.Equal(new(StockResourceDecision.Conflict), await access.ItemAsync(Reader, itemCommand with { OperationId = Guid.NewGuid() }));
        Assert.Equal(new(StockResourceDecision.Conflict), await access.ContainerAsync(Reader, containerCommand with { OperationId = Guid.NewGuid() }));
        Assert.Single(await owner.StockItems.ToListAsync()); Assert.Single(await owner.StockContainers.ToListAsync());
    }

    [Theory]
    [InlineData("tenant")]
    [InlineData("actor")]
    [InlineData("session")]
    public async Task LegalResourceRejectsUntrustedSubjectsBeforeCreatingFacts(string invalid)
    {
        var subject = invalid == "tenant" ? Reader with { TenantId = _other }
            : invalid == "actor" ? Reader with { ActorId = Guid.Empty } : Reader with { SessionId = Guid.Empty };
        await using var owner = Open(_tenant);
        Assert.Equal(new(StockResourceDecision.Forbidden), await ResourceAccess(owner).UnitAsync(subject,
            new(Guid.NewGuid(), Guid.NewGuid(), "EA", "Each", true, 0)));
        Assert.Empty(await owner.StockUnits.ToListAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LegalResourceRegistrationRechecksAuthorityAfterSaveOrBeforeReplay(bool replay)
    {
        var command = new RegisterUnit(Guid.NewGuid(), Guid.NewGuid(), "EA", "Each", true, 0);
        if (replay) await ResourceUnitAsync(command);
        Assert.Equal(new(StockResourceDecision.Forbidden), await ResourceUnitAsync(command, new ResourceAuthority { RevokeAfterFirstWrite = true }));
        await using var verify = Open(_tenant); Assert.Equal(replay ? 1 : 0, await verify.StockUnits.CountAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LegalResourceFaultOrCancellationAfterActualSaveRollsBack(bool cancel)
    {
        using var cancellation = new CancellationTokenSource();
        await using (var owner = Open(_tenant, new AfterSaveFault(cancel, cancellation)))
        {
            Task<StockResourceResult> Execute() => ResourceAccess(owner).UnitAsync(Reader,
                new(Guid.NewGuid(), Guid.NewGuid(), "EA", "Each", true, 0), cancellation.Token);
            if (cancel) await Assert.ThrowsAnyAsync<OperationCanceledException>(Execute);
            else await Assert.ThrowsAsync<InvalidOperationException>(Execute);
        }
        await using var verify = Open(_tenant); Assert.Empty(await verify.StockUnits.ToListAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LegalResourceRegisteredFactsCannotBeEditedOrDeleted(bool delete)
    {
        await ResourceUnitAsync(new(Guid.NewGuid(), Guid.NewGuid(), "EA", "Each", true, 0));
        await using var owner = Open(_tenant);
        var unit = await owner.StockUnits.SingleAsync();
        if (delete) owner.StockUnits.Remove(unit);
        else owner.Entry(unit).Property(row => row.Precision).CurrentValue = 6;
        await Assert.ThrowsAsync<InvalidOperationException>(() => owner.SaveChangesAsync());
    }

    [Theory]
    [InlineData("dirty")]
    [InlineData("transaction")]
    [InlineData("ambient")]
    public async Task LegalResourceRejectsNonIndependentOwner(string mode)
    {
        await using var owner = Open(_tenant);
        await using var transaction = mode == "transaction" ? await owner.Database.BeginTransactionAsync() : null;
        using var ambient = mode == "ambient" ? new System.Transactions.TransactionScope(System.Transactions.TransactionScopeAsyncFlowOption.Enabled) : null;
        if (mode == "dirty") owner.Warehouses.Add(new(_tenant, "DIRTY", "Dirty", _clock.GetUtcNow()));
        await Assert.ThrowsAsync<InvalidOperationException>(() => ResourceAccess(owner).UnitAsync(Reader,
            new(Guid.NewGuid(), Guid.NewGuid(), "EA", "Each", true, 0)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LegalResourceProviderFailureNeverBecomesSuccessfulReceipt(bool transient)
    {
        await using var owner = Open(_tenant, new QueryFailure(transient));
        var failure = await Assert.ThrowsAsync<WmsDatabaseException>(() => ResourceAccess(owner).ContainerAsync(Reader,
            new(Guid.NewGuid(), Guid.NewGuid(), "C", "Container", Guid.NewGuid())));
        Assert.Equal(transient, failure.IsTransient); Assert.Null(failure.InnerException);
        Assert.DoesNotContain("sensitive", failure.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void LegalResourceMigrationOnlyAddsTablesAndUsesRuntimeOwnerHistory()
    {
        using var owner = new Wes.Wms.Infrastructure.Persistence.WmsDesignTimeFactory().CreateDbContext([]);
        var script = owner.GetService<IMigrator>().GenerateScript("20261028024735_WmsLocationState", "20261028031720_WmsStockResources");
        foreach (var table in new[] { "wms_stock_units", "wms_stock_items", "wms_stock_containers", "wms_stock_dimension_registrations" })
            Assert.Contains($"CREATE TABLE `{table}`", script, StringComparison.Ordinal);
        Assert.Contains("INSERT INTO `__WesWmsMigrations`", script, StringComparison.Ordinal);
        Assert.DoesNotContain("__EFMigrationsHistory", script, StringComparison.Ordinal);
        Assert.DoesNotContain("ALTER TABLE", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("UPDATE ", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("DROP TABLE", script, StringComparison.OrdinalIgnoreCase);
    }

    private StockResourceAccess ResourceAccess(Wes.Wms.Infrastructure.Persistence.WmsDbContext owner,
        ResourceAuthority? authority = null, Schema? schema = null) =>
        new(owner, authority ?? new ResourceAuthority(), schema ?? new Schema(LocationSchemaDecision.Valid),
            _clock, NullLogger<StockResourceAccess>.Instance);

    private async Task<StockResourceResult> ResourceUnitAsync(RegisterUnit command, ResourceAuthority? authority = null)
    {
        await using var owner = Open(_tenant); return await ResourceAccess(owner, authority).UnitAsync(Reader, command);
    }

    private sealed class ResourceAuthority : IMasterDataAuthorization
    {
        internal string? Missing { get; init; }
        internal bool RevokeAfterFirstWrite { get; init; }
        internal bool RevokeAfterFirstRead { get; init; }
        internal Dictionary<string, Guid> Creators { get; init; } = [];
        private int _writes;
        private int _reads;
        public Task<IReadOnlySet<string>> ReadFieldsAsync(MasterDataSubject subject, string permission, Guid creator, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (Creators.TryGetValue(permission, out var expected)) Assert.Equal(expected, creator);
            if (permission.EndsWith(".write", StringComparison.Ordinal)) ++_writes;
            if (permission.EndsWith(".read", StringComparison.Ordinal)) ++_reads;
            string[] fields =
            {
                "Id", "Code", "Name", "Version", "CreatedAt", "Countable", "Precision", "BaseUnitId", "BatchRequired",
                "WarehouseId", "LocationId", "ItemId", "Batch", "ContainerId", "Enabled", "PackId", "SchemaVersion"
            };
            return Task.FromResult<IReadOnlySet<string>>(RevokeAfterFirstWrite && _writes > 1 || RevokeAfterFirstRead && _reads > 1 ? new HashSet<string>()
                : fields.Where(field => $"{permission}:{field}" != Missing).ToHashSet());
        }
        public Task<MasterDataCreatorAccess> ReadCreatorsAsync(MasterDataSubject subject, string permission,
            IReadOnlyCollection<string> requiredFields, CancellationToken ct) => throw new InvalidOperationException("No catalog authority.");
    }
}
