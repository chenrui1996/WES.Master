namespace Wes.WorkflowForm.Infrastructure.Persistence;

internal sealed class WorkflowSchemaDraftEntity
{
    public Guid TenantId { get; set; }
    public Guid Id { get; set; }
    public string BusinessType { get; set; } = "";
    public int PlannedVersion { get; set; }
    public string DraftVersion { get; set; } = "";
    public string SnapshotJson { get; set; } = "";
    public Guid LastOperationId { get; set; }
    public Guid AuditFactId { get; set; }
}

internal sealed class WorkflowDefinitionDraftEntity
{
    public Guid TenantId { get; set; }
    public Guid Id { get; set; }
    public string BusinessType { get; set; } = "";
    public int PlannedVersion { get; set; }
    public string DraftVersion { get; set; } = "";
    public string SnapshotJson { get; set; } = "";
    public Guid LastOperationId { get; set; }
    public Guid AuditFactId { get; set; }
}

internal sealed class WorkflowSchemaVersionEntity
{
    public Guid TenantId { get; set; }
    public Guid Id { get; set; }
    public int Version { get; set; }
    public string VersionToken { get; set; } = "";
    public string BusinessType { get; set; } = "";
    public string SnapshotJson { get; set; } = "";
    public Guid OperationId { get; set; }
    public DateTimeOffset PublishedAt { get; set; }
}

internal sealed class WorkflowDefinitionVersionEntity
{
    public Guid TenantId { get; set; }
    public Guid Id { get; set; }
    public int Version { get; set; }
    public string VersionToken { get; set; } = "";
    public string BusinessType { get; set; } = "";
    public Guid SchemaId { get; set; }
    public int SchemaVersion { get; set; }
    public string SnapshotJson { get; set; } = "";
    public Guid OperationId { get; set; }
    public DateTimeOffset PublishedAt { get; set; }
}

internal sealed class WorkflowOperationReceiptEntity
{
    public Guid TenantId { get; set; }
    public Guid OperationId { get; set; }
    public Guid ActorId { get; set; }
    public Guid CorrelationId { get; set; }
    public string Action { get; set; } = "";
    public Guid ResourceId { get; set; }
    public string Fingerprint { get; set; } = "";
    public Guid AuditFactId { get; set; }
    public string ResultJson { get; set; } = "";
    public DateTimeOffset OccurredAt { get; set; }
}

internal sealed class WorkflowAuditFactEntity
{
    public Guid TenantId { get; set; }
    public Guid Id { get; set; }
    public Guid OperationId { get; set; }
    public Guid ActorId { get; set; }
    public Guid CorrelationId { get; set; }
    public Guid ResourceId { get; set; }
    public string ResourceKind { get; set; } = "";
    public string Action { get; set; } = "";
    public string? BeforeVersion { get; set; }
    public string AfterVersion { get; set; } = "";
    public DateTimeOffset OccurredAt { get; set; }
}
