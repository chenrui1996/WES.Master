using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Wes.Wms.Features.Inbound;
using Wes.Wms.Infrastructure.Persistence;

namespace Wes.Wms;

/// <summary>Independent current-authorized draft creation owner; cannot approve, receive, activate or issue transport.</summary>
public sealed class WmsInboundDraftWriter : IAsyncDisposable
{
    private readonly WmsDbContext _database;
    private readonly InboundDraftAccess _access;

    /// <summary>Requires explicit deployment connection, tenant, live authority and clock; never applies migrations.</summary>
    public WmsInboundDraftWriter(string connection, Guid tenant, IInboundDraftAuthorization authorization,
        TimeProvider clock, ILoggerFactory logs)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connection);
        ArgumentNullException.ThrowIfNull(authorization);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(logs);
        _database = new(new DbContextOptionsBuilder<WmsDbContext>().UseMySQL(connection,
            mysql => mysql.MigrationsHistoryTable("__WesWmsMigrations")).Options, tenant);
        _access = new(_database, authorization, clock, logs.CreateLogger<InboundDraftAccess>());
    }

    /// <summary>
    /// Creates or recovers the original actor/operation/plan under current complete-field and real parent authority.
    /// Use a fresh owner and the same operation after an uncertain response; never invent another operation automatically.
    /// Only the permanent document ID is returned; obtain its current version by separately authorized detail read.
    /// </summary>
    public Task<InboundDraftResult> CreateAsync(InboundSubject subject, InboundDraftCreation request,
        CancellationToken cancellationToken = default) => _access.CreateAsync(subject, request, cancellationToken);

    /// <summary>Disposes the private owner without deleting retained draft identities.</summary>
    public ValueTask DisposeAsync() => _database.DisposeAsync();
}
