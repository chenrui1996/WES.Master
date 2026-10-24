using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MySql.Data.MySqlClient;
using Wes.Platform.Domain;
using Wes.Platform.Features.Identity;
using Wes.Platform.Features.Tenancy;
using Wes.Platform.Infrastructure.Persistence;
using Xunit;

namespace Wes.Persistence.Tests;

/// <summary>Real MySQL credential CAS, five-value history, audited revocation and rollback under owner-scoped fixtures.</summary>
[Collection("PlatformPersistence")]
[Trait("Provider", "MySQL")]
public sealed class PasswordChangePersistenceTests : IAsyncLifetime
{
    private static readonly DateTimeOffset Start = new(2026, 10, 24, 5, 0, 0, TimeSpan.Zero);
    private const string Initial = "WES reference fixture password";
    private const string Candidate = "new reviewed password for WES";
    private const string Legacy = "$2a$12$N9qo8uLOickgx2ZMRZoMyeA6a9XHSXAtYQSvRT4Sc0ziVCqOGHZUS";
    private readonly Guid _tenant = Guid.NewGuid();
    private readonly Guid _otherTenant = Guid.NewGuid();
    private readonly Guid _actor = Guid.NewGuid();
    private string _connection = "";
    private bool _migrated;

    /// <summary>Uses only the authorized isolated database after the additive migration scope gate has passed.</summary>
    public async Task InitializeAsync()
    {
        _connection = MySqlTestConnection.LoadAuthorizedTestConnection();
        await using var database = Open();
        await database.Database.MigrateAsync();
        _migrated = true;
    }

    /// <summary>Credential/history, both owned families and their version-linked audit survive a new context together.</summary>
    [Fact]
    [Trait("CaseId", "IDENTITY-PASSWORD-CHANGE-001")]
    public async Task ChangePersistsNewSchemeHistoryAndRevokesOnlyAllOwnedFamilies()
    {
        var (context, version) = await SeedAsync();
        var (foreignContext, foreignVersion) = await SeedAsync(_otherTenant);
        var correlation = Guid.NewGuid();
        await using (var write = Open())
        {
            Assert.Equal(PasswordChangeDecision.Completed,
                (await Handler(write).HandleAsync(context, version, Initial, Candidate, PasswordEvidence.Clear, correlation)).Decision);
            Assert.Empty(write.ChangeTracker.Entries());
        }
        await using var verify = Open();
        var credential = await verify.PasswordCredentials.AsNoTracking().SingleAsync();
        Assert.NotEqual(version, credential.Version);
        Assert.Equal(Start.AddSeconds(1), credential.ChangedAt);
        Assert.Equal(CredentialVerification.Verified, credential.Verify(Candidate));
        Assert.Equal(CredentialVerification.Invalid, credential.Verify(Initial));
        Assert.Equal(PasswordEvidence.Prohibited, credential.CheckHistory(Initial));
        Assert.Equal(2, credential.Recent.Count);
        Assert.Equal(PasswordHashScheme.BcryptSha384V1, credential.Recent[0].Scheme);
        var families = await verify.Sessions.AsNoTracking().ToListAsync();
        Assert.All(families.Where(item => item.UserId == _actor), item => Assert.NotNull(item.RevokedAt));
        Assert.Null(Assert.Single(families, item => item.UserId != _actor).RevokedAt);
        var audits = await verify.Audit.AsNoTracking().ToListAsync();
        Assert.Equal(3, audits.Count);
        var changed = Assert.Single(audits, item => item.Action == PlatformAuditAction.PasswordChanged);
        Assert.Equal(_actor, changed.ActorId);
        Assert.Equal(_actor, changed.TargetId);
        Assert.Equal(version, changed.BeforeVersion);
        Assert.Equal(credential.Version, changed.AfterVersion);
        Assert.All(audits, item => Assert.Equal(correlation, item.CorrelationId));
        foreach (var family in families.Where(item => item.UserId == _actor))
            Assert.Equal(family.Version, Assert.Single(audits, item => item.TargetId == family.Id).AfterVersion);
        await using var foreign = Open(_otherTenant);
        Assert.Equal(foreignVersion, (await foreign.PasswordCredentials.SingleAsync()).Version);
        Assert.Null((await foreign.Sessions.SingleAsync(item => item.Id == foreignContext.SessionId)).RevokedAt);
        Assert.Empty(await foreign.Audit.ToListAsync());
    }

