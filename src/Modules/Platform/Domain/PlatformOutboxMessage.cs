using System.Text.Json;

namespace Wes.Platform.Domain;

/// <summary>Platform-owned durable event staged with a safe audit fact in the same owner transaction.</summary>
public sealed class PlatformOutboxMessage
{
    private static readonly JsonSerializerOptions PayloadOptions = new(JsonSerializerDefaults.Web);

    private PlatformOutboxMessage() { }

    internal PlatformOutboxMessage(PlatformAudit audit)
    {
        ArgumentNullException.ThrowIfNull(audit);
        if (!IsPublishable(audit.Action))
        {
            throw new ArgumentException("The audit action is not eligible for event publication.", nameof(audit));
        }

        TenantId = audit.TenantId;
        EventId = audit.Id;
        ContractName = "platform.audit";
        SchemaVersion = 1;
        Owner = "Platform";
        AggregateId = audit.TargetId;
        AggregateVersion = audit.AfterVersion;
        CorrelationId = audit.CorrelationId;
        OccurredAt = audit.OccurredAt;
        Payload = JsonSerializer.Serialize(new PlatformAuditEvent(audit.Action.ToString(),
            audit.BeforeVersion, audit.AfterVersion), PayloadOptions);
        Status = OutboxMessageStatus.Pending;
        NextAttemptAt = audit.OccurredAt;
        CreatedAt = audit.OccurredAt;
    }

    /// <summary>Creates an event only for explicitly approved non-secret Platform facts.</summary>
    internal static PlatformOutboxMessage? FromAudit(PlatformAudit audit) =>
        IsPublishable(audit.Action) ? new PlatformOutboxMessage(audit) : null;

    /// <summary>Trusted tenant identity.</summary>
    public Guid TenantId { get; private set; }
    /// <summary>Stable event identity, shared with the append-only source audit fact.</summary>
    public Guid EventId { get; private set; }
    /// <summary>Stable event contract name.</summary>
    public string ContractName { get; private set; } = "";
    /// <summary>Version of the platform audit event payload.</summary>
    public int SchemaVersion { get; private set; }
    /// <summary>Owning module identifier.</summary>
    public string Owner { get; private set; } = "";
    /// <summary>Source aggregate identity.</summary>
    public Guid AggregateId { get; private set; }
    /// <summary>Opaque source aggregate version.</summary>
    public string AggregateVersion { get; private set; } = "";
    /// <summary>Request correlation identity.</summary>
    public Guid CorrelationId { get; private set; }
    /// <summary>Optional predecessor event identity.</summary>
    public Guid? CausationId { get; private set; }
    /// <summary>Source fact time in UTC.</summary>
    public DateTimeOffset OccurredAt { get; private set; }
    /// <summary>Allowlisted, non-secret audit event payload.</summary>
    public string Payload { get; private set; } = "";
    /// <summary>Delivery lifecycle state.</summary>
    public OutboxMessageStatus Status { get; private set; }
    /// <summary>Number of broker publish attempts.</summary>
    public int Attempts { get; private set; }
    /// <summary>First failed delivery instant; starts the bounded automatic retry budget.</summary>
    public DateTimeOffset? FirstFailureAt { get; private set; }
    /// <summary>Current publisher lease identity, when this message is leased.</summary>
    public Guid? LeaseId { get; private set; }
    /// <summary>Current publisher lease expiry in UTC.</summary>
    public DateTimeOffset? LeaseUntil { get; private set; }
    /// <summary>Earliest eligible retry instant.</summary>
    public DateTimeOffset NextAttemptAt { get; private set; }
    /// <summary>Successful broker publish instant, if acknowledged.</summary>
    public DateTimeOffset? PublishedAt { get; private set; }
    /// <summary>Finite failure category; raw exception and payload are never stored.</summary>
    public string? LastErrorCategory { get; private set; }
    /// <summary>Creation time in UTC.</summary>
    public DateTimeOffset CreatedAt { get; private set; }

