using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Logging.Abstractions;
using Wes.Wms;
using Wes.Wms.Features.MasterData;
using Wes.Wms.Domain;
using Wes.Wms.Features.Inbound;
using Wes.Wms.Features.Ledger;
using Xunit;

namespace Wes.Persistence.Tests;

public sealed partial class WmsMasterDataTests
{
    [Fact]
    public async Task LocationStatePreservesOriginalReceiptsAndPinnedLineage()
    {
        var warehouse = await ExecuteAsync(Command());
        var enabledWarehouse = await StateAsync(warehouse.Id!.Value, new(Guid.NewGuid(), warehouse.Version!, true, "Ready"));
        var command = Command("LOC") with { WarehouseId = warehouse.Id, PackId = Guid.NewGuid(), SchemaVersion = "v1" };
        var created = await ExecuteAsync(command, new Schema(LocationSchemaDecision.Valid));
        var request = new LocationStateCommand(Guid.NewGuid(), created.Version!, true, "Ready");
        var first = await LocationStateAsync(created.Id!.Value, request);
        Assert.Equal(LocationStateDecision.Changed, first.Decision);
        var second = await LocationStateAsync(created.Id.Value, new(Guid.NewGuid(), first.Version!, false, "Stop"));
        Assert.Equal(LocationStateDecision.Changed, second.Decision);
        Assert.Equal(WarehouseStateDecision.Changed, (await StateAsync(warehouse.Id.Value,
            new(Guid.NewGuid(), enabledWarehouse.Version!, false, "Stop"))).Decision);
        var replay = await LocationStateAsync(created.Id.Value, request, schema: new Schema(LocationSchemaDecision.Unavailable));
        Assert.Equal(first with { Decision = LocationStateDecision.Replayed }, replay);
        Assert.Equal(created with { Decision = MasterDataDecision.Replayed }, await ExecuteAsync(command, new Schema(LocationSchemaDecision.Unavailable)));
        await using var verify = Open(_tenant);
        var row = await verify.Locations.SingleAsync();
        Assert.False(row.Enabled); Assert.Equal(second.Version, row.Version);
        Assert.Equal((warehouse.Id, command.PackId, "v1"), ((Guid?)row.WarehouseId, (Guid?)row.PackId, row.SchemaVersion));
        Assert.Equal(2, await verify.LocationStateChanges.CountAsync());
        Assert.Equal(2, await verify.MasterDataChanges.CountAsync()); Assert.Equal(2, await verify.OutboxMessages.CountAsync());
        Assert.Empty(await verify.Balances.ToListAsync());
    }

    [Fact]
    public async Task LocationStateResolvesDistinctRealCreatorsAndDoesNotExposeForeignResources()
    {
        var warehouse = await ExecuteAsync(Command() with { ActorId = _creator });
        await StateAsync(warehouse.Id!.Value, new(Guid.NewGuid(), warehouse.Version!, true, "Ready"), new StateAuthority(_creator));
        var created = await ExecuteAsync(Command("LOC") with { WarehouseId = warehouse.Id, PackId = Guid.NewGuid(), SchemaVersion = "v1" },
            new Schema(LocationSchemaDecision.Valid));
        var request = new LocationStateCommand(Guid.NewGuid(), created.Version!, true, "Ready");
        var actor = Reader with { ActorId = Guid.NewGuid() };
        await using (var database = Open(_tenant))
            Assert.Equal(LocationStateDecision.Changed,
                (await new LocationStateAccess(database, new LocationStateAuthority(_actor, _creator),
                    new Schema(LocationSchemaDecision.Valid), _clock, NullLogger<LocationStateAccess>.Instance)
                    .ChangeAsync(actor, created.Id!.Value, request)).Decision);
        await using (var database = Open(_tenant))
            Assert.Equal(LocationStateDecision.Replayed,
                (await new LocationStateAccess(database, new LocationStateAuthority(_actor, _creator),
                    new Schema(LocationSchemaDecision.Unavailable), _clock, NullLogger<LocationStateAccess>.Instance)
                    .ChangeAsync(actor, created.Id!.Value, request)).Decision);
        await using (var foreign = Open(_other))
            Assert.Equal(new(LocationStateDecision.NotFound),
                await new LocationStateAccess(foreign, new LocationStateAuthority(Guid.Empty),
                    new Schema(LocationSchemaDecision.Valid), _clock, NullLogger<LocationStateAccess>.Instance)
                    .ChangeAsync(Reader with { TenantId = _other }, created.Id!.Value, request));
        Assert.Equal(new(LocationStateDecision.NotFound), await LocationStateAsync(Guid.NewGuid(), request));
        await using var verify = Open(_tenant);
        Assert.Equal(actor.ActorId, (await verify.LocationStateChanges.SingleAsync()).ActorId);
    }

