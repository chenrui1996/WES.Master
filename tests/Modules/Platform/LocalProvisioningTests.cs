using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Wes.Host.Identity;
using Xunit;

namespace Wes.Modules.Tests.Platform;

/// <summary>Local entry-point guards execute before reading a password or resolving persistence/secrets.</summary>
public sealed class LocalProvisioningTests
{
    /// <summary>No production browser refresh delivery may silently rely on ephemeral or relative key storage.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("relative-key-ring")]
    public void ProductionKeyRingMustBeExplicitAndAbsolute(string? path) =>
        Assert.Throws<InvalidOperationException>(() => IdentityKeyRing.Validate(path, production: true));

    /// <summary>Existing development/test defaults remain available without pretending to be production persistence.</summary>
    [Fact]
    public void DevelopmentCanLeavePersistentKeyRingUnconfigured() => IdentityKeyRing.Validate(null, production: false);
    /// <summary>A closed explicit local gate must never read input or touch a database.</summary>
    [Fact]
    public async Task ClosedLocalGateRejectsBeforeSecretInputOrPersistenceResolution()
    {
        using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        var config = new ConfigurationBuilder().Build();
        Assert.False(await LocalProvisioning.RunAsync(services, config, () => throw new InvalidOperationException("Input must not run.")));
    }

    /// <summary>Enabling the command is not permission to invent IDs, grants or default credentials.</summary>
    [Fact]
    public async Task OpenLocalGateWithMissingReviewedMetadataFailsBeforeSecretInput()
    {
        using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Wes:Provisioning:Enabled"] = "true" }).Build();
        await Assert.ThrowsAsync<InvalidOperationException>(() => LocalProvisioning.RunAsync(services, config,
            () => throw new NotSupportedException("Input must not run.")));
    }
}
