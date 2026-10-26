using System.Data.Common;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Wes.Platform.Domain;
using Wes.Platform.Infrastructure.Persistence;

namespace Wes.Platform.Features.Files;

internal enum FileProcessingDecision
{
    Available, Quarantined, Rejected, Deleted, Invalid, NotFound, VersionRequired, VersionConflict,
    StateConflict, ObsoleteAttempt, Referenced, TooYoung
}

internal enum FileProcessingFailure { Evidence, Database }
internal sealed class FileProcessingException(FileProcessingFailure failure, bool transient = false)
    : Exception("Platform file processing failed.")
{
    internal FileProcessingFailure Failure { get; } = failure;
    internal bool IsTransient { get; } = transient;
}

internal sealed record FileProcessingResult(FileProcessingDecision Decision, QuarantinedFileState? State = null, string? Version = null);

/// <summary>Internal owner coordination only; callers must establish entity authority before exposing any file operation.</summary>
internal sealed class FileProcessing(PlatformDbContext database, IQuarantineStore storage, IFileScanner scanner,
    TimeProvider clock, ILogger<FileProcessing> logger)
{
    internal async Task<FileProcessingResult> ScanAsync(Guid id, string? expectedVersion, CancellationToken ct = default)
    {
        RequireCleanOwner(ct);
        if (id == Guid.Empty) return new(FileProcessingDecision.Invalid);
        if (!ValidVersion(expectedVersion)) return new(FileProcessingDecision.VersionRequired);
        try
        {
            var file = await database.Files.SingleOrDefaultAsync(item => item.Id == id, ct);
            if (file is null) return new(FileProcessingDecision.NotFound);
            if (file.State is QuarantinedFileState.Available or QuarantinedFileState.Rejected)
                return Scanned(file);
            if (file.Version != expectedVersion) return new(FileProcessingDecision.VersionConflict);
            if (file.State != QuarantinedFileState.Quarantined || !file.Stored || HasLiveScan(file))
                return new(FileProcessingDecision.StateConflict);
            var attempt = file.BeginScan(clock.GetUtcNow());
            await database.SaveChangesAsync(ct);
            database.ChangeTracker.Clear();

            FileScanEvidence evidence;
            byte[]? content = null;
            var remaining = file.ScanStartedAt!.Value + QuarantinedFile.ScanTimeout - clock.GetUtcNow();
            using var budget = new CancellationTokenSource(remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero, clock);
            if (remaining <= TimeSpan.Zero) budget.Cancel();
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, budget.Token);
            try
            {
                linked.Token.ThrowIfCancellationRequested();
                content = await storage.ReadVerifiedAsync(database.TenantId, id, file.Sha256, file.ByteCount, linked.Token);
                if (content is null || content.LongLength != file.ByteCount
                    || Convert.ToHexString(SHA256.HashData(content)) != file.Sha256)
                    throw EvidenceFailure();
                evidence = await scanner.ScanAsync(content, linked.Token);
                linked.Token.ThrowIfCancellationRequested();
                if (evidence is null || !Enum.IsDefined(evidence.Verdict)
                    || evidence.Sha256 != file.Sha256 || evidence.ByteCount != file.ByteCount)
                    throw EvidenceFailure();
            }
            catch (OperationCanceledException) when (budget.IsCancellationRequested && !ct.IsCancellationRequested)
            {
                evidence = new(FileScanVerdict.TimedOut, file.Sha256, file.ByteCount);
            }
            finally
            {
                if (content is not null) CryptographicOperations.ZeroMemory(content);
            }

            ct.ThrowIfCancellationRequested();
            var current = await database.Files.SingleOrDefaultAsync(item => item.Id == id, ct);
            if (current is null) return new(FileProcessingDecision.NotFound);
            if (!current.CompleteScan(attempt, evidence.Verdict, evidence.Sha256, evidence.ByteCount, clock.GetUtcNow()))
                return new(FileProcessingDecision.ObsoleteAttempt, current.State, current.Version);
            await database.SaveChangesAsync(ct);
            database.ChangeTracker.Clear();
            return Scanned(current);
        }
        catch (DbUpdateConcurrencyException)
        {
            ct.ThrowIfCancellationRequested();
            database.ChangeTracker.Clear();
            return new(FileProcessingDecision.VersionConflict);
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

    internal async Task<FileProcessingResult> DeleteOrphanAsync(Guid id, string? expectedVersion, CancellationToken ct = default)
    {
        RequireCleanOwner(ct);
        if (id == Guid.Empty) return new(FileProcessingDecision.Invalid);
        if (!ValidVersion(expectedVersion)) return new(FileProcessingDecision.VersionRequired);
        try
        {
            var file = await database.Files.SingleOrDefaultAsync(item => item.Id == id, ct);
            if (file is null) return new(FileProcessingDecision.NotFound);
            if (await ReferencedAsync(file, ct)) return new(FileProcessingDecision.Referenced);
            if (file.State == QuarantinedFileState.Deleted) return new(FileProcessingDecision.Deleted, file.State, file.Version);
            if (file.Version != expectedVersion) return new(FileProcessingDecision.VersionConflict);
            if (clock.GetUtcNow() - file.CreatedAt < QuarantinedFile.OrphanAge) return new(FileProcessingDecision.TooYoung);
            if (HasLiveScan(file)) return new(FileProcessingDecision.StateConflict);
            if (file.State != QuarantinedFileState.Deleting)
            {
                file.BeginDeletion(ownerConfirmedUnreferenced: true, clock.GetUtcNow());
                await database.SaveChangesAsync(ct);
            }
            database.ChangeTracker.Clear();
            await storage.DeleteAsync(database.TenantId, id, ct);
            ct.ThrowIfCancellationRequested();
            var current = await database.Files.SingleOrDefaultAsync(item => item.Id == id, ct);
            if (current is null) return new(FileProcessingDecision.NotFound);
            if (await ReferencedAsync(current, ct)) return new(FileProcessingDecision.Referenced);
            if (current.State == QuarantinedFileState.Deleted) return new(FileProcessingDecision.Deleted, current.State, current.Version);
            if (current.State != QuarantinedFileState.Deleting) return new(FileProcessingDecision.StateConflict);
            current.ConfirmDeleted(clock.GetUtcNow());
            await database.SaveChangesAsync(ct);
            database.ChangeTracker.Clear();
            return new(FileProcessingDecision.Deleted, current.State, current.Version);
        }
        catch (DbUpdateConcurrencyException)
        {
            ct.ThrowIfCancellationRequested();
            database.ChangeTracker.Clear();
            return new(FileProcessingDecision.VersionConflict);
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

    private async Task<bool> ReferencedAsync(QuarantinedFile file, CancellationToken ct) =>
        file.ReferenceCount != 0 || await database.FileReferences.AnyAsync(item => item.FileId == file.Id, ct);

    private bool HasLiveScan(QuarantinedFile file) =>
        file.ScanStartedAt is { } started && clock.GetUtcNow() - started < QuarantinedFile.ScanTimeout;

    private void RequireCleanOwner(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (database.ChangeTracker.Entries().Any() || database.Database.CurrentTransaction is not null)
            throw new InvalidOperationException("File processing requires a clean Platform owner scope.");
    }

    private static bool ValidVersion(string? version) => Guid.TryParseExact(version, "D", out var value) && value != Guid.Empty;

    private static FileProcessingResult Scanned(QuarantinedFile file) => new(file.State switch
    {
        QuarantinedFileState.Available => FileProcessingDecision.Available,
        QuarantinedFileState.Rejected => FileProcessingDecision.Rejected,
        QuarantinedFileState.Quarantined => FileProcessingDecision.Quarantined,
        _ => throw new InvalidOperationException("Only persisted scanner outcomes can be returned as scan results.")
    }, file.State, file.Version);

    private FileProcessingException EvidenceFailure()
    {
        logger.LogWarning("File processing rejected mismatched content evidence.");
        return new(FileProcessingFailure.Evidence);
    }

    private FileProcessingException DatabaseFailure(DbException error)
    {
        var transient = PlatformDatabaseFailures.IsTransient(error);
        logger.LogWarning("File processing database operation failed. Transient: {Transient}.", transient);
        return new(FileProcessingFailure.Database, transient);
    }
}
