using Microsoft.Extensions.Configuration;
using Wes.Host.Wms;
using Xunit;

namespace Wes.Modules.Tests.Wms;

public sealed class WmsMasterDataSettingsTests
{
    [Theory]
    [InlineData("InboundApprovalEnabled", null, "true", "true", false)]
    [InlineData("InboundApprovalEnabled", "false", "true", "true", false)]
    [InlineData("InboundApprovalEnabled", "true", "true", "true", true)]
    [InlineData("InboundReceiptEnabled", null, "true", "true", false)]
    [InlineData("InboundReceiptEnabled", "false", "true", "true", false)]
    [InlineData("InboundReceiptEnabled", "true", "true", "true", true)]
    public void InboundExecutionIsIndependentlyExplicit(string key, string? value, string reads, string identity, bool expected)
    {
        var configuration = CreationConfiguration("false", reads, identity); configuration[$"Wes:WmsMasterData:{key}"] = value;
        var settings = WmsMasterDataSettings.Load(configuration);
        Assert.Equal(expected, key == "InboundApprovalEnabled" ? settings.InboundApprovalEnabled : settings.InboundReceiptEnabled);
        Assert.False(settings.InboundDraftCreationEnabled); Assert.False(settings.InboundDraftRevisionEnabled);
        Assert.False(settings.StockResourceRegistrationEnabled);
        Assert.False(key == "InboundApprovalEnabled" ? settings.InboundReceiptEnabled : settings.InboundApprovalEnabled);
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("false", false)]
    [InlineData("true", true)]
    public void OutboundExecutionIsIndependentlyExplicitAndDefaultDisabled(string? value, bool expected)
    {
        var configuration = CreationConfiguration("false", "true", "true");
        configuration["Wes:WmsMasterData:OutboundEnabled"] = value;
        var settings = WmsMasterDataSettings.Load(configuration);
        Assert.Equal(expected, settings.OutboundEnabled);
        Assert.False(settings.InboundApprovalEnabled);
        Assert.False(settings.InboundReceiptEnabled);
    }

    [Theory]
    [InlineData("yes", "true", "true")]
    [InlineData("", "true", "true")]
    [InlineData("1", "true", "true")]
    [InlineData("true", "false", "true")]
    [InlineData("true", "true", "false")]
    public void OutboundRejectsMalformedOrUnassembledConfiguration(string value, string reads, string identity)
    {
        var configuration = CreationConfiguration("false", reads, identity);
        configuration["Wes:WmsMasterData:OutboundEnabled"] = value;
        Assert.Throws<InvalidOperationException>(() => WmsMasterDataSettings.Load(configuration));
    }

    [Theory]
    [InlineData("InboundApprovalEnabled", "yes", "true", "true")]
    [InlineData("InboundApprovalEnabled", "", "true", "true")]
    [InlineData("InboundApprovalEnabled", "1", "true", "true")]
    [InlineData("InboundApprovalEnabled", "true", "false", "true")]
    [InlineData("InboundApprovalEnabled", "true", "true", "false")]
    [InlineData("InboundReceiptEnabled", "yes", "true", "true")]
    [InlineData("InboundReceiptEnabled", "", "true", "true")]
    [InlineData("InboundReceiptEnabled", "1", "true", "true")]
    [InlineData("InboundReceiptEnabled", "true", "false", "true")]
    [InlineData("InboundReceiptEnabled", "true", "true", "false")]
    public void InboundExecutionRejectsMalformedOrUnassembledConfiguration(string key, string value, string reads, string identity)
    {
        var configuration = CreationConfiguration("false", reads, identity); configuration[$"Wes:WmsMasterData:{key}"] = value;
        Assert.Throws<InvalidOperationException>(() => WmsMasterDataSettings.Load(configuration));
    }

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

    [Theory]
    [InlineData(null, false)]
    [InlineData("false", false)]
    [InlineData("true", true)]
    public void InboundDraftCreationIsIndependentDefaultDisabled(string? creation, bool expected)
    {
        var configuration = CreationConfiguration("false", "true", "true");
        configuration["Wes:WmsMasterData:InboundDraftCreationEnabled"] = creation;
        var settings = WmsMasterDataSettings.Load(configuration);
        Assert.Equal(expected, settings.InboundDraftCreationEnabled);
        Assert.False(settings.WarehouseCreationEnabled);
        Assert.False(settings.LocationCreationEnabled);
    }

    [Theory]
    [InlineData("yes", "true", "true")]
    [InlineData("", "true", "true")]
    [InlineData("1", "true", "true")]
    [InlineData("true", "false", "true")]
    [InlineData("true", "true", "false")]
    public void InvalidOrUnassembledInboundDraftCreationIsRejected(string creation, string reads, string identity)
    {
        var configuration = CreationConfiguration("false", reads, identity);
        configuration["Wes:WmsMasterData:InboundDraftCreationEnabled"] = creation;
        Assert.Throws<InvalidOperationException>(() => WmsMasterDataSettings.Load(configuration));
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("false", false)]
    [InlineData("true", true)]
    public void InboundDraftRevisionIsIndependentDefaultDisabled(string? revision, bool expected)
    {
        var configuration = CreationConfiguration("false", "true", "true");
        configuration["Wes:WmsMasterData:InboundDraftRevisionEnabled"] = revision;
        var settings = WmsMasterDataSettings.Load(configuration);
        Assert.Equal(expected, settings.InboundDraftRevisionEnabled);
        Assert.False(settings.InboundDraftCreationEnabled);
        Assert.False(settings.LocationCreationEnabled);
        Assert.False(settings.WarehouseCreationEnabled);
    }

