namespace Wes.Wms.Domain;

/// <summary>Tenant-owned warehouse identity; recording master data does not enable execution or allocation.</summary>
public sealed class Warehouse
{
    private Warehouse() { }

    /// <summary>Creates a disabled warehouse with a stable canonical ASCII code and safe Unicode display name.</summary>
    internal Warehouse(Guid tenantId, string code, string name, DateTimeOffset createdAt)
    {
        if (tenantId == Guid.Empty) throw new ArgumentException("A trusted warehouse tenant is required.");
        TenantId = tenantId;
        Code = MasterDataValue.Code(code);
        Name = MasterDataValue.Name(name);
        CreatedAt = createdAt.ToUniversalTime();
    }

    /// <summary>Permanent server-created warehouse identity.</summary>
    public Guid Id { get; private set; } = Guid.NewGuid();
    /// <summary>Explicit trusted tenant.</summary>
    public Guid TenantId { get; private set; }
    /// <summary>Canonical immutable tenant-unique code.</summary>
    public string Code { get; private set; } = "";
    /// <summary>Safe display name, never interpreted as code or a Pack address.</summary>
    public string Name { get; private set; } = "";
    /// <summary>Logical availability only; never proves location, inventory or physical safety authority.</summary>
    public bool Enabled { get; private set; }
    /// <summary>Opaque owner version for controlled updates, never an authorization cache.</summary>
    public string Version { get; private set; } = Guid.NewGuid().ToString("D");
    /// <summary>Owner UTC creation time.</summary>
    public DateTimeOffset CreatedAt { get; private set; }

    internal void ChangeState(bool enabled)
    {
        if (Enabled == enabled) throw new InvalidOperationException("Warehouse state must actually change.");
        Enabled = enabled;
        Version = Guid.NewGuid().ToString("D");
    }
}

internal static class MasterDataValue
{
    internal static bool IsCode(string? value) => value is { Length: > 0 and <= 64 }
        && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.');
    internal static bool IsName(string? value) => value is { Length: > 0 and <= 128 }
        && value == value.Trim() && !value.Any(char.IsControl);
    internal static string Code(string value) => IsCode(value) ? value.ToUpperInvariant()
        : throw new ArgumentException("A stable ASCII code of 1..64 characters is required.");
    internal static string Name(string value) => IsName(value) ? value
        : throw new ArgumentException("A bounded display name without control characters or boundary whitespace is required.");
}
