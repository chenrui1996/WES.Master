using Wes.Host.Identity;
using Wes.Platform;

namespace Wes.Host.Platform;

/// <summary>Application-only bridge to a fresh tenant owner; no persistence types escape Platform.</summary>
public interface IPlatformRetentionRunner
{
    /// <summary>Commits at most one batch of each receipt kind, returning only durable counts.</summary>
    Task<RetentionMaintenanceResult> RunAsync(Guid tenant, int batch, CancellationToken cancellationToken);
}

/// <summary>Reuses Identity's clean owner factory rather than constructing or discovering tenant persistence.</summary>
public sealed class PlatformRetentionRunner(IdentityDatabase database) : IPlatformRetentionRunner
{
    /// <inheritdoc />
    public async Task<RetentionMaintenanceResult> RunAsync(Guid tenant, int batch, CancellationToken cancellationToken)
    {
        await using var owner = database.Open(tenant);
        return await owner.RemoveExpiredReceiptsAsync(batch, cancellationToken);
    }
}

/// <summary>Opt-in serial fixed-delay maintenance; unexpected failures follow the standard StopHost contract.</summary>
public sealed class PlatformRetentionWorker(PlatformRetentionSettings settings, IPlatformRetentionRunner runner,
    TimeProvider clock, ILogger<PlatformRetentionWorker> logger) : BackgroundService
{
    /// <summary>Performs one bounded pass through the explicit allowlist, never a backlog-draining loop.</summary>
    public async Task RunTickAsync(CancellationToken cancellationToken)
    {
        foreach (var tenant in settings.TenantIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var correlation = Guid.NewGuid();
            try
            {
                var result = await runner.RunAsync(tenant, settings.BatchSize, cancellationToken);
                logger.LogInformation(
                    "Platform retention committed for tenant {TenantId}, correlation {CorrelationId}: directory {DirectoryCount}, inbox {InboxCount}.",
                    tenant, correlation, result.DirectoryCount, result.InboxCount);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                logger.LogInformation("Platform retention cancelled for tenant {TenantId}, correlation {CorrelationId}.", tenant, correlation);
                throw;
            }
            catch (PlatformRetentionDatabaseException error)
            {
                logger.LogError("Platform retention failed for tenant {TenantId}, correlation {CorrelationId}: {FailureCategory}.",
                    tenant, correlation, error.IsTransient ? "database-transient" : "database-permanent");
                if (!error.IsTransient) throw;
            }
        }
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await RunTickAsync(stoppingToken);
            await Task.Delay(settings.Interval, clock, stoppingToken);
        }
    }
}

/// <summary>Registers no owner, timer or hosted maintenance when disabled.</summary>
public static class PlatformRetentionRegistration
{
    /// <summary>Validates deployment settings before registering the opt-in owner runner.</summary>
    public static IServiceCollection AddWesPlatformRetention(this IServiceCollection services, IConfiguration configuration)
    {
        var settings = PlatformRetentionSettings.Load(configuration);
        if (!settings.Enabled) return services;
        services.AddSingleton(settings);
        services.AddSingleton<IPlatformRetentionRunner, PlatformRetentionRunner>();
        services.AddHostedService<PlatformRetentionWorker>();
        return services;
    }
}
