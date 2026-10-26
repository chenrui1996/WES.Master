using System.Collections.Immutable;
using System.Data;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Wes.Platform.Contracts;
using Wes.Platform.Domain;
using Wes.Platform.Features.Authorization;
using Wes.Platform.Features.Tenancy;
using Wes.Platform.Infrastructure.Persistence;

namespace Wes.Platform.Features.Audit;

/// <summary>Only authorized metadata columns are returned; absent field grants produce null, not raw entities.</summary>
public sealed record AuditRow(Guid Id, DateTimeOffset OccurredAt, Guid? ActorId, Guid? TargetId,
    Guid? CorrelationId, PlatformAuditAction? Action, string? BeforeVersion, string? AfterVersion);

/// <summary>An explicit denied outcome is distinct from an authorized empty interval.</summary>
public sealed record AuditPage(bool Authorized, ImmutableArray<AuditRow> Rows);

/// <summary>Owner-local fixed-membership audit snapshot result.</summary>
public sealed record AuditSnapshotPage(Guid SnapshotId, DateTimeOffset ExpiresAt, int Count, ImmutableArray<AuditRow> Rows);

/// <summary>Explicit outcomes for creating or continuing a fixed audit snapshot.</summary>
public enum AuditSnapshotDecision
{
    /// <summary>The snapshot page was created or read successfully.</summary>
    Success,
    /// <summary>The current session lacks the required audit permission.</summary>
    Forbidden,
    /// <summary>The snapshot does not exist in the trusted tenant and session scope.</summary>
    NotFound,
    /// <summary>The snapshot TTL has elapsed.</summary>
    Expired,
    /// <summary>The requested result exceeds the owner-local snapshot row limit.</summary>
    TooLarge
}

/// <summary>Snapshot decision and optional authorized page.</summary>
public sealed record AuditSnapshotResult(AuditSnapshotDecision Decision, AuditSnapshotPage? Page);

/// <summary>Owner-local current-authorized audit queries and explicitly audited fixed-snapshot export; never cross-owner access.</summary>
public sealed partial class QueryAudit(PlatformDbContext database, TimeProvider clock, ILogger<QueryAudit> logger)
{
    private const string StableSort = "OccurredAt ASC, Id ASC";

    /// <summary>Queries [from,to), stable instant/ID order, with a bounded page after tenant and data-scope filtering.</summary>
    public async Task<AuditPage> ReadAsync(TenantContext context, DateTimeOffset from, DateTimeOffset to,
        int offset = 0, int limit = 100, CancellationToken cancellationToken = default,
        PlatformAuditAction? filterAction = null, Guid? correlationId = null)
    {
        ValidateQuery(from, to, offset, limit, filterAction, correlationId);
        return await ReadCoreAsync(context, from, to, offset, limit, cancellationToken, filterAction, correlationId);
    }

    /// <summary>Captures immutable audit membership in one repeatable-read transaction for a 600-second verification window.</summary>
    public async Task<AuditSnapshotResult> CreateSnapshotAsync(
        TenantContext context,
        DateTimeOffset from,
        DateTimeOffset to,
        int limit = 25,
        CancellationToken cancellationToken = default,
        PlatformAuditAction? filterAction = null,
        Guid? correlationId = null)
    {
        ValidateQuery(from, to, 0, limit, filterAction, correlationId);
        await using var transaction = await database.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken);
        var firstPage = await ReadCoreAsync(context, from, to, 0, limit, cancellationToken, filterAction, correlationId);
        if (!firstPage.Authorized) return new(AuditSnapshotDecision.Forbidden, null);
        var membership = await ReadAuthorizedIdsAsync(context, from, to, filterAction, correlationId, cancellationToken);
        if (!membership.Authorized) return new(AuditSnapshotDecision.Forbidden, null);
        if (membership.Ids.Count > PlatformAuditQuerySnapshot.MaximumRows)
            return new(AuditSnapshotDecision.TooLarge, null);

        var grants = await new CurrentAuthorization(database, clock).ReadGrantsAsync(context, cancellationToken);
        var scope = CreateScope(context, grants, from, to, filterAction, correlationId);
        var createdAt = clock.GetUtcNow();
        var snapshot = new PlatformAuditQuerySnapshot(context.TenantId, context.ActorId, context.SessionId,
            scope.AuthorizationFingerprint, scope.FilterFingerprint, scope.SortFingerprint,
            from, to, filterAction, correlationId, membership.Ids, createdAt);

