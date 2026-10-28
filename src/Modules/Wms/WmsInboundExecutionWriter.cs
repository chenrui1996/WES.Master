using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Wes.Wms.Features.Inbound;
using Wes.Wms.Features.MasterData;
using Wes.Wms.Infrastructure.Persistence;

namespace Wes.Wms;

/// <summary>Independent current-authority inbound execution owner; never migrates or grants physical-control authority.</summary>
public sealed class WmsInboundExecutionWriter : IAsyncDisposable
{
    private readonly WmsDbContext _database;
    private readonly InboundExecutionAccess _access;

    /// <summary>Requires an explicit tenant, current resource authority and schema adapter; no default legal resources are inferred.</summary>
    public WmsInboundExecutionWriter(string connection, Guid tenant, IMasterDataAuthorization authority, IWarehouseLocationSchema schema,
        TimeProvider clock, ILoggerFactory logs)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connection); ArgumentNullException.ThrowIfNull(authority);
        ArgumentNullException.ThrowIfNull(schema); ArgumentNullException.ThrowIfNull(clock); ArgumentNullException.ThrowIfNull(logs);
        _database = new(new DbContextOptionsBuilder<WmsDbContext>().UseMySQL(connection,
            mysql => mysql.MigrationsHistoryTable("__WesWmsMigrations")).Options, tenant);
        _access = new(_database, authority, schema, clock, logs.CreateLogger<InboundExecutionAccess>());
    }

    /// <summary>Approves a legal draft or recovers its exact original operation under current complete resource authority.</summary>
    public Task<InboundExecutionResult> ApproveAsync(MasterDataSubject subject, Guid id, InboundApproval request, CancellationToken ct = default) =>
        _access.ApproveAsync(subject, id, request, ct);
    /// <summary>Atomically receives into a legal protected dimension; a different target remains pending transport, never arrived by inference.</summary>
    public Task<InboundExecutionResult> ReceiveAsync(MasterDataSubject subject, Guid id, InboundReceiptRegistration request, CancellationToken ct = default) =>
        _access.ReceiveAsync(subject, id, request, ct);
    /// <summary>Disposes the private owner; committed source facts and request receipts remain durable.</summary>
    public ValueTask DisposeAsync() => _database.DisposeAsync();
}
