namespace Wes.Wms.Domain;

/// <summary>Exact public request identity, atomically paired with its immutable inbound source fact.</summary>
internal sealed class InboundExecutionOperation
{
    private InboundExecutionOperation() { }
    internal InboundExecutionOperation(InboundFact fact, string input, DateTimeOffset now)
    {
        if (fact.Kind is not (InboundFactKind.Approved or InboundFactKind.Received) || string.IsNullOrEmpty(input) || input.Length > 4096)
            throw new ArgumentException("An execution operation requires its exact supported source fact and bounded input.");
        TenantId = fact.TenantId; OperationId = fact.OperationId; OrderId = fact.OrderId; ActorId = fact.ActorId;
        CorrelationId = fact.CorrelationId; Kind = fact.Kind; Input = input; AfterVersion = fact.AfterVersion;
        ResultId = fact.Kind == InboundFactKind.Approved ? fact.OrderId : fact.ExecutionId!.Value;
        CreatedAt = now.ToUniversalTime();
    }
    public Guid TenantId { get; private set; }
    public Guid OperationId { get; private set; }
    public Guid OrderId { get; private set; }
    public Guid ActorId { get; private set; }
    public Guid CorrelationId { get; private set; }
    public InboundFactKind Kind { get; private set; }
    public string Input { get; private set; } = "";
    public Guid ResultId { get; private set; }
    public string AfterVersion { get; private set; } = "";
    public DateTimeOffset CreatedAt { get; private set; }
}