    /// <summary>Current-owner proof and active-family restrictions never become a password oracle or a cross-user revoke.</summary>
    [Theory]
    [InlineData("wrong-password", PasswordChangeDecision.Rejected)]
    [InlineData("wrong-user", PasswordChangeDecision.Rejected)]
    [InlineData("unknown-session", PasswordChangeDecision.Rejected)]
    [InlineData("foreign-tenant", PasswordChangeDecision.Rejected)]
    [InlineData("idle", PasswordChangeDecision.Rejected)]
    [InlineData("stale", PasswordChangeDecision.VersionConflict)]
    [Trait("CaseId", "IDENTITY-PASSWORD-SCOPE-001")]
    public async Task InvalidProofOrScopeDoesNotWrite(string variant, PasswordChangeDecision expected)
    {
        var (context, version) = await SeedAsync();
        if (variant == "wrong-user") context = context with { ActorId = Guid.NewGuid() };
        if (variant == "unknown-session") context = context with { SessionId = Guid.NewGuid() };
        if (variant == "foreign-tenant") context = context with { TenantId = _otherTenant };
        await using var write = Open(context.TenantId);
        var outcome = await Handler(write, variant == "idle" ? Start.AddSeconds(900) : null).HandleAsync(context,
            variant == "stale" ? Guid.NewGuid().ToString("D") : version,
            variant == "wrong-password" ? "wrong test candidate" : Initial, Candidate, PasswordEvidence.Clear, Guid.NewGuid());
        Assert.Equal(expected, outcome.Decision);
        Assert.Empty(write.ChangeTracker.Entries());
        await AssertUnchangedAsync(version);
    }

    /// <summary>External breach clearance cannot replace an actual local history check; failures leave all durable state alone.</summary>
    [Theory]
    [InlineData(PasswordEvidence.Unknown, Candidate, PasswordDecision.EvidenceUnavailable)]
    [InlineData(PasswordEvidence.Prohibited, Candidate, PasswordDecision.Compromised)]
    [InlineData(PasswordEvidence.Clear, Initial, PasswordDecision.RecentlyUsed)]
    [InlineData(PasswordEvidence.Clear, "short", PasswordDecision.InvalidShape)]
    [Trait("CaseId", "IDENTITY-PASSWORD-SCOPE-001")]
    public async Task RejectedPolicyDoesNotRotateOrRevoke(PasswordEvidence breach, string candidate, PasswordDecision expected)
    {
        var (context, version) = await SeedAsync();
        await using var write = Open();
        var outcome = await Handler(write).HandleAsync(context, version, Initial, candidate, breach, Guid.NewGuid());
        Assert.Equal(PasswordChangeDecision.PolicyRejected, outcome.Decision);
        Assert.Equal(expected, outcome.Policy);
        Assert.Empty(write.ChangeTracker.Entries());
        await AssertUnchangedAsync(version);
    }

    /// <summary>Both real transactions stage the same owner versions; only one can set a new credential and audit.</summary>
    [Fact]
    [Trait("CaseId", "IDENTITY-PASSWORD-CONCURRENT-001")]
    public async Task ConcurrentChangesHaveOneCommittedVersionAndNoAutomaticProofReuse()
    {
        var (context, version) = await SeedAsync();
        var barrier = new SaveBarrier();
        await using var first = Open(interceptor: barrier);
        await using var second = Open(interceptor: barrier);
        var results = await Task.WhenAll(
            Handler(first).HandleAsync(context, version, Initial, Candidate, PasswordEvidence.Clear, Guid.NewGuid()),
            Handler(second).HandleAsync(context, version, Initial, Candidate + " alternate", PasswordEvidence.Clear, Guid.NewGuid()));
        Assert.Single(results, item => item.Decision == PasswordChangeDecision.Completed);
        Assert.Single(results, item => item.Decision == PasswordChangeDecision.VersionConflict);
        await using var verify = Open();
        Assert.Equal(2, (await verify.PasswordCredentials.SingleAsync()).Recent.Count);
        Assert.Equal(3, await verify.Audit.CountAsync());
        Assert.All(await verify.Sessions.Where(item => item.UserId == _actor).ToListAsync(), item => Assert.NotNull(item.RevokedAt));
        Assert.Empty(first.ChangeTracker.Entries());
        Assert.Empty(second.ChangeTracker.Entries());
    }