    /// <summary>Records a finite delivery failure and schedules only bounded, non-physical retries.</summary>
    public void RecordFailure(DateTimeOffset failedAt, OutboxFailureCategory category)
    {
        if (!Enum.IsDefined(category))
        {
            throw new ArgumentOutOfRangeException(nameof(category));
        }

        if (Status is not (OutboxMessageStatus.Pending or OutboxMessageStatus.Leased))
        {
            throw new InvalidOperationException("Only pending or leased outbox messages can record a delivery failure.");
        }

        if (failedAt < CreatedAt)
        {
            throw new ArgumentOutOfRangeException(nameof(failedAt));
        }

        FirstFailureAt ??= failedAt;
        Attempts++;
        LastErrorCategory = category.ToString();
        ClearLease();

        if (!category.IsTransient() || failedAt >= FirstFailureAt.Value.AddMinutes(15) || Attempts > RetryDelays.Length)
        {
            Status = OutboxMessageStatus.Quarantined;
            return;
        }

        var retryAt = failedAt + RetryDelays[Attempts - 1];
        if (retryAt >= FirstFailureAt.Value.AddMinutes(15))
        {
            Status = OutboxMessageStatus.Quarantined;
            return;
        }

        NextAttemptAt = retryAt;
        Status = OutboxMessageStatus.Pending;
    }

    internal bool TryAcquireLease(Guid leaseId, DateTimeOffset now, TimeSpan duration)
    {
        if (leaseId == Guid.Empty)
        {
            throw new ArgumentException("A publisher lease requires an identity.", nameof(leaseId));
        }

        if (duration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(duration));
        }

        if (Status == OutboxMessageStatus.Pending)
        {
            if (NextAttemptAt > now)
            {
                return false;
            }
        }
        else if (Status == OutboxMessageStatus.Leased)
        {
            if (LeaseUntil is null || LeaseUntil > now)
            {
                return false;
            }
        }
        else
        {
            return false;
        }

        if (FirstFailureAt is { } firstFailureAt && now >= firstFailureAt.AddMinutes(15))
        {
            Status = OutboxMessageStatus.Quarantined;
            ClearLease();
            return false;
        }

        Status = OutboxMessageStatus.Leased;
        LeaseId = leaseId;
        LeaseUntil = now + duration;
        return true;
    }

    private sealed record PlatformAuditEvent(string Action, string? BeforeVersion, string AfterVersion);

    private static readonly TimeSpan[] RetryDelays =
    [
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(30),
        TimeSpan.FromSeconds(120),
        TimeSpan.FromSeconds(600)
    ];

    private static bool IsPublishable(PlatformAuditAction action) => action is
        PlatformAuditAction.RoleChanged or
        PlatformAuditAction.MembershipChanged or
        PlatformAuditAction.SettingChanged or
        PlatformAuditAction.UserChanged or
        PlatformAuditAction.DepartmentChanged or
        PlatformAuditAction.TenantChanged or
        PlatformAuditAction.DictionaryChanged;

    private void ClearLease()
    {
        LeaseId = null;
        LeaseUntil = null;
    }
}

/// <summary>Finite durable message delivery states.</summary>
public enum OutboxMessageStatus
{
    /// <summary>Ready for an eligible publisher to claim.</summary>
    Pending,
    /// <summary>Owned by a publisher lease.</summary>
    Leased,
    /// <summary>Broker acknowledged publication.</summary>
    Published,
    /// <summary>Automatic delivery stopped; operator action is required.</summary>
    Quarantined
}

/// <summary>Finite failure categories prevent exception text or message content from entering the outbox.</summary>
public enum OutboxFailureCategory
{
    /// <summary>Broker or transport is temporarily unavailable.</summary>
    BrokerUnavailable,
    /// <summary>Broker confirmation did not arrive before the transport timeout.</summary>
    BrokerTimeout,
    /// <summary>Message contract is permanently invalid or unsupported.</summary>
    InvalidContract,
    /// <summary>Delivery was permanently rejected by an authorization boundary.</summary>
    AuthorizationDenied
}

internal static class OutboxFailureCategoryExtensions
{
    internal static bool IsTransient(this OutboxFailureCategory category) =>
        category is OutboxFailureCategory.BrokerUnavailable or OutboxFailureCategory.BrokerTimeout;
}
