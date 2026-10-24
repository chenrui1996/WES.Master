using Wes.Platform.Domain;
using Xunit;

namespace Wes.Modules.Tests.Platform;

/// <summary>PAR-02 account transitions independent of account existence or transport responses.</summary>
public sealed class LoginAccountBudgetTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 24, 2, 0, 0, TimeSpan.Zero);

    /// <summary>Only actual password failures count; challenge and lock rejections leave state unchanged.</summary>
    [Fact]
    [Trait("CaseId", "IDENTITY-ACCOUNT-WINDOW-001")]
    public void ThirdFailureRequiresChallenge_AndFifthLocksWithoutExtending()
    {
        var budget = NewBudget();
        for (var index = 0; index < 3; index++)
            Assert.Equal(LoginGate.VerifyPassword, budget.RecordFailure(Start, false));
        Assert.Equal(LoginGate.ChallengeRequired, budget.Evaluate(Start, false));
        var version = budget.Version;
        Assert.Equal(LoginGate.ChallengeRequired, budget.RecordFailure(Start, false));
        Assert.Equal(version, budget.Version);
        Assert.Equal(3, budget.Failures.Count);
        Assert.Equal(LoginGate.VerifyPassword, budget.RecordFailure(Start, true));
        Assert.Equal(LoginGate.VerifyPassword, budget.RecordFailure(Start, true));
        Assert.Equal(LoginGate.Locked, budget.Evaluate(Start, true));
        var lockedVersion = budget.Version;
        Assert.Equal(LoginGate.Locked, budget.RecordFailure(Start.AddSeconds(899), true));
        Assert.Equal(lockedVersion, budget.Version);
        Assert.Equal(Start.AddSeconds(900), budget.LockedUntil);
        Assert.Equal(5, budget.Failures.Count);
    }

    /// <summary>The lock is measured from the fifth failure, not from the oldest rolling observation.</summary>
    [Fact]
    [Trait("CaseId", "IDENTITY-ACCOUNT-LOCK-001")]
    public void LockExpiresAtNineHundredSecondsFromFifth_AndWindowIsRolling()
    {
        var budget = NewBudget();
        for (var index = 0; index < 4; index++) budget.RecordFailure(Start, true);
        budget.RecordFailure(Start.AddSeconds(899), true);
        Assert.Equal(LoginGate.Locked, budget.Evaluate(Start.AddSeconds(1799).AddTicks(-10), true));
        Assert.Equal(LoginGate.VerifyPassword, budget.Evaluate(Start.AddSeconds(1799), false));
        budget.RecordFailure(Start.AddSeconds(1799), false);
        Assert.Single(budget.Failures);
        Assert.Null(budget.LockedUntil);
    }

    /// <summary>A calendar boundary cannot erase three recent failures or keep them beyond the rolling deadline.</summary>
    [Fact]
    [Trait("CaseId", "IDENTITY-ACCOUNT-WINDOW-001")]
    public void ChallengeWindowIsRollingAndExpiresExactlyAtNineHundredSeconds()
    {
        var budget = NewBudget();
        for (var index = 0; index < 3; index++) budget.RecordFailure(Start.AddSeconds(899), false);
        Assert.Equal(LoginGate.ChallengeRequired, budget.Evaluate(Start.AddSeconds(900), false));
        Assert.Equal(LoginGate.ChallengeRequired, budget.Evaluate(Start.AddSeconds(1799).AddTicks(-10), false));
        Assert.Equal(LoginGate.VerifyPassword, budget.Evaluate(Start.AddSeconds(1799), false));
    }

    /// <summary>Persistable precision and detached snapshots cannot be used to rewrite the failure window.</summary>
    [Fact]
    [Trait("CaseId", "IDENTITY-ACCOUNT-STATE-001")]
    public void UtcMicrosecondsAndClockReversalProtectDurableState()
    {
        var budget = NewBudget();
        budget.RecordFailure(Start.ToOffset(TimeSpan.FromHours(8)).AddTicks(19), false);
        Assert.Equal(Start.AddTicks(10), budget.LastFailureAt);
        var snapshot = Assert.IsType<DateTimeOffset[]>(budget.Failures);
        snapshot[0] = Start.AddDays(-2);
        Assert.Equal(Start.AddTicks(10), Assert.Single(budget.Failures));
        var version = budget.Version;
        Assert.Throws<InvalidOperationException>(() => budget.Evaluate(Start, true));
        Assert.Throws<InvalidOperationException>(() => budget.RecordFailure(Start, true));
        Assert.Equal(version, budget.Version);
    }

    /// <summary>No account existence or original account text is part of the persisted identity.</summary>
    [Fact]
    [Trait("CaseId", "IDENTITY-ACCOUNT-DB-001")]
    public void KeyRequiresTrustedNormalizedAccount_AndTenantCannotBeEmpty()
    {
        var key = LoginAccountKey.FromNormalizedAccount("USER-001");
        Assert.Equal(key, LoginAccountKey.FromNormalizedAccount("USER-001"));
        Assert.NotEqual(key, LoginAccountKey.FromNormalizedAccount("USER-002"));
        Assert.Matches("^[0-9A-F]{64}$", key.Digest);
        Assert.Equal("4ADDDCB0A5CF900AF0CD31FB963DF2F470E88D2DDC70390A17B6CBF126A0B32A", key.Digest);
        Assert.DoesNotContain(key.Digest, key.ToString());
        Assert.Throws<ArgumentException>(() => LoginAccountKey.FromNormalizedAccount(""));
        Assert.Throws<ArgumentException>(() => LoginAccountKey.FromNormalizedAccount("  "));
        Assert.Throws<ArgumentNullException>(() => LoginAccountKey.FromNormalizedAccount(null!));
        Assert.Throws<System.Text.EncoderFallbackException>(() => LoginAccountKey.FromNormalizedAccount("\ud800"));
        Assert.Throws<ArgumentException>(() => new LoginAccountBudget(Guid.Empty, key));
        Assert.Throws<ArgumentNullException>(() => new LoginAccountBudget(Guid.NewGuid(), null!));
    }

    private static LoginAccountBudget NewBudget() =>
        new(Guid.NewGuid(), LoginAccountKey.FromNormalizedAccount("UNKNOWN-ACCOUNT"));
}
