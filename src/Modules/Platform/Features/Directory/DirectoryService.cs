using System.Data;
using Microsoft.EntityFrameworkCore;
using MySql.Data.MySqlClient;
using Wes.Platform.Domain;
using Wes.Platform.Features.Authorization;
using Wes.Platform.Features.Identity;
using Wes.Platform.Features.Tenancy;
using Wes.Platform.Infrastructure.Persistence;

namespace Wes.Platform.Features.Directory;

/// <summary>Platform directory owner; exposes only explicitly authorized projections and audited commands.</summary>
public sealed partial class DirectoryService(PlatformDbContext database, TimeProvider clock, DirectoryOperation? operation = null)
{
    /// <summary>Creates a credential-free subject after validating current department scope and required write fields.</summary>
    public Task<DirectoryWriteResult> CreateUserAsync(TenantContext context, string account, string name,
        Guid departmentId, Guid correlation, CancellationToken cancellationToken = default) =>
        WriteAsync(context, correlation, async () =>
        {
            if (!await DepartmentEnabledAsync(departmentId, cancellationToken)
                || !await AllowedAsync(context, "platform.users.write", Guid.Empty, departmentId,
                    ["Account", "Name", "DepartmentId", "Enabled"], cancellationToken)) return Denied();
            var user = new DirectoryUser(database.TenantId, Guid.NewGuid(), account, name, departmentId);
            if (await database.DirectoryUsers.AnyAsync(x => x.AccountDigest == user.AccountDigest, cancellationToken))
                return new(DirectoryDecision.Duplicate);
            database.DirectoryUsers.Add(user);
            return Audit(context, user.Id, null, user.Version, PlatformAuditAction.UserChanged, correlation);
        }, cancellationToken, DirectoryCommandKind.CreateUser, [account, name, departmentId],
            id => UserReplayAllowedAsync(context, id, departmentId, ["Account", "Name", "DepartmentId", "Enabled"], cancellationToken));

    /// <summary>Updates a profile with old/new scope checks. Disable revokes existing families and fences credentials atomically.</summary>
    public Task<DirectoryWriteResult> ChangeUserAsync(TenantContext context, Guid userId, string? expectedVersion,
        string name, Guid departmentId, bool enabled, Guid correlation, CancellationToken cancellationToken = default) =>
        WriteAsync(context, correlation, async () =>
        {
            var user = await database.DirectoryUsers.SingleOrDefaultAsync(x => x.Id == userId, cancellationToken);
            if (user is null || !await DepartmentEnabledAsync(departmentId, cancellationToken)
                || !await AllowedAsync(context, "platform.users.write", user.Id, user.DepartmentId,
                    ["Name", "DepartmentId", "Enabled"], cancellationToken)
                || !await AllowedAsync(context, "platform.users.write", user.Id, departmentId,
                    ["Name", "DepartmentId", "Enabled"], cancellationToken)) return Denied();
            var before = user.Version;
            var decision = user.Change(expectedVersion, name, departmentId, enabled);
            if (decision != SettingDecision.Changed) return VersionResult(decision);
            var credential = await database.PasswordCredentials.SingleOrDefaultAsync(x => x.UserId == userId, cancellationToken);
            // Directory edits cannot undo a security disable; credential re-enabling requires a separate reviewed policy.
            if (enabled && credential is { Enabled: false }) return Denied();
            if (!enabled)
            {
                credential?.SetEnabled(false, clock.GetUtcNow());
                var sessions = await database.Sessions.Where(x => x.UserId == userId).ToListAsync(cancellationToken);
                foreach (var session in sessions.Where(x => x.StateAt(clock.GetUtcNow()) != SessionState.Revoked))
                {
                    var old = session.Version;
                    session.Revoke(clock.GetUtcNow());
                    Audit(context, session.Id, old, session.Version, PlatformAuditAction.SessionRevoked, correlation);
                }
            }
            return Audit(context, user.Id, before, user.Version, PlatformAuditAction.UserChanged, correlation);
        }, cancellationToken, DirectoryCommandKind.ChangeUser, [userId, expectedVersion, name, departmentId, enabled],
            id => UserReplayAllowedAsync(context, id, departmentId, ["Name", "DepartmentId", "Enabled"], cancellationToken));

