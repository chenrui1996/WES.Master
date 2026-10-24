using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Wes.Platform.Domain;
using Wes.Platform.Features.Tenancy;
using Wes.Platform.Infrastructure.Persistence;

namespace Wes.Platform.Features.Identity;

/// <summary>Internal change outcomes; none grants a replacement authenticated session.</summary>
public enum PasswordChangeDecision
{
    /// <summary>Credential, existing-family revocations and audit committed together.</summary>
    Completed,
    /// <summary>The current subject, session or old proof did not qualify.</summary>
    Rejected,
    /// <summary>A fresh explicit submission and fresh policy evidence are required.</summary>
    VersionConflict,
    /// <summary>The new value failed the approved policy without changing state.</summary>
    PolicyRejected
}

/// <summary>Safe owner result, never an encoded hash or raw password.</summary>
public sealed record PasswordChangeResult(PasswordChangeDecision Decision, PasswordDecision? Policy = null);

/// <summary>Current-subject controlled change; not an anonymous reset or administrative credential write.</summary>
public sealed class ChangePasswordHandler(PlatformDbContext database, TimeProvider clock, ILogger<ChangePasswordHandler> logger)
{
    /// <summary>Requires current verified tenant/session context, expected credential version and authoritative breach evidence.</summary>
    public async Task<PasswordChangeResult> HandleAsync(TenantContext context, string expectedVersion, string oldPassword,
        string newPassword, PasswordEvidence breach, Guid correlation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(oldPassword);
        ArgumentNullException.ThrowIfNull(newPassword);
        cancellationToken.ThrowIfCancellationRequested();
        if (correlation == Guid.Empty || !Guid.TryParseExact(expectedVersion, "D", out var expected) || expected == Guid.Empty)
            throw new ArgumentException("Password change requires correlation and an expected credential version.");
        if (context.TenantId != database.TenantId || context.ActorId == Guid.Empty || context.SessionId == Guid.Empty)
            throw new InvalidOperationException("Password change requires matching trusted owner context.");
        if (database.ChangeTracker.Entries().Any() || database.Database.CurrentTransaction is not null
            || System.Transactions.Transaction.Current is not null
            || database.Database.AutoTransactionBehavior == AutoTransactionBehavior.Never)
            throw new InvalidOperationException("Password change requires a clean independent unit of work.");
        try
        {
            var session = await database.Sessions.SingleOrDefaultAsync(
                item => item.Id == context.SessionId && item.UserId == context.ActorId, cancellationToken);
            var now = Now();
            if (session is null || session.StateAt(now) != SessionState.Active) return Rejected();
            var credential = await database.PasswordCredentials.SingleOrDefaultAsync(item => item.UserId == context.ActorId, cancellationToken);
            if (credential is null) return Rejected();
            if (credential.Version != expectedVersion) return Conflict();
            if (credential.Verify(oldPassword) == CredentialVerification.Invalid) return Rejected();
            var before = credential.Version;
            var policy = credential.Change(newPassword, breach, now);
            if (policy != PasswordDecision.Accepted)
            {
                logger.LogWarning("Password change rejected {Code} {Policy}", "PasswordChangePolicyRejected", policy);
                return new PasswordChangeResult(PasswordChangeDecision.PolicyRejected, policy);
            }
            var families = await database.Sessions.Where(item => item.UserId == context.ActorId).OrderBy(item => item.Id).ToListAsync(cancellationToken);
            now = Now();
            if (now < credential.ChangedAt) throw new InvalidOperationException("Credential time cannot move backwards before commit.");
            if (session.StateAt(now) != SessionState.Active) return Rejected();
            foreach (var family in families)
            {
                if (family.StateAt(now) == SessionState.Revoked) continue;
                var previous = family.Version;
                family.Revoke(now);
                database.Audit.Add(new PlatformAudit(context.TenantId, context.ActorId, family.Id, correlation,
                    PlatformAuditAction.SessionRevoked, previous, family.Version, now));
            }
            database.Audit.Add(new PlatformAudit(context.TenantId, context.ActorId, context.ActorId, correlation,
                PlatformAuditAction.PasswordChanged, before, credential.Version, now));
            try
            {
                await database.SaveChangesAsync(cancellationToken);
                return new PasswordChangeResult(PasswordChangeDecision.Completed);
            }
            catch (DbUpdateConcurrencyException)
            {
                return Conflict();
            }
        }
        finally
        {
            database.ChangeTracker.Clear();
        }
    }

    private DateTimeOffset Now()
    {
        var instant = clock.GetUtcNow();
        return new DateTimeOffset(instant.UtcTicks - instant.UtcTicks % 10, TimeSpan.Zero);
    }

    private PasswordChangeResult Rejected()
    {
        logger.LogWarning("Password change rejected {Code}", "PasswordChangeRejected");
        return new PasswordChangeResult(PasswordChangeDecision.Rejected);
    }

    private PasswordChangeResult Conflict()
    {
        logger.LogWarning("Password change contention {Code}", "PasswordChangeVersionConflict");
        return new PasswordChangeResult(PasswordChangeDecision.VersionConflict);
    }
}
