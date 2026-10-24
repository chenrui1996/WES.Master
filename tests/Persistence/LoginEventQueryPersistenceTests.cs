using System.Collections.Immutable;
using System.Data.Common;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using MySql.Data.MySqlClient;
using Wes.Platform.Domain;
using Wes.Platform.Features.Audit;
using Wes.Platform.Features.Authorization;
using Wes.Platform.Features.Identity;
using Wes.Platform.Features.Tenancy;
using Wes.Platform.Infrastructure.Persistence;
using Xunit;

namespace Wes.Persistence.Tests;

/// <summary>Real MySQL security-event projection, scope/field authorization and explicit failures.</summary>
[Collection("PlatformPersistence")]
[Trait("Provider", "MySQL")]
public sealed class LoginEventQueryPersistenceTests : IAsyncLifetime
{
    private static readonly DateTimeOffset Now = new(2026, 10, 24, 9, 0, 0, TimeSpan.Zero);
    private static readonly string Account = new('A', 64);
    private static readonly string Source = new('B', 64);
    private static readonly ImmutableHashSet<string> AllFields =
        ImmutableHashSet.Create("Id", "OccurredAt", "Kind", "CorrelationId", "AccountDigest", "SourceDigest", "SessionId");
    private readonly Guid _tenant = Guid.NewGuid();
    private readonly Guid _foreign = Guid.NewGuid();
    private readonly Guid _actor = Guid.NewGuid();
    private readonly Guid _correlation = Guid.NewGuid();
    private readonly Guid _successSession = Guid.NewGuid();
    private string _connection = "";
    private bool _migrated;

    /// <summary>Only the reviewed owner migrations and explicitly authorized isolated database are used.</summary>
    public async Task InitializeAsync()
    {
        _connection = MySqlTestConnection.LoadAuthorizedTestConnection();
        await using var database = Open();
        await database.Database.MigrateAsync();
        _migrated = true;
    }

    /// <summary>Removing tenant/time filtering, deterministic tie ordering or SQL pagination must fail these literal boundaries.</summary>
    [Fact]
    [Trait("CaseId", "IDENTITY-LOGIN-EVENT-QUERY-001")]
    public async Task StableTenantTimeAndPageProjectionHasNoTrackingOrWrites()
    {
        var context = await SeedAsync();
        await using var read = Open();
        var query = Query(read);
        var page = await query.ReadAsync(context, Now, Now.AddSeconds(1));
        Assert.True(page.Authorized);
        Assert.Equal(3, page.Rows.Length);
        Assert.All(page.Rows, row => Assert.Equal(Now, row.OccurredAt));
        Assert.Equal(page.Rows.Select(row => row.Id).OrderBy(id => id.ToString("D"), StringComparer.Ordinal), page.Rows.Select(row => row.Id));
        var next = await query.ReadAsync(context, Now, Now.AddSeconds(1), offset: 1, limit: 1);
        Assert.Equal(page.Rows[1], Assert.Single(next.Rows));
        Assert.Empty((await query.ReadAsync(context, Now.AddSeconds(2), Now.AddSeconds(3))).Rows);
        Assert.Empty(read.ChangeTracker.Entries());
        Assert.Empty(await read.Audit.ToListAsync());
        Assert.Equal(5, await read.LoginEvents.CountAsync());
        var session = await read.Sessions.SingleAsync();
        Assert.Equal(Now, session.LastActivityAt);
    }

