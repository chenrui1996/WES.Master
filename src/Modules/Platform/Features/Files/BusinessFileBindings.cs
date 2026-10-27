using System.Data;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using MySql.Data.MySqlClient;
using Wes.Platform.Domain;
using Wes.Platform.Features.Tenancy;
using Wes.Platform.Infrastructure.Persistence;

namespace Wes.Platform.Features.Files;

/// <summary>Finite business reference transitions; neither action scans or deletes physical content.</summary>
public enum FileBindingAction
{
    /// <summary>Adds one exact business reference.</summary>
    Attach,
    /// <summary>Removes one exact business reference.</summary>
    Detach
}

/// <summary>Current target write and persisted source read authority; read alone cannot authorize a reference mutation.</summary>
public interface IBusinessFileBindingAuthorization : IBusinessFileAuthorization
{
    /// <summary>Checks current entity read and the exact Attach/Detach write action; errors/cancellation propagate.</summary>
    Task<bool> CanChangeAsync(TenantContext context, FileEntityReference entity, FileBindingAction action, CancellationToken cancellationToken);
}

/// <summary>Original committed operation receipt only, not a current file projection or membership guarantee.</summary>
public sealed record FileBindingResult(FileAccessDecision Decision, Guid? Id = null, string? Version = null,
    bool? Attached = null, bool Replayed = false);

/// <summary>Atomically changes exact references, file count/version, audit and a permanent actor-bound operation receipt.</summary>
public sealed class BusinessFileBindings
{
    private readonly PlatformDbContext _database;
    private readonly FileEntityReference _entity;
    private readonly IBusinessFileBindingAuthorization _authorization;
    private readonly TimeProvider _clock;
    private readonly ILogger<BusinessFileBindings> _logger;

    internal BusinessFileBindings(PlatformDbContext database, FileEntityReference entity,
        IBusinessFileBindingAuthorization authorization, TimeProvider clock, ILoggerFactory logs)
    {
        ArgumentNullException.ThrowIfNull(entity);
        ArgumentNullException.ThrowIfNull(authorization);
        _database = database; _entity = entity; _authorization = authorization; _clock = clock;
        _logger = logs.CreateLogger<BusinessFileBindings>();
    }

    /// <summary>Binds available stored bytes under current dual-owner Attach authority and the exact file version.</summary>
    public Task<FileBindingResult> BindAsync(TenantContext context, Guid file, Guid operation, Guid correlation,
        string? expectedVersion, CancellationToken ct = default) =>
        ExecuteAsync(context, file, operation, correlation, expectedVersion, attached: true, ct);

    /// <summary>Unbinds an exact reference under current dual-owner Detach authority; content is not deleted.</summary>
    public Task<FileBindingResult> UnbindAsync(TenantContext context, Guid file, Guid operation, Guid correlation,
        string? expectedVersion, CancellationToken ct = default) =>
        ExecuteAsync(context, file, operation, correlation, expectedVersion, attached: false, ct);

    private async Task<FileBindingResult> ExecuteAsync(TenantContext context, Guid file, Guid operation, Guid correlation,
        string? expectedVersion, bool attached, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        ct.ThrowIfCancellationRequested();
        if (_database.ChangeTracker.Entries().Any() || _database.Database.CurrentTransaction is not null)
            throw new InvalidOperationException("File binding requires a clean Platform owner.");
        if (file == Guid.Empty || operation == Guid.Empty || correlation == Guid.Empty) return Reject(FileAccessDecision.Invalid);
        try
        {
            try { return await ChangeAsync(context, file, operation, correlation, expectedVersion, attached, ct); }
            catch (DbUpdateConcurrencyException)
            {
                ct.ThrowIfCancellationRequested();
                _database.ChangeTracker.Clear();
                return await ResolveConflictAsync(context, file, operation, expectedVersion, attached, ct);
            }
            catch (DbUpdateException error) when (error.InnerException is MySqlException { Number: 1062 })
            {
                ct.ThrowIfCancellationRequested();
                _database.ChangeTracker.Clear();
                return await ResolveConflictAsync(context, file, operation, expectedVersion, attached, ct);
            }
        }
        catch (DbUpdateException error) when (error.InnerException is DbException provider)
        {
            ct.ThrowIfCancellationRequested();
            throw Failure(provider);
        }
        catch (DbException error)
        {
            ct.ThrowIfCancellationRequested();
            throw Failure(error);
        }
        finally { _database.ChangeTracker.Clear(); }
    }

