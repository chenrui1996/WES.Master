using System.Collections.Immutable;

namespace Wes.Wms.Features.Inbound;

/// <summary>Validated principal identities, never client-supplied role or creator claims.</summary>
public sealed record InboundSubject(Guid TenantId, Guid ActorId, Guid SessionId);

/// <summary>Live resource authority resolving the WMS-owned creator through the authorization owner.</summary>
public interface IInboundAuthorization
{
    /// <summary>Revalidates the session and exact current wms.inbound.read fields for the real creator.</summary>
    Task<IReadOnlySet<string>> ReadFieldsAsync(InboundSubject subject, Guid creator, CancellationToken cancellationToken);
    /// <summary>Resolves the real currently visible creators for all required fields before WMS pages its own data.</summary>
    Task<InboundCreatorAccess> ReadCreatorsAsync(InboundSubject subject, IReadOnlyCollection<string> requiredFields,
        CancellationToken cancellationToken);
}

/// <summary>Current full-projection authority; an allowed empty creator set is not a forbidden request.</summary>
public sealed record InboundCreatorAccess(bool Authorized, IReadOnlySet<Guid> CreatorIds);
/// <summary>Exact optional filters and realtime paging; not a fixed reconciliation snapshot.</summary>
public sealed record InboundQuery(int Offset = 0, int Limit = 25, string? Code = null, Guid? WarehouseId = null, InboundState? State = null);
/// <summary>Safe list metadata without private provenance or receipt payloads.</summary>
public sealed record InboundSummary(Guid Id, string Code, Guid WarehouseId, InboundState State, string Version, DateTimeOffset CreatedAt);
/// <summary>Only Success carries current authorized rows, with echoed explicit bounds and no hidden total.</summary>
public sealed record InboundPage(InboundReadDecision Decision, ImmutableArray<InboundSummary> Rows, int Offset, int Limit);

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
