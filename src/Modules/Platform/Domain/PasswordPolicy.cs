using System.Buffers;
using System.Text;

namespace Wes.Platform.Domain;

/// <summary>Policy verification state from an explicit authoritative breach/history check.</summary>
public enum PasswordEvidence
{
    /// <summary>The relevant check was completed and the candidate is not prohibited.</summary>
    Clear,
    /// <summary>The check positively matched a prohibited candidate.</summary>
    Prohibited,
    /// <summary>Missing configuration or dependency failure cannot be treated as clearance.</summary>
    Unknown
}

/// <summary>Expected password-policy results; never includes the password or a previous hash.</summary>
public enum PasswordDecision
{
    /// <summary>The shape and required evidence pass; hashing/persistence are separate responsibilities.</summary>
    Accepted,
    /// <summary>Malformed Unicode or length outside the approved scalar range.</summary>
    InvalidShape,
    /// <summary>The password matches the account name.</summary>
    MatchesAccount,
    /// <summary>The password is known compromised.</summary>
    Compromised,
    /// <summary>The password matches one of the protected recent five entries.</summary>
    RecentlyUsed,
    /// <summary>Required breach/history evidence was not obtained.</summary>
    EvidenceUnavailable
}

/// <summary>Pure new-password validation. It never truncates, hashes, persists, logs or normalizes stored credentials.</summary>
public static class PasswordPolicy
{
    /// <summary>Requires explicit breach/history evidence; lack of evidence is not a success-shaped default.</summary>
    public static PasswordDecision Evaluate(
        string password, string account, PasswordEvidence breach, PasswordEvidence history)
    {
        if (password is null || string.IsNullOrWhiteSpace(account))
        {
            return PasswordDecision.InvalidShape;
        }

        var remaining = password.AsSpan();
        var scalarCount = 0;
        while (!remaining.IsEmpty)
        {
            if (Rune.DecodeFromUtf16(remaining, out _, out var consumed) != OperationStatus.Done || ++scalarCount > 128)
            {
                return PasswordDecision.InvalidShape;
            }

            remaining = remaining[consumed..];
        }

        if (scalarCount < 15)
        {
            return PasswordDecision.InvalidShape;
        }

        if (string.Equals(password, account, StringComparison.OrdinalIgnoreCase))
        {
            return PasswordDecision.MatchesAccount;
        }

        if (breach is not (PasswordEvidence.Clear or PasswordEvidence.Prohibited)
            || history is not (PasswordEvidence.Clear or PasswordEvidence.Prohibited))
        {
            return PasswordDecision.EvidenceUnavailable;
        }

        if (breach == PasswordEvidence.Prohibited)
        {
            return PasswordDecision.Compromised;
        }

        return history == PasswordEvidence.Prohibited ? PasswordDecision.RecentlyUsed : PasswordDecision.Accepted;
    }
}
