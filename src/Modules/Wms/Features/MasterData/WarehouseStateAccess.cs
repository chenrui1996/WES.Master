using System.Data;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using MySql.Data.MySqlClient;
using Wes.Wms.Domain;
using Wes.Wms.Features.Ledger;
using Wes.Wms.Infrastructure.Persistence;

namespace Wes.Wms.Features.MasterData;

internal sealed class WarehouseStateAccess(WmsDbContext database, IMasterDataAuthorization authorization,
    TimeProvider clock, ILogger<WarehouseStateAccess> logger)
{
    private static readonly string[] ReadFields = ["Id", "Code", "Name", "Enabled", "Version", "CreatedAt"];

    internal async Task<WarehouseStateResult> ChangeAsync(MasterDataSubject subject, Guid id, WarehouseStateCommand request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(subject); ArgumentNullException.ThrowIfNull(request);
        ct.ThrowIfCancellationRequested();
        if (database.ChangeTracker.Entries().Any() || database.Database.CurrentTransaction is not null
            || System.Transactions.Transaction.Current is not null)
            throw new InvalidOperationException("Warehouse state changes require a fresh independent WMS owner.");
        if (subject.TenantId != database.TenantId || subject.ActorId == Guid.Empty || subject.SessionId == Guid.Empty)
            return Reject(WarehouseStateDecision.Forbidden);
        if (id == Guid.Empty || request.OperationId == Guid.Empty || !WarehouseStateChange.ValidReason(request.Reason)
            || !Guid.TryParseExact(request.ExpectedVersion, "D", out var expected) || expected == Guid.Empty)
            return Reject(WarehouseStateDecision.Invalid);
        try
        {
            var creator = await database.MasterDataChanges.AsNoTracking().Where(item => item.WarehouseId == id && item.LocationId == null)
                .Select(item => (Guid?)item.ActorId).SingleOrDefaultAsync(ct);
            if (creator is null) return Reject(WarehouseStateDecision.NotFound);
            if (!await AuthorizedAsync(subject, creator.Value, ct)) return Reject(WarehouseStateDecision.Forbidden);
            await using var transaction = await database.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
            try
            {
                // Serialize original-operation recovery and state transitions on the same parent; keep activity reads protected until commit.
                await using var fence = database.Database.GetDbConnection().CreateCommand();
                fence.Transaction = transaction.GetDbTransaction();
                fence.CommandText = "SELECT Version FROM wms_warehouses WHERE TenantId = @tenant AND Id = @id FOR UPDATE";
                var tenant = fence.CreateParameter(); tenant.ParameterName = "@tenant";
                tenant.Value = database.TenantId.ToString("D"); fence.Parameters.Add(tenant);
                var warehouse = fence.CreateParameter(); warehouse.ParameterName = "@id";
                warehouse.Value = id.ToString("D"); fence.Parameters.Add(warehouse);
                if (await fence.ExecuteScalarAsync(ct) is not string version) return Reject(WarehouseStateDecision.NotFound);
                var existing = await database.WarehouseStateChanges.AsNoTracking()
                    .SingleOrDefaultAsync(item => item.OperationId == request.OperationId, ct);
                if (existing is not null)
                {
                    if (existing.WarehouseId != id || existing.ActorId != subject.ActorId
                        || existing.BeforeVersion != request.ExpectedVersion || existing.Enabled != request.Enabled || existing.Reason != request.Reason)
                        return Reject(WarehouseStateDecision.Conflict);
                    await transaction.CommitAsync(ct);
                    if (!await AuthorizedAsync(subject, creator.Value, ct)) return Reject(WarehouseStateDecision.Forbidden);
                    return new(WarehouseStateDecision.Replayed, id, existing.AfterVersion);
                }
                if (version != request.ExpectedVersion) return Reject(WarehouseStateDecision.Conflict);
                var row = await database.Warehouses.SingleAsync(item => item.Id == id, ct);
                if (await database.Counts.AnyAsync(item => item.WarehouseId == id
                    && (item.State == StockCountState.Counting || item.State == StockCountState.Ready), ct))
                    return Reject(WarehouseStateDecision.Blocked);
                if (row.Enabled == request.Enabled) return Reject(WarehouseStateDecision.Conflict);
                if (!request.Enabled && await HasActivityAsync(id, ct)) return Reject(WarehouseStateDecision.Blocked);
                var beforeEnabled = row.Enabled;
                row.ChangeState(request.Enabled);
                database.WarehouseStateChanges.Add(new(request.OperationId, subject.ActorId, Guid.NewGuid(), row,
                    request.ExpectedVersion, beforeEnabled, request.Reason, clock.GetUtcNow()));
                await database.SaveChangesAsync(ct);
                if (!await AuthorizedAsync(subject, creator.Value, ct))
                {
                    await transaction.RollbackAsync(ct); database.ChangeTracker.Clear();
                    return Reject(WarehouseStateDecision.Forbidden);
                }
                ct.ThrowIfCancellationRequested();
                await transaction.CommitAsync(ct);
                return new(WarehouseStateDecision.Changed, id, row.Version);
            }
            catch (DbUpdateConcurrencyException)
            {
                await transaction.RollbackAsync(ct); database.ChangeTracker.Clear();
                return Reject(WarehouseStateDecision.Conflict);
            }
            catch (DbUpdateException error) when (error.InnerException is MySqlException { Number: 1062 })
            {
                await transaction.RollbackAsync(ct); database.ChangeTracker.Clear();
                return Reject(WarehouseStateDecision.Conflict);
            }
        }
        catch (DbUpdateException error) when (error.InnerException is DbException failure)
        {
            ct.ThrowIfCancellationRequested();
            logger.LogError("WMS warehouse state failed {Code}", "DatabaseUnavailable");
            throw WmsDatabaseFailures.Safe(failure);
        }
        catch (DbException error)
        {
            ct.ThrowIfCancellationRequested();
            logger.LogError("WMS warehouse state failed {Code}", "DatabaseUnavailable");
            throw WmsDatabaseFailures.Safe(error);
        }
    }

    private async Task<bool> HasActivityAsync(Guid id, CancellationToken ct) =>
        await database.Locations.AnyAsync(item => item.WarehouseId == id && item.Enabled, ct)
        || await database.Balances.AnyAsync(item => item.WarehouseId == id && item.Quantity > 0m, ct)
        || await (from reservation in database.Reservations
                  join balance in database.Balances on reservation.BalanceId equals balance.Id
                  where balance.WarehouseId == id && reservation.RemainingQuantity > 0m
                  select reservation.ExecutionUnitId).AnyAsync(ct)
        || await database.InboundDocuments.AnyAsync(item => item.WarehouseId == id
            && (item.State == InboundOrderState.Approved || item.State == InboundOrderState.Executing), ct);

    private async Task<bool> AuthorizedAsync(MasterDataSubject subject, Guid creator, CancellationToken ct)
    {
        var write = await authorization.ReadFieldsAsync(subject, "wms.warehouses.write", creator, ct);
        ct.ThrowIfCancellationRequested();
        var read = await authorization.ReadFieldsAsync(subject, "wms.warehouses.read", creator, ct);
        ct.ThrowIfCancellationRequested();
        if (write is null || read is null) throw new InvalidOperationException("Warehouse state authority returned no decision.");
        return write.ToHashSet(StringComparer.Ordinal).Contains("Enabled") && ReadFields.All(read.ToHashSet(StringComparer.Ordinal).Contains);
    }

    private WarehouseStateResult Reject(WarehouseStateDecision decision)
    {
        logger.LogWarning("WMS warehouse state rejected {Code}", decision.ToString());
        return new(decision);
    }
}
