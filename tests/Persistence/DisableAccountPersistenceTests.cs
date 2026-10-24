using System.Collections.Immutable;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using MySql.Data.MySqlClient;
using Wes.Platform.Domain;
using Wes.Platform.Features.Authorization;
using Wes.Platform.Features.Identity;
using Wes.Platform.Features.Tenancy;
using Wes.Platform.Infrastructure.Persistence;
using Xunit;

namespace Wes.Persistence.Tests;

/// <summary>Real current authorization, target/family CAS, audit rollback and tenant isolation for management disable.</summary>
[Collection("PlatformPersistence")]
[Trait("Provider", "MySQL")]
public sealed class DisableAccountPersistenceTests : IAsyncLifetime
{
    private static readonly DateTimeOffset Start = new(2026, 10, 24, 7, 0, 0, TimeSpan.Zero);
    private const string Hash = "$2a$12$N9qo8uLOickgx2ZMRZoMyebq.QrfAcQ2j7P1iiG.cM44tPZp1iuQS";
    private readonly Guid _tenant = Guid.NewGuid();
    private readonly Guid _otherTenant = Guid.NewGuid();
    private readonly Guid _admin = Guid.NewGuid();
    private readonly Guid _target = Guid.NewGuid();
    private string _connection = "";
    private bool _migrated;

    /// <summary>Uses only the previously reviewed owner schema and authorized isolated database.</summary>
    public async Task InitializeAsync()
    {
        _connection = MySqlTestConnection.LoadAuthorizedTestConnection();
        await using var database = Open();
        await database.Database.MigrateAsync();
        _migrated = true;
    }

    /// <summary>Both target families and credential share an audited commit; unrelated owners and tenants remain unchanged.</summary>
    [Fact]
    [Trait("CaseId", "IDENTITY-DISABLE-OWNER-001")]
    public async Task DisableIsAtomicScopedAuditedAndEffectIdempotent()
    {
        var (context, version) = await SeedAsync();
        await using (var other = Open(_otherTenant))
        {
            other.PasswordCredentials.Add(NewCredential(_otherTenant));
            other.Sessions.Add(new Session(_otherTenant, _target, SessionClient.Web, Start, RefreshSecret.Generate().Digest));
            await other.SaveChangesAsync();
        }
        var correlation = Guid.NewGuid();
        await using (var write = Open())
        {
            Assert.Equal(AccountDisableDecision.Completed, await Handler(write).HandleAsync(context, _target, version, correlation));
            Assert.Empty(write.ChangeTracker.Entries());
            Assert.Equal(AccountDisableDecision.Completed, await Handler(write).HandleAsync(context, _target, version, Guid.NewGuid()));
        }
        await using var verify = Open();
        var credential = await verify.PasswordCredentials.SingleAsync();
        Assert.False(credential.Enabled);
        Assert.NotEqual(version, credential.Version);
        Assert.Equal(Start, credential.ChangedAt);
        Assert.Single(credential.Recent);
        Assert.All(await verify.Sessions.Where(item => item.UserId == _target).ToListAsync(), family => Assert.NotNull(family.RevokedAt));
        Assert.All(await verify.Sessions.Where(item => item.UserId != _target).ToListAsync(), family => Assert.Null(family.RevokedAt));
        var audits = await verify.Audit.ToListAsync();
        Assert.Equal(3, audits.Count);
        var disabled = Assert.Single(audits, item => item.Action == PlatformAuditAction.AccountDisabled);
        Assert.Equal(_admin, disabled.ActorId);
        Assert.Equal(_target, disabled.TargetId);
        Assert.Equal(version, disabled.BeforeVersion);
        Assert.Equal(credential.Version, disabled.AfterVersion);
        Assert.All(audits, item => Assert.Equal(correlation, item.CorrelationId));
        foreach (var family in await verify.Sessions.Where(item => item.UserId == _target).ToListAsync())
            Assert.Equal(family.Version, Assert.Single(audits, item => item.TargetId == family.Id).AfterVersion);
        await using var foreign = Open(_otherTenant);
        Assert.True((await foreign.PasswordCredentials.SingleAsync()).Enabled);
        Assert.Null((await foreign.Sessions.SingleAsync()).RevokedAt);
        Assert.Empty(await foreign.Audit.ToListAsync());
    }

