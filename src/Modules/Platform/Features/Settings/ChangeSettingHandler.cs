using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Wes.Platform.Domain;
using Wes.Platform.Features.Authorization;
using Wes.Platform.Features.Directory;
using Wes.Platform.Features.Tenancy;
using Wes.Platform.Infrastructure.Persistence;

namespace Wes.Platform.Features.Settings;

/// <summary>Only a committed change discloses its successor version.</summary>
public sealed record SettingWriteResult(SettingDecision Decision, string? Version = null);

/// <summary>Changes one reviewed nonsecret setting and its audit in the owner transaction.</summary>
public sealed class ChangeSettingHandler(PlatformDbContext database, TimeProvider clock, ILogger<ChangeSettingHandler> logger,
    DirectoryOperation? operation = null)
{
    /// <summary>Requires a clean owner context, current authorization and an expected version.</summary>
    public async Task<SettingWriteResult> HandleAsync(TenantContext context, Guid id, string? expectedVersion,
        string value, Guid correlation, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (database.ChangeTracker.Entries().Any() || database.Database.CurrentTransaction is not null)
        {
            throw new InvalidOperationException("Settings writes require a clean Platform unit of work.");
        }
        if (id == Guid.Empty || correlation == Guid.Empty)
        {
            throw new ArgumentException("Settings writes require valid target and correlation references.");
        }

        async Task<bool> ReplayAllowedAsync()
        {
            var fields = await new CurrentAuthorization(database, clock).VisibleFieldsAsync(context,
                "platform.settings.write", new ResourceScope(database.TenantId, Guid.Empty, Guid.Empty), cancellationToken);
            return fields.Contains("Value") && await database.Settings.AsNoTracking().AnyAsync(item => item.Id == id, cancellationToken);
        }

        var committed = await VersionedCommandTransaction.RunAsync(database, clock, logger, context, operation,
            DirectoryCommandKind.ChangeSetting, id, [expectedVersion, value], async () =>
        {
            var fields = await new CurrentAuthorization(database, clock).VisibleFieldsAsync(context,
                "platform.settings.write", new ResourceScope(database.TenantId, Guid.Empty, Guid.Empty), cancellationToken);
            if (!fields.Contains("Value")) return new(SettingDecision.Forbidden);
            var setting = await database.Settings.SingleOrDefaultAsync(item => item.Id == id, cancellationToken);
            if (setting is null) return new(SettingDecision.UnknownSetting);
            var before = setting.Version;
            var decision = setting.Change(expectedVersion, value);
            if (decision != SettingDecision.Changed) return new(decision);
            database.Audit.Add(new PlatformAudit(context.TenantId, context.ActorId, setting.Id, correlation,
                PlatformAuditAction.SettingChanged, before, setting.Version, clock.GetUtcNow()));
            return new(SettingDecision.Changed, setting.Version);
        }, ReplayAllowedAsync, cancellationToken);
        return committed.Decision == SettingDecision.Changed ? committed : Reject(committed.Decision);
    }

    private SettingWriteResult Reject(SettingDecision decision)
    {
        logger.LogWarning("Settings write rejected {Code}", decision);
        return new SettingWriteResult(decision);
    }
}
