namespace Wes.Platform.Domain;

/// <summary>Expected versioned-setting decisions; no rejected change mutates stored state.</summary>
public enum SettingDecision
{
    /// <summary>The current session and scoped grants do not authorize this operation.</summary>
    Forbidden,
    /// <summary>A schema-validated change is staged for the owner transaction.</summary>
    Changed,
    /// <summary>The key is not part of the owner-reviewed nonsecret catalog.</summary>
    UnknownSetting,
    /// <summary>The current version was not supplied.</summary>
    VersionRequired,
    /// <summary>The supplied version no longer identifies the current value.</summary>
    VersionConflict,
    /// <summary>The type/range or approved fixed rule rejects the value.</summary>
    InvalidValue,
    /// <summary>Physical evidence is missing; client configuration cannot establish it.</summary>
    Unverified,
    /// <summary>The operation UUID already identifies a different nonsecret request.</summary>
    IdempotencyConflict
}

/// <summary>Tenant-owned typed nonsecret setting. Hardware evidence and secrets have no writable value path here.</summary>
public sealed class VersionedSetting
{
    private VersionedSetting() { }

    /// <summary>Creates an owner-reviewed key with its approved initial value or explicit Unverified state.</summary>
    public VersionedSetting(Guid tenantId, string key)
    {
        if (tenantId == Guid.Empty)
        {
            throw new ArgumentException("A setting requires a trusted tenant.");
        }
        var baseline = key switch
        {
            "query.snapshotTtlSeconds" => "600",
            "dictionary.cacheTtlSeconds" => "60",
            "device.feedbackFreshnessMilliseconds" => null,
            _ => throw new ArgumentException("The setting is not in the approved nonsecret catalog.")
        };
        Id = Guid.NewGuid();
        TenantId = tenantId;
        Key = key;
        Value = baseline;
        Source = baseline is null ? "Unverified" : "ApprovedBaseline";
        Version = Guid.NewGuid().ToString("D");
    }

    /// <summary>Stable reference for audit, independent of the setting's display key.</summary>
    public Guid Id { get; private set; }
    /// <summary>Trusted tenant owner, never an implicit zero/shared scope.</summary>
    public Guid TenantId { get; private set; }
    /// <summary>Stable catalog key, not arbitrary user input.</summary>
    public string Key { get; private set; } = "";
    /// <summary>Canonical invariant-culture scalar, or null when physical evidence is absent.</summary>
    public string? Value { get; private set; }
    /// <summary>Opaque optimistic version shared by settings and the corresponding audit transition.</summary>
    public string Version { get; private set; } = "";
    /// <summary>Explicit source distinguishes approved baseline from an authorized override.</summary>
    public string Source { get; private set; } = "ApprovedBaseline";

    /// <summary>Stages a validated change without committing; audit must be added in the same owner transaction.</summary>
    public SettingDecision Change(string? expectedVersion, string value)
    {
        if (string.IsNullOrWhiteSpace(expectedVersion))
        {
            return SettingDecision.VersionRequired;
        }
        if (expectedVersion != Version)
        {
            return SettingDecision.VersionConflict;
        }
        if (Key == "device.feedbackFreshnessMilliseconds")
        {
            return SettingDecision.Unverified;
        }
        if (!int.TryParse(value, System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture, out var parsed) || parsed <= 0)
        {
            return SettingDecision.InvalidValue;
        }

        Value = parsed.ToString(System.Globalization.CultureInfo.InvariantCulture);
        Source = "AuthorizedOverride";
        Version = Guid.NewGuid().ToString("D");
        return SettingDecision.Changed;
    }
}
