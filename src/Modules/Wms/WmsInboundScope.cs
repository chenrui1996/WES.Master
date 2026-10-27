using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Wes.Wms.Features.Inbound;
using Wes.Wms.Infrastructure.Persistence;

namespace Wes.Wms;

/// <summary>Independent read-only inbound owner without exposed persistence or internal write primitives.</summary>
public sealed class WmsInboundScope : IAsyncDisposable
{
    private readonly WmsDbContext _database;
    private readonly InboundAccess _access;

    /// <summary>Requires an explicit connection, trusted tenant, live resource authority and safe owner logging.</summary>
    public WmsInboundScope(string connection, Guid tenant, IInboundAuthorization authorization, ILoggerFactory logs)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connection);
        ArgumentNullException.ThrowIfNull(authorization);
        ArgumentNullException.ThrowIfNull(logs);
        _database = new(new DbContextOptionsBuilder<WmsDbContext>().UseMySQL(connection,
            mysql => mysql.MigrationsHistoryTable("__WesWmsMigrations")).Options, tenant);
        _access = new(_database, authorization, logs.CreateLogger<InboundAccess>());
    }

    /// <summary>Reads one complete current-authorized document with coherent plan and receipt progress.</summary>
    public Task<InboundReadResult> ReadAsync(InboundSubject subject, Guid id, CancellationToken cancellationToken = default) =>
        _access.ReadAsync(subject, id, cancellationToken);

    /// <summary>Disposes the private owner without modifying retained facts.</summary>
    public ValueTask DisposeAsync() => _database.DisposeAsync();
}