    private async Task<FileBindingResult> ChangeAsync(TenantContext context, Guid id, Guid operation, Guid correlation,
        string? expectedVersion, bool attached, CancellationToken ct)
    {
        await using var transaction = await _database.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        var file = await _database.Files.AsNoTracking().SingleOrDefaultAsync(item => item.Id == id, ct);
        if (file is null) return Reject(FileAccessDecision.NotFound);
        if (!await AllowedAsync(context, file, attached, ct)) return Reject(FileAccessDecision.Forbidden);
        if (string.IsNullOrEmpty(expectedVersion)) return Reject(FileAccessDecision.VersionRequired);
        if (!Guid.TryParseExact(expectedVersion, "D", out var version) || version == Guid.Empty)
            return Reject(FileAccessDecision.Invalid);
        var original = await FindOperationAsync(operation, ct);
        if (original is not null) return Replay(original, context, id, expectedVersion, attached);
        if (file.Version != expectedVersion) return Reject(FileAccessDecision.VersionConflict);
        if (file.State != QuarantinedFileState.Available || !file.Stored) return Reject(FileAccessDecision.StateConflict);
        if (!await AllowedAsync(context, file, attached, ct)) return Reject(FileAccessDecision.Forbidden);
        if (!await _database.LockFileForBindingAsync(id, ct)) return Reject(FileAccessDecision.NotFound);
        file = await _database.Files.AsNoTracking().SingleOrDefaultAsync(item => item.Id == id, ct);
        if (file is null) return Reject(FileAccessDecision.NotFound);
        if (!await AllowedAsync(context, file, attached, ct)) return Reject(FileAccessDecision.Forbidden);
        original = await FindOperationAsync(operation, ct);
        if (original is not null) return Replay(original, context, id, expectedVersion, attached);
        if (file.Version != expectedVersion) return Reject(FileAccessDecision.VersionConflict);
        if (file.State != QuarantinedFileState.Available || !file.Stored) return Reject(FileAccessDecision.StateConflict);
        var references = await _database.FileReferences.AsNoTracking().Where(item => item.FileId == id).ToArrayAsync(ct);
        if (references.Length != file.ReferenceCount)
        {
            _logger.LogWarning("File binding rejected inconsistent reference evidence.");
            throw new FileAccessException();
        }
        var reference = references.SingleOrDefault(item => item.Owner == _entity.Owner && item.EntityId == _entity.EntityId);
        if (attached == (reference is not null)) return Reject(FileAccessDecision.StateConflict);
        if (attached && references.Length > 0)
        {
            foreach (var source in references)
                if (!await _authorization.CanReadAsync(context, new(source.Owner, source.EntityId), ct))
                    return Reject(FileAccessDecision.Forbidden);
            if (!await AllowedAsync(context, file, attached, ct)) return Reject(FileAccessDecision.Forbidden);
        }
        _database.Files.Attach(file);
        var before = file.Version;
        if (attached)
        {
            file.AddReference(_clock.GetUtcNow());
            _database.FileReferences.Add(new(_database.TenantId, id, _entity.Owner, _entity.EntityId));
        }
        else if (reference is not null)
        {
            file.RemoveReference(_clock.GetUtcNow());
            _database.FileReferences.Remove(reference);
        }
        else throw new InvalidOperationException("Validated attachment reference disappeared from the owner snapshot.");
        var audit = new PlatformAudit(_database.TenantId, context.ActorId, id, correlation,
            attached ? PlatformAuditAction.FileAttached : PlatformAuditAction.FileDetached, before, file.Version, _clock.GetUtcNow());
        var receipt = new FileBindingOperation(_database.TenantId, operation, context.ActorId, id, _entity.Owner,
            _entity.EntityId, attached, expectedVersion, file.Version, audit.Id, correlation, _clock.GetUtcNow());
        _database.Audit.Add(audit);
        _database.FileBindingOperations.Add(receipt);
        await _database.SaveChangesAsync(ct);
        ct.ThrowIfCancellationRequested();
        await transaction.CommitAsync(ct);
        return Committed(receipt, replayed: false);
    }

    private async Task<FileBindingResult> ResolveConflictAsync(TenantContext context, Guid id, Guid operation,
        string? expectedVersion, bool attached, CancellationToken ct)
    {
        var file = await _database.Files.AsNoTracking().SingleOrDefaultAsync(item => item.Id == id, ct);
        if (file is null) return Reject(FileAccessDecision.NotFound);
        if (!await AllowedAsync(context, file, attached, ct)) return Reject(FileAccessDecision.Forbidden);
        var receipt = await FindOperationAsync(operation, ct);
        return receipt is null ? Reject(FileAccessDecision.VersionConflict) : Replay(receipt, context, id, expectedVersion, attached);
    }

    private Task<FileBindingOperation?> FindOperationAsync(Guid operation, CancellationToken ct) =>
        _database.FileBindingOperations.AsNoTracking().SingleOrDefaultAsync(item => item.OperationId == operation, ct);

    private async Task<bool> AllowedAsync(TenantContext context, QuarantinedFile file, bool attached, CancellationToken ct) =>
        await new CurrentFileAuthorization(_database, _clock).AllowedAsync(context, file, "platform.files.write",
            [attached ? "Attach" : "Detach"], ct)
        && await _authorization.CanChangeAsync(context, _entity, attached ? FileBindingAction.Attach : FileBindingAction.Detach, ct);

    private FileBindingResult Replay(FileBindingOperation receipt, TenantContext context, Guid file, string? expectedVersion, bool attached) =>
        receipt.Matches(context.ActorId, file, _entity.Owner, _entity.EntityId, attached, expectedVersion)
            ? Committed(receipt, replayed: true) : Reject(FileAccessDecision.IdempotencyConflict);

    private static FileBindingResult Committed(FileBindingOperation receipt, bool replayed) =>
        new(FileAccessDecision.Success, receipt.FileId, receipt.ResultVersion, receipt.Attached, replayed);

    private FileBindingResult Reject(FileAccessDecision decision)
    {
        _logger.LogWarning("File binding rejected {Code}.", decision.ToString());
        return new(decision);
    }

    private FileAccessException Failure(DbException error)
    {
        var transient = PlatformDatabaseFailures.IsTransient(error);
        _logger.LogWarning("File binding database operation failed. Transient: {Transient}.", transient);
        return new(transient);
    }
}
