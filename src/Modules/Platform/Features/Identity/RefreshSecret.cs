using System.Security.Cryptography;
using System.Text;

namespace Wes.Platform.Features.Identity;

/// <summary>A transport-only secret; never attach this object to persistence, logs, or audit events.</summary>
public sealed class RefreshSecret
{
    private RefreshSecret(string value)
    {
        Value = value;
        Digest = DigestOf(value);
    }

    /// <summary>Raw refresh value, disclosed only over the authenticated protected transport.</summary>
    public string Value { get; }
    /// <summary>Uppercase SHA-256 digest accepted by the session aggregate.</summary>
    public string Digest { get; }

    /// <summary>Generates a fresh 256-bit cryptographic refresh value.</summary>
    public static RefreshSecret Generate() => new(Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
        .TrimEnd('=').Replace('+', '-').Replace('/', '_'));

    /// <summary>Hashes a raw input without retaining or reflecting it in diagnostics.</summary>
    public static string DigestOf(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    /// <summary>Prevents incidental formatting from revealing the raw refresh value.</summary>
    public override string ToString() => "RefreshSecret[REDACTED]";
}
