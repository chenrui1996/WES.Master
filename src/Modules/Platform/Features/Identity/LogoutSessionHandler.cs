using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Wes.Platform.Domain;
using Wes.Platform.Features.Tenancy;
using Wes.Platform.Infrastructure.Persistence;

namespace Wes.Platform.Features.Identity;

/// <summary>Current-family logout outcomes, never broader administrative revocation.</summary>
public enum LogoutDecision
{
    /// <summary>The owned family is durably revoked, including an idempotent repeat.</summary>
    Completed,
    /// <summary>No matching owned family; no existence details are disclosed.</summary>
    Rejected
}

/// <summary>Revokes the trusted subject's current family and writes a version-linked audit atomically.</summary>
public sealed class LogoutSessionHandler(PlatformDbContext database, TimeProvider clock, ILogger<LogoutSessionHandler> logger)
{
    /// <summary>Requires verified owner context and a clean independent transaction; no target ID is accepted.</summary>
    public async Task<LogoutDecision> HandleAsync(TenantContext context, Guid correlation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();
        if (correlation == Guid.Empty) throw new ArgumentException("Logout requires a valid correlation reference.");
        if (context.TenantId != database.TenantId || context.ActorId == Guid.Empty || context.SessionId == Guid.Empty)
            throw new InvalidOperationException("Logout requires matching trusted owner context.");
        if (database.ChangeTracker.Entries().Any() || database.Database.CurrentTransaction is not null
            || System.Transactions.Transaction.Current is not null
            || database.Database.AutoTransactionBehavior == AutoTransactionBehavior.Never)
            throw new InvalidOperationException("Logout requires a clean independent unit of work.");
        try
        {
            for (var attempt = 0; attempt < 8; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var session = await database.Sessions.SingleOrDefaultAsync(
                    item => item.Id == context.SessionId && item.UserId == context.ActorId, cancellationToken);
                if (session is null)
                {
                    logger.LogWarning("Logout rejected {Code}", "LogoutSessionRejected");
                    return LogoutDecision.Rejected;
                }
                var instant = clock.GetUtcNow();
                var now = new DateTimeOffset(instant.UtcTicks - instant.UtcTicks % 10, TimeSpan.Zero);
                if (session.StateAt(now) == SessionState.Revoked) return LogoutDecision.Completed;
                var before = session.Version;
                session.Revoke(now);
                database.Audit.Add(new PlatformAudit(context.TenantId, context.ActorId, session.Id, correlation,
                    PlatformAuditAction.SessionRevoked, before, session.Version, now));
                try
                {
                    await database.SaveChangesAsync(cancellationToken);
                    return LogoutDecision.Completed;
                }
                catch (DbUpdateConcurrencyException)
                {
                    logger.LogWarning("Logout contention {Code} at attempt {Attempt}", "LogoutSessionVersionConflict", attempt + 1);
                }
                database.ChangeTracker.Clear();
            }
            logger.LogError("Logout failed {Code}", "LogoutSessionContentionExhausted");
            throw new InvalidOperationException("Logout could not acquire a current session version.");
        }
        finally
        {
            database.ChangeTracker.Clear();
        }
    }
}
