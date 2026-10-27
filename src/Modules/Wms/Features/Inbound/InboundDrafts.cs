namespace Wes.Wms.Features.Inbound;

/// <summary>Stable planned line identity and lossless positive decimal string; never an inventory receipt.</summary>
public sealed record InboundDraftLine(Guid Id, Guid ItemId, Guid UnitId, string Batch, int Precision, string Planned);

/// <summary>Original creation operation and ordered plan. Actor, tenant and session come only from the trusted subject.</summary>
public sealed record InboundDraftCreation(Guid OperationId, string Code, Guid WarehouseId, IReadOnlyList<InboundDraftLine> Lines);

/// <summary>Live authority for the new actor-owned draft and the actual WMS-owned parent warehouse creator.</summary>
public interface IInboundDraftAuthorization
{
    /// <summary>Current wms.inbound.write fields with the trusted actor as the proposed creator.</summary>
    Task<IReadOnlySet<string>> WriteFieldsAsync(InboundSubject subject, CancellationToken cancellationToken);
    /// <summary>Current wms.warehouses.read fields for the creator resolved by WMS, never supplied by a client.</summary>
    Task<IReadOnlySet<string>> WarehouseFieldsAsync(InboundSubject subject, Guid creator, CancellationToken cancellationToken);
}

/// <summary>Only Created or Replayed discloses a permanent document identity; no result implies approval or receipt.</summary>
public enum InboundDraftDecision
{
    /// <summary>A new draft committed.</summary>
    Created,
    /// <summary>The exact actor, operation and plan already committed.</summary>
    Replayed,
    /// <summary>Current complete write or parent-read authority is missing.</summary>
    Forbidden,
    /// <summary>The bounded input or exact quantity is invalid.</summary>
    Invalid,
    /// <summary>No parent warehouse with creation provenance exists in this tenant.</summary>
    NotFound,
    /// <summary>The operation or tenant business code belongs to a different creation.</summary>
    Conflict
}

/// <summary>Permanent ID-only creation receipt. Read the current detail separately for its current version.</summary>
public sealed record InboundDraftResult(InboundDraftDecision Decision, Guid? Id = null);
