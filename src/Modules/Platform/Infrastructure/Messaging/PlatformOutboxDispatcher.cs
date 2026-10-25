using Microsoft.EntityFrameworkCore;
using Wes.Platform.Domain;
using Wes.Platform.Infrastructure.Persistence;

namespace Wes.Platform.Infrastructure.Messaging;

/// <summary>Scans one tenant owner's durable outbox and attempts a bounded batch sequentially.</summary>
public sealed class PlatformOutboxDispatcher
{
    private const int MaximumBatchSize = 100;
    private readonly PlatformDbContext _database;
    private readonly IPlatformOutboxTransport _transport;
    private readonly TimeProvider _clock;
    private readonly TimeSpan _leaseDuration;

    /// <summary>Creates a dispatcher for one trusted owner context and an explicitly configured transport.</summary>
    public PlatformOutboxDispatcher(
        PlatformDbContext database, IPlatformOutboxTransport transport, TimeProvider clock, TimeSpan leaseDuration)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(clock);
        if (leaseDuration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(leaseDuration));
        }

        _database = database;
        _transport = transport;
        _clock = clock;
        _leaseDuration = leaseDuration;
    }

    /// <summary>Publishes eligible events in a stable order, bounded to at most one hundred lease attempts.</summary>
    public async Task<IReadOnlyList<PlatformOutboxPublishResult>> DispatchDueAsync(
        int batchSize, CancellationToken cancellationToken = default)
    {
        if (batchSize is < 1 or > MaximumBatchSize)
        {
            throw new ArgumentOutOfRangeException(nameof(batchSize), "Outbox batches must contain between 1 and 100 events.");
        }

        var now = _clock.GetUtcNow();
        var eventIds = await _database.OutboxMessages.AsNoTracking()
            .Where(message =>
                (message.Status == OutboxMessageStatus.Pending && message.NextAttemptAt <= now)
                || (message.Status == OutboxMessageStatus.Leased
                    && message.LeaseUntil.HasValue && message.LeaseUntil <= now))
            .OrderBy(message => message.NextAttemptAt)
            .ThenBy(message => message.EventId)
            .Select(message => message.EventId)
            .Take(batchSize)
            .ToListAsync(cancellationToken);

        var publisher = new PlatformOutboxPublisher(_database, _transport, _clock, _leaseDuration);
        var results = new List<PlatformOutboxPublishResult>(eventIds.Count);
        foreach (var eventId in eventIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            results.Add(await publisher.PublishAsync(eventId, cancellationToken));
        }

        return results;
    }
}
