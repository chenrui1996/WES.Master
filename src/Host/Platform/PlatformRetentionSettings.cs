using System.Collections.Immutable;

namespace Wes.Host.Platform;

/// <summary>Immutable, deployment-only allowlist and bounded schedule; absent configuration is disabled.</summary>
public sealed class PlatformRetentionSettings
{
    private PlatformRetentionSettings(bool enabled, ImmutableArray<Guid> tenants, TimeSpan interval, int batchSize)
    {
        Enabled = enabled;
        TenantIds = tenants;
        Interval = interval;
        BatchSize = batchSize;
    }

    /// <summary>Only an explicitly valid true value enables registration.</summary>
    public bool Enabled { get; }
    /// <summary>Explicit nonempty tenant owners, without discovery or wildcard expansion.</summary>
    public ImmutableArray<Guid> TenantIds { get; }
    /// <summary>Fixed delay after each completed tick.</summary>
    public TimeSpan Interval { get; }
    /// <summary>Maximum receipts per kind and tenant in one tick.</summary>
    public int BatchSize { get; }

    /// <summary>Rejects malformed supplied values even when disabled; enabled operation requires all settings.</summary>
    public static PlatformRetentionSettings Load(IConfiguration configuration)
    {
        var schedule = TenantWorkerSchedule.Load(configuration, "Wes:PlatformRetention", "Platform retention", 60, 86400, 200);
        return new(schedule.Enabled, schedule.TenantIds, schedule.Interval, schedule.BatchSize);
    }
}
