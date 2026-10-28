namespace Wes.Wms.Features.MasterData;

/// <summary>Registers a fixed base unit; countable units require precision zero, divisible units explicit precision 0..6.</summary>
public sealed record RegisterUnit(Guid OperationId, Guid CorrelationId, string Code, string Name, bool Countable, int Precision);
/// <summary>Registers an immutable material and batch policy against a real registered base unit.</summary>
public sealed record RegisterItem(Guid OperationId, Guid CorrelationId, string Code, string Name, Guid BaseUnitId, bool BatchRequired);
/// <summary>Registers an unpositioned container in one real warehouse. This is not occupancy or control authorization.</summary>
public sealed record RegisterContainer(Guid OperationId, Guid CorrelationId, string Code, string Name, Guid WarehouseId);
/// <summary>Creates only an empty legal dimension; unit and precision come from the registered material's base unit.</summary>
public sealed record RegisterStockDimension(Guid OperationId, Guid CorrelationId, Guid WarehouseId, Guid LocationId,
    Guid ItemId, string Batch, Guid ContainerId);

/// <summary>Only Created/Replayed disclose a permanent original receipt.</summary>
public enum StockResourceDecision
{
    /// <summary>A new resource or empty dimension committed.</summary>
    Created,
    /// <summary>The exact original operation committed previously.</summary>
    Replayed,
    /// <summary>Current subject or complete fields do not authorize the operation.</summary>
    Forbidden,
    /// <summary>Malformed identities, bounded values or batch/unit policy.</summary>
    Invalid,
    /// <summary>A real resource or creator provenance is missing from this tenant.</summary>
    NotFound,
    /// <summary>An operation tuple, code or existing balance conflicts.</summary>
    Conflict,
    /// <summary>The warehouse or location is disabled or belongs elsewhere.</summary>
    Blocked,
    /// <summary>The fixed location tuple was rejected by the schema.</summary>
    SchemaRejected,
    /// <summary>No current schema evidence is available.</summary>
    SchemaUnavailable
}

/// <summary>Original resource identity/version, never current stock, position or physical-readiness evidence.</summary>
public sealed record StockResourceResult(StockResourceDecision Decision, Guid? Id = null, string? Version = null);

/// <summary>Fixed current-readable immutable unit projection; no creator or operation metadata.</summary>
public sealed record StockUnitDefinition(Guid Id, string Code, string Name, bool Countable, int Precision, string Version, DateTimeOffset CreatedAt);
/// <summary>Fixed material projection; base-unit identity is not a unit conversion or tracking-policy default.</summary>
public sealed record StockItemDefinition(Guid Id, string Code, string Name, Guid BaseUnitId, bool BatchRequired, string Version, DateTimeOffset CreatedAt);
/// <summary>Fixed container identity; warehouse assignment is not observed position or occupancy.</summary>
public sealed record StockContainerDefinition(Guid Id, string Code, string Name, Guid WarehouseId, string Version, DateTimeOffset CreatedAt);
/// <summary>Only Success returns the complete currently authorized projection.</summary>
public sealed record StockResourceReadResult<T>(MasterDataReadDecision Decision, T? Definition = null) where T : class;
