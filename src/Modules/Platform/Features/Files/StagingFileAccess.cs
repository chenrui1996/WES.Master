using System.Data.Common;
using System.Security.Cryptography;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Wes.Platform.Domain;
using Wes.Platform.Features.Tenancy;
using Wes.Platform.Infrastructure.Persistence;

namespace Wes.Platform.Features.Files;

/// <summary>Explicit application results; only Success can carry a file view or downloaded content.</summary>
public enum FileAccessDecision
{
    /// <summary>The current caller is authorized for the returned projection or committed action.</summary>
    Success,
    /// <summary>Current authority is absent, or business-entity authorization is required instead.</summary>
    Forbidden,
    /// <summary>The file identity does not exist in the trusted tenant.</summary>
    NotFound,
    /// <summary>The current lifecycle does not allow the requested operation.</summary>
    StateConflict,
    /// <summary>An explicit opaque expected version is missing.</summary>
    VersionRequired,
    /// <summary>The file changed; no physical action is automatically retried.</summary>
    VersionConflict,
    /// <summary>A newer scan or deletion fence superseded this scan.</summary>
    ObsoleteAttempt,
    /// <summary>The file has not reached the minimum orphan age.</summary>
    TooYoung,
    /// <summary>The requested identity or correlation is invalid.</summary>
    Invalid,
    /// <summary>A permanent upload operation was reused with different content or metadata.</summary>
    IdempotencyConflict
}

/// <summary>Fixed safe file metadata without object paths, content digests or business references.</summary>
public sealed record StagingFileView(Guid Id, string FileName, string ContentType, long ByteCount,
    QuarantinedFileState State, bool Stored, string Version, DateTimeOffset CreatedAt, bool ProcessingProblem);

/// <summary>A finite application decision with only a fully authorized metadata projection.</summary>
public sealed record FileAccessResult(FileAccessDecision Decision, StagingFileView? File = null);

/// <summary>Owned verified bytes; the transport must dispose after delivery and must never serialize this buffer.</summary>
public sealed class FileDownload : IDisposable
{
    private byte[]? _content;
    internal FileDownload(string fileName, string contentType, byte[] content)
    {
        FileName = fileName;
        ContentType = contentType;
        _content = content;
    }

    /// <summary>Validated display name only; never an object key.</summary>
    public string FileName { get; }
    /// <summary>Canonical validated media type.</summary>
    public string ContentType { get; }
    /// <summary>Bytes verified against the authorized durable file; excluded from JSON and cleared on disposal.</summary>
    [JsonIgnore]
    public ReadOnlyMemory<byte> Content => _content ?? throw new ObjectDisposedException(nameof(FileDownload));
    /// <summary>Clears this owned content exactly once; retained memory views then contain zeros.</summary>
    public void Dispose()
    {
        var content = Interlocked.Exchange(ref _content, null);
        if (content is not null) CryptographicOperations.ZeroMemory(content);
    }
    /// <summary>Prevents accidental formatting of content or file metadata into ordinary logs.</summary>
    public override string ToString() => "FileDownload[REDACTED]";
}

/// <summary>A verified download is present only after current authority and the download audit have succeeded.</summary>
public sealed record FileDownloadResult(FileAccessDecision Decision, [property: JsonIgnore] FileDownload? Download = null)
{
    /// <summary>Never formats the content-bearing result into logs.</summary>
    public override string ToString() => $"FileDownloadResult[{Decision}]";
}

/// <summary>Safe failure without storage paths, raw SQL, file bytes or provider inner exceptions.</summary>
public sealed class FileAccessException(bool transient = false) : Exception("Platform file access failed.")
{
    /// <summary>Only a fresh owner may explicitly retry a transient database failure.</summary>
    public bool IsTransient { get; } = transient;
}

/// <summary>
/// The public staging factory rejects business attachments even for an All file grant.
/// BusinessFileAccess reuses the read/download implementation with mandatory exact binding and entity authority.
/// </summary>
public sealed partial class StagingFileAccess
{
    private static readonly string[] ReadFields =
        ["Id", "FileName", "ContentType", "ByteCount", "State", "Stored", "Version", "CreatedAt", "ProcessingProblem"];
    private readonly PlatformDbContext _database;
    private readonly IQuarantineStore _storage;
    private readonly IFileScanner _scanner;
    private readonly TimeProvider _clock;
    private readonly ILoggerFactory _logs;
    private readonly ILogger<StagingFileAccess> _logger;
    private readonly FileEntityReference? _entity;
    private readonly IBusinessFileAuthorization? _entityAuthorization;

