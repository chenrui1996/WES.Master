namespace Wes.Wms.Domain;

public sealed partial class WmsOutboxMessage
{
    private static readonly int[] RetrySeconds = [1, 5, 30, 120, 600];
    /// <summary>Opaque optimistic fence renewed by every delivery transition.</summary>
    public Guid DeliveryVersion { get; private set; }
    /// <summary>Durably claimed attempts, including interrupted sends; at most six.</summary>
    public int Attempts { get; private set; }
    /// <summary>Start of the fifteen-minute automatic recovery budget.</summary>
    public DateTimeOffset? FirstFailureAt { get; private set; }
    /// <summary>Only this lease may confirm a result before its deadline.</summary>
    public Guid? LeaseId { get; private set; }
    /// <summary>UTC deadline; equality means the lease is expired.</summary>
    public DateTimeOffset? LeaseUntil { get; private set; }
    /// <summary>Next eligible attempt, or the lease deadline while sending.</summary>
    public DateTimeOffset NextAttemptAt { get; private set; }
    /// <summary>Durably confirmed transport acknowledgement time, not business completion.</summary>
    public DateTimeOffset? PublishedAt { get; private set; }
    /// <summary>Finite failure category only, never provider text or payload.</summary>
    public string? LastErrorCategory { get; private set; }

    internal bool TryLease(DateTimeOffset now)
    {
        ValidateDelivery();
        if (Status is WmsOutboxStatus.Published or WmsOutboxStatus.Quarantined
            || now < NextAttemptAt || FirstFailureAt > now) return false;
        if (Status == WmsOutboxStatus.Leased)
        {
            FirstFailureAt ??= LeaseUntil;
            LastErrorCategory = "LeaseExpired";
        }
        if (Attempts >= 6 || BudgetExpired(now))
        {
            Quarantine(Attempts >= 6 ? "RetryExhausted" : "BudgetExpired");
            return false;
        }
        Attempts++;
        Status = WmsOutboxStatus.Leased;
        LeaseId = Guid.NewGuid();
        LeaseUntil = now.AddSeconds(90);
        NextAttemptAt = LeaseUntil.Value;
        DeliveryVersion = Guid.NewGuid();
        return true;
    }

    internal bool OwnsLease(Guid lease, DateTimeOffset now) =>
        Status == WmsOutboxStatus.Leased && LeaseId == lease && now < LeaseUntil;

    internal void Acknowledge(Guid lease, DateTimeOffset now)
    {
        if (!OwnsLease(lease, now)) throw new InvalidOperationException("WMS delivery lease is no longer current.");
        Status = WmsOutboxStatus.Published;
        PublishedAt = now;
        LeaseId = null; LeaseUntil = null;
        LastErrorCategory = null;
        DeliveryVersion = Guid.NewGuid();
    }

    internal void Fail(Guid lease, DateTimeOffset now, bool permanent, double jitter)
    {
        if (!OwnsLease(lease, now)) throw new InvalidOperationException("WMS delivery lease is no longer current.");
        if (!double.IsFinite(jitter) || jitter is < 0 or > 1) throw new ArgumentOutOfRangeException(nameof(jitter));
        FirstFailureAt ??= now;
        if (permanent || Attempts >= 6 || BudgetExpired(now))
        {
            Quarantine(permanent ? "PermanentFailure" : Attempts >= 6 ? "RetryExhausted" : "BudgetExpired");
            return;
        }
        var microseconds = decimal.Truncate(RetrySeconds[Attempts - 1] * 1_000_000m * (1m + (decimal)jitter * .2m));
        var due = now.AddTicks(checked((long)microseconds * 10));
        var deadline = FirstFailureAt.Value.AddMinutes(15);
        NextAttemptAt = due < deadline ? due : deadline;
        NextAttemptAt = NextAttemptAt.AddTicks(-(NextAttemptAt.Ticks % 10));
        Status = WmsOutboxStatus.Pending;
        LastErrorCategory = "TemporaryFailure";
        LeaseId = null; LeaseUntil = null;
        DeliveryVersion = Guid.NewGuid();
    }

    internal void ValidateDelivery()
    {
        if (!Enum.IsDefined(Status) || DeliveryVersion == Guid.Empty || Attempts is < 0 or > 6
            || NextAttemptAt < OccurredAt || LastErrorCategory is not (null or "TemporaryFailure" or "PermanentFailure"
                or "LeaseExpired" or "RetryExhausted" or "BudgetExpired")
            || Status == WmsOutboxStatus.Leased && (Attempts == 0 || LeaseId is null || LeaseId == Guid.Empty
                || LeaseUntil is null || LeaseUntil != NextAttemptAt)
            || Status != WmsOutboxStatus.Leased && (LeaseId is not null || LeaseUntil is not null)
            || (Status == WmsOutboxStatus.Published) != PublishedAt.HasValue
            || FirstFailureAt < OccurredAt || PublishedAt < OccurredAt
            || Status == WmsOutboxStatus.Pending && Attempts > 0
                && (FirstFailureAt is null || LastErrorCategory != "TemporaryFailure")
            || Status == WmsOutboxStatus.Published && LastErrorCategory is not null
            || Status == WmsOutboxStatus.Quarantined
                && (FirstFailureAt is null || LastErrorCategory is not ("PermanentFailure" or "RetryExhausted" or "BudgetExpired"))
            || Status != WmsOutboxStatus.Pending && Attempts == 0
            || Attempts == 0 && (FirstFailureAt is not null || LastErrorCategory is not null || NextAttemptAt != OccurredAt))
            throw new InvalidOperationException("Unsupported WMS delivery state.");
    }

    internal bool BudgetExpired(DateTimeOffset now) => FirstFailureAt.HasValue && now >= FirstFailureAt.Value.AddMinutes(15);
    private void Quarantine(string category)
    {
        Status = WmsOutboxStatus.Quarantined;
        LastErrorCategory = category;
        LeaseId = null; LeaseUntil = null;
        DeliveryVersion = Guid.NewGuid();
    }
}
