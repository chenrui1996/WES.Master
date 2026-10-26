namespace Wes.Platform.Domain;

/// <summary>Durable file lifecycle; quarantine is never equivalent to malware clearance.</summary>
public enum QuarantinedFileState
{
    /// <summary>Awaiting storage confirmation or a current successful scan.</summary>
    Quarantined,
    /// <summary>Stored bytes were cleared by the current scan attempt.</summary>
    Available,
    /// <summary>The scanner rejected the stored bytes.</summary>
    Rejected,
    /// <summary>Reference creation is fenced while object deletion is retried.</summary>
    Deleting,
    /// <summary>The object is absent; the record identity remains as a tombstone.</summary>
    Deleted
}

/// <summary>Finite scanner outcomes; unavailable or late evidence cannot clear quarantine.</summary>
public enum FileScanVerdict
{
    /// <summary>The scanner verified the complete submitted bytes as clean.</summary>
    Clean,
    /// <summary>The scanner detected a prohibited payload.</summary>
    Infected,
    /// <summary>The scanner could not establish a result.</summary>
    Unavailable,
    /// <summary>The approved scan deadline elapsed.</summary>
    TimedOut
}

/// <summary>Owner-local file evidence and transitions; callers must authorize and persist with a version fence.</summary>
public sealed class QuarantinedFile
{
    /// <summary>Maximum byte count permitted by PAR-04.</summary>
    public const long MaximumBytes = 20 * 1024 * 1024;
    /// <summary>Each attempt has its own fixed deadline.</summary>
    public static readonly TimeSpan ScanTimeout = TimeSpan.FromSeconds(60);
    /// <summary>Unresolved files become a visible processing problem at this age.</summary>
    public static readonly TimeSpan ProcessingProblemAge = TimeSpan.FromMinutes(10);
    /// <summary>Earliest age at which an owner may confirm an unreferenced orphan for deletion.</summary>
    public static readonly TimeSpan OrphanAge = TimeSpan.FromDays(1);

    private QuarantinedFile() { }

    /// <summary>Creates a quarantined identity after bounded content validation, before external storage.</summary>
    public QuarantinedFile(Guid tenantId, Guid creatorId, Guid correlationId, string fileName,
        string contentType, long byteCount, string sha256, DateTimeOffset createdAt, Guid? uploadOperationId = null)
    {
        if (tenantId == Guid.Empty || creatorId == Guid.Empty || correlationId == Guid.Empty)
            throw new ArgumentException("Explicit tenant, creator and correlation identities are required.");
        if (!IsValidDisplayName(fileName))
            throw new ArgumentException("A bounded file display name without path syntax is required.", nameof(fileName));
        if (contentType is not ("application/pdf" or "image/png" or "image/jpeg" or "text/plain"))
            throw new ArgumentException("A canonical validated content type is required.", nameof(contentType));
        if (byteCount is < 1 or > MaximumBytes)
            throw new ArgumentOutOfRangeException(nameof(byteCount));
        ValidateDigest(sha256);
        if (uploadOperationId == Guid.Empty)
            throw new ArgumentException("A supplied upload operation identity cannot be empty.", nameof(uploadOperationId));
        TenantId = tenantId;
        CreatorId = creatorId;
        CorrelationId = correlationId;
        FileName = fileName;
        ContentType = contentType;
        ByteCount = byteCount;
        Sha256 = sha256;
        CreatedAt = UpdatedAt = createdAt.ToUniversalTime();
        UploadOperationId = uploadOperationId;
    }

    /// <summary>Server-created object and record identity, never a user supplied path.</summary>
    public Guid Id { get; private set; } = Guid.NewGuid();
    /// <summary>Explicit trusted owner tenant.</summary>
    public Guid TenantId { get; private set; }
    /// <summary>Authenticated uploader identity.</summary>
    public Guid CreatorId { get; private set; }
    /// <summary>Upload correlation, without the original request body.</summary>
    public Guid CorrelationId { get; private set; }
    /// <summary>Permanent upload command identity; null only for records created before the upload application boundary.</summary>
    public Guid? UploadOperationId { get; private set; }
    /// <summary>Display-only name; never used as an object storage key.</summary>
    public string FileName { get; private set; } = "";
    /// <summary>Canonical validated media type.</summary>
    public string ContentType { get; private set; } = "";
    /// <summary>Actual bounded byte count.</summary>
    public long ByteCount { get; private set; }
    /// <summary>Uppercase SHA256 of the validated upload.</summary>
    public string Sha256 { get; private set; } = "";
    /// <summary>Server-clock upload instant.</summary>
    public DateTimeOffset CreatedAt { get; private set; }
    /// <summary>Last accepted owner transition instant.</summary>
    public DateTimeOffset UpdatedAt { get; private set; }
    /// <summary>Current storage and scanning state.</summary>
    public QuarantinedFileState State { get; private set; }
    /// <summary>Whether external storage acknowledged the matching bytes.</summary>
    public bool Stored { get; private set; }
    /// <summary>Current attempt fence, absent when no scan is pending.</summary>
    public Guid? ScanAttemptId { get; private set; }
    /// <summary>Start instant of the current attempt.</summary>
    public DateTimeOffset? ScanStartedAt { get; private set; }
    /// <summary>Last accepted finite scan outcome, without raw scanner output.</summary>
    public FileScanVerdict? LastScanVerdict { get; private set; }
    /// <summary>Owner-maintained reference count; a unique reference table must back every increment.</summary>
    public int ReferenceCount { get; private set; }
    /// <summary>Opaque compare-and-swap token for owner persistence.</summary>
    public string Version { get; private set; } = Guid.NewGuid().ToString("D");

