using System.Collections.Immutable;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Wes.Platform.Domain;
using Wes.Platform.Features.Audit;
using Wes.Platform.Features.Authorization;
using Wes.Platform.Features.Directory;
using Wes.Platform.Features.Identity;
using Wes.Platform.Features.Messaging;
using Wes.Platform.Features.Settings;
using Wes.Platform.Features.Tenancy;
using Wes.Platform.Infrastructure.Persistence;

namespace Wes.Platform;

/// <summary>Transport client classification, explicitly mapped to the owner domain.</summary>
public enum IdentityClient
{
    /// <summary>Web operations client.</summary>
    Web,
    /// <summary>PDA client.</summary>
    Pda
}
/// <summary>Authoritative server evidence contract; request bodies cannot supply this value.</summary>
public enum PasswordBreachEvidence
{
    /// <summary>A complete reviewed corpus did not contain the exact password.</summary>
    Clear,
    /// <summary>The exact password is present in the reviewed corpus.</summary>
    Prohibited
}
/// <summary>Untrusted explicit role grant input; converted and validated inside Platform, never treated as claims.</summary>
public sealed record PermissionRuleInput(string Permission, int Scope, Guid[] Departments, string[] Fields);
/// <summary>Explicit reviewed local metadata; contains no default subject, password or inferred administrator grant.</summary>
public sealed record LocalDirectoryInput(Guid UserId, string TenantName, string DefaultLanguage, string DepartmentCode,
    string DepartmentName, string Account, string DisplayName, string RoleCode, PermissionRuleInput[] ReviewedGrants);
/// <summary>Application outcomes independent of HTTP and private domain enum types.</summary>
public enum PlatformDecision
{
    /// <summary>Committed owner change.</summary>
    Changed,
    /// <summary>Current authority or proof is absent.</summary>
    Forbidden,
    /// <summary>An expected version was not supplied.</summary>
    VersionRequired,
    /// <summary>The expected version is no longer current.</summary>
    VersionConflict,
    /// <summary>The catalog identity is absent.</summary>
    NotFound,
    /// <summary>Schema or credential policy rejected the request.</summary>
    Invalid,
    /// <summary>Physical evidence cannot be established through this software command.</summary>
    Unverified,
    /// <summary>The same operation UUID was used with a different nonsecret command.</summary>
    IdempotencyConflict
}
/// <summary>Safe application command result, disclosing versions only on committed changes.</summary>
public sealed record PlatformWriteResult(PlatformDecision Decision, string? Version = null);
/// <summary>Current subject and current grants, excluding all secret storage.</summary>
public sealed record IdentitySubjectView(Guid TenantId, Guid UserId, Guid SessionId, bool RequiresPasswordChange,
    string? CredentialVersion, ImmutableArray<PermissionGrant> Grants);
/// <summary>Owned session projection, with no digest or raw refresh value.</summary>
public sealed record IdentitySessionView(Guid Id, int Client, DateTimeOffset CreatedAt, DateTimeOffset LastActivityAt,
    DateTimeOffset ExpiresAt, DateTimeOffset? RevokedAt, bool RequiresPasswordChange, string Version, bool Current, int State);
/// <summary>A source-admitted challenge, or an explicit source-budget refusal.</summary>
public sealed record ChallengeIssueResult(bool Admitted, LoginChallengeDelivery? Delivery);
/// <summary>A refresh successor returned only after durable rotation and current eligibility checks.</summary>
public sealed class IdentityRefreshResult(bool completed, bool restricted = false, RefreshSecret? secret = null)
{
    /// <summary>Whether a committed successor may be delivered.</summary>
    public bool Completed { get; } = completed;
    /// <summary>Legacy restrictions remain in force on the successor.</summary>
    public bool RequiresPasswordChange { get; } = restricted;
    /// <summary>In-process secret delivery only, never default JSON.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public RefreshSecret? Secret { get; } = secret;
    /// <summary>Never formats the delivery secret.</summary>
    public override string ToString() => "IdentityRefreshResult[REDACTED]";
}

