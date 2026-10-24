namespace Wes.Platform.Features.Identity;

/// <summary>Finite internal security causes; anonymous transport must not disclose account existence or thresholds.</summary>
public enum LoginEventKind
{
    /// <summary>A complete new-format session committed.</summary>
    Completed,
    /// <summary>A legacy credential created only a password-change-restricted session.</summary>
    LegacyRestricted,
    /// <summary>The global trusted source budget denied the request.</summary>
    SourceLimited,
    /// <summary>The submitted bound challenge was not accepted.</summary>
    ChallengeRejected,
    /// <summary>Current account risk required a challenge before checking a password.</summary>
    ChallengeRequired,
    /// <summary>Current account risk denied verification during a temporary lock.</summary>
    Locked,
    /// <summary>A checked candidate did not qualify, including unknown or disabled subjects.</summary>
    InvalidCredential,
    /// <summary>A success contender lost a credential/risk CAS and disclosed no secret.</summary>
    VersionConflict
}

/// <summary>Append-only tenant security evidence with pseudonymous bindings, never arbitrary payloads or secret material.</summary>
public sealed class LoginSecurityEvent
{
    private LoginSecurityEvent() { }
    /// <summary>Records only canonical bindings, finite result and optional successfully created session reference.</summary>
    public LoginSecurityEvent(Guid tenant, string accountDigest, string sourceDigest, Guid correlation,
        LoginEventKind kind, DateTimeOffset now, Guid? sessionId = null)
    {
        if (tenant == Guid.Empty || correlation == Guid.Empty || !Enum.IsDefined(kind)
            || !IsDigest(accountDigest) || !IsDigest(sourceDigest)
            || (kind is LoginEventKind.Completed or LoginEventKind.LegacyRestricted
                ? sessionId is null || sessionId == Guid.Empty : sessionId is not null))
            throw new ArgumentException("Login evidence requires canonical owner bindings and a valid finite outcome.");
        TenantId = tenant;
        AccountDigest = accountDigest;
        SourceDigest = sourceDigest;
        CorrelationId = correlation;
        Kind = kind;
        OccurredAt = new DateTimeOffset(now.UtcTicks - now.UtcTicks % 10, TimeSpan.Zero);
        SessionId = sessionId;
    }
    /// <summary>Opaque append-only fact ID.</summary>
    public Guid Id { get; private set; } = Guid.NewGuid();
    /// <summary>Trusted tenant.</summary>
    public Guid TenantId { get; private set; }
    /// <summary>Pseudonymous normalized account binding; not an anonymous identifier.</summary>
    public string AccountDigest { get; private set; } = "";
    /// <summary>Pseudonymous trusted network source binding.</summary>
    public string SourceDigest { get; private set; } = "";
    /// <summary>Nonsecret request correlation.</summary>
    public Guid CorrelationId { get; private set; }
    /// <summary>Internal finite security result.</summary>
    public LoginEventKind Kind { get; private set; }
    /// <summary>Server UTC microsecond instant.</summary>
    public DateTimeOffset OccurredAt { get; private set; }
    /// <summary>Successful session reference only, not a token.</summary>
    public Guid? SessionId { get; private set; }
    /// <summary>Prevents accidental binding disclosure in diagnostics.</summary>
    public override string ToString() => "LoginSecurityEvent[REDACTED]";
    private static bool IsDigest(string value) => value is { Length: 64 } && value.All(character => char.IsAsciiHexDigit(character)
        && !char.IsAsciiLetterLower(character));
}