    /// <summary>Refresh first forces an explicit fresh change; change first makes the contender re-read a revoked family.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("CaseId", "IDENTITY-PASSWORD-CONCURRENT-001")]
    public async Task RefreshRaceHonorsBothCommitOrders(bool changeFirst)
    {
        var (context, version) = await SeedAsync();
        var bothStaged = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var committed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secret = RefreshSecret.Generate();
        await using (var seed = Open())
        {
            var family = await seed.Sessions.Include(item => item.Credentials).SingleAsync(item => item.Id == context.SessionId);
            Assert.Equal(RefreshDecision.Rotated, family.Rotate(family.Credentials.Single().Digest, secret.Digest, Start));
            await seed.SaveChangesAsync();
        }
        await using var change = Open(interceptor: new OrderedSave(changeFirst, bothStaged, committed));
        await using var refresh = Open(interceptor: new OrderedSave(!changeFirst, bothStaged, committed));
        var changeTask = Handler(change).HandleAsync(context, version, Initial, Candidate, PasswordEvidence.Clear, Guid.NewGuid());
        var refreshTask = new RefreshSessionHandler(refresh, new FixedClock(Start.AddSeconds(1)),
            NullLogger<RefreshSessionHandler>.Instance).HandleAsync(context.SessionId, secret.Value);
        await Task.WhenAll(changeTask, refreshTask);
        Assert.Equal(changeFirst ? PasswordChangeDecision.Completed : PasswordChangeDecision.VersionConflict, (await changeTask).Decision);
        Assert.Equal(changeFirst ? RefreshDecision.Inactive : RefreshDecision.Rotated, (await refreshTask).Decision);
        if (!changeFirst)
        {
            await AssertUnchangedAsync(version);
            await using var retry = Open();
            Assert.Equal(PasswordChangeDecision.Completed,
                (await Handler(retry, Start.AddSeconds(2)).HandleAsync(context, version, Initial, Candidate,
                    PasswordEvidence.Clear, Guid.NewGuid())).Decision);
        }
        await using var verify = Open();
        Assert.All(await verify.Sessions.Where(item => item.UserId == _actor).ToListAsync(), item => Assert.NotNull(item.RevokedAt));
        Assert.Equal(CredentialVerification.Verified, (await verify.PasswordCredentials.SingleAsync()).Verify(Candidate));
        Assert.Equal(3, await verify.Audit.CountAsync());
    }

    /// <summary>A real audit PK1062 occurs after staging secret and family updates; none can escape the owner transaction.</summary>
    [Fact]
    [Trait("CaseId", "IDENTITY-PASSWORD-FAULT-001")]
    public async Task ActualAuditConstraintFailureRollsBackCredentialAndEveryFamily()
    {
        var (context, version) = await SeedAsync();
        var existing = new PlatformAudit(_tenant, _actor, _actor, Guid.NewGuid(), PlatformAuditAction.PasswordChanged,
            null, version, Start);
        await using (var seed = Open())
        {
            seed.Audit.Add(existing);
            await seed.SaveChangesAsync();
        }
        await using var write = Open(interceptor: new DuplicateAudit(existing.Id));
        var error = await Assert.ThrowsAsync<DbUpdateException>(() =>
            Handler(write).HandleAsync(context, version, Initial, Candidate, PasswordEvidence.Clear, Guid.NewGuid()));
        Assert.Equal(1062, Assert.IsType<MySqlException>(error.GetBaseException()).Number);
        Assert.Empty(write.ChangeTracker.Entries());
        await AssertUnchangedAsync(version, 1);
    }

