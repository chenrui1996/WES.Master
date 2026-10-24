using System.Collections.Immutable;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Wes.Platform.Domain;
using Wes.Platform.Features.Authorization;
using Wes.Platform.Features.Directory;
using Wes.Platform.Features.Tenancy;
using Wes.Platform.Infrastructure.Persistence;

namespace Wes.Platform.Features.Identity;

/// <summary>Bounded current owner query, with [from,to) creation times and an explicit state vocabulary.</summary>
public sealed record SessionQuery(string? State = null, DateTimeOffset? From = null, DateTimeOffset? To = null,
    int Offset = 0, int Limit = 50, Guid? UserId = null);
/// <summary>Administrative family metadata projected independently for each resource. No token or digest exists in this contract.</summary>
public sealed record AdministrativeSessionRow(Guid Id, Guid? UserId, int? Client, DateTimeOffset? CreatedAt,
    DateTimeOffset? LastActivityAt, DateTimeOffset? ExpiresAt, DateTimeOffset? RevokedAt, bool? RequiresPasswordChange,
    string? Version, bool Current, int? State);

/// <summary>Queries current durable families with SQL scope filtering before pagination and per-row field projection.</summary>
public sealed class QuerySessionFamilies(PlatformDbContext database, TimeProvider clock, ILogger<QuerySessionFamilies> logger)
{
    /// <summary>Self service does not require a role grant and never accepts another subject's identifier.</summary>
    public async Task<IReadOnlyList<IdentitySessionView>> OwnAsync(TenantContext subject, SessionQuery filter, CancellationToken ct)
    {
        Validate(filter);
        if (filter.UserId is not null) throw new ArgumentException("Self-session queries do not accept a target subject.");
        var rows = await Apply(database.Sessions.AsNoTracking().Where(item => item.UserId == subject.ActorId), filter)
            .OrderByDescending(item => item.CreatedAt).ThenBy(item => item.Id).Skip(filter.Offset).Take(filter.Limit).ToListAsync(ct);
        return rows.Select(item => new IdentitySessionView(item.Id, (int)item.Client, item.CreatedAt, item.LastActivityAt,
            item.ExpiresAt, item.RevokedAt, item.RequiresPasswordChange, item.Version, item.Id == subject.SessionId,
            (int)item.StateAt(clock.GetUtcNow()))).ToArray();
    }
    /// <summary>Current Id/CreatedAt authority is mandatory; optional filters cannot inspect hidden state or user bindings.</summary>
    public async Task<DirectoryPage<AdministrativeSessionRow>> AdministrativeAsync(TenantContext subject, SessionQuery filter, CancellationToken ct)
    {
        Validate(filter);
        var grants = await new CurrentAuthorization(database, clock).ReadGrantsAsync(subject, ct);
        var required = new List<string> { "Id", "CreatedAt" };
        if (filter.State is not null) required.Add("State");
        if (filter.UserId is not null) required.Add("UserId");
        var query = database.Sessions.AsNoTracking();
        foreach (var field in required)
        {
            var matches = grants.Where(grant => grant.Permission == "platform.sessions.read" && grant.Fields.Contains(field)).ToArray();
            if (matches.Length == 0)
            { logger.LogWarning("Session query rejected {Code}", "SessionReadForbidden"); return new(false, []); }
            if (matches.Any(grant => grant.Scope == DataScope.All)) continue;
            var own = matches.Any(grant => grant.Scope == DataScope.Own);
            var departments = matches.Where(grant => grant.Scope == DataScope.Departments).SelectMany(grant => grant.Departments);
            var users = database.DirectoryUsers.AsNoTracking().Where(ScopeExpressions.MatchIds<DirectoryUser>(user => user.DepartmentId, departments));
            query = query.Where(item => (own && item.UserId == subject.ActorId) || users.Any(user => user.Id == item.UserId));
        }
        if (filter.UserId is not null) query = query.Where(item => item.UserId == filter.UserId.Value);
        var rows = await Apply(query, filter).OrderByDescending(item => item.CreatedAt).ThenBy(item => item.Id)
            .Skip(filter.Offset).Take(filter.Limit).ToListAsync(ct);
        var ids = rows.Select(item => item.UserId);
        var owners = await database.DirectoryUsers.AsNoTracking().Where(ScopeExpressions.MatchIds<DirectoryUser>(user => user.Id, ids))
            .ToDictionaryAsync(user => user.Id, user => user.DepartmentId, ct);
        return new(true, rows.Select(item =>
        {
            var fields = RolePolicy.VisibleFields(database.TenantId, subject.ActorId, "platform.sessions.read",
                new ResourceScope(database.TenantId, item.UserId, owners.GetValueOrDefault(item.UserId)), grants);
            return new AdministrativeSessionRow(item.Id, fields.Contains("UserId") ? item.UserId : null,
                fields.Contains("Client") ? (int)item.Client : null, fields.Contains("CreatedAt") ? item.CreatedAt : null,
                fields.Contains("LastActivityAt") ? item.LastActivityAt : null, fields.Contains("ExpiresAt") ? item.ExpiresAt : null,
                fields.Contains("RevokedAt") ? item.RevokedAt : null, fields.Contains("RequiresPasswordChange") ? item.RequiresPasswordChange : null,
                fields.Contains("Version") ? item.Version : null, item.Id == subject.SessionId,
                fields.Contains("State") ? (int)item.StateAt(clock.GetUtcNow()) : null);
        }).ToImmutableArray());
    }
    private IQueryable<Session> Apply(IQueryable<Session> query, SessionQuery filter)
    {
        if (filter.From is not null) query = query.Where(item => item.CreatedAt >= filter.From.Value);
        if (filter.To is not null) query = query.Where(item => item.CreatedAt < filter.To.Value);
        var absolute = clock.GetUtcNow().AddHours(-8);
        var web = clock.GetUtcNow().AddSeconds(-900);
        var pda = clock.GetUtcNow().AddSeconds(-300);
        return filter.State switch
        {
            "revoked" => query.Where(item => item.RevokedAt != null),
            "expired" => query.Where(item => item.RevokedAt == null && item.CreatedAt <= absolute),
            "idle" => query.Where(item => item.RevokedAt == null && item.CreatedAt > absolute
                && ((item.Client == SessionClient.Web && item.LastActivityAt <= web) || (item.Client == SessionClient.Pda && item.LastActivityAt <= pda))),
            "active" => query.Where(item => item.RevokedAt == null && item.CreatedAt > absolute
                && ((item.Client == SessionClient.Web && item.LastActivityAt > web) || (item.Client == SessionClient.Pda && item.LastActivityAt > pda))),
            _ => query
        };
    }
    private static void Validate(SessionQuery filter)
    {
        if (filter.Offset < 0 || filter.Limit is < 1 or > 100 || filter.From >= filter.To || filter.UserId == Guid.Empty
            || filter.State is not (null or "active" or "idle" or "expired" or "revoked"))
            throw new ArgumentException("Session query requires valid bounds, references and state.");
    }
}