    internal StagingFileAccess(PlatformDbContext database, IQuarantineStore storage, IFileScanner scanner,
        TimeProvider clock, ILoggerFactory logs, FileEntityReference? entity = null, IBusinessFileAuthorization? entityAuthorization = null)
    {
        ArgumentNullException.ThrowIfNull(storage);
        ArgumentNullException.ThrowIfNull(scanner);
        if ((entity is null) != (entityAuthorization is null))
            throw new ArgumentException("Business file access requires both an exact entity and its current authorization.");
        _database = database;
        _storage = storage;
        _scanner = scanner;
        _clock = clock;
        _logs = logs;
        _logger = logs.CreateLogger<StagingFileAccess>();
        _entity = entity;
        _entityAuthorization = entityAuthorization;
    }

    /// <summary>Reads one fully authorized file projection after current session, binding and resource-scope checks.</summary>
    public Task<FileAccessResult> ReadAsync(TenantContext context, Guid fileId, CancellationToken ct = default) =>
        RunAsync(async () =>
        {
            RequireCleanOwner(ct);
            if (fileId == Guid.Empty) return new FileAccessResult(FileAccessDecision.Invalid);
            var file = await FindAsync(fileId, ct);
            if (file is null) return new(FileAccessDecision.NotFound);
            if (!await AllowedAsync(context, file, "platform.files.read", ReadFields, ct)) return Denied();
            return new(FileAccessDecision.Success, View(file));
        }, ct);

