namespace Wes.Wms.Features.Inbound;

/// <summary>Original revision identity, exact draft version and full ordered replacement plan; header identity remains immutable.</summary>
public sealed record InboundDraftRevision(Guid OperationId, string ExpectedVersion, IReadOnlyList<InboundDraftLine> Lines);

/// <summary>Current fields resolved for the real persisted inbound creator, not the current caller as a proposed creator.</summary>
public interface IInboundRevisionAuthorization
{
    /// <summary>Current wms.inbound.write fields for the creator supplied by WMS.</summary>
    Task<IReadOnlySet<string>> WriteFieldsAsync(InboundSubject subject, Guid creator, CancellationToken cancellationToken);
    /// <summary>Current complete detail fields for the same real creator.</summary>
    Task<IReadOnlySet<string>> ReadFieldsAsync(InboundSubject subject, Guid creator, CancellationToken cancellationToken);
}

/// <summary>Revision receipt outcome; no outcome grants approval, inventory movement or transport.</summary>
public enum InboundRevisionDecision
{
    /// <summary>The draft revision committed.</summary>
    Revised,
    /// <summary>The exact original revision already committed.</summary>
    Replayed,
    /// <summary>Current complete write or read fields are missing.</summary>
    Forbidden,
    /// <summary>The bounded input or exact quantity is invalid.</summary>
    Invalid,
    /// <summary>No document exists in this tenant.</summary>
    NotFound,
    /// <summary>The draft changed, was approved, or the operation belongs to another tuple.</summary>
    Conflict
}

/// <summary>Permanent original revision version, never a promise that this is the current document version.</summary>
public sealed record InboundRevisionResult(InboundRevisionDecision Decision, Guid? Id = null, string? Version = null);
