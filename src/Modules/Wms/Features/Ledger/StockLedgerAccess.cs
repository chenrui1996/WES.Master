using System.Collections.Immutable;
using System.Data.Common;
using System.Globalization;
using System.Transactions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Wes.Wms.Domain;
using Wes.Wms.Features.Inventory;
using Wes.Wms.Infrastructure.Persistence;

namespace Wes.Wms.Features.Ledger;

/// <summary>Independent live tenant-wide ledger authority; posting actors do not own stock history.</summary>
public interface IStockLedgerAuthorization
{
    /// <summary>Revalidates the live session and returns only current All-scope wms.ledger.read fields.</summary>
    Task<IReadOnlySet<string>> ReadAllFieldsAsync(InventorySubject subject, CancellationToken cancellationToken);
}
/// <summary>Public read contract categories, independent of the private posting command/entity.</summary>
public enum StockLedgerKind
{
    /// <summary>Positive receipt entry.</summary>
    Receipt,
    /// <summary>Negative shipment entry.</summary>
    Shipment,
    /// <summary>One signed leg of a transfer.</summary>
    Transfer,
    /// <summary>Authorized positive physical-count discrepancy.</summary>
    AdjustmentIn,
    /// <summary>Authorized negative physical-count discrepancy.</summary>
    AdjustmentOut
}
/// <summary>Exact immutable dimension/provenance filters and UTC microsecond half-open occurrence bounds.</summary>
public sealed record StockLedgerQuery(InventoryQuery Stock, Guid? SourceDocumentId = null, Guid? CorrelationId = null,
    StockLedgerKind? Kind = null, DateTimeOffset? From = null, DateTimeOffset? Before = null)
{
    /// <summary>A bounded, explicitly unfiltered first page.</summary>
    public StockLedgerQuery() : this(new InventoryQuery()) { }
}
/// <summary>Immutable entry and source identities, with signed delta and nonnegative book amounts as exact six-place strings.</summary>
public sealed record StockLedgerView(Guid Id, Guid PostingId, Guid BalanceId, Guid WarehouseId, Guid LocationId,
    Guid ItemId, string Batch, Guid ContainerId, Guid UnitId, int Precision, StockLedgerKind Kind, string Quantity,
    string Before, string After, Guid SourceDocumentId, Guid SourceLineId, Guid ExecutionUnitId, Guid CorrelationId,
    DateTimeOffset OccurredAt);
/// <summary>Realtime ascending occurrence/identity page; not an across-page reconciliation snapshot.</summary>
public sealed record StockLedgerPage(InventoryReadDecision Decision, ImmutableArray<StockLedgerView> Rows, int Offset, int Limit);

internal sealed class StockLedgerAccess(WmsDbContext database, IStockLedgerAuthorization authorization, ILogger<StockLedgerAccess> logger)
{
    internal static readonly string[] Fields = ["Id", "PostingId", "BalanceId", "WarehouseId", "LocationId", "ItemId",
        "Batch", "ContainerId", "UnitId", "Precision", "Kind", "Quantity", "Before", "After", "SourceDocumentId",
        "SourceLineId", "ExecutionUnitId", "CorrelationId", "OccurredAt"];

