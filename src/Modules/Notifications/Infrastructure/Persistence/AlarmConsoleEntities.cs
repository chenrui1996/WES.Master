using Wes.Notifications.Features.Alarms;

namespace Wes.Notifications.Infrastructure.Persistence;

internal sealed class AlarmEntity
{
    public Guid TenantId { get; set; }
    public Guid Id { get; set; }
    public Guid CreatorId { get; set; }
    public string AggregateKey { get; set; } = "";
    public string SourceOwner { get; set; } = "";
    public AlarmSourceKind SourceKind { get; set; }
    public Guid ResourceId { get; set; }
    public string Code { get; set; } = "";
    public AlarmSeverity Severity { get; set; }
    public DateTimeOffset FirstOccurredAt { get; set; }
    public DateTimeOffset LastOccurredAt { get; set; }
    public int OccurrenceCount { get; set; }
    public string State { get; set; } = "Active";
    public long Version { get; set; }
    public Guid? AcknowledgedBy { get; set; }
    public DateTimeOffset? AcknowledgedAt { get; set; }
    public DateTimeOffset? SilenceUntil { get; set; }
}

internal sealed class AlarmMembershipEntity
{
    public Guid TenantId { get; set; }
    public Guid OccurrenceId { get; set; }
    public Guid AlarmId { get; set; }
}

internal sealed class AlarmDeliveryEntity
{
    public Guid TenantId { get; set; }
    public Guid Id { get; set; }
    public Guid AlarmId { get; set; }
    public string Channel { get; set; } = "InApp";
    public string State { get; set; } = "Pending";
    public int OccurrenceCount { get; set; }
    public DateTimeOffset DueAt { get; set; }
    public int AttemptCount { get; set; }
    public DateTimeOffset? NextAttemptAt { get; set; }
    public string? ErrorCode { get; set; }
    public long Version { get; set; }
}

internal sealed class AlarmAttemptEntity
{
    public Guid TenantId { get; set; }
    public Guid Id { get; set; }
    public Guid DeliveryId { get; set; }
    public DateTimeOffset AttemptedAt { get; set; }
    public string State { get; set; } = "";
    public string? ErrorCode { get; set; }
}

internal sealed class AlarmOperationEntity
{
    public Guid TenantId { get; set; }
    public Guid OperationId { get; set; }
    public Guid ActorId { get; set; }
    public Guid TargetId { get; set; }
    public Guid CreatorId { get; set; }
    public string Kind { get; set; } = "";
    public string Fingerprint { get; set; } = "";
    public string Body { get; set; } = "";
}

internal sealed class AlarmAuditEntity
{
    public Guid TenantId { get; set; }
    public Guid Id { get; set; }
    public Guid OperationId { get; set; }
    public Guid ActorId { get; set; }
    public Guid TargetId { get; set; }
    public string Kind { get; set; } = "";
    public string Reason { get; set; } = "";
    public DateTimeOffset At { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }
}

internal sealed class AlarmConsoleLockEntity
{
    public Guid TenantId { get; set; }
}
