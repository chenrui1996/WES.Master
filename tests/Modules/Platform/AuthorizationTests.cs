using System.Collections.Immutable;
using System.Security.Claims;
using Wes.Platform.Domain;
using Wes.Platform.Features.Authorization;
using Wes.Platform.Features.Tenancy;
using Xunit;

namespace Wes.Modules.Tests.Platform;

/// <summary>Independent scope/field/delegation counterexamples; no menu or role-ID shortcut can grant access.</summary>
public sealed class AuthorizationTests
{
    private static readonly Guid Tenant = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Actor = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid D1 = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid D2 = Guid.Parse("44444444-4444-4444-4444-444444444444");

    /// <summary>Field permission in one department must not leak through another role's wider ordinary read.</summary>
    [Fact]
    [Trait("CaseId", "ACCESS-FIELD-001")]
    public void Fields_AreMergedOnlyAfterResourceScopeMatches()
    {
        var grants = new[]
        {
            Grant(DataScope.Departments, [D1], ["id", "cost"]),
            Grant(DataScope.Departments, [D2], ["id", "name"])
        };

        var result = RolePolicy.VisibleFields(Tenant, Actor, "wms.stock.read", new(Tenant, Actor, D2), grants);

        Assert.Equal(new[] { "id", "name" }, result.Order(StringComparer.Ordinal));
    }

    /// <summary>Explicit All is bounded to the tenant; no tenant or no actor is fail-closed.</summary>
    [Theory]
    [InlineData(true, true, true, 1)]
    [InlineData(false, true, true, 0)]
    [InlineData(true, false, true, 0)]
    [InlineData(true, true, false, 0)]
    [Trait("CaseId", "ACCESS-SCOPE-001")]
    public void All_RemainsBoundedToTrustedContext(bool sameTenant, bool hasTenant, bool hasActor, int count)
    {
        var resource = new ResourceScope(sameTenant ? Tenant : Guid.NewGuid(), Guid.NewGuid(), D2);

        Assert.Equal(count, RolePolicy.VisibleFields(hasTenant ? Tenant : Guid.Empty,
            hasActor ? Actor : Guid.Empty, "wms.stock.read", resource, [Grant(DataScope.All, [], ["id"])]).Count);
    }

    /// <summary>Missing grants/permissions and empty department scope cannot be interpreted as full access.</summary>
    [Fact]
    [Trait("CaseId", "ACCESS-SCOPE-001")]
    public void EmptyScopeOrMissingPermission_NeverMeansAll()
    {
        var resource = new ResourceScope(Tenant, Actor, D1);
        Assert.Empty(RolePolicy.VisibleFields(Tenant, Actor, "wms.stock.read", resource, []));
        Assert.Empty(RolePolicy.VisibleFields(Tenant, Actor, "wms.stock.read", resource,
            [Grant(DataScope.Departments, [], ["id"])]));
        Assert.Empty(RolePolicy.VisibleFields(Tenant, Actor, "wms.stock.write", resource,
            [Grant(DataScope.All, [], ["id"])]));
        Assert.Empty(RolePolicy.VisibleFields(Tenant, Actor, "wms.stock.read", new(Tenant, Guid.NewGuid(), D1),
            [Grant(DataScope.Own, [], ["id"])]));
        Assert.Contains("id", RolePolicy.VisibleFields(Tenant, Actor, "wms.stock.read", resource,
            [Grant(DataScope.Own, [], ["id"])]));
    }

    /// <summary>Delegation may narrow but never enlarge permission, scope or allowed fields.</summary>
    [Fact]
    [Trait("CaseId", "ACCESS-DELEGATE-001")]
    public void Delegate_RequiresCoveringGrant()
    {
        var caller = new[] { Grant(DataScope.Departments, [D1, D2], ["id", "name"]) };
        Assert.True(RolePolicy.CanDelegate(caller, [Grant(DataScope.Departments, [D1], ["id"])]));
        Assert.False(RolePolicy.CanDelegate(caller, [Grant(DataScope.All, [], ["id"])]));
        Assert.False(RolePolicy.CanDelegate(caller, [Grant(DataScope.Departments, [Guid.NewGuid()], ["id"])]));
        Assert.False(RolePolicy.CanDelegate(caller, [Grant(DataScope.Departments, [D1], ["cost"])]));
        Assert.False(RolePolicy.CanDelegate(caller,
            [Grant(DataScope.Departments, [D1], ["id"]) with { Permission = "platform.role.write" }]));
    }