    /// <summary>Field and resource range checks are current owner facts, not administrator labels or guessed department membership.</summary>
    [Theory]
    [InlineData("no-grant")]
    [InlineData("wrong-field")]
    [InlineData("own-other")]
    [InlineData("department")]
    [InlineData("role-disabled")]
    [InlineData("legacy-session")]
    [InlineData("idle")]
    [InlineData("unknown-session")]
    [Trait("CaseId", "IDENTITY-DISABLE-SCOPE-001")]
    public async Task MissingCurrentFieldAuthorityDoesNotMutateTarget(string variant)
    {
        var (context, version) = await SeedAsync(variant);
        if (variant == "unknown-session") context = context with { SessionId = Guid.NewGuid() };
        await using var write = Open();
        Assert.Equal(AccountDisableDecision.Rejected,
            await Handler(write, variant == "idle" ? Start.AddSeconds(900) : null).HandleAsync(context, _target, version, Guid.NewGuid()));
        Assert.Empty(write.ChangeTracker.Entries());
        await AssertUnchangedAsync(version);
    }

    /// <summary>Stale target proof and missing target have stable distinct internal decisions without exposing secret state.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("CaseId", "IDENTITY-DISABLE-SCOPE-001")]
    public async Task UnknownTargetAndStaleVersionDoNotMutate(bool stale)
    {
        var (context, version) = await SeedAsync();
        await using var write = Open();
        Assert.Equal(stale ? AccountDisableDecision.VersionConflict : AccountDisableDecision.Rejected,
            await Handler(write).HandleAsync(context, stale ? _target : Guid.NewGuid(), Guid.NewGuid().ToString("D"), Guid.NewGuid()));
        await AssertUnchangedAsync(version);
    }

    /// <summary>Own scope permits a truly authorized self-disable, which terminates the caller's own ability to keep managing.</summary>
    [Fact]
    [Trait("CaseId", "IDENTITY-DISABLE-OWNER-001")]
    public async Task AuthorizedSelfDisableRevokesCurrentFamilyAndRejectsSubsequentManagement()
    {
        var (context, _) = await SeedAsync("own-other");
        string version;
        await using (var seed = Open())
        {
            var credential = new PasswordCredential(_tenant, _admin, "ADMIN", StoredPasswordHash.Import(PasswordHashScheme.BcryptSha384V1, Hash), Start);
            version = credential.Version;
            seed.PasswordCredentials.Add(credential);
            await seed.SaveChangesAsync();
        }
        await using var write = Open();
        Assert.Equal(AccountDisableDecision.Completed, await Handler(write).HandleAsync(context, _admin, version, Guid.NewGuid()));
        Assert.Equal(AccountDisableDecision.Rejected, await Handler(write).HandleAsync(context, _admin, version, Guid.NewGuid()));
        await using var verify = Open();
        Assert.False((await verify.PasswordCredentials.SingleAsync(item => item.UserId == _admin)).Enabled);
        Assert.True((await verify.PasswordCredentials.SingleAsync(item => item.UserId == _target)).Enabled);
        Assert.Empty(await new CurrentAuthorization(verify, new FixedClock(Start.AddSeconds(2))).ReadGrantsAsync(context));
    }

    /// <summary>Already disabled migration/import state is not a reason to leave an active family alive.</summary>
    [Fact]
    [Trait("CaseId", "IDENTITY-DISABLE-OWNER-001")]
    public async Task AlreadyDisabledWithUnrevokedFamiliesIsFencedAndRepaired()
    {
        var (context, _) = await SeedAsync();
        string version;
        await using (var seed = Open())
        {
            var credential = await seed.PasswordCredentials.SingleAsync();
            credential.SetEnabled(false, Start);
            version = credential.Version;
            await seed.SaveChangesAsync();
        }
        await using var write = Open();
        Assert.Equal(AccountDisableDecision.Completed, await Handler(write).HandleAsync(context, _target, version, Guid.NewGuid()));
        await using var verify = Open();
        Assert.NotEqual(version, (await verify.PasswordCredentials.SingleAsync()).Version);
        Assert.All(await verify.Sessions.Where(item => item.UserId == _target).ToListAsync(), family => Assert.NotNull(family.RevokedAt));
        Assert.Equal(3, await verify.Audit.CountAsync());
    }

