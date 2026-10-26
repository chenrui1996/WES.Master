using System.Data.Common;
using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;
using MySql.Data.MySqlClient;
using Wes.Platform.Features.Directory;
using Wes.Platform.Infrastructure.Messaging;
using Wes.Platform.Infrastructure.Persistence;

[assembly: InternalsVisibleTo("Wes.Persistence.Tests")]

namespace Wes.Platform;

/// <summary>Counts returned only after both tenant-local retention batches commit together.</summary>
public sealed record RetentionMaintenanceResult(int DirectoryCount, int InboxCount);

/// <summary>Safe database failure classification, deliberately excluding provider details and inner exceptions.</summary>
public sealed class PlatformRetentionDatabaseException(bool transient)
    : Exception("Platform retention database operation failed.")
{
    /// <summary>Whether a later scheduled tick may retry with a fresh owner.</summary>
    public bool IsTransient { get; } = transient;
}

internal static class PlatformRetentionMaintenance
{
    internal static async Task<RetentionMaintenanceResult> RemoveExpiredAsync(PlatformDbContext database,
        TimeProvider clock, int limit, CancellationToken cancellationToken)
    {
        if (limit is < 1 or > 200) throw new ArgumentOutOfRangeException(nameof(limit));
        cancellationToken.ThrowIfCancellationRequested();
        if (database.ChangeTracker.Entries().Any() || database.Database.CurrentTransaction is not null)
            throw new InvalidOperationException("Platform retention requires a clean owner scope.");

        try
        {
            var now = clock.GetUtcNow();
            await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);
            var directory = await DirectoryCommandReceipts.RemoveExpiredAsync(database, now, limit, cancellationToken);
            var inbox = await PlatformInboxRetention.RemoveExpiredAsync(database, now, limit, cancellationToken);
            await database.SaveChangesAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            await transaction.CommitAsync(cancellationToken);
            return new(directory, inbox);
        }
        catch (DbUpdateConcurrencyException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw new PlatformRetentionDatabaseException(true);
        }
        catch (DbUpdateException error) when (error.InnerException is DbException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw SafeFailure((DbException)error.InnerException);
        }
        catch (DbException error)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw SafeFailure(error);
        }
    }

    private static PlatformRetentionDatabaseException SafeFailure(DbException error) =>
        new(error.IsTransient || error is MySqlException { Number: 1040 or 1042 or 1205 or 1213 or 2006 or 2013 });
}
