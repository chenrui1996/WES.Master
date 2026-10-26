using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using MySql.Data.MySqlClient;
using Wes.Wms.Domain;
using Wes.Wms.Features.Ledger;
using Wes.Wms.Infrastructure.Persistence;

namespace Wes.Wms.Features.MasterData;

/// <summary>Exact Pack schema validation outcomes; unavailable evidence never permits a location definition.</summary>
public enum LocationSchemaDecision
{
    /// <summary>The pinned warehouse/Pack/schema/code tuple is supported.</summary>
    Valid,
    /// <summary>The tuple is invalid for this Pack.</summary>
    Rejected,
    /// <summary>The adapter cannot establish current schema evidence.</summary>
    Unavailable
}

/// <summary>Semantic schema boundary; implementations must not expose vendor addresses or access another module's private data.</summary>
public interface IWarehouseLocationSchema
{
    /// <summary>Validates the complete trusted tuple without activating a location or issuing device commands.</summary>
    Task<LocationSchemaDecision> ValidateAsync(Guid tenantId, Guid warehouseId, Guid packId,
        string schemaVersion, string code, CancellationToken cancellationToken);
}

internal sealed record CreateMasterDataCommand(Guid OperationId, Guid ActorId, Guid CorrelationId,
    string Code, string Name, Guid? WarehouseId = null, Guid? PackId = null, string? SchemaVersion = null);
internal enum MasterDataDecision
{
    Created, Replayed, Invalid, NotFound, CodeConflict, IdempotencyConflict, SchemaRejected, SchemaUnavailable
}
internal sealed record MasterDataResult(MasterDataDecision Decision, Guid? Id = null, string? Version = null);

/// <summary>Internal disabled master-data creation only; a future external boundary must supply live authorization first.</summary>
internal sealed class CreateMasterData(WmsDbContext database, TimeProvider clock, IWarehouseLocationSchema schema)
{
    internal async Task<MasterDataResult> ExecuteAsync(CreateMasterDataCommand command, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(schema);
        ct.ThrowIfCancellationRequested();
        if (database.ChangeTracker.Entries().Any() || database.Database.CurrentTransaction is not null)
            throw new InvalidOperationException("Master-data creation requires a clean WMS owner.");
        if (command.OperationId == Guid.Empty || command.ActorId == Guid.Empty || command.CorrelationId == Guid.Empty
            || !MasterDataValue.IsCode(command.Code) || !MasterDataValue.IsName(command.Name)
            || (command.WarehouseId.HasValue
                ? command.WarehouseId == Guid.Empty || command.PackId is null || command.PackId == Guid.Empty
                    || !MasterDataValue.IsCode(command.SchemaVersion)
                : command.PackId.HasValue || command.SchemaVersion is not null))
            return new(MasterDataDecision.Invalid);
        var code = MasterDataValue.Code(command.Code);
        try
        {
            var existing = await ExistingAsync(command, code, ct);
            if (existing is not null) return existing;
            if (command.WarehouseId.HasValue)
            {
                if (!await database.Warehouses.AnyAsync(item => item.Id == command.WarehouseId, ct))
                    return new(MasterDataDecision.NotFound);
                var evidence = await schema.ValidateAsync(database.TenantId, command.WarehouseId.Value,
                    command.PackId!.Value, command.SchemaVersion!, code, ct);
                ct.ThrowIfCancellationRequested();
                if (evidence != LocationSchemaDecision.Valid) return new(evidence switch
                {
                    LocationSchemaDecision.Rejected => MasterDataDecision.SchemaRejected,
                    LocationSchemaDecision.Unavailable => MasterDataDecision.SchemaUnavailable,
                    _ => throw new InvalidOperationException("Unknown location schema decision.")
                });
            }
            await using var transaction = await database.Database.BeginTransactionAsync(ct);
            try
            {
                MasterDataChange change;
                if (command.WarehouseId.HasValue)
                {
                    var location = new WarehouseLocation(database.TenantId, command.WarehouseId.Value, command.PackId!.Value,
                        command.SchemaVersion!, code, command.Name, clock.GetUtcNow());
                    database.Locations.Add(location);
                    change = new(command.OperationId, command.ActorId, command.CorrelationId, location);
                }
                else
                {
                    var warehouse = new Warehouse(database.TenantId, code, command.Name, clock.GetUtcNow());
                    database.Warehouses.Add(warehouse);
                    change = new(command.OperationId, command.ActorId, command.CorrelationId, warehouse);
                }
                database.MasterDataChanges.Add(change);
                await database.SaveChangesAsync(ct);
                ct.ThrowIfCancellationRequested();
                await transaction.CommitAsync(ct);
                return new(MasterDataDecision.Created, change.LocationId ?? change.WarehouseId, change.AfterVersion);
            }
            catch (DbUpdateException error) when (error.InnerException is MySqlException { Number: 1062 })
            {
                await transaction.RollbackAsync(ct);
                await transaction.DisposeAsync();
                database.ChangeTracker.Clear();
                var winner = await ExistingAsync(command, code, ct);
                if (winner is not null) return winner;
                var duplicate = command.WarehouseId.HasValue
                    ? await database.Locations.AnyAsync(item => item.Code == code, ct)
                    : await database.Warehouses.AnyAsync(item => item.Code == code, ct);
                if (duplicate) return new(MasterDataDecision.CodeConflict);
                throw new WmsDatabaseException(false);
            }
        }
        catch (DbUpdateException error) when (error.InnerException is DbException)
        {
            ct.ThrowIfCancellationRequested();
            throw WmsDatabaseFailures.Safe((DbException)error.InnerException);
        }
        catch (DbException error)
        {
            ct.ThrowIfCancellationRequested();
            throw WmsDatabaseFailures.Safe(error);
        }
    }

    private async Task<MasterDataResult?> ExistingAsync(CreateMasterDataCommand command, string code, CancellationToken ct)
    {
        var existing = await database.MasterDataChanges.AsNoTracking().SingleOrDefaultAsync(
            item => item.OperationId == command.OperationId, ct);
        if (existing is null) return null;
        if (existing.ActorId != command.ActorId || existing.Code != code || existing.Name != command.Name
            || existing.PackId != command.PackId || existing.SchemaVersion != command.SchemaVersion
            || (command.WarehouseId.HasValue
                ? existing.LocationId is null || existing.WarehouseId != command.WarehouseId
                : existing.LocationId is not null))
            return new(MasterDataDecision.IdempotencyConflict);
        return new(MasterDataDecision.Replayed, existing.LocationId ?? existing.WarehouseId, existing.AfterVersion);
    }
}
