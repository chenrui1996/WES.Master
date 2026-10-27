using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Wes.Wms.Features.Inbound;
using Wes.Wms.Infrastructure.Persistence;

namespace Wes.Wms;

/// <summary>Independent current-authorized draft revision owner; cannot change identity, approve, receive or issue transport.</summary>
public sealed class WmsInboundRevisionWriter : IAsyncDisposable
{
    private readonly WmsDbContext _database;
    private readonly InboundRevisionAccess _access;

    /// <summary>Requires an explicit connection, tenant, live authority and clock; never applies migrations.</summary>
    public WmsInboundRevisionWriter(string connection, Guid tenant, IInboundRevisionAuthorization authorization,
        TimeProvider clock, ILoggerFactory logs)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connection);
        ArgumentNullException.ThrowIfNull(authorization);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(logs);
        _database = new(new DbContextOptionsBuilder<WmsDbContext>().UseMySQL(connection,
            mysql => mysql.MigrationsHistoryTable("__WesWmsMigrations")).Options, tenant);
        _access = new(_database, authorization, clock, logs.CreateLogger<InboundRevisionAccess>());
    }

    /// <summary>
    /// Replaces only a draft plan using the original version. Recover uncertain results with a fresh owner and the exact same tuple.
    /// A replay returns the original revision version; read current detail separately. Cancellation or lost response is not proof of rollback.
    /// </summary>
    public Task<InboundRevisionResult> ReviseAsync(InboundSubject subject, Guid id, InboundDraftRevision request,
        CancellationToken cancellationToken = default) => _access.ReviseAsync(subject, id, request, cancellationToken);

    /// <summary>Disposes the private owner without deleting retained history.</summary>
    public ValueTask DisposeAsync() => _database.DisposeAsync();
}
