namespace Wes.Platform.Domain;

/// <summary>Permanent metadata-only identity of an authorized snapshot delivery attempt, committed with its audit.</summary>
public sealed class PlatformAuditExportReceipt
{
    private PlatformAuditExportReceipt() { }

    internal PlatformAuditExportReceipt(Guid tenant, Guid operation, Guid actor, Guid session, Guid snapshot,
        string columns, Guid audit, DateTimeOffset createdAt)
    {
        TenantId = tenant; OperationId = operation; ActorId = actor; SessionId = session;
        SnapshotId = snapshot; Columns = columns; AuditId = audit; CreatedAt = createdAt;
    }

    /// <summary>Trusted owner.</summary>
    public Guid TenantId { get; private set; }
    /// <summary>Permanent tenant-local export identity.</summary>
    public Guid OperationId { get; private set; }
    /// <summary>Original subject, never replayable by another actor.</summary>
    public Guid ActorId { get; private set; }
    /// <summary>Original live session.</summary>
    public Guid SessionId { get; private set; }
    /// <summary>Immutable fixed membership, not a saved copy of data.</summary>
    public Guid SnapshotId { get; private set; }
    /// <summary>Exact ordered safe column identifiers.</summary>
    public string Columns { get; private set; } = "";
    /// <summary>Atomically committed authorized delivery audit.</summary>
    public Guid AuditId { get; private set; }
    /// <summary>First authorization instant, not proof of receipt by the browser.</summary>
    public DateTimeOffset CreatedAt { get; private set; }
}
