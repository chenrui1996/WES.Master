using System.Data.Common;
using System.Transactions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Wes.Wms.Domain;
using Wes.Wms.Features.Ledger;
using Wes.Wms.Infrastructure.Persistence;

namespace Wes.Wms.Features.MasterData;

/// <summary>Creator-scoped pending creation-event observation; no synthetic transport statuses, Inbox or business-effect counts.</summary>
public sealed record MasterDataEventSummary(string Owner, DateTimeOffset ObservedAt, long PendingCount);
/// <summary>Only a currently authorized Success contains a summary; denied requests never carry zero-shaped statistics.</summary>
public sealed record MasterDataEventSummaryResult(MasterDataReadDecision Decision, MasterDataEventSummary? Summary = null);

internal sealed partial class MasterDataAccess
{
    private static readonly string[] CreationEventCountFields = ["Owner", "ObservedAt", "PendingCount"];

    internal async Task<MasterDataEventSummaryResult> ReadCreationEventSummaryAsync(MasterDataSubject subject, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(subject);
        ct.ThrowIfCancellationRequested();
        if (database.ChangeTracker.Entries().Any() || database.Database.CurrentTransaction is not null || Transaction.Current is not null)
            throw new InvalidOperationException("Creation-event counts require a clean independent WMS owner.");
        if (subject.TenantId != database.TenantId || subject.ActorId == Guid.Empty || subject.SessionId == Guid.Empty)
            return Forbidden();
        try
        {
            var authority = await _authorization.ReadCreatorsAsync(subject, "wms.messages.read", CreationEventCountFields, ct);
            ct.ThrowIfCancellationRequested();
            if (authority is null || authority.CreatorIds is null || authority.CreatorIds.Contains(Guid.Empty))
                throw new InvalidOperationException("Creation-event count authority returned an invalid decision.");
            if (!authority.Authorized) return Forbidden();
            var changes = CreationFactsFor(authority.CreatorIds);
            var groups = await (from message in database.OutboxMessages.AsNoTracking()
                                join change in changes on new { message.TenantId, OperationId = message.EventId }
                                    equals new { change.TenantId, change.OperationId }
                                group message by new { message.Owner, message.ContractName, message.SchemaVersion, message.Status } into messages
                                select new { messages.Key, Count = messages.LongCount() }).ToListAsync(ct);
            ct.ThrowIfCancellationRequested();
            if (groups.Any(group => group.Key.Owner != "Wms" || group.Key.ContractName != "wms.master-data.created"
                || group.Key.SchemaVersion != 1 || group.Key.Status != WmsOutboxStatus.Pending || group.Count < 0))
            {
                logger.LogError("WMS creation-event counts failed {Code}", "UnsupportedStoredEnvelope");
                throw new InvalidOperationException("Unsupported WMS creation-event contract or state.");
            }
            return new(MasterDataReadDecision.Success,
                new("Wms", _clock.GetUtcNow(), groups.SingleOrDefault()?.Count ?? 0));
        }
        catch (DbException error)
        {
            ct.ThrowIfCancellationRequested();
            logger.LogError("WMS creation-event counts failed {Code}", "DatabaseUnavailable");
            throw WmsDatabaseFailures.Safe(error);
        }

        MasterDataEventSummaryResult Forbidden()
        {
            logger.LogWarning("WMS creation-event counts rejected {Code}", "Forbidden");
            return new(MasterDataReadDecision.Forbidden);
        }
    }
}
