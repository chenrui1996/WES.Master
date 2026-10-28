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

internal sealed class LocationStateAccess(WmsDbContext database, IMasterDataAuthorization authorization,
    IWarehouseLocationSchema schema, TimeProvider clock, ILogger<LocationStateAccess> logger)
{
    private static readonly string[] WarehouseFields = ["Id", "Code", "Name", "Enabled", "Version", "CreatedAt"];
    private static readonly string[] LocationFields = [.. WarehouseFields, "WarehouseId", "PackId", "SchemaVersion"];

    internal async Task<LocationStateResult> ChangeAsync(MasterDataSubject subject, Guid id, LocationStateCommand request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(subject); ArgumentNullException.ThrowIfNull(request);
        ct.ThrowIfCancellationRequested();
        if (database.ChangeTracker.Entries().Any() || database.Database.CurrentTransaction is not null
            || System.Transactions.Transaction.Current is not null)
            throw new InvalidOperationException("Location transitions require a fresh independent WMS owner.");
        if (subject.TenantId != database.TenantId || subject.ActorId == Guid.Empty || subject.SessionId == Guid.Empty)
            return Reject(LocationStateDecision.Forbidden);
        if (id == Guid.Empty || request.OperationId == Guid.Empty || !WarehouseStateChange.ValidReason(request.Reason)
            || !Guid.TryParseExact(request.ExpectedVersion, "D", out var expected) || expected == Guid.Empty)
            return Reject(LocationStateDecision.Invalid);
        try
        {
            var provenance = await database.MasterDataChanges.AsNoTracking().Where(item => item.LocationId == id)
                .Select(item => new { item.ActorId, item.WarehouseId }).SingleOrDefaultAsync(ct);
            if (provenance is null) return Reject(LocationStateDecision.NotFound);
            var parentCreator = await database.MasterDataChanges.AsNoTracking()
                .Where(item => item.WarehouseId == provenance.WarehouseId && item.LocationId == null)
                .Select(item => (Guid?)item.ActorId).SingleOrDefaultAsync(ct);
            if (parentCreator is null) return Reject(LocationStateDecision.NotFound);
            if (!await AuthorizedAsync(subject, provenance.ActorId, parentCreator.Value, ct)) return Reject(LocationStateDecision.Forbidden);
            await using var transaction = await database.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
            try
            {
                // Parent first: warehouse disabling and sibling transitions cannot invert the lock order.
                await FenceAsync("wms_warehouses", provenance.WarehouseId, ct);
                await FenceAsync("wms_locations", id, ct);
                var existing = await database.LocationStateChanges.AsNoTracking()
                    .SingleOrDefaultAsync(item => item.OperationId == request.OperationId, ct);
                if (existing is not null)
                {
                    if (existing.LocationId != id || existing.ActorId != subject.ActorId || existing.BeforeVersion != request.ExpectedVersion
                        || existing.Enabled != request.Enabled || existing.Reason != request.Reason)
                        return Reject(LocationStateDecision.Conflict);
                    await transaction.CommitAsync(ct);
                    if (!await AuthorizedAsync(subject, provenance.ActorId, parentCreator.Value, ct)) return Reject(LocationStateDecision.Forbidden);
                    return new(LocationStateDecision.Replayed, id, existing.AfterVersion);
                }
                var row = await database.Locations.SingleAsync(item => item.Id == id, ct);
                if (await WmsResourceLocks.CountConflictAsync(database, row.WarehouseId, [id], [], ct)
                    || await Wes.Wms.Features.Transfers.TransferProtection.ConflictsAsync(database, row.WarehouseId, [id], [], ct))
                    return Reject(LocationStateDecision.Blocked);
                if (row.Version != request.ExpectedVersion || row.Enabled == request.Enabled) return Reject(LocationStateDecision.Conflict);
                if (request.Enabled)
                {
                    if (!await database.Warehouses.AnyAsync(item => item.Id == row.WarehouseId && item.Enabled, ct))
                        return Reject(LocationStateDecision.Blocked);
                    var evidence = await schema.ValidateAsync(database.TenantId, row.WarehouseId, row.PackId, row.SchemaVersion, row.Code, ct);
                    ct.ThrowIfCancellationRequested();
                    if (evidence != LocationSchemaDecision.Valid) return Reject(evidence switch
                    {
                        LocationSchemaDecision.Rejected => LocationStateDecision.SchemaRejected,
                        LocationSchemaDecision.Unavailable => LocationStateDecision.SchemaUnavailable,
                        _ => throw new InvalidOperationException("Unknown location schema decision.")
                    });
                }
                else if (await HasActivityAsync(row, ct)) return Reject(LocationStateDecision.Blocked);
                var beforeEnabled = row.Enabled;
                row.ChangeState(request.Enabled);
                database.LocationStateChanges.Add(new(request.OperationId, subject.ActorId, Guid.NewGuid(), row,
                    request.ExpectedVersion, beforeEnabled, request.Reason, clock.GetUtcNow()));
                await database.SaveChangesAsync(ct);
                if (!await AuthorizedAsync(subject, provenance.ActorId, parentCreator.Value, ct))
                {
                    await transaction.RollbackAsync(ct); database.ChangeTracker.Clear();
                    return Reject(LocationStateDecision.Forbidden);
                }
                ct.ThrowIfCancellationRequested();
                await transaction.CommitAsync(ct);
                return new(LocationStateDecision.Changed, id, row.Version);
            }
            catch (DbUpdateConcurrencyException)
            {
                await transaction.RollbackAsync(ct); database.ChangeTracker.Clear(); return Reject(LocationStateDecision.Conflict);
            }
            catch (DbUpdateException error) when (error.InnerException is MySqlException { Number: 1062 })
            {
                await transaction.RollbackAsync(ct); database.ChangeTracker.Clear(); return Reject(LocationStateDecision.Conflict);
            }
        }
        catch (DbUpdateException error) when (error.InnerException is DbException failure)
        {
            ct.ThrowIfCancellationRequested(); logger.LogError("WMS location state failed {Code}", "DatabaseUnavailable");
            throw WmsDatabaseFailures.Safe(failure);
        }
        catch (DbException error)
        {
            ct.ThrowIfCancellationRequested(); logger.LogError("WMS location state failed {Code}", "DatabaseUnavailable");
            throw WmsDatabaseFailures.Safe(error);
        }
    }

    private async Task FenceAsync(string table, Guid id, CancellationToken ct)
    {
        await using var command = database.Database.GetDbConnection().CreateCommand();
        command.Transaction = database.Database.CurrentTransaction!.GetDbTransaction();
        command.CommandText = $"SELECT Version FROM {table} WHERE TenantId = @tenant AND Id = @id FOR UPDATE";
        var tenant = command.CreateParameter(); tenant.ParameterName = "@tenant"; tenant.Value = database.TenantId.ToString("D"); command.Parameters.Add(tenant);
        var resource = command.CreateParameter(); resource.ParameterName = "@id"; resource.Value = id.ToString("D"); command.Parameters.Add(resource);
        if (await command.ExecuteScalarAsync(ct) is not string)
            throw new InvalidOperationException("Immutable location provenance is missing its parent or resource.");
    }

    private async Task<bool> HasActivityAsync(WarehouseLocation location, CancellationToken ct)
    {
        if (await database.Balances.AnyAsync(item => item.LocationId == location.Id && item.Quantity > 0m, ct)
            || await (from reservation in database.Reservations
                      join balance in database.Balances on reservation.BalanceId equals balance.Id
                      where balance.LocationId == location.Id && reservation.RemainingQuantity > 0m
                      select reservation.ExecutionUnitId).AnyAsync(ct)) return true;
        var receipts = await Wes.Wms.Features.Inbound.InboundPendingProtection.ReadAsync(database, location.WarehouseId, ct);
        return receipts.Any(receipt => receipt.ReceiptLocationId == location.Id || receipt.TargetLocationId == location.Id);
    }

    private async Task<bool> AuthorizedAsync(MasterDataSubject subject, Guid creator, Guid parentCreator, CancellationToken ct)
    {
        var write = await authorization.ReadFieldsAsync(subject, "wms.locations.write", creator, ct);
        var read = await authorization.ReadFieldsAsync(subject, "wms.locations.read", creator, ct);
        var parent = await authorization.ReadFieldsAsync(subject, "wms.warehouses.read", parentCreator, ct);
        ct.ThrowIfCancellationRequested();
        if (write is null || read is null || parent is null) throw new InvalidOperationException("Location authority returned no decision.");
        return write.ToHashSet(StringComparer.Ordinal).Contains("Enabled") && LocationFields.All(read.ToHashSet(StringComparer.Ordinal).Contains)
            && WarehouseFields.All(parent.ToHashSet(StringComparer.Ordinal).Contains);
    }

    private LocationStateResult Reject(LocationStateDecision decision)
    {
        logger.LogWarning("WMS location state rejected {Code}", decision.ToString()); return new(decision);
    }
}
