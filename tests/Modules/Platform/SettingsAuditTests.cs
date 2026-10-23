using Wes.Platform.Domain;
using Xunit;

namespace Wes.Modules.Tests.Platform;

/// <summary>Rejects bad scalar/secret/H/version inputs without changing a nonsecret setting or reflecting payloads.</summary>
public sealed class SettingsAuditTests
{
    /// <summary>Representative approved P values have schema validation and explicit override provenance.</summary>
    [Fact]
    [Trait("CaseId", "SETTINGS-VERSION-001")]
    public void ValidChange_ChangesVersionAndSource()
    {
        var setting = new VersionedSetting(Guid.NewGuid(), "query.snapshotTtlSeconds");
        var before = setting.Version;
        Assert.Equal("600", setting.Value);

        Assert.Equal(SettingDecision.Changed, setting.Change(before, "300"));

        Assert.Equal("300", setting.Value);
        Assert.NotEqual(before, setting.Version);
        Assert.Equal("AuthorizedOverride", setting.Source);
        Assert.Equal(SettingDecision.VersionConflict, setting.Change(before, "400"));
        Assert.Equal("300", setting.Value);
    }

    /// <summary>No missing version or invalid value may silently fall back to a default or mutate the row.</summary>
    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("301.2")]
    [InlineData("secret-marker")]
    [InlineData("90000000000")]
    [Trait("CaseId", "SETTINGS-VERSION-001")]
    public void InvalidValue_LeavesStateUntouched(string value)
    {
        var setting = new VersionedSetting(Guid.NewGuid(), "query.snapshotTtlSeconds");
        var version = setting.Version;

        Assert.Equal(SettingDecision.InvalidValue, setting.Change(version, value));
        Assert.Equal(version, setting.Version);
        Assert.Equal("600", setting.Value);
        Assert.Equal(SettingDecision.VersionRequired, setting.Change(null, "300"));
    }

    /// <summary>Hardware freshness cannot acquire an approval merely by receiving a valid numeric value.</summary>
    [Fact]
    [Trait("CaseId", "SETTINGS-H-001")]
    public void PhysicalSetting_RemainsUnverified()
    {
        var setting = new VersionedSetting(Guid.NewGuid(), "device.feedbackFreshnessMilliseconds");
        var before = setting.Version;

        Assert.Null(setting.Value);
        Assert.Equal("Unverified", setting.Source);
        Assert.Equal(SettingDecision.Unverified, setting.Change(before, "500"));
        Assert.Equal(before, setting.Version);
    }

    /// <summary>Secret-looking keys and unknown keys are never writable generic configuration.</summary>
    [Theory]
    [InlineData("Database.ConnectionString")]
    [InlineData("jwt.signingKey")]
    [InlineData("custom.setting")]
    [Trait("CaseId", "SETTINGS-VERSION-001")]
    public void UnknownOrSecretKey_IsRejectedWithoutValueEcho(string key)
    {
        Assert.Throws<ArgumentException>(() => new VersionedSetting(Guid.NewGuid(), key));
    }

    /// <summary>The audit cannot carry arbitrary raw before/after payloads disguised as opaque versions.</summary>
    [Fact]
    [Trait("CaseId", "AUDIT-IMMUTABLE-001")]
    public void Audit_RejectsSecretInVersionAndMissingOwner()
    {
        var tenant = Guid.NewGuid();
        Assert.Throws<ArgumentException>(() => new PlatformAudit(tenant, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            PlatformAuditAction.SettingChanged, null, "secret-marker", DateTimeOffset.UtcNow));
        Assert.Throws<ArgumentException>(() => new PlatformAudit(Guid.Empty, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            PlatformAuditAction.RoleChanged, null, Guid.NewGuid().ToString("D"), DateTimeOffset.UtcNow));
    }
}
