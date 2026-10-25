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
    /// <summary>Earliest eligible retry instant.</summary>
    public DateTimeOffset NextAttemptAt { get; private set; }
    /// <summary>Successful broker publish instant, if acknowledged.</summary>
    public DateTimeOffset? PublishedAt { get; private set; }
    /// <summary>Finite failure category; raw exception and payload are never stored.</summary>
    public string? LastErrorCategory { get; private set; }
    /// <summary>Creation time in UTC.</summary>
    public DateTimeOffset CreatedAt { get; private set; }

    private sealed record PlatformAuditEvent(string Action, string? BeforeVersion, string AfterVersion);

    private static bool IsPublishable(PlatformAuditAction action) => action is
        PlatformAuditAction.RoleChanged or
        PlatformAuditAction.MembershipChanged or
        PlatformAuditAction.SettingChanged or
        PlatformAuditAction.UserChanged or
        PlatformAuditAction.DepartmentChanged or
        PlatformAuditAction.TenantChanged or
        PlatformAuditAction.DictionaryChanged;
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
