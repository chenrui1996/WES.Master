using System.Text.Json;
using Wes.Platform.Domain;
using Wes.Platform.Features.Identity;
using Xunit;

namespace Wes.Modules.Tests.Platform;

/// <summary>Seven reference vectors and full-input new-password behavior, not synthetic user-store completion.</summary>
public sealed class PasswordCredentialCodecTests
{
    private const string LegacyFixture = "$2a$12$N9qo8uLOickgx2ZMRZoMyeA6a9XHSXAtYQSvRT4Sc0ziVCqOGHZUS";
    private const string FixturePassword = "WES reference fixture password";

    /// <summary>Golden vector was generated independently through the same unmodified API used by Seven.</summary>
    [Fact]
    [Trait("CaseId", "IDENTITY-CREDENTIAL-LEGACY-001")]
    public void SevenReferenceVerifiesOnlyAsRestrictedLegacy()
    {
        var stored = StoredPasswordHash.Import(PasswordHashScheme.SevenBcryptV1, LegacyFixture);
        Assert.Equal(CredentialVerification.LegacyVerifiedRestricted, PasswordCredentialCodec.Verify(FixturePassword, stored));
        Assert.Equal(CredentialVerification.Invalid, PasswordCredentialCodec.Verify("wrong", stored));
        var mislabelled = StoredPasswordHash.Import(PasswordHashScheme.BcryptSha384V1, LegacyFixture);
        Assert.Equal(CredentialVerification.Invalid, PasswordCredentialCodec.Verify(FixturePassword, mislabelled));
    }

    /// <summary>Legacy truncation remains explicit and restricted instead of being silently reinterpreted as the new mode.</summary>
    [Fact]
    [Trait("CaseId", "IDENTITY-CREDENTIAL-LEGACY-001")]
    public void LegacySeventyTwoByteAliasIsCompatibilityOnly()
    {
        const string reference = "$2a$12$N9qo8uLOickgx2ZMRZoMyeHwZf6TiCLhf89ZyJUF69joEzGe9b1m6";
        var stored = StoredPasswordHash.Import(PasswordHashScheme.SevenBcryptV1, reference);
        var prefix = new string('x', 72);
        Assert.Equal(CredentialVerification.LegacyVerifiedRestricted, PasswordCredentialCodec.Verify(prefix + "A", stored));
        Assert.Equal(CredentialVerification.LegacyVerifiedRestricted, PasswordCredentialCodec.Verify(prefix + "B", stored));
    }

    /// <summary>PowerShell/net462 enhanced-mode vector pins SHA384 semantics independently of the new codec's create path.</summary>
    [Fact]
    [Trait("CaseId", "IDENTITY-CREDENTIAL-FORMAT-001")]
    public void EnhancedFormatMatchesCrossRuntimeReference()
    {
        const string reference = "$2a$12$N9qo8uLOickgx2ZMRZoMyebq.QrfAcQ2j7P1iiG.cM44tPZp1iuQS";
        var stored = StoredPasswordHash.Import(PasswordHashScheme.BcryptSha384V1, reference);
        Assert.Equal(CredentialVerification.Verified, PasswordCredentialCodec.Verify(FixturePassword, stored));
        Assert.Equal(CredentialVerification.Invalid, PasswordCredentialCodec.Verify(FixturePassword + " ", stored));
    }

