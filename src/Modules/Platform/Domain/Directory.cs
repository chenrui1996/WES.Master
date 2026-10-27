using System.Globalization;
using System.Text.RegularExpressions;
using Wes.Platform.Features.Identity;

namespace Wes.Platform.Domain;

/// <summary>Validated bounded directory values, shared only within the Platform owner.</summary>
internal static class DirectoryValue
{
    internal static string Name(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 128 || value.Any(char.IsControl))
            throw new ArgumentException("Directory display name must contain 1..128 non-control characters.");
        return value.Trim();
    }

    internal static string Code(string value)
    {
        if (!Regex.IsMatch(value ?? "", "^[a-zA-Z][a-zA-Z0-9._-]{0,127}$", RegexOptions.CultureInvariant))
            throw new ArgumentException("Directory code must be a stable bounded identifier.");
        return value!;
    }

    internal static string Language(string value)
    {
        var culture = CultureInfo.GetCultureInfo(value);
        if (culture.Equals(CultureInfo.InvariantCulture) || value.Length > 35)
            throw new ArgumentException("A concrete language tag is required.");
        return culture.Name;
    }

    internal static SettingDecision Version(string current, string? expected) =>
        string.IsNullOrWhiteSpace(expected) ? SettingDecision.VersionRequired
        : current == expected ? SettingDecision.Changed : SettingDecision.VersionConflict;
}

/// <summary>One explicit tenant identity; no shared tenant or implicit enabled fallback exists.</summary>
public sealed class DirectoryTenant
{
    private DirectoryTenant() { }
    /// <summary>Explicit local provisioning only; no roles or credentials are created.</summary>
    public DirectoryTenant(Guid tenantId, string name, string defaultLanguage = "en")
    {
        if (tenantId == Guid.Empty) throw new ArgumentException("Tenant identity is required.");
        TenantId = tenantId;
        Name = DirectoryValue.Name(name);
        DefaultLanguage = DirectoryValue.Language(defaultLanguage);
    }
    /// <summary>Stable tenant identity and sole key.</summary>
    public Guid TenantId { get; private set; }
    /// <summary>Human readable tenant name.</summary>
    public string Name { get; private set; } = "";
    /// <summary>Current tenant state.</summary>
    public bool Enabled { get; private set; } = true;
    /// <summary>Explicit translation fallback language.</summary>
    public string DefaultLanguage { get; private set; } = "en";
    /// <summary>Opaque optimistic version.</summary>
    public string Version { get; private set; } = Guid.NewGuid().ToString("D");
    /// <summary>Stages versioned tenant metadata; owner authorizes and atomically audits.</summary>
    public SettingDecision Change(string? expected, string name, bool enabled, string language)
    {
        var decision = DirectoryValue.Version(Version, expected);
        if (decision != SettingDecision.Changed) return decision;
        Name = DirectoryValue.Name(name);
        DefaultLanguage = DirectoryValue.Language(language);
        Enabled = enabled;
        Version = Guid.NewGuid().ToString("D");
        return decision;
    }
}

