namespace Wes.Wms.Domain;

/// <summary>Append-only reservation command identity; partial release cannot be applied twice under one operation.</summary>
public sealed class ReservationChange
{
    private ReservationChange() { }
    internal ReservationChange(Guid tenant, Guid operation, Guid actor, Guid correlation, ReservationAction action,
        StockReservation reservation, decimal quantity, decimal before, StockProtectionKind beforeKind, DateTimeOffset occurredAt)
    {
        Id = Guid.NewGuid();
        TenantId = tenant;
        OperationId = operation;
        ActorId = actor;
        CorrelationId = correlation;
        Action = action;
        ExecutionUnitId = reservation.ExecutionUnitId;
        BalanceId = reservation.BalanceId;
        SourceDocumentId = reservation.SourceDocumentId;
        SourceLineId = reservation.SourceLineId;
        Quantity = quantity;
        Before = before;
        After = reservation.RemainingQuantity;
        BeforeKind = beforeKind;
        AfterKind = reservation.Kind;
        OccurredAt = occurredAt;
    }

    /// <summary>Immutable change identity.</summary>
    public Guid Id { get; private set; }
    /// <summary>Trusted tenant owner.</summary>
    public Guid TenantId { get; private set; }
    /// <summary>Permanent command identity.</summary>
    public Guid OperationId { get; private set; }
    /// <summary>Currently authorized actor.</summary>
    public Guid ActorId { get; private set; }
    /// <summary>Original validated correlation.</summary>
    public Guid CorrelationId { get; private set; }
    /// <summary>Finite reservation transition.</summary>
    public ReservationAction Action { get; private set; }
    /// <summary>Permanent reserved execution portion.</summary>
    public Guid ExecutionUnitId { get; private set; }
    /// <summary>Immutable source balance reference.</summary>
    public Guid BalanceId { get; private set; }
    /// <summary>Original business document reference.</summary>
    public Guid SourceDocumentId { get; private set; }
    /// <summary>Original business line reference.</summary>
    public Guid SourceLineId { get; private set; }
    /// <summary>Reserve or release quantity; zero for reason-only transitions.</summary>
    public decimal Quantity { get; private set; }
    /// <summary>Protected quantity before this command.</summary>
    public decimal Before { get; private set; }
    /// <summary>Protected quantity after this command.</summary>
    public decimal After { get; private set; }
    /// <summary>Previous overlapping protection reasons.</summary>
    public StockProtectionKind BeforeKind { get; private set; }
    /// <summary>New overlapping protection reasons.</summary>
    public StockProtectionKind AfterKind { get; private set; }
    /// <summary>Original command UTC.</summary>
    public DateTimeOffset OccurredAt { get; private set; }
}
