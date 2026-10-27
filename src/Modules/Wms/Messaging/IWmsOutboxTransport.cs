namespace Wes.Wms.Messaging;

/// <summary>Original immutable nonphysical creation envelope; retry never creates a new event identity.</summary>
public sealed record WmsOutboxEnvelope(Guid EventId, string ContractName, int SchemaVersion, Guid TenantId,
    string Owner, Guid AggregateId, string AggregateVersion, Guid CorrelationId, Guid? CausationId,
    DateTimeOffset OccurredAt, string Payload);

/// <summary>Explicit adapter result; an interruption or exception is not an acknowledgement.</summary>
public enum WmsOutboxTransportOutcome
{
    /// <summary>The transport positively acknowledged this stable event.</summary>
    Acknowledged,
    /// <summary>A nonphysical transient rejection may use the bounded retry policy.</summary>
    TemporaryFailure,
    /// <summary>A permanent contract or authorization rejection must not retry automatically.</summary>
    PermanentFailure
}

/// <summary>Deployment-supplied transport. No adapter is registered by default; implementations must honor cancellation and avoid raw payload/error logging.</summary>
public interface IWmsOutboxTransport
{
    /// <summary>Publish only after a durable lease; exceptions/cancellation retain an unconfirmed lease for later recovery.</summary>
    Task<WmsOutboxTransportOutcome> PublishAsync(WmsOutboxEnvelope envelope, CancellationToken cancellationToken);
}
