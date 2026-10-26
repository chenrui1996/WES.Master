namespace Wes.Wms.Domain;

/// <summary>Finite owner transitions; unknown physical effects cannot be released by an expiry or a retry.</summary>
public enum ReservationAction
{
    /// <summary>Allocate a new disjoint execution portion.</summary>
    Reserve,
    /// <summary>Retain reservation while execution is in transit.</summary>
    MarkInTransit,
    /// <summary>Add reconciliation protection without releasing any quantity.</summary>
    MarkUnknown,
    /// <summary>Release an owner-verified unstarted quantity, never an in-transit or unknown effect.</summary>
    ReleaseUnstarted
}

/// <summary>Permanent identity of one disjoint protected stock portion, including its zero-remaining tombstone.</summary>
public sealed class StockReservation
{
    private StockReservation() { }
    internal StockReservation(Guid tenant, Guid execution, Guid balance, Guid document, Guid line, Guid actor,
        decimal quantity, DateTimeOffset createdAt)
    {
        TenantId = tenant;
        ExecutionUnitId = execution;
        BalanceId = balance;
        SourceDocumentId = document;
        SourceLineId = line;
        ActorId = actor;
        OriginalQuantity = quantity;
        RemainingQuantity = quantity;
        Kind = StockProtectionKind.Reserved;
        CreatedAt = createdAt;
    }

    /// <summary>Trusted tenant.</summary>
    public Guid TenantId { get; private set; }
    /// <summary>Permanent owner execution identity; it may not be recreated after full consumption or release.</summary>
    public Guid ExecutionUnitId { get; private set; }
    /// <summary>Fixed source book dimension.</summary>
    public Guid BalanceId { get; private set; }
    /// <summary>Owning business document.</summary>
    public Guid SourceDocumentId { get; private set; }
    /// <summary>Owning document line.</summary>
    public Guid SourceLineId { get; private set; }
    /// <summary>Original authorized allocator.</summary>
    public Guid ActorId { get; private set; }
    /// <summary>Exact original reserved quantity.</summary>
    public decimal OriginalQuantity { get; private set; }
    /// <summary>Current protected portion; zero is a retained terminal identity.</summary>
    public decimal RemainingQuantity { get; private set; }
    /// <summary>Overlapping reasons protect the same portion only once.</summary>
    public StockProtectionKind Kind { get; private set; }
    /// <summary>Original owner time, never an automatic release deadline.</summary>
    public DateTimeOffset CreatedAt { get; private set; }

    internal bool TryChange(ReservationAction action, decimal quantity)
    {
        if (RemainingQuantity == 0m) return false;
        switch (action)
        {
            case ReservationAction.MarkInTransit when Kind == StockProtectionKind.Reserved:
                Kind |= StockProtectionKind.InTransit;
                return true;
            case ReservationAction.MarkUnknown when (Kind & StockProtectionKind.Unknown) == 0:
                Kind |= StockProtectionKind.Unknown;
                return true;
            case ReservationAction.ReleaseUnstarted when Kind == StockProtectionKind.Reserved
                && quantity > 0m && quantity <= RemainingQuantity:
                RemainingQuantity -= quantity;
                return true;
            default:
                return false;
        }
    }

    internal bool CanConsume(Guid document, Guid line, decimal quantity) =>
        SourceDocumentId == document && SourceLineId == line && quantity > 0m && quantity <= RemainingQuantity
        && (Kind & StockProtectionKind.Unknown) == 0;

    internal void Consume(decimal quantity)
    {
        if (quantity <= 0m || quantity > RemainingQuantity || (Kind & StockProtectionKind.Unknown) != 0)
            throw new InvalidOperationException("Unknown or insufficient reservation cannot be consumed.");
        RemainingQuantity -= quantity;
    }
}
