using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using MySql.Data.MySqlClient;
using Wes.Platform.Domain;
using Wes.Platform.Infrastructure.Persistence;

namespace Wes.Platform.Features.Identity;

/// <summary>Internal risk snapshot, not a transport response or permission to issue a session.</summary>
/// <param name="Version">Null means no prior persisted failures.</param>
/// <param name="Gate">Credential verification prerequisite at the observed time.</param>
public sealed record AccountRiskSnapshot(string? Version, LoginGate Gate);

/// <summary>Failure persistence outcomes; none represents successful authentication.</summary>
public enum AccountFailureDecision
{
    /// <summary>One actual incorrect-password observation was committed.</summary>
    Recorded,
    /// <summary>The verification context is stale; restart the whole risk/credential procedure.</summary>
    VersionConflict,
    /// <summary>A consumed valid challenge is required before checking credentials.</summary>
    ChallengeRequired,
    /// <summary>The temporary account lock prevents checking credentials.</summary>
    Locked
}

/// <summary>Two-stage version-bound account risk; callers cannot reuse a stale credential/challenge decision.</summary>
public sealed class AccountLoginRiskHandler(PlatformDbContext database, TimeProvider clock,
    ILogger<AccountLoginRiskHandler> logger)
{
    /// <summary>Reads risk without creating empty rows or looking up account existence.</summary>
    public async Task<AccountRiskSnapshot> ReadAsync(LoginAccountKey key, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        cancellationToken.ThrowIfCancellationRequested();
        RequireIndependentContext();
        var budget = await database.LoginAccounts.AsNoTracking()
            .SingleOrDefaultAsync(item => item.AccountDigest == key.Digest, cancellationToken);
        return new AccountRiskSnapshot(budget?.Version,
            budget?.Evaluate(clock.GetUtcNow(), false) ?? LoginGate.VerifyPassword);
    }

    /// <summary>Persists only a real password failure against the observed version, never silently retries verification.</summary>
    public async Task<AccountFailureDecision> RecordFailureAsync(LoginAccountKey key, string? expectedVersion,
        bool validChallenge, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        cancellationToken.ThrowIfCancellationRequested();
        if (expectedVersion is not null
            && (!Guid.TryParseExact(expectedVersion, "D", out var version) || version == Guid.Empty))
            throw new ArgumentException("Account risk requires an opaque observed version.");
        RequireIndependentContext();
        try
        {
            var budget = await database.LoginAccounts
                .SingleOrDefaultAsync(item => item.AccountDigest == key.Digest, cancellationToken);
            if (!string.Equals(budget?.Version, expectedVersion, StringComparison.Ordinal))
                return Conflict();
            if (budget is null)
            {
                budget = new LoginAccountBudget(database.TenantId, key);
                database.LoginAccounts.Add(budget);
            }
            var gate = budget.RecordFailure(clock.GetUtcNow(), validChallenge);
            if (gate != LoginGate.VerifyPassword)
            {
                var decision = gate == LoginGate.Locked ? AccountFailureDecision.Locked : AccountFailureDecision.ChallengeRequired;
                logger.LogWarning("Account risk rejected {Code}",
                    gate == LoginGate.Locked ? "AccountTemporarilyLocked" : "AccountChallengeRequired");
                return decision;
            }
            try
            {
                await database.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateConcurrencyException)
            {
                return Conflict();
            }
            catch (DbUpdateException error) when (error.InnerException is MySqlException { Number: 1062 }
                && database.Entry(budget).State == EntityState.Added
                && error.Entries.Count == 1 && ReferenceEquals(error.Entries[0].Entity, budget))
            {
                return Conflict();
            }
            logger.LogWarning("Account password failure committed {Code}",
                budget.LockedUntil is not null ? "AccountLockStarted" : "AccountPasswordFailure");
            return AccountFailureDecision.Recorded;
        }
        finally
        {
            database.ChangeTracker.Clear();
        }
    }

    private AccountFailureDecision Conflict()
    {
        logger.LogWarning("Account risk rejected {Code}", "AccountRiskVersionConflict");
        return AccountFailureDecision.VersionConflict;
    }

    private void RequireIndependentContext()
    {
        if (database.ChangeTracker.Entries().Any() || database.Database.CurrentTransaction is not null
            || System.Transactions.Transaction.Current is not null)
            throw new InvalidOperationException("Account risk requires a clean independent unit of work.");
    }
}
