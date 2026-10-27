using System.Transactions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Wes.Wms;
using Wes.Wms.Domain;
using Wes.Wms.Features.Inventory;
using Wes.Wms.Features.Ledger;
using Wes.Wms.Infrastructure.Persistence;
using Xunit;

namespace Wes.Persistence.Tests;

public sealed partial class WmsLedgerTests
{
    [Fact]
    public async Task LedgerReadsExactSignedHistoryAndPostingProvenanceWithoutCurrentBookSubstitution()
    {
        await ReceiveAsync(10.125001m);
        await ExecuteAsync(await CommandAsync(StockPostingKind.Shipment, _source, null, 3.001001m));
        await ExecuteAsync(await CommandAsync(StockPostingKind.Transfer, _source, _target, 1.000001m));
        await using var database = Open(_tenant);
        var result = await LedgerReader(database).QueryAsync(Subject(), new());
        Assert.Equal(InventoryReadDecision.Success, result.Decision);
        Assert.Equal(4, result.Rows.Length);
        Assert.Equal(new[] { "-1.000001", "-3.001001", "1.000001", "10.125001" },
            result.Rows.Select(row => row.Quantity).Order(StringComparer.Ordinal).ToArray());
        var receipt = Assert.Single(result.Rows, row => row.Kind == StockLedgerKind.Receipt);
        Assert.Equal(("0.000000", "10.125001"), (receipt.Before, receipt.After));
        Assert.All(result.Rows, row =>
        {
            Assert.Equal((_document, _line, 6, ""), (row.SourceDocumentId, row.SourceLineId, row.Precision, row.Batch));
            Assert.Equal(_clock.GetUtcNow(), row.OccurredAt);
        });
        Assert.Empty(database.ChangeTracker.Entries());
        Assert.Equal(3, await database.Postings.CountAsync());
        Assert.Equal(4, await database.Entries.CountAsync());
    }

    [Fact]
    public async Task LedgerReadsApplyExactFiltersBeforeStableSqlPaging()
    {
        for (var index = 0; index < 27; index++) await ReceiveAsync(0.000001m);
        await using var database = Open(_tenant);
        var balance = await database.Balances.AsNoTracking().SingleAsync(item => item.Id == _source);
        var entry = await database.Entries.AsNoTracking().FirstAsync();
        var query = new StockLedgerQuery(new(WarehouseId: balance.WarehouseId, LocationId: balance.LocationId,
            ItemId: balance.ItemId, ContainerId: balance.ContainerId, Batch: ""), SourceDocumentId: _document,
            Kind: StockLedgerKind.Receipt, From: _clock.GetUtcNow(), Before: _clock.GetUtcNow().AddMicroseconds(1));
        var access = LedgerReader(database);
        var first = await access.QueryAsync(Subject(), query);
        var last = await access.QueryAsync(Subject(), query with { Stock = query.Stock with { Offset = 25 } });
        Assert.Equal(25, first.Rows.Length); Assert.Equal(2, last.Rows.Length);
        var ids = first.Rows.Concat(last.Rows).Select(row => row.Id).ToArray();
        Assert.Equal(27, ids.Distinct().Count());
        Assert.Equal(ids.OrderBy(id => id.ToString("D"), StringComparer.Ordinal), ids);
        Assert.Empty((await access.QueryAsync(Subject(), query with { Before = _clock.GetUtcNow(), From = null })).Rows);
        Assert.Empty((await access.QueryAsync(Subject(), query with { Stock = query.Stock with { ItemId = Guid.NewGuid() } })).Rows);
        Assert.Single((await access.QueryAsync(Subject(), query with { CorrelationId = entry.CorrelationId })).Rows);
        Assert.Empty((await access.QueryAsync(Subject(), query with { SourceDocumentId = Guid.NewGuid() })).Rows);
        Assert.Empty((await access.QueryAsync(Subject(), query with { Kind = StockLedgerKind.Shipment })).Rows);
    }

