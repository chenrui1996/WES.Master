using System.Collections.Immutable;
using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Wes.Platform.Domain;
using Wes.Platform.Features.Authorization;
using Wes.Platform.Features.Identity;
using Wes.Platform.Features.Tenancy;

namespace Wes.Platform.Features.Directory;

public sealed partial class DirectoryService
{
    /// <summary>Reads stable paginated users after scope filtering and projects fields per individual resource.</summary>
    public async Task<DirectoryPage<UserDirectoryRow>> ReadUsersAsync(TenantContext context, UserDirectoryFilter filter,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filter);
        GuardRead();
        Bounds(filter.Offset, filter.Limit);
        if (filter.Name is { Length: > 128 }) throw new ArgumentException("Name filter is too long.");
        var grants = await Authorization.ReadGrantsAsync(context, cancellationToken);
        var readable = grants.Where(x => x.Permission == "platform.users.read" && x.Fields.Contains("Id")).ToArray();
        if (readable.Length == 0) return new(false, []);
        var query = database.DirectoryUsers.AsNoTracking();
        var required = new List<string> { "Id" };
        if (filter.Account is not null) required.Add("Account");
        if (!string.IsNullOrEmpty(filter.Name)) required.Add("Name");
        if (filter.DepartmentId.HasValue) required.Add("DepartmentId");
        if (filter.Enabled.HasValue) required.Add("Enabled");
        foreach (var field in required)
        {
            var matching = grants.Where(x => x.Permission == "platform.users.read" && x.Fields.Contains(field)).ToArray();
            if (matching.Length == 0) return new(false, []);
            if (matching.Any(x => x.Scope == DataScope.All)) continue;
            var own = matching.Any(x => x.Scope == DataScope.Own);
            var departments = matching.Where(x => x.Scope == DataScope.Departments).SelectMany(x => x.Departments).Distinct().ToArray();
            var departmentScope = AnyId<DirectoryUser>(x => x.DepartmentId, departments);
            var actorId = context.ActorId;
            var ownScope = Expression.Equal(Expression.Property(departmentScope.Parameters[0], nameof(DirectoryUser.Id)),
                Expression.Constant(own ? actorId : Guid.Empty));
            query = query.Where(Expression.Lambda<Func<DirectoryUser, bool>>(
                Expression.OrElse(departmentScope.Body, ownScope), departmentScope.Parameters));
        }
        if (filter.Account is not null)
        {
            var digest = LoginAccountKey.FromNormalizedAccount(AccountName.Normalize(filter.Account)).Digest;
            query = query.Where(x => x.AccountDigest == digest);
        }
        if (!string.IsNullOrEmpty(filter.Name)) query = query.Where(x => x.Name.Contains(filter.Name));
        if (filter.DepartmentId.HasValue) query = query.Where(x => x.DepartmentId == filter.DepartmentId.Value);
        if (filter.Enabled.HasValue)
            query = query.Where(x => (x.Enabled && !database.PasswordCredentials.Any(c => c.UserId == x.Id && !c.Enabled)) == filter.Enabled.Value);
        var users = await query.OrderBy(x => x.Id)
            .Skip(filter.Offset).Take(filter.Limit).ToListAsync(cancellationToken);
        var ids = users.Select(x => x.Id).ToArray();
        var disabled = await database.PasswordCredentials.AsNoTracking().Where(AnyId<PasswordCredential>(x => x.UserId, ids)).Where(x => !x.Enabled)
            .Select(x => x.UserId).ToListAsync(cancellationToken);
        var projectedFields = users.ToDictionary(user => user.Id,
            user => Fields(context, "platform.users.read", user.Id, user.DepartmentId, grants));
        var roleReaders = users.Where(user => projectedFields[user.Id].Contains("Roles"))
            .Select(x => x.Id).ToArray();
        var memberships = await database.Memberships.AsNoTracking().Where(AnyId<RoleMembership>(x => x.UserId, roleReaders))
            .OrderBy(x => x.RoleId).ToListAsync(cancellationToken);
        var credentialReaders = users.Where(user => projectedFields[user.Id].Contains("Credential"))
            .Select(user => user.Id).ToArray();
        var credentialVersions = await database.PasswordCredentials.AsNoTracking()
            .Where(AnyId<PasswordCredential>(x => x.UserId, credentialReaders))
            .Select(credential => new { credential.UserId, credential.Version })
            .ToDictionaryAsync(credential => credential.UserId, credential => credential.Version, cancellationToken);
        return new(true, users.Select(user =>
        {
            var fields = projectedFields[user.Id];
            return new UserDirectoryRow(user.Id, Pick(fields, "Account", user.Account), Pick(fields, "Name", user.Name),
                fields.Contains("DepartmentId") ? user.DepartmentId : null,
                fields.Contains("Enabled") ? user.Enabled && !disabled.Contains(user.Id) : null,
                Pick(fields, "Version", user.Version), fields.Contains("Roles")
                    ? memberships.Where(x => x.UserId == user.Id).Select(x => x.RoleId).ToImmutableArray() : null,
                credentialVersions.GetValueOrDefault(user.Id));
        }).ToImmutableArray());
    }

    /// <summary>Filters departments within the intersection of ID and code grants before paging and projects fields per resource.</summary>
    public async Task<DirectoryPage<DepartmentDirectoryRow>> ReadDepartmentsAsync(TenantContext context,
        int offset = 0, int limit = 50, CancellationToken cancellationToken = default, string? code = null)
    {
        GuardRead();
        Bounds(offset, limit);
        var grants = await Authorization.ReadGrantsAsync(context, cancellationToken);
        var readableIds = grants.Where(x => x.Permission == "platform.departments.read" && x.Fields.Contains("Id")).ToArray();
        if (readableIds.Length == 0) return new(false, []);
        var readableCodes = code is null ? [] : grants
            .Where(x => x.Permission == "platform.departments.read" && x.Fields.Contains("Code")).ToArray();
        if (code is not null && readableCodes.Length == 0) return new(false, []);
        var idAll = readableIds.Any(x => x.Scope == DataScope.All);
        var idDepartments = readableIds.Where(x => x.Scope == DataScope.Departments)
            .SelectMany(x => x.Departments).Distinct().ToArray();
        var all = idAll;
        var ids = idDepartments;
        if (code is not null)
        {
            var codeAll = readableCodes.Any(x => x.Scope == DataScope.All);
            var codeDepartments = readableCodes.Where(x => x.Scope == DataScope.Departments)
                .SelectMany(x => x.Departments).Distinct().ToArray();
            all = idAll && codeAll;
            ids = idAll ? codeDepartments : codeAll ? idDepartments : idDepartments.Intersect(codeDepartments).ToArray();
        }
        var departmentQuery = database.Departments.AsNoTracking();
        if (!all) departmentQuery = departmentQuery.Where(AnyId<DirectoryDepartment>(x => x.Id, ids));
        if (code is not null) departmentQuery = departmentQuery.Where(x => x.Code == code);
        var rows = await departmentQuery
            .OrderBy(x => x.Code).ThenBy(x => x.Id).Skip(offset).Take(limit).ToListAsync(cancellationToken);
        return new(true, await ProjectDepartmentsAsync(context, rows, grants, cancellationToken));
    }

    /// <summary>Resolves a bounded set of saved department references without depending on a catalog page.</summary>
    public async Task<DirectoryPage<DepartmentDirectoryRow>> ReadDepartmentReferencesAsync(TenantContext context,
        IReadOnlyCollection<Guid> requestedIds, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(requestedIds);
        GuardRead();
        var ids = requestedIds.Distinct().ToArray();
        if (ids.Length is 0 or > 100 || ids.Contains(Guid.Empty))
            throw new ArgumentException("Department reference batches require 1 to 100 nonempty unique IDs.", nameof(requestedIds));

        var grants = await Authorization.ReadGrantsAsync(context, cancellationToken);
        var readableIds = grants.Where(x => x.Permission == "platform.departments.read" && x.Fields.Contains("Id")).ToArray();
        if (readableIds.Length == 0) return new(false, []);
        var all = readableIds.Any(x => x.Scope == DataScope.All);
        var permittedIds = readableIds.Where(x => x.Scope == DataScope.Departments)
            .SelectMany(x => x.Departments).Distinct().ToArray();
        var query = database.Departments.AsNoTracking().Where(AnyId<DirectoryDepartment>(x => x.Id, ids));
        if (!all) query = query.Where(AnyId<DirectoryDepartment>(x => x.Id, permittedIds));
        var rows = await query.OrderBy(x => x.Code).ThenBy(x => x.Id).ToListAsync(cancellationToken);
        return new(true, await ProjectDepartmentsAsync(context, rows, grants, cancellationToken));
    }

    private async Task<ImmutableArray<DepartmentDirectoryRow>> ProjectDepartmentsAsync(TenantContext context,
        IReadOnlyCollection<DirectoryDepartment> rows, IEnumerable<PermissionGrant> grants, CancellationToken cancellationToken)
    {
        var parentIds = rows.Where(row => row.ParentId.HasValue).Select(row => row.ParentId!.Value).Distinct().ToArray();
        var parents = await database.Departments.AsNoTracking().Where(AnyId<DirectoryDepartment>(x => x.Id, parentIds))
            .ToDictionaryAsync(row => row.Id, cancellationToken);
        return rows.Select(row =>
        {
            var fields = Fields(context, "platform.departments.read", Guid.Empty, row.Id, grants);
            return new DepartmentDirectoryRow(row.Id, Pick(fields, "Code", row.Code), Pick(fields, "Name", row.Name),
                fields.Contains("Enabled") ? row.Enabled : null, Pick(fields, "Version", row.Version),
                fields.Contains("ParentId") ? row.ParentId : null,
                fields.Contains("ParentId") && row.ParentId is { } parent && parents.TryGetValue(parent, out var source)
                    && Fields(context, "platform.departments.read", Guid.Empty, parent, grants).Contains("Name") ? source.Name : null);
        }).ToImmutableArray();
    }

    /// <summary>Roles are tenant-wide resources; scoped grants never become global role/rule visibility.</summary>
    public async Task<DirectoryPage<RoleDirectoryRow>> ReadRolesAsync(TenantContext context, int offset = 0, int limit = 50,
        CancellationToken cancellationToken = default, string? code = null, string? permission = null)
    {
        GuardRead();
        Bounds(offset, limit);
        var fields = Fields(context, "platform.roles.read", Guid.Empty, Guid.Empty,
            await Authorization.ReadGrantsAsync(context, cancellationToken));
        if (!fields.Contains("Id")) return new(false, []);
        if (code is not null && !fields.Contains("Code") || permission is not null && !fields.Contains("Rules")) return new(false, []);
        if (code?.Length > 128 || permission?.Length > 128 || permission?.Any(char.IsControl) == true)
            throw new ArgumentException("Role filters exceed the catalog bounds.");
        var query = database.Roles.AsNoTracking();
        if (code is not null) query = query.Where(role => role.Code == code.Trim());
        var ordered = query.OrderBy(x => x.Code).ThenBy(x => x.Id);
        // Rules are a validated owner JSON value, not a provider JSON_TABLE projection. Filter exact permissions before paging.
        var roles = permission is null ? await ordered.Skip(offset).Take(limit).ToListAsync(cancellationToken)
            : (await ordered.ToListAsync(cancellationToken)).Where(role => role.Rules.Any(rule => rule.Permission == permission.Trim()))
                .Skip(offset).Take(limit).ToList();
        var roleIds = roles.Select(role => role.Id).ToArray();
        var impact = fields.Contains("AffectedSubjects")
            ? await database.Memberships.AsNoTracking().Where(AnyId<RoleMembership>(row => row.RoleId, roleIds))
                .GroupBy(row => row.RoleId).Select(group => new { Id = group.Key, Count = group.Count() })
                .ToDictionaryAsync(row => row.Id, row => row.Count, cancellationToken) : new Dictionary<Guid, int>();
        return new(true, roles.Select(role => new RoleDirectoryRow(role.Id, Pick(fields, "Code", role.Code),
            fields.Contains("Enabled") ? role.Enabled : null, Pick(fields, "Version", role.Version),
            fields.Contains("Rules") ? role.Rules : null,
            fields.Contains("AffectedSubjects") ? impact.GetValueOrDefault(role.Id) : null)).ToImmutableArray());
    }

    /// <summary>Resolves a bounded set of saved role references using the current tenant-wide role projection.</summary>
    public async Task<DirectoryPage<RoleDirectoryRow>> ReadRoleReferencesAsync(TenantContext context,
        IReadOnlyCollection<Guid> requestedIds, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(requestedIds);
        GuardRead();
        var ids = requestedIds.Distinct().ToArray();
        if (ids.Length is 0 or > 100 || ids.Contains(Guid.Empty))
            throw new ArgumentException("Role reference batches require 1 to 100 nonempty unique IDs.", nameof(requestedIds));

        var fields = Fields(context, "platform.roles.read", Guid.Empty, Guid.Empty,
            await Authorization.ReadGrantsAsync(context, cancellationToken));
        if (!fields.Contains("Id")) return new(false, []);

        var roles = await database.Roles.AsNoTracking().Where(AnyId<AccessRole>(role => role.Id, ids))
            .OrderBy(role => role.Code).ThenBy(role => role.Id).ToListAsync(cancellationToken);
        return new(true, roles.Select(role => new RoleDirectoryRow(role.Id, Pick(fields, "Code", role.Code),
            fields.Contains("Enabled") ? role.Enabled : null, null, null, null))
            .ToImmutableArray());
    }

    /// <summary>Lists only the current tenant and explicitly provisioned same-subject eligible tenant memberships; never all tenants.</summary>
    public async Task<DirectoryPage<TenantDirectoryRow>> ReadTenantsAsync(TenantContext context, int offset = 0, int limit = 50,
        CancellationToken cancellationToken = default, Guid? tenantId = null, bool? enabled = null)
    {
        GuardRead();
        Bounds(offset, limit);
        var grants = await Authorization.ReadGrantsAsync(context, cancellationToken);
        var fields = Fields(context, "platform.tenants.read", Guid.Empty, Guid.Empty, grants);
        if (!fields.Contains("Id")) return new(false, []);
        if (tenantId == Guid.Empty) throw new ArgumentException("A tenant filter must be a nonempty UUID.");
        if (enabled.HasValue && !fields.Contains("Enabled")) return new(false, []);
        var canSwitch = Fields(context, "platform.tenants.switch", Guid.Empty, Guid.Empty, grants).Contains("Id");
        var eligible = EligibleTargetTenants(context.ActorId).Select(x => x.TenantId);
        var query = database.DirectoryTenants.IgnoreQueryFilters().AsNoTracking()
            .Where(x => x.TenantId == context.TenantId || (canSwitch && eligible.Contains(x.TenantId)));
        if (tenantId.HasValue) query = query.Where(x => x.TenantId == tenantId.Value);
        if (enabled.HasValue) query = query.Where(x => x.Enabled == enabled.Value);
        var tenants = await query
            .OrderBy(x => x.TenantId).Skip(offset).Take(limit).ToListAsync(cancellationToken);
        return new(true, tenants.Select(row => new TenantDirectoryRow(row.TenantId, Pick(fields, "Name", row.Name),
            fields.Contains("Enabled") ? row.Enabled : null, Pick(fields, "DefaultLanguage", row.DefaultLanguage),
            Pick(fields, "Version", row.Version))).ToImmutableArray());
    }

    /// <summary>Revalidates current source authorization and target eligibility; does not create tokens or reuse a source session.</summary>
    public async Task<TenantSwitchDecision> CheckTenantSwitchAsync(TenantContext context, Guid targetTenantId,
        CancellationToken cancellationToken = default)
    {
        GuardRead();
        var grants = await Authorization.ReadGrantsAsync(context, cancellationToken);
        if (targetTenantId == Guid.Empty
            || !Fields(context, "platform.tenants.switch", Guid.Empty, Guid.Empty, grants).Contains("Id"))
            return new(false);
        return await EligibleTargetTenants(context.ActorId).AnyAsync(x => x.TenantId == targetTenantId, cancellationToken)
            ? new(true, targetTenantId) : new(false);
    }

    private IQueryable<DirectoryTenant> EligibleTargetTenants(Guid actorId) =>
        // The only cross-filter query is bounded to the authenticated immutable subject and all current eligibility facts.
        from tenant in database.DirectoryTenants.IgnoreQueryFilters().AsNoTracking()
        join user in database.DirectoryUsers.IgnoreQueryFilters().AsNoTracking() on tenant.TenantId equals user.TenantId
        join department in database.Departments.IgnoreQueryFilters().AsNoTracking()
            on new { user.TenantId, Id = user.DepartmentId } equals new { department.TenantId, department.Id }
        join credential in database.PasswordCredentials.IgnoreQueryFilters().AsNoTracking()
            on new { user.TenantId, UserId = user.Id } equals new { credential.TenantId, credential.UserId }
        where user.Id == actorId && tenant.Enabled && user.Enabled && department.Enabled && credential.Enabled
        select tenant;

    private static ImmutableHashSet<string> Fields(TenantContext context, string permission, Guid owner, Guid department,
        IEnumerable<PermissionGrant> grants) =>
        RolePolicy.VisibleFields(context.TenantId, context.ActorId, permission,
            new ResourceScope(context.TenantId, owner, department), grants);
    private static string? Pick(ImmutableHashSet<string> fields, string field, string value) =>
        fields.Contains(field) ? value : null;
    private static void Bounds(int offset, int limit)
    {
        if (offset is < 0 or > 100000 || limit is < 1 or > 100)
            throw new ArgumentOutOfRangeException(nameof(limit), "Directory paging requires offset 0..100000 and limit 1..100.");
    }

    private static Expression<Func<T, bool>> AnyId<T>(Expression<Func<T, Guid>> selector, IEnumerable<Guid> ids)
        => ScopeExpressions.MatchIds(selector, ids);

    private void GuardRead()
    {
        if (database.ChangeTracker.Entries().Any() || database.Database.CurrentTransaction is not null
            || System.Transactions.Transaction.Current is not null)
            throw new InvalidOperationException("Directory queries require a clean current owner read, not a caller transaction snapshot.");
    }
}
