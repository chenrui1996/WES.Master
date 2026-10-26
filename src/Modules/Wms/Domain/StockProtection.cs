namespace Wes.Wms.Domain;

/// <summary>Overlapping reasons that keep an execution unit unavailable for allocation.</summary>
[Flags]
public enum StockProtectionKind
{
    /// <summary>Allocated to an owner operation.</summary>
    Reserved = 1,
    /// <summary>In execution while retaining its WMS book ownership.</summary>
    InTransit = 2,
    /// <summary>Awaiting reconciliation; neither timeout nor failure implies release.</summary>
    Unknown = 4
}

/// <summary>A protected, disjoint execution unit within one inventory dimension and base unit.</summary>
public sealed record StockProtection
{
    /// <summary>Gets the owner-verified identity of this indivisible protection portion.</summary>
    public Guid ExecutionUnitId { get; }
    /// <summary>Gets the positive quantity protected by all of the indicated reasons together.</summary>
    public InventoryQuantity Quantity { get; }
    /// <summary>Gets the overlapping protection reasons, counted only once in total protection.</summary>
    public StockProtectionKind Kind { get; }

    /// <summary>Creates a protection portion; the owner must prove disjointness from other unit identities.</summary>
    /// <param name="executionUnitId">A non-empty identity verified by the stock owner.</param>
    /// <param name="quantity">A strictly positive portion in the stock dimension's base unit.</param>
    /// <param name="kind">One or more supported protection reasons.</param>
    /// <exception cref="ArgumentException">An identity, quantity or reason is invalid.</exception>
    public StockProtection(Guid executionUnitId, InventoryQuantity quantity, StockProtectionKind kind)
    {
        ArgumentNullException.ThrowIfNull(quantity);
        if (executionUnitId == Guid.Empty)
        {
            throw new ArgumentException("A verified execution unit identity is required.", nameof(executionUnitId));
        }
        if (quantity.Value == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(quantity), "A protected execution unit must have positive quantity.");
        }
        const StockProtectionKind supported = StockProtectionKind.Reserved | StockProtectionKind.InTransit | StockProtectionKind.Unknown;
        if (kind == 0 || (kind & ~supported) != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(kind), "At least one supported protection reason is required.");
        }

        ExecutionUnitId = executionUnitId;
        Quantity = quantity;
        Kind = kind;
    }
}

/// <summary>An immutable availability projection, not a reservation command or concurrency boundary.</summary>
public sealed record StockAvailability
{
    /// <summary>Gets the unchanged WMS book quantity.</summary>
    public InventoryQuantity Quantity { get; }
    /// <summary>Gets the quantity bearing a reservation reason, including overlaps.</summary>
    public InventoryQuantity Reserved { get; }
    /// <summary>Gets the quantity in transit, including overlaps.</summary>
    public InventoryQuantity InTransit { get; }
    /// <summary>Gets the quantity awaiting reconciliation, including overlaps.</summary>
    public InventoryQuantity Unknown { get; }
    /// <summary>Gets the union of all protected execution units, counted once each.</summary>
    public InventoryQuantity Protected { get; }
    /// <summary>Gets the book quantity less the union of protected execution units.</summary>
    public InventoryQuantity Available { get; }

    private StockAvailability(InventoryQuantity quantity, InventoryQuantity reserved, InventoryQuantity inTransit,
        InventoryQuantity unknown, InventoryQuantity protectedQuantity)
    {
        Quantity = quantity;
        Reserved = reserved;
        InTransit = inTransit;
        Unknown = unknown;
        Protected = protectedQuantity;
        Available = quantity.Subtract(protectedQuantity);
    }

    /// <summary>Projects owner-verified, non-overlapping portions of one stock dimension without releasing any protection.</summary>
    /// <param name="quantity">The current non-negative WMS book quantity.</param>
    /// <param name="protections">Disjoint protected portions in the same stock dimension and base unit.</param>
    /// <returns>A detached quantity projection; no stock, reservation or protection is written.</returns>
    /// <exception cref="ArgumentException">A protection is null or an execution identity is duplicated.</exception>
    /// <exception cref="InvalidOperationException">Protected quantity exceeds the book quantity.</exception>
    /// <exception cref="OverflowException">A quantity sum exceeds its supported decimal range.</exception>
    public static StockAvailability Calculate(InventoryQuantity quantity, IEnumerable<StockProtection> protections)
    {
        ArgumentNullException.ThrowIfNull(quantity);
        ArgumentNullException.ThrowIfNull(protections);
        var identities = new HashSet<Guid>();
        var reserved = InventoryQuantity.Zero;
        var inTransit = InventoryQuantity.Zero;
        var unknown = InventoryQuantity.Zero;
        var protectedQuantity = InventoryQuantity.Zero;
        foreach (var unit in protections)
        {
            if (unit is null || !identities.Add(unit.ExecutionUnitId))
            {
                throw new ArgumentException("Protection portions must be non-null with unique execution identities.", nameof(protections));
            }
            protectedQuantity = protectedQuantity.Add(unit.Quantity);
            if ((unit.Kind & StockProtectionKind.Reserved) != 0) reserved = reserved.Add(unit.Quantity);
            if ((unit.Kind & StockProtectionKind.InTransit) != 0) inTransit = inTransit.Add(unit.Quantity);
            if ((unit.Kind & StockProtectionKind.Unknown) != 0) unknown = unknown.Add(unit.Quantity);
        }
        if (protectedQuantity.Value > quantity.Value)
        {
            throw new InvalidOperationException("Protected quantity exceeds the WMS book quantity.");
        }

        return new StockAvailability(quantity, reserved, inTransit, unknown, protectedQuantity);
    }
}
