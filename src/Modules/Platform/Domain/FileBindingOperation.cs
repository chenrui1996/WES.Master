namespace Wes.Platform.Domain;

/// <summary>Permanent metadata-only attachment command and original receipt; never an authorization cache.</summary>
public sealed class FileBindingOperation
{
    private FileBindingOperation() { }

    internal FileBindingOperation(Guid tenant, Guid operation, Guid actor, Guid file, string owner, Guid entity,
        bool attached, string expectedVersion, string resultVersion, Guid audit, Guid correlation, DateTimeOffset createdAt)
    {
        if (new[] { tenant, operation, actor, file, entity, audit, correlation }.Any(id => id == Guid.Empty)
            || !Version(expectedVersion) || !Version(resultVersion))
            throw new ArgumentException("Attachment operations require explicit identities and opaque UUID versions.");
        TenantId = tenant; OperationId = operation; ActorId = actor; FileId = file;
        Owner = DirectoryValue.Code(owner); EntityId = entity; Attached = attached;
        ExpectedVersion = expectedVersion; ResultVersion = resultVersion;
        AuditId = audit; CorrelationId = correlation; CreatedAt = createdAt;
    }

    /// <summary>Trusted tenant; part of the permanent operation key.</summary>
    public Guid TenantId { get; private set; }
    /// <summary>Permanent tenant-local attachment operation identity.</summary>
    public Guid OperationId { get; private set; }
    /// <summary>Original actor; a different actor cannot recover this receipt.</summary>
    public Guid ActorId { get; private set; }
    /// <summary>Retained file identity, not a physical object path.</summary>
    public Guid FileId { get; private set; }
    /// <summary>Reviewed fixed business owner/type.</summary>
    public string Owner { get; private set; } = "";
    /// <summary>Business identity interpreted only by its owner.</summary>
    public Guid EntityId { get; private set; }
    /// <summary>Original committed membership transition, not necessarily today's membership.</summary>
    public bool Attached { get; private set; }
    /// <summary>Exact original file version precondition.</summary>
    public string ExpectedVersion { get; private set; } = "";
    /// <summary>Original committed file version, never replaced during replay.</summary>
    public string ResultVersion { get; private set; } = "";
    /// <summary>Atomically appended audit identity.</summary>
    public Guid AuditId { get; private set; }
    /// <summary>Original trusted correlation identity.</summary>
    public Guid CorrelationId { get; private set; }
    /// <summary>Original server UTC instant; this receipt has no automatic expiry.</summary>
    public DateTimeOffset CreatedAt { get; private set; }

    internal bool Matches(Guid actor, Guid file, string owner, Guid entity, bool attached, string? expectedVersion) =>
        ActorId == actor && FileId == file && Owner == owner && EntityId == entity
        && Attached == attached && ExpectedVersion == expectedVersion;

    private static bool Version(string value) => Guid.TryParseExact(value, "D", out var id) && id != Guid.Empty;
}
