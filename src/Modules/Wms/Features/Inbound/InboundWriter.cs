using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using MySql.Data.MySqlClient;
using Wes.Wms.Domain;
using Wes.Wms.Features.Ledger;
using Wes.Wms.Infrastructure.Persistence;

namespace Wes.Wms.Features.Inbound;

internal enum InboundWriteDecision { Committed, Replayed, Invalid, NotFound, Conflict, VersionConflict, DimensionMismatch, Forbidden, SourceRejected }
internal sealed record InboundWriteResult(InboundWriteDecision Decision, Guid? Id = null, string? Version = null);
internal sealed record InboundWriteGuards(Func<CancellationToken, Task<bool>> BeforeWrite,
    Func<CancellationToken, Task> Stage, Func<CancellationToken, Task<bool>> BeforeCommit);

/// <summary>Internal WMS transaction primitive; public callers must first establish current field/resource and operational authority.</summary>
internal sealed partial class InboundWriter(WmsDbContext database, TimeProvider clock)
{
    internal async Task<InboundWriteResult> CreateAsync(Guid operation, Guid actor, Guid warehouse, string code,
        IReadOnlyList<InboundPlanLine> lines, CancellationToken ct = default,
        Func<CancellationToken, Task<bool>>? beforeCommit = null)
    {
        Clean(ct);
        var proposed = new InboundDocument(database.TenantId, operation, actor, warehouse, code, lines, clock.GetUtcNow());
        var existing = await database.InboundDocuments.AsNoTracking().SingleOrDefaultAsync(item => item.CreationOperationId == operation, ct);
        if (existing is not null) return SameCreation(existing, proposed);
        if (!await database.Warehouses.AnyAsync(item => item.Id == warehouse, ct)) return new(InboundWriteDecision.NotFound);
        await using var transaction = await database.Database.BeginTransactionAsync(ct);
        database.InboundDocuments.Add(proposed);
        try
        {
            await database.SaveChangesAsync(ct);
            if (beforeCommit is not null && !await beforeCommit(ct))
            {
                await transaction.RollbackAsync(ct);
                database.ChangeTracker.Clear();
                return new(InboundWriteDecision.Forbidden);
            }
            ct.ThrowIfCancellationRequested();
            await transaction.CommitAsync(ct);
            return new(InboundWriteDecision.Committed, proposed.Id, proposed.Version);
        }
        catch (DbUpdateException error) when (error.InnerException is MySqlException { Number: 1062 })
        {
            await transaction.RollbackAsync(ct);
            await transaction.DisposeAsync();
            database.ChangeTracker.Clear();
            existing = await database.InboundDocuments.AsNoTracking().SingleOrDefaultAsync(item => item.CreationOperationId == operation, ct);
            return existing is null ? new(InboundWriteDecision.Conflict) : SameCreation(existing, proposed);
        }
        catch (DbUpdateException error) when (error.InnerException is DbException failure) { throw WmsDatabaseFailures.Safe(failure); }
    }

    internal Task<InboundWriteResult> ApproveAsync(Guid orderId, Guid operation, Guid actor, Guid correlation,
        string version, CancellationToken ct = default, InboundWriteGuards? guards = null) =>
        UpdateDraftAsync(orderId, operation, actor, correlation, version, null, ct, null, guards);

