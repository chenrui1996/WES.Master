using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Wes.Wms.Features.Ledger;
using Wes.Wms.Infrastructure.Persistence;

namespace Wes.Wms.Features.Inbound;

internal sealed class InboundRevisionAccess(WmsDbContext database, IInboundRevisionAuthorization authorization,
    TimeProvider clock, ILogger<InboundRevisionAccess> logger)
{
    internal async Task<InboundRevisionResult> ReviseAsync(InboundSubject subject, Guid id, InboundDraftRevision request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(subject);
        ArgumentNullException.ThrowIfNull(request);
        ct.ThrowIfCancellationRequested();
        if (database.ChangeTracker.Entries().Any() || database.Database.CurrentTransaction is not null
            || System.Transactions.Transaction.Current is not null)
            throw new InvalidOperationException("Draft revision requires a fresh independent WMS owner.");
        if (subject.TenantId != database.TenantId || subject.ActorId == Guid.Empty || subject.SessionId == Guid.Empty)
            return Rejected(InboundRevisionDecision.Forbidden);
        var lines = InboundDraftAccess.ParseLines(request.Lines);
        if (id == Guid.Empty || request.OperationId == Guid.Empty || lines is null
            || !Guid.TryParseExact(request.ExpectedVersion, "D", out var version) || version == Guid.Empty)
            return Rejected(InboundRevisionDecision.Invalid);
        try
        {
            var creator = await database.InboundDocuments.AsNoTracking().Where(item => item.Id == id)
                .Select(item => (Guid?)item.CreatorId).SingleOrDefaultAsync(ct);
            if (creator is null) return Rejected(InboundRevisionDecision.NotFound);
            if (!await AuthorizedAsync(subject, creator.Value, ct)) return Rejected(InboundRevisionDecision.Forbidden);
            var result = await new InboundWriter(database, clock).ReviseAsync(id, request.OperationId, subject.ActorId,
                Guid.NewGuid(), request.ExpectedVersion, lines, ct, token => AuthorizedAsync(subject, creator.Value, token));
            if (result.Decision == InboundWriteDecision.Replayed && !await AuthorizedAsync(subject, creator.Value, ct))
                return Rejected(InboundRevisionDecision.Forbidden);
            ct.ThrowIfCancellationRequested();
            return result.Decision switch
            {
                InboundWriteDecision.Committed => new(InboundRevisionDecision.Revised, result.Id, result.Version),
                InboundWriteDecision.Replayed => new(InboundRevisionDecision.Replayed, result.Id, result.Version),
                InboundWriteDecision.Forbidden => Rejected(InboundRevisionDecision.Forbidden),
                InboundWriteDecision.NotFound => Rejected(InboundRevisionDecision.NotFound),
                InboundWriteDecision.VersionConflict or InboundWriteDecision.Conflict => Rejected(InboundRevisionDecision.Conflict),
                _ => throw new InvalidOperationException("Unknown inbound revision outcome.")
            };
        }
        catch (DbException error)
        {
            ct.ThrowIfCancellationRequested();
            logger.LogError("WMS inbound revision failed {Code}", "DatabaseUnavailable");
            throw WmsDatabaseFailures.Safe(error);
        }
        catch (WmsDatabaseException)
        {
            logger.LogError("WMS inbound revision failed {Code}", "DatabaseUnavailable");
            throw;
        }
        catch (System.Text.Json.JsonException)
        {
            logger.LogError("WMS inbound revision rejected malformed history {Code}", "InboundHistoryInvalid");
            throw new InvalidOperationException("The persisted inbound history is invalid.");
        }
        catch (InvalidOperationException)
        {
            logger.LogError("WMS inbound revision rejected inconsistent state {Code}", "InboundRevisionInvalid");
            throw;
        }
    }

    private async Task<bool> AuthorizedAsync(InboundSubject subject, Guid creator, CancellationToken ct)
    {
        var write = await authorization.WriteFieldsAsync(subject, creator, ct);
        ct.ThrowIfCancellationRequested();
        var read = await authorization.ReadFieldsAsync(subject, creator, ct);
        ct.ThrowIfCancellationRequested();
        if (write is null || read is null) throw new InvalidOperationException("Revision authority returned no decision.");
        return InboundDraftAccess.WriteFields.All(write.ToHashSet(StringComparer.Ordinal).Contains)
            && InboundAccess.Fields.All(read.ToHashSet(StringComparer.Ordinal).Contains);
    }

    private InboundRevisionResult Rejected(InboundRevisionDecision decision)
    {
        logger.LogWarning("WMS inbound revision rejected {Code}", decision.ToString());
        return new(decision);
    }
}
