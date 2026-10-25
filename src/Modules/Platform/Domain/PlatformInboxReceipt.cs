namespace Wes.Platform.Domain;

/// <summary>Owner-local durable proof that one consumer committed an event's effects.</summary>
public sealed class PlatformInboxReceipt
{
    private PlatformInboxReceipt() { }

    internal PlatformInboxReceipt(Guid tenantId, string consumer, Guid eventId, string contractName,
        int schemaVersion, string envelopeDigest, DateTimeOffset processedAt)
    {
        if (tenantId == Guid.Empty || eventId == Guid.Empty || !IsConsumer(consumer)
            || string.IsNullOrWhiteSpace(contractName) || contractName.Length > 128
            || schemaVersion < 1 || !IsDigest(envelopeDigest))
        {
            throw new ArgumentException("Inbox receipt requires valid owner, consumer and event identity.");
        }

        TenantId = tenantId;
        Consumer = consumer;
        EventId = eventId;
        ContractName = contractName;
        SchemaVersion = schemaVersion;
        EnvelopeDigest = envelopeDigest;
        ProcessedAt = processedAt.ToUniversalTime();
    }

    /// <summary>Trusted tenant owner.</summary>
    public Guid TenantId { get; private set; }
    /// <summary>Stable consumer identity within the owner.</summary>
    public string Consumer { get; private set; } = "";
    /// <summary>Stable event identity used to reject duplicate delivery.</summary>
    public Guid EventId { get; private set; }
    /// <summary>Contract identity observed on the first successful delivery.</summary>
    public string ContractName { get; private set; } = "";
    /// <summary>Contract version observed on the first successful delivery.</summary>
    public int SchemaVersion { get; private set; }
    /// <summary>Digest of the stable envelope, used to detect event identity reuse.</summary>
    public string EnvelopeDigest { get; private set; } = "";
    /// <summary>UTC instant at which the owner effect and receipt committed.</summary>
    public DateTimeOffset ProcessedAt { get; private set; }

    internal bool Matches(string contractName, int schemaVersion, string envelopeDigest) =>
        ContractName == contractName && SchemaVersion == schemaVersion && EnvelopeDigest == envelopeDigest;

    private static bool IsConsumer(string value) =>
        !string.IsNullOrEmpty(value) && value.Length <= 128
        && value.All(character => character is >= 'a' and <= 'z'
            or >= '0' and <= '9' or '.' or '-' or '_');

    private static bool IsDigest(string value) =>
        value.Length == 64 && value.All(character => character is >= '0' and <= '9'
            or >= 'a' and <= 'f');
}
