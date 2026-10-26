using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Serilog;
using Wes.Host.Identity;
using Wes.Host.Platform;
using Wes.Host.Runtime;
using Wes.Platform;

using var bootstrapLogger = new LoggerConfiguration().WriteTo.Console().CreateLogger();
bootstrapLogger.Information("WES Host starting");
var builder = WebApplication.CreateBuilder(args);
builder.Services.AddWesRuntime(builder.Configuration);
builder.Services.AddPlatformDependencyHealth(builder.Configuration);
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddWesIdentity(builder.Configuration);
builder.Services.AddWesPlatformRetention(builder.Configuration);
builder.Services.AddHealthChecks()
    .AddCheck("ApplicationNotAssembled",
        () => HealthCheckResult.Unhealthy("Business persistence and control modules have not been assembled."),
        tags: ["application"]);

await using var app = builder.Build();
if (args.Contains("--wes-provision=true", StringComparer.Ordinal))
{
    Environment.ExitCode = await LocalProvisioning.RunAsync(app.Services, app.Configuration, LocalProvisioning.ReadPassword) ? 0 : 1;
    return;
}
app.UseWesRuntime();
if (app.Configuration.GetValue<bool>("Wes:Identity:Enabled"))
{
    IdentityKeyRing.Validate(app.Configuration["Wes:Identity:KeyRingPath"], app.Environment.IsProduction());
    _ = app.Services.GetRequiredService<IdentitySettings>();
    app.Use(async (http, next) =>
    {
        if (http.Request.Path.StartsWithSegments("/api/identity") || http.Request.Path.StartsWithSegments("/api/platform"))
            http.Response.OnStarting(() =>
            {
                http.Response.Headers.CacheControl = "no-store";
                return Task.CompletedTask;
            });
        await next(http);
    });
    app.UseAuthentication();
    app.UseAuthorization();
    app.MapWesIdentity();
    app.MapWesPlatform();
    app.MapOperationsWeb();
}
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
app.MapFeatureCatalog();
app.Run();

/// <summary>
/// Exposes the composition root to the HTTP integration-test host.
/// No warehouse or equipment behavior belongs in this type.
/// </summary>
public partial class Program;
