namespace Wes.Wms.Domain;

/// <summary>Tenant-owned logical location with immutable warehouse, Pack and schema lineage; created disabled.</summary>
public sealed class WarehouseLocation
{
    private WarehouseLocation() { }

    /// <summary>Requires an application-level schema check before persisting this disabled location and its change.</summary>
    internal WarehouseLocation(Guid tenantId, Guid warehouseId, Guid packId, string schemaVersion,
        string code, string name, DateTimeOffset createdAt)
    {
        if (tenantId == Guid.Empty || warehouseId == Guid.Empty || packId == Guid.Empty)
            throw new ArgumentException("Explicit tenant, warehouse and Pack identities are required.");
        if (!MasterDataValue.IsCode(schemaVersion)) throw new ArgumentException("An explicit bounded schema version is required.");
        TenantId = tenantId;
        WarehouseId = warehouseId;
        PackId = packId;
        SchemaVersion = schemaVersion;
        Code = MasterDataValue.Code(code);
        Name = MasterDataValue.Name(name);
        CreatedAt = createdAt.ToUniversalTime();
    }

    /// <summary>Permanent server-created location identity.</summary>
    public Guid Id { get; private set; } = Guid.NewGuid();
    /// <summary>Trusted owner tenant.</summary>
    public Guid TenantId { get; private set; }
    /// <summary>Immutable owner warehouse, never changed to relocate occupied stock.</summary>
    public Guid WarehouseId { get; private set; }
    /// <summary>Immutable semantic Pack reference; no private Pack table dependency.</summary>
    public Guid PackId { get; private set; }
    /// <summary>Pinned exact schema version validated by the Pack adapter.</summary>
    public string SchemaVersion { get; private set; } = "";
    /// <summary>Tenant-unique canonical location code, not a supplier device address.</summary>
    public string Code { get; private set; } = "";
    /// <summary>Safe display name only.</summary>
    public string Name { get; private set; } = "";
    /// <summary>Master-data creation does not permit allocation or physical control.</summary>
    public bool Enabled { get; private set; }
    /// <summary>Opaque version for later controlled operational changes.</summary>
    public string Version { get; private set; } = Guid.NewGuid().ToString("D");
    /// <summary>Owner UTC creation time.</summary>
    public DateTimeOffset CreatedAt { get; private set; }

    internal void ChangeState(bool enabled)
    {
        if (Enabled == enabled) throw new InvalidOperationException("Location state must actually change.");
        Enabled = enabled;
        Version = Guid.NewGuid().ToString("D");
    }
}
