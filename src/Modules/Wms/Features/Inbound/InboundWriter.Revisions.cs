using System.Data;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using MySql.Data.MySqlClient;
using Wes.Wms.Domain;
using Wes.Wms.Features.Ledger;
using Wes.Wms.Infrastructure.Persistence;

namespace Wes.Wms.Features.Inbound;

internal sealed partial class InboundWriter
{
    internal Task<InboundWriteResult> ReviseAsync(Guid orderId, Guid operation, Guid actor, Guid correlation,
        string version, IReadOnlyList<InboundPlanLine> lines, CancellationToken ct = default,
        Func<CancellationToken, Task<bool>>? beforeCommit = null) =>
        UpdateDraftAsync(orderId, operation, actor, correlation, version, lines, ct, beforeCommit);

    private async Task<InboundWriteResult> UpdateDraftAsync(Guid orderId, Guid operation, Guid actor, Guid correlation,
        string version, IReadOnlyList<InboundPlanLine>? lines, CancellationToken ct, Func<CancellationToken, Task<bool>>? beforeCommit)
    {
        Clean(ct);
        if (new[] { orderId, operation, actor, correlation }.Contains(Guid.Empty))
            throw new ArgumentException("Draft transitions require complete trusted operation identities.");
        var replay = await DraftReplayAsync(orderId, operation, actor, version, lines, ct);
        if (replay is not null) return replay;
        await using var transaction = await database.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        try
        {
            // Both draft transitions lock the parent before adding sequence/FK children, as receipts already do.
            await using var fence = database.Database.GetDbConnection().CreateCommand();
            fence.Transaction = transaction.GetDbTransaction();
            fence.CommandText = "SELECT Version FROM wms_inbound_documents WHERE TenantId = @tenant AND Id = @id FOR UPDATE";
            var tenant = fence.CreateParameter(); tenant.ParameterName = "@tenant";
            tenant.Value = database.TenantId.ToString("D"); fence.Parameters.Add(tenant);
            var id = fence.CreateParameter(); id.ParameterName = "@id";
            id.Value = orderId.ToString("D"); fence.Parameters.Add(id);
            if (await fence.ExecuteScalarAsync(ct) is not string currentVersion) return new(InboundWriteDecision.NotFound);
            replay = await DraftReplayAsync(orderId, operation, actor, version, lines, ct);
            if (replay is not null) return replay;
            if (currentVersion != version) return new(InboundWriteDecision.VersionConflict);
            var document = await database.InboundDocuments.SingleAsync(item => item.Id == orderId, ct);
            var domain = document.Restore(await HistoryAsync(orderId, ct));
            if (domain.State != InboundOrderState.Draft) return new(InboundWriteDecision.Conflict);
            if (lines is null) domain.Approve();
            else domain.Revise(lines);
            document.Advance(domain.State);
            database.InboundFacts.Add(new(document, operation, actor, correlation,
                lines is null ? InboundFactKind.Approved : InboundFactKind.Revised, clock.GetUtcNow(),
                revision: lines is null ? null : new(version, lines)));
            await database.SaveChangesAsync(ct);
            if (beforeCommit is not null && !await beforeCommit(ct))
            {
                await transaction.RollbackAsync(ct);
                database.ChangeTracker.Clear();
                return new(InboundWriteDecision.Forbidden);
            }
            ct.ThrowIfCancellationRequested();
            await transaction.CommitAsync(ct);
            return new(InboundWriteDecision.Committed, orderId, document.Version);
        }
        catch (DbUpdateConcurrencyException)
        {
            await transaction.RollbackAsync(ct);
            database.ChangeTracker.Clear();
            return new(InboundWriteDecision.VersionConflict);
        }
        catch (DbUpdateException error) when (error.InnerException is MySqlException { Number: 1062 })
        {
            await transaction.RollbackAsync(ct);
            await transaction.DisposeAsync();
            database.ChangeTracker.Clear();
            return await DraftReplayAsync(orderId, operation, actor, version, lines, ct) ?? new(InboundWriteDecision.Conflict);
        }
        catch (DbUpdateException error) when (error.InnerException is DbException failure) { throw WmsDatabaseFailures.Safe(failure); }
    }

    private async Task<InboundWriteResult?> DraftReplayAsync(Guid orderId, Guid operation, Guid actor,
        string version, IReadOnlyList<InboundPlanLine>? lines, CancellationToken ct)
    {
        var fact = await database.InboundFacts.AsNoTracking().SingleOrDefaultAsync(item => item.OperationId == operation, ct);
        if (fact is null) return null;
        var kind = lines is null ? InboundFactKind.Approved : InboundFactKind.Revised;
        if (fact.OrderId != orderId || fact.ActorId != actor || fact.Kind != kind)
            return new(InboundWriteDecision.Conflict);
        if (lines is not null)
        {
            var original = fact.Revision();
            if (original.ExpectedVersion != version || !original.Lines.SequenceEqual(lines))
                return new(InboundWriteDecision.Conflict);
        }
        return new(InboundWriteDecision.Replayed, orderId, fact.AfterVersion);
    }
}
