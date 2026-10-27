using System.Collections.Immutable;
using System.Data.Common;
using System.Transactions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Wes.Wms.Domain;
using Wes.Wms.Features.Ledger;
using Wes.Wms.Infrastructure.Persistence;

namespace Wes.Wms.Features.MasterData;

/// <summary>Realtime bounds for the currently implemented creation-event contract only; no total, transport health or Inbox count.</summary>
public sealed record MasterDataEventQuery(int Offset = 0, int Limit = 25);
/// <summary>Fixed safe event metadata; no payload, actor, name, tenant identity or physical-control authority.</summary>
public sealed record MasterDataEventView(Guid EventId, string Owner, string ContractName, int SchemaVersion,
    Guid AggregateId, string AggregateVersion, Guid CorrelationId, DateTimeOffset OccurredAt, WmsOutboxStatus Status);
/// <summary>Only Success carries currently authorized rows; an authorized empty creator scope remains an empty success.</summary>
public sealed record MasterDataEventPage(MasterDataReadDecision Decision, ImmutableArray<MasterDataEventView> Rows, int Offset, int Limit);

internal sealed partial class MasterDataAccess
{
    private static readonly string[] CreationEventFields =
        ["EventId", "Owner", "ContractName", "SchemaVersion", "AggregateId", "AggregateVersion", "CorrelationId", "OccurredAt", "Status"];

    internal async Task<MasterDataEventPage> QueryCreationEventsAsync(MasterDataSubject subject, MasterDataEventQuery query, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(subject);
        ArgumentNullException.ThrowIfNull(query);
        ct.ThrowIfCancellationRequested();
        if (database.ChangeTracker.Entries().Any() || database.Database.CurrentTransaction is not null || Transaction.Current is not null)
            throw new InvalidOperationException("Creation-event diagnostics require a clean independent WMS owner.");
        if (subject.TenantId != database.TenantId || subject.ActorId == Guid.Empty || subject.SessionId == Guid.Empty)
            return Rejected(MasterDataReadDecision.Forbidden);
        if (query.Offset is < 0 or > 10000 || query.Limit is not (25 or 50 or 100))
            return Rejected(MasterDataReadDecision.Invalid);
        try
        {
            var authority = await _authorization.ReadCreatorsAsync(subject, "wms.messages.read", CreationEventFields, ct);
            ct.ThrowIfCancellationRequested();
            if (authority is null || authority.CreatorIds is null || authority.CreatorIds.Contains(Guid.Empty))
                throw new InvalidOperationException("Creation-event authority returned an invalid decision.");
            if (!authority.Authorized) return Rejected(MasterDataReadDecision.Forbidden);
            var changes = CreationFactsFor(authority.CreatorIds);
            var rows = await (from message in database.OutboxMessages.AsNoTracking()
                              join change in changes on new { message.TenantId, OperationId = message.EventId }
                                  equals new { change.TenantId, change.OperationId }
                              orderby message.OccurredAt, message.EventId
                              select new MasterDataEventView(message.EventId, message.Owner, message.ContractName, message.SchemaVersion,
                                  message.AggregateId, message.AggregateVersion, message.CorrelationId, message.OccurredAt, message.Status))
                .Skip(query.Offset).Take(query.Limit).ToListAsync(ct);
            ct.ThrowIfCancellationRequested();
            if (rows.Any(row => row.Status != WmsOutboxStatus.Pending || row.Owner != "Wms"
                || row.ContractName != "wms.master-data.created" || row.SchemaVersion != 1))
                throw new InvalidOperationException("Unsupported WMS creation-event contract or state.");
            return new(MasterDataReadDecision.Success, rows.ToImmutableArray(), query.Offset, query.Limit);
        }
        catch (DbException error)
        {
            ct.ThrowIfCancellationRequested();
            logger.LogError("WMS creation-event diagnostics failed {Code}", "DatabaseUnavailable");
            throw WmsDatabaseFailures.Safe(error);
        }

        MasterDataEventPage Rejected(MasterDataReadDecision decision)
        {
            logger.LogWarning("WMS creation-event diagnostics rejected {Code}", decision.ToString());
            return new(decision, [], query.Offset, query.Limit);
        }
    }
}
