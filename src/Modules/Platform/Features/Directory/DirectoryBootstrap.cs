using System.Data;
using Microsoft.EntityFrameworkCore;
using Wes.Platform.Domain;
using Wes.Platform.Features.Identity;
using Wes.Platform.Infrastructure.Persistence;

namespace Wes.Platform.Features.Directory;

/// <summary>Explicit local process provisioning only; must never be mapped to an anonymous or ordinary HTTP endpoint.</summary>
public sealed class DirectoryBootstrap(PlatformDbContext database, TimeProvider clock)
{
    /// <summary>Creates a new tenant's initial directory/credential/role/membership atomically using explicit reviewed grants and breach evidence.</summary>
    public async Task<DirectoryWriteResult> ProvisionAsync(bool localProvisioningEnabled, Guid userId, string tenantName,
        string defaultLanguage, string departmentCode, string departmentName, string account, string displayName,
        string password, PasswordEvidence breachEvidence, string roleCode, IEnumerable<PermissionGrant> reviewedGrants,
        Guid correlation, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!localProvisioningEnabled) return new(DirectoryDecision.Forbidden);
        if (userId == Guid.Empty || correlation == Guid.Empty)
            throw new ArgumentException("Explicit bootstrap subject and correlation identities are required.");
        if (database.ChangeTracker.Entries().Any() || database.Database.CurrentTransaction is not null
            || System.Transactions.Transaction.Current is not null
            || database.Database.AutoTransactionBehavior == AutoTransactionBehavior.Never)
            throw new InvalidOperationException("Local bootstrap requires a clean independent owner unit of work.");
        try
        {
            await using var transaction = await database.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
            // Existing/imported security state is never adopted as an implicitly approved new administrator.
            if (await database.DirectoryTenants.AnyAsync(cancellationToken)
                || await database.DirectoryUsers.AnyAsync(cancellationToken)
                || await database.PasswordCredentials.AnyAsync(cancellationToken)
                || await database.Roles.AnyAsync(cancellationToken)
                || await database.Memberships.AnyAsync(cancellationToken)
                || await database.Sessions.AnyAsync(cancellationToken)
                || await database.Settings.AnyAsync(cancellationToken))
                return new(DirectoryDecision.Forbidden);
            var accountName = AccountName.Normalize(account);
            var hashed = PasswordCredentialCodec.Create(password, accountName, breachEvidence, PasswordEvidence.Clear);
            if (hashed.Decision != PasswordDecision.Accepted || hashed.Hash is null)
                return new(DirectoryDecision.PasswordRejected, PasswordDecision: hashed.Decision);
            var tenant = new DirectoryTenant(database.TenantId, tenantName, defaultLanguage);
            var department = new DirectoryDepartment(database.TenantId, departmentCode, departmentName);
            var user = new DirectoryUser(database.TenantId, userId, accountName, displayName, department.Id);
            var role = new AccessRole(database.TenantId, roleCode, reviewedGrants);
            var credential = new PasswordCredential(database.TenantId, userId, accountName, hashed.Hash, clock.GetUtcNow());
            database.DirectoryTenants.Add(tenant);
            database.Departments.Add(department);
            database.DirectoryUsers.Add(user);
            database.Roles.Add(role);
            database.Memberships.Add(new RoleMembership(database.TenantId, userId, role.Id));
            database.PasswordCredentials.Add(credential);
            foreach (var key in new[] { "query.snapshotTtlSeconds", "dictionary.cacheTtlSeconds", "device.feedbackFreshnessMilliseconds" })
            {
                var setting = new VersionedSetting(database.TenantId, key);
                database.Settings.Add(setting);
                database.Audit.Add(new PlatformAudit(database.TenantId, userId, setting.Id, correlation,
                    PlatformAuditAction.SettingChanged, null, setting.Version, clock.GetUtcNow()));
            }
            foreach (var (id, version, action) in new[]
            {
                (tenant.TenantId, tenant.Version, PlatformAuditAction.TenantChanged),
                (department.Id, department.Version, PlatformAuditAction.DepartmentChanged),
                (user.Id, user.Version, PlatformAuditAction.UserChanged),
                (user.Id, credential.Version, PlatformAuditAction.CredentialCreated),
                (role.Id, role.Version, PlatformAuditAction.RoleChanged),
                (user.Id, user.Version, PlatformAuditAction.MembershipChanged)
            })
                database.Audit.Add(new PlatformAudit(database.TenantId, userId, id, correlation, action, null, version, clock.GetUtcNow()));
            await database.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new(DirectoryDecision.Changed, userId, user.Version);
        }
        finally { database.ChangeTracker.Clear(); }
    }
}
