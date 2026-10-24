using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using Wes.Platform.Domain;

namespace Wes.Platform.Features.Identity;

/// <summary>Server-keyed challenge answer protection; the owner supplies an approved shared secret, never a default key.</summary>
public sealed class LoginChallengeProtection : IDisposable
{
    private readonly byte[] _key;
    private bool _disposed;

    /// <summary>Requires at least 256 bits of deployment-provided secret material.</summary>
    public LoginChallengeProtection(byte[] key)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (key.Length < 32) throw new ArgumentException("Challenge protection requires an approved secret of at least 256 bits.");
        _key = (byte[])key.Clone();
    }
    /// <summary>Binds an answer MAC to tenant, challenge, normalized account and trusted peer.</summary>
    public string ComputeMac(Guid tenantId, Guid id, LoginAccountKey account, LoginSourceKey source, string answer)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (tenantId == Guid.Empty || id == Guid.Empty) throw new ArgumentException("Challenge protection requires a trusted binding.");
        ArgumentNullException.ThrowIfNull(account);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(answer);
        if (answer.Length > 128) throw new ArgumentException("Challenge protection input exceeds the internal bound.");
        var payload = new UTF8Encoding(false, true).GetBytes(
            $"wes-login-challenge-v1\n{tenantId:D}\n{id:D}\n{account.Digest}\n{source.Digest}\n{answer}");
        try
        {
            return Convert.ToHexString(HMACSHA256.HashData(_key, payload));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(payload);
        }
    }
    /// <summary>Erases the owned key and prevents further use.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        CryptographicOperations.ZeroMemory(_key);
        _disposed = true;
    }
}

/// <summary>Server-renderer-only delivery; not an HTTP response, audit payload or persistence entity.</summary>
public sealed class LoginChallengeDelivery(Guid id, string answer, DateTimeOffset expiresAt)
{
    /// <summary>Opaque reference safe for the eventual challenge response.</summary>
    public Guid Id { get; } = id;
    /// <summary>Secret available only to the trusted server renderer.</summary>
    [JsonIgnore]
    public string Answer { get; } = answer;
    /// <summary>Exclusive expiry of the committed challenge.</summary>
    public DateTimeOffset ExpiresAt { get; } = expiresAt;
    /// <summary>Redacts incidental formatting.</summary>
    public override string ToString() => "LoginChallengeDelivery[REDACTED]";
}
