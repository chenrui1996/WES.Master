using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Wes.Wms.Domain;
using Wes.Wms.Features.MasterData;
using Xunit;

namespace Wes.Persistence.Tests;

public sealed partial class WmsMasterDataTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CreationOutboxCommitsExactStableSafeEnvelopeAndReplayDoesNotAppend(bool location)
    {
        var warehouse = await ExecuteAsync(Command());
        var command = location ? Command("L-01") with { WarehouseId = warehouse.Id, PackId = Guid.NewGuid(), SchemaVersion = "Schema-v1" }
            : Command("W-02");
        var result = await ExecuteAsync(command, new Schema(LocationSchemaDecision.Valid));
        Assert.Equal(MasterDataDecision.Created, result.Decision);
        Assert.Equal(MasterDataDecision.Replayed, (await ExecuteAsync(command, new Schema(LocationSchemaDecision.Valid))).Decision);
        await using var verify = Open(_tenant);
        var message = await verify.OutboxMessages.SingleAsync(row => row.EventId == command.OperationId);
        Assert.Equal(_tenant, message.TenantId);
        Assert.Equal(("Wms", "wms.master-data.created", 1, WmsOutboxStatus.Pending),
            (message.Owner, message.ContractName, message.SchemaVersion, message.Status));
        Assert.Equal(result.Id, message.AggregateId);
        Assert.Equal(result.Version, message.AggregateVersion);
        Assert.Equal(command.CorrelationId, message.CorrelationId);
        Assert.Null(message.CausationId);
        Assert.Equal(_clock.GetUtcNow(), message.OccurredAt);
        var payload = JsonSerializer.Deserialize<JsonElement>(message.Payload);
        Assert.Equal(7, payload.EnumerateObject().Count());
        Assert.Equal(location ? "Location" : "Warehouse", payload.GetProperty("kind").GetString());
        Assert.False(payload.GetProperty("enabled").GetBoolean());
        Assert.Equal(location ? command.WarehouseId : result.Id, payload.GetProperty("warehouseId").GetGuid());
        Assert.Equal(command.Code.ToUpperInvariant(), payload.GetProperty("code").GetString());
        Assert.Equal(location ? "Schema-v1" : null, payload.GetProperty("schemaVersion").GetString());
        Assert.Equal(location ? result.Id : null, payload.GetProperty("locationId").ValueKind == JsonValueKind.Null
            ? null : payload.GetProperty("locationId").GetGuid());
        Assert.Equal(command.PackId, payload.GetProperty("packId").ValueKind == JsonValueKind.Null
            ? null : payload.GetProperty("packId").GetGuid());
        Assert.DoesNotContain("actor", message.Payload, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("name", message.Payload, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, await verify.OutboxMessages.CountAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CreationOutboxRollsBackActualSqlOnLateFailureOrCancellation(bool cancel)
    {
        using var cancellation = new CancellationTokenSource();
        var fault = new AfterSaveFault(cancel, cancellation);
        await using (var owner = Open(_tenant, fault))
        {
            var handler = new CreateMasterData(owner, _clock, new Schema(LocationSchemaDecision.Unavailable));
            if (cancel) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => handler.ExecuteAsync(Command(), cancellation.Token));
            else await Assert.ThrowsAsync<InvalidOperationException>(() => handler.ExecuteAsync(Command()));
        }
        Assert.True(fault.Reached);
        await using var verify = Open(_tenant);
        Assert.Empty(await verify.Warehouses.ToListAsync());
        Assert.Empty(await verify.MasterDataChanges.ToListAsync());
        Assert.Empty(await verify.OutboxMessages.ToListAsync());
    }

    [Fact]
    public async Task CreationOutboxIsDerivedForDirectOwnerSaveAndSameContextExplicitSaveRetry()
    {
        var fault = new FailFirstCreationSave();
        await using (var writer = Open(_tenant, fault))
        {
            var warehouse = new Warehouse(_tenant, "DIRECT", "Direct", _clock.GetUtcNow());
            writer.Warehouses.Add(warehouse);
            writer.MasterDataChanges.Add(new(Guid.NewGuid(), _actor, Guid.NewGuid(), warehouse));
            await Assert.ThrowsAsync<InvalidOperationException>(() => writer.SaveChangesAsync());
            Assert.Single(writer.ChangeTracker.Entries<WmsOutboxMessage>());
            await writer.SaveChangesAsync();
        }
        await using var verify = Open(_tenant);
        Assert.Single(await verify.Warehouses.ToListAsync());
        Assert.Single(await verify.MasterDataChanges.ToListAsync());
        Assert.Single(await verify.OutboxMessages.ToListAsync());
    }

    [Theory]
    [InlineData("Payload")]
    [InlineData("ContractName")]
    [InlineData("AggregateVersion")]
    [InlineData("delete")]
    public async Task CreationOutboxCommittedEventsCannotBeChangedOrDeleted(string action)
    {
        await ExecuteAsync(Command());
        await using var writer = Open(_tenant);
        var message = await writer.OutboxMessages.SingleAsync();
        if (action == "delete") writer.OutboxMessages.Remove(message);
        else writer.Entry(message).Property(action).CurrentValue = "forged";
        await Assert.ThrowsAsync<InvalidOperationException>(() => writer.SaveChangesAsync());
    }

    [Fact]
    public async Task CreationOutboxHistoricalEnvelopeCannotBeAddedWithoutANewMatchingFact()
    {
        await ExecuteAsync(Command());
        WmsOutboxMessage copy;
        await using (var source = Open(_tenant)) copy = await source.OutboxMessages.AsNoTracking().SingleAsync();
        await using var target = Open(_tenant);
        target.OutboxMessages.Add(copy);
        await Assert.ThrowsAsync<InvalidOperationException>(() => target.SaveChangesAsync());
    }

    [Fact]
    public async Task CreationOutboxDoesNotCrossTenantQueriesOrInsertAnUnrelatedForeignEvent()
    {
        await ExecuteAsync(Command());
        await using var other = Open(_other);
        Assert.Empty(await other.OutboxMessages.ToListAsync());
        await using var own = Open(_tenant);
        var message = await own.OutboxMessages.AsNoTracking().SingleAsync();
        other.OutboxMessages.Add(message);
        await Assert.ThrowsAsync<InvalidOperationException>(() => other.SaveChangesAsync());
    }

    [Fact]
    public async Task CreationOutboxLegacyOperationReplayDoesNotBackfillAnEvent()
    {
        var command = Command();
        var created = await ExecuteAsync(command);
        await using (var setup = Open(_tenant))
            await setup.Database.ExecuteSqlInterpolatedAsync(
                $"DELETE FROM wms_outbox_messages WHERE TenantId = {_tenant.ToString("D")}");
        var replay = await ExecuteAsync(command);
        Assert.Equal(MasterDataDecision.Replayed, replay.Decision);
        Assert.Equal(created.Id, replay.Id);
        await using var verify = Open(_tenant);
        Assert.Single(await verify.MasterDataChanges.ToListAsync());
        Assert.Empty(await verify.OutboxMessages.ToListAsync());
    }

    [Theory]
    [InlineData("Payload")]
    [InlineData("ContractName")]
    [InlineData("AggregateVersion")]
    [InlineData("Status")]
    public async Task CreationOutboxRejectsForgedNewEnvelopesBeforeAnySql(string property)
    {
        await using var owner = Open(_tenant);
        var warehouse = new Warehouse(_tenant, "FORGED", "Forged", _clock.GetUtcNow());
        var change = new MasterDataChange(Guid.NewGuid(), _actor, Guid.NewGuid(), warehouse);
        var message = new WmsOutboxMessage(change);
        owner.Warehouses.Add(warehouse);
        owner.MasterDataChanges.Add(change);
        owner.OutboxMessages.Add(message);
        if (property == "Status") owner.Entry(message).Property(item => item.Status).CurrentValue = (WmsOutboxStatus)99;
        else owner.Entry(message).Property(property).CurrentValue = "forged";
        await Assert.ThrowsAsync<InvalidOperationException>(() => owner.SaveChangesAsync());
        await using var verify = Open(_tenant);
        Assert.Empty(await verify.OutboxMessages.ToListAsync());
        Assert.Empty(await verify.Warehouses.ToListAsync());
        Assert.Empty(await verify.MasterDataChanges.ToListAsync());
    }

    [Fact]
    public async Task CreationOutboxActualMySqlDdlHasCompositeSourceForeignKeyAndPendingIndexWithoutModelDrift()
    {
        await using var owner = Open(_tenant);
        Assert.False(owner.Database.HasPendingModelChanges());
        var keys = await owner.Database.SqlQueryRaw<string>(
            """
            SELECT CONCAT(COLUMN_NAME, ':', REFERENCED_TABLE_NAME, ':', REFERENCED_COLUMN_NAME) AS Value
            FROM information_schema.KEY_COLUMN_USAGE
            WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'wms_outbox_messages'
              AND REFERENCED_TABLE_NAME IS NOT NULL ORDER BY ORDINAL_POSITION
            """).ToListAsync();
        Assert.Equal(new[] { "TenantId:wms_master_data_changes:TenantId", "EventId:wms_master_data_changes:OperationId" },
            keys, StringComparer.OrdinalIgnoreCase);
        var index = await owner.Database.SqlQueryRaw<string>(
            """
            SELECT COLUMN_NAME AS Value FROM information_schema.STATISTICS
            WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'wms_outbox_messages'
              AND INDEX_NAME = 'IX_wms_outbox_messages_TenantId_Status_OccurredAt_EventId' ORDER BY SEQ_IN_INDEX
            """).ToListAsync();
        Assert.Equal(new[] { "TenantId", "Status", "OccurredAt", "EventId" }, index);
        var timestamp = await owner.Database.SqlQueryRaw<string>(
            """
            SELECT COLUMN_TYPE AS Value FROM information_schema.COLUMNS
            WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'wms_outbox_messages' AND COLUMN_NAME = 'OccurredAt'
            """).SingleAsync();
        Assert.Equal("datetime(6)", timestamp);
    }

    private sealed class FailFirstCreationSave : SaveChangesInterceptor
    {
        private bool _failed;
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (!_failed) { _failed = true; throw new InvalidOperationException("test-save-failure"); }
            return ValueTask.FromResult(result);
        }
    }
}
