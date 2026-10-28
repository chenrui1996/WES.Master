using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Wes.Wms.Domain;
using Wes.Wms.Features.MasterData;
using Wes.Wms.Infrastructure.Persistence;
using Xunit;

namespace Wes.Persistence.Tests;

/// <summary>Owner-local real MySQL master-data identities; no physical state or authorization substitution.</summary>
[Collection("PlatformPersistence")]
[Trait("Provider", "MySQL")]
public sealed partial class WmsMasterDataTests : IAsyncLifetime
{
    private readonly Guid _tenant = Guid.NewGuid();
    private readonly Guid _other = Guid.NewGuid();
    private readonly Guid _actor = Guid.NewGuid();
    private readonly Clock _clock = new();
    private string _connection = "";
    private bool _ready;

    public async Task InitializeAsync()
    {
        _connection = MySqlTestConnection.LoadAuthorizedTestConnection();
        await using var owner = Open(_tenant);
        await owner.Database.MigrateAsync();
        _ready = true;
    }

    [Fact]
    public void OwnerModelProvidesIsolatedWarehouseLocationAndPermanentChangeTables()
    {
        using var owner = new WmsDbContext(new DbContextOptionsBuilder<WmsDbContext>().UseMySQL().Options, Guid.NewGuid());
        var tables = owner.Model.GetEntityTypes().Select(entity => entity.GetTableName()).ToArray();
        Assert.Contains("wms_warehouses", tables);
        Assert.Contains("wms_locations", tables);
        Assert.Contains("wms_master_data_changes", tables);
    }

    [Fact]
    public async Task WarehouseAndLocationCreateDisabledWithOnePreciseImmutableFactEach()
    {
        var warehouse = await ExecuteAsync(Command());
        var pack = Guid.NewGuid();
        var schema = new Schema(LocationSchemaDecision.Valid);
        var locationCommand = Command("a-01") with { WarehouseId = warehouse.Id, PackId = pack, SchemaVersion = "schema-v1" };
        var location = await ExecuteAsync(locationCommand, schema);
        Assert.Equal(MasterDataDecision.Created, location.Decision);
        Assert.Equal((_tenant, warehouse.Id!.Value, pack, "schema-v1", "A-01"), Assert.Single(schema.Inputs));
        await using var verify = Open(_tenant);
        var actualWarehouse = Assert.Single(await verify.Warehouses.ToListAsync());
        var actual = Assert.Single(await verify.Locations.ToListAsync());
        Assert.False(actualWarehouse.Enabled); Assert.False(actual.Enabled);
        Assert.Equal((warehouse.Id, pack, "schema-v1", "A-01", location.Version),
            ((Guid?)actual.WarehouseId, actual.PackId, actual.SchemaVersion, actual.Code, actual.Version));
        var change = await verify.MasterDataChanges.SingleAsync(item => item.LocationId == location.Id);
        Assert.Equal(locationCommand.OperationId, change.OperationId);
        Assert.Equal(_actor, change.ActorId);
        Assert.Equal(locationCommand.CorrelationId, change.CorrelationId);
        Assert.Equal(_clock.GetUtcNow(), change.OccurredAt);
        Assert.Equal(2, await verify.MasterDataChanges.CountAsync());
        Assert.Empty(await verify.Balances.ToListAsync());
        Assert.Empty(await verify.Entries.ToListAsync());
    }

    [Fact]
    public async Task PermanentOperationReplaysCanonicalIdentityButCannotChangeBodyOrActor()
    {
        var command = Command("wh-01");
        var first = await ExecuteAsync(command);
        var replay = await ExecuteAsync(command with { Code = "WH-01", CorrelationId = Guid.NewGuid() });
        Assert.Equal(MasterDataDecision.Replayed, replay.Decision);
        Assert.Equal(first.Id, replay.Id); Assert.Equal(first.Version, replay.Version);
        foreach (var changed in new[] { command with { Name = "Other" }, command with { ActorId = Guid.NewGuid() },
            command with { Code = "OTHER" } })
            Assert.Equal(MasterDataDecision.IdempotencyConflict, (await ExecuteAsync(changed)).Decision);
        await using var verify = Open(_tenant);
        Assert.Single(await verify.Warehouses.ToListAsync());
        Assert.Single(await verify.MasterDataChanges.ToListAsync());
    }

    [Theory]
    [InlineData(LocationSchemaDecision.Rejected)]
    [InlineData(LocationSchemaDecision.Unavailable)]
    public async Task UnknownOrRejectedSchemaNeverCreatesALocationOrCreationFact(LocationSchemaDecision evidence)
    {
        var warehouse = await ExecuteAsync(Command());
        var schema = new Schema(evidence);
        var command = Command("A1") with { WarehouseId = warehouse.Id, PackId = Guid.NewGuid(), SchemaVersion = "v1" };
        var result = await ExecuteAsync(command, schema);
        Assert.Equal(evidence == LocationSchemaDecision.Rejected ? MasterDataDecision.SchemaRejected : MasterDataDecision.SchemaUnavailable,
            result.Decision);
        await using var verify = Open(_tenant);
        Assert.Empty(await verify.Locations.ToListAsync());
        Assert.Single(await verify.MasterDataChanges.ToListAsync());
    }

