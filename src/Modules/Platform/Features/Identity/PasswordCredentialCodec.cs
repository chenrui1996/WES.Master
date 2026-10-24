using System.Buffers;
using System.Text;
using System.Text.Json.Serialization;
using Wes.Platform.Domain;

namespace Wes.Platform.Features.Identity;

/// <summary>Explicit stored format; BCrypt prefixes alone cannot distinguish the two modes.</summary>
public enum PasswordHashScheme
{
    /// <summary>Unmodified Seven BCrypt/work12 verification, restricted until controlled password change.</summary>
    SevenBcryptV1,
    /// <summary>Full-input SHA384 enhanced BCrypt/work12 for new approved credentials.</summary>
    BcryptSha384V1
}

/// <summary>Internal verification only; no value grants identity, access or production-write permission.</summary>
public enum CredentialVerification
{
    /// <summary>The candidate did not verify.</summary>
    Invalid,
    /// <summary>A new-format credential verified; current risk/account/session checks are still required.</summary>
    Verified,
    /// <summary>A compatible legacy credential verified but controlled reset/write restriction remains required.</summary>
    LegacyVerifiedRestricted
}

/// <summary>Secret owner-local encoded hash with explicit scheme, not an HTTP DTO.</summary>
public sealed class StoredPasswordHash
{
    private const string BcryptAlphabet = "./ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";
    private StoredPasswordHash(PasswordHashScheme scheme, string encodedHash)
    {
        Scheme = scheme;
        EncodedHash = encodedHash;
    }
    /// <summary>Trusted persisted format metadata, never guessed from a hash prefix.</summary>
    public PasswordHashScheme Scheme { get; }
    /// <summary>Secret hash for explicit owner persistence only.</summary>
    [JsonIgnore]
    public string EncodedHash { get; }
    /// <summary>Imports only a reviewed version and supported canonical cost/encoding.</summary>
    public static StoredPasswordHash Import(PasswordHashScheme scheme, string encodedHash)
    {
        ArgumentNullException.ThrowIfNull(encodedHash);
        if (!Enum.IsDefined(scheme) || encodedHash.Length != 60
            || encodedHash[0] != '$' || encodedHash[1] != '2' || encodedHash[2] is not ('a' or 'b' or 'y')
            || !encodedHash.AsSpan(3, 4).SequenceEqual("$12$")
            || !encodedHash.Skip(7).All(BcryptAlphabet.Contains)
            || BcryptAlphabet.IndexOf(encodedHash[28], StringComparison.Ordinal) % 16 != 0
            || BcryptAlphabet.IndexOf(encodedHash[59], StringComparison.Ordinal) % 4 != 0)
            throw new ArgumentException("Credential format or cost is not supported by the approved codec.");
        return new StoredPasswordHash(scheme, encodedHash);
    }
    /// <summary>Prevents incidental secret formatting.</summary>
    public override string ToString() => "StoredPasswordHash[REDACTED]";
}

/// <summary>Policy result and optional owner-local hash; a rejected policy never produces a hash.</summary>
public sealed record NewPasswordHashResult(PasswordDecision Decision, StoredPasswordHash? Hash);

/// <summary>Maintained-library adapter preserving Seven verification without truncating new Unicode passwords.</summary>
public static class PasswordCredentialCodec
{
    /// <summary>Requires approved breach/history evidence and hashes only a policy-accepted complete candidate.</summary>
    public static NewPasswordHashResult Create(string password, string account, PasswordEvidence breach, PasswordEvidence history)
    {
        var decision = PasswordPolicy.Evaluate(password, account, breach, history);
        if (decision != PasswordDecision.Accepted) return new NewPasswordHashResult(decision, null);
        var hash = BCrypt.Net.BCrypt.EnhancedHashPassword(password, 12, BCrypt.Net.HashType.SHA384);
        return new NewPasswordHashResult(decision, StoredPasswordHash.Import(PasswordHashScheme.BcryptSha384V1, hash));
    }
    /// <summary>Verifies one explicit scheme without normalizing input or trying alternate modes on failure.</summary>
    public static CredentialVerification Verify(string password, StoredPasswordHash stored)
    {
        ArgumentNullException.ThrowIfNull(password);
        ArgumentNullException.ThrowIfNull(stored);
        var remaining = password.AsSpan();
        while (!remaining.IsEmpty)
        {
            if (Rune.DecodeFromUtf16(remaining, out _, out var consumed) != OperationStatus.Done)
                return CredentialVerification.Invalid;
            remaining = remaining[consumed..];
        }
        try
        {
            var matches = stored.Scheme == PasswordHashScheme.SevenBcryptV1
                ? BCrypt.Net.BCrypt.Verify(password, stored.EncodedHash)
                : BCrypt.Net.BCrypt.EnhancedVerify(password, stored.EncodedHash, BCrypt.Net.HashType.SHA384);
            return !matches ? CredentialVerification.Invalid
                : stored.Scheme == PasswordHashScheme.SevenBcryptV1
                    ? CredentialVerification.LegacyVerifiedRestricted : CredentialVerification.Verified;
        }
        catch (BCrypt.Net.SaltParseException)
        {
            throw new InvalidOperationException("Stored credential cannot be parsed by the approved codec.");
        }
    }
}
