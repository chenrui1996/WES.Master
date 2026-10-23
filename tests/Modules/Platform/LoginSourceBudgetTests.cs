using System.Net;
using Wes.Platform.Domain;
using Xunit;

namespace Wes.Modules.Tests.Platform;

/// <summary>Approved literal source-window and canonical-identity examples, independent of storage and proxy headers.</summary>
public sealed class LoginSourceBudgetTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 24, 1, 0, 0, TimeSpan.Zero);

    /// <summary>Mapped addresses and link-local scope IDs cannot create extra budgets for the same address.</summary>
    [Fact]
    [Trait("CaseId", "IDENTITY-SOURCE-KEY-001")]
    public void SourceKey_CanonicalizesPeerIdentity_WithoutPrintingAddress()
    {
        var ipv4 = LoginSourceKey.FromTrustedPeer(IPAddress.Parse("192.0.2.1"));
        var mapped = LoginSourceKey.FromTrustedPeer(IPAddress.Parse("::ffff:192.0.2.1"));
        var ipv6 = LoginSourceKey.FromTrustedPeer(IPAddress.Parse("2001:db8::1"));
        var scope1 = LoginSourceKey.FromTrustedPeer(IPAddress.Parse("fe80::1%1"));
        var scope2 = LoginSourceKey.FromTrustedPeer(IPAddress.Parse("fe80::1%2"));
        Assert.Equal(ipv4, mapped);
        Assert.Equal(scope1, scope2);
        Assert.NotEqual(ipv4, ipv6);
        Assert.NotEqual(ipv6, LoginSourceKey.FromTrustedPeer(IPAddress.Parse("2001:db8::2")));
        Assert.Matches("^[0-9A-F]{64}$", ipv4.Digest);
        Assert.Equal("4B8FEB9364512928546DEBFC76C9ECC0F96E4CF545B30AB5CBDE831712702B9E", ipv4.Digest);
        Assert.DoesNotContain("192.0.2.1", ipv4.ToString());
        Assert.DoesNotContain(ipv4.Digest, ipv4.ToString());
        Assert.Throws<ArgumentNullException>(() => LoginSourceKey.FromTrustedPeer(null!));
    }

    /// <summary>Thirty requests at the same instant still count individually; the thirty-first cannot change state.</summary>
    [Fact]
    [Trait("CaseId", "IDENTITY-SOURCE-WINDOW-001")]
    public void Admission_CountsDuplicateInstants_AndRejectsThirtyFirst()
    {
        var budget = NewBudget();
        var initial = budget.Version;
        for (var attempt = 0; attempt < 30; attempt++) Assert.Equal(SourceAdmission.Admitted, budget.Admit(Start));
        var full = budget.Version;
        Assert.NotEqual(initial, full);
        Assert.Equal(SourceAdmission.RateLimited, budget.Admit(Start.AddSeconds(1)));
        Assert.Equal(full, budget.Version);
        Assert.Equal(Start, budget.LastObservedAt);
        Assert.Equal(30, budget.Observations.Count);
        Assert.All(budget.Observations, instant => Assert.Equal(Start, instant));
    }

    /// <summary>Window expiry is exclusive at exactly sixty seconds and does not reset at the minute boundary.</summary>
    [Fact]
    [Trait("CaseId", "IDENTITY-SOURCE-WINDOW-001")]
    public void Admission_UsesRollingBoundary_ThenDiscardsExpiredObservations()
    {
        var budget = NewBudget();
        for (var attempt = 0; attempt < 30; attempt++) budget.Admit(Start.AddSeconds(59));
        Assert.Equal(SourceAdmission.RateLimited, budget.Admit(Start.AddSeconds(60)));
        Assert.Equal(SourceAdmission.RateLimited, budget.Admit(Start.AddSeconds(119).AddTicks(-10)));
        Assert.Equal(SourceAdmission.Admitted, budget.Admit(Start.AddSeconds(119)));
        Assert.Equal(new[] { Start.AddSeconds(119) }, budget.Observations);
    }

    /// <summary>Persistence quantization cannot make an event expire before its approved normalized microsecond deadline.</summary>
    [Fact]
    [Trait("CaseId", "IDENTITY-SOURCE-WINDOW-001")]
    public void Admission_NormalizesUtcMicroseconds_AndDoesNotExposeMutableState()
    {
        var budget = NewBudget();
        var local = Start.ToOffset(TimeSpan.FromHours(8)).AddTicks(19);
        Assert.Equal(SourceAdmission.Admitted, budget.Admit(local));
        Assert.Equal(Start.AddTicks(10), budget.LastObservedAt);
        var snapshot = budget.Observations;
        if (snapshot is DateTimeOffset[] values) values[0] = Start.AddDays(-1);
        Assert.Equal(Start.AddTicks(10), Assert.Single(budget.Observations));
    }

    /// <summary>Clock reversal, even after a long idle period, cannot silently clear durable risk state.</summary>
    [Fact]
    [Trait("CaseId", "IDENTITY-SOURCE-CLOCK-001")]
    public void ClockReversal_IsRejectedWithoutMutation()
    {
        var budget = NewBudget();
        budget.Admit(Start);
        budget.Admit(Start.AddSeconds(120));
        var version = budget.Version;
        Assert.Throws<InvalidOperationException>(() => budget.Admit(Start.AddSeconds(119)));
        Assert.Equal(version, budget.Version);
        Assert.Equal(Start.AddSeconds(120), Assert.Single(budget.Observations));
        Assert.Throws<ArgumentNullException>(() => new LoginSourceBudget(null!));
    }

    private static LoginSourceBudget NewBudget() => new(LoginSourceKey.FromTrustedPeer(IPAddress.Parse("192.0.2.1")));
}
