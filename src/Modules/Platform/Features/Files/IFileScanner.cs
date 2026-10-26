using Wes.Platform.Domain;

namespace Wes.Platform.Features.Files;

/// <summary>Evidence tied to the exact byte snapshot submitted to a scanner, without raw scanner output.</summary>
public sealed record FileScanEvidence(FileScanVerdict Verdict, string Sha256, long ByteCount);

/// <summary>Scanning boundary; implementing the protocol is not itself evidence that an engine is healthy.</summary>
public interface IFileScanner
{
    /// <summary>Scans a bounded immutable snapshot, propagating caller cancellation and returning explicit unavailable evidence.</summary>
    Task<FileScanEvidence> ScanAsync(ReadOnlyMemory<byte> content, CancellationToken cancellationToken);
}