    /// <summary>Finite result and correlation filters must compose before offset/take and preserve authorized nullable fields.</summary>
    [Fact]
    [Trait("CaseId", "IDENTITY-LOGIN-EVENT-QUERY-001")]
    public async Task AuthorizedFiltersAndSafeShapeReturnOnlyMatchingFacts()
    {
        var context = await SeedAsync();
        await using var read = Open();
        var page = await Query(read).ReadAsync(context, Now, Now.AddSeconds(1),
            kind: LoginEventKind.Completed, correlationId: _correlation);
        var row = Assert.Single(page.Rows);
        Assert.True(page.Authorized);
        Assert.Equal(LoginEventKind.Completed, row.Kind);
        Assert.Equal(_correlation, row.CorrelationId);
        Assert.Equal(_successSession, row.SessionId);
        Assert.Equal(Account, row.AccountDigest);
        Assert.Equal(Source, row.SourceDigest);
        Assert.DoesNotContain(Account, row.ToString());
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(row));
        Assert.Equal(7, json.RootElement.EnumerateObject().Count());
        Assert.False(json.RootElement.TryGetProperty("TenantId", out _));
        Assert.False(json.RootElement.TryGetProperty("Password", out _));
        Assert.False(json.RootElement.TryGetProperty("Secret", out _));
    }

    /// <summary>The actual default page stops at 100 rows; filtering and offset still apply before the next bounded result.</summary>
    [Fact]
    [Trait("CaseId", "IDENTITY-LOGIN-EVENT-QUERY-001")]
    public async Task DefaultPageActuallyStopsAtOneHundredMatchingRows()
    {
        var context = await SeedAsync();
        await using (var seed = Open())
        {
            for (var index = 0; index < 105; index++)
                seed.LoginEvents.Add(new LoginSecurityEvent(_tenant, Account, Source, _correlation,
                    LoginEventKind.VersionConflict, Now.AddTicks((index + 1) * 10)));
            await seed.SaveChangesAsync();
        }
        await using var read = Open();
        var query = Query(read);
        var first = await query.ReadAsync(context, Now, Now.AddSeconds(1), kind: LoginEventKind.VersionConflict);
        Assert.Equal(100, first.Rows.Length);
        Assert.Equal(Now.AddTicks(10), first.Rows[0].OccurredAt);
        Assert.Equal(Now.AddTicks(1000), first.Rows[^1].OccurredAt);
        var next = await query.ReadAsync(context, Now, Now.AddSeconds(1), offset: 100, kind: LoginEventKind.VersionConflict);
        Assert.Equal(5, next.Rows.Length);
        Assert.Equal(Now.AddTicks(1010), next.Rows[0].OccurredAt);
        Assert.Equal(Now.AddTicks(1050), next.Rows[^1].OccurredAt);
    }

    /// <summary>Own mandatory columns do not complete an otherwise incomplete All grant.</summary>
    [Fact]
    [Trait("CaseId", "IDENTITY-LOGIN-EVENT-AUTH-001")]
    public async Task OwnMandatoryColumnCannotCompleteAllScope()
    {
        var context = await SeedAsync();
        await SetGrantsAsync([Grant(DataScope.All, AllFields.Remove("Id")), Grant(DataScope.Own, ImmutableHashSet.Create("Id"))]);
        await using var read = Open();
        Assert.False((await Query(read).ReadAsync(context, Now, Now.AddSeconds(1))).Authorized);
    }

    /// <summary>Own/department and unrelated permission fields cannot be combined with All mandatory columns to leak digests.</summary>
    [Fact]
    [Trait("CaseId", "IDENTITY-LOGIN-EVENT-AUTH-001")]
    public async Task CrossScopeFieldCompositionNeverWidensProjectionOrFilterAuthority()
    {
        var context = await SeedAsync();
        await SetGrantsAsync([
            Grant(DataScope.All, ImmutableHashSet.Create("Id", "OccurredAt")),
            Grant(DataScope.Own, AllFields),
            new PermissionGrant("platform.users.write", DataScope.All, ImmutableHashSet<Guid>.Empty, AllFields)]);
        await using var read = Open();
        var query = Query(read);
        var page = await query.ReadAsync(context, Now, Now.AddSeconds(1));
        Assert.True(page.Authorized);
        Assert.Equal(3, page.Rows.Length);
        Assert.All(page.Rows, row =>
        {
            Assert.Null(row.Kind);
            Assert.Null(row.CorrelationId);
            Assert.Null(row.AccountDigest);
            Assert.Null(row.SourceDigest);
            Assert.Null(row.SessionId);
        });
        Assert.False((await query.ReadAsync(context, Now, Now.AddSeconds(1), kind: LoginEventKind.Completed)).Authorized);
        Assert.False((await query.ReadAsync(context, Now, Now.AddSeconds(1), correlationId: _correlation)).Authorized);
        Assert.Empty(read.ChangeTracker.Entries());
    }

    /// <summary>Scope, required columns and current authentication state are enforced even if security events exist.</summary>
    [Theory]
    [InlineData("none")]
    [InlineData("own")]
    [InlineData("department")]
    [InlineData("missing-id")]
    [InlineData("missing-time")]
    [InlineData("disabled-role")]
    [InlineData("legacy")]
    [InlineData("idle")]
    [InlineData("revoked")]
    [InlineData("unknown-session")]
    [Trait("CaseId", "IDENTITY-LOGIN-EVENT-AUTH-001")]
    public async Task DeniedAuthorityIsNotAnAuthorizedEmptyPage(string variant)
    {
        var context = await SeedAsync(variant);
        await using var read = Open();
        var result = await Query(read, variant == "idle" ? Now.AddSeconds(900) : null)
            .ReadAsync(context, Now, Now.AddSeconds(1));
        Assert.False(result.Authorized);
        Assert.Empty(result.Rows);
        Assert.Empty(read.ChangeTracker.Entries());
        Assert.Equal(5, await read.LoginEvents.CountAsync());
    }

    /// <summary>No prior successful read or cached role may authorize the next request after real role revocation.</summary>
    [Fact]
    [Trait("CaseId", "IDENTITY-LOGIN-EVENT-AUTH-001")]
    public async Task CurrentRoleRevocationRejectsTheNextReadOnTheSameContext()
    {
        var context = await SeedAsync();
        await using var read = Open();
        Assert.True((await Query(read).ReadAsync(context, Now, Now.AddSeconds(1))).Authorized);
        await SetGrantsAsync([]);
        Assert.False((await Query(read).ReadAsync(context, Now, Now.AddSeconds(1))).Authorized);
        Assert.Empty(read.ChangeTracker.Entries());
    }

    /// <summary>Invalid query contracts fail explicitly instead of becoming empty pages or silently clamped bounds.</summary>
    [Theory]
    [InlineData("interval")]
    [InlineData("offset")]
    [InlineData("limit-zero")]
    [InlineData("limit-large")]
    [InlineData("kind")]
    [InlineData("correlation")]
    [Trait("CaseId", "IDENTITY-LOGIN-EVENT-FAULT-001")]
    public async Task InvalidBoundsAndFiltersAreExplicit(string variant)
    {
        var context = await SeedAsync();
        await using var read = Open();
        await Assert.ThrowsAnyAsync<ArgumentException>(() => Query(read).ReadAsync(context, Now,
            variant == "interval" ? Now : Now.AddSeconds(1), offset: variant == "offset" ? -1 : 0,
            limit: variant == "limit-zero" ? 0 : variant == "limit-large" ? 101 : 100,
            kind: variant == "kind" ? (LoginEventKind)999 : null,
            correlationId: variant == "correlation" ? Guid.Empty : null));
    }

    /// <summary>Owner mismatch, stale transaction snapshots and caller dirty state are rejected without clearing that caller's work.</summary>
    [Fact]
    [Trait("CaseId", "IDENTITY-LOGIN-EVENT-FAULT-001")]
    public async Task ContextAndUnitOfWorkGuardsPreserveCallerState()
    {
        var context = await SeedAsync();
        await using var read = Open();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Query(read).ReadAsync(context with { TenantId = _foreign }, Now, Now.AddSeconds(1)));
        await using (var transaction = await read.Database.BeginTransactionAsync())
            await Assert.ThrowsAsync<InvalidOperationException>(() => Query(read).ReadAsync(context, Now, Now.AddSeconds(1)));
        using (var ambient = new System.Transactions.TransactionScope(System.Transactions.TransactionScopeAsyncFlowOption.Enabled))
            await Assert.ThrowsAsync<InvalidOperationException>(() => Query(read).ReadAsync(context, Now, Now.AddSeconds(1)));
        var pending = new LoginSecurityEvent(_tenant, Account, Source, Guid.NewGuid(), LoginEventKind.Locked, Now);
        read.LoginEvents.Add(pending);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Query(read).ReadAsync(context, Now, Now.AddSeconds(1)));
        Assert.Equal(EntityState.Added, read.Entry(pending).State);
    }

    /// <summary>Pre-cancellation and a real missing-table query cannot masquerade as successful empty security evidence.</summary>
    [Fact]
    [Trait("CaseId", "IDENTITY-LOGIN-EVENT-FAULT-001")]
    public async Task CancellationAndActualSqlFailurePropagate()
    {
        var context = await SeedAsync();
        await using var read = Open();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Query(read).ReadAsync(context, Now, Now.AddSeconds(1), cancellationToken: new CancellationToken(true)));
        await using var broken = Open(broken: true);
        var error = await Assert.ThrowsAsync<MySqlException>(() => Query(broken).ReadAsync(context, Now, Now.AddSeconds(1)));
        Assert.Equal(1146, error.Number);
        Assert.Empty(broken.ChangeTracker.Entries());
    }

    private async Task<TenantContext> SeedAsync(string variant = "")
    {
        await using var seed = Open();
        DirectoryTestFixture.Add(seed, _actor);
        var session = new Session(_tenant, _actor, SessionClient.Web, Now, RefreshSecret.Generate().Digest, variant == "legacy");
        if (variant == "revoked") session.Revoke(Now);
        var fields = variant == "missing-id" ? AllFields.Remove("Id") : variant == "missing-time" ? AllFields.Remove("OccurredAt") : AllFields;
        var scope = variant == "own" ? DataScope.Own : variant == "department" ? DataScope.Departments : DataScope.All;
        var role = new AccessRole(_tenant, "security-reader", variant == "none" ? [] : [Grant(scope, fields)]);
        if (variant == "disabled-role") role.Change(role.Version, false, role.Rules);
        seed.Roles.Add(role);
        seed.Memberships.Add(new RoleMembership(_tenant, _actor, role.Id));
        seed.Sessions.Add(session);
        seed.LoginEvents.AddRange(
            new LoginSecurityEvent(_tenant, Account, Source, _correlation, LoginEventKind.Locked, Now.AddTicks(-10)),
            new LoginSecurityEvent(_tenant, Account, Source, _correlation, LoginEventKind.Completed, Now, _successSession),
            new LoginSecurityEvent(_tenant, Account, Source, _correlation, LoginEventKind.InvalidCredential, Now),
            new LoginSecurityEvent(_tenant, Account, Source, Guid.NewGuid(), LoginEventKind.Locked, Now),
            new LoginSecurityEvent(_tenant, Account, Source, _correlation, LoginEventKind.Locked, Now.AddSeconds(1)));
        await seed.SaveChangesAsync();
        await using var foreign = Open(_foreign);
        foreign.LoginEvents.Add(new LoginSecurityEvent(_foreign, Account, Source, _correlation, LoginEventKind.Completed, Now, Guid.NewGuid()));
        await foreign.SaveChangesAsync();
        return new TenantContext(_tenant, _actor, variant == "unknown-session" ? Guid.NewGuid() : session.Id);
    }

    private static PermissionGrant Grant(DataScope scope, ImmutableHashSet<string> fields) =>
        new("platform.audit.read", scope, scope == DataScope.Departments ? ImmutableHashSet.Create(Guid.NewGuid()) : ImmutableHashSet<Guid>.Empty, fields);

    private async Task SetGrantsAsync(IEnumerable<PermissionGrant> grants)
    {
        await using var write = Open();
        var role = await write.Roles.SingleAsync();
        role.Change(role.Version, true, grants);
        await write.SaveChangesAsync();
    }

    private PlatformDbContext Open(Guid? tenant = null, bool broken = false)
    {
        MySqlTestConnection.ValidateTarget(_connection);
        var options = new DbContextOptionsBuilder<PlatformDbContext>().UseMySQL(_connection,
            mysql => mysql.MigrationsHistoryTable("__WesPlatformMigrations"));
        if (broken) options.AddInterceptors(new MissingEventTable());
        return new PlatformDbContext(options.Options, tenant ?? _tenant);
    }

    private static QueryLoginEvents Query(PlatformDbContext database, DateTimeOffset? now = null) =>
        new(database, new FixedClock(now ?? Now.AddSeconds(1)), NullLogger<QueryLoginEvents>.Instance);

    /// <summary>Removes only both exact random fixture tenants; immutable evidence cleanup is isolated to this test assembly.</summary>
    public async Task DisposeAsync()
    {
        if (!_migrated) return;
        foreach (var tenant in new[] { _tenant, _foreign })
        {
            await using var database = Open(tenant);
            await database.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM platform_login_events WHERE TenantId = {tenant.ToString("D")}");
            database.Memberships.RemoveRange(await database.Memberships.ToListAsync());
            database.Roles.RemoveRange(await database.Roles.ToListAsync());
            database.Sessions.RemoveRange(await database.Sessions.ToListAsync());
            await DirectoryTestFixture.RemoveAsync(database);
            await database.SaveChangesAsync();
        }
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class MissingEventTable : DbCommandInterceptor
    {
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            command.CommandText = command.CommandText.Replace("`platform_login_events`", "`missing_login_event_query_fixture`", StringComparison.Ordinal);
            return ValueTask.FromResult(result);
        }
    }
}
