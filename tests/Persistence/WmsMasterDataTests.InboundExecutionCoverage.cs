using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Wes.Wms.Domain;
using Wes.Wms.Features.Inbound;
using Wes.Wms.Features.Ledger;
using Wes.Wms.Features.MasterData;
using Xunit;

namespace Wes.Persistence.Tests;

public sealed partial class WmsMasterDataTests
{
    public static TheoryData<string> ExecutionReceiptFields
    {
        get
        {
            var result = new TheoryData<string>();
            foreach (var row in ExecutionApprovalFields)
                if (!row.StartsWith("wms.inbound.approve:", StringComparison.Ordinal)) result.Add(row);
            foreach (var (permission, fields) in new (string, string[])[]
            {
                ("wms.inbound.receive", ["LineId", "ExecutionId", "Quantity", "ContainerId", "ReceiptLocationId", "TargetLocationId", "BalanceId"]),
                ("wms.locations.read", ["Id", "Code", "Name", "Enabled", "Version", "CreatedAt", "WarehouseId", "PackId", "SchemaVersion"]),
                ("wms.containers.read", ["Id", "Code", "Name", "WarehouseId", "Version", "CreatedAt"])
            })
                foreach (var name in fields) result.Add($"{permission}:{name}");
            return result;
        }
    }

    [Theory]
    [MemberData(nameof(ExecutionReceiptFields))]
    public async Task InboundExecutionReceiptRequiresEveryCurrentFieldIncludingOriginalRecovery(string missing)
    {
        var fixture = await ExecutionReadyAsync(); var request = await ExecutionReceiptAsync(fixture);
        await using var owner = Open(_tenant);
        var authority = new ExecutionAuthority { Missing = missing };
        Assert.Equal(new(InboundExecutionDecision.Forbidden), await ExecutionAccess(owner, authority).ReceiveAsync(Reader, fixture.Order, request));
        await AssertExecutionUnchangedAsync(true);
        Assert.Equal(InboundExecutionDecision.Committed, (await ExecutionAccess(owner).ReceiveAsync(Reader, fixture.Order, request)).Decision);
        Assert.Equal(new(InboundExecutionDecision.Forbidden), await ExecutionAccess(owner, authority).ReceiveAsync(Reader, fixture.Order, request));
        Assert.Single(await owner.Postings.ToListAsync());
    }

    [Fact]
    public async Task InboundExecutionPartialThenRemainingReceiptUsesCurrentVersionsButPreservesOriginalReceipt()
    {
        var fixture = await ExecutionReadyAsync(); var request = await ExecutionReceiptAsync(fixture);
        await using var owner = Open(_tenant);
        var partial = request with { Quantity = "2.000" };
        var first = await ExecutionAccess(owner).ReceiveAsync(Reader, fixture.Order, partial);
        Assert.Equal(InboundExecutionDecision.Committed, first.Decision);
        Assert.Equal(InboundOrderState.Executing, (await owner.InboundDocuments.AsNoTracking().SingleAsync()).State);
        var balanceVersion = await owner.Balances.AsNoTracking().Select(row => row.Version).SingleAsync();
        var remaining = request with
        {
            OperationId = Guid.NewGuid(),
            ExecutionId = Guid.NewGuid(),
            Quantity = "3",
            ExpectedVersion = first.Version!,
            ExpectedBalanceVersion = balanceVersion
        };
        var second = await ExecutionAccess(owner).ReceiveAsync(Reader, fixture.Order, remaining);
        Assert.Equal(InboundExecutionDecision.Committed, second.Decision);
        Assert.Equal(first with { Decision = InboundExecutionDecision.Replayed }, await ExecutionAccess(owner).ReceiveAsync(Reader, fixture.Order, partial));
        Assert.NotEqual(first.Version, second.Version);
        Assert.Equal(InboundOrderState.Completed, (await owner.InboundDocuments.AsNoTracking().SingleAsync()).State);
        Assert.Equal(5m, (await owner.Balances.AsNoTracking().SingleAsync()).Quantity);
        Assert.Equal(2, await owner.Postings.CountAsync()); Assert.Equal(3, await owner.InboundExecutionOperations.CountAsync());
    }

