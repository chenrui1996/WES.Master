using System.Security.Cryptography;

namespace Wes.Platform.Domain;

/// <summary>Internal challenge decision, never successful authentication.</summary>
public enum ChallengeDecision
{
    /// <summary>A correct bound answer was consumed exactly once.</summary>
    Accepted,
    /// <summary>Generic rejection without disclosing challenge existence or binding.</summary>
    Rejected
}

/// <summary>Tenant-owned one-shot challenge protected by a server-keyed answer MAC.</summary>
public sealed class LoginChallenge
{
    private LoginChallenge() { }

    /// <summary>Creates a bound challenge; raw answers do not belong in the entity.</summary>
    public LoginChallenge(Guid tenantId, Guid id, LoginAccountKey account, LoginSourceKey source,
        string answerMac, DateTimeOffset createdAt)
    {
        if (tenantId == Guid.Empty || id == Guid.Empty)
            throw new ArgumentException("Challenge requires trusted tenant and unpredictable identity.");
        ArgumentNullException.ThrowIfNull(account);
        ArgumentNullException.ThrowIfNull(source);
        if (!IsDigest(answerMac)) throw new ArgumentException("Challenge requires a protected answer.");
        TenantId = tenantId;
        Id = id;
        AccountDigest = account.Digest;
        SourceDigest = source.Digest;
        AnswerMac = answerMac;
        CreatedAt = Normalize(createdAt);
        _ = CreatedAt.AddSeconds(120);
    }
    /// <summary>Trusted tenant.</summary>
    public Guid TenantId { get; private set; }
    /// <summary>Unpredictable challenge identifier.</summary>
    public Guid Id { get; private set; }
    /// <summary>Owner-normalized account binding.</summary>
    public string AccountDigest { get; private set; } = "";
    /// <summary>Trusted peer binding.</summary>
    public string SourceDigest { get; private set; } = "";
    /// <summary>Server-keyed answer authenticator, not a naked short-answer hash.</summary>
    public string AnswerMac { get; private set; } = "";
    /// <summary>Opaque CAS version.</summary>
    public string Version { get; private set; } = Guid.NewGuid().ToString("D");
    /// <summary>UTC creation instant.</summary>
    public DateTimeOffset CreatedAt { get; private set; }
    /// <summary>Exclusive approved expiry.</summary>
    public DateTimeOffset ExpiresAt => CreatedAt.AddSeconds(120);
    /// <summary>First bound submission instant, including wrong answers.</summary>
    public DateTimeOffset? ConsumedAt { get; private set; }
    /// <summary>Stages one bound submission; the caller must commit before using Accepted.</summary>
    public ChallengeDecision Consume(LoginAccountKey account, LoginSourceKey source, string candidateMac, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(account);
        ArgumentNullException.ThrowIfNull(source);
        if (!IsDigest(candidateMac)) throw new ArgumentException("Challenge requires a protected candidate.");
        var instant = Normalize(now);
        if (TenantId == Guid.Empty || Id == Guid.Empty || !IsDigest(AccountDigest)
            || !IsDigest(SourceDigest) || !IsDigest(AnswerMac)
            || !Guid.TryParseExact(Version, "D", out var version) || version == Guid.Empty
            || CreatedAt.Offset != TimeSpan.Zero || CreatedAt.Ticks % 10 != 0
            || CreatedAt > DateTimeOffset.MaxValue.AddSeconds(-120)
            || ConsumedAt.HasValue && (ConsumedAt.Value.Offset != TimeSpan.Zero
                || ConsumedAt.Value.Ticks % 10 != 0 || ConsumedAt < CreatedAt || ConsumedAt >= ExpiresAt))
            throw new InvalidOperationException("Challenge state is invalid.");
        if (instant < CreatedAt || instant < ConsumedAt)
            throw new InvalidOperationException("Challenge clock moved behind durable state.");
        if (AccountDigest != account.Digest || SourceDigest != source.Digest || ConsumedAt is not null || instant >= ExpiresAt)
            return ChallengeDecision.Rejected;

        var matches = CryptographicOperations.FixedTimeEquals(Convert.FromHexString(AnswerMac), Convert.FromHexString(candidateMac));
        ConsumedAt = instant;
        Version = Guid.NewGuid().ToString("D");
        return matches ? ChallengeDecision.Accepted : ChallengeDecision.Rejected;
    }

    private static DateTimeOffset Normalize(DateTimeOffset instant) =>
        new(instant.UtcTicks - instant.UtcTicks % 10, TimeSpan.Zero);

    private static bool IsDigest(string? digest) =>
        digest is { Length: 64 } && digest.All(character => character is >= '0' and <= '9' or >= 'A' and <= 'F');
}
