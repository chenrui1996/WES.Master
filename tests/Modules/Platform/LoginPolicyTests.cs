using Wes.Platform.Domain;
using Xunit;

namespace Wes.Modules.Tests.Platform;

/// <summary>Literal rolling-window boundary tests, independent of any authentication store or proxy headers.</summary>
public sealed class LoginPolicyTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 23, 12, 30, 0, TimeSpan.Zero);

    /// <summary>Three prior failures trigger a challenge; valid challenge permits only password verification.</summary>
    [Theory]
    [InlineData(2, false, LoginGate.VerifyPassword)]
    [InlineData(3, false, LoginGate.ChallengeRequired)]
    [InlineData(3, true, LoginGate.VerifyPassword)]
    [InlineData(4, false, LoginGate.ChallengeRequired)]
    [Trait("CaseId", "IDENTITY-BUDGET-001")]
    public void AccountFailureCount_RequiresChallenge(int failures, bool challenge, LoginGate expected)
    {
        var timestamps = Enumerable.Range(0, failures).Select(index => Now.AddSeconds(-index)).ToArray();

        Assert.Equal(expected, LoginPolicy.EvaluateAccount(timestamps, null, Now, challenge));
    }

    /// <summary>The fifth failed verification locks for fifteen minutes, with an exclusive expiry boundary.</summary>
    [Fact]
    [Trait("CaseId", "IDENTITY-BUDGET-001")]
    public void FifthFailure_LocksUntilExactDeadline()
    {
        var failures = Enumerable.Range(0, 5).Select(index => Now.AddSeconds(-index)).ToArray();
        var until = LoginPolicy.LockAfterFailure(failures, Now);

        Assert.Equal(Now.AddSeconds(900), until);
        Assert.Equal(LoginGate.Locked, LoginPolicy.EvaluateAccount(failures, until, Now.AddSeconds(899), true));
        Assert.Equal(LoginGate.VerifyPassword, LoginPolicy.EvaluateAccount(failures, until, Now.AddSeconds(900), false));
        Assert.Null(LoginPolicy.LockAfterFailure(failures.Take(4).ToArray(), Now));
    }

    /// <summary>Rolling windows exclude events exactly at the lower boundary, not all events in a calendar bucket.</summary>
    [Fact]
    [Trait("CaseId", "IDENTITY-BUDGET-001")]
    public void RollingWindows_ExpireOnlyOldEvents()
    {
        Assert.Equal(LoginGate.VerifyPassword, LoginPolicy.EvaluateAccount(
            [Now.AddSeconds(-900), Now.AddSeconds(-899), Now], null, Now, false));
        var full = Enumerable.Repeat(Now, 30).ToArray();
        Assert.False(LoginPolicy.CanAdmitSource(full, Now));
        Assert.True(LoginPolicy.CanAdmitSource(full.Take(29).ToArray(), Now));
        Assert.True(LoginPolicy.CanAdmitSource(Enumerable.Repeat(Now.AddSeconds(-60), 30).ToArray(), Now));
        Assert.False(LoginPolicy.CanAdmitSource(Enumerable.Repeat(Now.AddSeconds(-59), 30).ToArray(), Now));
    }

    /// <summary>Future observations indicate a clock/data fault and must not be ignored to admit requests.</summary>
    [Fact]
    [Trait("CaseId", "IDENTITY-BUDGET-001")]
    public void FutureTimestamp_IsRejected()
    {
        Assert.Throws<ArgumentException>(() => LoginPolicy.CanAdmitSource([Now.AddSeconds(1)], Now));
        Assert.Throws<ArgumentException>(() => LoginPolicy.EvaluateAccount([Now.AddSeconds(1)], null, Now, false));
    }
}
