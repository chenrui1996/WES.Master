using Microsoft.Extensions.Logging;
using Wes.Wms.Features.Inbound;
using Wes.Wms.Infrastructure.Persistence;

namespace Wes.Wms;

/// <summary>Explicit trusted module boundary, not a hosted dispatcher or an operator completion endpoint.</summary>
public sealed class WmsInboundTransportWorker
{
    private readonly InboundTransportAccess _access;

    /// <summary>Requires a clean scoped WMS owner, a trusted adapter and a distinct nonempty service principal.</summary>
    public WmsInboundTransportWorker(WmsDbContext database, IInboundTransportAdapter adapter, Guid serviceActor,
        TimeProvider clock, ILogger<WmsInboundTransportWorker> logger)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(adapter);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(logger);
        if (serviceActor == Guid.Empty) throw new ArgumentException("A trusted service actor is required.", nameof(serviceActor));
        _access = new(database, adapter, serviceActor, clock, logger);
    }

    /// <summary>Accept or reconcile the original durable envelope; never issue physical controls.</summary>
    public Task<InboundTransportResult> DispatchAsync(Guid executionId, CancellationToken cancellationToken = default) =>
        _access.DispatchAsync(executionId, cancellationToken);

    /// <summary>Authenticate final evidence, durably retain it, and synchronize the exact final transfer.</summary>
    public Task<InboundTransportResult> ApplyCompletionAsync(InboundTransportCompletion completion,
        CancellationToken cancellationToken = default) => _access.ApplyCompletionAsync(completion, cancellationToken);
}
