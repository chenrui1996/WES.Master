using System.Net;
using Wes.Platform.Domain;
using Xunit;

namespace Wes.Modules.Tests.Platform;

/// <summary>Approved one-shot lifetime and binding semantics independent of persistence or rendering.</summary>
public sealed class LoginChallengeTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 24, 3, 0, 0, TimeSpan.Zero);
    private static readonly Guid Tenant = new("11111111-1111-4111-8111-111111111111");
    private static readonly LoginAccountKey Account = LoginAccountKey.FromNormalizedAccount("UNKNOWN");
    private static readonly LoginSourceKey Source = LoginSourceKey.FromTrustedPeer(IPAddress.Parse("192.0.2.1"));
    private static readonly string Mac = new('A', 64);
    private static LoginChallenge NewChallenge() => new(Tenant, Guid.NewGuid(), Account, Source, Mac, Start);

    /// <summary>Even two correct submissions cannot receive two accepted transitions from one challenge.</summary>
    [Fact]
    [Trait("CaseId", "IDENTITY-CHALLENGE-ONCE-001")]
    public void CorrectAnswerConsumesOnce_AndReplayCannotChangeVersion()
    {
        var challenge = NewChallenge();
        Assert.Equal(ChallengeDecision.Accepted, challenge.Consume(Account, Source, Mac, Start.AddSeconds(119)));
        var version = challenge.Version;
        Assert.Equal(Start.AddSeconds(119), challenge.ConsumedAt);
        Assert.Equal(ChallengeDecision.Rejected, challenge.Consume(Account, Source, Mac, Start.AddSeconds(119)));
        Assert.Equal(version, challenge.Version);
    }

    /// <summary>An incorrect first submission burns the one opportunity instead of allowing repeated guessing.</summary>
    [Fact]
    [Trait("CaseId", "IDENTITY-CHALLENGE-ONCE-001")]
    public void WrongAnswerConsumesBeforeCorrectReplay()
    {
        var challenge = NewChallenge();
        var version = challenge.Version;
        Assert.Equal(ChallengeDecision.Rejected, challenge.Consume(Account, Source, new string('B', 64), Start));
        Assert.NotEqual(version, challenge.Version);
        Assert.Equal(Start, challenge.ConsumedAt);
        Assert.Equal(ChallengeDecision.Rejected, challenge.Consume(Account, Source, Mac, Start));
    }

    /// <summary>The last allowed microsecond works; exact expiry does not create a consumption or extend the lifetime.</summary>
    [Fact]
    [Trait("CaseId", "IDENTITY-CHALLENGE-LIFETIME-001")]
    public void ExpiryIsExclusiveAndUtcMicrosecondPrecise()
    {
        var challenge = new LoginChallenge(Tenant, Guid.NewGuid(), Account, Source, Mac,
            Start.ToOffset(TimeSpan.FromHours(8)).AddTicks(19));
        Assert.Equal(Start.AddTicks(10), challenge.CreatedAt);
        Assert.Equal(Start.AddSeconds(120).AddTicks(10), challenge.ExpiresAt);
        Assert.Equal(ChallengeDecision.Accepted, challenge.Consume(Account, Source, Mac, Start.AddSeconds(120)));
        var expired = NewChallenge();
        var version = expired.Version;
        Assert.Equal(ChallengeDecision.Rejected, expired.Consume(Account, Source, Mac, Start.AddSeconds(120)));
        Assert.Null(expired.ConsumedAt);
        Assert.Equal(version, expired.Version);
    }

    /// <summary>Changing account or trusted source cannot use or burn another binding's opportunity.</summary>
    [Fact]
    [Trait("CaseId", "IDENTITY-CHALLENGE-BINDING-001")]
    public void WrongBindingRejectsWithoutBurningOriginal()
    {
        var challenge = NewChallenge();
        var version = challenge.Version;
        Assert.Equal(ChallengeDecision.Rejected, challenge.Consume(LoginAccountKey.FromNormalizedAccount("DIFFERENT"), Source, Mac, Start));
        Assert.Equal(ChallengeDecision.Rejected, challenge.Consume(Account,
            LoginSourceKey.FromTrustedPeer(IPAddress.Parse("192.0.2.2")), Mac, Start));
        Assert.Equal(version, challenge.Version);
        Assert.Null(challenge.ConsumedAt);
        Assert.Equal(ChallengeDecision.Accepted, challenge.Consume(Account, Source, Mac, Start));
    }

    /// <summary>Invalid construction and reversed clocks cannot be accepted as an unused valid challenge.</summary>
    [Fact]
    [Trait("CaseId", "IDENTITY-CHALLENGE-FAULT-001")]
    public void InvalidStateAndClockReversalFailExplicitly()
    {
        Assert.Throws<ArgumentException>(() => new LoginChallenge(Guid.Empty, Guid.NewGuid(), Account, Source, Mac, Start));
        Assert.Throws<ArgumentException>(() => new LoginChallenge(Tenant, Guid.Empty, Account, Source, Mac, Start));
        Assert.Throws<ArgumentException>(() => new LoginChallenge(Tenant, Guid.NewGuid(), Account, Source, "secret-marker", Start));
        var challenge = NewChallenge();
        Assert.Throws<InvalidOperationException>(() => challenge.Consume(Account, Source, Mac, Start.AddTicks(-10)));
        Assert.Null(challenge.ConsumedAt);
        challenge.Consume(Account, Source, Mac, Start.AddSeconds(1));
        Assert.Throws<InvalidOperationException>(() => challenge.Consume(Account, Source, Mac, Start));
    }
}
