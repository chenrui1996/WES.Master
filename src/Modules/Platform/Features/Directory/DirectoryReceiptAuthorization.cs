using Microsoft.EntityFrameworkCore;
using Wes.Platform.Domain;
using Wes.Platform.Features.Authorization;
using Wes.Platform.Features.Tenancy;

namespace Wes.Platform.Features.Directory;

public sealed partial class DirectoryService
{
    private async Task<bool> UserReplayAllowedAsync(TenantContext context, Guid id, Guid requestedDepartment,
        string[] fields, CancellationToken ct)
    {
        var user = await database.DirectoryUsers.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct);
        return user is not null
            && await DepartmentEnabledAsync(requestedDepartment, ct)
            && await AllowedAsync(context, "platform.users.write", user.Id, user.DepartmentId, fields, ct)
            && await AllowedAsync(context, "platform.users.write", user.Id, requestedDepartment, fields, ct);
    }

    private async Task<bool> MembershipReplayAllowedAsync(TenantContext context, Guid id, Guid[] requested, CancellationToken ct)
    {
        var user = await database.DirectoryUsers.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct);
        if (user is null || !await AllowedAsync(context, "platform.users.write", id, user.DepartmentId, ["Roles"], ct))
            return false;
        var proof = await database.CommandDelegations.AsNoTracking()
            .Where(AnyId<DirectoryDelegationRequirement>(x => x.ActorId, [context.ActorId]))
            .SingleOrDefaultAsync(AnyId<DirectoryDelegationRequirement>(x => x.OperationId, [operation!.OperationId]), ct);
        if (proof is null) return false;
        var current = await database.Memberships.Where(x => x.UserId == id).Select(x => x.RoleId).ToListAsync(ct);
        var affected = proof.RoleIds.Concat(current).Concat(requested).Distinct().ToArray();
        var roles = await database.Roles.AsNoTracking().Where(AnyId<AccessRole>(x => x.Id, affected)).ToListAsync(ct);
        return roles.Count == affected.Length && requested.All(id => roles.Any(x => x.Id == id && x.Enabled))
            && RolePolicy.CanDelegate(await Authorization.ReadGrantsAsync(context, ct), roles.SelectMany(x => x.Rules));
    }

    private async Task<bool> RoleReplayAllowedAsync(TenantContext context, Guid id, IEnumerable<PermissionGrant> requested,
        CancellationToken ct)
    {
        var role = await database.Roles.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct);
        return role is not null
            && await AllowedAsync(context, "platform.roles.write", Guid.Empty, Guid.Empty, ["Code", "Rules", "Enabled"], ct)
            && RolePolicy.CanDelegate(await Authorization.ReadGrantsAsync(context, ct), role.Rules.Concat(requested));
    }

    private async Task<bool> DepartmentReplayAllowedAsync(TenantContext context, Guid id, string[] fields, CancellationToken ct) =>
        await database.Departments.AsNoTracking().AnyAsync(x => x.Id == id, ct)
        && await AllowedAsync(context, "platform.departments.write", Guid.Empty, id, fields, ct);

}
