namespace Wes.Platform.Domain;

/// <summary>Allowlisted nonsecret T04 command kinds; credentials and login intentionally have no kind.</summary>
public enum DirectoryCommandKind
{
    /// <summary>Credential-free user creation.</summary>
    CreateUser,
    /// <summary>User profile and enabled state.</summary>
    ChangeUser,
    /// <summary>Explicit user role membership replacement.</summary>
    ChangeMemberships,
    /// <summary>Role metadata and initial delegable rules.</summary>
    CreateRole,
    /// <summary>Existing role rule replacement, reserved for the role owner adapter.</summary>
    ChangeRole,
    /// <summary>Department creation.</summary>
    CreateDepartment,
    /// <summary>Department metadata and enabled state.</summary>
    ChangeDepartment,
    /// <summary>Current tenant metadata.</summary>
    ChangeTenant,
    /// <summary>Reviewed nonsecret setting change, reserved for the setting owner adapter.</summary>
    ChangeSetting,
    /// <summary>Stable dictionary entry creation.</summary>
    CreateDictionary,
    /// <summary>Plain-text dictionary translation.</summary>
    ChangeTranslation,
    /// <summary>Explicit dictionary owner reference.</summary>
    ChangeDictionaryReference,
    /// <summary>Unreferenced dictionary deletion.</summary>
    DeleteDictionary,
    /// <summary>Versioned department display-hierarchy move, without implicit grant inheritance.</summary>
    ReparentDepartment
}

/// <summary>Bounded ordinary result, never a request archive or source of authorization.</summary>
public sealed class DirectoryCommandReceipt
{
    private DirectoryCommandReceipt() { }

    internal DirectoryCommandReceipt(Guid tenantId, Guid actorId, Guid operationId, DirectoryCommandKind kind,
        string fingerprint, Guid resultId, string resultVersion, DateTimeOffset createdAt)
    {
        TenantId = tenantId;
        ActorId = actorId;
        OperationId = operationId;
        Kind = kind;
        Fingerprint = fingerprint;
        ResultId = resultId;
        ResultVersion = resultVersion;
        CreatedAt = createdAt;
    }

    /// <summary>Trusted owner tenant.</summary>
    public Guid TenantId { get; private set; }
    /// <summary>Trusted actor; another actor cannot claim this operation key.</summary>
    public Guid ActorId { get; private set; }
    /// <summary>Client operation UUID, scoped to the trusted tenant and actor.</summary>
    public Guid OperationId { get; private set; }
    /// <summary>Allowlisted nonsecret command kind.</summary>
    public DirectoryCommandKind Kind { get; private set; }
    /// <summary>Server-computed SHA256 of the allowlisted nonsecret arguments.</summary>
    public string Fingerprint { get; private set; } = "";
    /// <summary>Original committed safe business identity.</summary>
    public Guid ResultId { get; private set; }
    /// <summary>Original committed safe opaque business version.</summary>
    public string ResultVersion { get; private set; } = "";
    /// <summary>UTC commit preparation time; ordinary replay retention is 90 days.</summary>
    public DateTimeOffset CreatedAt { get; private set; }
}
