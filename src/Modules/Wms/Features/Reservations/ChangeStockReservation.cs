using System.Data;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using MySql.Data.MySqlClient;
using Wes.Wms.Domain;
using Wes.Wms.Features.Inbound;
using Wes.Wms.Infrastructure.Persistence;

namespace Wes.Wms.Features.Reservations;

internal sealed record ReservationCommand(Guid OperationId, Guid ActorId, Guid CorrelationId, ReservationAction Action,
    Guid ExecutionUnitId, Guid BalanceId, Guid SourceDocumentId, Guid SourceLineId, decimal Quantity, string? ExpectedVersion);
internal enum ReservationDecision { Changed, Replayed, Invalid, NotFound, VersionRequired, VersionConflict, IdempotencyConflict, InsufficientStock, StateConflict }
internal sealed record ReservationResult(ReservationDecision Decision);

internal sealed class ChangeStockReservation(WmsDbContext database, TimeProvider clock)
{
    internal async Task<ReservationResult> ExecuteAsync(ReservationCommand command, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        ct.ThrowIfCancellationRequested();
        if (database.ChangeTracker.Entries().Any() || database.Database.CurrentTransaction is not null)
            throw new InvalidOperationException("Reservation changes require a clean WMS owner.");
        if (!Valid(command)) return new(ReservationDecision.Invalid);
        try
        {
            var replay = await FindReplayAsync(command, ct);
            if (replay is not null) return replay;
            await using var transaction = await database.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
            try
            {
                var dimension = await database.Balances.AsNoTracking().SingleOrDefaultAsync(item => item.Id == command.BalanceId, ct);
                if (dimension is null) return new(ReservationDecision.NotFound);
                if (await database.StockContainers.AnyAsync(row => row.Id == dimension.ContainerId, ct))
                {
                    var locations = await database.Locations.Where(row => row.Id == dimension.LocationId).Select(row => row.Id).ToArrayAsync(ct);
                    await WmsResourceLocks.AcquireAsync(database, dimension.WarehouseId, locations, dimension.ContainerId, ct);
                }
                var balance = await database.Balances.SingleOrDefaultAsync(item => item.Id == command.BalanceId, ct);
                if (balance is null) return new(ReservationDecision.NotFound);
                if (!Guid.TryParseExact(command.ExpectedVersion, "D", out var version) || version == Guid.Empty)
                    return new(ReservationDecision.VersionRequired);
                if (balance.Version != command.ExpectedVersion)
                    return await FindReplayAsync(command, ct) ?? new(ReservationDecision.VersionConflict);
                if (!QuantityPrecision.Create(balance.Precision).CanRepresent(command.Quantity))
                    return new(ReservationDecision.Invalid);
                var reservation = await database.Reservations.SingleOrDefaultAsync(item => item.ExecutionUnitId == command.ExecutionUnitId, ct);
                decimal before;
                StockProtectionKind beforeKind;
                if (command.Action == ReservationAction.Reserve)
                {
                    if ((await InboundPendingProtection.ReadAsync(database, balance.WarehouseId, ct))
                        .Any(receipt => receipt.ContainerId == balance.ContainerId && receipt.ExecutionId != command.ExecutionUnitId))
                        return new(ReservationDecision.StateConflict);
                    if (reservation is not null)
                        return await FindReplayAsync(command, ct)
                            ?? throw new InvalidOperationException("A reserved execution identity must resolve to its permanent record.");
                    var reservations = await database.Reservations.Where(item => item.BalanceId == balance.Id && item.RemainingQuantity > 0m).ToListAsync(ct);
                    if (!await database.Balances.AnyAsync(item => item.Id == balance.Id && item.Version == balance.Version, ct))
                        return await FindReplayAsync(command, ct) ?? new(ReservationDecision.VersionConflict);
                    var availability = StockAvailability.Calculate(InventoryQuantity.Create(balance.Quantity),
                        reservations.Select(item => new StockProtection(item.ExecutionUnitId, InventoryQuantity.Create(item.RemainingQuantity), item.Kind)));
                    if (command.Quantity > availability.Available.Value) return new(ReservationDecision.InsufficientStock);
                    reservation = new(database.TenantId, command.ExecutionUnitId, balance.Id, command.SourceDocumentId,
                        command.SourceLineId, command.ActorId, command.Quantity, clock.GetUtcNow());
                    database.Reservations.Add(reservation);
                    before = 0m;
                    beforeKind = 0;
                }
                else
                {
                    if (reservation is null) return new(ReservationDecision.NotFound);
                    if (reservation.BalanceId != balance.Id || reservation.SourceDocumentId != command.SourceDocumentId
                        || reservation.SourceLineId != command.SourceLineId)
                        return new(ReservationDecision.IdempotencyConflict);
                    before = reservation.RemainingQuantity;
                    beforeKind = reservation.Kind;
                    if (!reservation.TryChange(command.Action, command.Quantity)) return new(ReservationDecision.StateConflict);
                }
                database.ReservationChanges.Add(new(database.TenantId, command.OperationId, command.ActorId, command.CorrelationId,
                    command.Action, reservation, command.Quantity, before, beforeKind, clock.GetUtcNow()));
                balance.TouchReservationFence();
                await database.SaveChangesAsync(ct);
                ct.ThrowIfCancellationRequested();
                await transaction.CommitAsync(ct);
                return new(ReservationDecision.Changed);
            }
            catch (DbUpdateConcurrencyException)
            {
                await transaction.RollbackAsync(ct);
                await transaction.DisposeAsync();
                database.ChangeTracker.Clear();
                return await FindReplayAsync(command, ct) ?? new(ReservationDecision.VersionConflict);
            }
            catch (DbUpdateException error) when (error.InnerException is MySqlException { Number: 1062 })
            {
                await transaction.RollbackAsync(ct);
                await transaction.DisposeAsync();
                database.ChangeTracker.Clear();
                return await FindReplayAsync(command, ct) ?? throw new InvalidOperationException("Reservation uniqueness conflict has no matching command identity.");
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

    private async Task<ReservationResult?> FindReplayAsync(ReservationCommand command, CancellationToken ct)
    {
        var change = await database.ReservationChanges.AsNoTracking().SingleOrDefaultAsync(item => item.OperationId == command.OperationId, ct);
        if (change is not null)
            return new(change.Action == command.Action && change.ExecutionUnitId == command.ExecutionUnitId
                && change.ActorId == command.ActorId && change.BalanceId == command.BalanceId
                && change.SourceDocumentId == command.SourceDocumentId && change.SourceLineId == command.SourceLineId
                && change.Quantity == command.Quantity ? ReservationDecision.Replayed : ReservationDecision.IdempotencyConflict);
        if (command.Action != ReservationAction.Reserve) return null;
        var existing = await database.Reservations.AsNoTracking().SingleOrDefaultAsync(item => item.ExecutionUnitId == command.ExecutionUnitId, ct);
        if (existing is null) return null;
        return new(existing.ActorId == command.ActorId && existing.BalanceId == command.BalanceId
            && existing.SourceDocumentId == command.SourceDocumentId && existing.SourceLineId == command.SourceLineId
            && existing.OriginalQuantity == command.Quantity ? ReservationDecision.Replayed : ReservationDecision.IdempotencyConflict);
    }

    private static bool Valid(ReservationCommand command) =>
        new[] { command.OperationId, command.ActorId, command.CorrelationId, command.ExecutionUnitId,
            command.BalanceId, command.SourceDocumentId, command.SourceLineId }.All(item => item != Guid.Empty)
        && Enum.IsDefined(command.Action)
        && (command.Action is ReservationAction.MarkInTransit or ReservationAction.MarkUnknown
            ? command.Quantity == 0m
            : command.Quantity > 0m && command.Quantity <= StockMovementQuantity.MaximumValue
                && QuantityPrecision.Create(6).CanRepresent(command.Quantity));
}
