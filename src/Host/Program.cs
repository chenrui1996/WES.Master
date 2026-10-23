using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Serilog;
using Wes.Host.Runtime;
using Wes.Platform;

using var bootstrapLogger = new LoggerConfiguration().WriteTo.Console().CreateLogger();
bootstrapLogger.Information("WES Host starting");
var builder = WebApplication.CreateBuilder(args);
builder.Services.AddWesRuntime(builder.Configuration);
builder.Services.AddPlatformDependencyHealth(builder.Configuration);
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddHealthChecks()
    .AddCheck("ApplicationNotAssembled",
        () => HealthCheckResult.Unhealthy("Business persistence and control modules have not been assembled."),
        tags: ["application"]);

var app = builder.Build();
app.UseWesRuntime();
app.MapHealthChecks("/health/live", new HealthCheckOptions
{
    Predicate = _ => false
});
app.MapHealthChecks("/health/ready", new HealthCheckOptions
{
    Predicate = registration => registration.Tags.Contains("dependency") || registration.Tags.Contains("application")
});
app.MapHealthChecks("/health/dependencies", new HealthCheckOptions
{
    Predicate = registration => registration.Tags.Contains("dependency")
});
app.Run();

/// <summary>
/// Exposes the composition root to the HTTP integration-test host.
/// No warehouse or equipment behavior belongs in this type.
/// </summary>
public partial class Program;
