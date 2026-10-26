using System.Collections.Immutable;
using System.Globalization;

namespace Wes.Host.Platform;

internal sealed record TenantWorkerSchedule(bool Enabled, ImmutableArray<Guid> TenantIds, TimeSpan Interval, int BatchSize)
{
    internal static TenantWorkerSchedule Load(IConfiguration configuration, string sectionName, string label,
        int minimumInterval, int maximumInterval, int maximumBatch)
    {
        var section = configuration.GetSection(sectionName);
        var children = section.GetChildren().ToArray();
        string[] supportedKeys = ["Enabled", "TenantIds", "IntervalSeconds", "BatchSize"];
        InvalidOperationException Invalid() => new($"{label} configuration is invalid.");
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
        var interval = Number("IntervalSeconds", minimumInterval, maximumInterval);
        var batch = Number("BatchSize", 1, maximumBatch);
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
            throw new InvalidOperationException($"{label} requires enabled Identity configuration.");
        return new(enabled, tenants.ToImmutable(), TimeSpan.FromSeconds(interval), batch);
    }
}
