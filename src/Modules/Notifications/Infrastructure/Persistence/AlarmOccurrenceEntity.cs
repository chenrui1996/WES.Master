using Wes.Notifications.Features.Alarms;

namespace Wes.Notifications.Infrastructure.Persistence;

internal sealed class AlarmOccurrenceEntity
{
    public Guid TenantId { get; set; }
    public Guid Id { get; set; }
    public Guid SourceEventId { get; set; }
    public string SourceOwner { get; set; } = "";
    public AlarmSourceKind SourceKind { get; set; }
    public Guid ResourceId { get; set; }
    public string Code { get; set; } = "";
    public AlarmSeverity Severity { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
    public DateTimeOffset ReceivedAt { get; set; }
    public Guid CorrelationId { get; set; }
    public string Summary { get; set; } = "";
    public string Fingerprint { get; set; } = "";
}
