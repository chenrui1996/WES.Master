using System.Data;
using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Wes.Wms.Domain;
using Wes.Wms.Features.Ledger;

namespace Wes.Wms.Features.Inbound;

internal sealed partial class InboundTransportAccess
{
    internal Task<InboundTransportResult> ApplyCompletionAsync(InboundTransportCompletion completion, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(completion);
        // A caller cannot mutate its evidence collection while authentication or database I/O is suspended.
        completion = completion with { EvidenceRefs = Array.AsReadOnly(completion.EvidenceRefs?.ToArray() ?? []) };
        return OwnAsync(() => ApplyCoreAsync(completion, ct), ct);
    }

    private async Task<InboundTransportResult> ApplyCoreAsync(InboundTransportCompletion completion, CancellationToken ct)
    {
        if (completion.TenantId != database.TenantId) return new(InboundTransportDecision.Rejected);
        var body = JsonSerializer.Serialize(completion);
        var originalEvent = await database.InboundTransportInbox.AsNoTracking()
            .SingleOrDefaultAsync(row => row.EventId == completion.EventId, ct);
        if (originalEvent is not null && originalEvent.Body != body) return new(InboundTransportDecision.Conflict);
        if (new[] { completion.EventId, completion.CompletionId, completion.IntentId, completion.ExecutionId,
                completion.TransportId, completion.ContainerId, completion.SourceLocationId, completion.FinalLocationId }.Contains(Guid.Empty)
            || completion.TenantId != database.TenantId || completion.SchemaVersion != 1 || completion.Producer != "Orchestration"
            || completion.CompletedAt.Offset != TimeSpan.Zero || completion.CompletedAt == default
            || completion.CompletedAt > clock.GetUtcNow() || completion.EvidenceRefs.Count is < 1 or > 100
            || completion.EvidenceRefs.Any(value => string.IsNullOrWhiteSpace(value) || value.Length > 512
                || value != value.Trim() || value.Any(char.IsControl))
            || completion.EvidenceRefs.Distinct(StringComparer.Ordinal).Count() != completion.EvidenceRefs.Count)
            return new(InboundTransportDecision.Rejected);
        var intent = await database.InboundTransportIntents.AsNoTracking().SingleOrDefaultAsync(row => row.Id == completion.IntentId, ct);
        if (intent is null) return new(InboundTransportDecision.Rejected);
        var request = intent.Request();
        if (serviceActor == request.ActorId)
            throw new InvalidOperationException("Transport facts require a service actor distinct from the original operator.");
        if (completion.ExecutionId != intent.ExecutionId || completion.TransportId != intent.TransportId
            || completion.ContainerId != request.ContainerId || completion.SourceLocationId != request.SourceLocationId
            || completion.FinalLocationId != request.TargetLocationId || completion.CompletedAt < request.OccurredAt)
            return new(InboundTransportDecision.Rejected);
        var verified = Verification(await adapter.VerifyCompletionAsync(request, completion, ct));
        ct.ThrowIfCancellationRequested();
        if (verified is not null) return new(verified.Value);
        if (originalEvent is { Applied: true })
            return new(InboundTransportDecision.Replayed, intent.TransportId, originalEvent.PostingId);
        await using (var ingestion = await database.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct))
        {
            await ResourcesAsync(request, ct);
            await ParentFenceAsync(intent.OrderId, ct);
            var originals = await database.InboundTransportInbox.AsNoTracking().Where(row =>
                row.EventId == completion.EventId || row.IntentId == intent.Id || row.CompletionId == completion.CompletionId
                || row.ExecutionId == completion.ExecutionId).ToArrayAsync(ct);
            if (originals.Length > 1) return new(InboundTransportDecision.Conflict);
            var old = originals.SingleOrDefault();
            if (old is not null)
            {
                if (old.Body != body) return new(InboundTransportDecision.Conflict);
                if (old.Applied) return new(InboundTransportDecision.Replayed, intent.TransportId, old.PostingId);
            }
            else
            {
                database.InboundTransportInbox.Add(new(completion));
                await database.SaveChangesAsync(ct);
            }
            ct.ThrowIfCancellationRequested();
            await ingestion.CommitAsync(ct);
        }
        database.ChangeTracker.Clear();
        return await SynchronizeAsync(intent, completion, ct);
    }

    private async Task<InboundTransportResult> SynchronizeAsync(InboundTransportIntent intent,
        InboundTransportCompletion completion, CancellationToken ct)
    {
        var request = intent.Request();
        var source = await database.Balances.AsNoTracking().SingleOrDefaultAsync(row => row.Id == intent.SourceBalanceId, ct);
        var target = await (from registration in database.DimensionRegistrations
                            join balance in database.Balances on registration.BalanceId equals balance.Id
                            where balance.WarehouseId == request.WarehouseId && balance.LocationId == request.TargetLocationId
                                && balance.ContainerId == request.ContainerId && balance.ItemId == request.ItemId
                                && balance.Batch == request.Batch && balance.UnitId == request.UnitId && balance.Precision == request.Precision
                            select balance).AsNoTracking().SingleOrDefaultAsync(ct);
        if (source is null || target is null) return new(InboundTransportDecision.PendingSync, intent.TransportId);
        InboundDocument? document = null;
        InboundOrder? domain = null;
        InboundTransportInbox? inbox = null;
        var result = await new PostStock(database, clock).ExecuteComposedAsync(new(completion.EventId, serviceActor,
            request.CorrelationId, StockPostingKind.Transfer, intent.OrderId, request.LineId, intent.ExecutionId,
            source.Id, target.Id, decimal.Parse(request.Quantity, CultureInfo.InvariantCulture), source.Version, target.Version),
            async token =>
            {
                database.InboundDocuments.Attach(document!);
                var previous = document!.Version;
                domain!.ApplyArrival(intent.ExecutionId, completion.TransportId, request.ContainerId,
                    request.SourceLocationId, request.TargetLocationId);
                document.Advance(domain.State);
                var posting = database.ChangeTracker.Entries<StockPosting>().Single(row => row.State == EntityState.Added).Entity;
                database.InboundFacts.Add(new(document, completion.EventId, serviceActor, request.CorrelationId,
                    InboundFactKind.Arrived, clock.GetUtcNow(), posting: posting.Id,
                    transport: new(previous, intent.Id, intent.ExecutionId, completion.TransportId, request.ContainerId,
                        request.SourceLocationId, request.TargetLocationId, completion.EventId)));
                database.InboundTransportInbox.Attach(inbox!);
                inbox!.Apply(posting.Id);
                await Task.CompletedTask;
            }, ct, async token =>
            {
                await ResourcesAsync(request, token);
                await ParentFenceAsync(intent.OrderId, token);
                inbox = await database.InboundTransportInbox.AsNoTracking().SingleAsync(row => row.EventId == completion.EventId, token);
                if (inbox.Applied || inbox.Body != JsonSerializer.Serialize(completion) || !await WholeContainerAsync(intent, token)) return false;
                document = await database.InboundDocuments.AsNoTracking().SingleAsync(row => row.Id == intent.OrderId, token);
                domain = document.Restore(await HistoryAsync(document.Id, token));
                return domain.Receipts.Any(row => row.Id == intent.ExecutionId && !row.Arrived && row.TransportId == intent.TransportId);
            });
        database.ChangeTracker.Clear();
        if (result.Decision == StockPostingDecision.Posted)
            return new(InboundTransportDecision.Applied, intent.TransportId, result.PostingId);
        var original = await database.InboundTransportInbox.AsNoTracking().SingleAsync(row => row.EventId == completion.EventId, ct);
        if (original.Applied) return new(InboundTransportDecision.Replayed, intent.TransportId, original.PostingId);
        return result.Decision switch
        {
            StockPostingDecision.SourceRejected or StockPostingDecision.VersionConflict or StockPostingDecision.NotFound
                or StockPostingDecision.ProtectedStock or StockPostingDecision.InsufficientStock
                => new(InboundTransportDecision.PendingSync, intent.TransportId),
            StockPostingDecision.IdempotencyConflict => new(InboundTransportDecision.Conflict),
            _ => throw new InvalidOperationException("Final transfer returned an unsupported synchronization outcome.")
        };
    }
}
