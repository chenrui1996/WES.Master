using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Wes.Wms.Domain;
using Wes.Wms.Features.Ledger;
using Wes.Wms.Features.MasterData;
using Xunit;

namespace Wes.Persistence.Tests;

public sealed partial class WmsMasterDataTests
{
    [Fact]
    public async Task ActualMasterDataColumnsAndSchemaQueriesPreserveOrdinalIdentity()
    {
        var warehouse = await ExecuteAsync(Command());
        await ExecuteAsync(Command("A1") with { WarehouseId = warehouse.Id, PackId = Guid.NewGuid(), SchemaVersion = "v1" },
            new Schema(LocationSchemaDecision.Valid));
        await using var owner = Open(_tenant);
        var collations = await owner.Database.SqlQueryRaw<string>(
            """
            SELECT COLLATION_NAME AS Value FROM information_schema.COLUMNS
            WHERE TABLE_SCHEMA = DATABASE()
              AND ((TABLE_NAME IN ('wms_warehouses', 'wms_locations', 'wms_master_data_changes') AND COLUMN_NAME = 'Code')
                OR (TABLE_NAME IN ('wms_locations', 'wms_master_data_changes') AND COLUMN_NAME = 'SchemaVersion'))
            """).ToListAsync();
        Assert.Equal(5, collations.Count);
        Assert.All(collations, collation => Assert.Equal("utf8mb4_bin", collation));
        Assert.Empty(await owner.Locations.Where(item => item.SchemaVersion == "V1").ToListAsync());
        Assert.Empty(await owner.MasterDataChanges.Where(item => item.SchemaVersion == "V1").ToListAsync());
        Assert.Single(await owner.Locations.Where(item => item.SchemaVersion == "v1").ToListAsync());
    }

