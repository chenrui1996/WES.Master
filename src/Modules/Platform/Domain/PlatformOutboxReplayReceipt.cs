namespace Wes.Platform.Domain;

/// <summary>Immutable evidence of one authorized operation requesting an Outbox replay.</summary>
public sealed class PlatformOutboxReplayReceipt
{
    private PlatformOutboxReplayReceipt() { }

    internal PlatformOutboxReplayReceipt(Guid tenantId, Guid operationId, Guid eventId, Guid actorId,
        string reason, int previousAttempts, string fingerprint, DateTimeOffset requestedAt)
    {
        if (tenantId == Guid.Empty || operationId == Guid.Empty || eventId == Guid.Empty || actorId == Guid.Empty
            || !IsReason(reason) || previousAttempts < 1 || !IsFingerprint(fingerprint))
        {
            throw new ArgumentException("Replay evidence requires valid owner, operation, actor, reason and event identity.");
        }

        TenantId = tenantId;
        OperationId = operationId;
        EventId = eventId;
        ActorId = actorId;
        Reason = reason;
        PreviousAttempts = previousAttempts;
        Fingerprint = fingerprint;
        RequestedAt = requestedAt.ToUniversalTime();
    }

    /// <summary>Trusted tenant owner.</summary>
    public Guid TenantId { get; private set; }
    /// <summary>Tenant-wide idempotent management operation key.</summary>
    public Guid OperationId { get; private set; }
    /// <summary>Original stable event identity; replay never creates a replacement business identity.</summary>
    public Guid EventId { get; private set; }
    /// <summary>Authenticated actor who requested the replay.</summary>
    public Guid ActorId { get; private set; }
    /// <summary>Operator-provided bounded reason, never logged or included in transport payloads.</summary>
    public string Reason { get; private set; } = "";
    /// <summary>Attempt count before resetting the bounded automatic retry window.</summary>
    public int PreviousAttempts { get; private set; }
    /// <summary>Fingerprint of the actor, event, expected attempt count and reason.</summary>
    public string Fingerprint { get; private set; } = "";
    /// <summary>UTC instant the replay command committed.</summary>
    public DateTimeOffset RequestedAt { get; private set; }

    internal bool Matches(Guid actorId, Guid eventId, string fingerprint) =>
        ActorId == actorId && EventId == eventId && Fingerprint == fingerprint;

    private static bool IsReason(string value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length is >= 3 and <= 256
        && value.All(character => !char.IsControl(character));

    private static bool IsFingerprint(string value) =>
        value.Length == 64 && value.All(character => character is >= '0' and <= '9'
            or >= 'a' and <= 'f');
}
