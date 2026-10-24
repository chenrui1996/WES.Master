namespace Wes.Platform.Domain;

/// <summary>Client kind determines the approved idle-lock deadline, not a client-supplied override.</summary>
public enum SessionClient
{
    /// <summary>Web operations client, with a fifteen-minute idle deadline.</summary>
    Web,
    /// <summary>PDA client, with a five-minute idle deadline.</summary>
    Pda
}

/// <summary>Explicit authentication state; none of these states cancels already dispatched physical work.</summary>
public enum SessionState
{
    /// <summary>Eligible for current authenticated activity.</summary>
    Active,
    /// <summary>Requires reauthentication after the client-specific idle deadline.</summary>
    IdleLocked,
    /// <summary>The absolute lifetime has ended.</summary>
    Expired,
    /// <summary>The family was explicitly revoked or replayed.</summary>
    Revoked
}

/// <summary>Expected refresh decisions; invalid credentials are not exceptional control flow.</summary>
public enum RefreshDecision
{
    /// <summary>The current credential was consumed and a successor installed.</summary>
    Rotated,
    /// <summary>An already consumed credential was presented; persist the family revocation.</summary>
    Replayed,
    /// <summary>The presented credential does not belong to this family.</summary>
    Invalid,
    /// <summary>The session is no longer eligible and must be reauthenticated.</summary>
    Inactive
}

/// <summary>
/// Platform-owned refresh family with a fixed absolute lifetime and explicit version.
/// It holds only token digests; callers must persist mutations with optimistic concurrency.
/// </summary>
public sealed class Session
{
    private readonly List<RefreshCredential> _credentials = [];

    private Session() { }

    /// <summary>Creates one session with trusted nonempty owner IDs and an initial digest, never a raw token.</summary>
    public Session(Guid tenantId, Guid userId, SessionClient client, DateTimeOffset now, string digest, bool requiresPasswordChange = false)
    {
        if (tenantId == Guid.Empty || userId == Guid.Empty || !Enum.IsDefined(client))
        {
            throw new ArgumentException("A session requires trusted owner IDs and a supported client kind.");
        }

        ValidateDigest(digest);
        TenantId = tenantId;
        UserId = userId;
        Client = client;
        RequiresPasswordChange = requiresPasswordChange;
        CreatedAt = LastActivityAt = now;
        Id = Guid.NewGuid();
        Version = Guid.NewGuid().ToString("D");
        _credentials.Add(new RefreshCredential(tenantId, Id, digest));
    }

    /// <summary>Opaque session ID, stable for this refresh family.</summary>
    public Guid Id { get; private set; }
    /// <summary>Trusted tenant owner; never inferred from an unverified request parameter.</summary>
    public Guid TenantId { get; private set; }
    /// <summary>Trusted authenticated user owner.</summary>
    public Guid UserId { get; private set; }
    /// <summary>Approved client classification.</summary>
    public SessionClient Client { get; private set; }
    /// <summary>Durable legacy credential restriction; refresh cannot clear it and client claims cannot override it.</summary>
    public bool RequiresPasswordChange { get; private set; }
    /// <summary>UTC instant at initial login; refresh cannot modify it.</summary>
    public DateTimeOffset CreatedAt { get; private set; }
    /// <summary>Last accepted real user activity; refresh does not count.</summary>
    public DateTimeOffset LastActivityAt { get; private set; }
    /// <summary>Absolute expiry derived from the original login, not token rotation.</summary>
    public DateTimeOffset ExpiresAt => CreatedAt.AddHours(8);
    /// <summary>Revocation instant when the family becomes unusable.</summary>
    public DateTimeOffset? RevokedAt { get; private set; }
    /// <summary>Current opaque optimistic version. A mutation changes it exactly once.</summary>
    public string Version { get; private set; } = "";
    /// <summary>Digest-only refresh history, retained for replay detection.</summary>
    public IReadOnlyCollection<RefreshCredential> Credentials => _credentials.AsReadOnly();

