using System.Collections.Immutable;
using System.Globalization;

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
        var section = configuration.GetSection("Wes:PlatformRetention");
        var children = section.GetChildren().ToArray();
        string[] supportedKeys = ["Enabled", "TenantIds", "IntervalSeconds", "BatchSize"];
        if (section.Value is not null || children.Any(child => !supportedKeys.Contains(child.Key, StringComparer.OrdinalIgnoreCase)))
            throw Invalid();
        bool Has(string key) => children.Any(child => child.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
        string? Scalar(string key)
        {
            if (section.GetSection(key).GetChildren().Any()) throw Invalid();
            return section[key];
        }
        var enabled = false;
        if (Has("Enabled") && !bool.TryParse(Scalar("Enabled"), out enabled)) throw Invalid();

        int Number(string key, int minimum, int maximum)
        {
            if (!Has(key) && !enabled) return 0;
            if (!int.TryParse(Scalar(key), NumberStyles.None, CultureInfo.InvariantCulture, out var number)
                || number < minimum || number > maximum) throw Invalid();
            return number;
        }
        var interval = Number("IntervalSeconds", 60, 86400);
        var batch = Number("BatchSize", 1, 200);
        var tenants = ImmutableArray.CreateBuilder<Guid>();
        if (Has("TenantIds") || enabled)
        {
            var tenantSection = section.GetSection("TenantIds");
            var entries = tenantSection.GetChildren().ToArray();
            if (tenantSection.Value is not null || entries.Length is < 1 or > 100) throw Invalid();
            for (var index = 0; index < entries.Length; index++)
            {
                var entry = entries[index];
                if (entry.Key != index.ToString(CultureInfo.InvariantCulture) || entry.GetChildren().Any()
                    || !Guid.TryParseExact(entry.Value, "D", out var tenant) || tenant == Guid.Empty || tenants.Contains(tenant))
                    throw Invalid();
                tenants.Add(tenant);
            }
        }
        if (enabled && (!bool.TryParse(configuration["Wes:Identity:Enabled"], out var identity) || !identity))
            throw new InvalidOperationException("Platform retention requires enabled Identity configuration.");
        return new(enabled, tenants.ToImmutable(), TimeSpan.FromSeconds(interval), batch);
    }

    private static InvalidOperationException Invalid() => new("Platform retention configuration is invalid.");
}
