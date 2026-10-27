using Microsoft.EntityFrameworkCore;
using Wes.Platform.Domain;
using Wes.Platform.Features.Authorization;
using Wes.Platform.Features.Tenancy;
using Wes.Platform.Infrastructure.Persistence;

namespace Wes.Platform.Features.Messaging;

/// <summary>Owner-local observations, not a shared transaction snapshot, broker health or business completion count.</summary>
public sealed record MessageSummary(string Owner, DateTimeOffset ObservedAt, long PendingCount, long LeasedCount,
    long PublishedCount, long QuarantinedCount, long InboxReceiptCount);

/// <summary>Denied diagnostics carry no counts; an authorized empty owner explicitly returns zero counts.</summary>
public sealed record MessageSummaryResult(bool Authorized, MessageSummary? Summary);

/// <summary>Current-session-authorized Platform status counts and retained consumer/event receipt count.</summary>
public sealed class QueryMessageSummary(PlatformDbContext database, TimeProvider clock)
{
    private static readonly string[] RequiredFields =
        ["Owner", "ObservedAt", "PendingCount", "LeasedCount", "PublishedCount", "QuarantinedCount", "InboxReceiptCount"];

    /// <summary>Reads tenant-wide owner counts with explicit field grants; cancellations and database failures propagate.</summary>
    public async Task<MessageSummaryResult> ReadAsync(TenantContext subject, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(subject);
        ct.ThrowIfCancellationRequested();
        if (database.ChangeTracker.Entries().Any() || database.Database.CurrentTransaction is not null
            || System.Transactions.Transaction.Current is not null)
            throw new InvalidOperationException("Message summary requires a clean independent owner scope.");
        var fields = await new CurrentAuthorization(database, clock).VisibleFieldsAsync(
            subject, "platform.messages.read", new ResourceScope(database.TenantId, Guid.Empty, Guid.Empty), ct);
        ct.ThrowIfCancellationRequested();
        if (!RequiredFields.All(fields.Contains)) return new(false, null);

        var grouped = await database.OutboxMessages.AsNoTracking()
            .GroupBy(message => message.Status)
            .Select(group => new { Status = group.Key, Count = group.LongCount() }).ToListAsync(ct);
        ct.ThrowIfCancellationRequested();
        if (grouped.Any(group => !Enum.IsDefined(group.Status) || group.Count < 0))
            throw new InvalidOperationException("The owner returned an invalid message status count.");
        var inbox = await database.InboxReceipts.LongCountAsync(ct);
        ct.ThrowIfCancellationRequested();
        long Count(OutboxMessageStatus status) => grouped.SingleOrDefault(group => group.Status == status)?.Count ?? 0;
        return new(true, new("Platform", clock.GetUtcNow(), Count(OutboxMessageStatus.Pending),
            Count(OutboxMessageStatus.Leased), Count(OutboxMessageStatus.Published),
            Count(OutboxMessageStatus.Quarantined), inbox));
    }
}