    /// <summary>Each accepted new candidate obtains fresh salt and its complete enhanced-mode hash, not a legacy alias.</summary>
    [Fact]
    [Trait("CaseId", "IDENTITY-CREDENTIAL-FORMAT-001")]
    public void NewHashUsesRandomSaltAndExplicitEnhancedFormat()
    {
        var first = Create(FixturePassword);
        var second = Create(FixturePassword);
        Assert.Equal(PasswordDecision.Accepted, first.Decision);
        Assert.Equal(PasswordHashScheme.BcryptSha384V1, first.Hash!.Scheme);
        Assert.NotEqual(first.Hash.EncodedHash, second.Hash!.EncodedHash);
        Assert.Matches(@"^\$2[aby]\$12\$", first.Hash.EncodedHash);
        Assert.Equal(CredentialVerification.Verified, PasswordCredentialCodec.Verify(FixturePassword, first.Hash));
        Assert.Equal(CredentialVerification.Invalid, PasswordCredentialCodec.Verify("wrong", first.Hash));
        var wrongMode = StoredPasswordHash.Import(PasswordHashScheme.SevenBcryptV1, first.Hash.EncodedHash);
        Assert.Equal(CredentialVerification.Invalid, PasswordCredentialCodec.Verify(FixturePassword, wrongMode));
    }

    /// <summary>Changing only bytes past BCrypt's old 72-byte boundary must change new credential verification.</summary>
    [Fact]
    [Trait("CaseId", "IDENTITY-CREDENTIAL-UNICODE-001")]
    public void NewFormatDistinguishesSuffixPastSeventyTwoBytes()
    {
        var prefix = new string('x', 72);
        var stored = Create(prefix + "A").Hash!;
        Assert.Equal(CredentialVerification.Verified, PasswordCredentialCodec.Verify(prefix + "A", stored));
        Assert.Equal(CredentialVerification.Invalid, PasswordCredentialCodec.Verify(prefix + "B", stored));
    }

    /// <summary>128 Unicode scalars can exceed 72 bytes; the last non-BMP scalar cannot be discarded or normalized.</summary>
    [Fact]
    [Trait("CaseId", "IDENTITY-CREDENTIAL-UNICODE-001")]
    public void FullNonBmpScalarLimitIsNotTruncated()
    {
        var rocket = char.ConvertFromUtf32(0x1F680);
        var password = string.Concat(Enumerable.Repeat(rocket, 128));
        var stored = Create(password).Hash!;
        Assert.Equal(CredentialVerification.Verified, PasswordCredentialCodec.Verify(password, stored));
        Assert.Equal(CredentialVerification.Invalid,
            PasswordCredentialCodec.Verify(string.Concat(Enumerable.Repeat(rocket, 127)) + "X", stored));
    }

    /// <summary>Whitespace and canonical-equivalent Unicode are password data, never trimmed or normalized by the codec.</summary>
    [Theory]
    [InlineData("  Long accepted password  ", "Long accepted password")]
    [InlineData("Long accepted password \u00e9", "Long accepted password e\u0301")]
    [Trait("CaseId", "IDENTITY-CREDENTIAL-UNICODE-001")]
    public void VerificationPreservesExactInput(string original, string altered)
    {
        var stored = Create(original).Hash!;
        Assert.Equal(CredentialVerification.Verified, PasswordCredentialCodec.Verify(original, stored));
        Assert.Equal(CredentialVerification.Invalid, PasswordCredentialCodec.Verify(altered, stored));
    }

    /// <summary>Missing/prohibited evidence cannot be converted into a usable hash even for an otherwise valid shape.</summary>
    [Theory]
    [InlineData(PasswordEvidence.Unknown, PasswordEvidence.Clear, PasswordDecision.EvidenceUnavailable)]
    [InlineData(PasswordEvidence.Clear, PasswordEvidence.Unknown, PasswordDecision.EvidenceUnavailable)]
    [InlineData(PasswordEvidence.Prohibited, PasswordEvidence.Clear, PasswordDecision.Compromised)]
    [InlineData(PasswordEvidence.Clear, PasswordEvidence.Prohibited, PasswordDecision.RecentlyUsed)]
    [Trait("CaseId", "IDENTITY-CREDENTIAL-POLICY-001")]
    public void EvidenceFailuresDoNotProduceHash(PasswordEvidence breach, PasswordEvidence history, PasswordDecision expected)
    {
        var result = PasswordCredentialCodec.Create(FixturePassword, "operator", breach, history);
        Assert.Equal(expected, result.Decision);
        Assert.Null(result.Hash);
    }