    [Theory]
    [InlineData("yes", "true", "true")]
    [InlineData("", "true", "true")]
    [InlineData("1", "true", "true")]
    [InlineData("true", "false", "true")]
    [InlineData("true", "true", "false")]
    public void InvalidOrUnassembledInboundDraftRevisionIsRejected(string revision, string reads, string identity)
    {
        var configuration = CreationConfiguration("false", reads, identity);
        configuration["Wes:WmsMasterData:InboundDraftRevisionEnabled"] = revision;
        Assert.Throws<InvalidOperationException>(() => WmsMasterDataSettings.Load(configuration));
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("false", false)]
    [InlineData("true", true)]
    public void WarehouseStateIsIndependentDefaultDisabled(string? state, bool expected)
    {
        var configuration = CreationConfiguration("false", "true", "true");
        configuration["Wes:WmsMasterData:WarehouseStateEnabled"] = state;
        var settings = WmsMasterDataSettings.Load(configuration);
        Assert.Equal(expected, settings.WarehouseStateEnabled);
        Assert.False(settings.WarehouseCreationEnabled);
        Assert.False(settings.LocationCreationEnabled);
        Assert.False(settings.InboundDraftCreationEnabled);
    }

    [Theory]
    [InlineData("yes", "true", "true")]
    [InlineData("", "true", "true")]
    [InlineData("1", "true", "true")]
    [InlineData("true", "false", "true")]
    [InlineData("true", "true", "false")]
    public void InvalidOrUnassembledWarehouseStateIsRejected(string state, string reads, string identity)
    {
        var configuration = CreationConfiguration("false", reads, identity);
        configuration["Wes:WmsMasterData:WarehouseStateEnabled"] = state;
        Assert.Throws<InvalidOperationException>(() => WmsMasterDataSettings.Load(configuration));
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("false", false)]
    [InlineData("true", true)]
    public void LocationStateIsIndependentDefaultDisabled(string? state, bool expected)
    {
        var configuration = CreationConfiguration("false", "true", "true");
        configuration["Wes:WmsMasterData:LocationStateEnabled"] = state;
        var settings = WmsMasterDataSettings.Load(configuration);
        Assert.Equal(expected, settings.LocationStateEnabled);
        Assert.False(settings.WarehouseCreationEnabled); Assert.False(settings.LocationCreationEnabled);
        Assert.False(settings.WarehouseStateEnabled); Assert.False(settings.InboundDraftCreationEnabled);
    }

    [Theory]
    [InlineData("yes", "true", "true")]
    [InlineData("", "true", "true")]
    [InlineData("1", "true", "true")]
    [InlineData("true", "false", "true")]
    [InlineData("true", "true", "false")]
    public void InvalidOrUnassembledLocationStateIsRejected(string state, string reads, string identity)
    {
        var configuration = CreationConfiguration("false", reads, identity);
        configuration["Wes:WmsMasterData:LocationStateEnabled"] = state;
        Assert.Throws<InvalidOperationException>(() => WmsMasterDataSettings.Load(configuration));
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("false", false)]
    [InlineData("true", true)]
    public void StockResourceRegistrationIsIndependentDefaultDisabled(string? state, bool expected)
    {
        var configuration = CreationConfiguration("false", "true", "true");
        configuration["Wes:WmsMasterData:StockResourceRegistrationEnabled"] = state;
        var settings = WmsMasterDataSettings.Load(configuration);
        Assert.Equal(expected, settings.StockResourceRegistrationEnabled);
        Assert.False(settings.WarehouseCreationEnabled); Assert.False(settings.LocationCreationEnabled);
        Assert.False(settings.WarehouseStateEnabled); Assert.False(settings.LocationStateEnabled);
        Assert.False(settings.InboundDraftCreationEnabled); Assert.False(settings.InboundDraftRevisionEnabled);
    }

    [Theory]
    [InlineData("yes", "true", "true")]
    [InlineData("", "true", "true")]
    [InlineData("1", "true", "true")]
    [InlineData("true", "false", "true")]
    [InlineData("true", "true", "false")]
    public void InvalidOrUnassembledStockResourceRegistrationIsRejected(string state, string reads, string identity)
    {
        var configuration = CreationConfiguration("false", reads, identity);
        configuration["Wes:WmsMasterData:StockResourceRegistrationEnabled"] = state;
        Assert.Throws<InvalidOperationException>(() => WmsMasterDataSettings.Load(configuration));
    }

    private static IConfiguration Configuration(string enabled, string identity) =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Wes:WmsMasterData:Enabled"] = enabled,
            ["Wes:Identity:Enabled"] = identity
        }).Build();
}
