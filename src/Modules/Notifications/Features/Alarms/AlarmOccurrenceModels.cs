namespace Wes.Notifications.Features.Alarms;

internal enum AlarmSourceKind { Device = 1, Manual }
internal enum AlarmSeverity { Information = 1, Warning, High, Critical }
internal enum OccurrenceDecision { Recorded, Replayed, Invalid, IdempotencyConflict }

/// <summary>Trusted source input; this internal contract does not acquire source or user authorization.</summary>
internal sealed record AlarmOccurrenceInput(
    Guid SourceEventId, string SourceOwner, AlarmSourceKind SourceKind, Guid ResourceId,
    string Code, AlarmSeverity Severity, DateTimeOffset OccurredAt, Guid CorrelationId, string Summary);

/// <summary>Immutable fact projection, independent of alarm acknowledgement, delivery and equipment recovery.</summary>
internal sealed record AlarmOccurrence(
    Guid Id, Guid SourceEventId, string SourceOwner, AlarmSourceKind SourceKind, Guid ResourceId,
    string Code, AlarmSeverity Severity, DateTimeOffset OccurredAt, DateTimeOffset ReceivedAt,
    Guid CorrelationId, string Summary);

internal sealed record OccurrenceResult(OccurrenceDecision Decision, AlarmOccurrence? Value = null, string? ErrorCode = null);
