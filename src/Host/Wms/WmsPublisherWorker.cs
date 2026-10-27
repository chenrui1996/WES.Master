using Wes.Host.Identity;
using Wes.Wms;
using Wes.Wms.Features.Ledger;
using Wes.Wms.Messaging;

namespace Wes.Host.Wms;

/// <summary>Public WMS application bridge, without a persistence handle or cross-owner transaction.</summary>
public interface IWmsPublisherRunner
{
    /// <summary>Creates a new independent owner for one bounded tenant batch; no migrations or business-completion inference.</summary>
    Task<WmsPublishBatchResult> RunAsync(Guid tenant, int batch, CancellationToken cancellationToken);
}

/// <summary>Stateless deployment bridge; no DbContext is retained between tenants, ticks or instances.</summary>
public sealed class WmsPublisherRunner(IdentitySettings settings, IWmsOutboxTransport transport, TimeProvider clock,
    ILoggerFactory logs) : IWmsPublisherRunner
{
    /// <inheritdoc />
    public async Task<WmsPublishBatchResult> RunAsync(Guid tenant, int batch, CancellationToken cancellationToken)
    {
        await using var owner = new WmsPublishingScope(settings.ConnectionString, tenant, transport, clock, logs);
        return await owner.DispatchDueAsync(batch, cancellationToken);
    }
}

/// <summary>Opt-in serial publisher with cooperative per-tenant budget and owner-local durable recovery.</summary>
public sealed class WmsPublisherWorker(WmsPublisherSettings settings, IWmsPublisherRunner runner,
    TimeProvider clock, ILogger<WmsPublisherWorker> logger) : BackgroundService
{
    /// <summary>Runs each allowlisted tenant once. Expected transient database failures continue; permanent/program errors stop the caller.</summary>
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
                logger.Log(result.Unconfirmed > 0 ? LogLevel.Warning : LogLevel.Information,
                    "WMS publisher batch for tenant {TenantId}, correlation {CorrelationId}: acknowledged {Published}, retry {RetryScheduled}, quarantined {Quarantined}, not claimed {NotClaimed}, unconfirmed {Unconfirmed}.",
                    tenant, correlation, result.Published, result.RetryScheduled, result.Quarantined, result.NotClaimed, result.Unconfirmed);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                logger.LogInformation("WMS publishing cancelled for tenant {TenantId}, correlation {CorrelationId}.", tenant, correlation);
                throw;
            }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested)
            {
                logger.LogWarning("WMS publishing failed for tenant {TenantId}, correlation {CorrelationId}: {FailureCategory}.",
                    tenant, correlation, "tick-deadline");
            }
            catch (WmsDatabaseException error)
            {
                logger.LogError("WMS publishing failed for tenant {TenantId}, correlation {CorrelationId}: {FailureCategory}.",
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

/// <summary>No worker, runner, timer, owner or fallback transport is registered when disabled.</summary>
public static class WmsPublisherRegistration
{
    /// <summary>Requires one explicitly supplied unkeyed singleton WMS transport; never borrows Platform's adapter.</summary>
    public static IServiceCollection AddWesWmsPublisher(this IServiceCollection services, IConfiguration configuration)
    {
        var settings = WmsPublisherSettings.Load(configuration);
        if (!settings.Enabled) return services;
        var transports = services.Where(item => item.ServiceType == typeof(IWmsOutboxTransport) && !item.IsKeyedService).ToArray();
        if (transports.Length != 1 || transports[0].Lifetime != ServiceLifetime.Singleton)
            throw new InvalidOperationException("WMS publisher requires exactly one explicitly configured singleton transport.");
        services.AddSingleton(settings);
        services.AddSingleton<IWmsPublisherRunner, WmsPublisherRunner>();
        services.AddHostedService<WmsPublisherWorker>();
        return services;
    }
}