    [Theory]
    [InlineData("item")]
    [InlineData("unit")]
    [InlineData("precision")]
    [InlineData("batch")]
    public async Task InboundExecutionCannotApproveLegacyPlanWithUnregisteredOrIncompatibleResources(string mismatch)
    {
        var fixture = await ExecutionReadyAsync();
        await using var owner = Open(_tenant);
        var legacy = await new InboundWriter(owner, _clock).CreateAsync(Guid.NewGuid(), _actor, fixture.Warehouse, "LEGACY",
            [new(Guid.NewGuid(), mismatch == "item" ? Guid.NewGuid() : fixture.Item,
                mismatch == "unit" ? Guid.NewGuid() : fixture.Unit, mismatch == "batch" ? "UNAPPROVED" : "",
                mismatch == "precision" ? 2 : 3, 5m)]);
        owner.ChangeTracker.Clear();
        Assert.Equal(new(InboundExecutionDecision.Invalid), await ExecutionAccess(owner).ApproveAsync(Reader, legacy.Id!.Value,
            new(Guid.NewGuid(), Guid.NewGuid(), legacy.Version!)));
        Assert.Empty(await owner.InboundFacts.ToListAsync()); Assert.Empty(await owner.Postings.ToListAsync());
    }

    [Fact]
    public async Task InboundExecutionDoesNotAdoptLegacyUnregisteredBalance()
    {
        var fixture = await ExecutionReadyAsync(); var request = await ExecutionReceiptAsync(fixture);
        await using var owner = Open(_tenant);
        await owner.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM wms_stock_dimension_registrations WHERE TenantId = {_tenant.ToString("D")}");
        Assert.Equal(new(InboundExecutionDecision.Invalid), await ExecutionAccess(owner).ReceiveAsync(Reader, fixture.Order, request));
        await AssertExecutionUnchangedAsync(true);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InboundExecutionCannotChangeOrDeletePermanentRequestIdentity(bool delete)
    {
        var fixture = await ExecutionReadyAsync();
        await using var owner = Open(_tenant);
        await ExecutionAccess(owner).ApproveAsync(Reader, fixture.Order, new(Guid.NewGuid(), Guid.NewGuid(), fixture.OrderVersion));
        var row = await owner.InboundExecutionOperations.SingleAsync();
        if (delete) owner.InboundExecutionOperations.Remove(row);
        else owner.Entry(row).Property(value => value.Input).CurrentValue = "{}";
        await Assert.ThrowsAsync<InvalidOperationException>(() => owner.SaveChangesAsync());
    }

    [Fact]
    public async Task InboundExecutionDoesNotAdoptAnOldPrimitiveFact()
    {
        var fixture = await ExecutionReadyAsync(); var operation = Guid.NewGuid();
        await using var owner = Open(_tenant);
        var internalResult = await new InboundWriter(owner, _clock).ApproveAsync(fixture.Order, operation, _actor, Guid.NewGuid(), fixture.OrderVersion);
        Assert.Equal(InboundWriteDecision.Committed, internalResult.Decision);
        owner.ChangeTracker.Clear();
        Assert.Equal(new(InboundExecutionDecision.Conflict), await ExecutionAccess(owner).ApproveAsync(Reader, fixture.Order,
            new(operation, Guid.NewGuid(), fixture.OrderVersion)));
        var fact = await owner.InboundFacts.SingleAsync();
        owner.InboundExecutionOperations.Add(new(fact, "{}", _clock.GetUtcNow()));
        await Assert.ThrowsAsync<InvalidOperationException>(() => owner.SaveChangesAsync());
    }

    [Theory]
    [InlineData("tenant")]
    [InlineData("actor")]
    [InlineData("session")]
    public async Task InboundExecutionRejectsUntrustedSubjects(string invalid)
    {
        var fixture = await ExecutionReadyAsync();
        var subject = invalid switch
        {
            "tenant" => Reader with { TenantId = Guid.NewGuid() },
            "actor" => Reader with { ActorId = Guid.Empty },
            _ => Reader with { SessionId = Guid.Empty }
        };
        await using var owner = Open(_tenant);
        Assert.Equal(new(InboundExecutionDecision.Forbidden), await ExecutionAccess(owner).ApproveAsync(subject, fixture.Order,
            new(Guid.NewGuid(), Guid.NewGuid(), fixture.OrderVersion)));
        await AssertExecutionUnchangedAsync(false);
    }

    [Fact]
    public async Task InboundExecutionCannotResolveAnotherTenantsOrderOrOperation()
    {
        var fixture = await ExecutionReadyAsync(); var request = await ExecutionReceiptAsync(fixture);
        await using var owner = Open(_other);
        var subject = Reader with { TenantId = _other };
        Assert.Equal(new(InboundExecutionDecision.NotFound), await ExecutionAccess(owner).ApproveAsync(subject, fixture.Order,
            new(Guid.NewGuid(), Guid.NewGuid(), fixture.OrderVersion)));
        Assert.Equal(new(InboundExecutionDecision.NotFound), await ExecutionAccess(owner).ReceiveAsync(subject, fixture.Order, request));
        Assert.Empty(await owner.InboundExecutionOperations.ToListAsync()); Assert.Empty(await owner.Postings.ToListAsync());
        await AssertExecutionUnchangedAsync(true);
    }

    [Fact]
    public async Task InboundExecutionUsesActualDifferentCreatorsForEveryResource()
    {
        var fixture = await ExecutionReadyAsync();
        var unitActor = Guid.NewGuid(); var itemActor = Guid.NewGuid(); var containerActor = Guid.NewGuid(); var documentActor = Guid.NewGuid();
        await using var owner = Open(_tenant);
        var resources = ResourceAccess(owner);
        var unit = await resources.UnitAsync(Reader with { ActorId = unitActor }, new(Guid.NewGuid(), Guid.NewGuid(), "EACH", "Each", true, 0));
        var item = await resources.ItemAsync(Reader with { ActorId = itemActor }, new(Guid.NewGuid(), Guid.NewGuid(), "OTHER", "Other", unit.Id!.Value, false));
        var container = await resources.ContainerAsync(Reader with { ActorId = containerActor }, new(Guid.NewGuid(), Guid.NewGuid(), "OTHER", "Other", fixture.Warehouse));
        var balance = await resources.DimensionAsync(Reader, new(Guid.NewGuid(), Guid.NewGuid(), fixture.Warehouse, fixture.Location, item.Id!.Value, "", container.Id!.Value));
        var line = Guid.NewGuid();
        var created = await new InboundWriter(owner, _clock).CreateAsync(Guid.NewGuid(), documentActor, fixture.Warehouse,
            "OTHER", [new(line, item.Id.Value, unit.Id.Value, "", 0, 5m)]);
        owner.ChangeTracker.Clear();
        var authority = new ExecutionAuthority
        {
            Creators = new()
            {
                ["wms.inbound.read"] = documentActor,
                ["wms.inbound.approve"] = documentActor,
                ["wms.inbound.receive"] = documentActor,
                ["wms.warehouses.read"] = _actor,
                ["wms.locations.read"] = _actor,
                ["wms.units.read"] = unitActor,
                ["wms.items.read"] = itemActor,
                ["wms.containers.read"] = containerActor
            }
        };
        var approved = await ExecutionAccess(owner, authority).ApproveAsync(Reader, created.Id!.Value, new(Guid.NewGuid(), Guid.NewGuid(), created.Version!));
        Assert.Equal(InboundExecutionDecision.Committed, approved.Decision);
        var received = await ExecutionAccess(owner, authority).ReceiveAsync(Reader, created.Id.Value,
            new(Guid.NewGuid(), Guid.NewGuid(), approved.Version!, balance.Version!, line, Guid.NewGuid(), "5",
                container.Id.Value, fixture.Location, fixture.Location, balance.Id!.Value));
        Assert.Equal(InboundExecutionDecision.Committed, received.Decision);
        Assert.Equal(_actor, (await owner.InboundExecutionOperations.AsNoTracking().FirstAsync()).ActorId);
    }

    [Fact]
    public async Task InboundExecutionDifferentLocationStaysPendingAndProtectsContainerSourceAndTarget()
    {
        var fixture = await ExecutionReadyAsync(); var request = await ExecutionReceiptAsync(fixture);
        var target = await ExecuteAsync(Command("TARGET") with { WarehouseId = fixture.Warehouse, PackId = Guid.NewGuid(), SchemaVersion = "v1" },
            new Schema(LocationSchemaDecision.Valid));
        Assert.Equal(LocationStateDecision.Changed,
            (await LocationStateAsync(target.Id!.Value, new(Guid.NewGuid(), target.Version!, true, "Ready"))).Decision);
        await using var owner = Open(_tenant);
        var received = await ExecutionAccess(owner).ReceiveAsync(Reader, fixture.Order, request with { Quantity = "2", TargetLocationId = target.Id.Value });
        Assert.Equal(InboundExecutionDecision.Committed, received.Decision);
        Assert.Equal(InboundOrderState.Executing, (await owner.InboundDocuments.AsNoTracking().SingleAsync()).State);
        var pending = Assert.Single(await InboundPendingProtection.ReadAsync(owner, fixture.Warehouse, default));
        Assert.Equal(fixture.Container, pending.ContainerId); Assert.Equal(fixture.Location, pending.ReceiptLocationId);
        Assert.Equal(target.Id, pending.TargetLocationId);
        var balanceVersion = await owner.Balances.AsNoTracking().Select(row => row.Version).SingleAsync();
        Assert.Equal(new(InboundExecutionDecision.Blocked), await ExecutionAccess(owner).ReceiveAsync(Reader, fixture.Order,
            request with { OperationId = Guid.NewGuid(), ExecutionId = Guid.NewGuid(), ExpectedVersion = received.Version!, ExpectedBalanceVersion = balanceVersion, Quantity = "3" }));
        foreach (var location in await owner.Locations.AsNoTracking().ToListAsync())
            Assert.Equal(LocationStateDecision.Blocked, (await LocationStateAsync(location.Id, new(Guid.NewGuid(), location.Version, false, "Cannot release pending"))).Decision);
        Assert.Single(await owner.Postings.ToListAsync()); Assert.Equal(2m, (await owner.Balances.AsNoTracking().SingleAsync()).Quantity);
    }

    [Theory]
    [InlineData("dirty")]
    [InlineData("transaction")]
    [InlineData("ambient")]
    public async Task InboundExecutionRequiresIndependentCleanOwner(string invalid)
    {
        await using var owner = Open(_tenant);
        await using var transaction = invalid == "transaction" ? await owner.Database.BeginTransactionAsync() : null;
        using var ambient = invalid == "ambient" ? new System.Transactions.TransactionScope(System.Transactions.TransactionScopeAsyncFlowOption.Enabled) : null;
        if (invalid == "dirty") owner.Warehouses.Add(new(_tenant, "DIRTY", "Dirty", _clock.GetUtcNow()));
        await Assert.ThrowsAsync<InvalidOperationException>(() => ExecutionAccess(owner).ApproveAsync(Reader, Guid.NewGuid(),
            new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid().ToString("D"))));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InboundExecutionProviderFailureIsSafeAndNeverBecomesSuccessfulReceipt(bool transient)
    {
        var fixture = await ExecutionReadyAsync();
        await using var owner = Open(_tenant, new QueryFailure(transient));
        var failure = await Assert.ThrowsAsync<WmsDatabaseException>(() => ExecutionAccess(owner).ApproveAsync(Reader, fixture.Order,
            new(Guid.NewGuid(), Guid.NewGuid(), fixture.OrderVersion)));
        Assert.Equal(transient, failure.IsTransient); Assert.Null(failure.InnerException);
        Assert.DoesNotContain("sensitive", failure.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task InboundExecutionUnknownSchemaDecisionFailsExplicitlyWithNoLedger()
    {
        var fixture = await ExecutionReadyAsync(); var request = await ExecutionReceiptAsync(fixture);
        await using var owner = Open(_tenant);
        await Assert.ThrowsAsync<InvalidOperationException>(() => ExecutionAccess(owner, schema: new Schema((LocationSchemaDecision)99))
            .ReceiveAsync(Reader, fixture.Order, request));
        await AssertExecutionUnchangedAsync(true);
    }

    [Fact]
    public async Task InboundExecutionReceiptRechecksActualLocationStateAfterApproval()
    {
        var fixture = await ExecutionReadyAsync(); var request = await ExecutionReceiptAsync(fixture);
        await using var owner = Open(_tenant);
        var version = await owner.Locations.AsNoTracking().Select(row => row.Version).SingleAsync();
        Assert.Equal(LocationStateDecision.Changed,
            (await LocationStateAsync(fixture.Location, new(Guid.NewGuid(), version, false, "Stopped before receipt"))).Decision);
        Assert.Equal(new(InboundExecutionDecision.Blocked), await ExecutionAccess(owner).ReceiveAsync(Reader, fixture.Order, request));
        await AssertExecutionUnchangedAsync(true);
    }

    [Fact]
    public async Task InboundExecutionDifferentLegalBalancesCannotConcurrentlyOverreceiveTheSameOrder()
    {
        var fixture = await ExecutionReadyAsync(); var request = await ExecutionReceiptAsync(fixture);
        var secondLocation = await ExecuteAsync(Command("SOURCE2") with { WarehouseId = fixture.Warehouse, PackId = Guid.NewGuid(), SchemaVersion = "v1" },
            new Schema(LocationSchemaDecision.Valid));
        Assert.Equal(LocationStateDecision.Changed,
            (await LocationStateAsync(secondLocation.Id!.Value, new(Guid.NewGuid(), secondLocation.Version!, true, "Ready"))).Decision);
        InboundReceiptRegistration second;
        await using (var resources = Open(_tenant))
        {
            var container = await ResourceAccess(resources).ContainerAsync(Reader,
                new(Guid.NewGuid(), Guid.NewGuid(), "SECOND", "Second", fixture.Warehouse));
            var balance = await ResourceAccess(resources).DimensionAsync(Reader,
                new(Guid.NewGuid(), Guid.NewGuid(), fixture.Warehouse, secondLocation.Id.Value, fixture.Item, "", container.Id!.Value));
            second = request with
            {
                OperationId = Guid.NewGuid(),
                ExecutionId = Guid.NewGuid(),
                ContainerId = container.Id.Value,
                ReceiptLocationId = secondLocation.Id.Value,
                TargetLocationId = secondLocation.Id.Value,
                BalanceId = balance.Id!.Value,
                ExpectedBalanceVersion = balance.Version!
            };
        }
        async Task<InboundExecutionResult> Execute(InboundReceiptRegistration input)
        { await using var owner = Open(_tenant); return await ExecutionAccess(owner).ReceiveAsync(Reader, fixture.Order, input); }
        var results = await Task.WhenAll(Execute(request), Execute(second));
        Assert.Single(results, result => result.Decision == InboundExecutionDecision.Committed);
        Assert.Single(results, result => result.Decision == InboundExecutionDecision.Conflict);
        await using var verify = Open(_tenant);
        Assert.Equal(5m, await verify.Balances.SumAsync(row => row.Quantity)); Assert.Single(await verify.Postings.ToListAsync());
        Assert.Equal(2, await verify.InboundFacts.CountAsync()); Assert.Equal(2, await verify.InboundExecutionOperations.CountAsync());
    }

    [Fact]
    public async Task InboundExecutionConcurrentOriginalDifferentLocationReceiptReconcilesWinnerBeforeReturningBlocked()
    {
        var fixture = await ExecutionReadyAsync(); var request = await ExecutionReceiptAsync(fixture);
        var target = await ExecuteAsync(Command("TARGET") with { WarehouseId = fixture.Warehouse, PackId = Guid.NewGuid(), SchemaVersion = "v1" },
            new Schema(LocationSchemaDecision.Valid));
        await LocationStateAsync(target.Id!.Value, new(Guid.NewGuid(), target.Version!, true, "Ready"));
        request = request with { TargetLocationId = target.Id.Value };
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var pause = new ExecutionCommitPause();
        await using var winnerOwner = Open(_tenant, pause);
        var winner = ExecutionAccess(winnerOwner).ReceiveAsync(Reader, fixture.Order, request, deadline.Token);
        await pause.Entered.Task.WaitAsync(deadline.Token);
        var release = new ExecutionReleaseWinner(pause.Release, winner);
        await using var contender = Open(_tenant, release);
        try
        {
            var result = await ExecutionAccess(contender).ReceiveAsync(Reader, fixture.Order, request, deadline.Token);
            Assert.True(release.Reached);
            Assert.Equal((await winner) with { Decision = InboundExecutionDecision.Replayed }, result);
            Assert.Single(await contender.Postings.AsNoTracking().ToListAsync());
            Assert.Equal(2, await contender.InboundExecutionOperations.CountAsync());
        }
        finally { pause.Release.TrySetResult(); await winner; }
    }

    private sealed class ExecutionCommitPause : SaveChangesInterceptor
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
        {
            Entered.TrySetResult(); await Release.Task.WaitAsync(cancellationToken); return result;
        }
    }

    private sealed class ExecutionReleaseWinner(TaskCompletionSource release, Task winner) : DbCommandInterceptor
    {
        internal bool Reached { get; private set; }
        public override async ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData,
            DbDataReader result, CancellationToken cancellationToken = default)
        {
            if (!Reached && command.CommandText.Contains("wms_stock_postings", StringComparison.Ordinal))
            {
                // The contender's replay query observed no committed row; commit the winner before operational checks.
                Reached = true; release.TrySetResult(); await winner.WaitAsync(cancellationToken);
            }
            return result;
        }
    }
}
