using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Logging.Abstractions;
using Wes.Wms;
using Wes.Wms.Domain;
using Wes.Wms.Features.Inbound;
using Wes.Wms.Features.Ledger;
using Wes.Wms.Features.MasterData;
using Xunit;

namespace Wes.Persistence.Tests;

public sealed partial class WmsMasterDataTests
{
    private MasterDataSubject Reader => new(_tenant, _actor, Guid.NewGuid());
    [Fact]
    public async Task WarehouseStateRetainsPermanentOriginalReceiptAndCreationFact()
    {
        var creation = Command();
        var created = await ExecuteAsync(creation);
        var request = new WarehouseStateCommand(Guid.NewGuid(), created.Version!, true, "Ready for logical use");
        WarehouseStateResult enabled;
        await using (var database = Open(_tenant))
            enabled = await new WarehouseStateAccess(database, new StateAuthority(_actor), _clock,
                NullLogger<WarehouseStateAccess>.Instance).ChangeAsync(Reader, created.Id!.Value, request);
        Assert.Equal(WarehouseStateDecision.Changed, enabled.Decision);
        await using (var database = Open(_tenant))
            Assert.Equal(WarehouseStateDecision.Changed, (await new WarehouseStateAccess(database, new StateAuthority(_actor), _clock,
                NullLogger<WarehouseStateAccess>.Instance).ChangeAsync(Reader, created.Id!.Value,
                new(Guid.NewGuid(), enabled.Version!, false, "No active work"))).Decision);
        await using (var database = Open(_tenant))
            Assert.Equal(enabled with { Decision = WarehouseStateDecision.Replayed },
                await new WarehouseStateAccess(database, new StateAuthority(_actor), _clock, NullLogger<WarehouseStateAccess>.Instance)
                    .ChangeAsync(Reader, created.Id!.Value, request));
        Assert.Equal(created with { Decision = MasterDataDecision.Replayed }, await ExecuteAsync(creation));
        await using var verify = Open(_tenant);
        Assert.False((await verify.Warehouses.SingleAsync()).Enabled);
        Assert.Single(await verify.MasterDataChanges.ToListAsync());
        Assert.Equal(2, await verify.WarehouseStateChanges.CountAsync());
        Assert.Single(await verify.OutboxMessages.ToListAsync());
        Assert.Empty(await verify.Postings.ToListAsync());
    }

