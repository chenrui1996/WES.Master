namespace Wes.Platform.Messaging;

/// <summary>Stable wire envelope projected from an owner-local outbox row.</summary>
public sealed record PlatformOutboxEnvelope(
    Guid TenantId,
    Guid EventId,
    string ContractName,
    int SchemaVersion,
    string Owner,
    Guid AggregateId,
    string AggregateVersion,
    Guid CorrelationId,
    Guid? CausationId,
    DateTimeOffset OccurredAt,
    string Payload);

/// <summary>Finite transport result; exception text and broker payloads are never persisted.</summary>
public enum PlatformOutboxTransportOutcome
{
    /// <summary>The broker acknowledged the event.</summary>
    Acknowledged,
    /// <summary>A temporary broker/transport failure may be retried.</summary>
    TemporaryFailure,
    /// <summary>A permanent contract rejection must be quarantined.</summary>
    PermanentFailure
}

/// <summary>Transport boundary for the Platform owner publisher; no broker is enabled by this contract alone.</summary>
public interface IPlatformOutboxTransport
{
    /// <summary>Publishes a stable envelope and returns a finite outcome.</summary>
    Task<PlatformOutboxTransportOutcome> PublishAsync(
        PlatformOutboxEnvelope envelope,
        CancellationToken cancellationToken);
}