    [Fact]
    public async Task LedgerReadsRequireEachExactFieldAndDoNotAcceptMissingAuthority()
    {
        await using var database = Open(_tenant);
        foreach (var missing in StockLedgerAccess.Fields)
        {
            var result = await LedgerReader(database, new() { Fields = StockLedgerAccess.Fields.Where(field => field != missing).ToHashSet() })
                .QueryAsync(Subject(), new());
            Assert.Equal(InventoryReadDecision.Forbidden, result.Decision);
            Assert.Empty(result.Rows);
        }
        await Assert.ThrowsAsync<InvalidOperationException>(() => LedgerReader(database, new() { Fields = null! }).QueryAsync(Subject(), new()));
    }

    [Theory]
    [InlineData("tenant")]
    [InlineData("actor")]
    [InlineData("session")]
    public async Task LedgerReadsRejectInvalidSubjectBeforeAuthority(string invalid)
    {
        await using var database = Open(_tenant);
        var subject = Subject();
        subject = invalid switch
        {
            "tenant" => subject with { TenantId = _other },
            "actor" => subject with { ActorId = Guid.Empty },
            _ => subject with { SessionId = Guid.Empty }
        };
        Assert.Equal(InventoryReadDecision.Forbidden,
            (await LedgerReader(database, new() { FailIfCalled = true }).QueryAsync(subject, new())).Decision);
    }

    [Theory]
    [InlineData("offset")]
    [InlineData("limit")]
    [InlineData("uuid")]
    [InlineData("batch")]
    [InlineData("document")]
    [InlineData("correlation")]
    [InlineData("kind")]
    [InlineData("reversed")]
    [InlineData("offset-time")]
    [InlineData("precision-time")]
    [InlineData("early-time")]
    public async Task LedgerReadsRejectInvalidQueryBeforeAuthority(string invalid)
    {
        var query = invalid switch
        {
            "offset" => new StockLedgerQuery(new(Offset: 10001)),
            "limit" => new(new(Limit: 1)),
            "uuid" => new(new(LocationId: Guid.Empty)),
            "batch" => new(new(Batch: " bad")),
            "document" => new(new(), SourceDocumentId: Guid.Empty),
            "correlation" => new(new(), CorrelationId: Guid.Empty),
            "kind" => new(new(), Kind: (StockLedgerKind)99),
            "reversed" => new(new(), From: _clock.GetUtcNow(), Before: _clock.GetUtcNow()),
            "offset-time" => new(new(), From: _clock.GetUtcNow().ToOffset(TimeSpan.FromHours(1))),
            "precision-time" => new(new(), From: _clock.GetUtcNow().AddTicks(1)),
            _ => new(new(), From: new DateTimeOffset(999, 1, 1, 0, 0, 0, TimeSpan.Zero))
        };
        await using var database = Open(_tenant);
        Assert.Equal(InventoryReadDecision.Invalid, (await LedgerReader(database, new() { FailIfCalled = true }).QueryAsync(Subject(), query)).Decision);
    }

