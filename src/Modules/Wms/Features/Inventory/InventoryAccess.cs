using System.Collections.Immutable;
using System.Data.Common;
using System.Globalization;
using System.Transactions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Wes.Wms.Domain;
using Wes.Wms.Infrastructure.Persistence;

namespace Wes.Wms.Features.Inventory;

/// <summary>Trusted current identity; inventory dimensions do not derive ownership from a posting actor.</summary>
public sealed record InventorySubject(Guid TenantId, Guid ActorId, Guid SessionId);
/// <summary>Live tenant-wide field authority; Own/department grants cannot stand in for warehouse resource authorization.</summary>
public interface IInventoryAuthorization
{
    /// <summary>Revalidates the live session and returns only current All-scope wms.inventory.read fields, without caching.</summary>
    Task<IReadOnlySet<string>> ReadAllFieldsAsync(InventorySubject subject, CancellationToken cancellationToken);
}
/// <summary>Realtime bounds and exact dimension filters; null batch is unfiltered and empty batch means no batch.</summary>
public sealed record InventoryQuery(int Offset = 0, int Limit = 25, Guid? WarehouseId = null, Guid? LocationId = null,
    Guid? ItemId = null, Guid? ContainerId = null, string? Batch = null);
/// <summary>Only Success carries a page; no rejected query is represented as an authorized empty result.</summary>
public enum InventoryReadDecision
{
    /// <summary>Current full projection authority and one consistent owner read succeeded.</summary>
    Success,
    /// <summary>Trusted subject or current All-scope field authority is insufficient.</summary>
    Forbidden,
    /// <summary>Paging or exact filters are invalid.</summary>
    Invalid
}
/// <summary>
/// Book dimensions and overlapping protection totals in the base unit; decimal quantities are lossless fixed-six strings.
/// Available is an observation, not an allocation grant or physical-location assertion.
/// </summary>
public sealed record InventoryView(Guid Id, Guid WarehouseId, Guid LocationId, Guid ItemId, string Batch,
    Guid ContainerId, Guid UnitId, int Precision, string Quantity, string Reserved, string InTransit,
    string Unknown, string Protected, string Available, string Version);
/// <summary>A bounded realtime page, not a durable reconciliation snapshot or an across-page total.</summary>
public sealed record InventoryPage(InventoryReadDecision Decision, ImmutableArray<InventoryView> Rows, int Offset, int Limit);

internal sealed class InventoryAccess(WmsDbContext database, IInventoryAuthorization authorization, ILogger<InventoryAccess> logger)
{
    internal static readonly string[] Fields = ["Id", "WarehouseId", "LocationId", "ItemId", "Batch", "ContainerId",
        "UnitId", "Precision", "Quantity", "Reserved", "InTransit", "Unknown", "Protected", "Available", "Version"];

    internal async Task<InventoryPage> QueryAsync(InventorySubject subject, InventoryQuery query, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(subject);
        ArgumentNullException.ThrowIfNull(query);
        ct.ThrowIfCancellationRequested();
        if (database.ChangeTracker.Entries().Any() || database.Database.CurrentTransaction is not null || Transaction.Current is not null)
            throw new InvalidOperationException("Inventory queries require a clean independent WMS owner.");
        if (subject.TenantId != database.TenantId || subject.ActorId == Guid.Empty || subject.SessionId == Guid.Empty)
            return Rejected(InventoryReadDecision.Forbidden);
        if (query.Offset is < 0 or > 10000 || query.Limit is not (25 or 50 or 100)
            || new[] { query.WarehouseId, query.LocationId, query.ItemId, query.ContainerId }.Any(id => id == Guid.Empty)
            || query.Batch is { } batch && (batch.Length > 128 || batch != batch.Trim() || batch.Any(char.IsControl)))
            return Rejected(InventoryReadDecision.Invalid);
        try
        {
            var fields = await authorization.ReadAllFieldsAsync(subject, ct);
            ct.ThrowIfCancellationRequested();
            if (fields is null) throw new InvalidOperationException("Inventory authority returned no decision.");
            var exact = fields.ToHashSet(StringComparer.Ordinal);
            if (!Fields.All(exact.Contains)) return Rejected(InventoryReadDecision.Forbidden);
            var balances = database.Balances.AsNoTracking().AsQueryable();
            if (query.WarehouseId.HasValue) balances = balances.Where(item => item.WarehouseId == query.WarehouseId);
            if (query.LocationId.HasValue) balances = balances.Where(item => item.LocationId == query.LocationId);
            if (query.ItemId.HasValue) balances = balances.Where(item => item.ItemId == query.ItemId);
            if (query.ContainerId.HasValue) balances = balances.Where(item => item.ContainerId == query.ContainerId);
            if (query.Batch is not null) balances = balances.Where(item => item.Batch == query.Batch);
            var page = balances.OrderBy(item => item.Id).Skip(query.Offset).Take(query.Limit);
            // Reservation commits also change the balance fence. A shared MVCC view prevents mixing old book values
            // with newly committed protections; this is not a write lock or a persistent across-request snapshot.
            await using var transaction = await database.Database.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, ct);
            var books = await page.ToListAsync(ct);
            var portions = await database.Reservations.AsNoTracking()
                .Where(item => item.RemainingQuantity != 0m && page.Any(balance => balance.Id == item.BalanceId))
                .Select(item => new { item.BalanceId, item.ExecutionUnitId, item.RemainingQuantity, item.Kind }).ToListAsync(ct);
            ct.ThrowIfCancellationRequested();
            var grouped = portions.ToLookup(item => item.BalanceId);
            var rows = books.Select(book =>
            {
                var totals = StockAvailability.Calculate(InventoryQuantity.Create(book.Quantity),
                    grouped[book.Id].Select(item => new StockProtection(item.ExecutionUnitId,
                        InventoryQuantity.Create(item.RemainingQuantity), item.Kind)));
                return new InventoryView(book.Id, book.WarehouseId, book.LocationId, book.ItemId, book.Batch,
                    book.ContainerId, book.UnitId, book.Precision, Exact(totals.Quantity), Exact(totals.Reserved),
                    Exact(totals.InTransit), Exact(totals.Unknown), Exact(totals.Protected), Exact(totals.Available), book.Version);
            }).ToImmutableArray();
            await transaction.CommitAsync(ct);
            ct.ThrowIfCancellationRequested();
            return new(InventoryReadDecision.Success, rows, query.Offset, query.Limit);
        }
        catch (DbException error)
        {
            ct.ThrowIfCancellationRequested();
            logger.LogError("WMS inventory query failed {Code}", "DatabaseUnavailable");
            throw WmsDatabaseFailures.Safe(error);
        }
        catch (InvalidOperationException)
        {
            logger.LogError("WMS inventory query rejected inconsistent state {Code}", "InventoryReadInvalid");
            throw;
        }
        catch (ArgumentException)
        {
            logger.LogError("WMS inventory query rejected invalid stored protection {Code}", "InventoryReadInvalid");
            throw;
        }

        InventoryPage Rejected(InventoryReadDecision decision)
        {
            logger.LogWarning("WMS inventory query rejected {Code}", decision.ToString());
            return new(decision, [], query.Offset, query.Limit);
        }
    }

    private static string Exact(InventoryQuantity value) => value.Value.ToString("F6", CultureInfo.InvariantCulture);
}
