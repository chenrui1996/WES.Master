namespace Wes.Platform.Domain;

/// <summary>Finite owner actions keep credentials and arbitrary payloads out of the audit input schema.</summary>
public enum PlatformAuditAction
{
    /// <summary>A role's permission rules or enabled state changed.</summary>
    RoleChanged,
    /// <summary>A role was assigned to or removed from a subject.</summary>
    MembershipChanged,
    /// <summary>A typed nonsecret setting changed.</summary>
    SettingChanged,
    /// <summary>A subject explicitly revoked their current session family.</summary>
    SessionRevoked,
    /// <summary>The current subject changed their credential and revoked their existing session families.</summary>
    PasswordChanged,
    /// <summary>A current authorized manager disabled an account and revoked its existing families.</summary>
    AccountDisabled
}

/// <summary>Append-only audit fact staged with the owner mutation; no general request/payload string is accepted.</summary>
public sealed class PlatformAudit
{
    private PlatformAudit() { }

    /// <summary>Records UUID references, fixed action and before/after versions only; it does not commit independently.</summary>
    public PlatformAudit(Guid tenant, Guid actor, Guid target, Guid correlation, PlatformAuditAction action,
        string? beforeVersion, string afterVersion, DateTimeOffset occurredAt)
    {
        if (tenant == Guid.Empty || actor == Guid.Empty || target == Guid.Empty || correlation == Guid.Empty
            || !Enum.IsDefined(action) || !IsVersion(afterVersion) || (beforeVersion is not null && !IsVersion(beforeVersion)))
        {
            throw new ArgumentException("Audit requires valid owner references, action and opaque UUID versions.");
        }

        Id = Guid.NewGuid();
        TenantId = tenant;
        ActorId = actor;
        TargetId = target;
        CorrelationId = correlation;
        Action = action;
        BeforeVersion = beforeVersion;
        AfterVersion = afterVersion;
        OccurredAt = occurredAt;
    }

    /// <summary>Immutable audit ID.</summary>
    public Guid Id { get; private set; }
    /// <summary>Audit tenant owner.</summary>
    public Guid TenantId { get; private set; }
    /// <summary>Trusted actor reference, without names or credentials.</summary>
    public Guid ActorId { get; private set; }
    /// <summary>Immutable entity ID reference; not a supplied URL or payload.</summary>
    public Guid TargetId { get; private set; }
    /// <summary>Validated correlation UUID.</summary>
    public Guid CorrelationId { get; private set; }
    /// <summary>Finite action category.</summary>
    public PlatformAuditAction Action { get; private set; }
    /// <summary>Prior opaque version, if a previous entity existed.</summary>
    public string? BeforeVersion { get; private set; }
    /// <summary>Committed target version after the transition.</summary>
    public string AfterVersion { get; private set; } = "";
    /// <summary>Server UTC instant.</summary>
    public DateTimeOffset OccurredAt { get; private set; }

    private static bool IsVersion(string value) => Guid.TryParseExact(value, "D", out var id) && id != Guid.Empty;
}