    [Theory]
    [InlineData("tracked")]
    [InlineData("transaction")]
    [InlineData("ambient")]
    public async Task LedgerReadsRequireCleanIndependentOwner(string dirty)
    {
        await using var database = Open(_tenant);
        if (dirty == "tracked") await database.Balances.FirstAsync();
        await using var transaction = dirty == "transaction" ? await database.Database.BeginTransactionAsync() : null;
        using var ambient = dirty == "ambient" ? new TransactionScope(TransactionScopeAsyncFlowOption.Enabled) : null;
        await Assert.ThrowsAsync<InvalidOperationException>(() => LedgerReader(database).QueryAsync(Subject(), new()));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LedgerReadsHonorCancellationAfterAuthorityOrSql(bool sql)
    {
        await ReceiveAsync(1m);
        using var cancellation = new CancellationTokenSource();
        await using var database = Open(_tenant, sql ? new CancelLedgerRead(cancellation) : null);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => LedgerReader(database, new() { After = sql ? null : cancellation.Cancel })
            .QueryAsync(Subject(), new(), cancellation.Token));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LedgerReadsDoNotTurnProviderFailureIntoEmptySuccess(bool transient)
    {
        await using var database = Open(_tenant, new QueryFault(transient, "wms_stock_entries"));
        var error = await Assert.ThrowsAsync<WmsDatabaseException>(() => LedgerReader(database).QueryAsync(Subject(), new()));
        Assert.Equal(transient, error.IsTransient);
        Assert.DoesNotContain("sensitive-sentinel", error.ToString());
    }

    [Theory]
    [InlineData("quantity")]
    [InlineData("before")]
    [InlineData("kind")]
    [InlineData("correlation")]
    [InlineData("time")]
    [InlineData("precision")]
    [InlineData("source")]
    public async Task LedgerReadsRejectDamagedStoredFactsInsteadOfReconstructingSuccess(string damaged)
    {
        await ReceiveAsync(1.000001m);
        await using (var setup = Open(_tenant))
        {
            var sql = damaged switch
            {
                "quantity" => "UPDATE wms_stock_entries SET Quantity = 0 WHERE TenantId = {0}",
                "before" => "UPDATE wms_stock_entries SET `Before` = 1 WHERE TenantId = {0}",
                "kind" => "UPDATE wms_stock_postings SET Kind = 99 WHERE TenantId = {0}",
                "correlation" => "UPDATE wms_stock_entries SET CorrelationId = '00000000-0000-0000-0000-000000000000' WHERE TenantId = {0}",
                "time" => "UPDATE wms_stock_entries SET OccurredAt = '2026-01-01' WHERE TenantId = {0}",
                "precision" => "UPDATE wms_stock_balances SET `Precision` = 0 WHERE TenantId = {0}",
                _ => "UPDATE wms_stock_postings SET TargetBalanceId = NULL WHERE TenantId = {0}"
            };
            await setup.Database.ExecuteSqlRawAsync(sql, _tenant.ToString("D"));
        }
        await using var database = Open(_tenant);
        await Assert.ThrowsAsync<InvalidOperationException>(() => LedgerReader(database).QueryAsync(Subject(), new()));
    }

    [Fact]
    public async Task PublicLedgerScopeRejectsForeignTenantAndReadsWithTheSameIndependentBoundary()
    {
        await ReceiveAsync(1m);
        await using var owner = new WmsStockLedgerScope(_connection, _tenant, new LedgerAuthority(), NullLoggerFactory.Instance);
        Assert.Single((await owner.QueryAsync(Subject(), new())).Rows);
        Assert.Equal(InventoryReadDecision.Forbidden, (await owner.QueryAsync(Subject() with { TenantId = _other }, new())).Decision);
        await using var foreign = new WmsStockLedgerScope(_connection, _other, new LedgerAuthority(), NullLoggerFactory.Instance);
        Assert.Empty((await foreign.QueryAsync(Subject() with { TenantId = _other }, new())).Rows);
    }

    private static StockLedgerAccess LedgerReader(WmsDbContext database, LedgerAuthority? authority = null) =>
        new(database, authority ?? new(), NullLogger<StockLedgerAccess>.Instance);
    private sealed class LedgerAuthority : IStockLedgerAuthorization
    {
        public IReadOnlySet<string> Fields { get; init; } = StockLedgerAccess.Fields.ToHashSet();
        public Action? After { get; init; }
        public bool FailIfCalled { get; init; }
        public Task<IReadOnlySet<string>> ReadAllFieldsAsync(InventorySubject subject, CancellationToken cancellationToken)
        {
            Assert.False(FailIfCalled);
            After?.Invoke();
            return Task.FromResult(Fields);
        }
    }
    private sealed class CancelLedgerRead(CancellationTokenSource cancellation) : DbCommandInterceptor
    {
        public override ValueTask<System.Data.Common.DbDataReader> ReaderExecutedAsync(System.Data.Common.DbCommand command,
            CommandExecutedEventData eventData, System.Data.Common.DbDataReader result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("wms_stock_entries", StringComparison.Ordinal)) cancellation.Cancel();
            return ValueTask.FromResult(result);
        }
    }
}