    /// <summary>Revoking a manager's role is observed on the next command rather than trusting a previously authorized page.</summary>
    [Fact]
    [Trait("CaseId", "IDENTITY-DISABLE-SCOPE-001")]
    public async Task CurrentRoleRevocationRejectsEvenAnIdempotentRepeat()
    {
        var (context, version) = await SeedAsync();
        await using var write = Open();
        Assert.Equal(AccountDisableDecision.Completed, await Handler(write).HandleAsync(context, _target, version, Guid.NewGuid()));
        await using (var seed = Open())
        {
            var role = await seed.Roles.SingleAsync();
            role.Change(role.Version, false, role.Rules);
            await seed.SaveChangesAsync();
        }
        Assert.Equal(AccountDisableDecision.Rejected, await Handler(write).HandleAsync(context, _target, version, Guid.NewGuid()));
        await using var verify = Open();
        Assert.Equal(3, await verify.Audit.CountAsync());
    }

    /// <summary>A real unique-key audit failure cannot leave Enabled=false or only part of the family set revoked.</summary>
    [Fact]
    [Trait("CaseId", "IDENTITY-DISABLE-FAULT-001")]
    public async Task ActualAuditPkFailureRollsBackEveryTargetMutation()
    {
        var (context, version) = await SeedAsync();
        var existing = new PlatformAudit(_tenant, _admin, _target, Guid.NewGuid(), PlatformAuditAction.AccountDisabled, null, version, Start);
        await using (var seed = Open())
        {
            seed.Audit.Add(existing);
            await seed.SaveChangesAsync();
        }
        await using var write = Open(interceptor: new DuplicateAudit(existing.Id));
        var error = await Assert.ThrowsAsync<DbUpdateException>(() => Handler(write).HandleAsync(context, _target, version, Guid.NewGuid()));
        Assert.Equal(1062, Assert.IsType<MySqlException>(error.GetBaseException()).Number);
        Assert.Empty(write.ChangeTracker.Entries());
        await AssertUnchangedAsync(version, 1);
    }

    /// <summary>Explicit DB failures and cooperative cancellation cannot turn into a completed disable result.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("CaseId", "IDENTITY-DISABLE-FAULT-001")]
    public async Task CancellationOrUnknownSaveErrorIsNotASuccessShapedFallback(bool cancel)
    {
        var (context, version) = await SeedAsync();
        using var cancellation = new CancellationTokenSource();
        var failure = new DbUpdateException("injected disable failure");
        await using var write = Open(interceptor: new FailSave(failure, cancel ? cancellation : null));
        var task = Handler(write).HandleAsync(context, _target, version, Guid.NewGuid(), cancellation.Token);
        if (cancel) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        else Assert.Same(failure, await Assert.ThrowsAsync<DbUpdateException>(() => task));
        Assert.Empty(write.ChangeTracker.Entries());
        await AssertUnchangedAsync(version);
    }

