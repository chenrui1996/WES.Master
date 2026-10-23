using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using MySql.Data.MySqlClient;
using Wes.Platform.Domain;
using Wes.Platform.Infrastructure.Persistence;

namespace Wes.Platform.Features.Identity;

/// <summary>Counts one login request across accounts and tenants before any credential/challenge checks.</summary>
public sealed class AdmitLoginSourceHandler(LoginSourceDbContext database, TimeProvider clock,
    ILogger<AdmitLoginSourceHandler> logger)
{
    /// <summary>Returns admission only after the observation is committed; this never issues identity or access.</summary>
    public async Task<SourceAdmission> HandleAsync(LoginSourceKey key, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        cancellationToken.ThrowIfCancellationRequested();
        if (database.ChangeTracker.Entries().Any() || database.Database.CurrentTransaction is not null
            || System.Transactions.Transaction.Current is not null)
        {
            throw new InvalidOperationException("Source admission requires a clean independent risk unit of work.");
        }

        try
        {
            for (var attempt = 0; attempt < 8; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var budget = await database.Sources.SingleOrDefaultAsync(item => item.SourceDigest == key.Digest, cancellationToken);
                if (budget is null)
                {
                    budget = new LoginSourceBudget(key);
                    database.Sources.Add(budget);
                }
                var decision = budget.Admit(clock.GetUtcNow());
                if (decision == SourceAdmission.RateLimited)
                {
                    logger.LogWarning("Login source rejected {Code}", "LoginSourceRateLimited");
                    return decision;
                }

                try
                {
                    await database.SaveChangesAsync(cancellationToken);
                    return SourceAdmission.Admitted;
                }
                catch (DbUpdateConcurrencyException)
                {
                    logger.LogWarning("Login source contention {Code} at attempt {Attempt}", "LoginSourceVersionConflict", attempt + 1);
                }
                catch (DbUpdateException error) when (error.InnerException is MySqlException { Number: 1062 }
                    && database.Entry(budget).State == EntityState.Added
                    && error.Entries.Count == 1 && ReferenceEquals(error.Entries[0].Entity, budget))
                {
                    // This clean unit of work inserts only the source primary key; a competing first admission won.
                    logger.LogWarning("Login source contention {Code} at attempt {Attempt}", "LoginSourceCreationConflict", attempt + 1);
                }
                database.ChangeTracker.Clear();
            }

            logger.LogError("Login source admission failed {Code}", "LoginSourceContentionExhausted");
            throw new InvalidOperationException("Login source admission could not acquire a current budget.");
        }
        finally
        {
            database.ChangeTracker.Clear();
        }
    }
}
