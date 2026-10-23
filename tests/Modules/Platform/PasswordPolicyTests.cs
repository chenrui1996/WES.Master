using Wes.Platform.Domain;
using Xunit;

namespace Wes.Modules.Tests.Platform;

/// <summary>Checks approved Unicode/evidence policy without pretending an unconfigured breach corpus is clean.</summary>
public sealed class PasswordPolicyTests
{
    /// <summary>UTF-16 code units are not Unicode scalar count; long passwords must never be truncated.</summary>
    [Theory]
    [InlineData(14, PasswordDecision.InvalidShape)]
    [InlineData(15, PasswordDecision.Accepted)]
    [InlineData(128, PasswordDecision.Accepted)]
    [InlineData(129, PasswordDecision.InvalidShape)]
    [Trait("CaseId", "IDENTITY-PASSWORD-001")]
    public void ScalarLength_UsesApprovedBoundaries(int count, PasswordDecision expected)
    {
        var password = string.Concat(Enumerable.Repeat(char.ConvertFromUtf32(0x1F680), count));

        Assert.Equal(expected, PasswordPolicy.Evaluate(password, "account", PasswordEvidence.Clear, PasswordEvidence.Clear));
    }

    /// <summary>Malformed UTF-16 is not silently replaced before hashing.</summary>
    [Fact]
    [Trait("CaseId", "IDENTITY-PASSWORD-001")]
    public void IsolatedSurrogate_IsRejected() =>
        Assert.Equal(PasswordDecision.InvalidShape,
            PasswordPolicy.Evaluate(new string('a', 15) + "\uD800", "account", PasswordEvidence.Clear, PasswordEvidence.Clear));

    /// <summary>Every required negative or missing evidence result rejects independently.</summary>
    [Theory]
    [InlineData(PasswordEvidence.Prohibited, PasswordEvidence.Clear, PasswordDecision.Compromised)]
    [InlineData(PasswordEvidence.Clear, PasswordEvidence.Prohibited, PasswordDecision.RecentlyUsed)]
    [InlineData(PasswordEvidence.Unknown, PasswordEvidence.Clear, PasswordDecision.EvidenceUnavailable)]
    [InlineData(PasswordEvidence.Clear, PasswordEvidence.Unknown, PasswordDecision.EvidenceUnavailable)]
    [Trait("CaseId", "IDENTITY-PASSWORD-001")]
    public void Evidence_MustBeExplicit(PasswordEvidence breach, PasswordEvidence history, PasswordDecision expected)
    {
        Assert.Equal(expected, PasswordPolicy.Evaluate("a long candidate password", "account", breach, history));
    }

    /// <summary>Account equality is forbidden, but the password itself must not be trimmed into another value.</summary>
    [Fact]
    [Trait("CaseId", "IDENTITY-PASSWORD-001")]
    public void AccountMatch_IsRejectedWithoutTrimmingPassword()
    {
        Assert.Equal(PasswordDecision.MatchesAccount,
            PasswordPolicy.Evaluate("WarehouseOperator", "warehouseoperator", PasswordEvidence.Clear, PasswordEvidence.Clear));
        Assert.Equal(PasswordDecision.Accepted,
            PasswordPolicy.Evaluate(" WarehouseOperator ", "WarehouseOperator", PasswordEvidence.Clear, PasswordEvidence.Clear));
    }
}
