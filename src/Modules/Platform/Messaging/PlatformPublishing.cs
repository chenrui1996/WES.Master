using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Wes.Platform.Infrastructure.Messaging;
using Wes.Platform.Infrastructure.Persistence;

namespace Wes.Platform.Messaging;

/// <summary>Counts of durable delivery transitions, not downstream business results.</summary>
public sealed record PlatformPublishBatchResult(int Published, int RetryScheduled, int Quarantined, int NotClaimed);

/// <summary>Safe database failure without provider messages, credentials or an inner exception.</summary>
public sealed class PlatformPublisherDatabaseException(bool transient)
    : Exception("Platform publisher database operation failed.")
{
    /// <summary>A fresh owner may retry at a later scheduled tick.</summary>
    public bool IsTransient { get; } = transient;
}

internal static class PlatformPublishing
{
    internal static async Task<PlatformPublishBatchResult> DispatchAsync(PlatformDbContext database,
        IPlatformOutboxTransport transport, TimeProvider clock, int limit, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transport);
        if (limit is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(limit));
        cancellationToken.ThrowIfCancellationRequested();
        if (database.ChangeTracker.Entries().Any() || database.Database.CurrentTransaction is not null)
            throw new InvalidOperationException("Platform publishing requires a clean owner scope.");
        try
        {
            var results = await new PlatformOutboxDispatcher(database, transport, clock, TimeSpan.FromSeconds(90))
                .DispatchDueAsync(limit, cancellationToken);
            return new(
                results.Count(item => item == PlatformOutboxPublishResult.Published),
                results.Count(item => item == PlatformOutboxPublishResult.RetryScheduled),
                results.Count(item => item == PlatformOutboxPublishResult.Quarantined),
                results.Count(item => item == PlatformOutboxPublishResult.NotClaimed));
        }
        catch (DbUpdateConcurrencyException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw new PlatformPublisherDatabaseException(true);
        }
        catch (DbUpdateException error) when (error.InnerException is DbException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw new PlatformPublisherDatabaseException(PlatformDatabaseFailures.IsTransient((DbException)error.InnerException));
        }
        catch (DbException error)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw new PlatformPublisherDatabaseException(PlatformDatabaseFailures.IsTransient(error));
        }
    }
}
