namespace Wes.Platform.Domain;

/// <summary>Unique owner-verified business attachment, never a foreign key into another module's private table.</summary>
public sealed class FileReference
{
    private FileReference() { }

    /// <summary>Creates a binding after current entity authorization; commit with the corresponding file version increment.</summary>
    public FileReference(Guid tenantId, Guid fileId, string owner, Guid entityId)
    {
        if (tenantId == Guid.Empty || fileId == Guid.Empty || entityId == Guid.Empty)
            throw new ArgumentException("Explicit tenant, file and business entity identities are required.");
        TenantId = tenantId;
        FileId = fileId;
        Owner = DirectoryValue.Code(owner);
        EntityId = entityId;
    }

    /// <summary>Trusted owner tenant shared with the file record.</summary>
    public Guid TenantId { get; private set; }
    /// <summary>Server-created file identity.</summary>
    public Guid FileId { get; private set; }
    /// <summary>Reviewed business owner code, not a caller-selected authorization strategy.</summary>
    public string Owner { get; private set; } = "";
    /// <summary>Stable business entity reference interpreted only by its owning module.</summary>
    public Guid EntityId { get; private set; }
}
