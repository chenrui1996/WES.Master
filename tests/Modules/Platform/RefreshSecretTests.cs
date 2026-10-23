using Wes.Platform.Features.Identity;
using Xunit;

namespace Wes.Modules.Tests.Platform;

/// <summary>Checks transport credential shape, standard digest semantics and incidental secret formatting.</summary>
public sealed class RefreshSecretTests
{
    /// <summary>Uses the independently published SHA-256 abc vector rather than the code-under-test as oracle.</summary>
    [Fact]
    [Trait("CaseId", "IDENTITY-SECRET-001")]
    public void Digest_UsesCanonicalSha256()
    {
        Assert.Equal("BA7816BF8F01CFEA414140DE5DAE2223B00361A396177A9CB410FF61F20015AD",
            RefreshSecret.DigestOf("abc"));
    }

    /// <summary>Generated values are full-length unpadded URL-safe random credentials, not GUIDs or timestamps.</summary>
    [Fact]
    [Trait("CaseId", "IDENTITY-SECRET-001")]
    public void GeneratedSecrets_HaveIndependentValuesAndSafeFormatting()
    {
        var secrets = Enumerable.Range(0, 32).Select(_ => RefreshSecret.Generate()).ToArray();

        Assert.Equal(32, secrets.Select(item => item.Value).Distinct().Count());
        Assert.All(secrets, secret =>
        {
            Assert.Matches("^[A-Za-z0-9_-]{43}$", secret.Value);
            Assert.Matches("^[A-F0-9]{64}$", secret.Digest);
            Assert.DoesNotContain(secret.Value, secret.ToString());
        });
    }
}
