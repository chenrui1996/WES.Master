using System.Net;
using Microsoft.Extensions.Logging;
using Wes.Platform.Features.Files;
using Wes.Platform.Infrastructure.Files;

namespace Wes.Platform;

/// <summary>Named deployment composition without exposing private storage implementation or paths to Host endpoints.</summary>
public sealed class PlatformFileAdapters
{
    private PlatformFileAdapters(IQuarantineStore storage, IFileScanner scanner) { Storage = storage; Scanner = scanner; }
    /// <summary>Private bounded create-new storage; application authority is still required.</summary>
    public IQuarantineStore Storage { get; }
    /// <summary>Explicit loopback scanner, never a no-op clean verdict.</summary>
    public IFileScanner Scanner { get; }
    /// <summary>Validates the existing private root and scanner endpoint, but does not assert ACLs or virus database health.</summary>
    public static PlatformFileAdapters Local(string root, IPAddress scannerAddress, int scannerPort, TimeProvider clock,
        ILoggerFactory logs) => new(new LocalQuarantineStore(root, logs.CreateLogger<LocalQuarantineStore>()),
            new ClamAvFileScanner(scannerAddress, scannerPort, clock, logs.CreateLogger<ClamAvFileScanner>()));

    /// <summary>Only recognized unavailable storage failures may be classified as retryable dependencies.</summary>
    public static bool IsStorageUnavailable(Exception error) =>
        error is QuarantineStorageException { Failure: QuarantineStorageFailure.Unavailable };
    /// <summary>Recognized integrity failures are explicit errors, never successful or retryable missing-object fallbacks.</summary>
    public static bool IsStorageIntegrityFailure(Exception error) =>
        error is QuarantineStorageException { Failure: QuarantineStorageFailure.Integrity };
}
