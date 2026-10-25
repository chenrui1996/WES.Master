using Microsoft.EntityFrameworkCore;
using Wes.Platform.Infrastructure.Persistence;

namespace Wes.Platform.Infrastructure.Messaging;

/// <summary>Bounded tenant-local expiry for completed Platform Inbox idempotency receipts.</summary>
public static class PlatformInboxRetention
{
    /// <summary>Completed receipts remain available for exactly 90 days.</summary>
    public static readonly TimeSpan Retention = TimeSpan.FromDays(90);

    /// <summary>Stages deletion of at most 200 receipts; the caller saves and commits or rolls back the owner transaction.</summary>
    public static async Task<int> RemoveExpiredAsync(
        PlatformDbContext database,
        DateTimeOffset now,
        int limit = 100,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(database);
        if (limit is < 1 or > 200)
            throw new ArgumentOutOfRangeException(nameof(limit), "Inbox retention batches must be between 1 and 200.");
        if (database.Database.CurrentTransaction is null)
            throw new InvalidOperationException("Inbox retention requires an explicit owner transaction.");

        var cutoff = now.ToUniversalTime() - Retention;
        var expired = await database.InboxReceipts
            .Where(receipt => receipt.ProcessedAt <= cutoff)
            .OrderBy(receipt => receipt.ProcessedAt)
            .ThenBy(receipt => receipt.Consumer)
            .ThenBy(receipt => receipt.EventId)
            .Take(limit)
            .ToListAsync(cancellationToken);

        database.StageInboxReceiptRetentionRemoval(expired, cutoff);
        return expired.Count;
    }
}
