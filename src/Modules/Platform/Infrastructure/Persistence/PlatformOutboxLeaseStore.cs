using Microsoft.EntityFrameworkCore;
using Wes.Platform.Domain;

namespace Wes.Platform.Infrastructure.Persistence;

/// <summary>Claims a single tenant-scoped outbox row using optimistic status concurrency.</summary>
public sealed class PlatformOutboxLeaseStore(PlatformDbContext database)
{
    /// <summary>Attempts an atomic claim; competing publishers receive false rather than sharing a lease.</summary>
    public async Task<bool> TryLeaseAsync(Guid eventId, Guid leaseId, DateTimeOffset now, TimeSpan duration,
        CancellationToken cancellationToken = default)
    {
        if (eventId == Guid.Empty)
        {
            throw new ArgumentException("An outbox event identity is required.", nameof(eventId));
        }

        var message = await database.OutboxMessages.SingleOrDefaultAsync(
            item => item.EventId == eventId, cancellationToken);
        if (message is null)
        {
            return false;
        }

        var acquired = message.TryAcquireLease(leaseId, now, duration);
        if (!acquired && message.Status != OutboxMessageStatus.Quarantined)
        {
            return false;
        }

        try
        {
            await database.SaveChangesAsync(cancellationToken);
            return acquired;
        }
        catch (DbUpdateConcurrencyException)
        {
            return false;
        }
    }
}
