using Wes.Wms.Domain;
using Xunit;

namespace Wes.Modules.Tests.Wms;

public sealed class StockProtectionTests
{
    [Fact]
    public void Calculate_CountsReservedInTransitIntersectionOnlyOnce()
    {
        var result = StockAvailability.Calculate(Quantity(10),
        [
            Protection(5, StockProtectionKind.Reserved | StockProtectionKind.InTransit),
            Protection(2, StockProtectionKind.Reserved)
        ]);

        Assert.Equal(10, result.Quantity.Value);
        Assert.Equal(7, result.Reserved.Value);
        Assert.Equal(5, result.InTransit.Value);
        Assert.Equal(0, result.Unknown.Value);
        Assert.Equal(7, result.Protected.Value);
        Assert.Equal(3, result.Available.Value);
    }

    [Fact]
    public void Calculate_SumsDisjointUnitsInsteadOfTakingMaximum()
    {
        var result = StockAvailability.Calculate(Quantity(10),
        [
            Protection(4, StockProtectionKind.Reserved),
            Protection(3, StockProtectionKind.InTransit)
        ]);

        Assert.Equal(7, result.Protected.Value);
        Assert.Equal(3, result.Available.Value);
    }

    [Fact]
    public void Calculate_UnknownAndAllFlagsContinueProtection()
    {
        var result = StockAvailability.Calculate(Quantity(10),
        [
            Protection(2, StockProtectionKind.Reserved | StockProtectionKind.InTransit | StockProtectionKind.Unknown),
            Protection(3, StockProtectionKind.Unknown)
        ]);

        Assert.Equal(2, result.Reserved.Value);
        Assert.Equal(2, result.InTransit.Value);
        Assert.Equal(5, result.Unknown.Value);
        Assert.Equal(5, result.Protected.Value);
        Assert.Equal(5, result.Available.Value);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    public void Calculate_EverySupportedFlagCombinationPreservesUnionInvariant(int flags)
    {
        var kind = (StockProtectionKind)flags;
        var result = StockAvailability.Calculate(Quantity(10), [Protection(3, kind)]);

        Assert.Equal((kind & StockProtectionKind.Reserved) != 0 ? 3 : 0, result.Reserved.Value);
        Assert.Equal((kind & StockProtectionKind.InTransit) != 0 ? 3 : 0, result.InTransit.Value);
        Assert.Equal((kind & StockProtectionKind.Unknown) != 0 ? 3 : 0, result.Unknown.Value);
        Assert.Equal(3, result.Protected.Value);
        Assert.Equal(7, result.Available.Value);
        Assert.Equal(result.Quantity, result.Protected.Add(result.Available));
    }

    [Fact]
    public void Calculate_EmptyProtectionsPreserveEntireQuantity()
    {
        var zero = StockAvailability.Calculate(Quantity(0), []);
        var maximum = StockAvailability.Calculate(InventoryQuantity.Create(InventoryQuantity.MaximumValue), []);

        Assert.Equal(0, zero.Available.Value);
        Assert.Equal(0, zero.Protected.Value);
        Assert.Equal(InventoryQuantity.MaximumValue, maximum.Available.Value);
    }

    [Fact]
    public void Calculate_PreservesSixDecimalPrecisionAtFullProtection()
    {
        var result = StockAvailability.Calculate(Quantity(0.000003m),
        [
            Protection(0.000001m, StockProtectionKind.Reserved),
            Protection(0.000002m, StockProtectionKind.InTransit)
        ]);

        Assert.Equal(0.000003m, result.Protected.Value);
        Assert.Equal(0, result.Available.Value);
    }

    [Fact]
    public void Calculate_AcceptsMaximumQuantityFullyProtectedWithoutChangingBookOwnership()
    {
        var maximum = InventoryQuantity.Create(InventoryQuantity.MaximumValue);
        var result = StockAvailability.Calculate(maximum,
            [new StockProtection(Guid.NewGuid(), maximum, StockProtectionKind.InTransit)]);

        Assert.Equal(maximum, result.Quantity);
        Assert.Equal(maximum, result.InTransit);
        Assert.Equal(maximum, result.Protected);
        Assert.Equal(InventoryQuantity.Zero, result.Available);
    }

    [Fact]
    public void Create_RejectsEmptyExecutionIdentity()
    {
        Assert.Throws<ArgumentException>(() =>
            new StockProtection(Guid.Empty, Quantity(1), StockProtectionKind.Reserved));
    }

    [Fact]
    public void Create_RejectsZeroQuantity()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            Protection(0, StockProtectionKind.Reserved));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(8)]
    [InlineData(-1)]
    public void Create_RejectsMissingOrUnknownProtectionKinds(int kind)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            Protection(1, (StockProtectionKind)kind));
    }

    [Fact]
    public void Calculate_RejectsDuplicateUnitsEvenIfValuesAgree()
    {
        var unit = Protection(2, StockProtectionKind.Reserved);

        Assert.Throws<ArgumentException>(() => StockAvailability.Calculate(Quantity(10), [unit, unit]));
        Assert.Throws<ArgumentException>(() => StockAvailability.Calculate(Quantity(10),
            [unit, new StockProtection(unit.ExecutionUnitId, Quantity(2), StockProtectionKind.InTransit)]));
    }

    [Fact]
    public void Calculate_RejectsOverProtectionInsteadOfClampingAvailability()
    {
        Assert.Throws<InvalidOperationException>(() =>
            StockAvailability.Calculate(Quantity(4), [Protection(5, StockProtectionKind.Unknown)]));
    }

    [Fact]
    public void Calculate_RejectsAggregateOverflow()
    {
        var maximum = InventoryQuantity.Create(InventoryQuantity.MaximumValue);

        Assert.Throws<OverflowException>(() => StockAvailability.Calculate(maximum,
        [
            new StockProtection(Guid.NewGuid(), maximum, StockProtectionKind.Reserved),
            Protection(0.000001m, StockProtectionKind.Reserved)
        ]));
    }

    [Fact]
    public void Calculate_IsIndependentOfInputOrderAndLaterListMutation()
    {
        var units = new List<StockProtection>
        {
            Protection(2, StockProtectionKind.Reserved), Protection(3, StockProtectionKind.InTransit)
        };
        var first = StockAvailability.Calculate(Quantity(10), units);
        units.Reverse();
        var reordered = StockAvailability.Calculate(Quantity(10), units);
        units.Clear();

        Assert.Equal(first, reordered);
        Assert.Equal(5, first.Protected.Value);
        Assert.Equal(5, first.Available.Value);
    }

    [Fact]
    public void NullInputsAreExplicitlyRejected()
    {
        Assert.Throws<ArgumentNullException>(() => new StockProtection(Guid.NewGuid(), null!, StockProtectionKind.Reserved));
        Assert.Throws<ArgumentNullException>(() => StockAvailability.Calculate(null!, []));
        Assert.Throws<ArgumentNullException>(() => StockAvailability.Calculate(Quantity(0), null!));
        Assert.Throws<ArgumentException>(() => StockAvailability.Calculate(Quantity(0), [null!]));
    }

    private static InventoryQuantity Quantity(decimal value) => InventoryQuantity.Create(value);

    private static StockProtection Protection(decimal value, StockProtectionKind kind) =>
        new(Guid.NewGuid(), Quantity(value), kind);
}
