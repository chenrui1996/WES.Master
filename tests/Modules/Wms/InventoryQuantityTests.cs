using System.Globalization;
using Wes.Wms.Domain;
using Xunit;

namespace Wes.Modules.Tests.Wms;

public sealed class InventoryQuantityTests
{
    [Fact]
    public void InventoryQuantity_AcceptsZeroSmallestIncrementAndMaximum()
    {
        var zero = InventoryQuantity.Create(0m);
        var smallestPositive = InventoryQuantity.Create(0.000001m);
        var maximum = InventoryQuantity.Create(9999999999999999999999.999999m);

        Assert.Equal(0m, zero.Value);
        Assert.Equal(0.000001m, smallestPositive.Value);
        Assert.Equal(9999999999999999999999.999999m, maximum.Value);
    }

    [Theory]
    [InlineData("-0.000001")]
    [InlineData("0.0000001")]
    [InlineData("10000000000000000000000")]
    public void InventoryQuantity_RejectsNegativeOverPrecisionAndOutOfRangeValues(string value)
    {
        Assert.ThrowsAny<ArgumentException>(() =>
            InventoryQuantity.Create(decimal.Parse(value, CultureInfo.InvariantCulture)));
    }

    [Fact]
    public void InventoryQuantity_AcceptsNumericallyRepresentableTrailingZeros()
    {
        var quantity = InventoryQuantity.Create(1.2300000m);

        Assert.Equal(1.23m, quantity.Value);
    }

    [Fact]
    public void InventoryQuantity_AddsAndSubtractsExactly()
    {
        var total = InventoryQuantity.Create(1.234567m)
            .Add(InventoryQuantity.Create(2.000003m));

        Assert.Equal(3.234570m, total.Value);
        Assert.Equal(1.234567m,
            total.Subtract(InventoryQuantity.Create(2.000003m)).Value);
    }

    [Fact]
    public void InventoryQuantity_RejectsArithmeticUnderflowAndOverflow()
    {
        var maximum = InventoryQuantity.Create(9999999999999999999999.999999m);

        Assert.Throws<OverflowException>(() =>
            maximum.Add(InventoryQuantity.Create(0.000001m)));
        Assert.Throws<OverflowException>(() =>
            InventoryQuantity.Zero.Subtract(InventoryQuantity.Create(0.000001m)));
    }

    [Fact]
    public void QuantityPrecision_ProvidesApprovedDefaultsAndExplicitRange()
    {
        Assert.Equal(0, QuantityPrecision.Each.Value);
        Assert.Equal(3, QuantityPrecision.Divisible.Value);

        for (var precision = 0; precision <= 6; precision++)
        {
            Assert.Equal(precision, QuantityPrecision.Create(precision).Value);
        }
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(7)]
    public void QuantityPrecision_RejectsValuesOutsideZeroThroughSix(int precision)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => QuantityPrecision.Create(precision));
    }

    [Fact]
    public void QuantityPrecision_UsesNumericRepresentabilityRatherThanDecimalScale()
    {
        Assert.True(QuantityPrecision.Each.CanRepresent(1.000m));
        Assert.False(QuantityPrecision.Each.CanRepresent(0.5m));
        Assert.True(QuantityPrecision.Divisible.CanRepresent(0.001m));
        Assert.True(QuantityPrecision.Divisible.CanRepresent(1.000000m));
        Assert.False(QuantityPrecision.Divisible.CanRepresent(0.0001m));
        Assert.True(QuantityPrecision.Create(6).CanRepresent(0.000001m));
    }

    [Fact]
    public void StockMovementQuantity_AcceptsPositivePrecisionAndMaximumBoundaries()
    {
        var minimum = StockMovementQuantity.Create(0.000001m, QuantityPrecision.Create(6));
        var maximum = StockMovementQuantity.Create(999999999.999999m, QuantityPrecision.Create(6));

        Assert.Equal(0.000001m, minimum.Value);
        Assert.Equal(999999999.999999m, maximum.Value);
        Assert.Equal(2m,
            StockMovementQuantity.Create(2.0000m, QuantityPrecision.Each).Value);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-0.000001")]
    [InlineData("1000000000")]
    [InlineData("0.0001")]
    public void StockMovementQuantity_RejectsNonPositiveExcessiveOrInexactValues(string value)
    {
        Assert.ThrowsAny<ArgumentException>(() =>
            StockMovementQuantity.Create(decimal.Parse(value, CultureInfo.InvariantCulture), QuantityPrecision.Divisible));
    }
}
