using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using MySql.Data.MySqlClient;
using Wes.Platform.Domain;
using Wes.Platform.Features.Authorization;
using Wes.Platform.Features.Directory;
using Wes.Platform.Features.Tenancy;
using Wes.Platform.Infrastructure.Persistence;

namespace Wes.Platform.Features.Messaging;

/// <summary>Explicit outcomes for an authorized operator replay command.</summary>
public enum OutboxReplayDecision
{
    /// <summary>The current authenticated actor lacks the required replay grant.</summary>
    Forbidden,
    /// <summary>No event exists in the current trusted owner scope.</summary>
    NotFound,
    /// <summary>The expected attempt count or current event state no longer matches.</summary>
    Conflict,
    /// <summary>The event is not an eligible quarantined Platform audit event.</summary>
    NotReplayable,
    /// <summary>The idempotency key was previously used for a different replay request.</summary>
    IdempotencyConflict,
    /// <summary>The replay was committed or an exact idempotent retry was recognized.</summary>
    Replayed
}

/// <summary>Safe replay result containing no payload or operator-supplied reason.</summary>
public sealed record OutboxReplayResult(OutboxReplayDecision Decision, int? Attempts = null);

/// <summary>Current-authorization-gated, owner-local replay of quarantined Platform audit events.</summary>
public sealed class ReplayOutboxMessage(PlatformDbContext database, TimeProvider clock)
{
    /// <summary>Replays a quarantined event with a tenant-scoped idempotency key and expected-attempt precondition.</summary>
    public async Task<OutboxReplayResult> HandleAsync(
        TenantContext subject,
        DirectoryOperation operation,
        Guid eventId,
        int expectedAttempts,
        string reason,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(subject);
        if (operation.OperationId == Guid.Empty)
        {
            throw new ArgumentException("Replay requires an idempotency operation key.", nameof(operation));
        }

        if (eventId == Guid.Empty)
        {
            throw new ArgumentException("Replay requires an event identity.", nameof(eventId));
        }

        if (expectedAttempts < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(expectedAttempts));
        }

        reason = reason?.Trim() ?? "";
        if (reason.Length is < 3 or > 256 || reason.Any(char.IsControl))
        {
            throw new ArgumentException("Replay reason must contain 3 to 256 printable characters.", nameof(reason));
        }

        if (database.ChangeTracker.Entries().Any() || database.Database.CurrentTransaction is not null
            || System.Transactions.Transaction.Current is not null)
        {
            throw new InvalidOperationException("Outbox replay requires a clean independent unit of work.");
        }

        var fields = await new CurrentAuthorization(database, clock).VisibleFieldsAsync(
            subject, "platform.messages.replay",
            new ResourceScope(database.TenantId, Guid.Empty, Guid.Empty), cancellationToken);
        if (!fields.Contains(nameof(PlatformOutboxMessage.EventId))
            || !fields.Contains(nameof(PlatformOutboxMessage.Status))
            || !fields.Contains(nameof(PlatformOutboxMessage.Attempts))
            || !fields.Contains(nameof(PlatformOutboxReplayReceipt.Reason)))
        {
            return new OutboxReplayResult(OutboxReplayDecision.Forbidden);
        }

        var fingerprint = Fingerprint(subject.ActorId, eventId, expectedAttempts, reason);
        var existing = await FindReceiptAsync(operation.OperationId, cancellationToken);
        if (existing is not null)
        {
            return existing.Matches(subject.ActorId, eventId, fingerprint)
                ? new OutboxReplayResult(OutboxReplayDecision.Replayed)
                : new OutboxReplayResult(OutboxReplayDecision.IdempotencyConflict);
        }

        try
        {
            await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);
            var message = await database.OutboxMessages.SingleOrDefaultAsync(item => item.EventId == eventId, cancellationToken);
            if (message is null)
            {
                return new OutboxReplayResult(OutboxReplayDecision.NotFound);
            }

            if (message.Status != OutboxMessageStatus.Quarantined || message.Owner != "Platform"
                || message.ContractName != "platform.audit" || message.SchemaVersion != 1)
            {
                return new OutboxReplayResult(OutboxReplayDecision.NotReplayable);
            }

            if (message.Attempts != expectedAttempts)
            {
                return new OutboxReplayResult(OutboxReplayDecision.Conflict, message.Attempts);
            }

            var requestedAt = clock.GetUtcNow();
            message.ReplayAfterQuarantine(expectedAttempts, requestedAt);
            await database.SaveChangesAsync(cancellationToken);
            database.OutboxReplayReceipts.Add(new PlatformOutboxReplayReceipt(
                database.TenantId, operation.OperationId, eventId, subject.ActorId,
                reason, expectedAttempts, fingerprint, requestedAt));
            await database.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new OutboxReplayResult(OutboxReplayDecision.Replayed, message.Attempts);
        }
        catch (DbUpdateConcurrencyException)
        {
            return await ResolveConcurrentResultAsync(operation.OperationId, subject.ActorId, eventId, fingerprint,
                cancellationToken);
        }
        catch (DbUpdateException exception) when (exception.InnerException is MySqlException { Number: 1062 })
        {
            return await ResolveConcurrentResultAsync(operation.OperationId, subject.ActorId, eventId, fingerprint,
                cancellationToken);
        }
    }

    private Task<PlatformOutboxReplayReceipt?> FindReceiptAsync(Guid operationId, CancellationToken cancellationToken) =>
        database.OutboxReplayReceipts.AsNoTracking()
            .SingleOrDefaultAsync(receipt => receipt.OperationId == operationId, cancellationToken);

    private async Task<OutboxReplayResult> ResolveConcurrentResultAsync(
        Guid operationId, Guid actorId, Guid eventId, string fingerprint, CancellationToken cancellationToken)
    {
        database.ChangeTracker.Clear();
        var committed = await FindReceiptAsync(operationId, cancellationToken);
        if (committed is not null)
        {
            return committed.Matches(actorId, eventId, fingerprint)
                ? new OutboxReplayResult(OutboxReplayDecision.Replayed)
                : new OutboxReplayResult(OutboxReplayDecision.IdempotencyConflict);
        }

        return new OutboxReplayResult(OutboxReplayDecision.Conflict);
    }

    private static string Fingerprint(Guid actorId, Guid eventId, int expectedAttempts, string reason)
    {
        var canonical = JsonSerializer.Serialize(new ReplayFingerprint(actorId, eventId, expectedAttempts, reason));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }

    private sealed record ReplayFingerprint(Guid ActorId, Guid EventId, int ExpectedAttempts, string Reason);
}
