using System.Transactions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Wes.Wms;
using Wes.Wms.Domain;
using Wes.Wms.Features.Inventory;
using Xunit;

namespace Wes.Persistence.Tests;

public sealed partial class WmsLedgerTests
{
    [Fact]
    public async Task InventoryReadsExactBookAndOverlappingProtectionUnionWithoutWrites()
    {
        await ReceiveAsync(10.125001m);
        var execution = Guid.NewGuid();
        await ReserveAsync(execution, 3.000001m);
        await ChangeReservationAsync(execution, ReservationAction.MarkInTransit, 0m);
        await ChangeReservationAsync(execution, ReservationAction.MarkUnknown, 0m);
        var released = Guid.NewGuid();
        await ReserveAsync(released, 2m);
        await ChangeReservationAsync(released, ReservationAction.ReleaseUnstarted, 2m);
        await using var database = Open(_tenant);
        var source = await database.Balances.AsNoTracking().SingleAsync(item => item.Id == _source);
        var result = await Reader(database).QueryAsync(Subject(), new());
        Assert.Equal(InventoryReadDecision.Success, result.Decision);
        var row = Assert.Single(result.Rows, item => item.Id == _source);
        Assert.Equal(("10.125001", "3.000001", "3.000001", "3.000001", "3.000001", "7.125000"),
            (row.Quantity, row.Reserved, row.InTransit, row.Unknown, row.Protected, row.Available));
        Assert.Equal((source.WarehouseId, source.LocationId, source.ItemId, source.ContainerId, source.UnitId, source.Version, 6, ""),
            (row.WarehouseId, row.LocationId, row.ItemId, row.ContainerId, row.UnitId, row.Version, row.Precision, row.Batch));
        Assert.Empty(database.ChangeTracker.Entries());
        Assert.Null(database.Database.CurrentTransaction);
        Assert.Single(await database.Entries.ToListAsync());
    }

    [Fact]
    public async Task InventoryFiltersAreExactAndAppliedBeforeThePage()
    {
        await using var seed = Open(_tenant);
        var original = await seed.Balances.AsNoTracking().SingleAsync(item => item.Id == _source);
        var created = Enumerable.Range(0, 27).Select(index => new StockBalance(_tenant, original.WarehouseId,
            Guid.NewGuid(), original.ItemId, "Batch", original.ContainerId, original.UnitId, 6)).ToArray();
        seed.Balances.AddRange(created);
        seed.Balances.Add(new(_tenant, original.WarehouseId, Guid.NewGuid(), original.ItemId, "batch", original.ContainerId, original.UnitId, 6));
        await seed.SaveChangesAsync();
        await using var database = Open(_tenant);
        var query = new InventoryQuery(Batch: "Batch", WarehouseId: original.WarehouseId,
            ItemId: original.ItemId, ContainerId: original.ContainerId);
        var first = await Reader(database).QueryAsync(Subject(), query);
        var second = await Reader(database).QueryAsync(Subject(), query with { Offset = 25 });
        Assert.Equal(25, first.Rows.Length); Assert.Equal(2, second.Rows.Length);
        Assert.Equal(created.Select(item => item.Id).Order().ToArray(), first.Rows.Concat(second.Rows).Select(item => item.Id).Order().ToArray());
        var exact = await Reader(database).QueryAsync(Subject(), query with { LocationId = created[0].LocationId });
        Assert.Equal(created[0].Id, Assert.Single(exact.Rows).Id);
        Assert.Empty((await Reader(database).QueryAsync(Subject(), query with { WarehouseId = Guid.NewGuid() })).Rows);
        Assert.Equal(2, (await Reader(database).QueryAsync(Subject(), new(Batch: ""))).Rows.Length);
    }

    [Fact]
    public async Task InventoryNeverReadsAnotherTenantOrInfersResourceOwnershipFromPostingActor()
    {
        await ReceiveAsync(4m);
        await using (var foreign = Open(_other))
        {
            foreign.Balances.Add(new(_other, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "", Guid.NewGuid(), Guid.NewGuid(), 6));
            await foreign.SaveChangesAsync();
        }
        await using var database = Open(_tenant);
        var result = await Reader(database).QueryAsync(Subject(), new());
        Assert.Equal(2, result.Rows.Length);
        Assert.Equal(InventoryReadDecision.Forbidden, (await Reader(database).QueryAsync(Subject() with { TenantId = _other }, new())).Decision);
    }

