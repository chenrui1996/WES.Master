using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Wes.Wms.Features.Ledger;
using Wes.Wms.Features.MasterData;
using Wes.Wms.Infrastructure.Persistence;

namespace Wes.Wms;

/// <summary>Current-authorized disabled creation scope; use a fresh scope per durable command or explicit operation replay.</summary>
public sealed class WmsMasterDataWriter : IAsyncDisposable
{
    private static readonly string[] WarehouseFields = ["Code", "Name"];
    private static readonly string[] LocationFields = [.. WarehouseFields, "WarehouseId", "PackId", "SchemaVersion"];
    private readonly WmsDbContext _database;
    private readonly IMasterDataAuthorization _authorization;
    private readonly CreateMasterData _create;
    private readonly MasterDataAccess _read;
    private readonly ILogger<WmsMasterDataWriter> _logger;

    /// <summary>Requires explicit live authority, schema, clock and deployment connection; does not migrate or provide successful defaults.</summary>
    public WmsMasterDataWriter(string connection, Guid tenant, IMasterDataAuthorization authorization,
        IWarehouseLocationSchema schema, TimeProvider clock, ILoggerFactory logs)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connection);
        ArgumentNullException.ThrowIfNull(authorization);
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(logs);
        _database = new WmsDbContext(new DbContextOptionsBuilder<WmsDbContext>().UseMySQL(connection,
            mysql => mysql.MigrationsHistoryTable("__WesWmsMigrations")).Options, tenant);
        _authorization = authorization;
        _create = new(_database, clock, schema);
        _read = new(_database, authorization, logs.CreateLogger<MasterDataAccess>());
        _logger = logs.CreateLogger<WmsMasterDataWriter>();
    }

    /// <summary>
    /// Authorizes the trusted actor as the new creator before any durable replay or write.
    /// Locations additionally require current parent read authority and explicit schema evidence; cancellation can leave
    /// an uncertain committed outcome requiring a fresh scope and the same operation, never blind automatic retry.
    /// </summary>
    public async Task<MasterDataWriteResult> CreateAsync(MasterDataSubject subject, MasterDataCreation request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(subject);
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        if (_database.ChangeTracker.Entries().Any() || _database.Database.CurrentTransaction is not null)
            throw new InvalidOperationException("Use a fresh WMS writer for each creation command or operation replay.");
        if (subject.TenantId != _database.TenantId || subject.ActorId == Guid.Empty || subject.SessionId == Guid.Empty)
            return Rejected(MasterDataWriteDecision.Forbidden);
        var command = new CreateMasterDataCommand(request.OperationId, subject.ActorId, request.CorrelationId,
            request.Code, request.Name, request.WarehouseId, request.PackId, request.SchemaVersion);
        if (!CreateMasterData.IsValid(command)) return Rejected(MasterDataWriteDecision.Invalid);
        try
        {
            var location = request.WarehouseId.HasValue;
            var fields = await _authorization.ReadFieldsAsync(subject,
                location ? "wms.locations.write" : "wms.warehouses.write", subject.ActorId, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (fields is null) throw new InvalidOperationException("Master-data creation authorization returned no decision.");
            var exact = fields.ToHashSet(StringComparer.Ordinal);
            if (!(location ? LocationFields : WarehouseFields).All(exact.Contains))
                return Rejected(MasterDataWriteDecision.Forbidden);
            if (location)
            {
                var parent = await _read.ReadWarehouseAsync(subject, request.WarehouseId!.Value, cancellationToken);
                if (parent.Decision != MasterDataReadDecision.Success) return Rejected(parent.Decision switch
                {
                    MasterDataReadDecision.Forbidden => MasterDataWriteDecision.Forbidden,
                    MasterDataReadDecision.NotFound => MasterDataWriteDecision.NotFound,
                    MasterDataReadDecision.Invalid => MasterDataWriteDecision.Invalid,
                    _ => throw new InvalidOperationException("Unknown parent warehouse read decision.")
                });
            }
            var result = await _create.ExecuteAsync(command, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            return result.Decision switch
            {
                MasterDataDecision.Created => new(MasterDataWriteDecision.Created, result.Id, result.Version),
                MasterDataDecision.Replayed => new(MasterDataWriteDecision.Replayed, result.Id, result.Version),
                MasterDataDecision.Invalid => Rejected(MasterDataWriteDecision.Invalid),
                MasterDataDecision.NotFound => Rejected(MasterDataWriteDecision.NotFound),
                MasterDataDecision.CodeConflict => Rejected(MasterDataWriteDecision.CodeConflict),
                MasterDataDecision.IdempotencyConflict => Rejected(MasterDataWriteDecision.IdempotencyConflict),
                MasterDataDecision.SchemaRejected => Rejected(MasterDataWriteDecision.SchemaRejected),
                MasterDataDecision.SchemaUnavailable => Rejected(MasterDataWriteDecision.SchemaUnavailable),
                _ => throw new InvalidOperationException("Unknown master-data creation decision.")
            };
        }
        catch (DbException error)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _logger.LogError("WMS authorized creation failed {Code}", "DatabaseUnavailable");
            throw WmsDatabaseFailures.Safe(error);
        }
        catch (WmsDatabaseException)
        {
            _logger.LogError("WMS authorized creation failed {Code}", "DatabaseUnavailable");
            throw;
        }
    }

    private MasterDataWriteResult Rejected(MasterDataWriteDecision decision)
    {
        _logger.LogWarning("WMS authorized creation rejected {Code}", decision.ToString());
        return new(decision);
    }

    /// <summary>Disposes the private owner; never deletes an identity, operation or permanent creation fact.</summary>
    public ValueTask DisposeAsync() => _database.DisposeAsync();
}
