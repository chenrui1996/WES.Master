using Wes.Platform.Domain;
using Wes.Platform.Features.Identity;
using Xunit;

namespace Wes.Modules.Tests.Platform;

/// <summary>Shared credential/risk CAS boundaries, with in-flight failures distinct from new admission.</summary>
public sealed class LoginCommitFenceTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 24, 6, 0, 0, TimeSpan.Zero);

    /// <summary>Successful issuance rotates the credential CAS without rewriting password history or its set date.</summary>
    [Fact]
    [Trait("CaseId", "IDENTITY-LOGIN-CONCURRENT-001")]
    public void CredentialFenceAndDisableShareVersionWithoutChangingPassword()
    {
        var credential = new PasswordCredential(Guid.NewGuid(), Guid.NewGuid(), "OPERATOR",
            StoredPasswordHash.Import(PasswordHashScheme.SevenBcryptV1,
                "$2a$12$N9qo8uLOickgx2ZMRZoMyeA6a9XHSXAtYQSvRT4Sc0ziVCqOGHZUS"), Start);
        var version = credential.Version;
        credential.FenceSession(Start.AddSeconds(1));
        Assert.NotEqual(version, credential.Version);
        Assert.Equal(Start, credential.ChangedAt);
        Assert.Single(credential.Recent);
        version = credential.Version;
        credential.SetEnabled(false, Start.AddSeconds(2));
        Assert.False(credential.Enabled);
        Assert.NotEqual(version, credential.Version);
        Assert.Throws<InvalidOperationException>(() => credential.FenceSession(Start.AddSeconds(3)));
        Assert.Throws<InvalidOperationException>(() => credential.SetEnabled(true, Start.AddTicks(-10)));
    }

    /// <summary>Every admitted failure is merged even when another attempt crossed the challenge gate, but the fifth lock is not extended.</summary>
    [Fact]
    [Trait("CaseId", "IDENTITY-LOGIN-CONCURRENT-001")]
    public void ConcurrentAdmittedFailuresSaturateWithoutExtendingLock()
    {
        var budget = new LoginAccountBudget(Guid.NewGuid(), LoginAccountKey.FromNormalizedAccount("OPERATOR"));
        for (var index = 0; index < 7; index++)
            budget.RecordAdmittedFailure(Start.AddSeconds(index));
        Assert.Equal(5, budget.Failures.Count);
        Assert.Equal(Start.AddSeconds(4), budget.LastFailureAt);
        Assert.Equal(Start.AddSeconds(904), budget.LockedUntil);
        Assert.Equal(LoginGate.Locked, budget.Evaluate(Start.AddSeconds(903), true));
        Assert.Equal(LoginGate.VerifyPassword, budget.Evaluate(Start.AddSeconds(904), false));
        budget.RecordAdmittedFailure(Start.AddSeconds(904));
        Assert.Single(budget.Failures);
        Assert.Null(budget.LockedUntil);
        Assert.Throws<InvalidOperationException>(() => budget.RecordAdmittedFailure(Start));
    }

    /// <summary>Correct verification must participate in account CAS; blocked gates do not advance a version.</summary>
    [Fact]
    [Trait("CaseId", "IDENTITY-LOGIN-RISK-001")]
    public void SuccessFencePreservesFailuresAndRequiresCurrentGate()
    {
        var budget = new LoginAccountBudget(Guid.NewGuid(), LoginAccountKey.FromNormalizedAccount("OPERATOR"));
        var version = budget.Version;
        Assert.Equal(LoginGate.VerifyPassword, budget.FenceSuccess(Start, false));
        Assert.NotEqual(version, budget.Version);
        for (var index = 0; index < 3; index++) budget.RecordAdmittedFailure(Start);
        version = budget.Version;
        Assert.Equal(LoginGate.ChallengeRequired, budget.FenceSuccess(Start, false));
        Assert.Equal(version, budget.Version);
        Assert.Equal(LoginGate.VerifyPassword, budget.FenceSuccess(Start, true));
        Assert.NotEqual(version, budget.Version);
        Assert.Equal(3, budget.Failures.Count);
    }
}