    [Theory]
    [InlineData("write:Enabled")]
    [InlineData("Id")]
    [InlineData("Code")]
    [InlineData("Name")]
    [InlineData("Enabled")]
    [InlineData("Version")]
    [InlineData("CreatedAt")]
    public async Task WarehouseStateRequiresEveryCurrentFieldBeforeNewChangeAndReplay(string missing)
    {
        var created = await ExecuteAsync(Command());
        var request = new WarehouseStateCommand(Guid.NewGuid(), created.Version!, true, "Ready");
        Assert.Equal(new(WarehouseStateDecision.Forbidden),
            await StateAsync(created.Id!.Value, request, new StateAuthority(_actor) { Missing = missing }));
        await AssertNoStateAsync(created.Version!);
        Assert.Equal(WarehouseStateDecision.Changed, (await StateAsync(created.Id.Value, request)).Decision);
        Assert.Equal(new(WarehouseStateDecision.Forbidden),
            await StateAsync(created.Id.Value, request, new StateAuthority(_actor) { Missing = missing }));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WarehouseStateRechecksAfterSaveAndBeforeReplayDisclosure(bool replay)
    {
        var created = await ExecuteAsync(Command());
        var request = new WarehouseStateCommand(Guid.NewGuid(), created.Version!, true, "Ready");
        if (replay) Assert.Equal(WarehouseStateDecision.Changed, (await StateAsync(created.Id!.Value, request)).Decision);
        Assert.Equal(new(WarehouseStateDecision.Forbidden),
            await StateAsync(created.Id!.Value, request, new StateAuthority(_actor) { RevokeAfterFirst = true }));
        if (!replay) await AssertNoStateAsync(created.Version!);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WarehouseStateSavedThenFaultOrCancellationRollsBackBothFactAndState(bool cancel)
    {
        var created = await ExecuteAsync(Command());
        using var cancellation = new CancellationTokenSource();
        await using (var database = Open(_tenant, new AfterSaveFault(cancel, cancellation)))
        {
            var access = new WarehouseStateAccess(database, new StateAuthority(_actor), _clock, NullLogger<WarehouseStateAccess>.Instance);
            Task<WarehouseStateResult> Execute() => access.ChangeAsync(Reader, created.Id!.Value,
                new(Guid.NewGuid(), created.Version!, true, "Ready"), cancellation.Token);
            if (cancel) await Assert.ThrowsAnyAsync<OperationCanceledException>(Execute);
            else await Assert.ThrowsAsync<InvalidOperationException>(Execute);
        }
        await AssertNoStateAsync(created.Version!);
    }

    [Theory]
    [InlineData("version")]
    [InlineData("operation")]
    [InlineData("reason")]
    [InlineData("control")]
    [InlineData("empty")]
    public async Task WarehouseStateRejectsInvalidInputBeforeAuthority(string invalid)
    {
        var created = await ExecuteAsync(Command());
        var request = new WarehouseStateCommand(Guid.NewGuid(), created.Version!, true, "Ready");
        request = invalid switch
        {
            "version" => request with { ExpectedVersion = "bad" },
            "operation" => request with { OperationId = Guid.Empty },
            "reason" => request with { Reason = " Ready " },
            "control" => request with { Reason = "Bad\nreason" },
            _ => request with { Reason = "" }
        };
        var authority = new StateAuthority(_actor);
        Assert.Equal(new(WarehouseStateDecision.Invalid), await StateAsync(created.Id!.Value, request, authority));
        Assert.Equal(0, authority.Calls);
        await AssertNoStateAsync(created.Version!);
    }

    [Fact]
    public async Task WarehouseStateRejectsSameStateStaleVersionAndChangedOriginalTuple()
    {
        var created = await ExecuteAsync(Command());
        var request = new WarehouseStateCommand(Guid.NewGuid(), created.Version!, true, "Ready");
        Assert.Equal(WarehouseStateDecision.Conflict,
            (await StateAsync(created.Id!.Value, request with { Enabled = false })).Decision);
        Assert.Equal(WarehouseStateDecision.Conflict,
            (await StateAsync(created.Id.Value, request with { ExpectedVersion = Guid.NewGuid().ToString("D") })).Decision);
        var changed = await StateAsync(created.Id.Value, request);
        Assert.Equal(WarehouseStateDecision.Changed, changed.Decision);
        foreach (var other in new[] { request with { Reason = "Other" }, request with { Enabled = false },
            request with { ExpectedVersion = changed.Version! } })
            Assert.Equal(WarehouseStateDecision.Conflict, (await StateAsync(created.Id.Value, other)).Decision);
        await using var verify = Open(_tenant);
        Assert.Single(await verify.WarehouseStateChanges.ToListAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WarehouseStateParentLockSerializesConcurrentOriginalAndDifferentOperations(bool same)
    {
        var created = await ExecuteAsync(Command());
        var first = new WarehouseStateCommand(Guid.NewGuid(), created.Version!, true, "Ready");
        var results = await Task.WhenAll(StateAsync(created.Id!.Value, first),
            StateAsync(created.Id.Value, same ? first : first with { OperationId = Guid.NewGuid() }));
        Assert.Single(results, result => result.Decision == WarehouseStateDecision.Changed);
        Assert.Single(results, result => result.Decision == (same ? WarehouseStateDecision.Replayed : WarehouseStateDecision.Conflict));
        await using var verify = Open(_tenant);
        Assert.Single(await verify.WarehouseStateChanges.ToListAsync());
    }

    [Theory]
    [InlineData("inbound")]
    [InlineData("stock")]
    [InlineData("reservation")]
    [InlineData("location")]
    public async Task WarehouseStateDisableIsBlockedByActualWarehouseActivity(string activity)
    {
        var created = await ExecuteAsync(Command());
        var enabled = await StateAsync(created.Id!.Value, new(Guid.NewGuid(), created.Version!, true, "Ready"));
        await using (var database = Open(_tenant))
        {
            if (activity == "inbound")
            {
                var header = new InboundDocument(_tenant, Guid.NewGuid(), _actor, created.Id.Value, "ACTIVE-IN",
                    [new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "", 0, 5m)], _clock.GetUtcNow());
                database.InboundDocuments.Add(header); await database.SaveChangesAsync();
                database.ChangeTracker.Clear();
                Assert.Equal(InboundWriteDecision.Committed, (await new InboundWriter(database, _clock).ApproveAsync(
                    header.Id, Guid.NewGuid(), _actor, Guid.NewGuid(), header.Version)).Decision);
            }
            else if (activity == "location")
            {
                var location = new WarehouseLocation(_tenant, created.Id.Value, Guid.NewGuid(), "v1", "L1", "Location", _clock.GetUtcNow());
                database.Locations.Add(location); database.MasterDataChanges.Add(new(Guid.NewGuid(), _actor, Guid.NewGuid(), location));
                await database.SaveChangesAsync();
                // Fixture represents an independently enabled child; no public location activation is claimed by this slice.
                await database.Database.ExecuteSqlInterpolatedAsync($"UPDATE wms_locations SET Enabled = 1 WHERE TenantId = {_tenant.ToString("D")} AND Id = {location.Id.ToString("D")}");
            }
            else
            {
                var balance = new StockBalance(_tenant, created.Id.Value, Guid.NewGuid(), Guid.NewGuid(), "", Guid.NewGuid(), Guid.NewGuid(), 0);
                database.Balances.Add(balance); await database.SaveChangesAsync();
                if (activity == "stock")
                    await database.Database.ExecuteSqlInterpolatedAsync($"UPDATE wms_stock_balances SET Quantity = 1 WHERE TenantId = {_tenant.ToString("D")} AND Id = {balance.Id.ToString("D")}");
                else
                {
                    // Isolated fixture represents an outstanding reservation independently of quantity blocking.
                    await database.Database.ExecuteSqlInterpolatedAsync($@"INSERT INTO wms_stock_reservations
                        (TenantId, ExecutionUnitId, BalanceId, SourceDocumentId, SourceLineId, ActorId, OriginalQuantity, RemainingQuantity, Kind, CreatedAt)
                        VALUES ({_tenant.ToString("D")}, {Guid.NewGuid().ToString("D")}, {balance.Id.ToString("D")}, {Guid.NewGuid().ToString("D")},
                        {Guid.NewGuid().ToString("D")}, {_actor.ToString("D")}, 1, 1, 1, {_clock.GetUtcNow().UtcDateTime})");
                }
            }
        }
        Assert.Equal(new(WarehouseStateDecision.Blocked),
            await StateAsync(created.Id.Value, new(Guid.NewGuid(), enabled.Version!, false, "Stop")));
        await using var verify = Open(_tenant);
        Assert.True((await verify.Warehouses.SingleAsync()).Enabled);
        Assert.Equal(enabled.Version, (await verify.Warehouses.SingleAsync()).Version);
        Assert.Single(await verify.WarehouseStateChanges.ToListAsync());
    }

    [Fact]
    public async Task WarehouseStateDraftAndEmptyStockDoNotPretendToBeActiveExecution()
    {
        var created = await ExecuteAsync(Command());
        var enabled = await StateAsync(created.Id!.Value, new(Guid.NewGuid(), created.Version!, true, "Ready"));
        await using (var database = Open(_tenant))
        {
            database.InboundDocuments.Add(new(_tenant, Guid.NewGuid(), _actor, created.Id.Value, "DRAFT",
                [new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "", 0, 5m)], _clock.GetUtcNow()));
            database.Balances.Add(new(_tenant, created.Id.Value, Guid.NewGuid(), Guid.NewGuid(), "", Guid.NewGuid(), Guid.NewGuid(), 0));
            await database.SaveChangesAsync();
        }
        Assert.Equal(WarehouseStateDecision.Changed,
            (await StateAsync(created.Id.Value, new(Guid.NewGuid(), enabled.Version!, false, "No execution"))).Decision);
    }

    [Fact]
    public async Task WarehouseStateCannotPersistUnmatchedStateOrForgePriorVersion()
    {
        var created = await ExecuteAsync(Command());
        await using (var database = Open(_tenant))
        {
            var warehouse = await database.Warehouses.SingleAsync();
            warehouse.ChangeState(true);
            await Assert.ThrowsAsync<InvalidOperationException>(() => database.SaveChangesAsync());
            database.WarehouseStateChanges.Add(new(Guid.NewGuid(), _actor, Guid.NewGuid(), warehouse,
                Guid.NewGuid().ToString("D"), false, "Forged prior version", _clock.GetUtcNow()));
            await Assert.ThrowsAsync<InvalidOperationException>(() => database.SaveChangesAsync());
        }
        await AssertNoStateAsync(created.Version!);
    }

    [Fact]
    public async Task WarehouseStateFactsCannotBeChangedDeletedOrDetached()
    {
        var created = await ExecuteAsync(Command());
        await StateAsync(created.Id!.Value, new(Guid.NewGuid(), created.Version!, true, "Ready"));
        await using (var database = Open(_tenant))
        {
            var fact = await database.WarehouseStateChanges.SingleAsync();
            database.Entry(fact).Property(item => item.Reason).CurrentValue = "Tampered";
            await Assert.ThrowsAsync<InvalidOperationException>(() => database.SaveChangesAsync());
        }
        await using (var database = Open(_tenant))
        {
            database.WarehouseStateChanges.Remove(await database.WarehouseStateChanges.SingleAsync());
            await Assert.ThrowsAsync<InvalidOperationException>(() => database.SaveChangesAsync());
        }
        await using var verify = Open(_tenant);
        Assert.Equal("Ready", (await verify.WarehouseStateChanges.SingleAsync()).Reason);
    }

    [Theory]
    [InlineData("tenant")]
    [InlineData("actor")]
    [InlineData("session")]
    public async Task WarehouseStateInvalidTrustedSubjectCannotBorrowResourceAuthority(string invalid)
    {
        var created = await ExecuteAsync(Command());
        var subject = invalid switch
        {
            "tenant" => Reader with { TenantId = _other },
            "actor" => Reader with { ActorId = Guid.Empty },
            _ => Reader with { SessionId = Guid.Empty }
        };
        var authority = new StateAuthority(_actor);
        await using var database = Open(_tenant);
        Assert.Equal(new(WarehouseStateDecision.Forbidden),
            await new WarehouseStateAccess(database, authority, _clock, NullLogger<WarehouseStateAccess>.Instance)
                .ChangeAsync(subject, created.Id!.Value, new(Guid.NewGuid(), created.Version!, true, "Ready")));
        Assert.Equal(0, authority.Calls);
    }

    [Fact]
    public async Task WarehouseStateUsesActualCreatorAndHidesForeignOrMissingResources()
    {
        var created = await ExecuteAsync(Command());
        await using (var database = Open(_tenant))
            Assert.Equal(WarehouseStateDecision.Changed,
                (await new WarehouseStateAccess(database, new StateAuthority(_actor), _clock, NullLogger<WarehouseStateAccess>.Instance)
                    .ChangeAsync(Reader with { ActorId = Guid.NewGuid() }, created.Id!.Value, new(Guid.NewGuid(), created.Version!, true, "Ready"))).Decision);
        await using (var foreign = Open(_other))
            Assert.Equal(new(WarehouseStateDecision.NotFound),
                await new WarehouseStateAccess(foreign, new StateAuthority(_actor), _clock, NullLogger<WarehouseStateAccess>.Instance)
                    .ChangeAsync(Reader with { TenantId = _other }, created.Id!.Value, new(Guid.NewGuid(), created.Version!, true, "Ready")));
        Assert.Equal(new(WarehouseStateDecision.NotFound),
            await StateAsync(Guid.NewGuid(), new(Guid.NewGuid(), created.Version!, true, "Ready")));
    }

    [Theory]
    [InlineData("dirty")]
    [InlineData("transaction")]
    [InlineData("ambient")]
    public async Task WarehouseStateRejectsNonIndependentOwnersBeforeAuthority(string invalid)
    {
        await using var database = Open(_tenant);
        await using var transaction = invalid == "transaction" ? await database.Database.BeginTransactionAsync() : null;
        using var ambient = invalid == "ambient"
            ? new System.Transactions.TransactionScope(System.Transactions.TransactionScopeAsyncFlowOption.Enabled) : null;
        if (invalid == "dirty") database.Warehouses.Add(new(_tenant, "DIRTY", "Dirty", _clock.GetUtcNow()));
        var authority = new StateAuthority(_actor);
        await Assert.ThrowsAsync<InvalidOperationException>(() => new WarehouseStateAccess(database, authority, _clock,
            NullLogger<WarehouseStateAccess>.Instance).ChangeAsync(Reader, Guid.NewGuid(), new(Guid.NewGuid(), Guid.NewGuid().ToString("D"), true, "Ready")));
        Assert.Equal(0, authority.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WarehouseStateDatabaseFailureIsSafeAndNeverBecomesSuccessfulReceipt(bool transient)
    {
        await using var database = Open(_tenant, new QueryFailure(transient));
        var error = await Assert.ThrowsAsync<WmsDatabaseException>(() => new WarehouseStateAccess(database, new StateAuthority(_actor), _clock,
            NullLogger<WarehouseStateAccess>.Instance).ChangeAsync(Reader, Guid.NewGuid(), new(Guid.NewGuid(), Guid.NewGuid().ToString("D"), true, "Ready")));
        Assert.Equal(transient, error.IsTransient); Assert.Null(error.InnerException);
        Assert.DoesNotContain("sensitive-sentinel", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task WarehouseStateCannotAppendAFactDetachedFromItsStateTransition()
    {
        var created = await ExecuteAsync(Command());
        await using (var database = Open(_tenant))
        {
            var warehouse = await database.Warehouses.SingleAsync();
            warehouse.ChangeState(true);
            database.WarehouseStateChanges.Add(new(Guid.NewGuid(), _actor, Guid.NewGuid(), warehouse, created.Version!, false, "Detached", _clock.GetUtcNow()));
            database.Entry(warehouse).State = EntityState.Unchanged;
            await Assert.ThrowsAsync<InvalidOperationException>(() => database.SaveChangesAsync());
        }
        await AssertNoStateAsync(created.Version!);
    }

    [Fact]
    public void WarehouseStateMigrationScriptUsesTheSameHistoryAsTheRuntimeOwner()
    {
        using var database = new Wes.Wms.Infrastructure.Persistence.WmsDesignTimeFactory().CreateDbContext([]);
        var script = database.GetService<IMigrator>().GenerateScript("20261027151032_WmsInboundReceipts", "20261027173433_WmsWarehouseState");
        Assert.Contains("CREATE TABLE `wms_warehouse_state_changes`", script, StringComparison.Ordinal);
        Assert.Contains("INSERT INTO `__WesWmsMigrations`", script, StringComparison.Ordinal);
        Assert.DoesNotContain("__EFMigrationsHistory", script, StringComparison.Ordinal);
        Assert.DoesNotContain("ALTER TABLE", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("UPDATE ", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("DROP TABLE", script, StringComparison.OrdinalIgnoreCase);
    }

    private async Task<WarehouseStateResult> StateAsync(Guid id, WarehouseStateCommand request, StateAuthority? authority = null)
    {
        await using var owner = new WmsWarehouseStateWriter(_connection, _tenant, authority ?? new StateAuthority(_actor), _clock, NullLoggerFactory.Instance);
        return await owner.ChangeAsync(Reader, id, request);
    }

    private async Task AssertNoStateAsync(string version)
    {
        await using var verify = Open(_tenant);
        var warehouse = await verify.Warehouses.SingleAsync();
        Assert.False(warehouse.Enabled); Assert.Equal(version, warehouse.Version);
        Assert.Empty(await verify.WarehouseStateChanges.ToListAsync());
        Assert.Single(await verify.MasterDataChanges.ToListAsync());
        Assert.Single(await verify.OutboxMessages.ToListAsync());
    }

    private sealed class StateAuthority(Guid creator) : IMasterDataAuthorization
    {
        internal string? Missing { get; init; }
        internal bool RevokeAfterFirst { get; init; }
        internal int Calls { get; private set; }
        public Task<IReadOnlySet<string>> ReadFieldsAsync(MasterDataSubject subject, string permission, Guid actualCreator, CancellationToken cancellationToken)
        {
            Assert.Equal(creator, actualCreator);
            if (permission == "wms.warehouses.write") ++Calls;
            return Task.FromResult<IReadOnlySet<string>>(RevokeAfterFirst && Calls > 1 ? new HashSet<string>()
                : permission == "wms.warehouses.write" ? new[] { "Enabled" }.Where(field => $"write:{field}" != Missing).ToHashSet()
                : new[] { "Id", "Code", "Name", "Enabled", "Version", "CreatedAt" }.Where(field => field != Missing).ToHashSet());
        }
        public Task<MasterDataCreatorAccess> ReadCreatorsAsync(MasterDataSubject subject, string permission,
            IReadOnlyCollection<string> requiredFields, CancellationToken cancellationToken) => throw new InvalidOperationException("No catalog authority is required.");
    }
}
