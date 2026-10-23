using Wes.Platform.Domain;
using Xunit;

namespace Wes.Modules.Tests.Platform;

/// <summary>Independent literal-time boundary tests for approved session semantics, without a fake database.</summary>
public sealed class SessionTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 23, 12, 0, 0, TimeSpan.Zero);
    private static readonly string Digest = new('A', 64);
    private static readonly string NextDigest = new('B', 64);

    /// <summary>The exact access expiry and future-issuance boundaries must reject even a signed token.</summary>
    [Theory]
    [InlineData(899, true)]
    [InlineData(900, false)]
    [InlineData(-1, false)]
    [Trait("CaseId", "IDENTITY-SESSION-001")]
    public void Access_ObeysExactDeadline(int seconds, bool expected)
    {
        var session = Create();

        Assert.Equal(expected, session.AcceptsAccess(Start, Start.AddSeconds(seconds)));
    }

    /// <summary>Client idle expiry uses the approved threshold, never a sliding refresh deadline.</summary>
    [Theory]
    [InlineData(SessionClient.Web, 899, SessionState.Active)]
    [InlineData(SessionClient.Web, 900, SessionState.IdleLocked)]
    [InlineData(SessionClient.Pda, 299, SessionState.Active)]
    [InlineData(SessionClient.Pda, 300, SessionState.IdleLocked)]
    [InlineData(SessionClient.Web, 28800, SessionState.Expired)]
    [Trait("CaseId", "IDENTITY-SESSION-002")]
    public void State_ObeysClientAndAbsoluteDeadlines(SessionClient client, int seconds, SessionState expected)
    {
        var session = Create(client);

        Assert.Equal(expected, session.StateAt(Start.AddSeconds(seconds)));
    }

    /// <summary>Successful refresh consumes the credential but does not pretend the user was active.</summary>
    [Fact]
    [Trait("CaseId", "IDENTITY-REFRESH-001")]
    public void Refresh_DoesNotExtendActivityOrAbsoluteExpiry()
    {
        var session = Create();
        var previous = session.Version;

        Assert.Equal(RefreshDecision.Rotated, session.Rotate(Digest, NextDigest, Start.AddSeconds(800)));

        Assert.Equal(Start, session.LastActivityAt);
        Assert.Equal(Start.AddHours(8), session.ExpiresAt);
        Assert.NotEqual(previous, session.Version);
        Assert.Equal(SessionState.IdleLocked, session.StateAt(Start.AddSeconds(900)));
        Assert.Equal(RefreshDecision.Inactive, session.Rotate(NextDigest, new string('C', 64), Start.AddSeconds(900)));
    }

    /// <summary>Known consumed credentials revoke this family; unknown credentials must not enable forced logout.</summary>
    [Fact]
    [Trait("CaseId", "IDENTITY-REFRESH-001")]
    public void Replay_RevokesFamily_UnknownDigestDoesNotMutate()
    {
        var session = Create();
        var previous = session.Version;
        Assert.Equal(RefreshDecision.Invalid, session.Rotate(new string('C', 64), NextDigest, Start.AddSeconds(1)));
        Assert.Equal(previous, session.Version);
        Assert.Equal(RefreshDecision.Rotated, session.Rotate(Digest, NextDigest, Start.AddSeconds(2)));

        Assert.Equal(RefreshDecision.Replayed, session.Rotate(Digest, new string('D', 64), Start.AddSeconds(3)));

        Assert.Equal(SessionState.Revoked, session.StateAt(Start.AddSeconds(3)));
        Assert.False(session.AcceptsAccess(Start, Start.AddSeconds(3)));
    }

    /// <summary>Revoking one family is idempotent and does not affect another independent login.</summary>
    [Fact]
    [Trait("CaseId", "IDENTITY-REVOKE-001")]
    public void Revoke_IsIdempotentAndLocal()
    {
        var first = Create();
        var other = Create();
        first.Revoke(Start.AddSeconds(1));
        var revokedVersion = first.Version;

        first.Revoke(Start.AddSeconds(2));

        Assert.Equal(SessionState.Revoked, first.StateAt(Start.AddSeconds(2)));
        Assert.Equal(revokedVersion, first.Version);
        Assert.Equal(SessionState.Active, other.StateAt(Start.AddSeconds(2)));
    }

    /// <summary>Real activity changes the idle watermark, but cannot revive a locked family.</summary>
    [Fact]
    [Trait("CaseId", "IDENTITY-SESSION-002")]
    public void Activity_CannotReviveLockedOrBackdateClock()
    {
        var session = Create();
        Assert.True(session.RecordActivity(Start.AddSeconds(800)));
        Assert.Equal(Start.AddSeconds(800), session.LastActivityAt);
        Assert.Equal(SessionState.Active, session.StateAt(Start.AddSeconds(1000)));
        Assert.False(session.RecordActivity(Start.AddSeconds(1700)));
        Assert.Throws<InvalidOperationException>(() => session.RecordActivity(Start));
    }

    /// <summary>Aggregate owners and refresh digest shape must be explicit before persistence.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("raw-token-marker")]
    [InlineData("GGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGG")]
    [Trait("CaseId", "IDENTITY-SECRET-001")]
    public void InvalidDigest_IsRejectedWithoutEcho(string digest)
    {
        var error = Assert.Throws<ArgumentException>(() =>
            new Session(Guid.NewGuid(), Guid.NewGuid(), SessionClient.Web, Start, digest));

        Assert.DoesNotContain("raw-token-marker", error.Message);
    }

    /// <summary>Missing tenant identity cannot create a shared implicit authentication scope.</summary>
    [Fact]
    [Trait("CaseId", "IDENTITY-TENANT-001")]
    public void MissingTenant_IsRejected() =>
        Assert.Throws<ArgumentException>(() => new Session(Guid.Empty, Guid.NewGuid(), SessionClient.Web, Start, Digest));

    /// <summary>Frequent legitimate activity and a very recent refresh cannot extend the absolute lifetime.</summary>
    [Fact]
    [Trait("CaseId", "IDENTITY-SESSION-001")]
    public void ActivePda_StillExpiresAtAbsoluteEightHours()
    {
        var session = Create(SessionClient.Pda);
        for (var seconds = 240; seconds < 28800; seconds += 240)
        {
            Assert.True(session.RecordActivity(Start.AddSeconds(seconds)));
        }

        Assert.Equal(RefreshDecision.Rotated, session.Rotate(Digest, NextDigest, Start.AddSeconds(28799)));
        Assert.True(session.AcceptsAccess(Start.AddSeconds(28790), Start.AddSeconds(28799)));
        Assert.False(session.AcceptsAccess(Start.AddSeconds(28790), Start.AddSeconds(28800)));
        Assert.Equal(RefreshDecision.Inactive,
            session.Rotate(NextDigest, new string('C', 64), Start.AddSeconds(28800)));
    }

    /// <summary>Installing an existing digest must not consume the current credential or change its version.</summary>
    [Fact]
    [Trait("CaseId", "IDENTITY-REFRESH-001")]
    public void SuccessorCannotReuseExistingCredential()
    {
        var session = Create();
        var version = session.Version;

        Assert.Equal(RefreshDecision.Invalid, session.Rotate(Digest, Digest, Start.AddSeconds(1)));
        Assert.Equal(version, session.Version);
        Assert.Null(Assert.Single(session.Credentials).ConsumedAt);
        Assert.Equal(RefreshDecision.Rotated, session.Rotate(Digest, NextDigest, Start.AddSeconds(2)));
        Assert.Equal(RefreshDecision.Invalid, session.Rotate(NextDigest, Digest, Start.AddSeconds(3)));
    }

    private static Session Create(SessionClient client = SessionClient.Web) =>
        new(Guid.NewGuid(), Guid.NewGuid(), client, Start, Digest);
}