    [Fact]
    public async Task CancellationDuringSchemaValidationCannotTurnALateValidResultIntoACreatedLocation()
    {
        var warehouse = await ExecuteAsync(Command());
        var command = Command("A1") with { WarehouseId = warehouse.Id, PackId = Guid.NewGuid(), SchemaVersion = "v1" };
        using var cancellation = new CancellationTokenSource();
        await using (var owner = Open(_tenant))
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                new CreateMasterData(owner, _clock, new Schema(LocationSchemaDecision.Valid, cancellation.Cancel))
                    .ExecuteAsync(command, cancellation.Token));
        await using var verify = Open(_tenant);
        Assert.Empty(await verify.Locations.ToListAsync());
        Assert.Single(await verify.MasterDataChanges.ToListAsync());
    }

    [Fact]
    public async Task UnknownSchemaOutcomeIsAnExplicitErrorNotAValidOrUnavailableDefault()
    {
        var warehouse = await ExecuteAsync(Command());
        var command = Command("A1") with { WarehouseId = warehouse.Id, PackId = Guid.NewGuid(), SchemaVersion = "v1" };
        await Assert.ThrowsAsync<InvalidOperationException>(() => ExecuteAsync(command, new Schema((LocationSchemaDecision)99)));
        await using var verify = Open(_tenant);
        Assert.Empty(await verify.Locations.ToListAsync());
        Assert.Single(await verify.MasterDataChanges.ToListAsync());
    }

    [Theory]
    [InlineData("operation")]
    [InlineData("actor")]
    [InlineData("correlation")]
    [InlineData("code")]
    [InlineData("name")]
    [InlineData("partial-location")]
    public async Task InvalidCommandsCannotWriteAnyMasterData(string invalid)
    {
        var command = Command();
        command = invalid switch
        {
            "operation" => command with { OperationId = Guid.Empty },
            "actor" => command with { ActorId = Guid.Empty },
            "correlation" => command with { CorrelationId = Guid.Empty },
            "code" => command with { Code = " path/code" },
            "name" => command with { Name = " name " },
            _ => command with { PackId = Guid.NewGuid() }
        };
        Assert.Equal(MasterDataDecision.Invalid, (await ExecuteAsync(command)).Decision);
        await using var owner = Open(_tenant);
        Assert.Empty(await owner.Warehouses.ToListAsync());
        Assert.Empty(await owner.Locations.ToListAsync());
        Assert.Empty(await owner.MasterDataChanges.ToListAsync());
    }

    [Fact]
    public async Task LocationOperationCannotBeReusedWithAnotherWarehousePackOrSchema()
    {
        var warehouse = await ExecuteAsync(Command());
        var command = Command("a1") with { WarehouseId = warehouse.Id, PackId = Guid.NewGuid(), SchemaVersion = "v1" };
        var first = await ExecuteAsync(command, new Schema(LocationSchemaDecision.Valid));
        Assert.Equal(MasterDataDecision.Created, first.Decision);
        Assert.Equal(MasterDataDecision.Replayed, (await ExecuteAsync(command)).Decision);
        foreach (var changed in new[] { command with { WarehouseId = Guid.NewGuid() },
            command with { PackId = Guid.NewGuid() }, command with { SchemaVersion = "V1" } })
            Assert.Equal(MasterDataDecision.IdempotencyConflict, (await ExecuteAsync(changed, new Schema(LocationSchemaDecision.Valid))).Decision);
        await using var verify = Open(_tenant);
        Assert.Single(await verify.Locations.ToListAsync());
        Assert.Equal(2, await verify.MasterDataChanges.CountAsync());
    }

    [Fact]
    public async Task LocationCodeIsUniqueAcrossWarehousesWithinATenant()
    {
        var first = await ExecuteAsync(Command("W1"));
        var second = await ExecuteAsync(Command("W2"));
        var command = Command("A1") with { WarehouseId = first.Id, PackId = Guid.NewGuid(), SchemaVersion = "v1" };
        Assert.Equal(MasterDataDecision.Created, (await ExecuteAsync(command, new Schema(LocationSchemaDecision.Valid))).Decision);
        Assert.Equal(MasterDataDecision.CodeConflict, (await ExecuteAsync(command with
        {
            OperationId = Guid.NewGuid(),
            WarehouseId = second.Id,
            Code = "a1"
        }, new Schema(LocationSchemaDecision.Valid))).Decision);
        await using var verify = Open(_tenant);
        Assert.Single(await verify.Locations.ToListAsync());
        Assert.Equal(3, await verify.MasterDataChanges.CountAsync());
    }

    [Fact]
    public async Task MissingSchemaAdapterIsExplicitlyRejectedNotAssumedSuccessful()
    {
        await using var owner = Open(_tenant);
        await Assert.ThrowsAsync<ArgumentNullException>(() => new CreateMasterData(owner, _clock, null!).ExecuteAsync(Command()));
        Assert.Empty(await owner.Warehouses.ToListAsync());
    }

    [Fact]
    public async Task ActualForeignWarehouseCannotBeUsedOrDiscoveredThroughLocationCreation()
    {
        Guid foreign;
        await using (var seed = Open(_other))
        {
            var result = await new CreateMasterData(seed, _clock, new Schema(LocationSchemaDecision.Unavailable)).ExecuteAsync(Command());
            foreign = result.Id!.Value;
        }
        var schema = new Schema(LocationSchemaDecision.Valid);
        var command = Command("A1") with { WarehouseId = foreign, PackId = Guid.NewGuid(), SchemaVersion = "v1" };
        Assert.Equal(MasterDataDecision.NotFound, (await ExecuteAsync(command, schema)).Decision);
        Assert.Empty(schema.Inputs);
    }

    [Fact]
    public async Task CompositeForeignKeyRejectsAnUnmatchedWarehouseEvenWithAMatchingCreationFact()
    {
        await using var owner = Open(_tenant);
        var location = new WarehouseLocation(_tenant, Guid.NewGuid(), Guid.NewGuid(), "v1", "A1", "Location", _clock.GetUtcNow());
        owner.Locations.Add(location);
        owner.MasterDataChanges.Add(new(Guid.NewGuid(), _actor, Guid.NewGuid(), location));
        await Assert.ThrowsAsync<DbUpdateException>(() => owner.SaveChangesAsync());
        await using var verify = Open(_tenant);
        Assert.Empty(await verify.Locations.ToListAsync());
        Assert.Empty(await verify.MasterDataChanges.ToListAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CreatedLocationCannotBeReassignedOrActivatedByChangingTrackedProperties(bool activate)
    {
        var warehouse = await ExecuteAsync(Command());
        var location = await ExecuteAsync(Command("A1") with { WarehouseId = warehouse.Id, PackId = Guid.NewGuid(), SchemaVersion = "v1" },
            new Schema(LocationSchemaDecision.Valid));
        await using (var owner = Open(_tenant))
        {
            var target = await owner.Locations.SingleAsync();
            if (activate) owner.Entry(target).Property(item => item.Enabled).CurrentValue = true;
            else owner.Entry(target).Property(item => item.PackId).CurrentValue = Guid.NewGuid();
            await Assert.ThrowsAsync<InvalidOperationException>(() => owner.SaveChangesAsync());
        }
        await using var verify = Open(_tenant);
        var actual = await verify.Locations.SingleAsync();
        Assert.False(actual.Enabled);
        Assert.Equal(location.Version, actual.Version);
    }

    [Fact]
    public async Task ASecondCreationFactCannotBeAttachedToAHistoricalWarehouse()
    {
        await ExecuteAsync(Command());
        await using var owner = Open(_tenant);
        var original = await owner.Warehouses.SingleAsync();
        owner.MasterDataChanges.Add(new(Guid.NewGuid(), _actor, Guid.NewGuid(), original));
        await Assert.ThrowsAsync<InvalidOperationException>(() => owner.SaveChangesAsync());
    }

    [Fact]
    public async Task WarehouseAndLocationCannotBePhysicallyDeleted()
    {
        var warehouse = await ExecuteAsync(Command());
        await ExecuteAsync(Command("A1") with { WarehouseId = warehouse.Id, PackId = Guid.NewGuid(), SchemaVersion = "v1" },
            new Schema(LocationSchemaDecision.Valid));
        await using (var owner = Open(_tenant))
        {
            owner.Locations.Remove(await owner.Locations.SingleAsync());
            await Assert.ThrowsAsync<InvalidOperationException>(() => owner.SaveChangesAsync());
        }
        await using (var owner = Open(_tenant))
        {
            owner.Warehouses.Remove(await owner.Warehouses.SingleAsync());
            await Assert.ThrowsAsync<InvalidOperationException>(() => owner.SaveChangesAsync());
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DatabaseReadFailureIsSanitizedAndNeverLooksLikeAnEmptyOrSuccessfulResult(bool transient)
    {
        await using var owner = Open(_tenant, new QueryFailure(transient));
        var error = await Assert.ThrowsAsync<WmsDatabaseException>(() =>
            new CreateMasterData(owner, _clock, new Schema(LocationSchemaDecision.Unavailable)).ExecuteAsync(Command()));
        Assert.Equal(transient, error.IsTransient);
        Assert.DoesNotContain("sensitive-sentinel", error.ToString(), StringComparison.Ordinal);
    }

    private sealed class QueryFailure(bool transient) : DbCommandInterceptor
    {
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default) =>
            command.CommandText.Contains("wms_master_data_changes", StringComparison.Ordinal)
                ? throw new DatabaseFailure(transient) : ValueTask.FromResult(result);
    }
    private sealed class DatabaseFailure(bool transient) : DbException("sensitive-sentinel")
    {
        public override bool IsTransient => transient;
    }
}
