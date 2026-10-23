using System.Collections.Immutable;
using System.Text.RegularExpressions;

namespace Wes.Platform.Domain;

/// <summary>Explicit data range. An empty department set never means All.</summary>
public enum DataScope
{
    /// <summary>Only resources owned by the authenticated actor.</summary>
    Own,
    /// <summary>Only explicitly granted departments; an empty set matches nothing.</summary>
    Departments,
    /// <summary>All resources within the same trusted tenant, never across tenants.</summary>
    All
}

/// <summary>A permission and its resource-scoped field whitelist; collections are immutable snapshots.</summary>
public sealed record PermissionGrant(
    string Permission, DataScope Scope, ImmutableHashSet<Guid> Departments, ImmutableHashSet<string> Fields)
{
    internal static ImmutableArray<PermissionGrant> ValidateSnapshot(IEnumerable<PermissionGrant> rules)
    {
        ArgumentNullException.ThrowIfNull(rules);
        var snapshot = rules.ToImmutableArray();
        if (snapshot.Any(rule => rule is null || !Enum.IsDefined(rule.Scope)
            || rule.Permission is null || rule.Permission.Length > 128
            || !Regex.IsMatch(rule.Permission, "^[a-z][a-z0-9]*(\\.[a-z][a-z0-9]*)+$", RegexOptions.CultureInvariant)
            || rule.Fields is null || rule.Departments is null || rule.Departments.Contains(Guid.Empty)
            || (rule.Scope != DataScope.Departments && rule.Departments.Count != 0)
            || rule.Fields.Any(field => !Regex.IsMatch(field ?? "", "^[a-zA-Z][a-zA-Z0-9]{0,63}$", RegexOptions.CultureInvariant))))
        {
            throw new ArgumentException("Role rules require explicit supported permission, scope and field metadata.");
        }

        return snapshot;
    }
}
