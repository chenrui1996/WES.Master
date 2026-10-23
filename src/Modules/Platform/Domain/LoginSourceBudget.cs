using System.Net;
using System.Security.Cryptography;
using System.Text.Json;

namespace Wes.Platform.Domain;

/// <summary>A pseudonymous canonical peer address key; not an authentication principal or anonymous identifier.</summary>
public sealed record LoginSourceKey
{
    /// <summary>Uppercase SHA-256 key used only inside the owner persistence boundary.</summary>
    public string Digest { get; }

    private LoginSourceKey(string digest) => Digest = digest;

    /// <summary>The caller must supply a trusted peer address, never unverified proxy-header text.</summary>
    public static LoginSourceKey FromTrustedPeer(IPAddress peer)
    {
        ArgumentNullException.ThrowIfNull(peer);
        var canonical = peer.IsIPv4MappedToIPv6 ? peer.MapToIPv4() : peer;
        return new LoginSourceKey(Convert.ToHexString(SHA256.HashData(canonical.GetAddressBytes())));
    }

    /// <summary>Does not accidentally log source-address material.</summary>
    public override string ToString() => "[LoginSourceKey]";
}

/// <summary>Expected source-admission outcomes do not imply authentication success.</summary>
public enum SourceAdmission
{
    /// <summary>One source request was durably counted; credentials still require independent verification.</summary>
    Admitted,
    /// <summary>The source exhausted thirty requests in the approved rolling sixty seconds.</summary>
    RateLimited
}

/// <summary>Bounded durable observations for one global trusted source, never a per-tenant bypass.</summary>
public sealed class LoginSourceBudget
{
    private LoginSourceBudget() { }

    /// <summary>Creates a source with no prior observation; admission is a separate explicit transition.</summary>
    public LoginSourceBudget(LoginSourceKey key)
    {
        ArgumentNullException.ThrowIfNull(key);
        SourceDigest = key.Digest;
    }

    /// <summary>Canonical pseudonymous address digest; no original address is persisted.</summary>
    public string SourceDigest { get; private set; } = "";
    /// <summary>Opaque concurrency version; only successful transitions change it.</summary>
    public string Version { get; private set; } = Guid.NewGuid().ToString("D");
    /// <summary>Most recent admitted observation, including after all older observations expire.</summary>
    public DateTimeOffset? LastObservedAt { get; private set; }
    private string ObservationsData { get; set; } = "[]";

    /// <summary>Returns a detached observation snapshot; callers cannot alter persisted state through it.</summary>
    public IReadOnlyList<DateTimeOffset> Observations => ReadObservations();

    /// <summary>Stages exactly one microsecond-precision request if the rolling source budget allows it.</summary>
    public SourceAdmission Admit(DateTimeOffset now)
    {
        var instant = new DateTimeOffset(now.UtcTicks - now.UtcTicks % 10, TimeSpan.Zero);
        var observations = ReadObservations();
        if (LastObservedAt > instant)
        {
            throw new InvalidOperationException("Login source clock moved behind durable observations.");
        }
        if (!LoginPolicy.CanAdmitSource(observations, instant)) return SourceAdmission.RateLimited;

        ObservationsData = JsonSerializer.Serialize(observations.Where(item => instant - item < TimeSpan.FromSeconds(60)).Append(instant));
        LastObservedAt = instant;
        Version = Guid.NewGuid().ToString("D");
        return SourceAdmission.Admitted;
    }

    private DateTimeOffset[] ReadObservations()
    {
        DateTimeOffset[]? observations;
        try
        {
            observations = JsonSerializer.Deserialize<DateTimeOffset[]>(ObservationsData);
        }
        catch (JsonException)
        {
            throw new InvalidOperationException("Login source observation schema is invalid.");
        }

        if (observations is null || observations.Length > 30
            || (observations.Length == 0 ? LastObservedAt is not null : LastObservedAt != observations[^1])
            || observations.Any(item => item.Offset != TimeSpan.Zero || item.Ticks % 10 != 0
                || item > LastObservedAt || LastObservedAt - item >= TimeSpan.FromSeconds(60))
            || !observations.SequenceEqual(observations.Order()))
        {
            throw new InvalidOperationException("Login source observation invariants are invalid.");
        }
        return observations;
    }
}
