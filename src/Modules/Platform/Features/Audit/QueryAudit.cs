using System.Collections.Immutable;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
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

/// <summary>Owner-local current-authorization query; this is not an export or cross-owner audit API.</summary>
public sealed class QueryAudit(PlatformDbContext database, TimeProvider clock, ILogger<QueryAudit> logger)
{
    /// <summary>Queries [from,to), stable instant/ID order, with a bounded page after tenant and data-scope filtering.</summary>
    public async Task<AuditPage> ReadAsync(TenantContext context, DateTimeOffset from, DateTimeOffset to,
        int offset = 0, int limit = 100, CancellationToken cancellationToken = default,
        PlatformAuditAction? filterAction = null, Guid? correlationId = null)
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
        var rows = await query.OrderBy(item => item.OccurredAt).ThenBy(item => item.Id).Skip(offset).Take(limit)
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
}