    internal async Task<InboundWriteResult> ReceiveAsync(Guid orderId, InboundReceiptCommand command,
        Guid correlation, string version, Guid balanceId, string balanceVersion, CancellationToken ct = default, InboundWriteGuards? guards = null)
    {
        Clean(ct);
        var existing = await OriginalReceiptAsync(orderId, command, balanceId, ct);
        if (existing is not null) return existing;
        var document = await database.InboundDocuments.AsNoTracking().SingleOrDefaultAsync(item => item.Id == orderId, ct);
        if (document is null) return new(InboundWriteDecision.NotFound);
        if (document.Version != version) return new(InboundWriteDecision.VersionConflict);
        var history = await HistoryAsync(orderId, ct);
        if (!SameFence(document, history)) return new(InboundWriteDecision.VersionConflict);
        var domain = document.Restore(history);
        if (domain.State is not (InboundOrderState.Approved or InboundOrderState.Executing)) return new(InboundWriteDecision.Conflict);
        var line = domain.Lines.SingleOrDefault(item => item.Id == command.LineId);
        if (line is null || command.ItemId != line.ItemId || command.UnitId != line.UnitId || command.Batch != line.Batch
            || command.Quantity <= 0m || command.Quantity > domain.Remaining(line.Id)
            || command.Quantity > StockMovementQuantity.MaximumValue || !QuantityPrecision.Create(line.Precision).CanRepresent(command.Quantity)
            || new[] { command.OperationId, command.ExecutionId, command.ActorId, command.ContainerId,
                command.ReceiptLocationId, command.TargetLocationId, correlation }.Contains(Guid.Empty))
            return new(InboundWriteDecision.Invalid);
        var balance = await database.Balances.AsNoTracking().SingleOrDefaultAsync(item => item.Id == balanceId, ct);
        if (balance is null) return new(InboundWriteDecision.NotFound);
        if (balance.WarehouseId != document.WarehouseId || balance.ItemId != command.ItemId || balance.UnitId != command.UnitId
            || balance.Batch != command.Batch || balance.ContainerId != command.ContainerId
            || balance.LocationId != command.ReceiptLocationId || balance.Precision != line.Precision)
            return new(InboundWriteDecision.DimensionMismatch);
        domain.Receive(command);
        var postingCommand = new StockPostingCommand(command.OperationId, command.ActorId, correlation, StockPostingKind.Receipt,
            orderId, command.LineId, command.ExecutionId, null, balanceId, command.Quantity, null, balanceVersion);
        var result = await new PostStock(database, clock).ExecuteComposedAsync(postingCommand, async token =>
        {
            // Lock the parent before inserting its sequence/FK children; otherwise MySQL can race on the unique sequence before the CAS update.
            await using var fence = database.Database.GetDbConnection().CreateCommand();
            fence.Transaction = database.Database.CurrentTransaction!.GetDbTransaction();
            fence.CommandText = "SELECT Version FROM wms_inbound_documents WHERE TenantId = @tenant AND Id = @id FOR UPDATE";
            var tenantParameter = fence.CreateParameter(); tenantParameter.ParameterName = "@tenant";
            tenantParameter.Value = database.TenantId.ToString("D"); fence.Parameters.Add(tenantParameter);
            var idParameter = fence.CreateParameter(); idParameter.ParameterName = "@id";
            idParameter.Value = orderId.ToString("D"); fence.Parameters.Add(idParameter);
            if (await fence.ExecuteScalarAsync(token) is not string currentVersion || currentVersion != version)
                throw new DbUpdateConcurrencyException("Inbound order changed before receipt commit.");
            database.InboundDocuments.Attach(document);
            document.Advance(domain.State);
            var posting = database.ChangeTracker.Entries<StockPosting>().Single(entry => entry.State == EntityState.Added).Entity;
            database.InboundFacts.Add(new(document, command.OperationId, command.ActorId, correlation,
                InboundFactKind.Received, clock.GetUtcNow(), command, posting.Id));
            if (guards is not null) await guards.Stage(token);
        }, ct, guards?.BeforeWrite, guards?.BeforeCommit);
        if (result.Decision == StockPostingDecision.Replayed)
        {
            database.ChangeTracker.Clear();
            return await OriginalReceiptAsync(orderId, command, balanceId, ct)
                ?? throw new InvalidOperationException("Inbound ledger receipt is missing its atomically committed source fact.");
        }
        return result.Decision switch
        {
            StockPostingDecision.Posted => new(InboundWriteDecision.Committed, command.ExecutionId, document.Version),
            StockPostingDecision.VersionConflict => new(InboundWriteDecision.VersionConflict),
            StockPostingDecision.IdempotencyConflict => new(InboundWriteDecision.Conflict),
            StockPostingDecision.NotFound => new(InboundWriteDecision.NotFound),
            StockPostingDecision.SourceRejected => new(InboundWriteDecision.SourceRejected),
            StockPostingDecision.Invalid or StockPostingDecision.VersionRequired => new(InboundWriteDecision.Invalid),
            _ => throw new InvalidOperationException("Unexpected inbound receipt posting outcome.")
        };
    }

    private Task<List<InboundFact>> HistoryAsync(Guid id, CancellationToken ct) =>
        database.InboundFacts.AsNoTracking().Where(item => item.OrderId == id).OrderBy(item => item.Sequence).ToListAsync(ct);
    private async Task<InboundWriteResult?> OriginalReceiptAsync(Guid orderId, InboundReceiptCommand command,
        Guid balanceId, CancellationToken ct)
    {
        var existing = await database.InboundFacts.AsNoTracking().Where(item => item.OperationId == command.OperationId
            || item.ExecutionId == command.ExecutionId).ToArrayAsync(ct);
        if (existing.Length == 0) return null;
        if (existing.Length != 1 || existing[0].OrderId != orderId || existing[0].Kind != InboundFactKind.Received
            || existing[0].Receipt() with { OperationId = command.OperationId } != command)
            return new(InboundWriteDecision.Conflict);
        var posting = await database.Postings.AsNoTracking().SingleAsync(item => item.Id == existing[0].PostingId, ct);
        return posting.TargetBalanceId == balanceId
            ? new(InboundWriteDecision.Replayed, command.ExecutionId, existing[0].AfterVersion) : new(InboundWriteDecision.Conflict);
    }
    private static bool SameFence(InboundDocument document, IReadOnlyList<InboundFact> history) =>
        history.Count == document.Sequence && (history.Count == 0 || history[^1].AfterVersion == document.Version);
    private void Clean(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (database.ChangeTracker.Entries().Any() || database.Database.CurrentTransaction is not null)
            throw new InvalidOperationException("Inbound writes require a fresh WMS owner.");
    }
    private static InboundWriteResult SameCreation(InboundDocument existing, InboundDocument proposed) =>
        existing.CreatorId == proposed.CreatorId && existing.WarehouseId == proposed.WarehouseId
            && existing.Code == proposed.Code && existing.Plan == proposed.Plan
        ? new(InboundWriteDecision.Replayed, existing.Id, existing.Version) : new(InboundWriteDecision.Conflict);
}
