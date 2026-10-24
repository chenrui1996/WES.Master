using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Wes.Platform.Domain;
using Wes.Platform.Features.Authorization;
using Wes.Platform.Features.Tenancy;
using Wes.Platform.Infrastructure.Persistence;

namespace Wes.Platform.Features.Identity;

/// <summary>Explicit scoped-family revocation outcomes, never a synthetic replacement session.</summary>
public enum SessionRevokeDecision
{
    /// <summary>The selected authorized family is durably revoked.</summary>
    Completed,
    /// <summary>Current subject or target scope does not authorize revocation.</summary>
    Forbidden,
    /// <summary>The family changed; a fresh explicit submission is required.</summary>
    VersionConflict
}

/// <summary>Selected self-family revocation or current All-authorized administration with an atomic audit chain.</summary>
public sealed class RevokeSessionHandler(PlatformDbContext database, TimeProvider clock, ILogger<RevokeSessionHandler> logger)
{
    /// <summary>Revalidates the current caller and expected target version, never accepting client-supplied ownership metadata.</summary>
    public async Task<SessionRevokeDecision> HandleAsync(TenantContext context, Guid id, string expectedVersion,
        Guid correlation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();
        if (context.TenantId != database.TenantId || context.ActorId == Guid.Empty || context.SessionId == Guid.Empty)
            throw new InvalidOperationException("Session revocation requires a trusted owner context.");
        if (id == Guid.Empty || correlation == Guid.Empty || !Guid.TryParseExact(expectedVersion, "D", out var version) || version == Guid.Empty)
            throw new ArgumentException("Session revocation requires valid references and expected version.");
        if (database.ChangeTracker.Entries().Any() || database.Database.CurrentTransaction is not null
            || System.Transactions.Transaction.Current is not null || database.Database.AutoTransactionBehavior == AutoTransactionBehavior.Never)
            throw new InvalidOperationException("Session revocation requires a clean independent unit of work.");
        try
        {
            var now = clock.GetUtcNow();
            now = new DateTimeOffset(now.UtcTicks - now.UtcTicks % 10, TimeSpan.Zero);
            var current = await database.Sessions.AsNoTracking().SingleOrDefaultAsync(
                item => item.Id == context.SessionId && item.UserId == context.ActorId, cancellationToken);
            if (current is null || current.StateAt(now) != SessionState.Active) return Reject(SessionRevokeDecision.Forbidden);
            var target = await database.Sessions.SingleOrDefaultAsync(item => item.Id == id, cancellationToken);
            if (target is null) return Reject(SessionRevokeDecision.Forbidden);
            if (target.UserId != context.ActorId)
            {
                var department = await database.DirectoryUsers.AsNoTracking().Where(item => item.Id == target.UserId)
                    .Select(item => (Guid?)item.DepartmentId).SingleOrDefaultAsync(cancellationToken) ?? Guid.Empty;
                var fields = await new CurrentAuthorization(database, clock).VisibleFieldsAsync(context, "platform.sessions.revoke",
                    new ResourceScope(database.TenantId, target.UserId, department), cancellationToken);
                if (!fields.Contains("RevokedAt")) return Reject(SessionRevokeDecision.Forbidden);
            }
            if (target.StateAt(now) == SessionState.Revoked) return SessionRevokeDecision.Completed;
            if (target.Version != expectedVersion) return Reject(SessionRevokeDecision.VersionConflict);
            var before = target.Version;
            target.Revoke(now);
            database.Audit.Add(new PlatformAudit(database.TenantId, context.ActorId, target.Id, correlation,
                PlatformAuditAction.SessionRevoked, before, target.Version, now));
            try
            {
                await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);
                await database.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return SessionRevokeDecision.Completed;
            }
            catch (DbUpdateConcurrencyException) { return Reject(SessionRevokeDecision.VersionConflict); }
        }
        finally { database.ChangeTracker.Clear(); }
    }
    private SessionRevokeDecision Reject(SessionRevokeDecision decision)
    {
        logger.LogWarning("Session revocation rejected {Code}", decision);
        return decision;
    }
}
