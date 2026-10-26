using Wes.Wms.Domain;
using Xunit;

namespace Wes.Modules.Tests.Wms;

public sealed class StockBalanceTests
{
    [Fact]
    public void StartsAtZeroAndEntriesExplainExactReceiptAndIssue()
    {
        var balance = Create();
        var initial = balance.Version;
        var receipt = balance.Credit(StockMovementQuantity.Create(10.125m, QuantityPrecision.Divisible));
        Assert.Equal((0m, 10.125m), receipt);
        Assert.NotEqual(initial, balance.Version);
        var credited = balance.Version;
        var shipment = balance.Debit(StockMovementQuantity.Create(3.001m, QuantityPrecision.Divisible));
        Assert.Equal((10.125m, 7.124m), shipment);
        Assert.Equal(7.124m, balance.Quantity);
        Assert.NotEqual(credited, balance.Version);
    }

    [Fact]
    public void InsufficientStockDoesNotChangeVersionOrQuantity()
    {
        var balance = Create();
        var version = balance.Version;
        Assert.Throws<InvalidOperationException>(() => balance.Debit(
            StockMovementQuantity.Create(1m, QuantityPrecision.Each)));
        Assert.Equal(0m, balance.Quantity);
        Assert.Equal(version, balance.Version);
    }

    [Fact]
    public void PrecisionCannotBeBypassedUsingQuantityConstructedForAnotherUnit()
    {
        var balance = Create(precision: 0);
        var version = balance.Version;
        Assert.Throws<ArgumentException>(() => balance.Credit(
            StockMovementQuantity.Create(0.001m, QuantityPrecision.Divisible)));
        Assert.Equal(0m, balance.Quantity);
        Assert.Equal(version, balance.Version);
    }

    [Fact]
    public void TransferCompatibilityIncludesEveryDimensionExceptLocation()
    {
        var source = Create();
        var target = new StockBalance(source.TenantId, source.WarehouseId, Guid.NewGuid(), source.ItemId,
            source.Batch, source.ContainerId, source.UnitId, source.Precision);
        Assert.True(source.CanTransferTo(target));
        Assert.False(source.CanTransferTo(source));
        Assert.False(source.CanTransferTo(new StockBalance(Guid.NewGuid(), source.WarehouseId, target.LocationId,
            source.ItemId, source.Batch, source.ContainerId, source.UnitId, source.Precision)));
        Assert.False(source.CanTransferTo(new StockBalance(source.TenantId, source.WarehouseId, target.LocationId,
            source.ItemId, "different", source.ContainerId, source.UnitId, source.Precision)));
        Assert.False(source.CanTransferTo(new StockBalance(source.TenantId, Guid.NewGuid(), target.LocationId,
            source.ItemId, source.Batch, source.ContainerId, source.UnitId, source.Precision)));
        Assert.False(source.CanTransferTo(new StockBalance(source.TenantId, source.WarehouseId, target.LocationId,
            Guid.NewGuid(), source.Batch, source.ContainerId, source.UnitId, source.Precision)));
        Assert.False(source.CanTransferTo(new StockBalance(source.TenantId, source.WarehouseId, target.LocationId,
            source.ItemId, source.Batch, Guid.NewGuid(), source.UnitId, source.Precision)));
        Assert.False(source.CanTransferTo(new StockBalance(source.TenantId, source.WarehouseId, target.LocationId,
            source.ItemId, source.Batch, source.ContainerId, Guid.NewGuid(), source.Precision)));
        Assert.False(source.CanTransferTo(new StockBalance(source.TenantId, source.WarehouseId, target.LocationId,
            source.ItemId, source.Batch, source.ContainerId, source.UnitId, 0)));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(7)]
    public void UnsupportedPrecisionIsRejected(int precision) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => Create(precision));

    [Theory]
    [InlineData(" ")]
    [InlineData(" leading")]
    [InlineData("trailing ")]
    [InlineData("bad\nbatch")]
    public void BatchIdentityIsNotSilentlyTrimmed(string batch) =>
        Assert.Throws<ArgumentException>(() => new StockBalance(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            Guid.NewGuid(), batch, Guid.NewGuid(), Guid.NewGuid(), 3));

    [Fact]
    public void MissingTenantOrDimensionIdentityIsRejected()
    {
        for (var missing = 0; missing < 6; missing++)
        {
            var ids = Enumerable.Range(0, 6).Select(_ => Guid.NewGuid()).ToArray();
            ids[missing] = Guid.Empty;
            Assert.Throws<ArgumentException>(() =>
                new StockBalance(ids[0], ids[1], ids[2], ids[3], "", ids[4], ids[5], 3));
        }
    }

    private static StockBalance Create(int precision = 3) =>
        new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "", Guid.NewGuid(), Guid.NewGuid(), precision);
}
