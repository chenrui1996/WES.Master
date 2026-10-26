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

    private static IConfiguration Configuration(string enabled, string identity) =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Wes:WmsMasterData:Enabled"] = enabled,
            ["Wes:Identity:Enabled"] = identity
        }).Build();
}