    /// <summary>Policy-invalid shape/account candidates never reach an expensive password hash operation.</summary>
    [Theory]
    [InlineData(14, PasswordDecision.InvalidShape)]
    [InlineData(129, PasswordDecision.InvalidShape)]
    [InlineData(20, PasswordDecision.MatchesAccount)]
    [Trait("CaseId", "IDENTITY-CREDENTIAL-POLICY-001")]
    public void ShapeAndAccountRejectionsDoNotProduceHash(int length, PasswordDecision expected)
    {
        var password = new string('x', length);
        var result = PasswordCredentialCodec.Create(password, length == 20 ? password : "operator",
            PasswordEvidence.Clear, PasswordEvidence.Clear);
        Assert.Equal(expected, result.Decision);
        Assert.Null(result.Hash);
    }

    /// <summary>Stored configuration errors are explicit and cannot initiate arbitrary-cost or fallback verification.</summary>
    [Fact]
    [Trait("CaseId", "IDENTITY-CREDENTIAL-FORMAT-001")]
    public void InvalidSchemeOrEncodingFailsWithoutReflectingHash()
    {
        Assert.Throws<ArgumentException>(() => StoredPasswordHash.Import((PasswordHashScheme)99, LegacyFixture));
        Assert.Throws<ArgumentException>(() => StoredPasswordHash.Import(PasswordHashScheme.SevenBcryptV1, "secret-marker"));
        Assert.Throws<ArgumentException>(() => StoredPasswordHash.Import(PasswordHashScheme.SevenBcryptV1,
            LegacyFixture.Replace("$12$", "$31$", StringComparison.Ordinal)));
        Assert.Throws<ArgumentNullException>(() => StoredPasswordHash.Import(PasswordHashScheme.SevenBcryptV1, null!));
        var error = Assert.Throws<ArgumentException>(() => StoredPasswordHash.Import(PasswordHashScheme.SevenBcryptV1, "secret-marker"));
        Assert.DoesNotContain("secret-marker", error.ToString());
        var invalidSalt = LegacyFixture.ToCharArray();
        invalidSalt[28] = 'f';
        Assert.Throws<ArgumentException>(() => StoredPasswordHash.Import(PasswordHashScheme.SevenBcryptV1, new string(invalidSalt)));
        var invalidChecksum = LegacyFixture.ToCharArray();
        invalidChecksum[59] = 'T';
        Assert.Throws<ArgumentException>(() => StoredPasswordHash.Import(PasswordHashScheme.SevenBcryptV1, new string(invalidChecksum)));
    }

    /// <summary>Formatting/serialization cannot expose hashes; malformed caller Unicode is a rejection, not replacement encoding.</summary>
    [Fact]
    [Trait("CaseId", "IDENTITY-CREDENTIAL-FORMAT-001")]
    public void HashIsRedactedAndMalformedCandidateDoesNotVerify()
    {
        var stored = StoredPasswordHash.Import(PasswordHashScheme.SevenBcryptV1, LegacyFixture);
        Assert.DoesNotContain(LegacyFixture, stored.ToString());
        Assert.DoesNotContain(LegacyFixture, JsonSerializer.Serialize(stored));
        Assert.Equal(CredentialVerification.Invalid, PasswordCredentialCodec.Verify("\ud800", stored));
        Assert.Throws<ArgumentNullException>(() => PasswordCredentialCodec.Verify(null!, stored));
        Assert.Throws<ArgumentNullException>(() => PasswordCredentialCodec.Verify(FixturePassword, null!));
    }

    private static NewPasswordHashResult Create(string password) =>
        PasswordCredentialCodec.Create(password, "operator", PasswordEvidence.Clear, PasswordEvidence.Clear);
}
