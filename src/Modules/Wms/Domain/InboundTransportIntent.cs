using System.Globalization;
using System.Text.Json;
using Wes.Wms.Features.Inbound;

namespace Wes.Wms.Domain;

internal sealed class InboundTransportIntent
{
    private InboundTransportIntent() { }
    internal InboundTransportIntent(InboundDocument document, InboundFact fact, Guid balance, int precision)
    {
        var receipt = fact.Receipt();
        if (receipt.ReceiptLocationId == receipt.TargetLocationId)
            throw new InvalidOperationException("Same-location receipts have no transport intent.");
        TenantId = document.TenantId; Id = Guid.NewGuid(); EventId = Guid.NewGuid();
        OrderId = document.Id; ExecutionId = receipt.ExecutionId; ReceiptOperationId = fact.OperationId;
        SourceBalanceId = balance;
        Envelope = JsonSerializer.Serialize(new InboundTransportRequest(EventId, 1, TenantId, Id, OrderId,
            receipt.LineId, ExecutionId, document.WarehouseId, receipt.ContainerId, receipt.ItemId, receipt.Batch,
            receipt.ReceiptLocationId, receipt.TargetLocationId, receipt.Quantity.ToString(CultureInfo.InvariantCulture),
            receipt.UnitId, precision, "WES-RULES-1.0", fact.ActorId, fact.CorrelationId, fact.OperationId, fact.OccurredAt));
    }

    public Guid TenantId { get; private set; }
    public Guid Id { get; private set; }
    public Guid EventId { get; private set; }
    public Guid OrderId { get; private set; }
    public Guid ExecutionId { get; private set; }
    public Guid ReceiptOperationId { get; private set; }
    public Guid SourceBalanceId { get; private set; }
    public string Envelope { get; private set; } = "";
    public Guid? TransportId { get; private set; }
    public Guid? AssociationOperationId { get; private set; }
    public string Version { get; private set; } = Guid.NewGuid().ToString("D");

    internal InboundTransportRequest Request() => JsonSerializer.Deserialize<InboundTransportRequest>(Envelope)
        ?? throw new InvalidOperationException("The persisted transport envelope is missing.");

    internal void Associate(Guid transport, Guid operation)
    {
        if (TransportId is not null || transport == Guid.Empty || operation == Guid.Empty)
            throw new InvalidOperationException("A transport association is permanent.");
        TransportId = transport; AssociationOperationId = operation; Version = Guid.NewGuid().ToString("D");
    }
}

internal sealed record InboundTransportFact(string ExpectedVersion, Guid IntentId, Guid ExecutionId,
    Guid TransportId, Guid ContainerId, Guid SourceLocationId, Guid TargetLocationId, Guid? CompletionEventId = null,
    InboundTransportAcceptance? Acceptance = null);
