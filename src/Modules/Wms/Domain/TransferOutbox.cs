using System.Text.Json;

namespace Wes.Wms.Domain;

/// <summary>Retained owner-local pending integration event; no default broker or delivery claim.</summary>
internal sealed class TransferOutbox
{
    private TransferOutbox() { }
    internal TransferOutbox(TransferFact fact, StockPosting posting)
    {
        TenantId = fact.TenantId; OperationId = fact.OperationId; PostingId = posting.Id; CreatedAt = fact.CreatedAt;
        Payload = JsonSerializer.Serialize(new
        {
            Type = "Wms.TransferUnitPosted.v1",
            fact.TenantId,
            fact.OrderId,
            fact.LineId,
            posting.ExecutionUnitId,
            posting.SourceBalanceId,
            posting.TargetBalanceId,
            posting.Quantity,
            fact.ActorId,
            fact.CorrelationId,
            fact.AfterVersion
        });
    }
    public Guid TenantId { get; private set; }
    public Guid OperationId { get; private set; }
    public Guid PostingId { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public string Payload { get; private set; } = "";
    internal bool Matches(TransferFact fact, StockPosting posting) =>
        TenantId == fact.TenantId && OperationId == fact.OperationId && PostingId == posting.Id
        && CreatedAt == fact.CreatedAt && Payload == new TransferOutbox(fact, posting).Payload;
}