    [Theory]
    [InlineData(-1, 25)]
    [InlineData(10001, 25)]
    [InlineData(0, 1)]
    [InlineData(0, 101)]
    public async Task InventoryRejectsInvalidPageBeforeAuthority(int offset, int limit)
    {
        var authority = new InventoryAuthority { FailIfCalled = true };
        await using var database = Open(_tenant);
        Assert.Equal(InventoryReadDecision.Invalid, (await Reader(database, authority).QueryAsync(Subject(), new(offset, limit))).Decision);
    }

    [Theory]
    [InlineData(" warehouse")]
    [InlineData("batch ")]
    [InlineData("\n")]
    public async Task InventoryRejectsInvalidBatchWithoutTrimming(string batch)
    {
        await using var database = Open(_tenant);
        Assert.Equal(InventoryReadDecision.Invalid, (await Reader(database).QueryAsync(Subject(), new(Batch: batch))).Decision);
    }

    [Fact]
    public async Task InventoryRequiresEveryFieldInTheExactProjection()
    {
        foreach (var field in InventoryAccess.Fields)
        {
            await using var database = Open(_tenant);
            var authority = new InventoryAuthority { Fields = InventoryAccess.Fields.Where(item => item != field).ToHashSet() };
            var result = await Reader(database, authority).QueryAsync(Subject(), new());
            Assert.Equal(InventoryReadDecision.Forbidden, result.Decision); Assert.Empty(result.Rows);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task InventoryRejectsMissingTrustedActorOrSession(bool actor)
    {
        await using var database = Open(_tenant);
        var subject = actor ? Subject() with { ActorId = Guid.Empty } : Subject() with { SessionId = Guid.Empty };
        Assert.Equal(InventoryReadDecision.Forbidden, (await Reader(database).QueryAsync(subject, new())).Decision);
    }

    [Theory]
    [InlineData("tracked")]
    [InlineData("transaction")]
    [InlineData("ambient")]
    public async Task InventoryRequiresCleanIndependentOwner(string dirty)
    {
        await using var database = Open(_tenant);
        if (dirty == "tracked") await database.Balances.FirstAsync();
        await using var transaction = dirty == "transaction" ? await database.Database.BeginTransactionAsync() : null;
        using var ambient = dirty == "ambient" ? new TransactionScope(TransactionScopeAsyncFlowOption.Enabled) : null;
        await Assert.ThrowsAsync<InvalidOperationException>(() => Reader(database).QueryAsync(Subject(), new()));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task InventoryDoesNotReturnEmptySuccessAfterProviderFailure(bool transient)
    {
        await using var database = Open(_tenant, new QueryFault(transient, "wms_stock_balances"));
        var error = await Assert.ThrowsAsync<Wes.Wms.Features.Ledger.WmsDatabaseException>(() => Reader(database).QueryAsync(Subject(), new()));
        Assert.Equal(transient, error.IsTransient); Assert.DoesNotContain("sensitive-sentinel", error.ToString());
    }

    [Fact]
    public async Task InventoryChecksCancellationAfterAuthority()
    {
        using var cancellation = new CancellationTokenSource();
        await using var database = Open(_tenant);
        var authority = new InventoryAuthority { After = cancellation.Cancel };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Reader(database, authority).QueryAsync(Subject(), new(), cancellation.Token));
    }

    [Fact]
    public async Task InventoryUsesOneRepeatableReadViewAcrossConcurrentReservationCommit()
    {
        await ReceiveAsync(10m);
        var interleave = new BeforeProtectionRead(async () => Assert.Equal(
            Wes.Wms.Features.Reservations.ReservationDecision.Changed, (await ReserveAsync(Guid.NewGuid(), 7m)).Decision));
        await using var database = Open(_tenant, interleave);
        var old = Assert.Single((await Reader(database).QueryAsync(Subject(), new(LocationId: await SourceLocationAsync()))).Rows);
        Assert.True(interleave.Reached);
        Assert.Equal(("10.000000", "0.000000", "10.000000"), (old.Quantity, old.Protected, old.Available));
        await using var fresh = Open(_tenant);
        var current = Assert.Single((await Reader(fresh).QueryAsync(Subject(), new(LocationId: old.LocationId))).Rows);
        Assert.Equal(("7.000000", "3.000000"), (current.Protected, current.Available));
        Assert.NotEqual(old.Version, current.Version);
    }

    [Fact]
    public async Task InventoryRejectsCorruptProtectionInsteadOfClamping()
    {
        await ReceiveAsync(10m); await ReserveAsync(Guid.NewGuid(), 2m);
        await using var database = Open(_tenant);
        await database.Database.ExecuteSqlInterpolatedAsync($"UPDATE wms_stock_reservations SET RemainingQuantity = 11 WHERE TenantId = {_tenant.ToString("D")}");
        await Assert.ThrowsAsync<InvalidOperationException>(() => Reader(database).QueryAsync(Subject(), new()));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task InventoryRejectsNegativeQuantityOrUnknownStoredProtectionKind(bool negative)
    {
        await ReceiveAsync(10m); await ReserveAsync(Guid.NewGuid(), 2m);
        await using var database = Open(_tenant);
        if (negative)
            await database.Database.ExecuteSqlInterpolatedAsync($"UPDATE wms_stock_reservations SET RemainingQuantity = -1 WHERE TenantId = {_tenant.ToString("D")}");
        else
            await database.Database.ExecuteSqlInterpolatedAsync($"UPDATE wms_stock_reservations SET Kind = 8 WHERE TenantId = {_tenant.ToString("D")}");
        await Assert.ThrowsAnyAsync<ArgumentException>(() => Reader(database).QueryAsync(Subject(), new()));
    }

    [Fact]
    public async Task InventoryReadPreservesTheFullDatabaseDecimalRangeWithoutJavaScriptRounding()
    {
        await using var database = Open(_tenant);
        // Isolated storage-boundary probe only; production stock must still be explained by real owner postings.
        await database.Database.ExecuteSqlInterpolatedAsync($"UPDATE wms_stock_balances SET Quantity = {InventoryQuantity.MaximumValue} WHERE TenantId = {_tenant.ToString("D")} AND Id = {_source.ToString("D")}");
        var row = Assert.Single((await Reader(database).QueryAsync(Subject(), new(LocationId: await SourceLocationAsync()))).Rows);
        Assert.Equal("9999999999999999999999.999999", row.Quantity);
        Assert.Equal(row.Quantity, row.Available);
    }

    [Fact]
    public async Task InventoryChecksCancellationAfterSqlMaterialization()
    {
        using var cancellation = new CancellationTokenSource();
        await using var database = Open(_tenant, new CancelInventoryRead(cancellation));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Reader(database).QueryAsync(Subject(), new(), cancellation.Token));
    }

    [Fact]
    public async Task PublicInventoryScopeUsesTheSameReadBoundary()
    {
        await using var owner = new WmsInventoryScope(_connection, _tenant, new InventoryAuthority(), NullLoggerFactory.Instance);
        Assert.Equal(2, (await owner.QueryAsync(Subject(), new())).Rows.Length);
    }

    private async Task<Guid> SourceLocationAsync()
    {
        await using var database = Open(_tenant);
        return (await database.Balances.SingleAsync(item => item.Id == _source)).LocationId;
    }
    private InventorySubject Subject() => new(_tenant, _actor, Guid.NewGuid());
    private static InventoryAccess Reader(Wes.Wms.Infrastructure.Persistence.WmsDbContext database, InventoryAuthority? authority = null) =>
        new(database, authority ?? new(), NullLogger<InventoryAccess>.Instance);
    private sealed class InventoryAuthority : IInventoryAuthorization
    {
        public IReadOnlySet<string> Fields { get; init; } = InventoryAccess.Fields.ToHashSet();
        public Action? After { get; init; }
        public bool FailIfCalled { get; init; }
        public Task<IReadOnlySet<string>> ReadAllFieldsAsync(InventorySubject subject, CancellationToken cancellationToken)
        {
            Assert.False(FailIfCalled); After?.Invoke(); return Task.FromResult(Fields);
        }
    }

    private sealed class CancelInventoryRead(CancellationTokenSource cancellation) : DbCommandInterceptor
    {
        public override ValueTask<System.Data.Common.DbDataReader> ReaderExecutedAsync(System.Data.Common.DbCommand command,
            CommandExecutedEventData eventData, System.Data.Common.DbDataReader result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("wms_stock_reservations", StringComparison.Ordinal)) cancellation.Cancel();
            return ValueTask.FromResult(result);
        }
    }
}
