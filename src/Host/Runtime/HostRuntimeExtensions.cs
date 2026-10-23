using Serilog;
using Serilog.Events;

namespace Wes.Host.Runtime;

/// <summary>Composes the common HTTP correlation, diagnostics and safe failure boundary.</summary>
public static class HostRuntimeExtensions
{
    /// <summary>Registers runtime diagnostics; never registers business state or device drivers.</summary>
    public static IServiceCollection AddWesRuntime(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddProblemDetails(options => options.CustomizeProblemDetails = problem =>
        {
            problem.ProblemDetails.Extensions["correlationId"] = problem.HttpContext.TraceIdentifier;
            problem.ProblemDetails.Extensions.TryAdd("code",
                SafeProblem.For(problem.ProblemDetails.Status ?? 500).Code);
        });
        services.AddSerilog((provider, logger) => logger
            .ReadFrom.Configuration(configuration)
            .ReadFrom.Services(provider)
            .Enrich.FromLogContext()
            .Enrich.With<TransportLogAllowlist>()
            // Framework request/exception diagnostics may contain raw URLs and exception messages.
            // Owned boundary events below replace those with safe, correlated summaries.
            .MinimumLevel.Override("Microsoft.AspNetCore", LogEventLevel.Fatal)
            .MinimumLevel.Override("Microsoft.Extensions.Diagnostics.HealthChecks", LogEventLevel.Fatal)
            .WriteTo.Console());
        return services;
    }

    /// <summary>Installs the shared HTTP boundary before module endpoints.</summary>
    public static IApplicationBuilder UseWesRuntime(this IApplicationBuilder app) =>
        app.UseMiddleware<HttpRuntimeBoundary>();
}