    /// <summary>A direct internal caller must not bypass the persisted-role schema by constructing malformed grants.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    [Trait("CaseId", "ACCESS-DELEGATE-001")]
    public void MalformedGrant_IsRejectedBeforePolicyEvaluation(bool invalidScope)
    {
        var invalid = invalidScope
            ? Grant((DataScope)99, [], ["id"])
            : Grant(DataScope.All, [], ["*"]);
        var caller = new[] { Grant(DataScope.All, [], ["id"]) };
        Assert.Throws<ArgumentException>(() => RolePolicy.CanDelegate(caller, [invalid]));
        Assert.Throws<ArgumentException>(() => RolePolicy.CanDelegate([invalid], caller));
        Assert.Throws<ArgumentException>(() =>
            RolePolicy.VisibleFields(Tenant, Actor, "wms.stock.read", new(Tenant, Actor, D1), [invalid]));
    }

    /// <summary>A caller-supplied collection comparer cannot redefine the owner's case-sensitive field identifiers.</summary>
    [Fact]
    [Trait("CaseId", "ACCESS-DELEGATE-001")]
    public void Delegate_UsesOrdinalFieldIdentityRegardlessOfCollectionComparer()
    {
        var caller = new[] { Grant(DataScope.All, [], ["id"]) };
        var requested = Grant(DataScope.All, [], ["ID"]) with
        {
            Fields = ImmutableHashSet.Create(StringComparer.OrdinalIgnoreCase, "ID")
        };

        Assert.False(RolePolicy.CanDelegate(caller, [requested]));
    }

    /// <summary>Caller-provided claim ambiguity must not create a convenient tenant fallback.</summary>
    [Theory]
    [InlineData("unauthenticated")]
    [InlineData("missing")]
    [InlineData("duplicate")]
    [InlineData("malformed")]
    [InlineData("multiple-identities")]
    [Trait("CaseId", "ACCESS-CONTEXT-001")]
    public void InvalidPrincipal_IsRejected(string variant)
    {
        var identity = Identity();
        if (variant == "unauthenticated")
        {
            identity = new ClaimsIdentity(identity.Claims);
        }
        if (variant == "missing")
        {
            identity.RemoveClaim(identity.FindFirst("tenantId")!);
        }
        if (variant == "duplicate")
        {
            identity.AddClaim(new Claim("tenantId", Guid.NewGuid().ToString("D")));
        }
        if (variant == "malformed")
        {
            identity.RemoveClaim(identity.FindFirst("tenantId")!);
            identity.AddClaim(new Claim("tenantId", Guid.Empty.ToString("D")));
        }
        var principal = variant == "multiple-identities"
            ? new ClaimsPrincipal(new[] { identity, Identity() }) : new ClaimsPrincipal(identity);

        Assert.Throws<UnauthorizedAccessException>(() => TenantContext.FromValidatedPrincipal(principal));
    }

    /// <summary>Only verified UUID identity fields become the context; menu claims are not permissions.</summary>
    [Fact]
    [Trait("CaseId", "ACCESS-CONTEXT-001")]
    public void ValidPrincipal_ProducesExactTrustedContext()
    {
        var context = TenantContext.FromValidatedPrincipal(new ClaimsPrincipal(Identity()));

        Assert.Equal(Tenant, context.TenantId);
        Assert.Equal(Actor, context.ActorId);
        Assert.NotEqual(Guid.Empty, context.SessionId);
    }

    private static ClaimsIdentity Identity() => new(new[]
    {
        new Claim("tenantId", Tenant.ToString("D")),
        new Claim("sub", Actor.ToString("D")),
        new Claim("sid", "55555555-5555-5555-5555-555555555555")
    }, "verified-test");

    internal static PermissionGrant Grant(DataScope scope, Guid[] departments, string[] fields) =>
        new("wms.stock.read", scope, departments.ToImmutableHashSet(), fields.ToImmutableHashSet(StringComparer.Ordinal));
}