    /// <summary>Guards run before owner queries and preserve callers' existing graphs on rejection.</summary>
    [Fact]
    [Trait("CaseId", "IDENTITY-DISABLE-FAULT-001")]
    public async Task InvalidContextVersionClockAndExternalUnitsOfWorkAreExplicit()
    {
        var (context, version) = await SeedAsync();
        await using var write = Open();
        Task<AccountDisableDecision> Invoke() => Handler(write).HandleAsync(context, _target, version, Guid.NewGuid());
        var credential = await write.PasswordCredentials.SingleAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(Invoke);
        Assert.Same(credential, Assert.Single(write.ChangeTracker.Entries()).Entity);
        write.ChangeTracker.Clear();
        write.Database.AutoTransactionBehavior = AutoTransactionBehavior.Never;
        await Assert.ThrowsAsync<InvalidOperationException>(Invoke);
        write.Database.AutoTransactionBehavior = AutoTransactionBehavior.WhenNeeded;
        await using (var transaction = await write.Database.BeginTransactionAsync())
        {
            await Assert.ThrowsAsync<InvalidOperationException>(Invoke);
            await transaction.RollbackAsync();
        }
        await Assert.ThrowsAsync<ArgumentException>(() => Handler(write).HandleAsync(context, _target, "bad", Guid.NewGuid()));
        await Assert.ThrowsAsync<ArgumentException>(() => Handler(write).HandleAsync(context, Guid.Empty, version, Guid.NewGuid()));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Handler(write).HandleAsync(context with { TenantId = _otherTenant },
            _target, version, Guid.NewGuid()));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Handler(write).HandleAsync(context, _target, version, Guid.NewGuid(),
            new CancellationToken(true)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Handler(write, Start.AddTicks(-10))
            .HandleAsync(context, _target, version, Guid.NewGuid()));
        using var scope = new System.Transactions.TransactionScope(System.Transactions.TransactionScopeAsyncFlowOption.Enabled);
        await Assert.ThrowsAsync<InvalidOperationException>(Invoke);
    }

    /// <summary>Two truly staged disables race on credential and families, with no duplicate committed audit chain.</summary>
    [Fact]
    [Trait("CaseId", "IDENTITY-DISABLE-CONCURRENT-001")]
    public async Task ConcurrentDisablesHaveOneCommitAndOneExplicitVersionConflict()
    {
        var (context, version) = await SeedAsync();
        var barrier = new SaveBarrier();
        await using var first = Open(interceptor: barrier);
        await using var second = Open(interceptor: barrier);
        var results = await Task.WhenAll(Handler(first).HandleAsync(context, _target, version, Guid.NewGuid()),
            Handler(second).HandleAsync(context, _target, version, Guid.NewGuid()));
        Assert.Single(results, result => result == AccountDisableDecision.Completed);
        Assert.Single(results, result => result == AccountDisableDecision.VersionConflict);
        await using var verify = Open();
        Assert.False((await verify.PasswordCredentials.SingleAsync()).Enabled);
        Assert.Equal(3, await verify.Audit.CountAsync());
        Assert.Empty(first.ChangeTracker.Entries());
        Assert.Empty(second.ChangeTracker.Entries());
    }

    /// <summary>Refresh first requires an explicit disable retry; disable first leaves no rotatable family.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("CaseId", "IDENTITY-DISABLE-CONCURRENT-001")]
    public async Task DisableAndRefreshHonorBothCommitOrders(bool disableFirst)
    {
        var (context, version) = await SeedAsync();
        var secret = RefreshSecret.Generate();
        Guid id;
        await using (var seed = Open())
        {
            var family = await seed.Sessions.Include(item => item.Credentials)
                .SingleAsync(item => item.UserId == _target && item.Client == SessionClient.Web);
            id = family.Id;
            Assert.Equal(RefreshDecision.Rotated, family.Rotate(family.Credentials.Single().Digest, secret.Digest, Start));
            await seed.SaveChangesAsync();
        }
        var staged = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var committed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var disable = Open(interceptor: new OrderedSave(disableFirst, staged, committed));
        await using var refresh = Open(interceptor: new OrderedSave(!disableFirst, staged, committed));
        var disableTask = Handler(disable).HandleAsync(context, _target, version, Guid.NewGuid());
        var refreshTask = new RefreshSessionHandler(refresh, new FixedClock(Start.AddSeconds(1)),
            NullLogger<RefreshSessionHandler>.Instance).HandleAsync(id, secret.Value);
        await Task.WhenAll(disableTask, refreshTask);
        Assert.Equal(disableFirst ? AccountDisableDecision.Completed : AccountDisableDecision.VersionConflict, await disableTask);
        Assert.Equal(disableFirst ? RefreshDecision.Inactive : RefreshDecision.Rotated, (await refreshTask).Decision);
        if (!disableFirst)
        {
            await AssertUnchangedAsync(version);
            await using var retry = Open();
            Assert.Equal(AccountDisableDecision.Completed,
                await Handler(retry, Start.AddSeconds(2)).HandleAsync(context, _target, version, Guid.NewGuid()));
        }
        await using var verify = Open();
        Assert.False((await verify.PasswordCredentials.SingleAsync()).Enabled);
        Assert.Equal(3, await verify.Audit.CountAsync());
        Assert.All(await verify.Sessions.Where(item => item.UserId == _target).ToListAsync(), family => Assert.NotNull(family.RevokedAt));
        Assert.Empty(await new CurrentAuthorization(verify, new FixedClock(Start.AddSeconds(3)))
            .ReadGrantsAsync(new TenantContext(_tenant, _target, id)));
    }

    /// <summary>Historical revoked families keep their versions while idle/expired target families are explicitly terminated.</summary>
    [Fact]
    [Trait("CaseId", "IDENTITY-DISABLE-OWNER-001")]
    public async Task AlreadyRevokedFamiliesAreNotReauditedAndExpiredFamiliesAreTerminated()
    {
        var (context, version) = await SeedAsync();
        Guid revokedId;
        string revokedVersion;
        await using (var seed = Open())
        {
            var family = await seed.Sessions.SingleAsync(item => item.UserId == _target && item.Client == SessionClient.Web);
            revokedId = family.Id;
            family.Revoke(Start);
            revokedVersion = family.Version;
            var admin = await seed.Sessions.Include(item => item.Credentials).SingleAsync(item => item.UserId == _admin);
            admin.Revoke(Start);
            var fresh = new Session(_tenant, _admin, SessionClient.Web, Start.AddDays(2), RefreshSecret.Generate().Digest);
            context = context with { SessionId = fresh.Id };
            seed.Sessions.Add(fresh);
            await seed.SaveChangesAsync();
        }
        await using var write = Open();
        Assert.Equal(AccountDisableDecision.Completed, await Handler(write, Start.AddDays(2).AddSeconds(1))
            .HandleAsync(context, _target, version, Guid.NewGuid()));
        await using var verify = Open();
        Assert.Equal(revokedVersion, (await verify.Sessions.SingleAsync(item => item.Id == revokedId)).Version);
        Assert.DoesNotContain(await verify.Audit.ToListAsync(), item => item.TargetId == revokedId);
        Assert.Equal(2, await verify.Audit.CountAsync());
        Assert.All(await verify.Sessions.Where(item => item.UserId == _target).ToListAsync(), item => Assert.NotNull(item.RevokedAt));
    }

    private PasswordCredential NewCredential(Guid tenant) => new(tenant, _target, "OPERATOR",
        StoredPasswordHash.Import(PasswordHashScheme.BcryptSha384V1, Hash), Start);

    private async Task<(TenantContext Context, string Version)> SeedAsync(string variant = "")
    {
        await using var seed = Open();
        DirectoryTestFixture.Add(seed, _admin, _target);
        var credential = NewCredential(_tenant);
        var current = new Session(_tenant, _admin, SessionClient.Web, Start, RefreshSecret.Generate().Digest, variant == "legacy-session");
        var scope = variant == "own-other" ? DataScope.Own : variant == "department" ? DataScope.Departments : DataScope.All;
        var role = new AccessRole(_tenant, "manager", variant == "no-grant" ? [] : [new PermissionGrant("platform.users.write", scope,
            scope == DataScope.Departments ? ImmutableHashSet.Create(Guid.NewGuid()) : ImmutableHashSet<Guid>.Empty,
            ImmutableHashSet.Create(variant == "wrong-field" ? "Name" : "Enabled"))]);
        if (variant == "role-disabled") role.Change(role.Version, false, role.Rules);
        seed.Roles.Add(role);
        seed.Memberships.Add(new RoleMembership(_tenant, _admin, role.Id));
        seed.Memberships.Add(new RoleMembership(_tenant, _target, role.Id));
        seed.PasswordCredentials.Add(credential);
        seed.Sessions.AddRange(current, new Session(_tenant, _target, SessionClient.Web, Start, RefreshSecret.Generate().Digest),
            new Session(_tenant, _target, SessionClient.Pda, Start, RefreshSecret.Generate().Digest));
        await seed.SaveChangesAsync();
        return (new TenantContext(_tenant, _admin, current.Id), credential.Version);
    }

    private async Task AssertUnchangedAsync(string version, int auditCount = 0)
    {
        await using var verify = Open();
        var credential = await verify.PasswordCredentials.SingleAsync();
        Assert.True(credential.Enabled);
        Assert.Equal(version, credential.Version);
        Assert.All(await verify.Sessions.ToListAsync(), family => Assert.Null(family.RevokedAt));
        Assert.Equal(auditCount, await verify.Audit.CountAsync());
    }

    private PlatformDbContext Open(Guid? tenant = null, IInterceptor? interceptor = null)
    {
        MySqlTestConnection.ValidateTarget(_connection);
        var options = new DbContextOptionsBuilder<PlatformDbContext>()
            .UseMySQL(_connection, mysql => mysql.MigrationsHistoryTable("__WesPlatformMigrations"));
        if (interceptor is not null) options.AddInterceptors(interceptor);
        return new PlatformDbContext(options.Options, tenant ?? _tenant);
    }

    private static DisableAccountHandler Handler(PlatformDbContext database, DateTimeOffset? now = null) =>
        new(database, new FixedClock(now ?? Start.AddSeconds(1)), NullLogger<DisableAccountHandler>.Instance);

    /// <summary>Only exact random fixture tenant scopes are removed, with audited history cleanup restricted to this test assembly.</summary>
    public async Task DisposeAsync()
    {
        if (!_migrated) return;
        foreach (var tenant in new[] { _tenant, _otherTenant })
        {
            await using var database = Open(tenant);
            await database.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM platform_audit WHERE TenantId = {tenant.ToString("D")}");
            database.PasswordCredentials.RemoveRange(await database.PasswordCredentials.ToListAsync());
            database.Sessions.RemoveRange(await database.Sessions.ToListAsync());
            database.Memberships.RemoveRange(await database.Memberships.ToListAsync());
            database.Roles.RemoveRange(await database.Roles.ToListAsync());
            await DirectoryTestFixture.RemoveAsync(database);
            await database.SaveChangesAsync();
        }
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class DuplicateAudit(Guid id) : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            eventData.Context!.ChangeTracker.Entries<PlatformAudit>().First(item => item.State == EntityState.Added)
                .Property(item => item.Id).CurrentValue = id;
            return ValueTask.FromResult(result);
        }
    }

    private sealed class FailSave(DbUpdateException error, CancellationTokenSource? cancellation) : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (cancellation is null) throw error;
            cancellation.Cancel();
            cancellationToken.ThrowIfCancellationRequested();
            throw new InvalidOperationException("Expected cooperative cancellation.");
        }
    }

    private sealed class SaveBarrier : SaveChangesInterceptor
    {
        private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _arrivals;
        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _arrivals) >= 2) _ready.TrySetResult();
            await _ready.Task.WaitAsync(TimeSpan.FromSeconds(15), cancellationToken);
            return result;
        }
    }

    private sealed class OrderedSave(bool first, TaskCompletionSource staged, TaskCompletionSource committed) : SaveChangesInterceptor
    {
        private bool _participated;
        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (_participated) return result;
            _participated = true;
            if (first) await staged.Task.WaitAsync(TimeSpan.FromSeconds(15), cancellationToken);
            else
            {
                staged.TrySetResult();
                await committed.Task.WaitAsync(TimeSpan.FromSeconds(15), cancellationToken);
            }
            return result;
        }
        public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
        {
            if (first) committed.TrySetResult();
            return ValueTask.FromResult(result);
        }
    }
}
