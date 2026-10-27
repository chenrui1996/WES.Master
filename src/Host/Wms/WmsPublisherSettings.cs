using System.Collections.Immutable;
using Wes.Host.Platform;

namespace Wes.Host.Wms;

/// <summary>Deployment-only startup snapshot; no tenant discovery, secret default or runtime configuration override.</summary>
public sealed class WmsPublisherSettings
{
    private readonly TenantWorkerSchedule _schedule;
    private WmsPublisherSettings(TenantWorkerSchedule schedule) => _schedule = schedule;
    /// <summary>Explicit opt-in; disabled composition registers no publisher.</summary>
    public bool Enabled => _schedule.Enabled;
    /// <summary>Exact trusted owner allowlist.</summary>
    public ImmutableArray<Guid> TenantIds => _schedule.TenantIds;
    /// <summary>Fixed delay after the entire serial pass.</summary>
    public TimeSpan Interval => _schedule.Interval;
    /// <summary>One bounded selection per tenant and tick, not a drain loop.</summary>
    public int BatchSize => _schedule.BatchSize;
    /// <summary>Rejects malformed supplied configuration even when disabled; enabled publishing needs Identity, Outbox and MessageQueue.</summary>
    public static WmsPublisherSettings Load(IConfiguration configuration)
    {
        var schedule = TenantWorkerSchedule.Load(configuration, "Wes:WmsPublisher", "WMS publisher", 1, 3600, 100);
        if (schedule.Enabled && new[] { "Outbox", "MessageQueue" }.Any(capability =>
            !bool.TryParse(configuration[$"Features:Capabilities:{capability}"], out var enabled) || !enabled))
            throw new InvalidOperationException("WMS publisher requires enabled Outbox and MessageQueue capabilities.");
        return new(schedule);
    }
}
