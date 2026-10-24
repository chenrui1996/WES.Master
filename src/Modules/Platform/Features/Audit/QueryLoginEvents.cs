using System.Collections.Immutable;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Wes.Platform.Features.Authorization;
using Wes.Platform.Features.Identity;
using Wes.Platform.Features.Tenancy;
using Wes.Platform.Infrastructure.Persistence;

namespace Wes.Platform.Features.Audit;

/// <summary>Authorized security metadata only; omitted fields are null, not hidden entity properties.</summary>
public sealed record LoginEventRow(Guid Id, DateTimeOffset OccurredAt, LoginEventKind? Kind, Guid? CorrelationId,
    string? AccountDigest, string? SourceDigest, Guid? SessionId)
{
    /// <summary>Pseudonymous bindings must not leak through incidental diagnostic formatting.</summary>
    public override string ToString() => "LoginEventRow[REDACTED]";
}

/// <summary>Distinguishes a denied query from an authorized empty interval.</summary>
public sealed record LoginEventPage(bool Authorized, ImmutableArray<LoginEventRow> Rows);

/// <summary>Owner-local current-field-authorized security-event read; not an export or public transport.</summary>
public sealed class QueryLoginEvents(PlatformDbContext database, TimeProvider clock, ILogger<QueryLoginEvents> logger)
{
    /// <summary>Reads [from,to) in instant/ID order, with authorized optional filters before bounded pagination.</summary>
    public async Task<LoginEventPage> ReadAsync(TenantContext context, DateTimeOffset from, DateTimeOffset to,
        int offset = 0, int limit = 100, LoginEventKind? kind = null, Guid? correlationId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();
        if (from >= to) throw new ArgumentException("Login event queries require an increasing time interval.");
        if (offset < 0) throw new ArgumentOutOfRangeException(nameof(offset));
        if (limit is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(limit));
        if (kind is not null && !Enum.IsDefined(kind.Value)) throw new ArgumentOutOfRangeException(nameof(kind));
        if (correlationId == Guid.Empty) throw new ArgumentException("A supplied correlation must be nonempty.", nameof(correlationId));
        if (database.ChangeTracker.Entries().Any() || database.Database.CurrentTransaction is not null
            || System.Transactions.Transaction.Current is not null)
            throw new InvalidOperationException("Login event queries require a clean independent unit of work.");
        var fields = await new CurrentAuthorization(database, clock).VisibleFieldsAsync(context, "platform.audit.read",
            new ResourceScope(database.TenantId, Guid.Empty, Guid.Empty), cancellationToken);
        // Failed/unknown login facts have no authoritative immutable subject or department; never guess Own from a current digest mapping.
        if (!fields.Contains("Id") || !fields.Contains("OccurredAt")
            || (kind is not null && !fields.Contains("Kind"))
            || (correlationId is not null && !fields.Contains("CorrelationId")))
        {
            logger.LogWarning("Login event query rejected {Code}", "LoginEventReadForbidden");
            return new LoginEventPage(false, []);
        }
        var showKind = fields.Contains("Kind");
        var showCorrelation = fields.Contains("CorrelationId");
        var showAccount = fields.Contains("AccountDigest");
        var showSource = fields.Contains("SourceDigest");
        var showSession = fields.Contains("SessionId");
        var query = database.LoginEvents.AsNoTracking().Where(item => item.OccurredAt >= from && item.OccurredAt < to);
        if (kind is not null) query = query.Where(item => item.Kind == kind.Value);
        if (correlationId is not null) query = query.Where(item => item.CorrelationId == correlationId.Value);
        var rows = await query.OrderBy(item => item.OccurredAt).ThenBy(item => item.Id).Skip(offset).Take(limit)
            .Select(item => new LoginEventRow(item.Id, item.OccurredAt,
                showKind ? item.Kind : null, showCorrelation ? item.CorrelationId : null,
                showAccount ? item.AccountDigest : null, showSource ? item.SourceDigest : null,
                showSession ? item.SessionId : null))
            .ToListAsync(cancellationToken);
        return new LoginEventPage(true, rows.ToImmutableArray());
    }
}
