using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Wes.Host.Runtime;
using Wes.Modules.Tests.Fixtures;
using Xunit;

namespace Wes.Modules.Tests;

public sealed class FeatureSwitchTests
{
    [Fact]
    [Trait("CaseId", "DTC-SYS-008")]
    public void Load_RequiresEveryDeclaredSwitch()
    {
        var values = FeatureSwitchConfigurationFixture.ValidValues();
        values.Remove("Features:Modules:Platform");
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();

        var error = Assert.Throws<InvalidOperationException>(() => FeatureSwitchConfiguration.Load(configuration));

        Assert.Contains("Features:Modules:Platform", error.Message);
    }

    [Fact]
    [Trait("CaseId", "DTC-SYS-008")]
    public void Load_RejectsUnknownKeysWithTheirConfigurationPath()
    {
        var values = FeatureSwitchConfigurationFixture.ValidValues();
        values["Features:Modules:Unexpected"] = "true";
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();

        var error = Assert.Throws<InvalidOperationException>(() => FeatureSwitchConfiguration.Load(configuration));

        Assert.Contains("Features:Modules:Unexpected", error.Message);
    }

    [Fact]
    [Trait("CaseId", "DTC-SYS-008")]
    public void Load_RejectsInvalidValuesWithoutEchoingThem()
    {
        var values = FeatureSwitchConfigurationFixture.ValidValues();
        values["Features:Modules:Platform"] = "not-a-boolean-secret";
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();

        var error = Assert.Throws<InvalidOperationException>(() => FeatureSwitchConfiguration.Load(configuration));

        Assert.Contains("Features:Modules:Platform", error.Message);
        Assert.DoesNotContain("not-a-boolean-secret", error.Message);
    }

    [Fact]
    [Trait("CaseId", "DTC-SYS-008")]
    public void Load_RejectsEnabledCapabilitiesWithoutAnOwner()
    {
        var values = FeatureSwitchConfigurationFixture.ValidValues();
        values["Features:Capabilities:MessageQueue"] = "true";
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();

        var error = Assert.Throws<InvalidOperationException>(() => FeatureSwitchConfiguration.Load(configuration));

        Assert.Contains("Features:Capabilities:MessageQueue", error.Message);
    }

    [Fact]
    [Trait("CaseId", "DTC-SYS-008")]
    public void Load_CapturesAStableTypedSnapshot()
    {
        var values = FeatureSwitchConfigurationFixture.ValidValues();
        values["Features:Modules:Platform"] = "true";
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var switches = FeatureSwitchConfiguration.Load(configuration);

        configuration["Features:Modules:Platform"] = "false";

        Assert.True(switches.Modules.Platform);
        Assert.False(switches.Modules.Wms);
    }

    [Fact]
    [Trait("CaseId", "DTC-SYS-008")]
    public async Task FeatureCatalog_ExposesOnlyAllowlistedModuleSwitches()
    {
        var values = FeatureSwitchConfigurationFixture.ValidValues();
        values["Features:Modules:Platform"] = "true";
        var switches = FeatureSwitchConfiguration.Load(
            new ConfigurationBuilder().AddInMemoryCollection(values).Build());
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton<IOptions<FeatureSwitches>>(Options.Create(switches));
        await using var app = builder.Build();
        app.MapFeatureCatalog();
        await app.StartAsync();
        using var client = app.GetTestClient();

        using var response = await client.GetAsync("/api/config/features");
        var document = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        Assert.False(document.TryGetProperty("capabilities", out _));
        Assert.False(document.TryGetProperty("connectionString", out _));
        Assert.Equal(JsonValueKind.Array, document.GetProperty("modules").ValueKind);
        var modules = document.GetProperty("modules").EnumerateArray().ToArray();
        Assert.Equal(6, modules.Length);
        Assert.True(modules.Single(module => module.GetProperty("code").GetString() == "Platform")
            .GetProperty("enabled").GetBoolean());
        Assert.False(modules.Single(module => module.GetProperty("code").GetString() == "Wms")
            .GetProperty("enabled").GetBoolean());
    }
}
