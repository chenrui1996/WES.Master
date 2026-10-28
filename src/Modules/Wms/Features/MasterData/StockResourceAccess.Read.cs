using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Wes.Wms.Infrastructure.Persistence;

namespace Wes.Wms.Features.MasterData;

internal sealed partial class StockResourceAccess
{
    internal Task<StockResourceReadResult<StockUnitDefinition>> ReadUnitAsync(MasterDataSubject subject, Guid id, CancellationToken ct) =>
        ReadAsync(subject, id, "wms.units.read", UnitRead, async () =>
        {
            var row = await database.StockUnits.AsNoTracking().Where(row => row.Id == id)
                .Select(row => new { row.ActorId, Value = new StockUnitDefinition(row.Id, row.Code, row.Name, row.Countable, row.Precision, row.Version, row.CreatedAt) })
                .SingleOrDefaultAsync(ct);
            return (row?.Value, row?.ActorId ?? Guid.Empty);
        }, ct);

    internal Task<StockResourceReadResult<StockItemDefinition>> ReadItemAsync(MasterDataSubject subject, Guid id, CancellationToken ct) =>
        ReadAsync(subject, id, "wms.items.read", ItemRead, async () =>
        {
            var row = await database.StockItems.AsNoTracking().Where(row => row.Id == id)
                .Select(row => new { row.ActorId, Value = new StockItemDefinition(row.Id, row.Code, row.Name, row.BaseUnitId, row.BatchRequired, row.Version, row.CreatedAt) })
                .SingleOrDefaultAsync(ct);
            return (row?.Value, row?.ActorId ?? Guid.Empty);
        }, ct);

    internal Task<StockResourceReadResult<StockContainerDefinition>> ReadContainerAsync(MasterDataSubject subject, Guid id, CancellationToken ct) =>
        ReadAsync(subject, id, "wms.containers.read", ContainerRead, async () =>
        {
            var row = await database.StockContainers.AsNoTracking().Where(row => row.Id == id)
                .Select(row => new { row.ActorId, Value = new StockContainerDefinition(row.Id, row.Code, row.Name, row.WarehouseId, row.Version, row.CreatedAt) })
                .SingleOrDefaultAsync(ct);
            return (row?.Value, row?.ActorId ?? Guid.Empty);
        }, ct);

    private async Task<StockResourceReadResult<T>> ReadAsync<T>(MasterDataSubject subject, Guid id, string permission,
        string[] fields, Func<Task<(T? Value, Guid Creator)>> fetch, CancellationToken ct) where T : class
    {
        ArgumentNullException.ThrowIfNull(subject); ct.ThrowIfCancellationRequested();
        if (database.ChangeTracker.Entries().Any() || database.Database.CurrentTransaction is not null
            || System.Transactions.Transaction.Current is not null)
            throw new InvalidOperationException("Resource reads require an independent clean owner.");
        if (subject.TenantId != database.TenantId || subject.ActorId == Guid.Empty || subject.SessionId == Guid.Empty)
            return RejectedRead<T>(MasterDataReadDecision.Forbidden);
        if (id == Guid.Empty) return RejectedRead<T>(MasterDataReadDecision.Invalid);
        try
        {
            var row = await fetch();
            if (row.Value is null) return RejectedRead<T>(MasterDataReadDecision.NotFound);
            if (!await FieldsAsync(subject, permission, row.Creator, fields, ct)
                || !await FieldsAsync(subject, permission, row.Creator, fields, ct))
                return RejectedRead<T>(MasterDataReadDecision.Forbidden);
            return new(MasterDataReadDecision.Success, row.Value);
        }
        catch (DbException error)
        {
            ct.ThrowIfCancellationRequested(); logger.LogError("WMS resource read failed {Code}", "DatabaseUnavailable");
            throw WmsDatabaseFailures.Safe(error);
        }
    }

    private StockResourceReadResult<T> RejectedRead<T>(MasterDataReadDecision decision) where T : class
    {
        logger.LogWarning("WMS resource read rejected {Code}", decision.ToString()); return new(decision);
    }
}
