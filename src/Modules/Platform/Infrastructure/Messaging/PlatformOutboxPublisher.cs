using Microsoft.EntityFrameworkCore;
using Wes.Platform.Domain;
using Wes.Platform.Infrastructure.Persistence;

namespace Wes.Platform.Infrastructure.Messaging;

/// <summary>Publishes one explicitly selected Platform event with a durable owner-local lease.</summary>
public sealed class PlatformOutboxPublisher
{
    private readonly PlatformDbContext _database;
    private readonly IPlatformOutboxTransport _transport;
    private readonly TimeProvider _clock;
    private readonly TimeSpan _leaseDuration;

    /// <summary>Creates a publisher for one trusted tenant-scoped owner context.</summary>
    public PlatformOutboxPublisher(PlatformDbContext database, IPlatformOutboxTransport transport,
        TimeProvider clock, TimeSpan leaseDuration)
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

    /// <summary>Claims and attempts one event; failures remain durable for later retry or operator recovery.</summary>
    public async Task<PlatformOutboxPublishResult> PublishAsync(
        Guid eventId, CancellationToken cancellationToken = default)
    {
        if (eventId == Guid.Empty)
        {
            throw new ArgumentException("An outbox event identity is required.", nameof(eventId));
        }

        var leaseId = Guid.NewGuid();
        var leasedAt = _clock.GetUtcNow();
        var leaseStore = new PlatformOutboxLeaseStore(_database);
        if (!await leaseStore.TryLeaseAsync(eventId, leaseId, leasedAt, _leaseDuration, cancellationToken))
        {
            return PlatformOutboxPublishResult.NotClaimed;
        }

        var message = await _database.OutboxMessages.SingleAsync(
            item => item.EventId == eventId, cancellationToken);
        var envelope = new PlatformOutboxEnvelope(
            message.TenantId,
            message.EventId,
            message.ContractName,
            message.SchemaVersion,
            message.Owner,
            message.AggregateId,
            message.AggregateVersion,
            message.CorrelationId,
            message.CausationId,
            message.OccurredAt,
            message.Payload);
        var outcome = await _transport.PublishAsync(envelope, cancellationToken);
        var completedAt = _clock.GetUtcNow();

        switch (outcome)
        {
            case PlatformOutboxTransportOutcome.Acknowledged:
                message.MarkPublished(leaseId, completedAt);
                break;
            case PlatformOutboxTransportOutcome.TemporaryFailure:
                message.RecordFailure(leaseId, completedAt, OutboxFailureCategory.BrokerUnavailable);
                break;
            case PlatformOutboxTransportOutcome.PermanentFailure:
                message.RecordFailure(leaseId, completedAt, OutboxFailureCategory.InvalidContract);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(outcome), outcome, "Unsupported transport outcome.");
        }

        await _database.SaveChangesAsync(cancellationToken);
        return message.Status switch
        {
            OutboxMessageStatus.Published => PlatformOutboxPublishResult.Published,
            OutboxMessageStatus.Pending => PlatformOutboxPublishResult.RetryScheduled,
            OutboxMessageStatus.Quarantined => PlatformOutboxPublishResult.Quarantined,
            _ => throw new InvalidOperationException("Outbox publisher completed without a terminal delivery transition.")
        };
    }
}

/// <summary>Outcome of one publisher attempt, not a claim about downstream business completion.</summary>
public enum PlatformOutboxPublishResult
{
    /// <summary>No eligible message was leased.</summary>
    NotClaimed,
    /// <summary>The transport acknowledged and the owner persisted that acknowledgement.</summary>
    Published,
    /// <summary>A transient failure remains pending on the bounded retry schedule.</summary>
    RetryScheduled,
    /// <summary>A permanent failure or exhausted retry budget is retained for operator recovery.</summary>
    Quarantined
}
