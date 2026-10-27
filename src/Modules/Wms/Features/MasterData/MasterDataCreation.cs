namespace Wes.Wms.Features.MasterData;

/// <summary>Disabled creation input; actor/tenant/session come only from the trusted subject, never this request.</summary>
public sealed record MasterDataCreation(Guid OperationId, Guid CorrelationId, string Code, string Name,
    Guid? WarehouseId = null, Guid? PackId = null, string? SchemaVersion = null);

/// <summary>Explicit current-authorized creation outcomes; none imply activation, allocation or physical safety evidence.</summary>
public enum MasterDataWriteDecision
{
    /// <summary>A disabled identity and its permanent creation fact committed together.</summary>
    Created,
    /// <summary>The exact permanent operation already committed and is still currently authorized.</summary>
    Replayed,
    /// <summary>The trusted context, complete field projection or parent scope is not currently authorized.</summary>
    Forbidden,
    /// <summary>The bounded creation input or operation identities are invalid.</summary>
    Invalid,
    /// <summary>The parent warehouse is absent in this tenant.</summary>
    NotFound,
    /// <summary>A different operation already owns this tenant code.</summary>
    CodeConflict,
    /// <summary>The operation identity was used for a different actor or body.</summary>
    IdempotencyConflict,
    /// <summary>The pinned location tuple is unsupported by the explicitly supplied schema adapter.</summary>
    SchemaRejected,
    /// <summary>The schema adapter cannot establish evidence; no definition was created.</summary>
    SchemaUnavailable
}

/// <summary>Id/version are disclosed only on Created or currently authorized Replayed, never on a refusal.</summary>
public sealed record MasterDataWriteResult(MasterDataWriteDecision Decision, Guid? Id = null, string? Version = null);
