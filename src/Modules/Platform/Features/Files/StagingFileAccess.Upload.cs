using System.Data;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using MySql.Data.MySqlClient;
using Wes.Platform.Domain;
using Wes.Platform.Features.Authorization;
using Wes.Platform.Features.Tenancy;
using Wes.Platform.Infrastructure.Files;

namespace Wes.Platform.Features.Files;

/// <summary>Upload storage evidence only, never an assertion of scan clearance or a business binding.</summary>
public sealed record FileUploadResult(FileAccessDecision Decision, Guid? Id = null, QuarantinedFileState? State = null,
    string? Version = null, FileContentRejection? Rejection = null);

public sealed partial class StagingFileAccess
{
    /// <summary>
    /// Validates one bounded immutable snapshot, commits a permanent quarantined identity and audit before external IO,
    /// then confirms exact private storage in a separate audited transaction. Replays never overwrite bytes.
    /// </summary>
    public async Task<FileUploadResult> UploadAsync(TenantContext context, Guid operation, Guid correlation, string fileName,
        string declaredContentType, ReadOnlyMemory<byte> content, CancellationToken ct = default)
    {
        RequireCleanOwner(ct);
        if (operation == Guid.Empty || correlation == Guid.Empty) return new(FileAccessDecision.Invalid);
        if (content.Length is < 1 or > FileContentValidator.MaximumBytes)
            return new(FileAccessDecision.Invalid, Rejection: content.IsEmpty ? FileContentRejection.Empty : FileContentRejection.TooLarge);
        var snapshot = content.ToArray();
        try
        {
            var validation = FileContentValidator.Validate(fileName, declaredContentType, snapshot);
            if (!validation.IsAccepted) return new(FileAccessDecision.Invalid, Rejection: validation.Rejection);
            var canonicalType = MediaTypeHeaderValue.Parse(declaredContentType).MediaType!.ToLowerInvariant();
            var digest = Convert.ToHexString(SHA256.HashData(snapshot));
            return await RunAsync(async () =>
            {
                if (!await CanUploadAsync(context, ct)) return UploadDenied();
                var candidate = new QuarantinedFile(_database.TenantId, context.ActorId, correlation, fileName, canonicalType,
                    snapshot.Length, digest, _clock.GetUtcNow(), operation);
                QuarantinedFile file;
                await using (var transaction = await _database.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct))
                {
                    try
                    {
                        if (!await CanUploadAsync(context, ct)) return UploadDenied();
                        var existing = await FindUploadAsync(operation, ct);
                        if (existing is not null) file = existing;
                        else
                        {
                            _database.Files.Add(candidate);
                            _database.Audit.Add(new(_database.TenantId, context.ActorId, candidate.Id, correlation,
                                PlatformAuditAction.FileUploadCreated, null, candidate.Version, _clock.GetUtcNow()));
                            await _database.SaveChangesAsync(ct);
                            ct.ThrowIfCancellationRequested();
                            await transaction.CommitAsync(ct);
                            file = candidate;
                        }
                    }
                    catch (DbUpdateException error) when (error.InnerException is MySqlException { Number: 1062 })
                    {
                        await transaction.RollbackAsync(ct);
                        await transaction.DisposeAsync();
                        _database.ChangeTracker.Clear();
                        file = await FindUploadAsync(operation, ct)
                            ?? throw new InvalidOperationException("Upload uniqueness conflict has no matching permanent command.");
                    }
                }
                _database.ChangeTracker.Clear();
                if (!SameUpload(file, candidate)) return new(FileAccessDecision.IdempotencyConflict);
                if (!await AllowedAsync(context, file, "platform.files.write", ["Upload"], ct)) return UploadDenied();
                if (file.State is QuarantinedFileState.Deleting or QuarantinedFileState.Deleted) return new(FileAccessDecision.StateConflict);
                if (file.Stored) return Uploaded(file);

                QuarantineStorageEvidence evidence;
                try
                {
                    evidence = await _storage.WriteAsync(_database.TenantId, file.Id, snapshot, ct);
                }
                catch (QuarantineStorageException error) when (error.Failure == QuarantineStorageFailure.Unavailable)
                {
                    // Create-new may report an earlier write whose database confirmation failed. Only exact bytes recover it.
                    var recovered = await _storage.ReadVerifiedAsync(_database.TenantId, file.Id, file.Sha256, file.ByteCount, ct);
                    try
                    {
                        if (recovered is null || recovered.LongLength != file.ByteCount
                            || Convert.ToHexString(SHA256.HashData(recovered)) != file.Sha256)
                            throw UploadEvidenceFailure();
                        evidence = new(file.Sha256, file.ByteCount);
                    }
                    finally
                    {
                        if (recovered is not null) CryptographicOperations.ZeroMemory(recovered);
                    }
                }
                ct.ThrowIfCancellationRequested();
                if (evidence is null || evidence.Sha256 != file.Sha256 || evidence.ByteCount != file.ByteCount)
                    throw UploadEvidenceFailure();
                try
                {
                    await using var transaction = await _database.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
                    var current = await _database.Files.SingleOrDefaultAsync(item => item.Id == file.Id, ct)
                        ?? throw new InvalidOperationException("A durable upload identity cannot disappear.");
                    if (!await AllowedAsync(context, current, "platform.files.write", ["Upload"], ct)) return UploadDenied();
                    if (current.State is QuarantinedFileState.Deleting or QuarantinedFileState.Deleted) return new(FileAccessDecision.StateConflict);
                    if (current.Stored) return Uploaded(current);
                    var before = current.Version;
                    current.ConfirmStored(evidence.Sha256, evidence.ByteCount, _clock.GetUtcNow());
                    _database.Audit.Add(new(_database.TenantId, context.ActorId, current.Id, correlation,
                        PlatformAuditAction.FileUploadStored, before, current.Version, _clock.GetUtcNow()));
                    await _database.SaveChangesAsync(ct);
                    ct.ThrowIfCancellationRequested();
                    await transaction.CommitAsync(ct);
                    _database.ChangeTracker.Clear();
                    return Uploaded(current);
                }
                catch (DbUpdateConcurrencyException)
                {
                    ct.ThrowIfCancellationRequested();
                    _database.ChangeTracker.Clear();
                    var current = await FindUploadAsync(operation, ct);
                    if (current is not null && SameUpload(current, candidate) && current.Stored
                        && current.State is not (QuarantinedFileState.Deleting or QuarantinedFileState.Deleted)
                        && await AllowedAsync(context, current, "platform.files.write", ["Upload"], ct))
                        return Uploaded(current);
                    return new(FileAccessDecision.VersionConflict);
                }
            }, ct);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(snapshot);
        }
    }

    private Task<QuarantinedFile?> FindUploadAsync(Guid operation, CancellationToken ct) =>
        _database.Files.AsNoTracking().SingleOrDefaultAsync(item => item.UploadOperationId == operation, ct);

    private async Task<bool> CanUploadAsync(TenantContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.TenantId != _database.TenantId || context.ActorId == Guid.Empty || context.SessionId == Guid.Empty) return false;
        var actor = await _database.DirectoryUsers.AsNoTracking().SingleOrDefaultAsync(item => item.Id == context.ActorId, ct);
        if (actor is null) return false;
        var fields = await new CurrentAuthorization(_database, _clock).VisibleFieldsAsync(context, "platform.files.write",
            new(_database.TenantId, actor.Id, actor.DepartmentId), ct);
        return fields.Contains("Upload");
    }

    private static bool SameUpload(QuarantinedFile file, QuarantinedFile candidate) =>
        file.CreatorId == candidate.CreatorId && file.FileName == candidate.FileName && file.ContentType == candidate.ContentType
        && file.ByteCount == candidate.ByteCount && file.Sha256 == candidate.Sha256;

    private static FileUploadResult Uploaded(QuarantinedFile file) => new(FileAccessDecision.Success, file.Id, file.State, file.Version);
    private FileUploadResult UploadDenied() => new(Denied().Decision);

    private FileAccessException UploadEvidenceFailure()
    {
        _logger.LogWarning("Upload storage evidence does not match the durable file.");
        return new();
    }
}