        var expired = await database.AuditQuerySnapshots
            .Where(item => item.ExpiresAt <= createdAt)
            .OrderBy(item => item.ExpiresAt).Take(100).ToListAsync(cancellationToken);
        database.AuditQuerySnapshots.RemoveRange(expired);
        database.AuditQuerySnapshots.Add(snapshot);
        await database.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new(AuditSnapshotDecision.Success,
            new(snapshot.Id, snapshot.ExpiresAt, snapshot.Count, firstPage.Rows));
    }

    /// <summary>Reads only captured members after revalidating session, current grants, query fingerprints and expiry.</summary>
    public async Task<AuditSnapshotResult> ReadSnapshotAsync(
        TenantContext context,
        Guid snapshotId,
        int offset,
        int limit,
        CancellationToken cancellationToken = default)
    {
        if (snapshotId == Guid.Empty) throw new ArgumentException("Snapshot identity is required.", nameof(snapshotId));
        if (offset < 0) throw new ArgumentOutOfRangeException(nameof(offset));
        if (limit is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(limit));
        var snapshot = await database.AuditQuerySnapshots.SingleOrDefaultAsync(
            item => item.Id == snapshotId, cancellationToken);
        if (snapshot is null || snapshot.ActorId != context.ActorId || snapshot.SessionId != context.SessionId)
            return new(AuditSnapshotDecision.NotFound, null);
        if (clock.GetUtcNow() >= snapshot.ExpiresAt) return new(AuditSnapshotDecision.Expired, null);

        var grants = await new CurrentAuthorization(database, clock).ReadGrantsAsync(context, cancellationToken);
        var currentScope = CreateScope(context, grants, snapshot.From, snapshot.To, snapshot.FilterAction, snapshot.CorrelationId);
        if (currentScope.AuthorizationFingerprint != snapshot.AuthorizationFingerprint
            || currentScope.FilterFingerprint != snapshot.FilterFingerprint
            || currentScope.SortFingerprint != snapshot.SortFingerprint)
        {
            logger.LogWarning("Audit snapshot rejected {Code}", "AuditSnapshotScopeChanged");
            return new(AuditSnapshotDecision.Forbidden, null);
        }

        var orderedIds = snapshot.ReadOrderedAuditIds();
        var pageIds = orderedIds.Skip(offset).Take(limit).ToArray();
        var page = await ReadCoreAsync(context, snapshot.From, snapshot.To, 0, Math.Max(pageIds.Length, 1), cancellationToken,
            snapshot.FilterAction, snapshot.CorrelationId, pageIds);
        if (!page.Authorized) return new(AuditSnapshotDecision.Forbidden, null);
        if (page.Rows.Length != pageIds.Length)
            throw new InvalidOperationException("Captured audit membership no longer resolves to immutable owner facts.");
        return new(AuditSnapshotDecision.Success,
            new(snapshot.Id, snapshot.ExpiresAt, snapshot.Count, page.Rows));
    }

    private async Task<AuditPage> ReadCoreAsync(TenantContext context, DateTimeOffset from, DateTimeOffset to,
        int offset, int limit, CancellationToken cancellationToken, PlatformAuditAction? filterAction,
        Guid? correlationId, IReadOnlyCollection<Guid>? fixedIds = null)
    {
        if (from >= to) throw new ArgumentException("Audit queries require an increasing time interval.");
        if (offset < 0) throw new ArgumentOutOfRangeException(nameof(offset));
        if (limit is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(limit));
        if (filterAction is not null && !Enum.IsDefined(filterAction.Value)) throw new ArgumentOutOfRangeException(nameof(filterAction));
        if (correlationId == Guid.Empty) throw new ArgumentException("Audit correlation must be a nonempty UUID.");
        var grants = await new CurrentAuthorization(database, clock).ReadGrantsAsync(context, cancellationToken);
        var relevant = grants.Where(grant => grant.Permission == "platform.audit.read").ToArray();
        var allFields = relevant.Where(grant => grant.Scope == DataScope.All).SelectMany(grant => grant.Fields)
            .ToImmutableHashSet(StringComparer.Ordinal);
        var ownFields = relevant.Where(grant => grant.Scope is DataScope.All or DataScope.Own).SelectMany(grant => grant.Fields)
            .ToImmutableHashSet(StringComparer.Ordinal);
        var required = new List<string> { "Id", "OccurredAt" };
        if (filterAction is not null) required.Add("Action");
        if (correlationId is not null) required.Add("CorrelationId");
        var all = required.All(allFields.Contains);
        var own = required.All(ownFields.Contains);
        if (!all && !own)
        {
            logger.LogWarning("Audit query rejected {Code}", "AuditReadForbidden");
            return new AuditPage(false, []);
        }

        // Audit has no authoritative department metadata; department grants cannot be widened to All.
        (bool All, bool Own) Access(string field) => (allFields.Contains(field), ownFields.Contains(field));
        var actor = Access("ActorId");
        var target = Access("TargetId");
        var correlation = Access("CorrelationId");
        var action = Access("Action");
        var before = Access("BeforeVersion");
        var after = Access("AfterVersion");
        var query = database.Audit.AsNoTracking()
            .Where(item => item.OccurredAt >= from && item.OccurredAt < to
                && (all || (own && item.ActorId == context.ActorId)));
        if (filterAction is not null) query = query.Where(item => item.Action == filterAction.Value);
        if (correlationId is not null) query = query.Where(item => item.CorrelationId == correlationId.Value);
        if (fixedIds is not null) query = query.Where(ScopeExpressions.MatchIds<PlatformAudit>(item => item.Id, fixedIds));
        IQueryable<PlatformAudit> orderedQuery = query.OrderBy(item => item.OccurredAt).ThenBy(item => item.Id);
        if (fixedIds is null) orderedQuery = orderedQuery.Skip(offset);
        var rows = await orderedQuery.Take(limit)
            .Select(item => new AuditRow(item.Id, item.OccurredAt,
                actor.All || (actor.Own && item.ActorId == context.ActorId) ? item.ActorId : null,
                target.All || (target.Own && item.ActorId == context.ActorId) ? item.TargetId : null,
                correlation.All || (correlation.Own && item.ActorId == context.ActorId) ? item.CorrelationId : null,
                action.All || (action.Own && item.ActorId == context.ActorId) ? item.Action : null,
                before.All || (before.Own && item.ActorId == context.ActorId) ? item.BeforeVersion : null,
                after.All || (after.Own && item.ActorId == context.ActorId) ? item.AfterVersion : null))
            .ToListAsync(cancellationToken);
        return new AuditPage(true, rows.ToImmutableArray());
    }

    private async Task<(bool Authorized, List<Guid> Ids)> ReadAuthorizedIdsAsync(TenantContext context,
        DateTimeOffset from, DateTimeOffset to, PlatformAuditAction? filterAction, Guid? correlationId,
        CancellationToken cancellationToken)
    {
        var grants = await new CurrentAuthorization(database, clock).ReadGrantsAsync(context, cancellationToken);
        var relevant = grants.Where(grant => grant.Permission == "platform.audit.read").ToArray();
        var allFields = relevant.Where(grant => grant.Scope == DataScope.All).SelectMany(grant => grant.Fields)
            .ToImmutableHashSet(StringComparer.Ordinal);
        var ownFields = relevant.Where(grant => grant.Scope is DataScope.All or DataScope.Own).SelectMany(grant => grant.Fields)
            .ToImmutableHashSet(StringComparer.Ordinal);
        var required = new List<string> { "Id", "OccurredAt" };
        if (filterAction is not null) required.Add("Action");
        if (correlationId is not null) required.Add("CorrelationId");
        var all = required.All(allFields.Contains);
        var own = required.All(ownFields.Contains);
        if (!all && !own) return (false, []);

        var query = database.Audit.AsNoTracking()
            .Where(item => item.OccurredAt >= from && item.OccurredAt < to
                && (all || (own && item.ActorId == context.ActorId)));
        if (filterAction is not null) query = query.Where(item => item.Action == filterAction.Value);
        if (correlationId is not null) query = query.Where(item => item.CorrelationId == correlationId.Value);
        var ids = await query.OrderBy(item => item.OccurredAt).ThenBy(item => item.Id)
            .Take(PlatformAuditQuerySnapshot.MaximumRows + 1)
            .Select(item => item.Id).ToListAsync(cancellationToken);
        return (true, ids);
    }

    private static void ValidateQuery(DateTimeOffset from, DateTimeOffset to, int offset, int limit,
        PlatformAuditAction? filterAction, Guid? correlationId)
    {
        if (from >= to) throw new ArgumentException("Audit queries require an increasing time interval.");
        if (offset < 0) throw new ArgumentOutOfRangeException(nameof(offset));
        if (limit is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(limit));
        if (filterAction is not null && !Enum.IsDefined(filterAction.Value))
            throw new ArgumentOutOfRangeException(nameof(filterAction));
        if (correlationId == Guid.Empty) throw new ArgumentException("Audit correlation must be a nonempty UUID.");
    }

    private static QueryScope CreateScope(TenantContext context, ImmutableArray<PermissionGrant> grants,
        DateTimeOffset from, DateTimeOffset to, PlatformAuditAction? filterAction, Guid? correlationId,
        string permission = "platform.audit.read")
    {
        var authorization = Fingerprint(string.Join("\n", grants
            .Where(grant => grant.Permission == permission)
            .Select(grant => string.Join("|", grant.Permission, grant.Scope,
                string.Join(",", grant.Departments.Select(id => id.ToString("D")).Order(StringComparer.Ordinal)),
                string.Join(",", grant.Fields.Order(StringComparer.Ordinal))))
            .Order(StringComparer.Ordinal)));
        var filters = Fingerprint(string.Join("|",
            from.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            to.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            filterAction?.ToString() ?? "",
            correlationId?.ToString("D") ?? ""));
        return new QueryScope(context.TenantId, context.ActorId, context.SessionId,
            authorization, filters, Fingerprint(StableSort));
    }

    private static string Fingerprint(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
