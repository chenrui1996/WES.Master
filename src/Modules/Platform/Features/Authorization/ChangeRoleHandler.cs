using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Wes.Platform.Domain;
using Wes.Platform.Features.Tenancy;
using Wes.Platform.Infrastructure.Persistence;

namespace Wes.Platform.Features.Authorization;

/// <summary>A committed role successor, or an explicit denial/conflict without a version disclosure.</summary>
public sealed record RoleWriteResult(SettingDecision Decision, string? Version = null);

/// <summary>Authorizes role edits against current management and delegable grants, then atomically audits the mutation.</summary>
public sealed class ChangeRoleHandler(PlatformDbContext database, TimeProvider clock, ILogger<ChangeRoleHandler> logger)
{
    /// <summary>Updates an existing tenant-wide role only; assignment, creation and HTTP idempotency remain separate contracts.</summary>
    public async Task<RoleWriteResult> HandleAsync(TenantContext context, Guid id, string? expectedVersion,
        bool enabled, IEnumerable<PermissionGrant> rules, Guid correlation, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (database.ChangeTracker.Entries().Any() || database.Database.CurrentTransaction is not null)
        {
            throw new InvalidOperationException("Role writes require a clean Platform unit of work.");
        }
        if (id == Guid.Empty || correlation == Guid.Empty)
        {
            throw new ArgumentException("Role writes require valid target and correlation references.");
        }

        try
        {
            var caller = await new CurrentAuthorization(database, clock).ReadGrantsAsync(context, cancellationToken);
            var fields = RolePolicy.VisibleFields(context.TenantId, context.ActorId, "platform.roles.write",
                new ResourceScope(database.TenantId, Guid.Empty, Guid.Empty), caller);
            if (!fields.Contains("Rules") || !fields.Contains("Enabled")) return Reject(SettingDecision.Forbidden);

            var role = await database.Roles.SingleOrDefaultAsync(item => item.Id == id, cancellationToken);
            if (role is null) return Reject(SettingDecision.Forbidden);
            var requested = PermissionGrant.ValidateSnapshot(rules);
            if (!RolePolicy.CanDelegate(caller, role.Rules) || !RolePolicy.CanDelegate(caller, requested))
            {
                return Reject(SettingDecision.Forbidden);
            }

            var before = role.Version;
            var decision = role.Change(expectedVersion, enabled, requested);
            if (decision != SettingDecision.Changed) return Reject(decision);
            database.Audit.Add(new PlatformAudit(context.TenantId, context.ActorId, role.Id, correlation,
                PlatformAuditAction.RoleChanged, before, role.Version, clock.GetUtcNow()));
            await database.SaveChangesAsync(cancellationToken);
            return new RoleWriteResult(SettingDecision.Changed, role.Version);
        }
        catch (DbUpdateConcurrencyException)
        {
            return Reject(SettingDecision.VersionConflict);
        }
        finally
        {
            // The handler owns the context and must not retain a failed permission mutation.
            database.ChangeTracker.Clear();
        }
    }

    private RoleWriteResult Reject(SettingDecision decision)
    {
        logger.LogWarning("Role write rejected {Code}", decision);
        return new RoleWriteResult(decision);
    }
}