    /// <summary>Cancellation and unknown storage errors are propagated instead of being disguised as a normal password denial.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("CaseId", "IDENTITY-PASSWORD-FAULT-001")]
    public async Task SaveFailureOrCancellationIsExplicitAndLeavesNoPartialState(bool cancel)
    {
        var (context, version) = await SeedAsync();
        using var cancellation = new CancellationTokenSource();
        var failure = new DbUpdateException("injected owner failure");
        await using var write = Open(interceptor: new FailSave(failure, cancel ? cancellation : null));
        var task = Handler(write).HandleAsync(context, version, Initial, Candidate, PasswordEvidence.Clear, Guid.NewGuid(), cancellation.Token);
        if (cancel) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        else Assert.Same(failure, await Assert.ThrowsAsync<DbUpdateException>(() => task));
        Assert.Empty(write.ChangeTracker.Entries());
        await AssertUnchangedAsync(version);
    }

    /// <summary>Uncommitted caller graphs and transaction overrides cannot delay or split the security commit.</summary>
    [Fact]
    [Trait("CaseId", "IDENTITY-PASSWORD-FAULT-001")]
    public async Task ExternalUnitsOfWorkAndDisabledAtomicityAreRejected()
    {
        var (context, version) = await SeedAsync();
        await using var write = Open();
        Task<PasswordChangeResult> Invoke() => Handler(write).HandleAsync(context, version, Initial, Candidate,
            PasswordEvidence.Clear, Guid.NewGuid());
        var own = await write.PasswordCredentials.SingleAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(Invoke);
        Assert.Same(own, Assert.Single(write.ChangeTracker.Entries()).Entity);
        write.ChangeTracker.Clear();
        write.Database.AutoTransactionBehavior = AutoTransactionBehavior.Never;
        await Assert.ThrowsAsync<InvalidOperationException>(Invoke);
        write.Database.AutoTransactionBehavior = AutoTransactionBehavior.WhenNeeded;
        await using (var transaction = await write.Database.BeginTransactionAsync())
        {
            await Assert.ThrowsAsync<InvalidOperationException>(Invoke);
            await transaction.RollbackAsync();
        }
        using var ambient = new System.Transactions.TransactionScope(System.Transactions.TransactionScopeAsyncFlowOption.Enabled);
        await Assert.ThrowsAsync<InvalidOperationException>(Invoke);
    }

