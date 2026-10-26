namespace Wes.Wms.Domain;

/// <summary>Permanent explicit warehouse/location creation fact; never an arbitrary audit payload or cached authority.</summary>
public sealed class MasterDataChange
{
    private MasterDataChange() { }

    internal MasterDataChange(Guid operationId, Guid actorId, Guid correlationId, Warehouse warehouse)
    {
        SetIdentity(operationId, actorId, correlationId);
        TenantId = warehouse.TenantId; WarehouseId = warehouse.Id;
        Code = warehouse.Code; Name = warehouse.Name; AfterVersion = warehouse.Version; OccurredAt = warehouse.CreatedAt;
    }

    internal MasterDataChange(Guid operationId, Guid actorId, Guid correlationId, WarehouseLocation location)
    {
        SetIdentity(operationId, actorId, correlationId);
        TenantId = location.TenantId; WarehouseId = location.WarehouseId; LocationId = location.Id;
        PackId = location.PackId; SchemaVersion = location.SchemaVersion;
        Code = location.Code; Name = location.Name; AfterVersion = location.Version; OccurredAt = location.CreatedAt;
    }

    private void SetIdentity(Guid operationId, Guid actorId, Guid correlationId)
    {
        if (operationId == Guid.Empty || actorId == Guid.Empty || correlationId == Guid.Empty)
            throw new ArgumentException("Master-data changes require operation, actor and correlation identities.");
        OperationId = operationId; ActorId = actorId; CorrelationId = correlationId;
    }

    /// <summary>Trusted tenant owner.</summary>
    public Guid TenantId { get; private set; }
    /// <summary>Permanent tenant-unique operation identity.</summary>
    public Guid OperationId { get; private set; }
    /// <summary>Authenticated actor supplied only by a future authorized business boundary.</summary>
    public Guid ActorId { get; private set; }
    /// <summary>Safe correlation identity.</summary>
    public Guid CorrelationId { get; private set; }
    /// <summary>Created warehouse or owning warehouse of the new location.</summary>
    public Guid WarehouseId { get; private set; }
    /// <summary>Null for warehouse creation; otherwise the exact created location.</summary>
    public Guid? LocationId { get; private set; }
    /// <summary>Pack lineage for location creation only.</summary>
    public Guid? PackId { get; private set; }
    /// <summary>Exact schema for location creation only.</summary>
    public string? SchemaVersion { get; private set; }
    /// <summary>Canonical created resource code.</summary>
    public string Code { get; private set; } = "";
    /// <summary>Safe created resource name.</summary>
    public string Name { get; private set; } = "";
    /// <summary>Exact created resource version.</summary>
    public string AfterVersion { get; private set; } = "";
    /// <summary>UTC creation fact time.</summary>
    public DateTimeOffset OccurredAt { get; private set; }
}
