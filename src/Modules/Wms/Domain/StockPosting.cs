namespace Wes.Wms.Domain;

/// <summary>WMS book transitions; none is a claim about a WCS task's physical outcome.</summary>
public enum StockPostingKind
{
    /// <summary>Actual owner-verified receipt into a book dimension.</summary>
    Receipt,
    /// <summary>Actual owner-verified shipment out of a book dimension.</summary>
    Shipment,
    /// <summary>One confirmed move between compatible book positions.</summary>
    Transfer
}

/// <summary>Permanent business identity of a single owner transaction, retained beyond ordinary receipt TTLs.</summary>
public sealed class StockPosting
{
    private StockPosting() { }

    internal StockPosting(Guid tenant, Guid operation, Guid actor, Guid correlation, StockPostingKind kind,
        Guid document, Guid line, Guid execution, Guid? source, Guid? target, decimal quantity, DateTimeOffset occurredAt)
    {
        Id = Guid.NewGuid();
        TenantId = tenant;
        OperationId = operation;
        ActorId = actor;
        CorrelationId = correlation;
        Kind = kind;
        SourceDocumentId = document;
        SourceLineId = line;
        ExecutionUnitId = execution;
        SourceBalanceId = source;
        TargetBalanceId = target;
        Quantity = quantity;
        OccurredAt = occurredAt;
    }

    /// <summary>Owner-generated identity shared by all entries of this posting.</summary>
    public Guid Id { get; private set; }
    /// <summary>Trusted tenant owner.</summary>
    public Guid TenantId { get; private set; }
    /// <summary>Permanent command idempotency identity.</summary>
    public Guid OperationId { get; private set; }
    /// <summary>Currently authorized actor reference, resolved before invoking the posting owner.</summary>
    public Guid ActorId { get; private set; }
    /// <summary>Original validated correlation identity; replay does not rewrite history.</summary>
    public Guid CorrelationId { get; private set; }
    /// <summary>Finite business posting category.</summary>
    public StockPostingKind Kind { get; private set; }
    /// <summary>Source document identity, not a display code.</summary>
    public Guid SourceDocumentId { get; private set; }
    /// <summary>Source document line identity.</summary>
    public Guid SourceLineId { get; private set; }
    /// <summary>Stable execution identity preventing a new operation from repeating the same effect.</summary>
    public Guid ExecutionUnitId { get; private set; }
    /// <summary>Debited dimension, absent for receipt.</summary>
    public Guid? SourceBalanceId { get; private set; }
    /// <summary>Credited dimension, absent for shipment.</summary>
    public Guid? TargetBalanceId { get; private set; }
    /// <summary>Positive exact base-unit movement quantity.</summary>
    public decimal Quantity { get; private set; }
    /// <summary>Original owner commit-attempt UTC time.</summary>
    public DateTimeOffset OccurredAt { get; private set; }
}
