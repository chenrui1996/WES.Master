namespace Wes.Platform.Domain;

/// <summary>Expected risk-gate result before password verification; transport errors must not enumerate accounts.</summary>
public enum LoginGate
{
    /// <summary>Application may proceed to password verification, not issue a session yet.</summary>
    VerifyPassword,
    /// <summary>A valid single-use challenge is required before password verification.</summary>
    ChallengeRequired,
    /// <summary>The approved temporary account lock is still active.</summary>
    Locked
}

/// <summary>
/// Pure approved rolling-window policy. Durable callers own atomic admission and authoritative timestamps;
/// these functions do not create an in-memory rate-limit authority.
/// </summary>
public static class LoginPolicy
{
    /// <summary>Evaluates same-tenant normalized account failures, without disclosing account existence.</summary>
    public static LoginGate EvaluateAccount(
        IReadOnlyCollection<DateTimeOffset> failures, DateTimeOffset? lockedUntil,
        DateTimeOffset now, bool validChallenge)
    {
        var count = RecentCount(failures, now, TimeSpan.FromMinutes(15));
        if (lockedUntil > now || count >= 5)
        {
            return LoginGate.Locked;
        }

        return count >= 3 && !validChallenge ? LoginGate.ChallengeRequired : LoginGate.VerifyPassword;
    }

    /// <summary>Evaluates the source-IP budget across accounts and tenants; input must come from the trusted peer.</summary>
    public static bool CanAdmitSource(IReadOnlyCollection<DateTimeOffset> attempts, DateTimeOffset now) =>
        RecentCount(attempts, now, TimeSpan.FromSeconds(60)) < 30;

    /// <summary>Computes the temporary lock created by the fifth password failure in the rolling account window.</summary>
    public static DateTimeOffset? LockAfterFailure(
        IReadOnlyCollection<DateTimeOffset> failuresIncludingCurrent, DateTimeOffset now) =>
        RecentCount(failuresIncludingCurrent, now, TimeSpan.FromMinutes(15)) >= 5
            ? now.AddMinutes(15) : null;

    private static int RecentCount(IReadOnlyCollection<DateTimeOffset> observations, DateTimeOffset now, TimeSpan window)
    {
        ArgumentNullException.ThrowIfNull(observations);
        if (observations.Any(instant => instant > now))
        {
            throw new ArgumentException("Login risk observations cannot be in the future.");
        }

        return observations.Count(instant => now - instant < window);
    }
}
