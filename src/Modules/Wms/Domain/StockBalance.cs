using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("Wes.Modules.Tests")]
[assembly: InternalsVisibleTo("Wes.Persistence.Tests")]

namespace Wes.Wms.Domain;

/// <summary>One fixed inventory dimension; only the WMS posting owner may mutate its quantity.</summary>
public sealed class StockBalance
{
    private StockBalance() { }

    /// <summary>Creates an empty dimension; initial nonzero stock requires an owner ledger posting.</summary>
    public StockBalance(Guid tenant, Guid warehouse, Guid location, Guid item, string batch, Guid container, Guid unit, int precision)
    {
        if (new[] { tenant, warehouse, location, item, container, unit }.Contains(Guid.Empty))
            throw new ArgumentException("Stock dimensions require explicit nonempty identities.");
        if (batch is null || batch.Length > 128 || batch != batch.Trim() || batch.Any(char.IsControl))
            throw new ArgumentException("Batch identity must be exact bounded plain text.", nameof(batch));
        _ = QuantityPrecision.Create(precision);
        Id = Guid.NewGuid();
        TenantId = tenant;
        WarehouseId = warehouse;
        LocationId = location;
        ItemId = item;
        Batch = batch;
        ContainerId = container;
        UnitId = unit;
        Precision = precision;
        Version = Guid.NewGuid().ToString("D");
    }

    /// <summary>Immutable owner-generated balance identity.</summary>
    public Guid Id { get; private set; }
    /// <summary>Trusted tenant owner.</summary>
    public Guid TenantId { get; private set; }
    /// <summary>Warehouse identity, not an inferred cross-warehouse transfer.</summary>
    public Guid WarehouseId { get; private set; }
    /// <summary>WMS book position, not observed physical position.</summary>
    public Guid LocationId { get; private set; }
    /// <summary>Material identity.</summary>
    public Guid ItemId { get; private set; }
    /// <summary>Exact batch identity; empty means the dimension has no batch.</summary>
    public string Batch { get; private set; } = "";
    /// <summary>Container book identity.</summary>
    public Guid ContainerId { get; private set; }
    /// <summary>Base unit; conversion is not enabled.</summary>
    public Guid UnitId { get; private set; }
    /// <summary>Approved base-unit precision from zero through six.</summary>
    public int Precision { get; private set; }
    /// <summary>Exact nonnegative book quantity, explained by immutable ledger entries.</summary>
    public decimal Quantity { get; private set; }
    /// <summary>Opaque optimistic write fence.</summary>
    public string Version { get; private set; } = "";

    internal bool CanTransferTo(StockBalance target) =>
        TenantId == target.TenantId && WarehouseId == target.WarehouseId && LocationId != target.LocationId
        && ItemId == target.ItemId && Batch == target.Batch && ContainerId == target.ContainerId
        && UnitId == target.UnitId && Precision == target.Precision;

    internal (decimal Before, decimal After) Credit(StockMovementQuantity quantity) => Change(quantity, incoming: true);
    internal (decimal Before, decimal After) Debit(StockMovementQuantity quantity) => Change(quantity, incoming: false);

    private (decimal Before, decimal After) Change(StockMovementQuantity quantity, bool incoming)
    {
        ArgumentNullException.ThrowIfNull(quantity);
        if (!QuantityPrecision.Create(Precision).CanRepresent(quantity.Value))
            throw new ArgumentException("Movement does not match this balance's base-unit precision.", nameof(quantity));
        if (!incoming && quantity.Value > Quantity)
            throw new InvalidOperationException("Stock quantity is insufficient for this posting.");
        var before = Quantity;
        var current = InventoryQuantity.Create(before);
        var amount = InventoryQuantity.Create(quantity.Value);
        var after = incoming ? current.Add(amount).Value : current.Subtract(amount).Value;
        Quantity = after;
        Version = Guid.NewGuid().ToString("D");
        return (before, after);
    }
}