    [Fact]
    public async Task SameTenantCodeConflictsWhileAnotherTenantCanUseTheSameCanonicalCode()
    {
        var command = Command();
        Assert.Equal(MasterDataDecision.Created, (await ExecuteAsync(command)).Decision);
        Assert.Equal(MasterDataDecision.CodeConflict, (await ExecuteAsync(command with { OperationId = Guid.NewGuid(), Code = "WH-01" })).Decision);
        await using var other = Open(_other);
        Assert.Equal(MasterDataDecision.Created, (await new CreateMasterData(other, _clock, new Schema(LocationSchemaDecision.Unavailable))
            .ExecuteAsync(command)).Decision);
        Assert.Single(await other.Warehouses.ToListAsync());
        Assert.Equal(command.OperationId, (await other.OutboxMessages.SingleAsync()).EventId);
        await using var original = Open(_tenant);
        Assert.Single(await original.Warehouses.ToListAsync());
        Assert.Single(await original.MasterDataChanges.ToListAsync());
        Assert.Equal(command.OperationId, (await original.OutboxMessages.SingleAsync()).EventId);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ConcurrentSameOperationOrCodeHasExactlyOneCreationEffect(bool sameOperation)
    {
        var command = Command();
        var second = sameOperation ? command : command with { OperationId = Guid.NewGuid() };
        var results = await Task.WhenAll(ExecuteAsync(command), ExecuteAsync(second));
        Assert.Single(results, result => result.Decision == MasterDataDecision.Created);
        Assert.Single(results, result => result.Decision == (sameOperation ? MasterDataDecision.Replayed : MasterDataDecision.CodeConflict));
        await using var verify = Open(_tenant);
        Assert.Single(await verify.Warehouses.ToListAsync());
        Assert.Single(await verify.MasterDataChanges.ToListAsync());
        var message = await verify.OutboxMessages.SingleAsync();
        Assert.Contains(message.EventId, new[] { command.OperationId, second.OperationId });
        Assert.Equal(results[0].Id ?? results[1].Id, message.AggregateId);
    }

    [Fact]
    public async Task ForeignWarehouseIsNotFoundBeforeInvokingAnySchemaAdapter()
    {
        var command = Command("A1") with { WarehouseId = Guid.NewGuid(), PackId = Guid.NewGuid(), SchemaVersion = "v1" };
        var schema = new Schema(LocationSchemaDecision.Valid);
        Assert.Equal(MasterDataDecision.NotFound, (await ExecuteAsync(command, schema)).Decision);
        Assert.Empty(schema.Inputs);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailureOrCancellationAfterRealSaveRollsBackBothIdentityAndFact(bool cancel)
    {
        using var cancellation = new CancellationTokenSource();
        var fault = new AfterSaveFault(cancel, cancellation);
        var command = Command();
        await using (var owner = Open(_tenant, fault))
        {
            var task = new CreateMasterData(owner, _clock, new Schema(LocationSchemaDecision.Unavailable))
                .ExecuteAsync(command, cancellation.Token);
            if (cancel) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
            else await Assert.ThrowsAsync<InvalidOperationException>(() => task);
        }
        Assert.True(fault.Reached);
        await using (var verify = Open(_tenant))
        {
            Assert.Empty(await verify.Warehouses.ToListAsync());
            Assert.Empty(await verify.MasterDataChanges.ToListAsync());
            Assert.Empty(await verify.OutboxMessages.ToListAsync());
        }
        Assert.Equal(MasterDataDecision.Created, (await ExecuteAsync(command)).Decision);
    }

    [Fact]
    public async Task MissingCreationFactCannotPersistAResource()
    {
        await using var owner = Open(_tenant);
        owner.Warehouses.Add(new Warehouse(_tenant, "W1", "Warehouse", _clock.GetUtcNow()));
        await Assert.ThrowsAsync<InvalidOperationException>(() => owner.SaveChangesAsync());
    }

    [Fact]
    public async Task HistoricalFactCannotCreateAnotherResourceOrBeChangedOrDeleted()
    {
        var created = await ExecuteAsync(Command());
        await using (var owner = Open(_tenant))
        {
            var existing = await owner.MasterDataChanges.SingleAsync();
            owner.Warehouses.Add(new Warehouse(_tenant, "OTHER", "Other", _clock.GetUtcNow()));
            Assert.Equal(created.Id, existing.WarehouseId);
            await Assert.ThrowsAsync<InvalidOperationException>(() => owner.SaveChangesAsync());
        }
        await using (var owner = Open(_tenant))
        {
            var change = await owner.MasterDataChanges.SingleAsync();
            owner.Entry(change).Property(item => item.Name).CurrentValue = "Forged";
            await Assert.ThrowsAsync<InvalidOperationException>(() => owner.SaveChangesAsync());
        }
        await using (var owner = Open(_tenant))
        {
            owner.MasterDataChanges.Remove(await owner.MasterDataChanges.SingleAsync());
            await Assert.ThrowsAsync<InvalidOperationException>(() => owner.SaveChangesAsync());
        }
    }

    [Fact]
    public async Task TenantOwnerCannotInsertAnotherTenantsResourcesOrReassignAnIdentity()
    {
        await using (var owner = Open(_tenant))
        {
            var foreign = new Warehouse(_other, "W1", "Foreign", _clock.GetUtcNow());
            owner.Warehouses.Add(foreign);
            owner.MasterDataChanges.Add(new MasterDataChange(Guid.NewGuid(), _actor, Guid.NewGuid(), foreign));
            await Assert.ThrowsAsync<InvalidOperationException>(() => owner.SaveChangesAsync());
        }
        await ExecuteAsync(Command());
        await using (var owner = Open(_tenant))
        {
            var warehouse = await owner.Warehouses.SingleAsync();
            Assert.Throws<InvalidOperationException>(() => owner.Entry(warehouse).Property(item => item.TenantId).CurrentValue = _other);
        }
    }

    private CreateMasterDataCommand Command(string code = "wh-01") => new(Guid.NewGuid(), _actor, Guid.NewGuid(), code, "主数据");
    private async Task<MasterDataResult> ExecuteAsync(CreateMasterDataCommand command, Schema? schema = null)
    {
        await using var owner = Open(_tenant);
        return await new CreateMasterData(owner, _clock, schema ?? new Schema(LocationSchemaDecision.Unavailable)).ExecuteAsync(command);
    }
    private WmsDbContext Open(Guid tenant, IInterceptor? interceptor = null)
    {
        MySqlTestConnection.ValidateTarget(_connection);
        var options = new DbContextOptionsBuilder<WmsDbContext>().UseMySQL(_connection,
            mysql => mysql.MigrationsHistoryTable("__WesWmsMigrations"));
        if (interceptor is not null) options.AddInterceptors(interceptor);
        return new(options.Options, tenant);
    }
    public async Task DisposeAsync()
    {
        if (!_ready) return;
        foreach (var tenant in new[] { _tenant, _other })
        {
            await using var owner = Open(tenant);
            await owner.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM wms_stock_entries WHERE TenantId = {tenant.ToString("D")}");
            await owner.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM wms_inbound_execution_operations WHERE TenantId = {tenant.ToString("D")}");
            await owner.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM wms_inbound_facts WHERE TenantId = {tenant.ToString("D")}");
            await owner.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM wms_inbound_documents WHERE TenantId = {tenant.ToString("D")}");
            await owner.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM wms_stock_postings WHERE TenantId = {tenant.ToString("D")}");
            await owner.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM wms_stock_reservations WHERE TenantId = {tenant.ToString("D")}");
            await owner.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM wms_stock_dimension_registrations WHERE TenantId = {tenant.ToString("D")}");
            await owner.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM wms_stock_balances WHERE TenantId = {tenant.ToString("D")}");
            await owner.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM wms_stock_containers WHERE TenantId = {tenant.ToString("D")}");
            await owner.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM wms_stock_items WHERE TenantId = {tenant.ToString("D")}");
            await owner.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM wms_stock_units WHERE TenantId = {tenant.ToString("D")}");
            await owner.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM wms_outbox_messages WHERE TenantId = {tenant.ToString("D")}");
            await owner.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM wms_location_state_changes WHERE TenantId = {tenant.ToString("D")}");
            await owner.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM wms_warehouse_state_changes WHERE TenantId = {tenant.ToString("D")}");
            await owner.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM wms_master_data_changes WHERE TenantId = {tenant.ToString("D")}");
            await owner.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM wms_locations WHERE TenantId = {tenant.ToString("D")}");
            await owner.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM wms_warehouses WHERE TenantId = {tenant.ToString("D")}");
        }
        await RemoveAuthorizationAsync();
    }
    private sealed class Clock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(2026, 10, 27, 0, 0, 0, TimeSpan.Zero);
    }
    private sealed class Schema(LocationSchemaDecision decision, Action? beforeReturn = null) : IWarehouseLocationSchema
    {
        public List<(Guid, Guid, Guid, string, string)> Inputs { get; } = [];
        public Task<LocationSchemaDecision> ValidateAsync(Guid tenantId, Guid warehouseId, Guid packId,
            string schemaVersion, string code, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Inputs.Add((tenantId, warehouseId, packId, schemaVersion, code));
            beforeReturn?.Invoke();
            return Task.FromResult(decision);
        }
    }
    private sealed class AfterSaveFault(bool cancel, CancellationTokenSource cancellation) : SaveChangesInterceptor
    {
        public bool Reached { get; private set; }
        public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result,
            CancellationToken cancellationToken = default)
        {
            Reached = true;
            if (cancel) cancellation.Cancel();
            else throw new InvalidOperationException("Injected post-save master-data failure.");
            return ValueTask.FromResult(result);
        }
    }
}
