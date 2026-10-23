using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Wes.Platform.Infrastructure.Health;

namespace Wes.Platform;

/// <summary>Composes Platform-owned capabilities without exposing persistence implementation to Host.</summary>
public static class PlatformModule
{
    /// <summary>Registers a read-only, bounded dependency probe; never enables business or device writes.</summary>
    public static IServiceCollection AddPlatformDependencyHealth(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<DatabaseHealthOptions>()
            .Configure(options =>
            {
                options.ConnectionString = configuration["DatabaseHealth:ConnectionString"] ?? "";
                var timeout = configuration["DatabaseHealth:TimeoutSeconds"];
                if (timeout is not null)
                {
                    if (!int.TryParse(timeout, System.Globalization.NumberStyles.Integer,
                        System.Globalization.CultureInfo.InvariantCulture, out var seconds))
                    {
                        // ConfigurationBinder's conversion exception includes the original value.
                        throw new OptionsValidationException(Options.DefaultName, typeof(DatabaseHealthOptions),
                            ["DatabaseHealth timeout must be an integer between one and five seconds."]);
                    }

                    options.TimeoutSeconds = seconds;
                }
            })
            .Validate(options => options.TimeoutSeconds is >= 1 and <= 5,
                "DatabaseHealth timeout must be between one and five seconds.")
            .Validate(DatabaseHealthOptions.HasValidConnection,
                "DatabaseHealth requires a valid authorized connection targeting wes_test.")
            .ValidateOnStart();
        services.AddHealthChecks().AddCheck<MySqlDependencyHealthCheck>("mysql", tags: ["dependency"]);
        return services;
    }
}
