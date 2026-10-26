namespace Wes.Platform.Features.Files;

/// <summary>Storage acknowledgement of the exact persisted byte snapshot, not scan clearance.</summary>
public sealed record QuarantineStorageEvidence(string Sha256, long ByteCount);

/// <summary>Private object storage keyed only by trusted owner identities; authorization remains in the application.</summary>
public interface IQuarantineStore
{
    /// <summary>Creates without overwriting; failures may leave a quarantined partial object for owner recovery.</summary>
    Task<QuarantineStorageEvidence> WriteAsync(Guid tenant, Guid file, ReadOnlyMemory<byte> content, CancellationToken cancellationToken);
    /// <summary>Reads only when stored length and digest agree with the owner record.</summary>
    Task<byte[]> ReadVerifiedAsync(Guid tenant, Guid file, string sha256, long byteCount, CancellationToken cancellationToken);
    /// <summary>Ensures absence only after the owner has persisted an unreferenced deletion fence.</summary>
    Task DeleteAsync(Guid tenant, Guid file, CancellationToken cancellationToken);
}
