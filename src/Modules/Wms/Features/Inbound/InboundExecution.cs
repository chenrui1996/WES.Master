namespace Wes.Wms.Features.Inbound;

/// <summary>Only committed/replayed outcomes disclose the permanent original result identity and order version.</summary>
public enum InboundExecutionDecision
{
    /// <summary>The source fact and any required ledger effects committed atomically.</summary>
    Committed,
    /// <summary>The exact original operation committed earlier; no new effect occurred.</summary>
    Replayed,
    /// <summary>The input or legal resource/plan tuple is invalid.</summary>
    Invalid,
    /// <summary>Current trusted subject or complete resource field authority is insufficient.</summary>
    Forbidden,
    /// <summary>A required same-tenant identity does not exist.</summary>
    NotFound,
    /// <summary>Original operation, execution identity, order state or version conflicts.</summary>
    Conflict,
    /// <summary>Operational state, ownership, occupancy or execution protection blocks a new action.</summary>
    Blocked,
    /// <summary>The pinned location tuple was rejected by its explicit schema adapter.</summary>
    SchemaRejected,
    /// <summary>The required schema adapter could not establish current validity.</summary>
    SchemaUnavailable
}
/// <summary>Original operation receipt, not a current order, balance or physical-position projection.</summary>
public sealed record InboundExecutionResult(InboundExecutionDecision Decision, Guid? Id = null, string? Version = null);
/// <summary>Exact original draft version; actor/session/correlation are supplied by the trusted transport adapter.</summary>
public sealed record InboundApproval(Guid OperationId, Guid CorrelationId, string ExpectedVersion);
/// <summary>Explicit online receipt against a registered dimension; item/unit/precision/batch are derived from the legal plan.</summary>
public sealed record InboundReceiptRegistration(Guid OperationId, Guid CorrelationId, string ExpectedVersion, string ExpectedBalanceVersion,
    Guid LineId, Guid ExecutionId, string Quantity, Guid ContainerId, Guid ReceiptLocationId, Guid TargetLocationId, Guid BalanceId);
