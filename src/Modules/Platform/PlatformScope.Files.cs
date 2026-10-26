using Wes.Platform.Features.Files;

namespace Wes.Platform;

public sealed partial class PlatformScope
{
    /// <summary>
    /// Creates the current-authorized unbound staging-file boundary with explicit private storage and scanner ports.
    /// Business-bound attachments require their entity owner's additional authorization and are rejected here.
    /// </summary>
    public StagingFileAccess StagingFiles(IQuarantineStore storage, IFileScanner scanner) =>
        new(_database, storage, scanner, _clock, _logs);
}
