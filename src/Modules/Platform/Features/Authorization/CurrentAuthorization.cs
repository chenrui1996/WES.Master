using System.Collections.Immutable;
using Microsoft.EntityFrameworkCore;
using Wes.Platform.Domain;
using Wes.Platform.Features.Directory;
using Wes.Platform.Features.Tenancy;
using Wes.Platform.Infrastructure.Persistence;

namespace Wes.Platform.Features.Authorization;

/// <summary>Reads current owner state for each request; it never uses caller-supplied menu/role claims or stale cache.</summary>
public sealed class CurrentAuthorization(PlatformDbContext database, TimeProvider clock)
{
    /// <summary>Returns grants only for a live matching session and current enabled roles in the trusted tenant.</summary>
    public async Task<ImmutableArray<PermissionGrant>> ReadGrantsAsync(TenantContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.TenantId != database.TenantId || context.ActorId == Guid.Empty || context.SessionId == Guid.Empty)
        {
            throw new InvalidOperationException("Authorization requires matching trusted owner context.");
        }

        if (!await new DirectoryEligibility(database).IsDirectoryEligibleAsync(context.ActorId, cancellationToken))
            return ImmutableArray<PermissionGrant>.Empty;

        var session = await database.Sessions.AsNoTracking().SingleOrDefaultAsync(
            item => item.Id == context.SessionId && item.UserId == context.ActorId, cancellationToken);
        if (session is null || session.RequiresPasswordChange || session.StateAt(clock.GetUtcNow()) != SessionState.Active)
        {
            return ImmutableArray<PermissionGrant>.Empty;
        }

        var roles = await (from membership in database.Memberships.AsNoTracking()
                           join role in database.Roles.AsNoTracking()
                               on new { membership.TenantId, Id = membership.RoleId } equals new { role.TenantId, role.Id }
                           where membership.UserId == context.ActorId && role.Enabled
                           select role).ToListAsync(cancellationToken);
        return roles.SelectMany(role => role.Rules).ToImmutableArray();
    }

    /// <summary>Returns the exact authorized projection for a resource, not an unfiltered entity or a role bypass.</summary>
    public async Task<ImmutableHashSet<string>> VisibleFieldsAsync(
        TenantContext context, string permission, ResourceScope resource, CancellationToken cancellationToken = default)
    {
        var grants = await ReadGrantsAsync(context, cancellationToken);
        if (permission.StartsWith("platform.users.", StringComparison.Ordinal) && resource.OwnerId != Guid.Empty)
        {
            // User department comes from current owner state even when a handler supplies stale/empty metadata.
            var user = await database.DirectoryUsers.AsNoTracking()
                .SingleOrDefaultAsync(x => x.Id == resource.OwnerId, cancellationToken);
            if (user is null || resource.TenantId != database.TenantId) return ImmutableHashSet<string>.Empty;
            resource = new ResourceScope(user.TenantId, user.Id, user.DepartmentId);
        }
        return RolePolicy.VisibleFields(context.TenantId, context.ActorId, permission, resource, grants);
    }

    /// <summary>
    /// Resolves a trusted resource creator's current directory department before evaluating fields.
    /// Only resource-owner application code may supply creator/tenant metadata; neither is a client authorization assertion.
    /// Missing creators and foreign resources fail closed, including All grants; no grant or department result is cached.
    /// </summary>
    public async Task<ImmutableHashSet<string>> VisibleOwnedFieldsAsync(TenantContext context, string permission,
        Guid resourceTenantId, Guid creatorId, CancellationToken cancellationToken = default)
    {
        var grants = await ReadGrantsAsync(context, cancellationToken);
        if (resourceTenantId != database.TenantId || creatorId == Guid.Empty || grants.IsEmpty)
            return ImmutableHashSet<string>.Empty;
        var creator = await database.DirectoryUsers.AsNoTracking()
            .Where(item => item.Id == creatorId)
            .Select(item => new { item.Id, item.DepartmentId })
            .SingleOrDefaultAsync(cancellationToken);
        if (creator is null) return ImmutableHashSet<string>.Empty;
        return RolePolicy.VisibleFields(context.TenantId, context.ActorId, permission,
            new ResourceScope(resourceTenantId, creator.Id, creator.DepartmentId), grants);
    }
}
