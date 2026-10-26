using System.Text.Json;

namespace Wes.Platform.Domain;

/// <summary>Tenant-local immutable membership for an authorized audit verification query.</summary>
public sealed class PlatformAuditQuerySnapshot
{
    private PlatformAuditQuerySnapshot() { }

    /// <summary>Maximum age of a query snapshot.</summary>
    public const int LifetimeSeconds = 600;
    /// <summary>Largest materialized audit membership accepted by one snapshot.</summary>
    public const int MaximumRows = 10_000;

    /// <summary>Creates an owner-local snapshot from already authorized ordered audit IDs.</summary>
    public PlatformAuditQuerySnapshot(
        Guid tenantId,
        Guid actorId,
        Guid sessionId,
        string authorizationFingerprint,
        string filterFingerprint,
        string sortFingerprint,
        DateTimeOffset from,
        DateTimeOffset to,
        PlatformAuditAction? filterAction,
        Guid? correlationId,
        IReadOnlyCollection<Guid> orderedAuditIds,
        DateTimeOffset createdAt)
    {
        ArgumentNullException.ThrowIfNull(orderedAuditIds);
        if (tenantId == Guid.Empty || actorId == Guid.Empty || sessionId == Guid.Empty
            || !IsFingerprint(authorizationFingerprint) || !IsFingerprint(filterFingerprint) || !IsFingerprint(sortFingerprint)
            || from >= to || (filterAction is not null && !Enum.IsDefined(filterAction.Value))
            || correlationId == Guid.Empty || orderedAuditIds.Count > MaximumRows
            || orderedAuditIds.Any(id => id == Guid.Empty)
            || orderedAuditIds.Distinct().Count() != orderedAuditIds.Count)
        {
            throw new ArgumentException("Audit snapshots require a valid bounded query and unique ordered audit IDs.");
        }

        Id = Guid.NewGuid();
        TenantId = tenantId;
        ActorId = actorId;
        SessionId = sessionId;
        AuthorizationFingerprint = authorizationFingerprint.ToLowerInvariant();
        FilterFingerprint = filterFingerprint.ToLowerInvariant();
        SortFingerprint = sortFingerprint.ToLowerInvariant();
        From = from.ToUniversalTime();
        To = to.ToUniversalTime();
        FilterAction = filterAction;
        CorrelationId = correlationId;
        OrderedAuditIds = JsonSerializer.Serialize(orderedAuditIds);
        Count = orderedAuditIds.Count;
        CreatedAt = createdAt.ToUniversalTime();
        ExpiresAt = CreatedAt.AddSeconds(LifetimeSeconds);
    }

    /// <summary>Snapshot identifier.</summary>
    public Guid Id { get; private set; }
    /// <summary>Trusted tenant owner.</summary>
    public Guid TenantId { get; private set; }
    /// <summary>Actor allowed to continue the snapshot.</summary>
    public Guid ActorId { get; private set; }
    /// <summary>Session to which the snapshot is bound.</summary>
    public Guid SessionId { get; private set; }
    /// <summary>Fingerprint of the effective audit-read grants at creation.</summary>
    public string AuthorizationFingerprint { get; private set; } = "";
    /// <summary>Fingerprint of the canonical audit filters.</summary>
    public string FilterFingerprint { get; private set; } = "";
    /// <summary>Fingerprint of the stable audit ordering.</summary>
    public string SortFingerprint { get; private set; } = "";
    /// <summary>Inclusive lower bound of the captured query.</summary>
    public DateTimeOffset From { get; private set; }
    /// <summary>Exclusive upper bound of the captured query.</summary>
    public DateTimeOffset To { get; private set; }
    /// <summary>Optional finite audit action filter.</summary>
    public PlatformAuditAction? FilterAction { get; private set; }
    /// <summary>Optional exact correlation filter.</summary>
    public Guid? CorrelationId { get; private set; }
    /// <summary>Serialized ordered audit membership, without materialized row values.</summary>
    public string OrderedAuditIds { get; private set; } = "";
    /// <summary>Number of captured audit IDs.</summary>
    public int Count { get; private set; }
    /// <summary>UTC instant when the snapshot was created.</summary>
    public DateTimeOffset CreatedAt { get; private set; }
    /// <summary>UTC instant at which further access is rejected.</summary>
    public DateTimeOffset ExpiresAt { get; private set; }

    internal IReadOnlyList<Guid> ReadOrderedAuditIds()
    {
        var ids = JsonSerializer.Deserialize<Guid[]>(OrderedAuditIds);
        if (ids is null || ids.Length != Count || ids.Length > MaximumRows
            || ids.Any(id => id == Guid.Empty) || ids.Distinct().Count() != ids.Length)
        {
            throw new InvalidOperationException("Persisted audit snapshot membership is invalid.");
        }

        return ids;
    }

    private static bool IsFingerprint(string value) =>
        value is { Length: 64 } && value.All(Uri.IsHexDigit);
}
