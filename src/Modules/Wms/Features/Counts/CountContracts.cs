namespace Wes.Wms.Features.Counts;

/// <summary>Finite outcomes; replay returns only the original plan identity and version.</summary>
public enum CountDecision
{
    /// <summary>New operation committed.</summary>
    Committed,
    /// <summary>Exact original operation recovered.</summary>
    Replayed,
    /// <summary>Malformed input.</summary>
    Invalid,
    /// <summary>Current field or creator authority denied.</summary>
    Forbidden,
    /// <summary>Tenant-owned resource absent.</summary>
    NotFound,
    /// <summary>Version, tuple or lifecycle conflict.</summary>
    Conflict,
    /// <summary>Active inventory protection or snapshot conflict.</summary>
    Blocked,
    /// <summary>Location schema rejected.</summary>
    SchemaRejected,
    /// <summary>Location schema unavailable.</summary>
    SchemaUnavailable
}

/// <summary>Permanent minimal write receipt.</summary>
public sealed record CountResult(CountDecision Decision, Guid? Id = null, string? Version = null);
/// <summary>One fixed lawful location, without client-selected lines.</summary>
public sealed record CountCreateRequest(string Code, Guid WarehouseId, Guid LocationId);
/// <summary>Expected current plan version for a fresh snapshot.</summary>
public sealed record CountStartRequest(string ExpectedVersion);
/// <summary>Exact scanned identities and nonnegative base-unit quantity; no book input.</summary>
public sealed record CountRecordRequest(string ExpectedVersion, Guid LineId, string Quantity, Guid LocationId,
    Guid ItemId, Guid ContainerId, string Reason);
/// <summary>Reasoned explicit approval or invalidation of the current generation.</summary>
public sealed record CountStateRequest(string ExpectedVersion, string Reason);
/// <summary>Blind line; intentionally cannot carry book quantity, difference or balance version.</summary>
public sealed record CountLineView(Guid Id, Guid ItemId, Guid UnitId, string Batch, int Precision,
    Guid ContainerId, string? Quantity, Guid? CountedBy, DateTimeOffset? CountedAt);
/// <summary>Independent authorized review line, never returned by blind GET.</summary>
public sealed record CountReviewLineView(Guid Id, Guid ItemId, Guid UnitId, string Batch, int Precision,
    Guid ContainerId, string? Quantity, Guid? CountedBy, DateTimeOffset? CountedAt, string BookQuantity, string? Difference);
/// <summary>Complete blind generation projection; reminder does not release the durable lock.</summary>
public sealed record CountView(Guid Id, string Code, Guid WarehouseId, Guid LocationId, int State, string Version,
    DateTimeOffset CreatedAt, DateTimeOffset? SnapshotAt, Guid? LockOwnerId, bool ReminderDue, string Policy,
    IReadOnlyList<CountLineView> Lines);
/// <summary>Separately authorized private review projection.</summary>
public sealed record CountReviewView(Guid Id, string Code, Guid WarehouseId, Guid LocationId, int State, string Version,
    DateTimeOffset CreatedAt, DateTimeOffset? SnapshotAt, Guid? LockOwnerId, bool ReminderDue, string Policy,
    IReadOnlyList<CountReviewLineView> Lines);
/// <summary>Read decision and optional blind record.</summary>
public sealed record CountReadResult(CountDecision Decision, CountView? Record = null);
/// <summary>Read decision and optional private review record.</summary>
public sealed record CountReviewResult(CountDecision Decision, CountReviewView? Record = null);
