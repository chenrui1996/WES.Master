namespace Wes.Wms.Domain;

/// <summary>Immutable registered base unit; conversion and implicit rounding are not supported.</summary>
internal sealed class StockUnit
{
    private StockUnit() { }
    internal StockUnit(Guid tenant, Guid operation, Guid actor, Guid correlation, string code, string name, bool countable, int precision, DateTimeOffset now)
    {
        StockResourceValues.Validate(tenant, operation, actor, correlation, code, name);
        _ = QuantityPrecision.Create(precision);
        if (countable && precision != 0) throw new ArgumentException("Countable units require zero decimal places.");
        TenantId = tenant; OperationId = operation; ActorId = actor; CorrelationId = correlation;
        Code = MasterDataValue.Code(code); Name = name; Countable = countable; Precision = precision; CreatedAt = now.ToUniversalTime();
    }
    public Guid TenantId { get; private set; }
    public Guid Id { get; private set; } = Guid.NewGuid();
    public Guid OperationId { get; private set; }
    public Guid ActorId { get; private set; }
    public Guid CorrelationId { get; private set; }
    public string Code { get; private set; } = "";
    public string Name { get; private set; } = "";
    public bool Countable { get; private set; }
    public int Precision { get; private set; }
    public string Version { get; private set; } = Guid.NewGuid().ToString("D");
    public DateTimeOffset CreatedAt { get; private set; }
}

/// <summary>Immutable material policy. Serial and expiry policies require their own explicit supported contract.</summary>
internal sealed class StockItem
{
    private StockItem() { }
    internal StockItem(Guid tenant, Guid operation, Guid actor, Guid correlation, string code, string name, Guid baseUnit, bool batchRequired, DateTimeOffset now)
    {
        StockResourceValues.Validate(tenant, operation, actor, correlation, code, name);
        if (baseUnit == Guid.Empty) throw new ArgumentException("A material requires a registered base unit.");
        TenantId = tenant; OperationId = operation; ActorId = actor; CorrelationId = correlation;
        Code = MasterDataValue.Code(code); Name = name; BaseUnitId = baseUnit; BatchRequired = batchRequired; CreatedAt = now.ToUniversalTime();
    }
    public Guid TenantId { get; private set; }
    public Guid Id { get; private set; } = Guid.NewGuid();
    public Guid OperationId { get; private set; }
    public Guid ActorId { get; private set; }
    public Guid CorrelationId { get; private set; }
    public string Code { get; private set; } = "";
    public string Name { get; private set; } = "";
    public Guid BaseUnitId { get; private set; }
    public bool BatchRequired { get; private set; }
    public string Version { get; private set; } = Guid.NewGuid().ToString("D");
    public DateTimeOffset CreatedAt { get; private set; }
}

/// <summary>Registered same-warehouse container identity; not an occupancy or observed physical-position fact.</summary>
internal sealed class StockContainer
{
    private StockContainer() { }
    internal StockContainer(Guid tenant, Guid operation, Guid actor, Guid correlation, string code, string name, Guid warehouse, DateTimeOffset now)
    {
        StockResourceValues.Validate(tenant, operation, actor, correlation, code, name);
        if (warehouse == Guid.Empty) throw new ArgumentException("A container requires a real warehouse.");
        TenantId = tenant; OperationId = operation; ActorId = actor; CorrelationId = correlation;
        Code = MasterDataValue.Code(code); Name = name; WarehouseId = warehouse; CreatedAt = now.ToUniversalTime();
    }
    public Guid TenantId { get; private set; }
    public Guid Id { get; private set; } = Guid.NewGuid();
    public Guid OperationId { get; private set; }
    public Guid ActorId { get; private set; }
    public Guid CorrelationId { get; private set; }
    public string Code { get; private set; } = "";
    public string Name { get; private set; } = "";
    public Guid WarehouseId { get; private set; }
    public string Version { get; private set; } = Guid.NewGuid().ToString("D");
    public DateTimeOffset CreatedAt { get; private set; }
}

/// <summary>Permanent original empty-dimension receipt; balance version may change later without rewriting this fact.</summary>
internal sealed class StockDimensionRegistration
{
    private StockDimensionRegistration() { }
    internal StockDimensionRegistration(Guid operation, Guid actor, Guid correlation, StockBalance balance, DateTimeOffset now)
    {
        if (new[] { operation, actor, correlation }.Contains(Guid.Empty) || balance.Quantity != 0m)
            throw new ArgumentException("Only a new empty dimension can be registered.");
        TenantId = balance.TenantId; BalanceId = balance.Id; OperationId = operation; ActorId = actor; CorrelationId = correlation;
        WarehouseId = balance.WarehouseId; LocationId = balance.LocationId; ItemId = balance.ItemId; Batch = balance.Batch;
        ContainerId = balance.ContainerId; UnitId = balance.UnitId; Precision = balance.Precision; AfterVersion = balance.Version;
        CreatedAt = now.ToUniversalTime();
    }
    public Guid TenantId { get; private set; }
    public Guid OperationId { get; private set; }
    public Guid BalanceId { get; private set; }
    public Guid ActorId { get; private set; }
    public Guid CorrelationId { get; private set; }
    public Guid WarehouseId { get; private set; }
    public Guid LocationId { get; private set; }
    public Guid ItemId { get; private set; }
    public Guid UnitId { get; private set; }
    public Guid ContainerId { get; private set; }
    public string Batch { get; private set; } = "";
    public int Precision { get; private set; }
    public string AfterVersion { get; private set; } = "";
    public DateTimeOffset CreatedAt { get; private set; }
}

internal static class StockResourceValues
{
    internal static void Validate(Guid tenant, Guid operation, Guid actor, Guid correlation, string code, string name)
    {
        if (new[] { tenant, operation, actor, correlation }.Contains(Guid.Empty)
            || !MasterDataValue.IsCode(code) || !MasterDataValue.IsName(name))
            throw new ArgumentException("A resource requires complete trusted identities and bounded code/name.");
    }
}
