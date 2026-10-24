using System.Collections.Immutable;
using Wes.Platform.Domain;

namespace Wes.Platform.Features.Directory;

/// <summary>Explicit safe owner outcomes; denied results do not disclose target existence or versions.</summary>
public enum DirectoryDecision
{
    /// <summary>Mutation and audit committed.</summary>
    Changed,
    /// <summary>Current scope, fields, eligibility or target authority is absent.</summary>
    Forbidden,
    /// <summary>A current opaque version is required.</summary>
    VersionRequired,
    /// <summary>A fresh read and authorization check are required.</summary>
    VersionConflict,
    /// <summary>The authorized tenant already owns this stable identity.</summary>
    Duplicate,
    /// <summary>A stable dictionary code is in use and cannot be removed.</summary>
    Referenced,
    /// <summary>Credential policy rejected or authoritative evidence is unavailable.</summary>
    PasswordRejected,
    /// <summary>The actor-scoped operation UUID was already committed with different nonsecret arguments.</summary>
    IdempotencyConflict
}

/// <summary>Committed owner identity/version only, never a credential or unfiltered entity.</summary>
public sealed record DirectoryWriteResult(DirectoryDecision Decision, Guid? Id = null, string? Version = null,
    PasswordDecision? PasswordDecision = null);

/// <summary>Authorized bounded projection; denied and legitimately empty pages are distinct.</summary>
public sealed record DirectoryPage<T>(bool Authorized, ImmutableArray<T> Rows);

/// <summary>Field-authorized user projection. Null means not granted, never a request to fetch a hidden entity.</summary>
public sealed record UserDirectoryRow(Guid Id, string? Account, string? Name, Guid? DepartmentId, bool? Enabled,
    string? Version, ImmutableArray<Guid>? RoleIds);

/// <summary>Optional exact account, contains-name, department/state filters; pagination is stable and bounded.</summary>
public sealed record UserDirectoryFilter(string? Account = null, string? Name = null, Guid? DepartmentId = null,
    bool? Enabled = null, int Offset = 0, int Limit = 50);

/// <summary>Field-authorized department projection.</summary>
public sealed record DepartmentDirectoryRow(Guid Id, string? Code, string? Name, bool? Enabled, string? Version,
    Guid? ParentId = null, string? ParentName = null);

/// <summary>Field-authorized role projection, with rules included only when expressly granted.</summary>
public sealed record RoleDirectoryRow(Guid Id, string? Code, bool? Enabled, string? Version,
    ImmutableArray<PermissionGrant>? Rules, int? AffectedSubjects = null);

/// <summary>Field-authorized tenant metadata; not a switch token or a trusted authentication context.</summary>
public sealed record TenantDirectoryRow(Guid TenantId, string? Name, bool? Enabled, string? DefaultLanguage, string? Version);

/// <summary>Explicit switch eligibility; transport must reauthenticate the target tenant and discard old state.</summary>
public sealed record TenantSwitchDecision(bool Authorized, Guid? TenantId = null);

/// <summary>Stable dictionary identity and separately projected localized display label.</summary>
public sealed record DictionaryDirectoryRow(Guid Id, string? Category, string? Code, string? LanguageKey,
    string? Label, string? ResolvedLanguage, bool? MissingTranslation, string? Version);