/// <summary>Tenant department ownership, not a caller-provided authorization claim.</summary>
public sealed class DirectoryDepartment
{
    private DirectoryDepartment() { }
    /// <summary>Creates a stable department; the owner validates its tenant before persistence.</summary>
    public DirectoryDepartment(Guid tenantId, string code, string name, Guid? parentId = null)
    {
        if (tenantId == Guid.Empty) throw new ArgumentException("Tenant identity is required.");
        TenantId = tenantId;
        Code = DirectoryValue.Code(code);
        Name = DirectoryValue.Name(name);
        if (parentId == Guid.Empty) throw new ArgumentException("A parent reference must be nonempty.");
        ParentId = parentId;
    }
    /// <summary>Stable department reference.</summary>
    public Guid Id { get; private set; } = Guid.NewGuid();
    /// <summary>Trusted tenant ownership.</summary>
    public Guid TenantId { get; private set; }
    /// <summary>Tenant-unique stable code.</summary>
    public string Code { get; private set; } = "";
    /// <summary>Optional tenant-local display hierarchy; does not imply permission or enabled-state inheritance.</summary>
    public Guid? ParentId { get; private set; }
    /// <summary>Display name, never a permission.</summary>
    public string Name { get; private set; } = "";
    /// <summary>Disabled departments deny member authorization and login.</summary>
    public bool Enabled { get; private set; } = true;
    /// <summary>Opaque optimistic version.</summary>
    public string Version { get; private set; } = Guid.NewGuid().ToString("D");
    /// <summary>Stages a versioned metadata/state change.</summary>
    public SettingDecision Change(string? expected, string name, bool enabled)
    {
        var decision = DirectoryValue.Version(Version, expected);
        if (decision != SettingDecision.Changed) return decision;
        Name = DirectoryValue.Name(name);
        Enabled = enabled;
        Version = Guid.NewGuid().ToString("D");
        return decision;
    }
    /// <summary>Stages one versioned hierarchy move after the owner has checked scope and all ancestors for cycles.</summary>
    public SettingDecision ChangeParent(string? expected, Guid? parentId)
    {
        if (parentId == Guid.Empty || parentId == Id) throw new ArgumentException("A department cannot parent itself.");
        var decision = DirectoryValue.Version(Version, expected);
        if (decision != SettingDecision.Changed) return decision;
        ParentId = parentId;
        Version = Guid.NewGuid().ToString("D");
        return decision;
    }
}

/// <summary>Durable directory identity separate from password history, roles and sessions.</summary>
public sealed class DirectoryUser
{
    private DirectoryUser() { }
    /// <summary>Creates a credential-free tenant subject; account and subject identity are immutable.</summary>
    public DirectoryUser(Guid tenantId, Guid id, string account, string name, Guid departmentId)
    {
        if (tenantId == Guid.Empty || id == Guid.Empty || departmentId == Guid.Empty)
            throw new ArgumentException("User requires tenant, subject and department references.");
        TenantId = tenantId;
        Id = id;
        Account = AccountName.Normalize(account);
        AccountDigest = LoginAccountKey.FromNormalizedAccount(Account).Digest;
        Name = DirectoryValue.Name(name);
        DepartmentId = departmentId;
    }
    /// <summary>Immutable subject shared only by explicit identity provisioning across tenants.</summary>
    public Guid Id { get; private set; }
    /// <summary>Trusted tenant ownership.</summary>
    public Guid TenantId { get; private set; }
    /// <summary>Immutable canonical account, not a credential.</summary>
    public string Account { get; private set; } = "";
    /// <summary>Bounded unique canonical account key.</summary>
    public string AccountDigest { get; private set; } = "";
    /// <summary>Display name.</summary>
    public string Name { get; private set; } = "";
    /// <summary>Authoritative current department ownership.</summary>
    public Guid DepartmentId { get; private set; }
    /// <summary>Current directory state; credential disable remains an additional veto.</summary>
    public bool Enabled { get; private set; } = true;
    /// <summary>Opaque CAS fence shared by profile and role membership edits.</summary>
    public string Version { get; private set; } = Guid.NewGuid().ToString("D");
    /// <summary>Stages a profile change; both old/new department authorization belongs to the service.</summary>
    public SettingDecision Change(string? expected, string name, Guid departmentId, bool enabled)
    {
        var decision = DirectoryValue.Version(Version, expected);
        if (decision != SettingDecision.Changed) return decision;
        if (departmentId == Guid.Empty) throw new ArgumentException("Department is required.");
        Name = DirectoryValue.Name(name);
        DepartmentId = departmentId;
        Enabled = enabled;
        Version = Guid.NewGuid().ToString("D");
        return decision;
    }
}

