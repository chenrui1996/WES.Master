using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Wes.Wms.Infrastructure.Messaging;
using Wes.Wms.Infrastructure.Persistence;
using Wes.Wms.Messaging;

namespace Wes.Wms;

/// <summary>Durable transitions only. Unconfirmed means an attempted result was not durably recorded; never implies business completion.</summary>
public sealed record WmsPublishBatchResult(int Published, int RetryScheduled, int Quarantined, int NotClaimed, int Unconfirmed);

/// <summary>Deployment-only independent WMS publisher; no migrations, automatic worker, default transport or cross-owner persistence access.</summary>
public sealed class WmsPublishingScope : IAsyncDisposable
{
    private readonly WmsDbContext _database;
    private readonly WmsOutboxPublisher _publisher;
    /// <summary>Requires an explicit tenant and approved adapter. Cancellation/transport errors propagate; provider failures are sanitized.</summary>
    public WmsPublishingScope(string connection, Guid tenant, IWmsOutboxTransport transport, TimeProvider clock, ILoggerFactory logs)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connection);
        ArgumentNullException.ThrowIfNull(transport); ArgumentNullException.ThrowIfNull(clock); ArgumentNullException.ThrowIfNull(logs);
        _database = new(new DbContextOptionsBuilder<WmsDbContext>().UseMySQL(connection,
            mysql => mysql.MigrationsHistoryTable("__WesWmsMigrations")).Options, tenant);
        _publisher = new(_database, transport, clock, Random.Shared.NextDouble, logs.CreateLogger<WmsOutboxPublisher>());
    }
    /// <summary>Sequential bounded due batch, 1..100; no automatic loop or external service is enabled by constructing this scope.</summary>
    public async Task<WmsPublishBatchResult> DispatchDueAsync(int limit, CancellationToken cancellationToken = default)
    {
        var results = await _publisher.DispatchDueAsync(limit, cancellationToken);
        return new(results.Count(item => item == WmsOutboxPublishResult.Published),
            results.Count(item => item == WmsOutboxPublishResult.RetryScheduled),
            results.Count(item => item == WmsOutboxPublishResult.Quarantined),
            results.Count(item => item == WmsOutboxPublishResult.NotClaimed),
            results.Count(item => item == WmsOutboxPublishResult.Unconfirmed));
    }
    /// <summary>Disposes the independent owner; no lease is acknowledged by disposal.</summary>
    public ValueTask DisposeAsync() => _database.DisposeAsync();
}
