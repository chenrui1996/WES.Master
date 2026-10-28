namespace Wes.Wms.Features.Outbound;

/// <summary>Stable outcome for a durable outbound operation.</summary>
public enum OutboundDecision
{
    /// <summary>The operation committed atomically.</summary>
    Committed,
    /// <summary>The exact original operation committed earlier.</summary>
    Replayed,
    /// <summary>The request is invalid.</summary>
    Invalid,
    /// <summary>Current complete authority is insufficient.</summary>
    Forbidden,
    /// <summary>The tenant-scoped order or resource does not exist.</summary>
    NotFound,
    /// <summary>The operation identity, state, version or quantity conflicts.</summary>
    Conflict,
    /// <summary>A current operational or stock-protection invariant blocks the operation.</summary>
    Blocked,
    /// <summary>The configured location schema explicitly rejected the legal-resource tuple.</summary>
    SchemaRejected,
    /// <summary>The location schema adapter could not establish current validity.</summary>
    SchemaUnavailable,
    /// <summary>The requested execution branch has no supported manual operation.</summary>
    Unsupported
}

/// <summary>Exact original operation receipt; it is not a projection of current order state.</summary>
public sealed record OutboundResult(OutboundDecision Decision, Guid? Id = null, string? Version = null);

/// <summary>Creation plan uses only legal source-balance identities and exact decimal strings.</summary>
public sealed record OutboundDraftRequest(Guid OperationId, Guid CorrelationId, string Code, Guid WarehouseId,
    IReadOnlyList<OutboundDraftLineRequest> Lines);

/// <summary>One fixed source balance, planned exact quantity and selected execution branch.</summary>
public sealed record OutboundDraftLineRequest(Guid Id, Guid BalanceId, string Quantity, int Branch);

/// <summary>Full expected balance-version set required to atomically reserve every planned line.</summary>
public sealed record OutboundApprovalRequest(string ExpectedVersion, IReadOnlyList<OutboundBalanceVersionRequest> Balances);
/// <summary>Expected source balance fence for one planned line.</summary>
public sealed record OutboundBalanceVersionRequest(Guid BalanceId, string ExpectedVersion);
/// <summary>Incremental picking quantities; shortage is reviewed separately and is never released implicitly.</summary>
public sealed record OutboundPickRequest(string ExpectedVersion, Guid LineId, string PickedQuantity,
    string ShortageQuantity, string Reason);
/// <summary>Explicit quantity of a confirmed unexecuted shortage to release.</summary>
public sealed record OutboundShortageReleaseRequest(string ExpectedVersion, Guid LineId, string Quantity, string Reason);
/// <summary>One actual direct shipment for the exact picked quantity on a line.</summary>
public sealed record OutboundShipmentRequest(string ExpectedVersion, Guid LineId, string Quantity);
/// <summary>Reasoned state change for cancellation or closure.</summary>
public sealed record OutboundStateRequest(string ExpectedVersion, string Reason);

/// <summary>Current detail decision and authorized order projection.</summary>
public sealed record OutboundReadResult(OutboundReadDecision Decision, OutboundRecord? Record = null);

/// <summary>Current order state; all quantity strings use six invariant decimal places.</summary>
public enum OutboundReadDecision
{
    /// <summary>The current complete record is authorized.</summary>
    Success,
    /// <summary>The identity or request is invalid.</summary>
    Invalid,
    /// <summary>Current complete read authority is insufficient.</summary>
    Forbidden,
    /// <summary>The tenant-scoped order does not exist.</summary>
    NotFound
}

/// <summary>Current authorized outbound record; no private WMS entities or storage fields are exposed.</summary>
public sealed record OutboundRecord(Guid Id, string Code, Guid WarehouseId, int State, string Version,
    DateTimeOffset CreatedAt, IReadOnlyList<OutboundLineRecord> Lines);

/// <summary>Current authorized line figures, including remaining reservation rather than original allocation.</summary>
public sealed record OutboundLineRecord(Guid Id, Guid ExecutionId, Guid BalanceId, Guid ItemId, Guid UnitId,
    string Batch, int Precision, Guid ContainerId, Guid LocationId, int Branch, string PlannedQuantity,
    string ReservedQuantity, string PickedQuantity, string ShortageQuantity, string ReleasedQuantity, string ShippedQuantity);
