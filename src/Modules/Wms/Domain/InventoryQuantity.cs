namespace Wes.Wms.Domain;

/// <summary>
/// Represents a non-negative inventory quantity representable by a <c>decimal(28,6)</c> value.
/// </summary>
public sealed record InventoryQuantity
{
    private const int Scale = 6;

    /// <summary>
    /// Gets the greatest inventory quantity representable by <c>decimal(28,6)</c>.
    /// </summary>
    public static decimal MaximumValue => 9999999999999999999999.999999m;

    /// <summary>
    /// Gets the zero inventory quantity.
    /// </summary>
    public static InventoryQuantity Zero { get; } = new(0m);

    /// <summary>
    /// Gets the numeric quantity.
    /// </summary>
    public decimal Value { get; }

    private InventoryQuantity(decimal value)
    {
        Value = value;
    }

    /// <summary>
    /// Creates an inventory quantity without rounding.
    /// </summary>
    /// <param name="value">A non-negative value within the <c>decimal(28,6)</c> range.</param>
    /// <returns>The validated quantity.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// The value is negative, exceeds the supported range, or has more than six meaningful fractional digits.
    /// </exception>
    public static InventoryQuantity Create(decimal value)
    {
        if (value < 0m || value > MaximumValue || decimal.Round(value, Scale) != value)
        {
            throw new ArgumentOutOfRangeException(
                nameof(value),
                value,
                "Inventory quantity must be non-negative and exactly representable by decimal(28,6).");
        }

        return value == 0m ? Zero : new InventoryQuantity(value);
    }

    /// <summary>
    /// Adds another quantity exactly.
    /// </summary>
    /// <param name="quantity">The non-negative quantity to add.</param>
    /// <returns>The exact sum.</returns>
    /// <exception cref="OverflowException">The sum exceeds the supported quantity range.</exception>
    public InventoryQuantity Add(InventoryQuantity quantity)
    {
        ArgumentNullException.ThrowIfNull(quantity);
        return CreateArithmeticResult(Value + quantity.Value);
    }

    /// <summary>
    /// Subtracts another quantity exactly.
    /// </summary>
    /// <param name="quantity">The non-negative quantity to subtract.</param>
    /// <returns>The exact difference.</returns>
    /// <exception cref="OverflowException">The difference is negative or outside the supported range.</exception>
    public InventoryQuantity Subtract(InventoryQuantity quantity)
    {
        ArgumentNullException.ThrowIfNull(quantity);
        return CreateArithmeticResult(Value - quantity.Value);
    }

    private static InventoryQuantity CreateArithmeticResult(decimal value)
    {
        try
        {
            return Create(value);
        }
        catch (ArgumentOutOfRangeException exception)
        {
            throw new OverflowException("Inventory quantity arithmetic exceeded its supported range.", exception);
        }
        catch (OverflowException exception)
        {
            throw new OverflowException("Inventory quantity arithmetic exceeded its supported range.", exception);
        }
    }
}

/// <summary>
/// Defines the number of fractional decimal places allowed for a quantity.
/// </summary>
public sealed record QuantityPrecision
{
    /// <summary>
    /// Gets the default precision for countable units.
    /// </summary>
    public static QuantityPrecision Each { get; } = new(0);

    /// <summary>
    /// Gets the default precision for divisible units.
    /// </summary>
    public static QuantityPrecision Divisible { get; } = new(3);

    /// <summary>
    /// Gets the allowed number of fractional decimal places.
    /// </summary>
    public int Value { get; }

    private QuantityPrecision(int value)
    {
        Value = value;
    }

    /// <summary>
    /// Creates a precision in the supported range.
    /// </summary>
    /// <param name="value">The number of allowed fractional decimal places, from zero through six.</param>
    /// <returns>The validated precision.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The precision is outside zero through six.</exception>
    public static QuantityPrecision Create(int value)
    {
        if (value is < 0 or > 6)
        {
            throw new ArgumentOutOfRangeException(nameof(value), value, "Precision must be between zero and six.");
        }

        return value switch
        {
            0 => Each,
            3 => Divisible,
            _ => new QuantityPrecision(value)
        };
    }

    /// <summary>
    /// Determines whether a value is exactly representable at this precision.
    /// </summary>
    /// <param name="value">The decimal value to examine.</param>
    /// <returns><see langword="true"/> when rounding to this precision does not change its numeric value.</returns>
    public bool CanRepresent(decimal value) => decimal.Round(value, Value) == value;
}

/// <summary>
/// Represents a positive single stock movement quantity within the approved per-movement limit.
/// </summary>
public sealed record StockMovementQuantity
{
    /// <summary>
    /// Gets the maximum permitted quantity for one stock movement.
    /// </summary>
    public static decimal MaximumValue => 999999999.999999m;

    /// <summary>
    /// Gets the numeric quantity.
    /// </summary>
    public decimal Value { get; }

    private StockMovementQuantity(decimal value)
    {
        Value = value;
    }

    /// <summary>
    /// Creates a positive movement quantity that is exactly representable at the supplied precision.
    /// </summary>
    /// <param name="value">A positive quantity not exceeding the per-movement maximum.</param>
    /// <param name="precision">The applicable unit precision.</param>
    /// <returns>The validated movement quantity.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// The value is not positive, exceeds the per-movement maximum, or is not exactly representable at the precision.
    /// </exception>
    public static StockMovementQuantity Create(decimal value, QuantityPrecision precision)
    {
        ArgumentNullException.ThrowIfNull(precision);

        if (value <= 0m || value > MaximumValue || !precision.CanRepresent(value))
        {
            throw new ArgumentOutOfRangeException(
                nameof(value),
                value,
                "Movement quantity must be positive, within its maximum, and exactly representable at the unit precision.");
        }

        return new StockMovementQuantity(value);
    }
}