    /// <summary>Reads verified bytes, rechecks authority after IO, and commits an immutable audit before delivery.</summary>
    public Task<FileDownloadResult> DownloadAsync(TenantContext context, Guid fileId, Guid correlation,
        CancellationToken ct = default) => RunAsync(async () =>
    {
        RequireCleanOwner(ct);
        if (fileId == Guid.Empty || correlation == Guid.Empty) return new FileDownloadResult(FileAccessDecision.Invalid);
        var file = await FindAsync(fileId, ct);
        if (file is null) return new(FileAccessDecision.NotFound);
        if (!await CanDownloadAsync(context, file, ct)) return DownloadDenied();
        if (file.State != QuarantinedFileState.Available || !file.Stored) return new(FileAccessDecision.StateConflict);
        byte[]? content = null;
        var delivered = false;
        try
        {
            content = await _storage.ReadVerifiedAsync(_database.TenantId, fileId, file.Sha256, file.ByteCount, ct);
            ct.ThrowIfCancellationRequested();
            if (content is null || content.LongLength != file.ByteCount
                || Convert.ToHexString(SHA256.HashData(content)) != file.Sha256)
            {
                _logger.LogWarning("File access rejected mismatched storage evidence.");
                throw new FileAccessException();
            }
            await using var transaction = await _database.Database.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted, ct);
            var current = await FindAsync(fileId, ct);
            if (current is null) return new(FileAccessDecision.NotFound);
            if (!await CanDownloadAsync(context, current, ct)) return DownloadDenied();
            if (current.Version != file.Version || current.State != QuarantinedFileState.Available || !current.Stored)
                return new(FileAccessDecision.VersionConflict);
            _database.Audit.Add(new(_database.TenantId, context.ActorId, fileId, correlation, PlatformAuditAction.FileDownloaded,
                current.Version, current.Version, _clock.GetUtcNow()));
            await _database.SaveChangesAsync(ct);
            ct.ThrowIfCancellationRequested();
            await transaction.CommitAsync(ct);
            var result = new FileDownload(current.FileName, current.ContentType, content);
            delivered = true;
            return new(FileAccessDecision.Success, result);
        }
        finally
        {
            if (!delivered && content is not null) CryptographicOperations.ZeroMemory(content);
        }
    }, ct);

    /// <summary>Explicitly scans only after current Scan authority; no caller-supplied clean result is accepted.</summary>
    public Task<FileAccessResult> ScanAsync(TenantContext context, Guid fileId, string? expectedVersion,
        CancellationToken ct = default) => ProcessAsync(context, fileId, expectedVersion, deleting: false, ct);

    /// <summary>Deletes only a currently authorized old unreferenced staging file through the durable deletion fence.</summary>
    public Task<FileAccessResult> DeleteOrphanAsync(TenantContext context, Guid fileId, string? expectedVersion,
        CancellationToken ct = default) => ProcessAsync(context, fileId, expectedVersion, deleting: true, ct);

    private Task<FileAccessResult> ProcessAsync(TenantContext context, Guid id, string? version, bool deleting, CancellationToken ct) =>
        RunAsync(async () =>
        {
            RequireCleanOwner(ct);
            if (id == Guid.Empty) return new FileAccessResult(FileAccessDecision.Invalid);
            var file = await FindAsync(id, ct);
            if (file is null) return new(FileAccessDecision.NotFound);
            var fields = new[] { deleting ? "Delete" : "Scan" };
            if (!await AllowedAsync(context, file, "platform.files.write", fields, ct)) return Denied();
            var processor = new FileProcessing(_database, _storage, _scanner, _clock, _logs.CreateLogger<FileProcessing>());
            var result = deleting ? await processor.DeleteOrphanAsync(id, version, ct) : await processor.ScanAsync(id, version, ct);
            _database.ChangeTracker.Clear();
            var current = await FindAsync(id, ct);
            if (current is null) return new(FileAccessDecision.NotFound);
            if (!await AllowedAsync(context, current, "platform.files.write", fields, ct)) return Denied();
            var decision = result.Decision switch
            {
                FileProcessingDecision.Available or FileProcessingDecision.Quarantined or FileProcessingDecision.Rejected
                    or FileProcessingDecision.Deleted => FileAccessDecision.Success,
                FileProcessingDecision.Invalid => FileAccessDecision.Invalid,
                FileProcessingDecision.NotFound => FileAccessDecision.NotFound,
                FileProcessingDecision.VersionRequired => FileAccessDecision.VersionRequired,
                FileProcessingDecision.VersionConflict => FileAccessDecision.VersionConflict,
                FileProcessingDecision.StateConflict => FileAccessDecision.StateConflict,
                FileProcessingDecision.ObsoleteAttempt => FileAccessDecision.ObsoleteAttempt,
                FileProcessingDecision.Referenced => FileAccessDecision.Forbidden,
                FileProcessingDecision.TooYoung => FileAccessDecision.TooYoung,
                _ => throw new InvalidOperationException("Unknown file processing outcome.")
            };
            return new(decision, decision == FileAccessDecision.Success
                && await AllowedAsync(context, current, "platform.files.read", ReadFields, ct) ? View(current) : null);
        }, ct);

    private Task<QuarantinedFile?> FindAsync(Guid id, CancellationToken ct) =>
        _database.Files.AsNoTracking().SingleOrDefaultAsync(item => item.Id == id, ct);

    private async Task<bool> CanDownloadAsync(TenantContext context, QuarantinedFile file, CancellationToken ct) =>
        await AllowedAsync(context, file, "platform.files.read", ReadFields, ct)
        && await AllowedAsync(context, file, "platform.files.download", ["Content"], ct);

    private async Task<bool> AllowedAsync(TenantContext context, QuarantinedFile file, string permission, string[] fields, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.TenantId != _database.TenantId || context.ActorId == Guid.Empty || context.SessionId == Guid.Empty)
            return false;
        if (_entity is null)
        {
            if (file.ReferenceCount != 0 || await _database.FileReferences.AsNoTracking().AnyAsync(item => item.FileId == file.Id, ct))
                return false;
        }
        else if (file.ReferenceCount <= 0
            || !await _database.FileReferences.AsNoTracking().AnyAsync(item =>
                item.FileId == file.Id && item.Owner == _entity.Owner && item.EntityId == _entity.EntityId, ct)
            || !await _entityAuthorization!.CanReadAsync(context, _entity, ct))
            return false;
        return await new CurrentFileAuthorization(_database, _clock).AllowedAsync(context, file, permission, fields, ct);
    }

    private StagingFileView View(QuarantinedFile file) => new(file.Id, file.FileName, file.ContentType, file.ByteCount,
        file.State, file.Stored, file.Version, file.CreatedAt, file.HasProcessingProblem(_clock.GetUtcNow()));

    private FileAccessResult Denied()
    {
        _logger.LogWarning("File access denied by current authority or binding.");
        return new(FileAccessDecision.Forbidden);
    }

    private FileDownloadResult DownloadDenied() => new(Denied().Decision);

    private void RequireCleanOwner(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (_database.ChangeTracker.Entries().Any() || _database.Database.CurrentTransaction is not null)
            throw new InvalidOperationException("File access requires a clean Platform owner.");
    }

    private async Task<T> RunAsync<T>(Func<Task<T>> action, CancellationToken ct)
    {
        try { return await action(); }
        catch (FileProcessingException error)
        {
            ct.ThrowIfCancellationRequested();
            throw new FileAccessException(error.IsTransient);
        }
        catch (DbUpdateException error) when (error.InnerException is DbException)
        {
            ct.ThrowIfCancellationRequested();
            throw DatabaseFailure((DbException)error.InnerException);
        }
        catch (DbException error)
        {
            ct.ThrowIfCancellationRequested();
            throw DatabaseFailure(error);
        }
    }

    private FileAccessException DatabaseFailure(DbException error)
    {
        var transient = PlatformDatabaseFailures.IsTransient(error);
        _logger.LogWarning("File access database operation failed. Transient: {Transient}.", transient);
        return new(transient);
    }
}
