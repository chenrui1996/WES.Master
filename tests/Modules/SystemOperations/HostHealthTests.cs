using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Wes.Modules.Tests.SystemOperations;

/// <summary>Verifies the HTTP and DI contract of the database-free foundation slice.</summary>
public sealed class HostHealthTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    /// <summary>Uses the actual Host composition root without any database substitution.</summary>
    public HostHealthTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory;
    }

    /// <summary>Process liveness must remain independent of unavailable persistence.</summary>
    [Fact]
    [Trait("CaseId", "FOUNDATION-LIVE-001")]
    public async Task Liveness_WithoutDatabase_ReturnsHealthy()
    {
        // Arrange
        using var client = _factory.CreateClient();

        // Act
        using var response = await client.GetAsync("/health/live");

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Healthy", await response.Content.ReadAsStringAsync());
    }

    /// <summary>Missing persistence prevents readiness without exposing diagnostic details over HTTP.</summary>
    [Fact]
    [Trait("CaseId", "FOUNDATION-READY-001")]
    public async Task Readiness_WithoutDatabase_ReturnsUnavailableWithoutInternalDetails()
    {
        // Arrange
        using var client = _factory.CreateClient();

        // Act
        using var response = await client.GetAsync("/health/ready");

        // Assert
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("Unhealthy", await response.Content.ReadAsStringAsync());
    }

    /// <summary>Unimplemented control surfaces must not return success-shaped responses.</summary>
    [Theory]
    [InlineData("/api/v1/wcs/commands")]
    [InlineData("/api/v1/orchestration/transports")]
    [Trait("CaseId", "FOUNDATION-SURFACE-001")]
    public async Task ControlEndpoint_BeforeImplementation_IsNotExposed(string path)
    {
        // Arrange
        using var client = _factory.CreateClient();

        // Act
        using var response = await client.PostAsync(path, null);

        // Assert
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    /// <summary>Provides an injectable clock for future deterministic application rules.</summary>
    [Fact]
    [Trait("CaseId", "FOUNDATION-CLOCK-001")]
    public void HostServices_ResolveTimeProvider_ForFutureDeterministicRules()
    {
        // Arrange
        using var scope = _factory.Services.CreateScope();

        // Act
        var clock = scope.ServiceProvider.GetService<TimeProvider>();

        // Assert
        Assert.NotNull(clock);
    }
}
