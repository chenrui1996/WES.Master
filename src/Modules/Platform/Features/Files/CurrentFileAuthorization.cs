using Microsoft.EntityFrameworkCore;
using Wes.Platform.Domain;
using Wes.Platform.Features.Authorization;
using Wes.Platform.Features.Tenancy;
using Wes.Platform.Infrastructure.Persistence;

namespace Wes.Platform.Features.Files;

internal sealed class CurrentFileAuthorization(PlatformDbContext database, TimeProvider clock)
{
    internal async Task<bool> AllowedAsync(TenantContext context, QuarantinedFile file, string permission,
        string[] fields, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        ct.ThrowIfCancellationRequested();
        if (context.TenantId != database.TenantId || context.ActorId == Guid.Empty || context.SessionId == Guid.Empty)
            return false;
        var creator = await database.DirectoryUsers.AsNoTracking().SingleOrDefaultAsync(item => item.Id == file.CreatorId, ct);
        if (creator is null) return false;
        var visible = await new CurrentAuthorization(database, clock).VisibleFieldsAsync(context, permission,
            new(file.TenantId, file.CreatorId, creator.DepartmentId), ct);
        return fields.All(visible.Contains);
    }
}
