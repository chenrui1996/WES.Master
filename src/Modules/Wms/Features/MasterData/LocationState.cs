namespace Wes.Wms.Features.MasterData;

/// <summary>Original logical-state operation; pinned lineage and trusted identity cannot be supplied by the caller.</summary>
public sealed record LocationStateCommand(Guid OperationId, string ExpectedVersion, bool Enabled, string Reason);

/// <summary>Only Changed and Replayed disclose a permanent original receipt.</summary>
public enum LocationStateDecision
{
    /// <summary>The logical transition committed.</summary>
    Changed,
    /// <summary>The exact original operation committed previously.</summary>
    Replayed,
    /// <summary>Current subject, resource or complete fields do not authorize the operation.</summary>
    Forbidden,
    /// <summary>The bounded command or identities are invalid.</summary>
    Invalid,
    /// <summary>The location or its parent provenance is missing in this tenant.</summary>
    NotFound,
    /// <summary>Version, original tuple or target state conflicts.</summary>
    Conflict,
    /// <summary>The parent is disabled or location activity prevents this transition.</summary>
    Blocked,
    /// <summary>The pinned tuple is not valid for the configured schema.</summary>
    SchemaRejected,
    /// <summary>The schema adapter cannot establish evidence.</summary>
    SchemaUnavailable
}

/// <summary>The original state version, never a current-state, occupancy or physical-readiness claim.</summary>
public sealed record LocationStateResult(LocationStateDecision Decision, Guid? Id = null, string? Version = null);
