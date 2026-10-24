using Microsoft.EntityFrameworkCore;
using Wes.Platform.Domain;
using Wes.Platform.Features.Tenancy;

namespace Wes.Platform.Features.Directory;

public sealed partial class DirectoryService
{
    /// <summary>Moves a display-hierarchy edge under Serializable reads, exact versioning and current child/parent authority.</summary>
    public Task<DirectoryWriteResult> ReparentDepartmentAsync(TenantContext context, Guid id, string? expectedVersion,
        Guid? parentId, Guid correlation, CancellationToken cancellationToken = default) =>
        WriteAsync(context, correlation, async () =>
        {
            if (!await ParentAuthorityAsync(context, id, parentId, cancellationToken)) return Denied();
            // Serializable ancestor reads fence concurrent opposite moves; no hierarchy-based grant inheritance is introduced.
            var visited = new HashSet<Guid> { id };
            var ancestor = parentId;
            while (ancestor is { } current)
            {
                if (!visited.Add(current)) return Denied();
                var parent = await database.Departments.AsNoTracking().SingleOrDefaultAsync(x => x.Id == current, cancellationToken);
                if (parent is null) return Denied();
                ancestor = parent.ParentId;
            }
            var department = await database.Departments.SingleAsync(x => x.Id == id, cancellationToken);
            var before = department.Version;
            var decision = department.ChangeParent(expectedVersion, parentId);
            return decision == SettingDecision.Changed
                ? Audit(context, id, before, department.Version, PlatformAuditAction.DepartmentChanged, correlation) : VersionResult(decision);
        }, cancellationToken, DirectoryCommandKind.ReparentDepartment, [id, expectedVersion, parentId],
            current => ParentAuthorityAsync(context, current, parentId, cancellationToken));

    private async Task<bool> ParentAuthorityAsync(TenantContext context, Guid id, Guid? parent, CancellationToken ct) =>
        id != parent && await database.Departments.AsNoTracking().AnyAsync(x => x.Id == id, ct)
        && await AllowedAsync(context, "platform.departments.write", Guid.Empty, id, ["ParentId"], ct)
        && (parent is null || await DepartmentEnabledAsync(parent.Value, ct)
            && await AllowedAsync(context, "platform.departments.write", Guid.Empty, parent.Value, ["ParentId"], ct));
}
