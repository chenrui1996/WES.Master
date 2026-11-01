using System.Text.Json.Serialization;

namespace Wes.Notifications.Features.Alarms;

/// <summary>Authenticated identity, never resource provenance supplied by a request.</summary>
public sealed record AlarmSubject(Guid TenantId, Guid ActorId, Guid SessionId);
/// <summary>Request-local live owner authorization for a complete explicit field set.</summary>
public sealed record AlarmCreatorAccess(bool Authorized, IReadOnlySet<Guid> CreatorIds);
/// <summary>Implemented by Host using the public Platform owner application boundary.</summary>
public interface IAlarmAuthorization
{
    /// <summary>Resolves current real directory creators with the complete required field intersection.</summary>
    Task<AlarmCreatorAccess> ReadCreatorsAsync(AlarmSubject subject, string permission,
        IReadOnlyCollection<string> fields, CancellationToken cancellationToken);
    /// <summary>Checks current owned provenance; re-evaluated after save and before commit, including replay.</summary>
    Task<IReadOnlySet<string>> ReadFieldsAsync(AlarmSubject subject, string permission, Guid creatorId,
        CancellationToken cancellationToken);
}
/// <summary>Complete operational alarm; state does not assert equipment recovery.</summary>
public sealed record AlarmRow(Guid Id, string SourceOwner, string SourceKind, Guid ResourceId, string Code,
    string Severity, DateTimeOffset FirstOccurredAt, DateTimeOffset LastOccurredAt, int OccurrenceCount,
    string State, long Version, Guid? AcknowledgedBy, DateTimeOffset? AcknowledgedAt, DateTimeOffset? SilenceUntil);
/// <summary>Immutable raw occurrence, including late original observations.</summary>
public sealed record AlarmOccurrenceRow(Guid Id, Guid SourceEventId, DateTimeOffset OccurredAt,
    DateTimeOffset ReceivedAt, Guid CorrelationId, string Summary);
/// <summary>Alarm detail uses the same row keys plus immutable occurrences.</summary>
public sealed record AlarmDetail(Guid Id, string SourceOwner, string SourceKind, Guid ResourceId, string Code,
    string Severity, DateTimeOffset FirstOccurredAt, DateTimeOffset LastOccurredAt, int OccurrenceCount,
    string State, long Version, Guid? AcknowledgedBy, DateTimeOffset? AcknowledgedAt, DateTimeOffset? SilenceUntil,
    IReadOnlyList<AlarmOccurrenceRow> Occurrences);
/// <summary>Durable independently recoverable in-app notification; Sent never means read.</summary>
public sealed record AlarmDeliveryRow(Guid Id, Guid AlarmId, string Channel, string State, int OccurrenceCount,
    DateTimeOffset DueAt, int AttemptCount, DateTimeOffset? NextAttemptAt, string? ErrorCode, long Version);
/// <summary>Immutable delivery transition evidence, not an external transport assertion.</summary>
public sealed record AlarmDeliveryAttemptRow(Guid Id, DateTimeOffset AttemptedAt, string State, string? ErrorCode);
/// <summary>Delivery detail with immutable attempts.</summary>
public sealed record AlarmDeliveryDetail(Guid Id, Guid AlarmId, string Channel, string State, int OccurrenceCount,
    DateTimeOffset DueAt, int AttemptCount, DateTimeOffset? NextAttemptAt, string? ErrorCode, long Version,
    IReadOnlyList<AlarmDeliveryAttemptRow> Attempts);
/// <summary>Bounded current-authorized page.</summary>
public sealed record AlarmPage<T>(IReadOnlyList<T> Rows, int Offset, int Limit);
/// <summary>Stable application decision; only Applied and Replayed disclose a mutation projection.</summary>
public sealed record AlarmResult<T>(string Decision, T? Value = default);
/// <summary>Actor-owned manual ingress; no tenant, creator or device provenance fields.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ManualAlarmCommand(Guid OperationId, Guid ResourceId, string Code, string Severity, string Summary);
/// <summary>Permanent exact-body lifecycle command.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AlarmActionCommand(Guid OperationId, long ExpectedVersion, string Reason, int? Seconds = null);
/// <summary>Public field and permission agreement; no implicit wildcard grants.</summary>
public static class AlarmConsoleFields
{
    /// <summary>Complete alarm and occurrence projection intersection.</summary>
    public static IReadOnlyList<string> AlarmRead { get; } = Array.AsReadOnly(new[]
    {
        "Id", "SourceOwner", "SourceKind", "ResourceId", "Code", "Severity", "FirstOccurredAt", "LastOccurredAt",
        "OccurrenceCount", "State", "Version", "AcknowledgedBy", "AcknowledgedAt", "SilenceUntil",
        "SourceEventId", "OccurredAt", "ReceivedAt", "CorrelationId", "Summary"
    });
    /// <summary>Complete delivery and attempt projection intersection.</summary>
    public static IReadOnlyList<string> DeliveryRead { get; } = Array.AsReadOnly(new[]
    {
        "Id", "AlarmId", "Channel", "State", "OccurrenceCount", "DueAt", "AttemptCount", "NextAttemptAt",
        "ErrorCode", "Version", "AttemptedAt"
    });
    /// <summary>Explicit manual creation fields.</summary>
    public static IReadOnlyList<string> ManualCreate { get; } =
        Array.AsReadOnly(new[] { "OperationId", "ResourceId", "Code", "Severity", "Summary" });
}
