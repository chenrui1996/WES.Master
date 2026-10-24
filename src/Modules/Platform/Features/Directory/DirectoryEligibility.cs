using Microsoft.EntityFrameworkCore;
using Wes.Platform.Infrastructure.Persistence;

namespace Wes.Platform.Features.Directory;

/// <summary>Current owner eligibility for authentication; no caller assertion or cached department is accepted.</summary>
public sealed class DirectoryEligibility(PlatformDbContext database)
{
    /// <summary>Checks tenant, subject, department and current credential enabled state; missing state fails closed.</summary>
    public async Task<bool> IsEligibleAsync(Guid userId, CancellationToken cancellationToken = default) =>
        await IsDirectoryEligibleAsync(userId, cancellationToken)
        && await (from user in database.DirectoryUsers.AsNoTracking()
                  join credential in database.PasswordCredentials.AsNoTracking()
                      on new { user.TenantId, UserId = user.Id, user.AccountDigest }
                      equals new { credential.TenantId, credential.UserId, credential.AccountDigest }
                  where user.Id == userId && credential.Enabled
                  select user.Id).AnyAsync(cancellationToken);

    /// <summary>Checks current directory eligibility for already issued sessions; a disabled credential is an additional veto.</summary>
    public async Task<bool> IsDirectoryEligibleAsync(Guid userId, CancellationToken cancellationToken = default) =>
        userId != Guid.Empty
        && await (from user in database.DirectoryUsers.AsNoTracking()
                  join department in database.Departments.AsNoTracking()
                      on new { user.TenantId, Id = user.DepartmentId } equals new { department.TenantId, department.Id }
                  join tenant in database.DirectoryTenants.AsNoTracking() on user.TenantId equals tenant.TenantId
                  where user.Id == userId && user.Enabled && department.Enabled && tenant.Enabled
                  select user.Id).AnyAsync(cancellationToken)
        && !await database.PasswordCredentials.AsNoTracking().AnyAsync(x => x.UserId == userId && !x.Enabled, cancellationToken);
}
