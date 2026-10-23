using Wes.Platform.Domain;
using Wes.Platform.Features.Authorization;
using Xunit;

namespace Wes.Modules.Tests.Platform;

/// <summary>Role schema must reject invalid identifiers and stale writes without partial rule changes.</summary>
public sealed class AccessRoleTests
{
    /// <summary>Rule update uses an opaque version and disabling is an actual current role state.</summary>
    [Fact]
    [Trait("CaseId", "ACCESS-CURRENT-001")]
    public void Change_RequiresVersion_AndUpdatesWholeSnapshot()
    {
        var grant = AuthorizationTests.Grant(DataScope.All, [], ["id"]);
        var role = new AccessRole(Guid.NewGuid(), "warehouse-reader", [grant]);
        var version = role.Version;
        Assert.Equal(SettingDecision.VersionRequired, role.Change(null, false, []));

        Assert.Equal(SettingDecision.Changed, role.Change(version, false, []));

        Assert.False(role.Enabled);
        Assert.Empty(role.Rules);
        Assert.NotEqual(version, role.Version);
        Assert.Equal(SettingDecision.VersionConflict, role.Change(version, true, [grant]));
        Assert.False(role.Enabled);
    }

    /// <summary>Malformed grant metadata is rejected rather than accepted and interpreted as a wildcard later.</summary>
    [Fact]
    [Trait("CaseId", "ACCESS-SCOPE-001")]
    public void Role_RejectsMalformedSchemaWithoutEchoingPayload()
    {
        var grant = AuthorizationTests.Grant(DataScope.All, [], ["id"]);
        Assert.Throws<ArgumentException>(() => new AccessRole(Guid.Empty, "reader", [grant]));
        Assert.Throws<ArgumentException>(() => new AccessRole(Guid.NewGuid(), "secret-marker;", [grant]));
        Assert.Throws<ArgumentException>(() => new AccessRole(Guid.NewGuid(), "reader",
            [grant with { Permission = "*" }]));
        Assert.Throws<ArgumentException>(() => new AccessRole(Guid.NewGuid(), "reader",
            [grant with { Scope = (DataScope)99 }]));
        Assert.Throws<ArgumentException>(() => new AccessRole(Guid.NewGuid(), "reader",
            [grant with { Fields = grant.Fields.Add("*") }]));
    }
}
