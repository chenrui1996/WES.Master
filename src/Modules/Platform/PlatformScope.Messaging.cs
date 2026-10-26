using Wes.Platform.Messaging;

namespace Wes.Platform;

public sealed partial class PlatformScope
{
    /// <summary>Attempts one bounded batch for this owner; durable transport ACKs never imply business completion.</summary>
    public Task<PlatformPublishBatchResult> PublishDueEventsAsync(IPlatformOutboxTransport transport, int limit,
        CancellationToken cancellationToken = default) =>
        PlatformPublishing.DispatchAsync(_database, transport, _clock, limit, cancellationToken);
}
