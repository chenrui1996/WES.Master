namespace Wes.Wms.Features.Transfers;

/// <summary>Stable transfer operation decision, independent of current posting progress.</summary>
public enum TransferDecision
{
    /// <summary>The original operation is durable.</summary>
    Committed,
    /// <summary>The exact original operation was already durable.</summary>
    Replayed,
    /// <summary>The request is invalid.</summary>
    Invalid,
    /// <summary>Current complete authority is insufficient.</summary>
    Forbidden,
    /// <summary>The tenant-scoped resource is absent.</summary>
    NotFound,
    /// <summary>The original tuple, state or version conflicts.</summary>
    Conflict,
    /// <summary>A current inventory or operational protection blocks this action.</summary>
    Blocked,
    /// <summary>The schema rejected the resource.</summary>
    SchemaRejected,
    /// <summary>The schema could not establish validity.</summary>
    SchemaUnavailable,
    /// <summary>No trusted transport completion contract is configured.</summary>
    Unsupported
}

/// <summary>Immutable minimal original receipt, not a current completion claim.</summary>
public sealed record TransferResult(TransferDecision Decision, Guid? Id = null, string? Version = null);
/// <summary>Trusted creation operation and explicit fixed balance plan.</summary>
public sealed record TransferDraftRequest(Guid OperationId, Guid CorrelationId, string Code, Guid WarehouseId,
    IReadOnlyList<TransferDraftLineRequest> Lines);
/// <summary>One indivisible whole-container execution unit.</summary>
public sealed record TransferDraftLineRequest(Guid Id, Guid SourceBalanceId, Guid TargetBalanceId, string Quantity, int Branch);
/// <summary>Complete current source and target preview versions.</summary>
public sealed record TransferApprovalRequest(string ExpectedVersion, IReadOnlyList<TransferBalanceVersionRequest> Balances);
/// <summary>Expected current balance version.</summary>
public sealed record TransferBalanceVersionRequest(Guid BalanceId, string ExpectedVersion);
/// <summary>One explicit execution-unit action.</summary>
public sealed record TransferLineRequest(string ExpectedVersion, Guid LineId);
/// <summary>Exact final manual arrival scans and whole-unit quantity.</summary>
public sealed record TransferArrivalRequest(string ExpectedVersion, Guid LineId, Guid SourceLocationId, Guid TargetLocationId,
    Guid ContainerId, string Quantity, string Reason);
/// <summary>Reasoned unknown execution status.</summary>
public sealed record TransferUnknownRequest(string ExpectedVersion, Guid LineId, string Reason);
/// <summary>Safe closure of only unstarted remainder.</summary>
public sealed record TransferClosureRequest(string ExpectedVersion, string Reason);
/// <summary>Current authorized read outcome.</summary>
public enum TransferReadDecision
{
    /// <summary>The complete current projection is authorized.</summary>
    Success,
    /// <summary>The identity or input is invalid.</summary>
    Invalid,
    /// <summary>Current complete read authority is absent.</summary>
    Forbidden,
    /// <summary>The tenant-scoped transfer does not exist.</summary>
    NotFound
}
/// <summary>Current read decision and complete record.</summary>
public sealed record TransferReadResult(TransferReadDecision Decision, TransferRecord? Record = null);
/// <summary>Current transfer projection; quantities are never summed across units.</summary>
public sealed record TransferRecord(Guid Id, string Code, Guid WarehouseId, int State, string Version,
    DateTimeOffset CreatedAt, IReadOnlyList<TransferLineRecord> Lines);
/// <summary>Current fixed execution tuple, exact progress and current preview balance versions.</summary>
public sealed record TransferLineRecord(Guid Id, Guid ExecutionId, Guid SourceBalanceId, Guid TargetBalanceId,
    Guid ItemId, Guid UnitId, string Batch, int Precision, Guid ContainerId, Guid SourceLocationId, Guid TargetLocationId,
    int Branch, string PlannedQuantity, string ReservedQuantity, string MovedQuantity, string ClosedQuantity,
    int ExecutionStatus, int PostingStatus, string SourceVersion, string TargetVersion);
