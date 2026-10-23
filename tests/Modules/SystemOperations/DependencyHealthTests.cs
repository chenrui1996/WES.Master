using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MySql.Data.MySqlClient;
using Wes.Persistence.Tests;
using Wes.Platform;
using Wes.Platform.Infrastructure.Health;
using Xunit;

namespace Wes.Modules.Tests.SystemOperations;

/// <summary>Probes real MySQL availability without stopping services or enabling control writes.</summary>
public sealed class DependencyHealthTests
{
    /// <summary>The same probe registration must fail closed on misconfiguration and not leak values.</summary>
    [Theory]
    [InlineData("Not A Connection String=secret-marker;", "3")]
    [InlineData("Server=localhost;Database=business;User ID=fixture;Password=secret-marker;", "3")]
    [InlineData("", "0")]
    [InlineData("", "6")]
    [InlineData("", "secret-marker")]
    [Trait("CaseId", "FOUNDATION-CONFIG-001")]
    public void InvalidHealthConfiguration_IsRejectedBeforeConnecting(string connection, string timeout)
    {
        var configuration = Config(connection, timeout);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddPlatformDependencyHealth(configuration);
        using var provider = services.BuildServiceProvider();

        var error = Assert.Throws<OptionsValidationException>(() =>
            provider.GetRequiredService<IOptions<DatabaseHealthOptions>>().Value);

        Assert.DoesNotContain("secret-marker", error.Message);
        Assert.Null(error.InnerException);
    }

    /// <summary>Host startup validates unsafe targets rather than deferring invalid options to requests.</summary>
    [Fact]
    [Trait("CaseId", "FOUNDATION-CONFIG-001")]
    public void ActualHost_InvalidConfigurationFailsStartupWithoutSecretEcho()
    {
        using var factory = new ConfiguredFactory(
            "Server=localhost;Database=business;User ID=fixture;Password=secret-marker;");

        var error = Assert.ThrowsAny<Exception>(() => factory.CreateClient());

        Assert.DoesNotContain("secret-marker", error.ToString());
        Assert.IsType<OptionsValidationException>(error.GetBaseException());
    }

    /// <summary>A socket failure is real but targets only a loopback port, never a production instance.</summary>
    [Fact]
    [Trait("Provider", "MySQL")]
    [Trait("CaseId", "FOUNDATION-READY-002")]
    public async Task RealDependency_HealthyDisconnectedThenRecovered()
    {
        var authorized = MySqlTestConnection.LoadAuthorizedTestConnection();
        var unavailable = new MySqlConnectionStringBuilder(authorized)
        {
            Server = "127.0.0.1",
            Port = 1
        }.ConnectionString;

        var healthy = await ProbeAsync(authorized);
        var disconnected = await ProbeAsync(unavailable);
        var recovered = await ProbeAsync(authorized);

        Assert.Equal(HealthStatus.Healthy, healthy.Status);
        Assert.Equal(HealthStatus.Unhealthy, disconnected.Status);
        Assert.Equal(HealthStatus.Healthy, recovered.Status);
        Assert.Equal("DependencyUnavailable", disconnected.Entries["mysql"].Description);
        Assert.Null(disconnected.Entries["mysql"].Exception);
    }

    /// <summary>Default omission must not yield a healthy empty check set.</summary>
    [Fact]
    [Trait("CaseId", "FOUNDATION-READY-002")]
    public async Task MissingConnection_IsExplicitlyUnhealthy()
    {
        var report = await ProbeAsync("");

        Assert.Equal(HealthStatus.Unhealthy, report.Status);
        Assert.Equal("DatabaseNotConfigured", report.Entries["mysql"].Description);
    }

    /// <summary>Dependency health never removes the separate unassembled-business readiness gate.</summary>
    [Fact]
    [Trait("Provider", "MySQL")]
    [Trait("CaseId", "FOUNDATION-READY-002")]
    public async Task ActualHost_HealthyDependencyStillDoesNotEnableBusinessReadiness()
    {
        using var factory = new ConfiguredFactory(MySqlTestConnection.LoadAuthorizedTestConnection());
        using var client = factory.CreateClient();

        using var dependencies = await client.GetAsync("/health/dependencies");
        using var live = await client.GetAsync("/health/live");
        using var ready = await client.GetAsync("/health/ready");

        Assert.Equal(HttpStatusCode.OK, dependencies.StatusCode);
        Assert.Equal("Healthy", await dependencies.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK, live.StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, ready.StatusCode);
        Assert.Equal("Unhealthy", await ready.Content.ReadAsStringAsync());
        Assert.True(Guid.TryParseExact(ready.Headers.GetValues("X-Correlation-Id").Single(), "D", out _));
    }

    /// <summary>Liveness does not call a failed dependency and both health outputs remain safe.</summary>
    [Fact]
    [Trait("CaseId", "FOUNDATION-READY-002")]
    public async Task ActualHost_UnreachableDependencyDoesNotBreakLiveness()
    {
        const string unavailable = "Server=127.0.0.1;Port=1;Database=wes_test;User ID=fixture;Password=secret-marker;";
        using var factory = new ConfiguredFactory(unavailable);
        using var client = factory.CreateClient();

        using var live = await client.GetAsync("/health/live");
        using var dependency = await client.GetAsync("/health/dependencies");

        Assert.Equal(HttpStatusCode.OK, live.StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, dependency.StatusCode);
        Assert.Equal("Unhealthy", await dependency.Content.ReadAsStringAsync());
    }

    /// <summary>Even accidental mutation of an options object cannot retarget a probe to a business database.</summary>
    [Fact]
    [Trait("CaseId", "FOUNDATION-CONFIG-001")]
    public async Task Probe_InvalidTargetIsRejectedAgainBeforeOpening()
    {
        var check = new MySqlDependencyHealthCheck(Options.Create(new DatabaseHealthOptions
        {
            ConnectionString = "Server=127.0.0.1;Port=1;Database=business;User ID=fixture;Password=synthetic;"
        }), NullLogger<MySqlDependencyHealthCheck>.Instance);

        var result = await check.CheckHealthAsync(new HealthCheckContext());

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Equal("DatabaseConfigurationInvalid", result.Description);
    }

    /// <summary>Caller cancellation must not be converted into a cached or synthetic dependency result.</summary>
    [Fact]
    [Trait("CaseId", "FOUNDATION-READY-002")]
    public async Task Probe_CallerCancellationPropagates()
    {
        var check = new MySqlDependencyHealthCheck(Options.Create(new DatabaseHealthOptions()),
            NullLogger<MySqlDependencyHealthCheck>.Instance);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            check.CheckHealthAsync(new HealthCheckContext(), new CancellationToken(canceled: true)));
    }

    private static async Task<HealthReport> ProbeAsync(string connection)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddPlatformDependencyHealth(Config(connection));
        using var provider = services.BuildServiceProvider();
        return await provider.GetRequiredService<HealthCheckService>().CheckHealthAsync();
    }

    private static IConfiguration Config(string connection, string timeout = "1") =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["DatabaseHealth:ConnectionString"] = connection,
            ["DatabaseHealth:TimeoutSeconds"] = timeout
        }).Build();

    private sealed class ConfiguredFactory(string connection) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder) =>
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddConfiguration(Config(connection)));
    }
}