    [Fact]
    public void LocationStateMigrationIsAdditiveAndUsesRuntimeOwnerHistory()
    {
        using var database = new Wes.Wms.Infrastructure.Persistence.WmsDesignTimeFactory().CreateDbContext([]);
        var script = database.GetService<IMigrator>().GenerateScript("20261027173433_WmsWarehouseState", "20261028024735_WmsLocationState");
        Assert.Contains("CREATE TABLE `wms_location_state_changes`", script, StringComparison.Ordinal);
        Assert.Contains("INSERT INTO `__WesWmsMigrations`", script, StringComparison.Ordinal);
        Assert.DoesNotContain("__EFMigrationsHistory", script, StringComparison.Ordinal);
        Assert.DoesNotContain("ALTER TABLE", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("UPDATE ", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("DROP TABLE", script, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("write:Enabled")]
    [InlineData("Id")]
    [InlineData("Code")]
    [InlineData("Name")]
    [InlineData("Enabled")]
    [InlineData("Version")]
    [InlineData("CreatedAt")]
    [InlineData("WarehouseId")]
    [InlineData("PackId")]
    [InlineData("SchemaVersion")]
    [InlineData("parent:Id")]
    [InlineData("parent:Code")]
    [InlineData("parent:Name")]
    [InlineData("parent:Enabled")]
    [InlineData("parent:Version")]
    [InlineData("parent:CreatedAt")]
    public async Task LocationStateRequiresAllCurrentLocationAndParentFieldsOnNewAndReplay(string missing)
    {
        var (location, _) = await LocationReadyAsync();
        var request = new LocationStateCommand(Guid.NewGuid(), location.Version!, true, "Ready");
        Assert.Equal(new(LocationStateDecision.Forbidden), await LocationStateAsync(location.Id!.Value, request,
            new LocationStateAuthority(_actor) { Missing = missing }));
        await AssertNoLocationStateAsync(location.Version!);
        Assert.Equal(LocationStateDecision.Changed, (await LocationStateAsync(location.Id.Value, request)).Decision);
        Assert.Equal(new(LocationStateDecision.Forbidden), await LocationStateAsync(location.Id.Value, request,
            new LocationStateAuthority(_actor) { Missing = missing }));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LocationStateRechecksAuthorityAfterSaveOrBeforeReplayDisclosure(bool replay)
    {
        var (location, _) = await LocationReadyAsync();
        var request = new LocationStateCommand(Guid.NewGuid(), location.Version!, true, "Ready");
        if (replay) await LocationStateAsync(location.Id!.Value, request);
        Assert.Equal(new(LocationStateDecision.Forbidden), await LocationStateAsync(location.Id!.Value, request,
            new LocationStateAuthority(_actor) { RevokeAfterFirst = true }));
        if (!replay) await AssertNoLocationStateAsync(location.Version!);
    }

    [Theory]
    [InlineData(LocationSchemaDecision.Rejected)]
    [InlineData(LocationSchemaDecision.Unavailable)]
    public async Task LocationStateActivationRequiresTheExactPinnedSchema(LocationSchemaDecision evidence)
    {
        var (location, _) = await LocationReadyAsync();
        var schema = new Schema(evidence);
        Assert.Equal(new(evidence == LocationSchemaDecision.Rejected ? LocationStateDecision.SchemaRejected : LocationStateDecision.SchemaUnavailable),
            await LocationStateAsync(location.Id!.Value, new(Guid.NewGuid(), location.Version!, true, "Ready"), schema: schema));
        await using var verify = Open(_tenant);
        var row = await verify.Locations.SingleAsync();
        Assert.Equal((_tenant, row.WarehouseId, row.PackId, row.SchemaVersion, row.Code), Assert.Single(schema.Inputs));
        await AssertNoLocationStateAsync(location.Version!);
    }

    [Theory]
    [InlineData("cancel")]
    [InlineData("fault")]
    [InlineData("unknown")]
    public async Task LocationStateSchemaFailureOrCancellationCannotActivateOrLeaveAFact(string mode)
    {
        var (location, _) = await LocationReadyAsync();
        using var cancellation = new CancellationTokenSource();
        var schema = new Schema(mode == "unknown" ? (LocationSchemaDecision)99 : LocationSchemaDecision.Valid, () =>
        {
            if (mode == "cancel") cancellation.Cancel();
            if (mode == "fault") throw new InvalidOperationException("Schema failed.");
        });
        await using (var database = Open(_tenant))
        {
            Task<LocationStateResult> Execute() => new LocationStateAccess(database, new LocationStateAuthority(_actor), schema,
                _clock, NullLogger<LocationStateAccess>.Instance).ChangeAsync(Reader,
                location.Id!.Value, new(Guid.NewGuid(), location.Version!, true, "Ready"), cancellation.Token);
            if (mode == "cancel") await Assert.ThrowsAnyAsync<OperationCanceledException>(Execute);
            else await Assert.ThrowsAsync<InvalidOperationException>(Execute);
        }
        await AssertNoLocationStateAsync(location.Version!);
    }

    [Fact]
    public async Task LocationStateDisabledParentBlocksActivationBeforeSchemaButNotHistoricalRecovery()
    {
        var (location, warehouse) = await LocationReadyAsync();
        await StateAsync(warehouse.Id!.Value, new(Guid.NewGuid(), warehouse.Version!, false, "Stop"));
        var schema = new Schema(LocationSchemaDecision.Valid);
        Assert.Equal(new(LocationStateDecision.Blocked),
            await LocationStateAsync(location.Id!.Value, new(Guid.NewGuid(), location.Version!, true, "Ready"), schema: schema));
        Assert.Empty(schema.Inputs); await AssertNoLocationStateAsync(location.Version!);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LocationStateConcurrentOriginalOrDifferentKeysHaveOneEffect(bool same)
    {
        var (location, _) = await LocationReadyAsync();
        var request = new LocationStateCommand(Guid.NewGuid(), location.Version!, true, "Ready");
        var results = await Task.WhenAll(LocationStateAsync(location.Id!.Value, request),
            LocationStateAsync(location.Id.Value, same ? request : request with { OperationId = Guid.NewGuid() }));
        Assert.Single(results, item => item.Decision == LocationStateDecision.Changed);
        Assert.Single(results, item => item.Decision == (same ? LocationStateDecision.Replayed : LocationStateDecision.Conflict));
        await using var verify = Open(_tenant); Assert.Single(await verify.LocationStateChanges.ToListAsync());
    }

    [Fact]
    public async Task LocationStateAndParentShutdownSerializeWithoutAnEnabledChildOfDisabledParent()
    {
        var (location, warehouse) = await LocationReadyAsync();
        var activation = LocationStateAsync(location.Id!.Value, new(Guid.NewGuid(), location.Version!, true, "Ready"));
        var shutdown = StateAsync(warehouse.Id!.Value, new(Guid.NewGuid(), warehouse.Version!, false, "Stop"));
        await Task.WhenAll(activation, shutdown);
        var activationResult = await activation; var shutdownResult = await shutdown;
        await using var verify = Open(_tenant);
        var parent = await verify.Warehouses.SingleAsync(); var child = await verify.Locations.SingleAsync();
        Assert.True(parent.Enabled || !child.Enabled);
        Assert.True(activationResult.Decision == LocationStateDecision.Changed && shutdownResult.Decision == WarehouseStateDecision.Blocked
            || activationResult.Decision == LocationStateDecision.Blocked && shutdownResult.Decision == WarehouseStateDecision.Changed);
    }

    [Fact]
    public async Task LocationStateRejectsStaleSameStateAndDifferentOriginalTuple()
    {
        var (location, _) = await LocationReadyAsync();
        var request = new LocationStateCommand(Guid.NewGuid(), location.Version!, true, "Ready");
        Assert.Equal(LocationStateDecision.Conflict, (await LocationStateAsync(location.Id!.Value, request with { Enabled = false })).Decision);
        Assert.Equal(LocationStateDecision.Conflict, (await LocationStateAsync(location.Id.Value,
            request with { ExpectedVersion = Guid.NewGuid().ToString("D") })).Decision);
        var first = await LocationStateAsync(location.Id.Value, request);
        foreach (var other in new[] { request with { Reason = "Other" }, request with { Enabled = false }, request with { ExpectedVersion = first.Version! } })
            Assert.Equal(LocationStateDecision.Conflict, (await LocationStateAsync(location.Id.Value, other)).Decision);
        await using var verify = Open(_tenant); Assert.Single(await verify.LocationStateChanges.ToListAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LocationStateFaultOrCancellationAfterActualSaveRollsBackEverything(bool cancel)
    {
        var (location, _) = await LocationReadyAsync();
        using var cancellation = new CancellationTokenSource();
        await using (var database = Open(_tenant, new AfterSaveFault(cancel, cancellation)))
        {
            Task<LocationStateResult> Execute() => new LocationStateAccess(database, new LocationStateAuthority(_actor),
                new Schema(LocationSchemaDecision.Valid), _clock, NullLogger<LocationStateAccess>.Instance).ChangeAsync(Reader,
                location.Id!.Value, new(Guid.NewGuid(), location.Version!, true, "Ready"), cancellation.Token);
            if (cancel) await Assert.ThrowsAnyAsync<OperationCanceledException>(Execute);
            else await Assert.ThrowsAsync<InvalidOperationException>(Execute);
        }
        await AssertNoLocationStateAsync(location.Version!);
    }

    [Theory]
    [InlineData("dirty")]
    [InlineData("transaction")]
    [InlineData("ambient")]
    public async Task LocationStateRejectsNonIndependentOwner(string invalid)
    {
        await using var database = Open(_tenant);
        await using var transaction = invalid == "transaction" ? await database.Database.BeginTransactionAsync() : null;
        using var ambient = invalid == "ambient" ? new System.Transactions.TransactionScope(System.Transactions.TransactionScopeAsyncFlowOption.Enabled) : null;
        if (invalid == "dirty") database.Warehouses.Add(new(_tenant, "DIRTY", "Dirty", _clock.GetUtcNow()));
        await Assert.ThrowsAsync<InvalidOperationException>(() => new LocationStateAccess(database, new LocationStateAuthority(_actor),
            new Schema(LocationSchemaDecision.Valid), _clock, NullLogger<LocationStateAccess>.Instance).ChangeAsync(Reader,
            Guid.NewGuid(), new(Guid.NewGuid(), Guid.NewGuid().ToString("D"), true, "Ready")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LocationStateProviderFailureDoesNotBecomeSuccessfulEmptyReceipt(bool transient)
    {
        await using var database = Open(_tenant, new QueryFailure(transient));
        var failure = await Assert.ThrowsAsync<WmsDatabaseException>(() => new LocationStateAccess(database, new LocationStateAuthority(_actor),
            new Schema(LocationSchemaDecision.Valid), _clock, NullLogger<LocationStateAccess>.Instance).ChangeAsync(Reader,
            Guid.NewGuid(), new(Guid.NewGuid(), Guid.NewGuid().ToString("D"), true, "Ready")));
        Assert.Equal(transient, failure.IsTransient); Assert.Null(failure.InnerException);
        Assert.DoesNotContain("sensitive", failure.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("tenant")]
    [InlineData("actor")]
    [InlineData("session")]
    public async Task LocationStateRejectsUntrustedSubject(string invalid)
    {
        var (location, _) = await LocationReadyAsync();
        var subject = invalid == "tenant" ? Reader with { TenantId = _other }
            : invalid == "actor" ? Reader with { ActorId = Guid.Empty } : Reader with { SessionId = Guid.Empty };
        await using var database = Open(_tenant);
        Assert.Equal(new(LocationStateDecision.Forbidden), await new LocationStateAccess(database, new LocationStateAuthority(_actor),
            new Schema(LocationSchemaDecision.Valid), _clock, NullLogger<LocationStateAccess>.Instance).ChangeAsync(subject,
            location.Id!.Value, new(Guid.NewGuid(), location.Version!, true, "Ready")));
    }

    [Theory]
    [InlineData("version")]
    [InlineData("operation")]
    [InlineData("reason")]
    [InlineData("control")]
    [InlineData("empty")]
    public async Task LocationStateRejectsMalformedCommand(string invalid)
    {
        var (location, _) = await LocationReadyAsync();
        var request = new LocationStateCommand(Guid.NewGuid(), location.Version!, true, "Ready");
        request = invalid switch
        {
            "version" => request with { ExpectedVersion = "bad" },
            "operation" => request with { OperationId = Guid.Empty },
            "reason" => request with { Reason = " Ready " },
            "control" => request with { Reason = "Bad\nreason" },
            _ => request with { Reason = "" }
        };
        Assert.Equal(new(LocationStateDecision.Invalid), await LocationStateAsync(location.Id!.Value, request));
        await AssertNoLocationStateAsync(location.Version!);
    }

    [Theory]
    [InlineData("stock")]
    [InlineData("reservation")]
    [InlineData("pending-target")]
    [InlineData("zero")]
    public async Task LocationStateDisableChecksActualLocationActivity(string activity)
    {
        var (location, warehouse) = await LocationReadyAsync();
        var enabled = await LocationStateAsync(location.Id!.Value, new(Guid.NewGuid(), location.Version!, true, "Ready"));
        await using (var database = Open(_tenant))
        {
            var balance = new StockBalance(_tenant, warehouse.Id!.Value, activity == "pending-target" ? Guid.NewGuid() : location.Id.Value,
                Guid.NewGuid(), "", Guid.NewGuid(), Guid.NewGuid(), 0);
            database.Balances.Add(balance); await database.SaveChangesAsync();
            if (activity == "stock")
                await database.Database.ExecuteSqlInterpolatedAsync($"UPDATE wms_stock_balances SET Quantity = 1 WHERE TenantId = {_tenant.ToString("D")} AND Id = {balance.Id.ToString("D")}");
            else if (activity == "reservation")
                await database.Database.ExecuteSqlInterpolatedAsync($@"INSERT INTO wms_stock_reservations
                    (TenantId, ExecutionUnitId, BalanceId, SourceDocumentId, SourceLineId, ActorId, OriginalQuantity, RemainingQuantity, Kind, CreatedAt)
                    VALUES ({_tenant.ToString("D")}, {Guid.NewGuid().ToString("D")}, {balance.Id.ToString("D")}, {Guid.NewGuid().ToString("D")},
                    {Guid.NewGuid().ToString("D")}, {_actor.ToString("D")}, 1, 1, 1, {_clock.GetUtcNow().UtcDateTime})");
            else if (activity == "pending-target")
            {
                var line = new InboundPlanLine(Guid.NewGuid(), balance.ItemId, balance.UnitId, "", 0, 1m);
                var header = new InboundDocument(_tenant, Guid.NewGuid(), _actor, warehouse.Id.Value, "PENDING", [line], _clock.GetUtcNow());
                database.InboundDocuments.Add(header); await database.SaveChangesAsync(); database.ChangeTracker.Clear();
                var approved = await new InboundWriter(database, _clock).ApproveAsync(header.Id, Guid.NewGuid(), _actor, Guid.NewGuid(), header.Version);
                database.ChangeTracker.Clear();
                var received = await new InboundWriter(database, _clock).ReceiveAsync(header.Id, new(Guid.NewGuid(), _actor, Guid.NewGuid(),
                    line.Id, line.ItemId, line.UnitId, "", balance.ContainerId, balance.LocationId, location.Id.Value, 1m),
                    Guid.NewGuid(), approved.Version!, balance.Id, balance.Version);
                Assert.Equal(InboundWriteDecision.Committed, received.Decision);
            }
        }
        var schema = new Schema(LocationSchemaDecision.Unavailable);
        var result = await LocationStateAsync(location.Id.Value, new(Guid.NewGuid(), enabled.Version!, false, "Stop"), schema: schema);
        Assert.Equal(activity == "zero" ? LocationStateDecision.Changed : LocationStateDecision.Blocked, result.Decision);
        Assert.Empty(schema.Inputs);
        await using var verify = Open(_tenant);
        Assert.Equal(activity != "zero", (await verify.Locations.SingleAsync()).Enabled);
    }

    [Fact]
    public async Task LocationStateCannotForgePriorFactOrDetachHistory()
    {
        var (location, _) = await LocationReadyAsync();
        await using (var database = Open(_tenant))
        {
            await using var transaction = await database.Database.BeginTransactionAsync();
            var row = await database.Locations.SingleAsync(); row.ChangeState(true);
            await Assert.ThrowsAsync<InvalidOperationException>(() => database.SaveChangesAsync());
            database.LocationStateChanges.Add(new(Guid.NewGuid(), _actor, Guid.NewGuid(), row, Guid.NewGuid().ToString("D"), false, "Forged", _clock.GetUtcNow()));
            await Assert.ThrowsAsync<InvalidOperationException>(() => database.SaveChangesAsync());
            database.ChangeTracker.Clear();
            row = await database.Locations.SingleAsync(); row.ChangeState(true);
            database.LocationStateChanges.Add(new(Guid.NewGuid(), _actor, Guid.NewGuid(), row, location.Version!, false, "Detached", _clock.GetUtcNow()));
            database.Entry(row).State = EntityState.Unchanged;
            await Assert.ThrowsAsync<InvalidOperationException>(() => database.SaveChangesAsync());
        }
        await AssertNoLocationStateAsync(location.Version!);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LocationStateFactsCannotBeChangedOrDeleted(bool delete)
    {
        var (location, _) = await LocationReadyAsync();
        await LocationStateAsync(location.Id!.Value, new(Guid.NewGuid(), location.Version!, true, "Ready"));
        await using var database = Open(_tenant);
        var fact = await database.LocationStateChanges.SingleAsync();
        if (delete) database.LocationStateChanges.Remove(fact);
        else database.Entry(fact).Property(item => item.Reason).CurrentValue = "Tampered";
        await Assert.ThrowsAsync<InvalidOperationException>(() => database.SaveChangesAsync());
    }

    private async Task<(MasterDataResult Location, WarehouseStateResult Warehouse)> LocationReadyAsync()
    {
        var warehouse = await ExecuteAsync(Command());
        var enabled = await StateAsync(warehouse.Id!.Value, new(Guid.NewGuid(), warehouse.Version!, true, "Ready"));
        var location = await ExecuteAsync(Command("LOC") with { WarehouseId = warehouse.Id, PackId = Guid.NewGuid(), SchemaVersion = "v1" },
            new Schema(LocationSchemaDecision.Valid));
        return (location, enabled);
    }

    private async Task AssertNoLocationStateAsync(string version)
    {
        await using var database = Open(_tenant);
        var row = await database.Locations.SingleAsync(); Assert.False(row.Enabled); Assert.Equal(version, row.Version);
        Assert.Empty(await database.LocationStateChanges.ToListAsync());
        Assert.Equal(2, await database.MasterDataChanges.CountAsync()); Assert.Equal(2, await database.OutboxMessages.CountAsync());
    }

    private async Task<LocationStateResult> LocationStateAsync(Guid id, LocationStateCommand command,
        IMasterDataAuthorization? authority = null, IWarehouseLocationSchema? schema = null)
    {
        await using var owner = new WmsLocationStateWriter(_connection, _tenant, authority ?? new LocationStateAuthority(_actor),
            schema ?? new Schema(LocationSchemaDecision.Valid), _clock, NullLoggerFactory.Instance);
        return await owner.ChangeAsync(Reader, id, command);
    }

    private sealed class LocationStateAuthority(Guid creator, Guid? parentCreator = null) : IMasterDataAuthorization
    {
        internal string? Missing { get; init; }
        internal bool RevokeAfterFirst { get; init; }
        private int _calls;
        public Task<MasterDataCreatorAccess> ReadCreatorsAsync(MasterDataSubject subject, string permission,
            IReadOnlyCollection<string> fields, CancellationToken ct) => throw new InvalidOperationException("State does not use catalog authority.");
        public Task<IReadOnlySet<string>> ReadFieldsAsync(MasterDataSubject subject, string permission, Guid creatorId, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Assert.Equal(permission == "wms.warehouses.read" ? parentCreator ?? creator : creator, creatorId);
            if (permission == "wms.locations.write") ++_calls;
            var fields = permission == "wms.locations.write" ? new[] { "Enabled" }
                : permission == "wms.locations.read" ? new[] { "Id", "Code", "Name", "Enabled", "Version", "CreatedAt", "WarehouseId", "PackId", "SchemaVersion" }
                : new[] { "Id", "Code", "Name", "Enabled", "Version", "CreatedAt" };
            var prefix = permission == "wms.locations.write" ? "write:" : permission == "wms.warehouses.read" ? "parent:" : "";
            return Task.FromResult<IReadOnlySet<string>>(RevokeAfterFirst && _calls > 1 ? new HashSet<string>()
                : fields.Where(field => prefix + field != Missing).ToHashSet());
        }
    }
}