    /// <summary>Creates the first credential separately, using authoritative owner breach evidence, never HTTP-supplied evidence.</summary>
    public Task<DirectoryWriteResult> CreateCredentialAsync(TenantContext context, Guid userId, string? expectedVersion,
        string password, PasswordEvidence breachEvidence, Guid correlation, CancellationToken cancellationToken = default) =>
        WriteAsync(context, correlation, async () =>
        {
            var user = await database.DirectoryUsers.SingleOrDefaultAsync(x => x.Id == userId, cancellationToken);
            if (user is null || !user.Enabled || !await DepartmentEnabledAsync(user.DepartmentId, cancellationToken)
                || !await AllowedAsync(context, "platform.users.write", user.Id, user.DepartmentId,
                    ["Credential"], cancellationToken)) return Denied();
            if (await database.PasswordCredentials.AnyAsync(x => x.UserId == userId, cancellationToken))
                return new(DirectoryDecision.Duplicate);
            var before = user.Version;
            var decision = user.Change(expectedVersion, user.Name, user.DepartmentId, user.Enabled);
            if (decision != SettingDecision.Changed) return VersionResult(decision);
            var hash = PasswordCredentialCodec.Create(password, user.Account, breachEvidence, PasswordEvidence.Clear);
            if (hash.Decision != PasswordDecision.Accepted || hash.Hash is null)
                return new(DirectoryDecision.PasswordRejected, PasswordDecision: hash.Decision);
            database.PasswordCredentials.Add(new PasswordCredential(database.TenantId, user.Id, user.Account, hash.Hash, clock.GetUtcNow()));
            return Audit(context, user.Id, before, user.Version, PlatformAuditAction.CredentialCreated, correlation);
        }, cancellationToken);

    /// <summary>Replaces membership under a shared user CAS fence; checks every removed and assigned grant for delegation.</summary>
    public Task<DirectoryWriteResult> ChangeMembershipsAsync(TenantContext context, Guid userId, string? expectedVersion,
        IEnumerable<Guid> roleIds, Guid correlation, CancellationToken cancellationToken = default)
    {
        var requested = roleIds.Distinct().Order().ToArray();
        return WriteAsync(context, correlation, async () =>
        {
            if (requested.Length > 100 || requested.Contains(Guid.Empty)) throw new ArgumentException("Role references must be bounded and nonempty.");
            var user = await database.DirectoryUsers.SingleOrDefaultAsync(x => x.Id == userId, cancellationToken);
            if (user is null || !await AllowedAsync(context, "platform.users.write", user.Id, user.DepartmentId,
                ["Roles"], cancellationToken)) return Denied();
            var memberships = await database.Memberships.Where(x => x.UserId == userId).ToListAsync(cancellationToken);
            var affected = requested.Concat(memberships.Select(x => x.RoleId)).Distinct().ToArray();
            var roles = await database.Roles.Where(AnyId<AccessRole>(x => x.Id, affected)).ToListAsync(cancellationToken);
            if (roles.Count != affected.Length || roles.Any(x => requested.Contains(x.Id) && !x.Enabled)) return Denied();
            var grants = await Authorization.ReadGrantsAsync(context, cancellationToken);
            if (!RolePolicy.CanDelegate(grants, roles.SelectMany(x => x.Rules))) return Denied();
            var before = user.Version;
            var decision = user.Change(expectedVersion, user.Name, user.DepartmentId, user.Enabled);
            if (decision != SettingDecision.Changed) return VersionResult(decision);
            database.Memberships.RemoveRange(memberships.Where(x => !requested.Contains(x.RoleId)));
            database.Memberships.AddRange(requested.Except(memberships.Select(x => x.RoleId))
                .Select(x => new RoleMembership(database.TenantId, userId, x)));
            if (operation is not null)
                database.CommandDelegations.Add(new DirectoryDelegationRequirement(database.TenantId, context.ActorId,
                    operation.OperationId, affected));
            return Audit(context, user.Id, before, user.Version, PlatformAuditAction.MembershipChanged, correlation);
        }, cancellationToken, DirectoryCommandKind.ChangeMemberships, [userId, expectedVersion, requested],
            id => MembershipReplayAllowedAsync(context, id, requested, cancellationToken));
    }

    /// <summary>Creates a role only within the caller's own delegable permission/range/field envelope.</summary>
    public Task<DirectoryWriteResult> CreateRoleAsync(TenantContext context, string code, IEnumerable<PermissionGrant> rules,
        Guid correlation, CancellationToken cancellationToken = default)
    {
        var snapshot = PermissionGrant.ValidateSnapshot(rules);
        return WriteAsync(context, correlation, async () =>
        {
            if (!await AllowedAsync(context, "platform.roles.write", Guid.Empty, Guid.Empty,
                ["Code", "Rules", "Enabled"], cancellationToken)) return Denied();
            if (!RolePolicy.CanDelegate(await Authorization.ReadGrantsAsync(context, cancellationToken), snapshot)) return Denied();
            var role = new AccessRole(database.TenantId, code, snapshot);
            database.Roles.Add(role);
            return Audit(context, role.Id, null, role.Version, PlatformAuditAction.RoleChanged, correlation);
        }, cancellationToken, DirectoryCommandKind.CreateRole, [code, DirectoryCommandReceipts.CanonicalRules(snapshot)],
            id => RoleReplayAllowedAsync(context, id, snapshot, cancellationToken));
    }

