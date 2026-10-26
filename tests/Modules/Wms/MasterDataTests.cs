using Wes.Wms.Domain;
using Xunit;

namespace Wes.Modules.Tests.Wms;

public sealed class MasterDataTests
{
    [Fact]
    public void WarehouseAndLocationStartDisabledWithCanonicalStableCodes()
    {
        var warehouse = new Warehouse(Guid.NewGuid(), "wh-01", "一号仓", DateTimeOffset.UtcNow);
        var location = new WarehouseLocation(warehouse.TenantId, warehouse.Id, Guid.NewGuid(),
            "schema-v1", "a-01", "一号位", DateTimeOffset.UtcNow);
        Assert.Equal("WH-01", warehouse.Code);
        Assert.Equal("A-01", location.Code);
        Assert.False(warehouse.Enabled);
        Assert.False(location.Enabled);
        Assert.Equal(warehouse.Id, location.WarehouseId);
        Assert.Equal("schema-v1", location.SchemaVersion);
        Assert.NotEqual(Guid.Empty, warehouse.Id);
        Assert.True(Guid.TryParseExact(warehouse.Version, "D", out _));
        Assert.True(Guid.TryParseExact(location.Version, "D", out _));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" A")]
    [InlineData("A ")]
    [InlineData("A/B")]
    [InlineData("A:B")]
    [InlineData("仓库")]
    [InlineData("A\n")]
    public void InvalidStableCodesAreRejectedWithoutSilentTrimming(string code) =>
        Assert.Throws<ArgumentException>(() => new Warehouse(Guid.NewGuid(), code, "Warehouse", DateTimeOffset.UtcNow));

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData(" name")]
    [InlineData("name ")]
    [InlineData("name\nvalue")]
    public void InvalidDisplayNamesAreRejected(string name) =>
        Assert.Throws<ArgumentException>(() => new Warehouse(Guid.NewGuid(), "W1", name, DateTimeOffset.UtcNow));

    [Fact]
    public void BoundsAndEveryLocationOwnerIdentityAreRequired()
    {
        Assert.Throws<ArgumentException>(() => new Warehouse(Guid.Empty, "W1", "Warehouse", DateTimeOffset.UtcNow));
        Assert.Throws<ArgumentException>(() => new Warehouse(Guid.NewGuid(), new string('A', 65), "Warehouse", DateTimeOffset.UtcNow));
        Assert.Throws<ArgumentException>(() => new Warehouse(Guid.NewGuid(), "W1", new string('A', 129), DateTimeOffset.UtcNow));
        for (var missing = 0; missing < 3; missing++)
        {
            var ids = new[] { Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid() };
            ids[missing] = Guid.Empty;
            Assert.Throws<ArgumentException>(() => new WarehouseLocation(ids[0], ids[1], ids[2],
                "v1", "A1", "Location", DateTimeOffset.UtcNow));
        }
        Assert.Throws<ArgumentException>(() => new WarehouseLocation(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            "", "A1", "Location", DateTimeOffset.UtcNow));
    }
}
