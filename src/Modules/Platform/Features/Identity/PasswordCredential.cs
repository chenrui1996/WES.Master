using System.Text.Json;
using System.Text.Json.Serialization;
using Wes.Platform.Domain;

namespace Wes.Platform.Features.Identity;

/// <summary>Owner-local versioned credential and last five set values, never a user or HTTP response model.</summary>
public sealed class PasswordCredential
{
    private static readonly JsonSerializerOptions HistoryJson = new() { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
    private PasswordCredential() { }
    /// <summary>Accepts only a trusted owner account and an already reviewed bootstrap/import hash.</summary>
    public PasswordCredential(Guid tenant, Guid user, string normalizedAccount, StoredPasswordHash initial, DateTimeOffset now)
    {
        if (tenant == Guid.Empty || user == Guid.Empty) throw new ArgumentException("Credentials require trusted owner references.");
        ArgumentNullException.ThrowIfNull(initial);
        TenantId = tenant;
        UserId = user;
        NormalizedAccount = normalizedAccount;
        AccountDigest = LoginAccountKey.FromNormalizedAccount(normalizedAccount).Digest;
        ChangedAt = Normalize(now);
        HistoryData = Encode([initial]);
    }
    /// <summary>Trusted tenant owner.</summary>
    public Guid TenantId { get; private set; }
    /// <summary>Stable owner user identity.</summary>
    public Guid UserId { get; private set; }
    /// <summary>Credential owner's normalized account, not a display name or a new normalization policy.</summary>
    [JsonIgnore]
    public string NormalizedAccount { get; private set; } = "";
    /// <summary>Tenant-scoped canonical account digest.</summary>
    [JsonIgnore]
    public string AccountDigest { get; private set; } = "";
    /// <summary>Opaque write version and future session-issuance fence.</summary>
    public string Version { get; private set; } = Guid.NewGuid().ToString("D");
    /// <summary>UTC microsecond instant of the last set.</summary>
    public DateTimeOffset ChangedAt { get; private set; }
    /// <summary>Current account security state; existing imported rows fail closed until explicitly enabled.</summary>
    public bool Enabled { get; private set; } = true;
    /// <summary>Stages the shared CAS fence used by successful session issuance, never a password change.</summary>
    public void FenceSession(DateTimeOffset now)
    {
        _ = ReadRecent();
        if (Normalize(now) < ChangedAt) throw new InvalidOperationException("Credential time cannot move backwards.");
        if (!Enabled) throw new InvalidOperationException("A disabled account cannot issue a session.");
        Version = Guid.NewGuid().ToString("D");
    }
    /// <summary>Stages a reviewed owner account-state transition; authorization/audit and family revocation are caller obligations.</summary>
    public void SetEnabled(bool enabled, DateTimeOffset now)
    {
        _ = ReadRecent();
        if (Normalize(now) < ChangedAt) throw new InvalidOperationException("Credential time cannot move backwards.");
        // Even repairing an already disabled account with surviving families must fence concurrent credential operations.
        Enabled = enabled;
        Version = Guid.NewGuid().ToString("D");
    }
    private string HistoryData { get; set; } = "[]";
    /// <summary>Detached owner-only view, including the current value, newest first.</summary>
    [JsonIgnore]
    public IReadOnlyList<StoredPasswordHash> Recent => Array.AsReadOnly(ReadRecent());
    /// <summary>Current explicit-mode verification only; does not create a session or grant production writes.</summary>
    public CredentialVerification Verify(string candidate) => PasswordCredentialCodec.Verify(candidate, ReadRecent()[0]);
    /// <summary>Computes real local history evidence; corrupt rows must fail rather than clear.</summary>
    public PasswordEvidence CheckHistory(string candidate) => MatchHistory(candidate, ReadRecent());
    /// <summary>Stages an accepted new-format value and prunes to five; persistence and old-password proof are owner responsibilities.</summary>
    public PasswordDecision Change(string candidate, PasswordEvidence breach, DateTimeOffset now)
    {
        var recent = ReadRecent();
        var instant = Normalize(now);
        if (instant < ChangedAt) throw new InvalidOperationException("Credential time cannot move backwards.");
        var preliminary = PasswordPolicy.Evaluate(candidate, NormalizedAccount, breach, PasswordEvidence.Clear);
        if (preliminary != PasswordDecision.Accepted) return preliminary;
        var result = PasswordCredentialCodec.Create(candidate, NormalizedAccount, breach, MatchHistory(candidate, recent));
        if (result.Decision != PasswordDecision.Accepted) return result.Decision;
        var accepted = result.Hash ?? throw new InvalidOperationException("Accepted password hashing produced no credential.");
        HistoryData = Encode(new[] { accepted }.Concat(recent).Take(5));
        ChangedAt = instant;
        Version = Guid.NewGuid().ToString("D");
        return PasswordDecision.Accepted;
    }
    /// <summary>Prevents incidental credential-state formatting.</summary>
    public override string ToString() => "PasswordCredential[REDACTED]";

    private StoredPasswordHash[] ReadRecent()
    {
        if (TenantId == Guid.Empty || UserId == Guid.Empty
            || !Guid.TryParseExact(Version, "D", out var version) || version == Guid.Empty
            || ChangedAt.Offset != TimeSpan.Zero || ChangedAt.Ticks % 10 != 0
            || string.IsNullOrWhiteSpace(NormalizedAccount)
            || AccountDigest != LoginAccountKey.FromNormalizedAccount(NormalizedAccount).Digest
            || HistoryData is null || HistoryData.Length > 2048)
            throw new InvalidOperationException("Stored credential metadata is invalid.");
        HistoryEntry[]? entries;
        try
        {
            entries = JsonSerializer.Deserialize<HistoryEntry[]>(HistoryData, HistoryJson);
        }
        catch (JsonException)
        {
            throw new InvalidOperationException("Stored credential history schema is invalid.");
        }
        if (entries is null || entries.Length is < 1 or > 5 || entries.Any(item => item is null))
            throw new InvalidOperationException("Stored credential history count is invalid.");
        try
        {
            return entries.Select(item => StoredPasswordHash.Import(item.Scheme, item.Hash)).ToArray();
        }
        catch (ArgumentException)
        {
            throw new InvalidOperationException("Stored credential history format is invalid.");
        }
    }

    private static PasswordEvidence MatchHistory(string candidate, IEnumerable<StoredPasswordHash> recent)
    {
        var matched = false;
        foreach (var stored in recent)
            matched |= PasswordCredentialCodec.Verify(candidate, stored) != CredentialVerification.Invalid;
        return matched ? PasswordEvidence.Prohibited : PasswordEvidence.Clear;
    }

    private static string Encode(IEnumerable<StoredPasswordHash> recent) =>
        JsonSerializer.Serialize(recent.Select(item => new HistoryEntry(item.Scheme, item.EncodedHash)), HistoryJson);

    private static DateTimeOffset Normalize(DateTimeOffset now) => new(now.UtcTicks - now.UtcTicks % 10, TimeSpan.Zero);
    private sealed record HistoryEntry([property: JsonRequired] PasswordHashScheme Scheme, [property: JsonRequired] string Hash);
}
