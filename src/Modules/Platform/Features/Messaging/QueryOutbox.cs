using System.Collections.Immutable;
using Microsoft.EntityFrameworkCore;
using Wes.Platform.Domain;
using Wes.Platform.Features.Authorization;
using Wes.Platform.Features.Tenancy;
using Wes.Platform.Infrastructure.Persistence;

namespace Wes.Platform.Features.Messaging;

/// <summary>Authorized Outbox metadata projection; payload bytes are never returned to the operator query.</summary>
public sealed record OutboxQueryRow(Guid EventId, string? ContractName, int? SchemaVersion, string? Owner,
    OutboxMessageStatus Status, int? Attempts, DateTimeOffset CreatedAt, DateTimeOffset? NextAttemptAt,
    DateTimeOffset? PublishedAt, string? LastErrorCategory);

/// <summary>An explicit denied outcome is distinct from an authorized empty page.</summary>
public sealed record OutboxQueryPage(bool Authorized, ImmutableArray<OutboxQueryRow> Rows);

/// <summary>Optional finite delivery status and half-open UTC creation bounds; omitted values preserve unfiltered reads.</summary>
public sealed record OutboxQueryFilter(int? Status = null, DateTimeOffset? CreatedFrom = null, DateTimeOffset? CreatedBefore = null)
{
    internal bool IsValid => Status is not (< 0 or > 3)
        && ValidTime(CreatedFrom) && ValidTime(CreatedBefore)
        && (!CreatedFrom.HasValue || !CreatedBefore.HasValue || CreatedFrom.Value < CreatedBefore.Value);

    private static bool ValidTime(DateTimeOffset? time) =>
        time is null || time.Value.Offset == TimeSpan.Zero && time.Value.Year >= 1000;
}

/// <summary>Owner-local, current-session-authorized Outbox diagnostics.</summary>
public sealed class QueryOutbox(PlatformDbContext database, TimeProvider clock)
{
    /// <summary>Reads a stable, bounded metadata page from the trusted tenant's Outbox.</summary>
    public async Task<OutboxQueryPage> ReadAsync(
        TenantContext subject, int offset = 0, int limit = 50, CancellationToken cancellationToken = default,
        OutboxQueryFilter? filter = null)
    {
        ArgumentNullException.ThrowIfNull(subject);
        cancellationToken.ThrowIfCancellationRequested();
        filter ??= new OutboxQueryFilter();
        if (!filter.IsValid)
        {
            throw new ArgumentException("Outbox filters require a known status and valid half-open UTC creation bounds.", nameof(filter));
        }
        if (offset < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(offset));
        }

        if (limit is < 1 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(limit));
        }

        if (database.ChangeTracker.Entries().Any() || database.Database.CurrentTransaction is not null
            || System.Transactions.Transaction.Current is not null)
        {
            throw new InvalidOperationException("Outbox diagnostics require a clean independent unit of work.");
        }

        var fields = await new CurrentAuthorization(database, clock).VisibleFieldsAsync(
            subject, "platform.messages.read", new ResourceScope(database.TenantId, Guid.Empty, Guid.Empty),
            cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (!fields.Contains(nameof(OutboxQueryRow.EventId))
            || !fields.Contains(nameof(OutboxQueryRow.Status))
            || !fields.Contains(nameof(OutboxQueryRow.CreatedAt))
            || !fields.Contains(nameof(OutboxQueryRow.Owner)))
        {
            return new OutboxQueryPage(false, []);
        }

        var showContract = fields.Contains(nameof(OutboxQueryRow.ContractName));
        var showSchema = fields.Contains(nameof(OutboxQueryRow.SchemaVersion));
        var showOwner = fields.Contains(nameof(OutboxQueryRow.Owner));
        var showAttempts = fields.Contains(nameof(OutboxQueryRow.Attempts));
        var showNextAttempt = fields.Contains(nameof(OutboxQueryRow.NextAttemptAt));
        var showPublished = fields.Contains(nameof(OutboxQueryRow.PublishedAt));
        var showLastError = fields.Contains(nameof(OutboxQueryRow.LastErrorCategory));
        var messages = database.OutboxMessages.AsNoTracking();
        if (filter.Status.HasValue)
        {
            var status = (OutboxMessageStatus)filter.Status.Value;
            messages = messages.Where(message => message.Status == status);
        }
        if (filter.CreatedFrom.HasValue) messages = messages.Where(message => message.CreatedAt >= filter.CreatedFrom.Value);
        if (filter.CreatedBefore.HasValue) messages = messages.Where(message => message.CreatedAt < filter.CreatedBefore.Value);
        var rows = await messages
            .OrderBy(message => message.CreatedAt).ThenBy(message => message.EventId)
            .Skip(offset).Take(limit)
            .Select(message => new OutboxQueryRow(
                message.EventId,
                showContract ? message.ContractName : null,
                showSchema ? message.SchemaVersion : null,
                showOwner ? message.Owner : null,
                message.Status,
                showAttempts ? message.Attempts : null,
                message.CreatedAt,
                showNextAttempt ? message.NextAttemptAt : null,
                showPublished ? message.PublishedAt : null,
                showLastError ? message.LastErrorCategory : null))
            .ToListAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return new OutboxQueryPage(true, rows.ToImmutableArray());
    }
}
