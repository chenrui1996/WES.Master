using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Wes.Platform.Domain;
using Wes.Platform.Infrastructure.Persistence;

namespace Wes.Platform.Features.Identity;

/// <summary>Refresh result; only a committed rotation may return a transport secret.</summary>
/// <param name="Decision">Expected family decision, never a synthetic success after persistence failure.</param>
/// <param name="Successor">New secret only after a durable committed rotation.</param>
public sealed record RefreshOutcome(RefreshDecision Decision, RefreshSecret? Successor);

/// <summary>
/// Owner-local refresh transaction. Reloads after a version conflict and persists known-replay revocation,
/// rather than treating an optimistic conflict as permission to keep using the stale family.
/// </summary>
public sealed class RefreshSessionHandler(
    PlatformDbContext database, TimeProvider clock, ILogger<RefreshSessionHandler> logger)
{
    /// <summary>Attempts one refresh in the trusted context tenant, with a bounded conflict re-read.</summary>
    public async Task<RefreshOutcome> HandleAsync(Guid sessionId, string refreshValue, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (sessionId == Guid.Empty || refreshValue is null || refreshValue.Length != 43
            || refreshValue.Any(character => !(char.IsAsciiLetterOrDigit(character) || character is '-' or '_')))
        {
            return new RefreshOutcome(RefreshDecision.Invalid, null);
        }

        if (database.ChangeTracker.Entries().Any() || database.Database.CurrentTransaction is not null)
        {
            throw new InvalidOperationException("Refresh requires its own clean Platform unit of work.");
        }

        var digest = RefreshSecret.DigestOf(refreshValue);
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var session = await database.Sessions.Include(item => item.Credentials)
                .SingleOrDefaultAsync(item => item.Id == sessionId, cancellationToken);
            if (session is null)
            {
                return new RefreshOutcome(RefreshDecision.Invalid, null);
            }

            var successor = RefreshSecret.Generate();
            var decision = session.Rotate(digest, successor.Digest, clock.GetUtcNow());
            if (decision is RefreshDecision.Invalid or RefreshDecision.Inactive)
            {
                return new RefreshOutcome(decision, null);
            }

            try
            {
                await database.SaveChangesAsync(cancellationToken);
                if (decision == RefreshDecision.Replayed)
                {
                    logger.LogWarning("Refresh rejected {Code}; family revocation committed", "RefreshReplay");
                }

                return new RefreshOutcome(decision, decision == RefreshDecision.Rotated ? successor : null);
            }
            catch (DbUpdateConcurrencyException)
            {
                logger.LogWarning("Refresh conflict {Code}; re-reading current family", "SessionVersionConflict");
                // This handler owns the clean unit of work. Discard only its failed aggregate graph.
                // EF rolled back the attempted root/credential changes as one SaveChanges transaction.
                database.ChangeTracker.Clear();
            }
        }

        logger.LogError("Refresh failed {Code}; no credential disclosed", "RepeatedSessionConflict");
        throw new InvalidOperationException("The session changed repeatedly; reauthentication is required.");
    }
}