/// <summary>Public Platform application boundary. Persistence and domain transitions stay inside the module.</summary>
public sealed class PlatformScope : IAsyncDisposable
{
    private readonly PlatformDbContext _database;
    private readonly string _connection;
    private readonly TimeProvider _clock;
    private readonly ILoggerFactory _logs;
    private readonly DirectoryOperation? _operation;
    /// <summary>Creates a clean tenant owner from deployment-only connection material.</summary>
    public PlatformScope(string connection, Guid tenant, TimeProvider clock, ILoggerFactory logs, DirectoryOperation? operation = null)
    {
        _connection = connection; _clock = clock; _logs = logs; _operation = operation;
        _database = new PlatformDbContext(new DbContextOptionsBuilder<PlatformDbContext>().UseMySQL(connection,
            mysql => mysql.MigrationsHistoryTable("__WesPlatformMigrations")).Options, tenant);
    }
    /// <summary>Named directory application use cases, not a persistence handle or generated CRUD surface.</summary>
    public DirectoryService Directory => new(_database, _clock, _operation);
    /// <summary>Trusted local-process provisioning only; it is deliberately absent from HTTP mappings.</summary>
    public Task<DirectoryWriteResult> ProvisionLocalAsync(bool enabled, LocalDirectoryInput input, string password,
        PasswordBreachEvidence evidence, Guid correlation, CancellationToken ct = default) =>
        new DirectoryBootstrap(_database, _clock).ProvisionAsync(enabled, input.UserId, input.TenantName, input.DefaultLanguage,
            input.DepartmentCode, input.DepartmentName, input.Account, input.DisplayName, password, Evidence(evidence),
            input.RoleCode, Grants(input.ReviewedGrants), correlation, ct);
    /// <summary>Coordinates source, challenge, credential, current directory and durable family creation.</summary>
    public async Task<LoginAttemptResult> LoginAsync(string account, string password, System.Net.IPAddress peer, IdentityClient client,
        Guid correlation, LoginChallengeProtection protection, Guid? challenge, string? answer, CancellationToken ct)
    {
        await using var sources = Sources();
        return await new LoginAttemptHandler(_database, sources, protection, _clock, _logs)
            .HandleAsync(account, password, LoginSourceKey.FromTrustedPeer(peer), (SessionClient)client, correlation, challenge, answer, ct);
    }
    /// <summary>Applies the source-only budget before issuing a durable one-shot challenge.</summary>
    public async Task<ChallengeIssueResult> ChallengeAsync(string account, System.Net.IPAddress peer, LoginChallengeProtection protection, CancellationToken ct)
    {
        var source = LoginSourceKey.FromTrustedPeer(peer);
        await using var sources = Sources();
        if (await new AdmitLoginSourceHandler(sources, _clock, _logs.CreateLogger<AdmitLoginSourceHandler>()).HandleAsync(source, ct)
            != SourceAdmission.Admitted) return new(false, null);
        var delivery = await new LoginChallengeHandler(_database, _clock, protection, _logs.CreateLogger<LoginChallengeHandler>())
            .IssueAsync(LoginAccountKey.FromNormalizedAccount(account), source, ct);
        return new(true, delivery);
    }
    /// <summary>Checks current directory and family on every signed request, independent of cached token claims.</summary>
    public async Task<bool> AcceptsAccessAsync(TenantContext subject, DateTimeOffset issued, CancellationToken ct)
    {
        var family = await _database.Sessions.AsNoTracking().SingleOrDefaultAsync(
            item => item.Id == subject.SessionId && item.UserId == subject.ActorId, ct);
        return await new DirectoryEligibility(_database).IsEligibleAsync(subject.ActorId, ct)
            && family is not null && family.AcceptsAccess(issued, _clock.GetUtcNow());
    }
    /// <summary>Rotates only a currently eligible owned family; refresh never advances the activity clock.</summary>
    public async Task<IdentityRefreshResult> RefreshAsync(Guid user, Guid session, string secret, CancellationToken ct)
    {
        if (!await new DirectoryEligibility(_database).IsEligibleAsync(user, ct)) return new(false);
        var result = await new RefreshSessionHandler(_database, _clock, _logs.CreateLogger<RefreshSessionHandler>()).HandleAsync(session, secret, ct);
        _database.ChangeTracker.Clear();
        if (result.Decision != RefreshDecision.Rotated || result.Successor is null) return new(false);
        var family = await _database.Sessions.AsNoTracking().SingleAsync(item => item.Id == session, ct);
        return family.UserId == user && family.StateAt(_clock.GetUtcNow()) == SessionState.Active
            && await new DirectoryEligibility(_database).IsEligibleAsync(user, ct)
            ? new(true, family.RequiresPasswordChange, result.Successor) : new(false);
    }
    /// <summary>Projects current identity without credential hashes or stale role claims.</summary>
    public async Task<IdentitySubjectView> SubjectAsync(TenantContext subject, CancellationToken ct)
    {
        var credential = await _database.PasswordCredentials.AsNoTracking().SingleOrDefaultAsync(item => item.UserId == subject.ActorId, ct);
        var family = await _database.Sessions.AsNoTracking().SingleAsync(item => item.Id == subject.SessionId, ct);
        return new(subject.TenantId, subject.ActorId, subject.SessionId, family.RequiresPasswordChange, credential?.Version,
            await new CurrentAuthorization(_database, _clock).ReadGrantsAsync(subject, ct));
    }
    /// <summary>Reads a bounded owned-family page with safe metadata and an authoritative evaluated state.</summary>
    public Task<IReadOnlyList<IdentitySessionView>> SessionsAsync(TenantContext subject, SessionQuery filter, CancellationToken ct) =>
        new QuerySessionFamilies(_database, _clock, _logs.CreateLogger<QuerySessionFamilies>()).OwnAsync(subject, filter, ct);
    /// <summary>Reads authorized administrative session projections using current directory ownership.</summary>
    public Task<DirectoryPage<AdministrativeSessionRow>> AdministrativeSessionsAsync(TenantContext subject, SessionQuery filter, CancellationToken ct) =>
        new QuerySessionFamilies(_database, _clock, _logs.CreateLogger<QuerySessionFamilies>()).AdministrativeAsync(subject, filter, ct);
    /// <summary>Revokes only the current caller's family and appends its audit.</summary>
    public Task<LogoutDecision> LogoutAsync(TenantContext subject, Guid correlation, CancellationToken ct) =>
        new LogoutSessionHandler(_database, _clock, _logs.CreateLogger<LogoutSessionHandler>()).HandleAsync(subject, correlation, ct);
    /// <summary>Records an actual user interaction, not a poll or refresh.</summary>
    public Task<ActivityDecision> ActivityAsync(TenantContext subject, CancellationToken ct) =>
        new RecordSessionActivity(_database, _clock, _logs.CreateLogger<RecordSessionActivity>()).HandleAsync(subject, ct);
    /// <summary>Revalidates selected-family ownership or current administrative scope and expected version.</summary>
    public Task<SessionRevokeDecision> RevokeAsync(TenantContext subject, Guid id, string version, Guid correlation, CancellationToken ct) =>
        new RevokeSessionHandler(_database, _clock, _logs.CreateLogger<RevokeSessionHandler>()).HandleAsync(subject, id, version, correlation, ct);
    /// <summary>Uses server-owned evidence and the current old-password proof, with full-history enforcement.</summary>
    public async Task<PlatformWriteResult> PasswordAsync(TenantContext subject, string version, string oldPassword, string newPassword,
        PasswordBreachEvidence evidence, Guid correlation, CancellationToken ct)
    {
        var result = await new ChangePasswordHandler(_database, _clock, _logs.CreateLogger<ChangePasswordHandler>())
            .HandleAsync(subject, version, oldPassword, newPassword, Evidence(evidence), correlation, ct);
        return new(result.Decision switch
        {
            PasswordChangeDecision.Completed => PlatformDecision.Changed,
            PasswordChangeDecision.VersionConflict => PlatformDecision.VersionConflict,
            PasswordChangeDecision.PolicyRejected => PlatformDecision.Invalid,
            _ => PlatformDecision.Forbidden
        });
    }
    /// <summary>Creates a role after module-local schema conversion and delegation checks.</summary>
    public Task<DirectoryWriteResult> CreateRoleAsync(TenantContext subject, string code, PermissionRuleInput[] rules, Guid correlation, CancellationToken ct) =>
        Directory.CreateRoleAsync(subject, code, Grants(rules), correlation, ct);
    /// <summary>Creates a first credential using trusted corpus evidence after current field/scope checks.</summary>
    public Task<DirectoryWriteResult> CreateCredentialAsync(TenantContext subject, Guid user, string? version, string password,
        PasswordBreachEvidence evidence, Guid correlation, CancellationToken ct) =>
        Directory.CreateCredentialAsync(subject, user, version, password, Evidence(evidence), correlation, ct);
    /// <summary>Disables only under current Enabled-field authority and the exact credential version.</summary>
    public Task<AccountDisableDecision> DisableAccountAsync(TenantContext subject, Guid user, string expectedVersion, Guid correlation,
        CancellationToken ct = default) =>
        new DisableAccountHandler(_database, _clock, _logs.CreateLogger<DisableAccountHandler>())
            .HandleAsync(subject, user, expectedVersion, correlation, ct);
    /// <summary>Changes a role without exposing domain/persistence implementation to transport.</summary>
    public async Task<PlatformWriteResult> ChangeRoleAsync(TenantContext subject, Guid id, string? version, bool enabled,
        PermissionRuleInput[] rules, Guid correlation, CancellationToken ct)
    {
        var result = await new ChangeRoleHandler(_database, _clock, _logs.CreateLogger<ChangeRoleHandler>(), _operation)
            .HandleAsync(subject, id, version, enabled, Grants(rules), correlation, ct);
        return new(Map(result.Decision), result.Version);
    }
    /// <summary>Reads only the reviewed nonsecret settings catalog.</summary>
    public Task<SettingsPage> SettingsAsync(TenantContext subject, int offset, int limit, CancellationToken ct, string? kind = null) =>
        new QuerySettings(_database, _clock).ReadAsync(subject, offset, limit, ct, kind);
    /// <summary>Changes one typed catalog scalar without granting physical approval.</summary>
    public async Task<PlatformWriteResult> ChangeSettingAsync(TenantContext subject, Guid id, string? version, string value, Guid correlation, CancellationToken ct)
    {
        var result = await new ChangeSettingHandler(_database, _clock, _logs.CreateLogger<ChangeSettingHandler>(), _operation)
            .HandleAsync(subject, id, version, value, correlation, ct);
        return new(Map(result.Decision), result.Version);
    }
    /// <summary>Reads immutable audit metadata after current field/data-scope authorization.</summary>
    public Task<AuditPage> AuditAsync(TenantContext subject, DateTimeOffset from, DateTimeOffset to, int offset, int limit, CancellationToken ct,
        int? action = null, Guid? correlation = null) =>
        new QueryAudit(_database, _clock, _logs.CreateLogger<QueryAudit>()).ReadAsync(subject, from, to, offset, limit, ct,
            action is null ? null : (PlatformAuditAction)action.Value, correlation);
    /// <summary>Reads login security metadata without inventing Own ownership from mutable account mappings.</summary>
    public Task<LoginEventPage> LoginEventsAsync(TenantContext subject, DateTimeOffset from, DateTimeOffset to, int offset, int limit,
        LoginEventKind? kind, Guid? correlation, CancellationToken ct) =>
        new QueryLoginEvents(_database, _clock, _logs.CreateLogger<QueryLoginEvents>()).ReadAsync(subject, from, to, offset, limit, kind, correlation, ct);
    /// <summary>Reads only current-authorized Platform Outbox metadata; payloads are never projected.</summary>
    public Task<OutboxQueryPage> OutboxAsync(TenantContext subject, int offset, int limit, CancellationToken ct) =>
        new QueryOutbox(_database, _clock).ReadAsync(subject, offset, limit, ct);
    /// <summary>Disposes the owned module unit of work.</summary>
    public ValueTask DisposeAsync() => _database.DisposeAsync();
    private LoginSourceDbContext Sources() => new(new DbContextOptionsBuilder<LoginSourceDbContext>().UseMySQL(_connection,
        mysql => mysql.MigrationsHistoryTable("__WesLoginSourceMigrations")).Options);
    private static PlatformDecision Map(SettingDecision decision) => decision switch
    {
        SettingDecision.Changed => PlatformDecision.Changed,
        SettingDecision.Forbidden => PlatformDecision.Forbidden,
        SettingDecision.VersionRequired => PlatformDecision.VersionRequired,
        SettingDecision.VersionConflict => PlatformDecision.VersionConflict,
        SettingDecision.UnknownSetting => PlatformDecision.NotFound,
        SettingDecision.InvalidValue => PlatformDecision.Invalid,
        SettingDecision.Unverified => PlatformDecision.Unverified,
        SettingDecision.IdempotencyConflict => PlatformDecision.IdempotencyConflict,
        _ => throw new InvalidOperationException("Unknown settings owner outcome.")
    };
    private static PasswordEvidence Evidence(PasswordBreachEvidence evidence) => evidence switch
    {
        PasswordBreachEvidence.Clear => PasswordEvidence.Clear,
        PasswordBreachEvidence.Prohibited => PasswordEvidence.Prohibited,
        _ => throw new ArgumentOutOfRangeException(nameof(evidence))
    };
    private static PermissionGrant[] Grants(PermissionRuleInput[]? rules)
    {
        if (rules is null || rules.Length > 100 || rules.Any(rule => rule is null || rule.Departments is null
            || rule.Fields is null || rule.Departments.Length > 100 || rule.Fields.Length > 100))
            throw new ArgumentException("Role grants require bounded explicit scope and field sets.");
        return rules.Select(rule => new PermissionGrant(rule.Permission, (DataScope)rule.Scope, rule.Departments.ToImmutableHashSet(),
            rule.Fields.ToImmutableHashSet(StringComparer.Ordinal))).ToArray();
    }
}
