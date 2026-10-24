using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Wes.Platform.Domain;

/// <summary>Owner-supplied canonical account identity; never a raw HTTP account value.</summary>
public sealed record LoginAccountKey
{
    /// <summary>Pseudonymous account identity within a trusted tenant.</summary>
    public string Digest { get; }
    private LoginAccountKey(string digest) => Digest = digest;
    /// <summary>Uses the credential owner's normalized account, not a second normalization policy.</summary>
    public static LoginAccountKey FromNormalizedAccount(string account)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(account);
        return new LoginAccountKey(Convert.ToHexString(SHA256.HashData(new UTF8Encoding(false, true).GetBytes(account))));
    }
    /// <summary>Prevents accidental account digest logging.</summary>
    public override string ToString() => "[LoginAccountKey]";
}

/// <summary>Tenant-owned rolling password failures with the approved challenge and temporary-lock gate.</summary>
public sealed class LoginAccountBudget
{
    private LoginAccountBudget() { }

    /// <summary>Creates risk state without a user lookup, preserving the unknown-account path.</summary>
    public LoginAccountBudget(Guid tenantId, LoginAccountKey key)
    {
        if (tenantId == Guid.Empty) throw new ArgumentException("Account risk requires a trusted tenant.");
        ArgumentNullException.ThrowIfNull(key);
        TenantId = tenantId;
        AccountDigest = key.Digest;
    }

    /// <summary>Trusted tenant boundary.</summary>
    public Guid TenantId { get; private set; }
    /// <summary>Canonical account digest without account-existence dependency.</summary>
    public string AccountDigest { get; private set; } = "";
    /// <summary>Opaque write version.</summary>
    public string Version { get; private set; } = Guid.NewGuid().ToString("D");
    /// <summary>Last committed password failure.</summary>
    public DateTimeOffset? LastFailureAt { get; private set; }
    /// <summary>Temporary lock expiry from the fifth failure.</summary>
    public DateTimeOffset? LockedUntil { get; private set; }
    private string FailuresData { get; set; } = "[]";
    /// <summary>Detached bounded failures.</summary>
    public IReadOnlyList<DateTimeOffset> Failures => ReadFailures();
    /// <summary>Checks risk only; does not authenticate.</summary>
    public LoginGate Evaluate(DateTimeOffset now, bool validChallenge)
    {
        var instant = Normalize(now);
        var failures = ReadFailures();
        if (LastFailureAt > instant)
            throw new InvalidOperationException("Account risk clock moved behind durable failures.");
        return LoginPolicy.EvaluateAccount(failures, LockedUntil, instant, validChallenge);
    }
    /// <summary>Stages one actual password failure only when the gate permits verification.</summary>
    public LoginGate RecordFailure(DateTimeOffset now, bool validChallenge)
    {
        var instant = Normalize(now);
        var gate = Evaluate(instant, validChallenge);
        if (gate != LoginGate.VerifyPassword) return gate;
        var failures = ReadFailures().Where(item => instant - item < TimeSpan.FromMinutes(15)).Append(instant).ToArray();
        var lockedUntil = LoginPolicy.LockAfterFailure(failures, instant);
        FailuresData = JsonSerializer.Serialize(failures);
        LastFailureAt = instant;
        LockedUntil = lockedUntil;
        Version = Guid.NewGuid().ToString("D");
        return gate;
    }

    private static DateTimeOffset Normalize(DateTimeOffset now) =>
        new(now.UtcTicks - now.UtcTicks % 10, TimeSpan.Zero);

    private DateTimeOffset[] ReadFailures()
    {
        DateTimeOffset[]? failures;
        try
        {
            failures = JsonSerializer.Deserialize<DateTimeOffset[]>(FailuresData);
        }
        catch (JsonException)
        {
            throw new InvalidOperationException("Account risk failure schema is invalid.");
        }

        if (failures is null || failures.Length > 5
            || (failures.Length == 0 ? LastFailureAt is not null : LastFailureAt != failures[^1])
            || failures.Any(item => item.Offset != TimeSpan.Zero || item.Ticks % 10 != 0
                || item > LastFailureAt || LastFailureAt - item >= TimeSpan.FromMinutes(15))
            || !failures.SequenceEqual(failures.Order())
            || (failures.Length == 5 ? LockedUntil != LastFailureAt?.AddMinutes(15) : LockedUntil is not null))
        {
            throw new InvalidOperationException("Account risk failure invariants are invalid.");
        }
        return failures;
    }
}
