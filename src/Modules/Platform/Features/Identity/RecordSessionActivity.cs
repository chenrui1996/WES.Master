using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Wes.Platform.Domain;
using Wes.Platform.Features.Tenancy;
using Wes.Platform.Infrastructure.Persistence;

namespace Wes.Platform.Features.Identity;

/// <summary>Real user activity can succeed, require reauthentication or conflict with a concurrent family change.</summary>
public enum ActivityDecision
{
    /// <summary>The current family records the accepted user action.</summary>
    Recorded,
    /// <summary>No current active family belongs to the verified caller.</summary>
    Inactive,
    /// <summary>A concurrent family write requires a fresh explicit interaction, never replay.</summary>
    VersionConflict
}

/// <summary>Owns the idle-clock transition; HTTP, refresh and polling cannot bypass this current-family check.</summary>
public sealed class RecordSessionActivity(PlatformDbContext database, TimeProvider clock, ILogger<RecordSessionActivity> logger)
{
    /// <summary>Records activity only for the verified subject in a clean owner unit of work.</summary>
    public async Task<ActivityDecision> HandleAsync(TenantContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();
        if (context.TenantId != database.TenantId || context.ActorId == Guid.Empty || context.SessionId == Guid.Empty)
            throw new InvalidOperationException("Activity requires a verified matching owner context.");
        if (database.ChangeTracker.Entries().Any() || database.Database.CurrentTransaction is not null
            || System.Transactions.Transaction.Current is not null)
            throw new InvalidOperationException("Activity requires a clean independent unit of work.");
        try
        {
            var session = await database.Sessions.SingleOrDefaultAsync(
                item => item.Id == context.SessionId && item.UserId == context.ActorId, cancellationToken);
            var instant = clock.GetUtcNow();
            var now = new DateTimeOffset(instant.UtcTicks - instant.UtcTicks % 10, TimeSpan.Zero);
            if (session is null || !session.RecordActivity(now))
            {
                logger.LogWarning("Session activity rejected {Code}", ActivityDecision.Inactive);
                return ActivityDecision.Inactive;
            }
            try { await database.SaveChangesAsync(cancellationToken); }
            catch (DbUpdateConcurrencyException)
            {
                logger.LogWarning("Session activity rejected {Code}", ActivityDecision.VersionConflict);
                return ActivityDecision.VersionConflict;
            }
            return ActivityDecision.Recorded;
        }
        finally { database.ChangeTracker.Clear(); }
    }
}
