using Microsoft.Extensions.Configuration;
using Wes.Host.Wms;
using Xunit;

namespace Wes.Modules.Tests.Wms;

public sealed class WmsMasterDataSettingsTests
{
    [Fact]
    public void MissingConfigurationIsDisabled() =>
        Assert.False(WmsMasterDataSettings.Load(new ConfigurationBuilder().Build()).Enabled);

    [Theory]
    [InlineData("yes")]
    [InlineData("1")]
    [InlineData("")]
    public void MalformedSuppliedToggleIsNotADisabledDefault(string value) =>
        Assert.Throws<InvalidOperationException>(() => WmsMasterDataSettings.Load(Configuration(value, "false")));

    [Fact]
    public void EnabledReadBoundaryRequiresIdentity() =>
        Assert.Throws<InvalidOperationException>(() => WmsMasterDataSettings.Load(Configuration("true", "false")));

    [Fact]
    public void ValidExplicitReadBoundaryCanBeEnabled() =>
        Assert.True(WmsMasterDataSettings.Load(Configuration("true", "true")).Enabled);

    [Fact]
    public void ReadAssemblyDoesNotAutomaticallyEnableWarehouseCreation() =>
        Assert.False(WmsMasterDataSettings.Load(Configuration("true", "true")).WarehouseCreationEnabled);

    [Theory]
    [InlineData("yes", "true", "true")]
    [InlineData("", "true", "true")]
    [InlineData("1", "true", "true")]
    [InlineData("true", "false", "true")]
    [InlineData("true", "true", "false")]
    public void InvalidOrUnassembledWarehouseCreationIsRejected(string creation, string reads, string identity) =>
        Assert.Throws<InvalidOperationException>(() => WmsMasterDataSettings.Load(CreationConfiguration(creation, reads, identity)));

    [Fact]
    public void ExplicitWarehouseCreationRequiresBothApprovedAssemblies() =>
        Assert.True(WmsMasterDataSettings.Load(CreationConfiguration("true", "true", "true")).WarehouseCreationEnabled);

    [Theory]
    [InlineData(null, "true", "true", false)]
    [InlineData("false", "true", "true", false)]
    [InlineData("true", "true", "true", true)]
    public void LocationCreationIsIndependentAndExplicit(string? creation, string reads, string identity, bool expected)
    {
        var configuration = CreationConfiguration("false", reads, identity);
        configuration["Wes:WmsMasterData:LocationCreationEnabled"] = creation;
        var settings = WmsMasterDataSettings.Load(configuration);
        Assert.Equal(expected, settings.LocationCreationEnabled);
        Assert.False(settings.WarehouseCreationEnabled);
    }

    [Theory]
    [InlineData("yes", "true", "true")]
    [InlineData("", "false", "false")]
    [InlineData("1", "true", "true")]
    [InlineData("true", "false", "true")]
    [InlineData("true", "true", "false")]
    public void InvalidOrUnassembledLocationCreationIsRejected(string creation, string reads, string identity)
    {
        var configuration = CreationConfiguration("false", reads, identity);
        configuration["Wes:WmsMasterData:LocationCreationEnabled"] = creation;
        Assert.Throws<InvalidOperationException>(() => WmsMasterDataSettings.Load(configuration));
    }

    private static IConfiguration CreationConfiguration(string creation, string reads, string identity) =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Wes:WmsMasterData:Enabled"] = reads,
            ["Wes:Identity:Enabled"] = identity,
            ["Wes:WmsMasterData:WarehouseCreationEnabled"] = creation
        }).Build();

    private static IConfiguration Configuration(string enabled, string identity) =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Wes:WmsMasterData:Enabled"] = enabled,
            ["Wes:Identity:Enabled"] = identity
        }).Build();
}
