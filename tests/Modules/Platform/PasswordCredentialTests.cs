using System.Text.Json;
using Wes.Platform.Domain;
using Wes.Platform.Features.Identity;
using Xunit;

namespace Wes.Modules.Tests.Platform;

/// <summary>Owner history semantics with actual approved BCrypt modes, not string equality or a mocked hasher.</summary>
public sealed class PasswordCredentialTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 24, 5, 0, 0, TimeSpan.Zero);
    private const string Initial = "WES reference fixture password";
    private const string Legacy = "$2a$12$N9qo8uLOickgx2ZMRZoMyeA6a9XHSXAtYQSvRT4Sc0ziVCqOGHZUS";

    /// <summary>The current value counts among five; the sixth old value exits only after a successful set.</summary>
    [Fact]
    [Trait("CaseId", "IDENTITY-PASSWORD-HISTORY-001")]
    public void HistoryUsesRealVerificationAndKeepsLatestFiveIncludingCurrent()
    {
        var credential = New();
        Assert.Equal(CredentialVerification.LegacyVerifiedRestricted, credential.Verify(Initial));
        Assert.Equal(PasswordEvidence.Prohibited, credential.CheckHistory(Initial));
        Assert.Equal(PasswordDecision.RecentlyUsed, credential.Change(Initial, PasswordEvidence.Clear, Start));
        for (var index = 1; index <= 5; index++)
        {
            var before = credential.Version;
            Assert.Equal(PasswordDecision.Accepted, credential.Change($"new reviewed password {index}", PasswordEvidence.Clear, Start.AddSeconds(index)));
            Assert.NotEqual(before, credential.Version);
            Assert.Equal(Math.Min(index + 1, 5), credential.Recent.Count);
        }
        Assert.Equal(PasswordEvidence.Clear, credential.CheckHistory(Initial));
        Assert.Equal(PasswordEvidence.Prohibited, credential.CheckHistory("new reviewed password 1"));
        Assert.Equal(PasswordEvidence.Prohibited, credential.CheckHistory("new reviewed password 5"));
        Assert.Equal(CredentialVerification.Verified, credential.Verify("new reviewed password 5"));
        Assert.All(credential.Recent, item => Assert.Equal(PasswordHashScheme.BcryptSha384V1, item.Scheme));
    }

    /// <summary>Policy failures never rotate a version, clear a legacy gate or prune any history.</summary>
    [Theory]
    [InlineData(PasswordEvidence.Unknown, PasswordDecision.EvidenceUnavailable)]
    [InlineData(PasswordEvidence.Prohibited, PasswordDecision.Compromised)]
    [Trait("CaseId", "IDENTITY-PASSWORD-HISTORY-001")]
    public void MissingOrProhibitedBreachEvidenceLeavesStateUnchanged(PasswordEvidence breach, PasswordDecision expected)
    {
        var credential = New();
        var version = credential.Version;
        Assert.Equal(expected, credential.Change("new reviewed password", breach, Start.AddSeconds(1)));
        Assert.Equal(version, credential.Version);
        Assert.Single(credential.Recent);
        Assert.Equal(Start, credential.ChangedAt);
        Assert.Equal(CredentialVerification.LegacyVerifiedRestricted, credential.Verify(Initial));
    }

    /// <summary>Owner references and chronology cannot be silently repaired by serialization or a replacement default.</summary>
    [Fact]
    [Trait("CaseId", "IDENTITY-PASSWORD-FAULT-001")]
    public void InvalidOwnersAndClockAreExplicit()
    {
        var hash = StoredPasswordHash.Import(PasswordHashScheme.SevenBcryptV1, Legacy);
        Assert.Throws<ArgumentException>(() => new PasswordCredential(Guid.Empty, Guid.NewGuid(), "OPERATOR", hash, Start));
        Assert.Throws<ArgumentException>(() => new PasswordCredential(Guid.NewGuid(), Guid.Empty, "OPERATOR", hash, Start));
        Assert.Throws<ArgumentNullException>(() => new PasswordCredential(Guid.NewGuid(), Guid.NewGuid(), "OPERATOR", null!, Start));
        Assert.Throws<InvalidOperationException>(() => New().Change("new reviewed password", PasswordEvidence.Clear, Start.AddTicks(-10)));
    }

    /// <summary>Entity JSON and formatting never expose an account, hash, history or password.</summary>
    [Fact]
    [Trait("CaseId", "IDENTITY-PASSWORD-HISTORY-001")]
    public void DefaultOutputsAndDetachedViewsContainNoCredentialSecrets()
    {
        var credential = New();
        var recent = credential.Recent;
        Assert.Single(recent);
        Assert.Equal(PasswordDecision.Accepted, credential.Change("new reviewed password", PasswordEvidence.Clear, Start));
        Assert.Single(recent);
        var output = JsonSerializer.Serialize(credential) + credential;
        Assert.DoesNotContain(Initial, output);
        Assert.DoesNotContain(Legacy, output);
        Assert.DoesNotContain("OPERATOR", output);
        Assert.DoesNotContain("HistoryData", output);
    }

    /// <summary>Every imported revision advertised by the codec must actually verify, not merely pass a format check.</summary>
    [Theory]
    [InlineData("$2b$")]
    [InlineData("$2y$")]
    [Trait("CaseId", "IDENTITY-CREDENTIAL-FORMAT-001")]
    public void ImportedSupportedRevisionsVerifyInTheirExplicitModes(string revision)
    {
        var legacy = StoredPasswordHash.Import(PasswordHashScheme.SevenBcryptV1, Legacy.Replace("$2a$", revision, StringComparison.Ordinal));
        const string enhanced = "$2a$12$N9qo8uLOickgx2ZMRZoMyebq.QrfAcQ2j7P1iiG.cM44tPZp1iuQS";
        var modern = StoredPasswordHash.Import(PasswordHashScheme.BcryptSha384V1, enhanced.Replace("$2a$", revision, StringComparison.Ordinal));
        Assert.Equal(CredentialVerification.LegacyVerifiedRestricted, PasswordCredentialCodec.Verify(Initial, legacy));
        Assert.Equal(CredentialVerification.Verified, PasswordCredentialCodec.Verify(Initial, modern));
    }

    private static PasswordCredential New() => new(Guid.NewGuid(), Guid.NewGuid(), "OPERATOR",
        StoredPasswordHash.Import(PasswordHashScheme.SevenBcryptV1, Legacy), Start);
}