    /// <summary>Creates tenant-local department metadata; creation requires explicit tenant-wide management scope.</summary>
    public Task<DirectoryWriteResult> CreateDepartmentAsync(TenantContext context, string code, string name,
        Guid correlation, CancellationToken cancellationToken = default, Guid? parentId = null) =>
        WriteAsync(context, correlation, async () =>
        {
            if (!await AllowedAsync(context, "platform.departments.write", Guid.Empty, Guid.Empty,
                ["Code", "Name", "Enabled"], cancellationToken)) return Denied();
            if (parentId is { } parent && (!await DepartmentEnabledAsync(parent, cancellationToken)
                || !await AllowedAsync(context, "platform.departments.write", Guid.Empty, parent, ["ParentId"], cancellationToken)))
                return Denied();
            var department = new DirectoryDepartment(database.TenantId, code, name, parentId);
            database.Departments.Add(department);
            return Audit(context, department.Id, null, department.Version, PlatformAuditAction.DepartmentChanged, correlation);
        }, cancellationToken, DirectoryCommandKind.CreateDepartment, [code, name, parentId],
            async id => await DepartmentReplayAllowedAsync(context, id, ["Code", "Name", "Enabled"], cancellationToken)
                && (parentId is null || await DepartmentEnabledAsync(parentId.Value, cancellationToken)
                    && await AllowedAsync(context, "platform.departments.write", Guid.Empty, parentId.Value, ["ParentId"], cancellationToken)));

    /// <summary>Updates a department; disabling it immediately vetoes directory/session eligibility for its members.</summary>
    public Task<DirectoryWriteResult> ChangeDepartmentAsync(TenantContext context, Guid departmentId, string? expectedVersion,
        string name, bool enabled, Guid correlation, CancellationToken cancellationToken = default) =>
        WriteAsync(context, correlation, async () =>
        {
            if (!await AllowedAsync(context, "platform.departments.write", Guid.Empty, departmentId,
                ["Name", "Enabled"], cancellationToken)) return Denied();
            var department = await database.Departments.SingleOrDefaultAsync(x => x.Id == departmentId, cancellationToken);
            if (department is null) return Denied();
            var before = department.Version;
            var decision = department.Change(expectedVersion, name, enabled);
            return decision == SettingDecision.Changed
                ? Audit(context, department.Id, before, department.Version, PlatformAuditAction.DepartmentChanged, correlation)
                : VersionResult(decision);
        }, cancellationToken, DirectoryCommandKind.ChangeDepartment, [departmentId, expectedVersion, name, enabled],
            id => DepartmentReplayAllowedAsync(context, id, ["Name", "Enabled"], cancellationToken));

    /// <summary>Updates only the trusted current tenant; supplied IDs cannot authorize cross-tenant administration.</summary>
    public Task<DirectoryWriteResult> ChangeTenantAsync(TenantContext context, string? expectedVersion, string name,
        bool enabled, string defaultLanguage, Guid correlation, CancellationToken cancellationToken = default) =>
        WriteAsync(context, correlation, async () =>
        {
            if (!await AllowedAsync(context, "platform.tenants.write", Guid.Empty, Guid.Empty,
                ["Name", "Enabled", "DefaultLanguage"], cancellationToken)) return Denied();
            var tenant = await database.DirectoryTenants.SingleOrDefaultAsync(cancellationToken);
            if (tenant is null) return Denied();
            var before = tenant.Version;
            var decision = tenant.Change(expectedVersion, name, enabled, defaultLanguage);
            return decision == SettingDecision.Changed
                ? Audit(context, tenant.TenantId, before, tenant.Version, PlatformAuditAction.TenantChanged, correlation)
                : VersionResult(decision);
        }, cancellationToken, DirectoryCommandKind.ChangeTenant, [expectedVersion, name, enabled, defaultLanguage],
            _ => AllowedAsync(context, "platform.tenants.write", Guid.Empty, Guid.Empty,
                ["Name", "Enabled", "DefaultLanguage"], cancellationToken));

    private CurrentAuthorization Authorization => new(database, clock);
    private Task<bool> DepartmentEnabledAsync(Guid id, CancellationToken ct) =>
        database.Departments.AsNoTracking().AnyAsync(x => x.Id == id && x.Enabled, ct);