    internal async Task<StockLedgerPage> QueryAsync(InventorySubject subject, StockLedgerQuery query, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(subject);
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(query.Stock);
        ct.ThrowIfCancellationRequested();
        if (database.ChangeTracker.Entries().Any() || database.Database.CurrentTransaction is not null || Transaction.Current is not null)
            throw new InvalidOperationException("Ledger queries require a clean independent WMS owner.");
        var stock = query.Stock;
        if (subject.TenantId != database.TenantId || subject.ActorId == Guid.Empty || subject.SessionId == Guid.Empty)
            return Rejected(InventoryReadDecision.Forbidden);
        if (stock.Offset is < 0 or > 10000 || stock.Limit is not (25 or 50 or 100)
            || new[] { stock.WarehouseId, stock.LocationId, stock.ItemId, stock.ContainerId, query.SourceDocumentId, query.CorrelationId }
                .Any(id => id == Guid.Empty)
            || stock.Batch is { } batch && (batch.Length > 128 || batch != batch.Trim() || batch.Any(char.IsControl))
            || query.Kind is { } kind && !Enum.IsDefined(kind)
            || !ValidTime(query.From) || !ValidTime(query.Before) || query.From >= query.Before)
            return Rejected(InventoryReadDecision.Invalid);
        try
        {
            var fields = await authorization.ReadAllFieldsAsync(subject, ct);
            ct.ThrowIfCancellationRequested();
            if (fields is null) throw new InvalidOperationException("Ledger authority returned no decision.");
            var exact = fields.ToHashSet(StringComparer.Ordinal);
            if (!Fields.All(exact.Contains)) return Rejected(InventoryReadDecision.Forbidden);
            var balances = database.Balances.AsNoTracking().AsQueryable();
            if (stock.WarehouseId.HasValue) balances = balances.Where(item => item.WarehouseId == stock.WarehouseId);
            if (stock.LocationId.HasValue) balances = balances.Where(item => item.LocationId == stock.LocationId);
            if (stock.ItemId.HasValue) balances = balances.Where(item => item.ItemId == stock.ItemId);
            if (stock.ContainerId.HasValue) balances = balances.Where(item => item.ContainerId == stock.ContainerId);
            if (stock.Batch is not null) balances = balances.Where(item => item.Batch == stock.Batch);
            var postings = database.Postings.AsNoTracking().AsQueryable();
            if (query.SourceDocumentId.HasValue) postings = postings.Where(item => item.SourceDocumentId == query.SourceDocumentId);
            if (query.CorrelationId.HasValue) postings = postings.Where(item => item.CorrelationId == query.CorrelationId);
            if (query.Kind.HasValue)
            {
                var postingKind = query.Kind.Value switch
                {
                    StockLedgerKind.Receipt => StockPostingKind.Receipt,
                    StockLedgerKind.Shipment => StockPostingKind.Shipment,
                    StockLedgerKind.Transfer => StockPostingKind.Transfer,
                    StockLedgerKind.AdjustmentIn => StockPostingKind.AdjustmentIn,
                    StockLedgerKind.AdjustmentOut => StockPostingKind.AdjustmentOut,
                    _ => throw new InvalidOperationException("Unknown ledger query kind.")
                };
                postings = postings.Where(item => item.Kind == postingKind);
            }
            var entries = database.Entries.AsNoTracking().AsQueryable();
            if (query.From.HasValue) entries = entries.Where(item => item.OccurredAt >= query.From);
            if (query.Before.HasValue) entries = entries.Where(item => item.OccurredAt < query.Before);
            var facts = await (from entry in entries
                               join posting in postings on new { entry.TenantId, Id = entry.PostingId } equals new { posting.TenantId, posting.Id }
                               join balance in balances on new { entry.TenantId, Id = entry.BalanceId } equals new { balance.TenantId, balance.Id }
                               orderby entry.OccurredAt, entry.Id
                               select new { Entry = entry, Posting = posting, Balance = balance })
                .Skip(stock.Offset).Take(stock.Limit).ToListAsync(ct);
            ct.ThrowIfCancellationRequested();
            var rows = facts.Select(fact => Project(fact.Entry, fact.Posting, fact.Balance)).ToImmutableArray();
            ct.ThrowIfCancellationRequested();
            return new(InventoryReadDecision.Success, rows, stock.Offset, stock.Limit);
        }
        catch (DbException error)
        {
            ct.ThrowIfCancellationRequested();
            logger.LogError("WMS ledger query failed {Code}", "DatabaseUnavailable");
            throw WmsDatabaseFailures.Safe(error);
        }
        catch (InvalidOperationException)
        {
            logger.LogError("WMS ledger query rejected inconsistent state {Code}", "LedgerReadInvalid");
            throw;
        }

        StockLedgerPage Rejected(InventoryReadDecision decision)
        {
            logger.LogWarning("WMS ledger query rejected {Code}", decision.ToString());
            return new(decision, [], stock.Offset, stock.Limit);
        }
    }

    private static bool ValidTime(DateTimeOffset? value) =>
        value is null || value.Value.Offset == TimeSpan.Zero && value.Value.Year >= 1000 && value.Value.Ticks % 10 == 0;

    private static StockLedgerView Project(StockEntry entry, StockPosting posting, StockBalance balance)
    {
        var directionValid = posting.Kind switch
        {
            StockPostingKind.Receipt or StockPostingKind.AdjustmentIn => entry.Quantity > 0m && posting.SourceBalanceId is null && posting.TargetBalanceId == entry.BalanceId,
            StockPostingKind.Shipment or StockPostingKind.AdjustmentOut => entry.Quantity < 0m && posting.TargetBalanceId is null && posting.SourceBalanceId == entry.BalanceId,
            StockPostingKind.Transfer => posting.SourceBalanceId.HasValue && posting.TargetBalanceId.HasValue
                && posting.SourceBalanceId != posting.TargetBalanceId
                && (entry.Quantity < 0m ? posting.SourceBalanceId : posting.TargetBalanceId) == entry.BalanceId,
            _ => false
        };
        if (!directionValid || balance.Precision is < 0 or > 6 || entry.Quantity == 0m
            || Math.Abs(entry.Quantity) != posting.Quantity || posting.Quantity > StockMovementQuantity.MaximumValue
            || entry.Before < 0m || entry.After < 0m || entry.After - entry.Before != entry.Quantity
            || decimal.Round(entry.Quantity, balance.Precision) != entry.Quantity
            || decimal.Round(entry.Before, balance.Precision) != entry.Before || decimal.Round(entry.After, balance.Precision) != entry.After
            || entry.CorrelationId == Guid.Empty || entry.CorrelationId != posting.CorrelationId || entry.OccurredAt != posting.OccurredAt
            || !ValidTime(entry.OccurredAt) || posting.SourceDocumentId == Guid.Empty || posting.SourceLineId == Guid.Empty
            || posting.ExecutionUnitId == Guid.Empty)
            throw new InvalidOperationException("Stored ledger fact is inconsistent.");
        var kind = posting.Kind switch
        {
            StockPostingKind.Receipt => StockLedgerKind.Receipt,
            StockPostingKind.Shipment => StockLedgerKind.Shipment,
            StockPostingKind.Transfer => StockLedgerKind.Transfer,
            StockPostingKind.AdjustmentIn => StockLedgerKind.AdjustmentIn,
            StockPostingKind.AdjustmentOut => StockLedgerKind.AdjustmentOut,
            _ => throw new InvalidOperationException("Stored ledger kind is inconsistent.")
        };
        return new(entry.Id, posting.Id, balance.Id, balance.WarehouseId, balance.LocationId, balance.ItemId, balance.Batch,
            balance.ContainerId, balance.UnitId, balance.Precision, kind, Exact(entry.Quantity), Exact(entry.Before),
            Exact(entry.After), posting.SourceDocumentId, posting.SourceLineId, posting.ExecutionUnitId, entry.CorrelationId, entry.OccurredAt);
    }

    private static string Exact(decimal value) => value.ToString("F6", CultureInfo.InvariantCulture);
}
