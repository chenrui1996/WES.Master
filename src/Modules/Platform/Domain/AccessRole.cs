using System.Collections.Immutable;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Wes.Platform.Domain;

/// <summary>Tenant-owned role rules with one opaque version; no special numeric administrator role exists.</summary>
public sealed class AccessRole
{
    private AccessRole() { }

    /// <summary>Creates a role after checking permission/range/field schema; assigning it is a separate authorized operation.</summary>
    public AccessRole(Guid tenantId, string code, IEnumerable<PermissionGrant> rules)
    {
        if (tenantId == Guid.Empty || !Regex.IsMatch(code ?? "", "^[a-zA-Z][a-zA-Z0-9._-]{0,63}$", RegexOptions.CultureInvariant))
        {
            throw new ArgumentException("A role requires a trusted tenant and a valid stable code.");
        }

        TenantId = tenantId;
        Id = Guid.NewGuid();
        Code = code!;
        RulesData = SerializeRules(rules);
        Version = Guid.NewGuid().ToString("D");
    }

    /// <summary>Stable role reference.</summary>
    public Guid Id { get; private set; }
    /// <summary>Trusted tenant owner.</summary>
    public Guid TenantId { get; private set; }
    /// <summary>Stable tenant-unique role code; display names are not authority.</summary>
    public string Code { get; private set; } = "";
    /// <summary>Disabled roles never grant permissions.</summary>
    public bool Enabled { get; private set; } = true;
    /// <summary>Opaque optimistic version.</summary>
    public string Version { get; private set; } = "";
    private string RulesData { get; set; } = "[]";

    /// <summary>Immutable validated grant snapshot. Corrupt persisted schema fails explicitly, never grants All.</summary>
    public ImmutableArray<PermissionGrant> Rules
    {
        get
        {
            var rules = JsonSerializer.Deserialize<ImmutableArray<PermissionGrant>>(RulesData);
            return PermissionGrant.ValidateSnapshot(rules);
        }
    }

    /// <summary>Stages a version-checked role update; caller must authorize and audit in the same transaction.</summary>
    public SettingDecision Change(string? expectedVersion, bool enabled, IEnumerable<PermissionGrant> rules)
    {
        if (string.IsNullOrWhiteSpace(expectedVersion))
        {
            return SettingDecision.VersionRequired;
        }
        if (Version != expectedVersion)
        {
            return SettingDecision.VersionConflict;
        }
        var snapshot = SerializeRules(rules);
        Enabled = enabled;
        RulesData = snapshot;
        Version = Guid.NewGuid().ToString("D");
        return SettingDecision.Changed;
    }

    private static string SerializeRules(IEnumerable<PermissionGrant> rules)
    {
        return JsonSerializer.Serialize(PermissionGrant.ValidateSnapshot(rules));
    }
}

/// <summary>Tenant-composite role assignment; role and subject identifiers are never interpreted across tenants.</summary>
public sealed class RoleMembership
{
    private RoleMembership() { }

    /// <summary>Creates an assignment after trusted tenant/user/role authorization at the use-case boundary.</summary>
    public RoleMembership(Guid tenantId, Guid userId, Guid roleId)
    {
        if (tenantId == Guid.Empty || userId == Guid.Empty || roleId == Guid.Empty)
        {
            throw new ArgumentException("A role assignment requires trusted tenant, user and role references.");
        }

        TenantId = tenantId;
        UserId = userId;
        RoleId = roleId;
    }

    /// <summary>Trusted tenant owner and component of the role foreign key.</summary>
    public Guid TenantId { get; private set; }
    /// <summary>Assigned user reference.</summary>
    public Guid UserId { get; private set; }
    /// <summary>Role reference within this tenant only.</summary>
    public Guid RoleId { get; private set; }
}
