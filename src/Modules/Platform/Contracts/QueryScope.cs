namespace Wes.Platform.Contracts;

/// <summary>
/// Immutable identity for an owner-managed query snapshot. It carries no authority and contains no query data.
/// </summary>
/// <remarks>
/// Owners must recompute the authorization fingerprint from current effective grants and reauthorize each use.
/// Filter and sort fingerprints must be computed from the owner's canonical, allowlisted query representation.
/// </remarks>
public sealed record QueryScope
{
    /// <summary>Creates a query scope bound to one tenant, actor, session, authorization, filter, and sort.</summary>
    public QueryScope(
        Guid tenantId,
        Guid actorId,
        Guid sessionId,
        string authorizationFingerprint,
        string filterFingerprint,
        string sortFingerprint)
    {
        TenantId = RequireIdentity(tenantId, nameof(tenantId));
        ActorId = RequireIdentity(actorId, nameof(actorId));
        SessionId = RequireIdentity(sessionId, nameof(sessionId));
        AuthorizationFingerprint = NormalizeFingerprint(authorizationFingerprint, nameof(authorizationFingerprint));
        FilterFingerprint = NormalizeFingerprint(filterFingerprint, nameof(filterFingerprint));
        SortFingerprint = NormalizeFingerprint(sortFingerprint, nameof(sortFingerprint));
    }

    /// <summary>Gets the trusted tenant identity.</summary>
    public Guid TenantId { get; }

    /// <summary>Gets the authenticated actor identity.</summary>
    public Guid ActorId { get; }

    /// <summary>Gets the authenticated session identity.</summary>
    public Guid SessionId { get; }

    /// <summary>Gets the lowercase SHA-256 fingerprint of the current effective authorization.</summary>
    public string AuthorizationFingerprint { get; }

    /// <summary>Gets the lowercase SHA-256 fingerprint of the canonical allowlisted filters.</summary>
    public string FilterFingerprint { get; }

    /// <summary>Gets the lowercase SHA-256 fingerprint of the canonical stable ordering.</summary>
    public string SortFingerprint { get; }

    private static Guid RequireIdentity(Guid value, string parameterName)
    {
        if (value == Guid.Empty)
            throw new ArgumentException("Query scopes require nonempty trusted identities.", parameterName);
        return value;
    }

    private static string NormalizeFingerprint(string value, string parameterName)
    {
        if (value is null || value.Length != 64 || value.Any(character => !Uri.IsHexDigit(character)))
            throw new ArgumentException("Query scope fingerprints must be 64 hexadecimal SHA-256 characters.", parameterName);
        return value.ToLowerInvariant();
    }
}
