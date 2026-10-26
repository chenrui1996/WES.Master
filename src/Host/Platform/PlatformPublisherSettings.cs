using System.Collections.Immutable;

namespace Wes.Host.Platform;

/// <summary>Immutable deployment-only schedule; transport and storage are never silently supplied.</summary>
public sealed class PlatformPublisherSettings
{
    private readonly TenantWorkerSchedule _schedule;
    private PlatformPublisherSettings(TenantWorkerSchedule schedule) => _schedule = schedule;
    /// <summary>Explicit opt-in.</summary>
    public bool Enabled => _schedule.Enabled;
    /// <summary>Exact tenant owners, never discovery.</summary>
    public ImmutableArray<Guid> TenantIds => _schedule.TenantIds;
    /// <summary>Fixed delay after the entire pass.</summary>
    public TimeSpan Interval => _schedule.Interval;
    /// <summary>Maximum selected events per tenant and tick.</summary>
    public int BatchSize => _schedule.BatchSize;

    /// <summary>Validates all supplied settings and the enabled deployment's capabilities before composition.</summary>
    public static PlatformPublisherSettings Load(IConfiguration configuration)
    {
        var schedule = TenantWorkerSchedule.Load(configuration, "Wes:PlatformPublisher", "Platform publisher", 1, 3600, 100);
        if (schedule.Enabled && new[] { "Outbox", "MessageQueue" }.Any(capability =>
                !bool.TryParse(configuration[$"Features:Capabilities:{capability}"], out var enabled) || !enabled))
            throw new InvalidOperationException("Platform publisher requires enabled Outbox and MessageQueue capabilities.");
        return new(schedule);
    }
}
