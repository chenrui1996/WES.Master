using System.Data;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using MySql.Data.MySqlClient;
using Wes.Wms.Domain;
using Wes.Wms.Infrastructure.Persistence;

namespace Wes.Wms.Features.Ledger;

internal sealed record StockPostingCommand(Guid OperationId, Guid ActorId, Guid CorrelationId, StockPostingKind Kind,
    Guid SourceDocumentId, Guid SourceLineId, Guid ExecutionUnitId, Guid? SourceBalanceId, Guid? TargetBalanceId,
    decimal Quantity, string? ExpectedSourceVersion, string? ExpectedTargetVersion);

internal enum StockPostingDecision
{
    Posted, Replayed, Invalid, NotFound, VersionRequired, VersionConflict, IdempotencyConflict, InsufficientStock, DimensionMismatch
}

internal sealed record StockPostingResult(StockPostingDecision Decision, Guid? PostingId = null);

/// <summary>Sanitized owner failure; a caller must reconcile an uncertain commit using the original operation identity.</summary>
public sealed class WmsDatabaseException(bool transient) : Exception("WMS database operation failed.")
{
    /// <summary>Whether a fresh owner can retry after reconciling the original operation.</summary>
    public bool IsTransient { get; } = transient;
}

internal sealed class PostStock(WmsDbContext database, TimeProvider clock)
{
    internal async Task<StockPostingResult> ExecuteAsync(StockPostingCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        cancellationToken.ThrowIfCancellationRequested();
        if (database.ChangeTracker.Entries().Any() || database.Database.CurrentTransaction is not null)
            throw new InvalidOperationException("Stock posting requires a clean WMS owner.");
        if (!Valid(command)) return new(StockPostingDecision.Invalid);
        try
        {
            var existing = await FindExistingAsync(command, cancellationToken);
            if (existing is not null) return existing;
            await using var transaction = await database.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
            try
            {
                var source = command.SourceBalanceId.HasValue
                    ? await database.Balances.SingleOrDefaultAsync(item => item.Id == command.SourceBalanceId, cancellationToken) : null;
                var target = command.TargetBalanceId.HasValue
                    ? await database.Balances.SingleOrDefaultAsync(item => item.Id == command.TargetBalanceId, cancellationToken) : null;
                if ((command.SourceBalanceId.HasValue && source is null) || (command.TargetBalanceId.HasValue && target is null))
                    return new(StockPostingDecision.NotFound);
                if ((source is not null && !IsVersion(command.ExpectedSourceVersion))
                    || (target is not null && !IsVersion(command.ExpectedTargetVersion)))
                    return new(StockPostingDecision.VersionRequired);
                if ((source is not null && source.Version != command.ExpectedSourceVersion)
                    || (target is not null && target.Version != command.ExpectedTargetVersion))
                    return await FindExistingAsync(command, cancellationToken) ?? new(StockPostingDecision.VersionConflict);
                if (source is not null && target is not null && !source.CanTransferTo(target))
                    return new(StockPostingDecision.DimensionMismatch);
                if (source is not null && source.Quantity < command.Quantity)
                    return new(StockPostingDecision.InsufficientStock);
                var precision = QuantityPrecision.Create((source ?? target!).Precision);
                if (!precision.CanRepresent(command.Quantity)) return new(StockPostingDecision.Invalid);
                var quantity = StockMovementQuantity.Create(command.Quantity, precision);
                if (target is not null && target.Quantity > InventoryQuantity.MaximumValue - quantity.Value)
                    return new(StockPostingDecision.Invalid);
                var posting = new StockPosting(database.TenantId, command.OperationId, command.ActorId,
                    command.CorrelationId, command.Kind, command.SourceDocumentId, command.SourceLineId,
                    command.ExecutionUnitId, command.SourceBalanceId, command.TargetBalanceId, command.Quantity, clock.GetUtcNow());
                database.Postings.Add(posting);
                if (source is not null)
                {
                    var change = source.Debit(quantity);
                    database.Entries.Add(new StockEntry(posting, source, change.Before, change.After));
                }
                if (target is not null)
                {
                    var change = target.Credit(quantity);
                    database.Entries.Add(new StockEntry(posting, target, change.Before, change.After));
                }
                await database.SaveChangesAsync(cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                await transaction.CommitAsync(cancellationToken);
                return new(StockPostingDecision.Posted, posting.Id);
            }
            catch (DbUpdateConcurrencyException)
            {
                await transaction.RollbackAsync(cancellationToken);
                await transaction.DisposeAsync();
                database.ChangeTracker.Clear();
                return await FindExistingAsync(command, cancellationToken) ?? new(StockPostingDecision.VersionConflict);
            }
            catch (DbUpdateException error) when (error.InnerException is MySqlException { Number: 1062 })
            {
                await transaction.RollbackAsync(cancellationToken);
                await transaction.DisposeAsync();
                database.ChangeTracker.Clear();
                var replay = await FindExistingAsync(command, cancellationToken);
                if (replay is not null) return replay;
                throw new WmsDatabaseException(false);
            }
        }
        catch (DbUpdateException error) when (error.InnerException is DbException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw Safe((DbException)error.InnerException);
        }
        catch (DbException error)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw Safe(error);
        }
    }

    private async Task<StockPostingResult?> FindExistingAsync(StockPostingCommand command, CancellationToken ct)
    {
        var existing = await database.Postings.AsNoTracking().Where(item => item.OperationId == command.OperationId
            || (item.Kind == command.Kind && item.ExecutionUnitId == command.ExecutionUnitId)).ToListAsync(ct);
        if (existing.Count == 0) return null;
        if (existing.Count != 1) return new(StockPostingDecision.IdempotencyConflict);
        var posting = existing[0];
        var same = posting.Kind == command.Kind && posting.ActorId == command.ActorId
            && posting.SourceDocumentId == command.SourceDocumentId && posting.SourceLineId == command.SourceLineId
            && posting.ExecutionUnitId == command.ExecutionUnitId && posting.SourceBalanceId == command.SourceBalanceId
            && posting.TargetBalanceId == command.TargetBalanceId && posting.Quantity == command.Quantity;
        return new(same ? StockPostingDecision.Replayed : StockPostingDecision.IdempotencyConflict, same ? posting.Id : null);
    }

    private static bool Valid(StockPostingCommand command) =>
        command.OperationId != Guid.Empty && command.ActorId != Guid.Empty && command.CorrelationId != Guid.Empty
        && command.SourceDocumentId != Guid.Empty && command.SourceLineId != Guid.Empty && command.ExecutionUnitId != Guid.Empty
        && Enum.IsDefined(command.Kind) && command.Quantity > 0m && command.Quantity <= StockMovementQuantity.MaximumValue
        && QuantityPrecision.Create(6).CanRepresent(command.Quantity)
        && command.SourceBalanceId != Guid.Empty && command.TargetBalanceId != Guid.Empty
        && (command.Kind switch
        {
            StockPostingKind.Receipt => command.SourceBalanceId is null && command.TargetBalanceId.HasValue,
            StockPostingKind.Shipment => command.SourceBalanceId.HasValue && command.TargetBalanceId is null,
            StockPostingKind.Transfer => command.SourceBalanceId.HasValue && command.TargetBalanceId.HasValue
                && command.SourceBalanceId != command.TargetBalanceId,
            _ => false
        });

    private static bool IsVersion(string? version) => Guid.TryParseExact(version, "D", out var id) && id != Guid.Empty;
    private static WmsDatabaseException Safe(DbException error) =>
        new(error.IsTransient || error is MySqlException { Number: 1040 or 1042 or 1205 or 1213 or 2006 or 2013 });
}
