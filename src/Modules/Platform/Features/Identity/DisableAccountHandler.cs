using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Wes.Platform.Domain;
using Wes.Platform.Features.Authorization;
using Wes.Platform.Features.Tenancy;
using Wes.Platform.Infrastructure.Persistence;

namespace Wes.Platform.Features.Identity;

/// <summary>Internal disable outcomes; no outcome enables a user or creates a replacement session.</summary>
public enum AccountDisableDecision
{
    /// <summary>The authorized target is disabled and its existing families are revoked, including an effect-idempotent repeat.</summary>
    Completed,
    /// <summary>Current management authority or a matching tenant target is absent.</summary>
    Rejected,
    /// <summary>A fresh target version and current authorization check are required.</summary>
    VersionConflict
}

/// <summary>Current-field-authorized management command with atomic credential/family/audit writes.</summary>
public sealed class DisableAccountHandler(PlatformDbContext database, TimeProvider clock, ILogger<DisableAccountHandler> logger)
{
    /// <summary>Only this trusted tenant is addressable; Department scope requires a future authoritative directory lookup.</summary>
    public async Task<AccountDisableDecision> HandleAsync(TenantContext context, Guid userId, string expectedVersion,
        Guid correlation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();
        if (userId == Guid.Empty || correlation == Guid.Empty
            || !Guid.TryParseExact(expectedVersion, "D", out var expected) || expected == Guid.Empty)
            throw new ArgumentException("Account disable requires target, correlation and an expected credential version.");
        if (context.TenantId != database.TenantId || context.ActorId == Guid.Empty || context.SessionId == Guid.Empty)
            throw new InvalidOperationException("Account disable requires matching trusted owner context.");
        if (database.ChangeTracker.Entries().Any() || database.Database.CurrentTransaction is not null
            || System.Transactions.Transaction.Current is not null
            || database.Database.AutoTransactionBehavior == AutoTransactionBehavior.Never)
            throw new InvalidOperationException("Account disable requires a clean independent unit of work.");
        try
        {
            var fields = await new CurrentAuthorization(database, clock).VisibleFieldsAsync(context, "platform.users.write",
                new ResourceScope(database.TenantId, userId, Guid.Empty), cancellationToken);
            if (!fields.Contains("Enabled")) return Reject(AccountDisableDecision.Rejected);
            var credential = await database.PasswordCredentials.SingleOrDefaultAsync(item => item.UserId == userId, cancellationToken);
            if (credential is null) return Reject(AccountDisableDecision.Rejected);
            var families = await database.Sessions.Where(item => item.UserId == userId).OrderBy(item => item.Id).ToListAsync(cancellationToken);
            var instant = clock.GetUtcNow();
            var now = new DateTimeOffset(instant.UtcTicks - instant.UtcTicks % 10, TimeSpan.Zero);
            var unrevoked = families.Where(item => item.StateAt(now) != SessionState.Revoked).ToArray();
            if (!credential.Enabled && unrevoked.Length == 0) return AccountDisableDecision.Completed;
            if (credential.Version != expectedVersion) return Reject(AccountDisableDecision.VersionConflict);
            var before = credential.Version;
            credential.SetEnabled(false, now);
            foreach (var family in unrevoked)
            {
                var previous = family.Version;
                family.Revoke(now);
                database.Audit.Add(new PlatformAudit(database.TenantId, context.ActorId, family.Id, correlation,
                    PlatformAuditAction.SessionRevoked, previous, family.Version, now));
            }
            database.Audit.Add(new PlatformAudit(database.TenantId, context.ActorId, userId, correlation,
                PlatformAuditAction.AccountDisabled, before, credential.Version, now));
            try
            {
                await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);
                await database.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return AccountDisableDecision.Completed;
            }
            catch (DbUpdateConcurrencyException)
            {
                return Reject(AccountDisableDecision.VersionConflict);
            }
        }
        finally
        {
            database.ChangeTracker.Clear();
        }
    }

    private AccountDisableDecision Reject(AccountDisableDecision decision)
    {
        logger.LogWarning("Account disable rejected {Code}", decision);
        return decision;
    }
}
