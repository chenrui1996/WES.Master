using Wes.Host.Identity;
using Wes.Platform.Messaging;

namespace Wes.Host.Platform;

/// <summary>Public application-only bridge, not a persistence handle.</summary>
public interface IPlatformPublisherRunner
{
    /// <summary>Creates a clean owner for one bounded tenant dispatch.</summary>
    Task<PlatformPublishBatchResult> RunAsync(Guid tenant, int batch, CancellationToken cancellationToken);
}

/// <summary>Never shares an EF context between tenants, ticks or concurrent instances.</summary>
public sealed class PlatformPublisherRunner(IdentityDatabase database, IPlatformOutboxTransport transport) : IPlatformPublisherRunner
{
    /// <inheritdoc />
    public async Task<PlatformPublishBatchResult> RunAsync(Guid tenant, int batch, CancellationToken cancellationToken)
    {
        await using var owner = database.Open(tenant);
        return await owner.PublishDueEventsAsync(transport, batch, cancellationToken);
    }
}

/// <summary>Opt-in serial publishing with cooperative per-tenant budget and durable owner-local retries.</summary>
public sealed class PlatformPublisherWorker(PlatformPublisherSettings settings, IPlatformPublisherRunner runner,
    TimeProvider clock, ILogger<PlatformPublisherWorker> logger) : BackgroundService
{
    /// <summary>Attempts each allowlisted tenant exactly once without draining a backlog.</summary>
    public async Task RunTickAsync(CancellationToken cancellationToken)
    {
        foreach (var tenant in settings.TenantIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var correlation = Guid.NewGuid();
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60), clock);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
            try
            {
                var result = await runner.RunAsync(tenant, settings.BatchSize, linked.Token);
                linked.Token.ThrowIfCancellationRequested();
                logger.LogInformation(
                    "Platform delivery transitions persisted for tenant {TenantId}, correlation {CorrelationId}: acknowledged {Published}, retry {RetryScheduled}, quarantined {Quarantined}, not claimed {NotClaimed}.",
                    tenant, correlation, result.Published, result.RetryScheduled, result.Quarantined, result.NotClaimed);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                logger.LogInformation("Platform publishing cancelled for tenant {TenantId}, correlation {CorrelationId}.", tenant, correlation);
                throw;
            }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested)
            {
                logger.LogWarning("Platform publishing failed for tenant {TenantId}, correlation {CorrelationId}: {FailureCategory}.",
                    tenant, correlation, "tick-deadline");
            }
            catch (PlatformPublisherDatabaseException error)
            {
                logger.LogError("Platform publishing failed for tenant {TenantId}, correlation {CorrelationId}: {FailureCategory}.",
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

/// <summary>No transport fallback, owner or timer is registered when disabled.</summary>
public static class PlatformPublisherRegistration
{
    /// <summary>Requires a unique singleton transport supplied by explicit infrastructure composition.</summary>
    public static IServiceCollection AddWesPlatformPublisher(this IServiceCollection services, IConfiguration configuration)
    {
        var settings = PlatformPublisherSettings.Load(configuration);
        if (!settings.Enabled) return services;
        var transports = services.Where(item => item.ServiceType == typeof(IPlatformOutboxTransport) && !item.IsKeyedService).ToArray();
        if (transports.Length != 1 || transports[0].Lifetime != ServiceLifetime.Singleton)
            throw new InvalidOperationException("Platform publisher requires exactly one explicitly configured singleton transport.");
        services.AddSingleton(settings);
        services.AddSingleton<IPlatformPublisherRunner, PlatformPublisherRunner>();
        services.AddHostedService<PlatformPublisherWorker>();
        return services;
    }
}
