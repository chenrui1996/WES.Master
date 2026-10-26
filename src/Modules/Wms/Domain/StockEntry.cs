namespace Wes.Wms.Domain;

/// <summary>Append-only signed quantity fact; the immutable balance identity supplies all inventory dimensions.</summary>
public sealed class StockEntry
{
    private StockEntry() { }

    internal StockEntry(StockPosting posting, StockBalance balance, decimal before, decimal after)
    {
        if (balance.TenantId != posting.TenantId || before == after || after < 0m
            || Math.Abs(after - before) != posting.Quantity)
            throw new ArgumentException("A ledger entry requires the exact posting quantity and a matching tenant balance.");
        Id = Guid.NewGuid();
        TenantId = posting.TenantId;
        PostingId = posting.Id;
        BalanceId = balance.Id;
        Before = before;
        After = after;
        Quantity = after - before;
        OccurredAt = posting.OccurredAt;
        CorrelationId = posting.CorrelationId;
    }

    /// <summary>Immutable entry identity.</summary>
    public Guid Id { get; private set; }
    /// <summary>Trusted tenant owner.</summary>
    public Guid TenantId { get; private set; }
    /// <summary>Transaction's permanent posting identity.</summary>
    public Guid PostingId { get; private set; }
    /// <summary>Immutable stock dimension identity.</summary>
    public Guid BalanceId { get; private set; }
    /// <summary>Signed exact delta; debit is negative, credit positive.</summary>
    public decimal Quantity { get; private set; }
    /// <summary>Book quantity immediately before this posting.</summary>
    public decimal Before { get; private set; }
    /// <summary>Book quantity immediately after this posting.</summary>
    public decimal After { get; private set; }
    /// <summary>Original UTC occurrence time shared by both transfer legs.</summary>
    public DateTimeOffset OccurredAt { get; private set; }
    /// <summary>Original correlation reference.</summary>
    public Guid CorrelationId { get; private set; }
}
