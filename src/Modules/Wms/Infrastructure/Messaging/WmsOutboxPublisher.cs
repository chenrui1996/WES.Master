using System.Data.Common;
using System.Transactions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Wes.Wms.Domain;
using Wes.Wms.Infrastructure.Persistence;
using Wes.Wms.Messaging;

namespace Wes.Wms.Infrastructure.Messaging;

internal enum WmsOutboxPublishResult { Published, RetryScheduled, Quarantined, NotClaimed, Unconfirmed }

internal sealed class WmsOutboxPublisher(WmsDbContext database, IWmsOutboxTransport transport, TimeProvider clock,
    Func<double> jitter, ILogger<WmsOutboxPublisher> logger)
{
    internal async Task<IReadOnlyList<WmsOutboxPublishResult>> DispatchDueAsync(int limit, CancellationToken ct = default)
    {
        if (limit is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(limit));
        RequireClean(ct);
        try
        {
            var now = Now();
            var ids = await database.OutboxMessages.AsNoTracking().Where(message =>
                (message.Status == WmsOutboxStatus.Pending || message.Status == WmsOutboxStatus.Leased) && message.NextAttemptAt <= now)
                .OrderBy(message => message.NextAttemptAt).ThenBy(message => message.EventId)
                .Select(message => message.EventId).Take(limit).ToListAsync(ct);
            List<WmsOutboxPublishResult> results = [];
            foreach (var id in ids) results.Add(await PublishAsync(id, ct));
            return results;
        }
        catch (DbException error) { throw DatabaseFailure(error, ct); }
    }

    internal async Task<WmsOutboxPublishResult> PublishAsync(Guid eventId, CancellationToken ct = default)
    {
        if (eventId == Guid.Empty) throw new ArgumentException("WMS event identity is required.", nameof(eventId));
        RequireClean(ct);
        try
        {
            var message = await database.OutboxMessages.SingleOrDefaultAsync(item => item.EventId == eventId, ct);
            if (message is null) return WmsOutboxPublishResult.NotClaimed;
            var source = await database.MasterDataChanges.AsNoTracking().SingleAsync(item => item.OperationId == eventId, ct);
            if (!message.MatchesEnvelope(source))
            {
                logger.LogError("WMS publisher rejected {Code}", "UnsupportedStoredEnvelope");
                throw new InvalidOperationException("Unsupported WMS creation-event contract.");
            }
            var before = message.DeliveryVersion;
            var claimed = message.TryLease(Now());
            if (!claimed && before == message.DeliveryVersion) return WmsOutboxPublishResult.NotClaimed;
            if (!await SaveDeliveryAsync(ct)) return WmsOutboxPublishResult.NotClaimed;
            if (!claimed) return WmsOutboxPublishResult.Quarantined;
            ct.ThrowIfCancellationRequested();
            var lease = message.LeaseId!.Value;
            var sendAt = Now();
            if (!message.OwnsLease(lease, sendAt)) return Unconfirmed();
            if (message.BudgetExpired(sendAt))
            {
                message.Fail(lease, sendAt, false, 0);
                return await SaveDeliveryAsync(ct) ? WmsOutboxPublishResult.Quarantined : Unconfirmed();
            }
            var outcome = await transport.PublishAsync(new(message.EventId, message.ContractName, message.SchemaVersion,
                message.TenantId, message.Owner, message.AggregateId, message.AggregateVersion, message.CorrelationId,
                message.CausationId, message.OccurredAt, message.Payload), ct);
            ct.ThrowIfCancellationRequested();
            if (!Enum.IsDefined(outcome))
            {
                logger.LogError("WMS publisher rejected {Code}", "InvalidTransportOutcome");
                throw new InvalidOperationException("Unsupported WMS transport result.");
            }
            var now = Now();
            if (!message.OwnsLease(lease, now)) return Unconfirmed();
            if (outcome == WmsOutboxTransportOutcome.Acknowledged) message.Acknowledge(lease, now);
            else message.Fail(lease, now, outcome == WmsOutboxTransportOutcome.PermanentFailure, jitter());
            if (!await SaveDeliveryAsync(ct)) return Unconfirmed();
            return message.Status switch
            {
                WmsOutboxStatus.Published => WmsOutboxPublishResult.Published,
                WmsOutboxStatus.Pending => WmsOutboxPublishResult.RetryScheduled,
                WmsOutboxStatus.Quarantined => WmsOutboxPublishResult.Quarantined,
                _ => throw new InvalidOperationException("Unsupported WMS delivery transition.")
            };
        }
        catch (DbUpdateException error) when (error.InnerException is DbException provider) { throw DatabaseFailure(provider, ct); }
        catch (DbException error) { throw DatabaseFailure(error, ct); }
        finally { database.ChangeTracker.Clear(); }
    }

    private async Task<bool> SaveDeliveryAsync(CancellationToken ct)
    {
        try { await database.SaveOutboxDeliveryAsync(ct); return true; }
        catch (DbUpdateConcurrencyException) { ct.ThrowIfCancellationRequested(); return false; }
    }

    private WmsOutboxPublishResult Unconfirmed()
    {
        logger.LogWarning("WMS publisher result unconfirmed {Code}", "LeaseLost");
        return WmsOutboxPublishResult.Unconfirmed;
    }
    private Exception DatabaseFailure(DbException error, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        logger.LogError("WMS publisher failed {Code}", "DatabaseUnavailable");
        return WmsDatabaseFailures.Safe(error);
    }
    private void RequireClean(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (database.ChangeTracker.Entries().Any() || database.Database.CurrentTransaction is not null || Transaction.Current is not null)
            throw new InvalidOperationException("WMS publishing requires a clean independent owner.");
    }
    private DateTimeOffset Now()
    {
        var now = clock.GetUtcNow();
        if (now.Offset != TimeSpan.Zero || now.Year is < 1000 or > 9998)
            throw new InvalidOperationException("WMS publishing requires a supported UTC clock.");
        return now.AddTicks(-(now.Ticks % 10));
    }
}