    /// <summary>Broken history is never an empty history; neither its contents nor submitted passwords enter safe logs.</summary>
    [Theory]
    [InlineData("[]")]
    [InlineData("secret-marker")]
    [InlineData("[{\"Scheme\":99,\"Hash\":\"secret-marker\"}]")]
    [InlineData("[{\"Hash\":\"" + Legacy + "\"}]")]
    [Trait("CaseId", "IDENTITY-PASSWORD-FAULT-001")]
    public async Task CorruptHistoryFailsWithoutReflectionOrSuccessfulFallback(string corrupt)
    {
        var (context, version) = await SeedAsync();
        await using (var seed = Open())
        {
            seed.Entry(await seed.PasswordCredentials.SingleAsync()).Property("HistoryData").CurrentValue = corrupt;
            await seed.SaveChangesAsync();
        }
        var logger = new CapturingLogger();
        await using var write = Open();
        var handler = new ChangePasswordHandler(write, new FixedClock(Start.AddSeconds(1)), logger);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            handler.HandleAsync(context, version, Initial, Candidate, PasswordEvidence.Clear, Guid.NewGuid()));
        Assert.DoesNotContain("secret-marker", error.ToString());
        Assert.Empty(write.ChangeTracker.Entries());
        await using var verify = Open();
        Assert.Empty(await verify.Audit.ToListAsync());
        Assert.All(await verify.Sessions.ToListAsync(), item => Assert.Null(item.RevokedAt));
        Assert.All(logger.Messages, message => Assert.DoesNotContain(Initial, message));
    }

    /// <summary>A backwards clock between proof and commit cannot create an audit/revocation earlier than the credential change.</summary>
    [Fact]
    [Trait("CaseId", "IDENTITY-PASSWORD-FAULT-001")]
    public async Task ClockReversalDuringHashingFailsBeforeCommit()
    {
        var (context, version) = await SeedAsync();
        await using var write = Open();
        var handler = new ChangePasswordHandler(write, new SequenceClock(Start.AddSeconds(2), Start.AddSeconds(1)),
            NullLogger<ChangePasswordHandler>.Instance);
        await Assert.ThrowsAsync<InvalidOperationException>(() => handler.HandleAsync(context, version, Initial,
            Candidate, PasswordEvidence.Clear, Guid.NewGuid()));
        Assert.Empty(write.ChangeTracker.Entries());
        await AssertUnchangedAsync(version);
    }

    /// <summary>A family may expire while BCrypt runs; the second clock check must not commit staged secret changes.</summary>
    [Fact]
    [Trait("CaseId", "IDENTITY-PASSWORD-SCOPE-001")]
    public async Task SessionExpiryDuringHashingRejectsWithoutCommitting()
    {
        var (context, version) = await SeedAsync();
        await using var write = Open();
        var handler = new ChangePasswordHandler(write, new SequenceClock(Start.AddSeconds(899), Start.AddSeconds(900)),
            NullLogger<ChangePasswordHandler>.Instance);
        Assert.Equal(PasswordChangeDecision.Rejected, (await handler.HandleAsync(context, version, Initial,
            Candidate, PasswordEvidence.Clear, Guid.NewGuid())).Decision);
        Assert.Empty(write.ChangeTracker.Entries());
        await AssertUnchangedAsync(version);
    }

    /// <summary>Independent contexts preserve the exact five-value boundary instead of resetting or accumulating history on materialization.</summary>
    [Fact]
    [Trait("CaseId", "IDENTITY-PASSWORD-HISTORY-001")]
    public async Task HistoryRetainsExactlyFiveAcrossEveryDatabaseReload()
    {
        await SeedAsync();
        for (var index = 1; index <= 5; index++)
        {
            await using var write = Open();
            var credential = await write.PasswordCredentials.SingleAsync();
            Assert.Equal(PasswordDecision.Accepted, credential.Change($"durable reviewed password {index}",
                PasswordEvidence.Clear, Start.AddSeconds(index)));
            await write.SaveChangesAsync();
        }
        await using var verify = Open();
        var stored = await verify.PasswordCredentials.AsNoTracking().SingleAsync();
        Assert.Equal(5, stored.Recent.Count);
        Assert.Equal(PasswordEvidence.Clear, stored.CheckHistory(Initial));
        Assert.Equal(PasswordEvidence.Prohibited, stored.CheckHistory("durable reviewed password 1"));
        Assert.Equal(PasswordEvidence.Prohibited, stored.CheckHistory("durable reviewed password 5"));
        Assert.Equal(CredentialVerification.Verified, stored.Verify("durable reviewed password 5"));
        Assert.Equal(Start.AddSeconds(5), stored.ChangedAt);
    }

    /// <summary>Previously revoked families retain their version and do not append another revocation audit.</summary>
    [Fact]
    [Trait("CaseId", "IDENTITY-PASSWORD-CHANGE-001")]
    public async Task AlreadyRevokedFamilyIsNotMutatedOrAuditedAgain()
    {
        var (context, version) = await SeedAsync();
        string revokedVersion;
        await using (var seed = Open())
        {
            var revoked = await seed.Sessions.SingleAsync(item => item.UserId == _actor && item.Id != context.SessionId);
            revoked.Revoke(Start);
            revokedVersion = revoked.Version;
            await seed.SaveChangesAsync();
        }
        await using var write = Open();
        Assert.Equal(PasswordChangeDecision.Completed, (await Handler(write).HandleAsync(context, version, Initial,
            Candidate, PasswordEvidence.Clear, Guid.NewGuid())).Decision);
        await using var verify = Open();
        Assert.Equal(2, await verify.Audit.CountAsync());
        Assert.Equal(revokedVersion,
            (await verify.Sessions.SingleAsync(item => item.UserId == _actor && item.Id != context.SessionId)).Version);
    }

    /// <summary>Real denial logs and serialized results have finite safe codes without proof, account or history material.</summary>
    [Fact]
    [Trait("CaseId", "IDENTITY-PASSWORD-FAULT-001")]
    public async Task RejectionLogsAndResponsesExcludeCredentialMaterial()
    {
        var (context, version) = await SeedAsync();
        var logger = new CapturingLogger();
        await using var write = Open();
        var handler = new ChangePasswordHandler(write, new FixedClock(Start.AddSeconds(1)), logger);
        var rejected = await handler.HandleAsync(context, version, "wrong old test secret", Candidate, PasswordEvidence.Clear, Guid.NewGuid());
        var policy = await handler.HandleAsync(context, version, Initial, Candidate, PasswordEvidence.Unknown, Guid.NewGuid());
        Assert.Equal(PasswordChangeDecision.Rejected, rejected.Decision);
        Assert.Equal(PasswordChangeDecision.PolicyRejected, policy.Decision);
        Assert.Equal(2, logger.Messages.Count);
        var output = string.Join('\n', logger.Messages) + System.Text.Json.JsonSerializer.Serialize(new[] { rejected, policy });
        foreach (var secret in new[] { Initial, Candidate, Legacy, "wrong old test secret", "OPERATOR", _actor.ToString("D") })
            Assert.DoesNotContain(secret, output);
        await AssertUnchangedAsync(version);
    }

    /// <summary>Database account uniqueness is tenant composite and cross-tenant attached secret writes remain forbidden.</summary>
    [Fact]
    [Trait("CaseId", "IDENTITY-PASSWORD-MIGRATION-001")]
    public async Task TenantCompositeAccountUniquenessAndWriteScopeAreEnforced()
    {
        await SeedAsync();
        await SeedAsync(_otherTenant);
        await using var duplicate = Open();
        duplicate.PasswordCredentials.Add(NewCredential(_tenant, Guid.NewGuid()));
        var error = await Assert.ThrowsAsync<DbUpdateException>(() => duplicate.SaveChangesAsync());
        Assert.Equal(1062, Assert.IsType<MySqlException>(error.GetBaseException()).Number);
        duplicate.ChangeTracker.Clear();
        duplicate.PasswordCredentials.Add(NewCredential(_otherTenant, Guid.NewGuid()));
        await Assert.ThrowsAsync<InvalidOperationException>(() => duplicate.SaveChangesAsync());
    }

    /// <summary>Validation/cancellation occurs before owner IO, and chronology cannot be silently normalized backwards.</summary>
    [Fact]
    [Trait("CaseId", "IDENTITY-PASSWORD-FAULT-001")]
    public async Task InvalidContextVersionCorrelationClockAndPreCancellationFailExplicitly()
    {
        var (context, version) = await SeedAsync();
        await using var write = Open();
        var handler = Handler(write);
        await Assert.ThrowsAsync<ArgumentException>(() => handler.HandleAsync(context, "bad", Initial, Candidate, PasswordEvidence.Clear, Guid.NewGuid()));
        await Assert.ThrowsAsync<ArgumentException>(() => handler.HandleAsync(context, version, Initial, Candidate, PasswordEvidence.Clear, Guid.Empty));
        await Assert.ThrowsAsync<InvalidOperationException>(() => handler.HandleAsync(context with { TenantId = _otherTenant }, version,
            Initial, Candidate, PasswordEvidence.Clear, Guid.NewGuid()));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => handler.HandleAsync(context, version, Initial, Candidate,
            PasswordEvidence.Clear, Guid.NewGuid(), new CancellationToken(true)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Handler(write, Start.AddTicks(-10)).HandleAsync(context, version,
            Initial, Candidate, PasswordEvidence.Clear, Guid.NewGuid()));
        Assert.Empty(write.ChangeTracker.Entries());
        await AssertUnchangedAsync(version);
    }

    private PasswordCredential NewCredential(Guid tenant, Guid user) => new(tenant, user, "OPERATOR",
        StoredPasswordHash.Import(PasswordHashScheme.SevenBcryptV1, Legacy), Start);

    private async Task<(TenantContext Context, string Version)> SeedAsync(Guid? tenant = null)
    {
        var scope = tenant ?? _tenant;
        await using var seed = Open(scope);
        var credential = NewCredential(scope, _actor);
        var own = new Session(scope, _actor, SessionClient.Web, Start, RefreshSecret.Generate().Digest);
        seed.PasswordCredentials.Add(credential);
        seed.Sessions.AddRange(own, new Session(scope, _actor, SessionClient.Pda, Start, RefreshSecret.Generate().Digest),
            new Session(scope, Guid.NewGuid(), SessionClient.Web, Start, RefreshSecret.Generate().Digest));
        await seed.SaveChangesAsync();
        return (new TenantContext(scope, _actor, own.Id), credential.Version);
    }

    private async Task AssertUnchangedAsync(string version, int auditCount = 0)
    {
        await using var verify = Open();
        var credential = await verify.PasswordCredentials.AsNoTracking().SingleAsync();
        Assert.Equal(version, credential.Version);
        Assert.Equal(CredentialVerification.LegacyVerifiedRestricted, credential.Verify(Initial));
        Assert.Single(credential.Recent);
        Assert.Equal(auditCount, await verify.Audit.CountAsync());
        Assert.All(await verify.Sessions.AsNoTracking().ToListAsync(), item => Assert.Null(item.RevokedAt));
    }

    private PlatformDbContext Open(Guid? tenant = null, IInterceptor? interceptor = null)
    {
        MySqlTestConnection.ValidateTarget(_connection);
        var options = new DbContextOptionsBuilder<PlatformDbContext>()
            .UseMySQL(_connection, mysql => mysql.MigrationsHistoryTable("__WesPlatformMigrations"));
        if (interceptor is not null) options.AddInterceptors(interceptor);
        return new PlatformDbContext(options.Options, tenant ?? _tenant);
    }

    private static ChangePasswordHandler Handler(PlatformDbContext database, DateTimeOffset? now = null) =>
        new(database, new FixedClock(now ?? Start.AddSeconds(1)), NullLogger<ChangePasswordHandler>.Instance);

    /// <summary>Removes only generated tenant fixtures, including exact test-only audit deletion and their password state.</summary>
    public async Task DisposeAsync()
    {
        if (!_migrated) return;
        foreach (var tenant in new[] { _tenant, _otherTenant })
        {
            await using var database = Open(tenant);
            await database.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM platform_audit WHERE TenantId = {tenant.ToString("D")}");
            database.Sessions.RemoveRange(await database.Sessions.ToListAsync());
            database.PasswordCredentials.RemoveRange(await database.PasswordCredentials.ToListAsync());
            await database.SaveChangesAsync();
        }
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class SequenceClock(DateTimeOffset first, DateTimeOffset second) : TimeProvider
    {
        private int _reads;
        public override DateTimeOffset GetUtcNow() => Interlocked.Increment(ref _reads) == 1 ? first : second;
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
        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
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

    private sealed class DuplicateAudit(Guid id) : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            var audit = eventData.Context!.ChangeTracker.Entries<PlatformAudit>().First(item => item.State == EntityState.Added);
            audit.Property(item => item.Id).CurrentValue = id;
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
            throw new InvalidOperationException("The test expected a cancelled owner save.");
        }
    }

    private sealed class CapturingLogger : ILogger<ChangePasswordHandler>
    {
        internal List<string> Messages { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Messages.Add(formatter(state, exception));
    }
}
