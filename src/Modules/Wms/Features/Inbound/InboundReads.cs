using System.Collections.Immutable;

namespace Wes.Wms.Features.Inbound;

/// <summary>Validated principal identities, never client-supplied role or creator claims.</summary>
public sealed record InboundSubject(Guid TenantId, Guid ActorId, Guid SessionId);

/// <summary>Live resource authority resolving the WMS-owned creator through the authorization owner.</summary>
public interface IInboundAuthorization
{
    /// <summary>Revalidates the session and exact current wms.inbound.read fields for the real creator.</summary>
    Task<IReadOnlySet<string>> ReadFieldsAsync(InboundSubject subject, Guid creator, CancellationToken cancellationToken);
}

/// <summary>Public document progress; executing does not imply arrival or successful transport creation.</summary>
public enum InboundState
{
    /// <summary>Plan not yet approved.</summary>
    Draft,
    /// <summary>Approved plan without received units.</summary>
    Approved,
    /// <summary>Partially received or still awaiting putaway.</summary>
    Executing,
    /// <summary>All planned quantities received and all units arrived.</summary>
    Completed
}

/// <summary>Explicit read result; only Success discloses a document.</summary>
public enum InboundReadDecision
{
    /// <summary>Complete currently authorized projection.</summary>
    Success,
    /// <summary>Current subject or complete fields do not authorize disclosure.</summary>
    Forbidden,
    /// <summary>No document in the current tenant.</summary>
    NotFound,
    /// <summary>Invalid document identity.</summary>
    Invalid
}

/// <summary>Immutable plan with lossless six-place decimal strings and remaining quantities.</summary>
public sealed record InboundLineView(Guid Id, Guid ItemId, Guid UnitId, string Batch, int Precision,
    string Planned, string Received, string Remaining);
/// <summary>Real received unit; null transport is unknown/unlinked, never assumed successful.</summary>
public sealed record InboundReceiptView(Guid Id, Guid LineId, Guid ContainerId, Guid ReceiptLocationId,
    Guid TargetLocationId, string Quantity, bool RequiresTransport, bool Arrived, Guid? TransportId);
/// <summary>Exact document detail without private authorization, operation or persistence payloads.</summary>
public sealed record InboundView(Guid Id, string Code, Guid WarehouseId, InboundState State, string Version,
    DateTimeOffset CreatedAt, ImmutableArray<InboundLineView> Lines, ImmutableArray<InboundReceiptView> Receipts);
/// <summary>Rejected decisions carry no document projection.</summary>
public sealed record InboundReadResult(InboundReadDecision Decision, InboundView? Order = null);
