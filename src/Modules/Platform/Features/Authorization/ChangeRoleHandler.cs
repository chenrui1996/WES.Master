using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Wes.Platform.Domain;
using Wes.Platform.Features.Directory;
using Wes.Platform.Features.Settings;
using Wes.Platform.Features.Tenancy;
using Wes.Platform.Infrastructure.Persistence;

namespace Wes.Platform.Features.Authorization;

/// <summary>A committed role successor, or an explicit denial/conflict without a version disclosure.</summary>
public sealed record RoleWriteResult(SettingDecision Decision, string? Version = null);

/// <summary>Authorizes role edits against current management and delegable grants, then atomically audits the mutation.</summary>
public sealed class ChangeRoleHandler(PlatformDbContext database, TimeProvider clock, ILogger<ChangeRoleHandler> logger,
    DirectoryOperation? operation = null)
{
    /// <summary>Updates an existing role with a CAS fence and an optional actor-scoped nonsecret receipt.</summary>
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

        var requested = PermissionGrant.ValidateSnapshot(rules);
        async Task<bool> ReplayAllowedAsync()
        {
            var caller = await new CurrentAuthorization(database, clock).ReadGrantsAsync(context, cancellationToken);
            var fields = RolePolicy.VisibleFields(context.TenantId, context.ActorId, "platform.roles.write",
                new ResourceScope(database.TenantId, Guid.Empty, Guid.Empty), caller);
            var current = await database.Roles.AsNoTracking().SingleOrDefaultAsync(item => item.Id == id, cancellationToken);
            return fields.Contains("Rules") && fields.Contains("Enabled") && current is not null
                && RolePolicy.CanDelegate(caller, current.Rules) && RolePolicy.CanDelegate(caller, requested);
        }

        var committed = await VersionedCommandTransaction.RunAsync(database, clock, logger, context, operation,
            DirectoryCommandKind.ChangeRole, id, [expectedVersion, enabled, DirectoryCommandReceipts.CanonicalRules(requested)], async () =>
        {
            if (!await ReplayAllowedAsync()) return new(SettingDecision.Forbidden);
            var role = await database.Roles.SingleOrDefaultAsync(item => item.Id == id, cancellationToken);
            if (role is null) return new(SettingDecision.Forbidden);
            var before = role.Version;
            var decision = role.Change(expectedVersion, enabled, requested);
            if (decision != SettingDecision.Changed) return new(decision);
            database.Audit.Add(new PlatformAudit(context.TenantId, context.ActorId, role.Id, correlation,
                PlatformAuditAction.RoleChanged, before, role.Version, clock.GetUtcNow()));
            return new(SettingDecision.Changed, role.Version);
        }, ReplayAllowedAsync, cancellationToken);
        return committed.Decision == SettingDecision.Changed ? new(committed.Decision, committed.Version) : Reject(committed.Decision);
    }

    private RoleWriteResult Reject(SettingDecision decision)
    {
        logger.LogWarning("Role write rejected {Code}", decision);
        return new RoleWriteResult(decision);
    }
}
