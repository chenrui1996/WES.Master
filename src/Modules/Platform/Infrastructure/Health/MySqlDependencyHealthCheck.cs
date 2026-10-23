using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MySql.Data.MySqlClient;

namespace Wes.Platform.Infrastructure.Health;

/// <summary>Read-only MySQL dependency probe. It cannot migrate, create a database or issue control writes.</summary>
public sealed class MySqlDependencyHealthCheck(
    IOptions<DatabaseHealthOptions> options,
    ILogger<MySqlDependencyHealthCheck> logger) : IHealthCheck
{
    /// <summary>
    /// Checks a real connection with a bounded query. Missing configuration and connection failures
    /// are explicitly unhealthy; cancellation propagates and no successful state is cached.
    /// </summary>
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var settings = options.Value;
        if (string.IsNullOrWhiteSpace(settings.ConnectionString))
        {
            return HealthCheckResult.Unhealthy("DatabaseNotConfigured");
        }

        if (!DatabaseHealthOptions.HasValidConnection(settings) || settings.TimeoutSeconds is < 1 or > 5)
        {
            logger.LogWarning("Dependency probe failed {Code}", "DatabaseConfigurationInvalid");
            return HealthCheckResult.Unhealthy("DatabaseConfigurationInvalid");
        }

        var builder = new MySqlConnectionStringBuilder(settings.ConnectionString)
        {
            ConnectionTimeout = (uint)settings.TimeoutSeconds,
            DefaultCommandTimeout = (uint)settings.TimeoutSeconds,
            Pooling = false,
            PersistSecurityInfo = false,
            AllowLoadLocalInfile = false
        };
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(settings.TimeoutSeconds));
        try
        {
            await using var connection = new MySqlConnection(builder.ConnectionString);
            await connection.OpenAsync(deadline.Token);
            await using var command = new MySqlCommand("SELECT 1", connection);
            var result = await command.ExecuteScalarAsync(deadline.Token);
            if (Convert.ToInt32(result, System.Globalization.CultureInfo.InvariantCulture) == 1)
            {
                return HealthCheckResult.Healthy();
            }

            logger.LogWarning("Dependency probe failed {Code}", "UnexpectedProbeResult");
            return HealthCheckResult.Unhealthy("DependencyUnavailable");
        }
        catch (MySqlException error)
        {
            cancellationToken.ThrowIfCancellationRequested();
            logger.LogWarning("Dependency probe failed {Code} with driver code {DriverCode}",
                "DependencyUnavailable", error.Number);
            return HealthCheckResult.Unhealthy("DependencyUnavailable");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("Dependency probe failed {Code}", "DependencyTimeout");
            return HealthCheckResult.Unhealthy("DependencyUnavailable");
        }
    }
}
