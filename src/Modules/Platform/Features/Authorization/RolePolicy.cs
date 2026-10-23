using System.Collections.Immutable;
using Wes.Platform.Domain;

namespace Wes.Platform.Features.Authorization;

/// <summary>Resource owner metadata supplied by the authoritative resource owner, not the request payload.</summary>
public sealed record ResourceScope(Guid TenantId, Guid OwnerId, Guid DepartmentId);

/// <summary>Pure deny-by-default policy. Authentication and current session checks precede evaluation.</summary>
public static class RolePolicy
{
    /// <summary>Returns only fields from grants that actually match this resource's scope.</summary>
    public static ImmutableHashSet<string> VisibleFields(
        Guid tenant, Guid actor, string permission, ResourceScope resource, IEnumerable<PermissionGrant> grants)
    {
        ArgumentNullException.ThrowIfNull(resource);
        ArgumentNullException.ThrowIfNull(grants);
        if (tenant == Guid.Empty || actor == Guid.Empty || resource.TenantId != tenant || string.IsNullOrWhiteSpace(permission))
        {
            return ImmutableHashSet<string>.Empty;
        }

        return PermissionGrant.ValidateSnapshot(grants).Where(grant => grant.Permission == permission && Matches(grant, actor, resource))
            .SelectMany(grant => grant.Fields).ToImmutableHashSet(StringComparer.Ordinal);
    }

    /// <summary>Checks that every delegated grant is covered by the caller's own permission/range/field grant.</summary>
    public static bool CanDelegate(IEnumerable<PermissionGrant> caller, IEnumerable<PermissionGrant> requested)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(requested);
        var available = PermissionGrant.ValidateSnapshot(caller);
        return PermissionGrant.ValidateSnapshot(requested).All(target => available.Any(source =>
            source.Permission == target.Permission && target.Fields.IsSubsetOf(source.Fields)
            && (source.Scope == DataScope.All
                || (source.Scope == DataScope.Own && target.Scope == DataScope.Own)
                || (source.Scope == DataScope.Departments && target.Scope == DataScope.Departments
                    && target.Departments.IsSubsetOf(source.Departments)))));
    }

    private static bool Matches(PermissionGrant grant, Guid actor, ResourceScope resource) => grant.Scope switch
    {
        DataScope.All => true,
        DataScope.Own => resource.OwnerId == actor,
        DataScope.Departments => resource.DepartmentId != Guid.Empty && grant.Departments.Contains(resource.DepartmentId),
        _ => false
    };
}
