namespace Wes.Wms.Features.Inbound;

/// <summary>Fixed WMS-owned versioned transport envelope; retries retain every original field.</summary>
public sealed record InboundTransportRequest(Guid EventId, int SchemaVersion, Guid TenantId, Guid IntentId,
    Guid DocumentId, Guid LineId, Guid ExecutionId, Guid WarehouseId, Guid ContainerId, Guid ItemId,
    string Batch, Guid SourceLocationId, Guid TargetLocationId, string Quantity, Guid UnitId, int Precision,
    string PolicyVersion, Guid ActorId, Guid CorrelationId, Guid CausationId, DateTimeOffset OccurredAt);

/// <summary>Explicit authenticated adapter outcome, never inferred from a supplied producer string.</summary>
public enum InboundTransportVerification
{
    /// <summary>The trusted adapter authenticated the complete original contract.</summary>
    Verified,
    /// <summary>The trusted adapter rejected the contract.</summary>
    Rejected,
    /// <summary>The dependency cannot currently establish the outcome.</summary>
    Unavailable
}

/// <summary>Durably accepted logical transport, authenticated by the required adapter.</summary>
public sealed record InboundTransportAcceptance(InboundTransportVerification Verification, Guid TenantId,
    Guid IntentId, Guid ExecutionId, Guid TransportId, int SchemaVersion, string Producer);

/// <summary>Final immutable completion identity and physical evidence; it is not an operator command.</summary>
public sealed record InboundTransportCompletion(Guid EventId, Guid CompletionId, int SchemaVersion, string Producer,
    Guid TenantId, Guid IntentId, Guid ExecutionId, Guid TransportId, Guid ContainerId, Guid SourceLocationId,
    Guid FinalLocationId, IReadOnlyList<string> EvidenceRefs, DateTimeOffset CompletedAt);

/// <summary>Required trusted integration; no default implementation or physical dispatcher is supplied by WMS.</summary>
public interface IInboundTransportAdapter
{
    /// <summary>Accept or reconcile the original intent idempotently; authenticate the durable acceptance.</summary>
    Task<InboundTransportAcceptance> AcceptAsync(InboundTransportRequest request, CancellationToken cancellationToken);
    /// <summary>Authenticate producer, original complete intent, association and final physical evidence.</summary>
    Task<InboundTransportVerification> VerifyCompletionAsync(InboundTransportRequest request,
        InboundTransportCompletion completion, CancellationToken cancellationToken);
}

/// <summary>Explicit WMS synchronization outcome.</summary>
public enum InboundTransportDecision
{
    /// <summary>One authenticated association was committed.</summary>
    Accepted,
    /// <summary>The final inventory transaction was committed.</summary>
    Applied,
    /// <summary>The exact original durable result was recovered.</summary>
    Replayed,
    /// <summary>Trusted final evidence is retained but local synchronization needs repair.</summary>
    PendingSync,
    /// <summary>The requested execution has no public durable intent.</summary>
    NotFound,
    /// <summary>Whole-container or original protection prerequisites are not satisfied.</summary>
    Blocked,
    /// <summary>The trusted adapter rejected the input or its tuple is invalid.</summary>
    Rejected,
    /// <summary>A durable original identity has different semantics.</summary>
    Conflict,
    /// <summary>The trusted dependency is unavailable; no success was assumed.</summary>
    Unavailable
}

/// <summary>Durable transport or posting identity, when established.</summary>
public sealed record InboundTransportResult(InboundTransportDecision Decision, Guid? TransportId = null, Guid? PostingId = null);