    /// <summary>Confirms storage of exactly the validated bytes; does not mark them safe.</summary>
    public void ConfirmStored(string sha256, long byteCount, DateTimeOffset now)
    {
        EnsureTime(now);
        if (State != QuarantinedFileState.Quarantined || Stored)
            throw new InvalidOperationException("Storage can only be confirmed once while quarantined.");
        EnsureContent(sha256, byteCount);
        Stored = true;
        Touch(now);
    }

    /// <summary>Starts or replaces an expired scan; active scans cannot be overlapped.</summary>
    public Guid BeginScan(DateTimeOffset now)
    {
        EnsureTime(now);
        if (State != QuarantinedFileState.Quarantined || !Stored
            || ScanStartedAt is { } started && now - started < ScanTimeout)
            throw new InvalidOperationException("A stored quarantined file without a live scan is required.");
        ScanAttemptId = Guid.NewGuid();
        ScanStartedAt = now.ToUniversalTime();
        Touch(now);
        return ScanAttemptId.Value;
    }

    /// <summary>Applies current evidence only; returns false for an obsolete attempt without mutating any state.</summary>
    public bool CompleteScan(Guid attemptId, FileScanVerdict verdict, string sha256, long byteCount, DateTimeOffset now)
    {
        if (!Enum.IsDefined(verdict)) throw new ArgumentOutOfRangeException(nameof(verdict));
        if (State != QuarantinedFileState.Quarantined || attemptId == Guid.Empty || ScanAttemptId != attemptId)
            return false;
        EnsureTime(now);
        EnsureContent(sha256, byteCount);
        if (now - ScanStartedAt!.Value >= ScanTimeout) verdict = FileScanVerdict.TimedOut;
        LastScanVerdict = verdict;
        State = verdict switch
        {
            FileScanVerdict.Clean => QuarantinedFileState.Available,
            FileScanVerdict.Infected => QuarantinedFileState.Rejected,
            _ => QuarantinedFileState.Quarantined
        };
        ScanAttemptId = null;
        ScanStartedAt = null;
        Touch(now);
        return true;
    }

    /// <summary>Reports unresolved quarantine after 600 seconds without changing safety state.</summary>
    public bool HasProcessingProblem(DateTimeOffset now)
    {
        EnsureTime(now);
        return State == QuarantinedFileState.Quarantined && now - CreatedAt >= ProcessingProblemAge;
    }

    /// <summary>Stages a reference alongside its owner-verified unique binding in the same transaction.</summary>
    public void AddReference(DateTimeOffset now)
    {
        EnsureTime(now);
        if (State != QuarantinedFileState.Available)
            throw new InvalidOperationException("Only available files can be referenced.");
        ReferenceCount = checked(ReferenceCount + 1);
        Touch(now);
    }

    /// <summary>Stages removal of an existing binding; the owner must atomically remove its unique row.</summary>
    public void RemoveReference(DateTimeOffset now)
    {
        EnsureTime(now);
        if (ReferenceCount == 0 || State != QuarantinedFileState.Available)
            throw new InvalidOperationException("An existing available-file reference is required.");
        ReferenceCount--;
        Touch(now);
    }

    /// <summary>Fences new bindings after the owner has rechecked zero references in its cleanup transaction.</summary>
    public void BeginDeletion(bool ownerConfirmedUnreferenced, DateTimeOffset now)
    {
        EnsureTime(now);
        if (!ownerConfirmedUnreferenced || ReferenceCount != 0 || now - CreatedAt < OrphanAge
            || State is QuarantinedFileState.Deleting or QuarantinedFileState.Deleted
            || ScanStartedAt is { } started && now - started < ScanTimeout)
            throw new InvalidOperationException("Only an old, verified unreferenced orphan without a live scan can be deleted.");
        State = QuarantinedFileState.Deleting;
        ScanAttemptId = null;
        ScanStartedAt = null;
        Touch(now);
    }

    /// <summary>Marks a deletion tombstone after external storage has confirmed absence; retries keep Deleting until then.</summary>
    public void ConfirmDeleted(DateTimeOffset now)
    {
        EnsureTime(now);
        if (State != QuarantinedFileState.Deleting)
            throw new InvalidOperationException("Storage deletion must follow the owner deletion fence.");
        State = QuarantinedFileState.Deleted;
        Touch(now);
    }

    private void EnsureContent(string digest, long byteCount)
    {
        if (!string.Equals(Sha256, digest, StringComparison.Ordinal) || ByteCount != byteCount)
            throw new ArgumentException("Storage or scan evidence does not match the validated bytes.");
    }

    private static void ValidateDigest(string digest)
    {
        if (digest is null || digest.Length != 64 || digest.Any(character => !char.IsAsciiHexDigit(character) || char.IsAsciiLetterLower(character)))
            throw new ArgumentException("An uppercase SHA256 digest is required.", nameof(digest));
    }

    internal static bool IsValidDisplayName(string? fileName) =>
        !string.IsNullOrWhiteSpace(fileName) && fileName.Length <= 255 && !fileName.Any(char.IsControl)
        && fileName.IndexOfAny(['/', '\\', ':']) < 0 && !fileName.EndsWith('.') && !fileName.EndsWith(' ');

    private void EnsureTime(DateTimeOffset now)
    {
        if (now < UpdatedAt) throw new ArgumentOutOfRangeException(nameof(now), "File transition time cannot move backwards.");
    }

    private void Touch(DateTimeOffset now)
    {
        UpdatedAt = now.ToUniversalTime();
        Version = Guid.NewGuid().ToString("D");
    }
}
