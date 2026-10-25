using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Wes.Platform.Domain;
using Wes.Platform.Infrastructure.Persistence;

namespace Wes.Platform.Infrastructure.Messaging;

/// <summary>Commits one consumer effect with its owner-local inbox receipt.</summary>
public sealed class PlatformInboxConsumer
{
    private static readonly JsonSerializerOptions EnvelopeOptions = new(JsonSerializerDefaults.Web);
    private readonly PlatformDbContext _database;
    private readonly TimeProvider _clock;

    /// <summary>Creates a consumer around a fresh, tenant-scoped owner context for one delivery.</summary>
    public PlatformInboxConsumer(PlatformDbContext database, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(clock);
        _database = database;
        _clock = clock;
    }

    /// <summary>
    /// Commits handler database changes with the receipt; competing deliveries may invoke the handler more than once,
    /// so it must stage owner database changes only and perform no external side effects.
    /// </summary>
    public async Task<PlatformInboxConsumeResult> ProcessAsync(
        PlatformOutboxEnvelope envelope,
        string consumer,
        Func<PlatformDbContext, PlatformOutboxEnvelope, CancellationToken, Task> apply,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ArgumentNullException.ThrowIfNull(apply);
        ValidateEnvelope(envelope, consumer);
        if (envelope.TenantId != _database.TenantId)
        {
            throw new InvalidOperationException("An inbox event cannot cross the owner tenant boundary.");
        }
        if (_database.ChangeTracker.Entries().Any())
        {
            throw new InvalidOperationException("Inbox consumption requires a fresh owner context per delivery.");
        }

        var digest = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(envelope, EnvelopeOptions)))
            .ToLowerInvariant();
        await using var transaction = await _database.Database.BeginTransactionAsync(cancellationToken);
        var existing = await FindReceiptAsync(envelope, consumer, cancellationToken);
        if (existing is not null)
        {
            EnsureSameEnvelope(existing, envelope, digest);
            await transaction.CommitAsync(cancellationToken);
            return PlatformInboxConsumeResult.Duplicate;
        }

        _database.InboxReceipts.Add(new PlatformInboxReceipt(
            envelope.TenantId, consumer, envelope.EventId, envelope.ContractName,
            envelope.SchemaVersion, digest, _clock.GetUtcNow()));
        await apply(_database, envelope, cancellationToken);

        try
        {
            await _database.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            await transaction.RollbackAsync(CancellationToken.None);
            _database.ChangeTracker.Clear();
            var committedReceipt = await FindReceiptAsync(envelope, consumer, cancellationToken);
            if (committedReceipt is null)
            {
                throw;
            }

            EnsureSameEnvelope(committedReceipt, envelope, digest);
            return PlatformInboxConsumeResult.Duplicate;
        }

        await transaction.CommitAsync(cancellationToken);
        return PlatformInboxConsumeResult.Processed;
    }

    private Task<PlatformInboxReceipt?> FindReceiptAsync(
        PlatformOutboxEnvelope envelope, string consumer, CancellationToken cancellationToken) =>
        _database.InboxReceipts.AsNoTracking().SingleOrDefaultAsync(
            item => item.EventId == envelope.EventId && item.Consumer == consumer,
            cancellationToken);

    private static void EnsureSameEnvelope(
        PlatformInboxReceipt receipt, PlatformOutboxEnvelope envelope, string digest)
    {
        if (!receipt.Matches(envelope.ContractName, envelope.SchemaVersion, digest))
        {
            throw new InvalidOperationException("An event identity was reused with a different envelope.");
        }
    }

    private static void ValidateEnvelope(PlatformOutboxEnvelope envelope, string consumer)
    {
        if (envelope.TenantId == Guid.Empty || envelope.EventId == Guid.Empty
            || string.IsNullOrWhiteSpace(envelope.ContractName) || envelope.ContractName.Length > 128
            || envelope.SchemaVersion < 1 || string.IsNullOrWhiteSpace(envelope.Owner)
            || envelope.AggregateId == Guid.Empty || string.IsNullOrWhiteSpace(envelope.AggregateVersion)
            || envelope.CorrelationId == Guid.Empty || string.IsNullOrWhiteSpace(envelope.Payload))
        {
            throw new ArgumentException("The event envelope is incomplete.", nameof(envelope));
        }

        if (string.IsNullOrEmpty(consumer) || consumer.Length > 128
            || consumer.Any(character => character is not (>= 'a' and <= 'z')
                and not (>= '0' and <= '9') and not '.' and not '-' and not '_'))
        {
            throw new ArgumentException("Consumer identity must use lowercase ASCII letters, digits, dot, dash or underscore.",
                nameof(consumer));
        }
    }
}

/// <summary>Result of an owner-local inbox delivery.</summary>
public enum PlatformInboxConsumeResult
{
    /// <summary>Handler effects and inbox receipt committed together.</summary>
    Processed,
    /// <summary>The same consumer already committed this unchanged event.</summary>
    Duplicate
}
