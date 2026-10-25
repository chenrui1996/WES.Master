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

/// <summary>Owner-local, current-session-authorized Outbox diagnostics.</summary>
public sealed class QueryOutbox(PlatformDbContext database, TimeProvider clock)
{
    /// <summary>Reads a stable, bounded metadata page from the trusted tenant's Outbox.</summary>
    public async Task<OutboxQueryPage> ReadAsync(
        TenantContext subject, int offset = 0, int limit = 50, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(subject);
        cancellationToken.ThrowIfCancellationRequested();
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
        var rows = await database.OutboxMessages.AsNoTracking()
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
        return new OutboxQueryPage(true, rows.ToImmutableArray());
    }
}
