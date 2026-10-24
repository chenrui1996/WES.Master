using System.Net;
using System.Text.Json;
using Wes.Platform.Domain;
using Wes.Platform.Features.Identity;
using Xunit;

namespace Wes.Modules.Tests.Platform;

/// <summary>Keyed protection vectors and secret delivery boundaries independent of the issuing handler.</summary>
public sealed class LoginChallengeProtectionTests
{
    private static readonly Guid Tenant = new("11111111-1111-4111-8111-111111111111");
    private static readonly Guid Id = new("22222222-2222-4222-8222-222222222222");
    private static readonly LoginAccountKey Account = LoginAccountKey.FromNormalizedAccount("UNKNOWN");
    private static readonly LoginSourceKey Source = LoginSourceKey.FromTrustedPeer(IPAddress.Parse("192.0.2.1"));

    /// <summary>Independent PowerShell HMAC fixture catches unkeyed hashing, missing bindings and payload ambiguity.</summary>
    [Fact]
    [Trait("CaseId", "IDENTITY-CHALLENGE-CRYPTO-001")]
    public void MacMatchesIndependentVector_AndBindsAllIdentityParts()
    {
        using var protection = new LoginChallengeProtection(Enumerable.Range(0, 32).Select(item => (byte)item).ToArray());
        var mac = protection.ComputeMac(Tenant, Id, Account, Source, "ABC234");
        Assert.Equal("1CFFFA264870C6E029FF0F69D1C98508DCF9130B8A38AFED34FBCB8273538591", mac);
        Assert.NotEqual(mac, protection.ComputeMac(Guid.NewGuid(), Id, Account, Source, "ABC234"));
        Assert.NotEqual(mac, protection.ComputeMac(Tenant, Guid.NewGuid(), Account, Source, "ABC234"));
        Assert.NotEqual(mac, protection.ComputeMac(Tenant, Id, LoginAccountKey.FromNormalizedAccount("DIFFERENT"), Source, "ABC234"));
        Assert.NotEqual(mac, protection.ComputeMac(Tenant, Id, Account,
            LoginSourceKey.FromTrustedPeer(IPAddress.Parse("192.0.2.2")), "ABC234"));
        Assert.NotEqual(mac, protection.ComputeMac(Tenant, Id, Account, Source, "abc234"));
        using var other = new LoginChallengeProtection(Enumerable.Repeat((byte)1, 32).ToArray());
        Assert.NotEqual(mac, other.ComputeMac(Tenant, Id, Account, Source, "ABC234"));
    }

    /// <summary>A missing/short key never enables a fallback; caller mutation and disposal cannot alter active key identity.</summary>
    [Fact]
    [Trait("CaseId", "IDENTITY-CHALLENGE-CRYPTO-001")]
    public void KeyIsRequiredCopiedAndInvalidAfterDisposal()
    {
        Assert.Throws<ArgumentNullException>(() => new LoginChallengeProtection(null!));
        Assert.Throws<ArgumentException>(() => new LoginChallengeProtection(new byte[31]));
        var bytes = Enumerable.Range(0, 32).Select(item => (byte)item).ToArray();
        var protection = new LoginChallengeProtection(bytes);
        Array.Fill(bytes, (byte)0);
        Assert.Equal("1CFFFA264870C6E029FF0F69D1C98508DCF9130B8A38AFED34FBCB8273538591",
            protection.ComputeMac(Tenant, Id, Account, Source, "ABC234"));
        protection.Dispose();
        protection.Dispose();
        Assert.Throws<ObjectDisposedException>(() => protection.ComputeMac(Tenant, Id, Account, Source, "ABC234"));
    }

    /// <summary>Server renderer delivery cannot accidentally disclose answers via formatting or default JSON serialization.</summary>
    [Fact]
    [Trait("CaseId", "IDENTITY-CHALLENGE-CRYPTO-001")]
    public void DeliveryFormattingAndSerializationExcludeRawAnswer()
    {
        var delivery = new LoginChallengeDelivery(Id, "ABC234", DateTimeOffset.UtcNow);
        Assert.Equal("ABC234", delivery.Answer);
        Assert.DoesNotContain("ABC234", delivery.ToString());
        Assert.DoesNotContain("ABC234", JsonSerializer.Serialize(delivery));
    }

    /// <summary>Invalid owner inputs cannot produce ambiguous MAC payloads or silently replace malformed Unicode.</summary>
    [Fact]
    [Trait("CaseId", "IDENTITY-CHALLENGE-CRYPTO-001")]
    public void MacRejectsInvalidOwnerArgumentsWithoutEchoingAnswer()
    {
        using var protection = new LoginChallengeProtection(new byte[32]);
        Assert.Throws<ArgumentException>(() => protection.ComputeMac(Guid.Empty, Id, Account, Source, "ABC234"));
        Assert.Throws<ArgumentException>(() => protection.ComputeMac(Tenant, Guid.Empty, Account, Source, "ABC234"));
        Assert.Throws<ArgumentNullException>(() => protection.ComputeMac(Tenant, Id, null!, Source, "ABC234"));
        Assert.Throws<ArgumentNullException>(() => protection.ComputeMac(Tenant, Id, Account, null!, "ABC234"));
        Assert.Throws<ArgumentNullException>(() => protection.ComputeMac(Tenant, Id, Account, Source, null!));
        var error = Assert.Throws<ArgumentException>(() => protection.ComputeMac(Tenant, Id, Account, Source, new string('X', 129)));
        Assert.DoesNotContain(new string('X', 129), error.ToString());
        Assert.Throws<System.Text.EncoderFallbackException>(() => protection.ComputeMac(Tenant, Id, Account, Source, "\ud800"));
    }
}
