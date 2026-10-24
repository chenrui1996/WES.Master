using System.Collections.Immutable;
using Microsoft.EntityFrameworkCore;
using Wes.Platform.Domain;
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
        TenantContext context, string permission, ResourceScope resource, CancellationToken cancellationToken = default) =>
        RolePolicy.VisibleFields(context.TenantId, context.ActorId, permission, resource,
            await ReadGrantsAsync(context, cancellationToken));
}