/// <summary>Stable business dictionary code with a diagnostic translation key, never localized business state.</summary>
public sealed class DictionaryEntry
{
    private DictionaryEntry() { }
    /// <summary>Creates an immutable category/code identity; numeric business codes remain exact strings, independent of translations.</summary>
    public DictionaryEntry(Guid tenantId, string category, string code, string languageKey)
    {
        if (tenantId == Guid.Empty) throw new ArgumentException("Tenant identity is required.");
        TenantId = tenantId;
        Category = DirectoryValue.Code(category);
        if (!Regex.IsMatch(code ?? "", @"\A[a-zA-Z0-9][a-zA-Z0-9._-]{0,127}\z", RegexOptions.CultureInvariant))
            throw new ArgumentException("Dictionary business code must contain 1..128 stable ASCII identifier characters.");
        Code = code!;
        LanguageKey = DirectoryValue.Code(languageKey);
    }
    /// <summary>Stable reference.</summary>
    public Guid Id { get; private set; } = Guid.NewGuid();
    /// <summary>Trusted tenant ownership.</summary>
    public Guid TenantId { get; private set; }
    /// <summary>Stable dictionary category.</summary>
    public string Category { get; private set; } = "";
    /// <summary>Immutable business code.</summary>
    public string Code { get; private set; } = "";
    /// <summary>Immutable diagnostic lookup key.</summary>
    public string LanguageKey { get; private set; } = "";
    /// <summary>Optimistic deletion/reference fence.</summary>
    public string Version { get; private set; } = Guid.NewGuid().ToString("D");
    /// <summary>Serializes reference/translation edits with removal.</summary>
    public SettingDecision Touch(string? expected)
    {
        var decision = DirectoryValue.Version(Version, expected);
        if (decision == SettingDecision.Changed) Version = Guid.NewGuid().ToString("D");
        return decision;
    }
}

/// <summary>One localized label attached to a stable dictionary identity.</summary>
public sealed class DictionaryTranslation
{
    private DictionaryTranslation() { }
    /// <summary>Creates a bounded plain-text label, not executable markup.</summary>
    public DictionaryTranslation(Guid tenant, Guid entry, string language, string value)
    {
        if (tenant == Guid.Empty || entry == Guid.Empty) throw new ArgumentException("Translation owner is required.");
        TenantId = tenant;
        EntryId = entry;
        Language = DirectoryValue.Language(language);
        Change(value);
    }
    /// <summary>Trusted tenant ownership.</summary>
    public Guid TenantId { get; private set; }
    /// <summary>Stable dictionary reference.</summary>
    public Guid EntryId { get; private set; }
    /// <summary>Canonical language tag.</summary>
    public string Language { get; private set; } = "";
    /// <summary>Plain display text.</summary>
    public string Value { get; private set; } = "";
    /// <summary>Stages text after owner entry-version authorization.</summary>
    public void Change(string value) => Value = DirectoryValue.Name(value);
}

/// <summary>Durable owner reference preventing deletion of a dictionary identity in use.</summary>
public sealed class DictionaryReference
{
    private DictionaryReference() { }
    /// <summary>Registers a tenant-local stable business reference; removal must be a separately authorized owner operation.</summary>
    public DictionaryReference(Guid tenant, Guid entry, Guid referenceId)
    {
        if (tenant == Guid.Empty || entry == Guid.Empty || referenceId == Guid.Empty)
            throw new ArgumentException("Dictionary reference requires nonempty owner identities.");
        TenantId = tenant;
        EntryId = entry;
        ReferenceId = referenceId;
    }
    /// <summary>Trusted tenant ownership.</summary>
    public Guid TenantId { get; private set; }
    /// <summary>Stable dictionary reference.</summary>
    public Guid EntryId { get; private set; }
    /// <summary>Stable referring entity identity, never a supplied SQL path.</summary>
    public Guid ReferenceId { get; private set; }
}
