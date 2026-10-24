using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Wes.Platform.Domain;
using Wes.Platform.Infrastructure.Persistence;

namespace Wes.Platform.Features.Identity;

/// <summary>Durable challenge issuance and consumption only, not a renderer or credential verifier.</summary>
public sealed class LoginChallengeHandler(PlatformDbContext database, TimeProvider clock,
    LoginChallengeProtection protection, ILogger<LoginChallengeHandler> logger)
{
    private const string AnswerAlphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";

    /// <summary>Returns raw answer only to a trusted server renderer after the bound challenge commits.</summary>
    public async Task<LoginChallengeDelivery> IssueAsync(LoginAccountKey account, LoginSourceKey source,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(account);
        ArgumentNullException.ThrowIfNull(source);
        cancellationToken.ThrowIfCancellationRequested();
        RequireIndependentContext();
        try
        {
            var id = Guid.NewGuid();
            var answer = new string(Enumerable.Range(0, 6)
                .Select(_ => AnswerAlphabet[RandomNumberGenerator.GetInt32(AnswerAlphabet.Length)]).ToArray());
            var challenge = new LoginChallenge(database.TenantId, id, account, source,
                protection.ComputeMac(database.TenantId, id, account, source, answer), clock.GetUtcNow());
            database.LoginChallenges.Add(challenge);
            await database.SaveChangesAsync(cancellationToken);
            return new LoginChallengeDelivery(id, answer, challenge.ExpiresAt);
        }
        finally
        {
            database.ChangeTracker.Clear();
        }
    }

    /// <summary>Returns Accepted only after committing the first correct bound submission; all expected failures are generic.</summary>
    public async Task<ChallengeDecision> ConsumeAsync(Guid id, LoginAccountKey account, LoginSourceKey source, string? answer,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(account);
        ArgumentNullException.ThrowIfNull(source);
        cancellationToken.ThrowIfCancellationRequested();
        RequireIndependentContext();
        try
        {
            if (id == Guid.Empty) return Reject();
            var challenge = await database.LoginChallenges.SingleOrDefaultAsync(item => item.Id == id, cancellationToken);
            if (challenge is null) return Reject();
            // Invalid transport answers still burn the bound attempt, without hashing unbounded or ill-formed input.
            var candidate = answer is { Length: 6 } && answer.All(AnswerAlphabet.Contains) ? answer : "";
            var previousVersion = challenge.Version;
            var decision = challenge.Consume(account, source,
                protection.ComputeMac(database.TenantId, id, account, source, candidate), clock.GetUtcNow());
            if (challenge.Version != previousVersion)
            {
                try
                {
                    await database.SaveChangesAsync(cancellationToken);
                }
                catch (DbUpdateConcurrencyException)
                {
                    return Reject();
                }
            }
            return decision == ChallengeDecision.Accepted ? decision : Reject();
        }
        finally
        {
            database.ChangeTracker.Clear();
        }
    }

    private ChallengeDecision Reject()
    {
        logger.LogWarning("Login challenge rejected {Code}", "LoginChallengeRejected");
        return ChallengeDecision.Rejected;
    }

    private void RequireIndependentContext()
    {
        if (database.ChangeTracker.Entries().Any() || database.Database.CurrentTransaction is not null
            || System.Transactions.Transaction.Current is not null)
            throw new InvalidOperationException("Challenge lifecycle requires a clean independent unit of work.");
    }
}
