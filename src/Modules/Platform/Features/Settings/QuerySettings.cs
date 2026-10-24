using System.Collections.Immutable;
using Microsoft.EntityFrameworkCore;
using Wes.Platform.Features.Authorization;
using Wes.Platform.Features.Tenancy;
using Wes.Platform.Infrastructure.Persistence;

namespace Wes.Platform.Features.Settings;

/// <summary>Nonsecret catalog projection; physical evidence absence is explicit and never becomes a fallback value.</summary>
public sealed record SettingRow(Guid Id, string? Key, string? Value, string? Source, string? Version, string? Kind, string? State);
/// <summary>Distinguishes permission denial from an empty configured catalog.</summary>
public sealed record SettingsPage(bool Authorized, ImmutableArray<SettingRow> Rows);

/// <summary>Owner-authorized read of tenant settings; secrets and arbitrary deployment configuration are not queried.</summary>
public sealed class QuerySettings(PlatformDbContext database, TimeProvider clock)
{
    /// <summary>Reads a bounded stable page with current tenant-wide field projection.</summary>
    public async Task<SettingsPage> ReadAsync(TenantContext context, int offset = 0, int limit = 50,
        CancellationToken cancellationToken = default, string? kind = null)
    {
        if (offset < 0 || limit is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(limit));
        var fields = await new CurrentAuthorization(database, clock).VisibleFieldsAsync(context, "platform.settings.read",
            new ResourceScope(database.TenantId, Guid.Empty, Guid.Empty), cancellationToken);
        if (!fields.Contains("Id")) return new(false, []);
        if (kind is not (null or "P" or "H")) throw new ArgumentOutOfRangeException(nameof(kind));
        if (kind is not null && !fields.Contains("Key")) return new(false, []);
        var key = fields.Contains("Key"); var value = fields.Contains("Value");
        var source = fields.Contains("Source"); var version = fields.Contains("Version");
        var query = database.Settings.AsNoTracking();
        if (kind is not null) query = query.Where(item => (item.Key == "device.feedbackFreshnessMilliseconds") == (kind == "H"));
        var rows = await query.OrderBy(item => item.Key).ThenBy(item => item.Id)
            .Skip(offset).Take(limit)
            .Select(item => new SettingRow(item.Id, key ? item.Key : null, value ? item.Value : null,
                source ? item.Source : null, version ? item.Version : null,
                key ? item.Key == "device.feedbackFreshnessMilliseconds" ? "H" : "P" : null,
                source ? item.Source == "Unverified" ? "Unverified" : "Configured" : null))
            .ToListAsync(cancellationToken);
        return new(true, rows.ToImmutableArray());
    }
}