    /// <summary>Evaluates approved deadlines without extending or deleting durable state.</summary>
    public SessionState StateAt(DateTimeOffset now)
    {
        ValidateClock(now);
        if (RevokedAt is not null)
        {
            return SessionState.Revoked;
        }

        if (now >= ExpiresAt)
        {
            return SessionState.Expired;
        }

        return now >= LastActivityAt.AddSeconds(Client == SessionClient.Web ? 900 : 300)
            ? SessionState.IdleLocked : SessionState.Active;
    }

    /// <summary>Checks the access deadline and current family state; it never substitutes for authorization.</summary>
    public bool AcceptsAccess(DateTimeOffset issuedAt, DateTimeOffset now)
    {
        if (now < LastActivityAt || issuedAt < CreatedAt || issuedAt > now)
        {
            return false;
        }

        return StateAt(now) == SessionState.Active && now < issuedAt.AddSeconds(900);
    }

    /// <summary>Records actual user activity only when current state permits it.</summary>
    public bool RecordActivity(DateTimeOffset now)
    {
        if (StateAt(now) != SessionState.Active)
        {
            return false;
        }

        if (LastActivityAt != now)
        {
            LastActivityAt = now;
            Touch();
        }

        return true;
    }

    /// <summary>Consumes one known digest and installs a successor, or revokes the family on known replay.</summary>
    public RefreshDecision Rotate(string digest, string successorDigest, DateTimeOffset now)
    {
        ValidateDigest(digest);
        ValidateDigest(successorDigest);
        ValidateClock(now);
        var credential = _credentials.SingleOrDefault(item => item.Digest == digest);
        if (credential is null)
        {
            return RefreshDecision.Invalid;
        }

        if (credential.ConsumedAt is not null)
        {
            Revoke(now);
            return RefreshDecision.Replayed;
        }

        if (StateAt(now) != SessionState.Active)
        {
            return RefreshDecision.Inactive;
        }

        if (_credentials.Any(item => item.Digest == successorDigest))
        {
            return RefreshDecision.Invalid;
        }

        credential.Consume(now);
        _credentials.Add(new RefreshCredential(TenantId, Id, successorDigest));
        Touch();
        return RefreshDecision.Rotated;
    }

    /// <summary>Idempotently revokes this family; a repeated revoke does not change its version.</summary>
    public void Revoke(DateTimeOffset now)
    {
        ValidateClock(now);
        if (RevokedAt is null)
        {
            RevokedAt = now;
            Touch();
        }
    }

    private static void ValidateDigest(string digest)
    {
        if (digest is null || digest.Length != 64 || digest.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new ArgumentException("A credential requires a canonical uppercase SHA-256 digest.");
        }

        if (!string.Equals(digest, digest.ToUpperInvariant(), StringComparison.Ordinal))
        {
            throw new ArgumentException("A credential requires a canonical uppercase SHA-256 digest.");
        }
    }

    private void ValidateClock(DateTimeOffset now)
    {
        if (now < LastActivityAt || now < CreatedAt || (RevokedAt is not null && now < RevokedAt))
        {
            throw new InvalidOperationException("Session time cannot move backwards.");
        }
    }

    private void Touch() => Version = Guid.NewGuid().ToString("D");
}

/// <summary>Digest-only credential history owned by one tenant and session; no raw refresh value is stored.</summary>
public sealed class RefreshCredential
{
    private RefreshCredential() { }

    internal RefreshCredential(Guid tenant, Guid session, string digest)
    {
        TenantId = tenant;
        SessionId = session;
        Digest = digest;
    }

    /// <summary>Tenant component of the immutable owner key.</summary>
    public Guid TenantId { get; private set; }
    /// <summary>Owning refresh family.</summary>
    public Guid SessionId { get; private set; }
    /// <summary>SHA-256 hexadecimal digest, never the raw refresh value.</summary>
    public string Digest { get; private set; } = "";
    /// <summary>Recorded consumption instant, retained to detect replay.</summary>
    public DateTimeOffset? ConsumedAt { get; private set; }

    internal void Consume(DateTimeOffset now) => ConsumedAt = now;
}