    private async Task<bool> AllowedAsync(TenantContext context, string permission, Guid owner, Guid department,
        string[] requiredFields, CancellationToken ct) =>
        requiredFields.All(RolePolicy.VisibleFields(context.TenantId, context.ActorId, permission,
            new ResourceScope(database.TenantId, owner, department), await Authorization.ReadGrantsAsync(context, ct)).Contains);

    private DirectoryWriteResult Audit(TenantContext context, Guid id, string? before, string after,
        PlatformAuditAction action, Guid correlation)
    {
        database.Audit.Add(new PlatformAudit(database.TenantId, context.ActorId, id, correlation, action, before, after, clock.GetUtcNow()));
        return new(DirectoryDecision.Changed, id, after);
    }

    private static DirectoryWriteResult Denied() => new(DirectoryDecision.Forbidden);
    private static DirectoryWriteResult VersionResult(SettingDecision decision) =>
        new(decision == SettingDecision.VersionRequired ? DirectoryDecision.VersionRequired : DirectoryDecision.VersionConflict);

    private async Task<DirectoryWriteResult> WriteAsync(TenantContext context, Guid correlation,
        Func<Task<DirectoryWriteResult>> mutation, CancellationToken ct, DirectoryCommandKind? kind = null,
        object?[]? arguments = null, Func<Guid, Task<bool>>? replayAllowed = null)
    {
        ct.ThrowIfCancellationRequested();
        if (operation?.OperationId == Guid.Empty) throw new ArgumentException("An operation UUID is required.");
        if (operation is not null && (kind is null || replayAllowed is null))
            throw new InvalidOperationException("This command cannot use nonsecret directory receipts.");
        if (correlation == Guid.Empty) throw new ArgumentException("A correlation identity is required.");
        if (database.ChangeTracker.Entries().Any() || database.Database.CurrentTransaction is not null
            || System.Transactions.Transaction.Current is not null
            || database.Database.AutoTransactionBehavior == AutoTransactionBehavior.Never)
            throw new InvalidOperationException("Directory commands require a clean independent owner unit of work.");
        var fingerprint = kind is not null && operation is not null
            ? DirectoryCommandReceipts.Fingerprint(kind.Value, [context.TenantId, context.ActorId, .. arguments ?? []]) : null;
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                // Receipt uniqueness, reference checks, business and audit share one owner transaction.
                await using var transaction = await database.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
                if ((await Authorization.ReadGrantsAsync(context, ct)).IsEmpty) return Denied();
                if (operation is not null)
                {
                    var receipt = await DirectoryCommandReceipts.FindAsync(database, context, operation, clock.GetUtcNow(), ct);
                    if (receipt is not null)
                    {
                        if (receipt.Kind != kind || receipt.Fingerprint != fingerprint)
                            return new(DirectoryDecision.IdempotencyConflict);
                        if (!await replayAllowed!(receipt.ResultId)) return Denied();
                        return new(DirectoryDecision.Changed, receipt.ResultId, receipt.ResultVersion);
                    }
                }
                var result = await mutation();
                if (result.Decision != DirectoryDecision.Changed) return result;
                if (operation is not null)
                    DirectoryCommandReceipts.Stage(database, context, operation, kind!.Value, fingerprint!,
                        result.Id!.Value, result.Version!, clock.GetUtcNow());
                await database.SaveChangesAsync(ct);
                await transaction.CommitAsync(ct);
                return result;
            }
            catch (DbUpdateConcurrencyException) { return new(DirectoryDecision.VersionConflict); }
            // Missing-key Serializable readers may deadlock. A fresh transaction then reads the winner's receipt.
            catch (DbUpdateException error) when (error.InnerException is MySqlException { Number: 1062 or 1213 or 1205 }
                && operation is not null && attempt < 4)
            { }
            catch (MySqlException error) when (error.Number is 1213 or 1205 && operation is not null && attempt < 4) { }
            catch (DbUpdateException error) when (error.InnerException is MySqlException { Number: 1062 })
            { return new(DirectoryDecision.Duplicate); }
            catch (DbUpdateException error) when (error.InnerException is MySqlException { Number: 1213 or 1205 })
            { return new(DirectoryDecision.VersionConflict); }
            catch (MySqlException error) when (error.Number is 1213 or 1205)
            { return new(DirectoryDecision.VersionConflict); }
            finally { database.ChangeTracker.Clear(); }
            await Task.Delay(TimeSpan.FromMilliseconds(10 * (attempt + 1)), ct);
        }
    }
}
