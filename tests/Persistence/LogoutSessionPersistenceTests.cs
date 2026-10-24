using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Wes.Platform.Domain;
using Wes.Platform.Features.Identity;
using Wes.Platform.Features.Tenancy;
using Wes.Platform.Infrastructure.Persistence;
using Xunit;

namespace Wes.Persistence.Tests;

/// <summary>Actual owner transaction, audit and refresh races; fixture cleanup is constrained to generated tenants.</summary>
[Collection("PlatformPersistence")]
[Trait("Provider", "MySQL")]
public sealed class LogoutSessionPersistenceTests : IAsyncLifetime
{
    private static readonly DateTimeOffset Start = new(2026, 10, 24, 4, 0, 0, TimeSpan.Zero);
    private readonly Guid _tenant = Guid.NewGuid();
    private readonly Guid _otherTenant = Guid.NewGuid();
    private readonly Guid _actor = Guid.NewGuid();
    private string _connection = "";
    private bool _migrated;

    /// <summary>Uses only the approved isolated target and existing owner migration history.</summary>
    public async Task InitializeAsync()
    {
        _connection = MySqlTestConnection.LoadAuthorizedTestConnection();
        await using var database = Open();
        await database.Database.MigrateAsync();
        _migrated = true;
    }

    /// <summary>Only the trusted current family is revoked and exactly one audit records its committed version.</summary>
    [Fact]
    [Trait("CaseId", "IDENTITY-LOGOUT-SCOPE-001")]
    public async Task CurrentFamilyLogoutIsAuditedAndIdempotent_WithoutRevokingOtherFamilies()
    {
        var (context, secret) = await SeedAsync();
        Guid otherId;
        string before;
        await using (var seed = Open())
        {
            before = (await seed.Sessions.AsNoTracking().SingleAsync()).Version;
            var other = new Session(_tenant, _actor, SessionClient.Web, Start, RefreshSecret.Generate().Digest);
            otherId = other.Id;
            seed.Sessions.Add(other);
            await seed.SaveChangesAsync();
        }
        var correlation = Guid.NewGuid();
        await using (var write = Open())
        {
            Assert.Equal(LogoutDecision.Completed, await Handler(write).HandleAsync(context, correlation));
            Assert.Empty(write.ChangeTracker.Entries());
            Assert.Equal(LogoutDecision.Completed, await Handler(write).HandleAsync(context, Guid.NewGuid()));
        }
        await using var verify = Open();
        var own = await verify.Sessions.AsNoTracking().SingleAsync(item => item.Id == context.SessionId);
        Assert.Equal(SessionState.Revoked, own.StateAt(Start.AddSeconds(1)));
        Assert.Equal(SessionState.Active, (await verify.Sessions.AsNoTracking().SingleAsync(item => item.Id == otherId)).StateAt(Start));
        var audit = await verify.Audit.AsNoTracking().SingleAsync();
        Assert.Equal(PlatformAuditAction.SessionRevoked, audit.Action);
        Assert.Equal(_actor, audit.ActorId);
        Assert.Equal(context.SessionId, audit.TargetId);
        Assert.Equal(correlation, audit.CorrelationId);
        Assert.Equal(before, audit.BeforeVersion);
        Assert.Equal(own.Version, audit.AfterVersion);
        Assert.Equal(Start.AddSeconds(1), audit.OccurredAt);
        var refresh = await new RefreshSessionHandler(verify, new FixedClock(Start.AddSeconds(2)),
            NullLogger<RefreshSessionHandler>.Instance).HandleAsync(context.SessionId, secret.Value);
        Assert.Equal(RefreshDecision.Inactive, refresh.Decision);
        Assert.Null(refresh.Successor);
    }

    /// <summary>Unknown, wrong-user and other-tenant claims cannot revoke a found session or create an audit.</summary>
    [Fact]
    [Trait("CaseId", "IDENTITY-LOGOUT-SCOPE-001")]
    public async Task UnknownAndNonOwnedFamiliesRejectWithoutSideEffects()
    {
        var (context, _) = await SeedAsync();
        await using var own = Open();
        Assert.Equal(LogoutDecision.Rejected,
            await Handler(own).HandleAsync(context with { ActorId = Guid.NewGuid() }, Guid.NewGuid()));
        Assert.Equal(LogoutDecision.Rejected,
            await Handler(own).HandleAsync(context with { SessionId = Guid.NewGuid() }, Guid.NewGuid()));
        await using var other = Open(_otherTenant);
        Assert.Equal(LogoutDecision.Rejected,
            await Handler(other).HandleAsync(context with { TenantId = _otherTenant }, Guid.NewGuid()));
        Assert.Empty(await own.Audit.ToListAsync());
        Assert.Empty(await other.Audit.ToListAsync());
        Assert.Null((await own.Sessions.SingleAsync()).RevokedAt);
    }

    /// <summary>Lost response retries and truly overlapping logouts cannot append a second version-linked audit.</summary>
    [Fact]
    [Trait("CaseId", "IDENTITY-LOGOUT-AUDIT-001")]
    public async Task ConcurrentLogoutsCommitOneAudit()
    {
        var (context, _) = await SeedAsync();
        var barrier = new SaveBarrier();
        await using var first = Open(interceptor: barrier);
        await using var second = Open(interceptor: barrier);
        var outcomes = await Task.WhenAll(Handler(first).HandleAsync(context, Guid.NewGuid()),
            Handler(second).HandleAsync(context, Guid.NewGuid()));
        Assert.All(outcomes, item => Assert.Equal(LogoutDecision.Completed, item));
        await using var verify = Open();
        Assert.Single(await verify.Audit.ToListAsync());
        Assert.NotNull((await verify.Sessions.SingleAsync()).RevokedAt);
        Assert.Empty(first.ChangeTracker.Entries());
        Assert.Empty(second.ChangeTracker.Entries());
    }

    /// <summary>Both ordered races converge to revoked; a refresh winning first does not survive the later logout.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("CaseId", "IDENTITY-LOGOUT-CONCURRENT-001")]
    public async Task LogoutAndRefreshRace_ConvergesToRevoked(bool logoutFirst)
    {
        var (context, secret) = await SeedAsync();
        var bothStaged = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstCommitted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var logout = Open(interceptor: new OrderedSave(logoutFirst, bothStaged, firstCommitted));
        await using var refresh = Open(interceptor: new OrderedSave(!logoutFirst, bothStaged, firstCommitted));
        var logoutTask = Handler(logout).HandleAsync(context, Guid.NewGuid());
        var refreshTask = new RefreshSessionHandler(refresh, new FixedClock(Start.AddSeconds(1)),
            NullLogger<RefreshSessionHandler>.Instance).HandleAsync(context.SessionId, secret.Value);
        await Task.WhenAll(logoutTask, refreshTask);
        Assert.Equal(LogoutDecision.Completed, await logoutTask);
        var outcome = await refreshTask;
        Assert.Equal(logoutFirst ? RefreshDecision.Inactive : RefreshDecision.Rotated, outcome.Decision);
        Assert.Equal(!logoutFirst, outcome.Successor is not null);
        await using var verify = Open();
        var family = await verify.Sessions.Include(item => item.Credentials).AsNoTracking().SingleAsync();
        Assert.Equal(SessionState.Revoked, family.StateAt(Start.AddSeconds(2)));
        Assert.False(family.AcceptsAccess(Start, Start.AddSeconds(2)));
        var audit = await verify.Audit.AsNoTracking().SingleAsync();
        Assert.Equal(family.Version, audit.AfterVersion);
        await using var repeated = Open();
        var rejected = await new RefreshSessionHandler(repeated, new FixedClock(Start.AddSeconds(2)),
            NullLogger<RefreshSessionHandler>.Instance).HandleAsync(context.SessionId, outcome.Successor?.Value ?? secret.Value);
        Assert.Null(rejected.Successor);
        Assert.Contains(rejected.Decision, new[] { RefreshDecision.Inactive, RefreshDecision.Replayed });
    }

    /// <summary>A real duplicate audit key must roll back the session mutation, not merely fail before reaching SQL.</summary>
    [Fact]
    [Trait("CaseId", "IDENTITY-LOGOUT-AUDIT-001")]
    public async Task AuditDatabaseFailureRollsBackRevocationAndVersion()
    {
        var (context, _) = await SeedAsync();
        string before;
        var existing = new PlatformAudit(_tenant, _actor, context.SessionId, Guid.NewGuid(),
            PlatformAuditAction.SessionRevoked, null, Guid.NewGuid().ToString("D"), Start);
        await using (var seed = Open())
        {
            before = (await seed.Sessions.SingleAsync()).Version;
            seed.Audit.Add(existing);
            await seed.SaveChangesAsync();
        }
        await using var database = Open(interceptor: new DuplicateAudit(existing.Id));
        var error = await Assert.ThrowsAsync<DbUpdateException>(() => Handler(database).HandleAsync(context, Guid.NewGuid()));
        Assert.Equal(1062, Assert.IsType<MySql.Data.MySqlClient.MySqlException>(error.InnerException).Number);
        Assert.Empty(database.ChangeTracker.Entries());
        await using var verify = Open();
        var session = await verify.Sessions.SingleAsync();
        Assert.Null(session.RevokedAt);
        Assert.Equal(before, session.Version);
        Assert.Equal(existing.Id, (await verify.Audit.SingleAsync()).Id);
    }

    /// <summary>Only caller-supplied verified context matching the database is allowed, before any query or mutation.</summary>
    [Fact]
    [Trait("CaseId", "IDENTITY-LOGOUT-FAULT-001")]
    public async Task InvalidTrustedContextIsRejectedExplicitly()
    {
        var (context, _) = await SeedAsync();
        await using var database = Open();
        var handler = Handler(database);
        await Assert.ThrowsAsync<ArgumentNullException>(() => handler.HandleAsync(null!, Guid.NewGuid()));
        await Assert.ThrowsAsync<ArgumentException>(() => handler.HandleAsync(context, Guid.Empty));
        foreach (var invalid in new[]
        {
            context with { TenantId = _otherTenant }, context with { ActorId = Guid.Empty },
            context with { SessionId = Guid.Empty }, context with { TenantId = Guid.Empty }
        })
            await Assert.ThrowsAsync<InvalidOperationException>(() => handler.HandleAsync(invalid, Guid.NewGuid()));
        Assert.Empty(database.ChangeTracker.Entries());
        Assert.Empty(await database.Audit.ToListAsync());
    }

    /// <summary>An independent commit cannot be deferred by outer transactions or include a caller-owned pending graph.</summary>
    [Fact]
    [Trait("CaseId", "IDENTITY-LOGOUT-FAULT-001")]
    public async Task DirtyAndExternalTransactionsAreRejected()
    {
        var (context, _) = await SeedAsync();
        await using var database = Open();
        database.LoginAccounts.Add(new LoginAccountBudget(_tenant, LoginAccountKey.FromNormalizedAccount("PENDING")));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Handler(database).HandleAsync(context, Guid.NewGuid()));
        Assert.Single(database.ChangeTracker.Entries());
        database.ChangeTracker.Clear();
        await using (var transaction = await database.Database.BeginTransactionAsync())
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => Handler(database).HandleAsync(context, Guid.NewGuid()));
            await transaction.RollbackAsync();
        }
        using var scope = new System.Transactions.TransactionScope(System.Transactions.TransactionScopeAsyncFlowOption.Enabled);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Handler(database).HandleAsync(context, Guid.NewGuid()));
    }

    /// <summary>Disabling EF automatic transactions must not silently split session revocation from its mandatory audit.</summary>
    [Fact]
    [Trait("CaseId", "IDENTITY-LOGOUT-AUDIT-001")]
    public async Task DisabledAutomaticTransactionsAreRejectedBeforeWriting()
    {
        var (context, _) = await SeedAsync();
        await using var database = Open();
        database.Database.AutoTransactionBehavior = AutoTransactionBehavior.Never;
        await Assert.ThrowsAsync<InvalidOperationException>(() => Handler(database).HandleAsync(context, Guid.NewGuid()));
        Assert.Empty(database.ChangeTracker.Entries());
        Assert.Null((await database.Sessions.AsNoTracking().SingleAsync()).RevokedAt);
        Assert.Empty(await database.Audit.ToListAsync());
    }

    /// <summary>Both pre-cancel and cancellation during the atomic write remain errors, with no pending revocation or audit.</summary>
    [Fact]
    [Trait("CaseId", "IDENTITY-LOGOUT-FAULT-001")]
    public async Task CancellationDoesNotReturnCompletedOrRetainOwnedGraph()
    {
        var (context, _) = await SeedAsync();
        using var cancellation = new CancellationTokenSource();
        await using var database = Open(interceptor: new CancelOnSave(cancellation));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Handler(database).HandleAsync(context, Guid.NewGuid(), new CancellationToken(true)));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Handler(database).HandleAsync(context, Guid.NewGuid(), cancellation.Token));
        Assert.Empty(database.ChangeTracker.Entries());
        await using var verify = Open();
        Assert.Null((await verify.Sessions.SingleAsync()).RevokedAt);
        Assert.Empty(await verify.Audit.ToListAsync());
    }

    /// <summary>Unknown driver failures propagate intact; no expected conflict or completed result masks an outage.</summary>
    [Fact]
    [Trait("CaseId", "IDENTITY-LOGOUT-FAULT-001")]
    public async Task UnknownPersistenceErrorPropagatesWithoutRevocation()
    {
        var (context, _) = await SeedAsync();
        var error = new DbUpdateException("Injected unknown database error.");
        await using var database = Open(interceptor: new FailSave(error));
        Assert.Same(error, await Assert.ThrowsAsync<DbUpdateException>(() => Handler(database).HandleAsync(context, Guid.NewGuid())));
        Assert.Empty(database.ChangeTracker.Entries());
        await using var verify = Open();
        Assert.Null((await verify.Sessions.SingleAsync()).RevokedAt);
        Assert.Empty(await verify.Audit.ToListAsync());
    }

    /// <summary>Clock reversal cannot silently claim a completed logout or invent an audit instant.</summary>
    [Fact]
    [Trait("CaseId", "IDENTITY-LOGOUT-FAULT-001")]
    public async Task ClockReversalFailsWithoutMutation()
    {
        var (context, _) = await SeedAsync();
        await using var database = Open();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new LogoutSessionHandler(database, new FixedClock(Start.AddTicks(-10)), NullLogger<LogoutSessionHandler>.Instance)
                .HandleAsync(context, Guid.NewGuid()));
        Assert.Empty(database.ChangeTracker.Entries());
        Assert.Null((await database.Sessions.AsNoTracking().SingleAsync()).RevokedAt);
        Assert.Empty(await database.Audit.ToListAsync());
    }

    /// <summary>Idle/expired families may still be explicitly terminated without granting them renewed access.</summary>
    [Theory]
    [InlineData(901)]
    [InlineData(28800)]
    [Trait("CaseId", "IDENTITY-LOGOUT-SCOPE-001")]
    public async Task InactiveOwnedFamilyCanBeTerminated(int seconds)
    {
        var (context, _) = await SeedAsync();
        await using var database = Open();
        var handler = new LogoutSessionHandler(database, new FixedClock(Start.AddSeconds(seconds)),
            NullLogger<LogoutSessionHandler>.Instance);
        Assert.Equal(LogoutDecision.Completed, await handler.HandleAsync(context, Guid.NewGuid()));
        var row = await database.Sessions.AsNoTracking().SingleAsync();
        Assert.Equal(SessionState.Revoked, row.StateAt(Start.AddSeconds(seconds)));
        Assert.Single(await database.Audit.ToListAsync());
    }

    /// <summary>Eight real competing activity commits cannot produce a false completion or orphaned logout audit.</summary>
    [Fact]
    [Trait("CaseId", "IDENTITY-LOGOUT-CONCURRENT-001")]
    public async Task RepeatedRealCasConflictsFailExplicitly_WithNoLogoutAudit()
    {
        var (context, secret) = await SeedAsync();
        var commits = 0;
        var competing = new CompetingWriter(async () =>
        {
            await using var writer = Open();
            var session = await writer.Sessions.SingleAsync();
            Assert.True(session.RecordActivity(Start.AddSeconds(++commits)));
            await writer.SaveChangesAsync();
        });
        var logger = new CapturingLogger();
        await using var database = Open(interceptor: competing);
        var handler = new LogoutSessionHandler(database, new FixedClock(Start.AddSeconds(60)), logger);
        await Assert.ThrowsAsync<InvalidOperationException>(() => handler.HandleAsync(context, Guid.NewGuid()));
        Assert.Equal(8, commits);
        Assert.Empty(database.ChangeTracker.Entries());
        await using var verify = Open();
        var stored = await verify.Sessions.AsNoTracking().SingleAsync();
        Assert.Null(stored.RevokedAt);
        Assert.Equal(Start.AddSeconds(8), stored.LastActivityAt);
        Assert.Empty(await verify.Audit.ToListAsync());
        Assert.Equal(8, logger.Messages.Count(message => message.Contains("LogoutSessionVersionConflict", StringComparison.Ordinal)));
        Assert.Contains(logger.Messages, message => message.Contains("LogoutSessionContentionExhausted", StringComparison.Ordinal));
        Assert.All(logger.Messages, message =>
        {
            Assert.DoesNotContain(secret.Value, message);
            Assert.DoesNotContain(secret.Digest, message);
            Assert.DoesNotContain(context.SessionId.ToString("D"), message);
            Assert.DoesNotContain(_actor.ToString("D"), message);
        });
    }

    private async Task<(TenantContext Context, RefreshSecret Secret)> SeedAsync()
    {
        var secret = RefreshSecret.Generate();
        await using var database = Open();
        var session = new Session(_tenant, _actor, SessionClient.Web, Start, secret.Digest);
        database.Sessions.Add(session);
        await database.SaveChangesAsync();
        return (new TenantContext(_tenant, _actor, session.Id), secret);
    }

    private PlatformDbContext Open(Guid? tenant = null, IInterceptor? interceptor = null)
    {
        MySqlTestConnection.ValidateTarget(_connection);
        var options = new DbContextOptionsBuilder<PlatformDbContext>()
            .UseMySQL(_connection, mysql => mysql.MigrationsHistoryTable("__WesPlatformMigrations"));
        if (interceptor is not null) options.AddInterceptors(interceptor);
        return new PlatformDbContext(options.Options, tenant ?? _tenant);
    }

    private static LogoutSessionHandler Handler(PlatformDbContext database) =>
        new(database, new FixedClock(Start.AddSeconds(1)), NullLogger<LogoutSessionHandler>.Instance);

    /// <summary>Deletes only this fixture's exact generated tenants, using the existing test-only audit cleanup allowance.</summary>
    public async Task DisposeAsync()
    {
        if (!_migrated) return;
        foreach (var tenant in new[] { _tenant, _otherTenant })
        {
            await using var database = Open(tenant);
            await database.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM platform_audit WHERE TenantId = {tenant.ToString("D")}");
            database.Sessions.RemoveRange(await database.Sessions.ToListAsync());
            await database.SaveChangesAsync();
        }
    }

    private sealed class FixedClock(DateTimeOffset instant) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => instant;
    }

    private sealed class SaveBarrier : SaveChangesInterceptor
    {
        private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _arrivals;
        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _arrivals) >= 2) _ready.TrySetResult();
            await _ready.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
            return result;
        }
    }

    private sealed class OrderedSave(bool first, TaskCompletionSource bothStaged, TaskCompletionSource firstCommitted)
        : SaveChangesInterceptor
    {
        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (first) await bothStaged.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
            else
            {
                bothStaged.TrySetResult();
                await firstCommitted.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
            }
            return result;
        }
        public override ValueTask<int> SavedChangesAsync(
            SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
        {
            if (first) firstCommitted.TrySetResult();
            return ValueTask.FromResult(result);
        }
    }

    private sealed class DuplicateAudit(Guid existingId) : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            var audit = Assert.Single(eventData.Context!.ChangeTracker.Entries<PlatformAudit>(), item => item.State == EntityState.Added);
            audit.Property(item => item.Id).CurrentValue = existingId;
            return ValueTask.FromResult(result);
        }
    }

    private sealed class CancelOnSave(CancellationTokenSource cancellation) : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            cancellation.Cancel();
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(result);
        }
    }

    private sealed class FailSave(DbUpdateException error) : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default) => throw error;
    }

    private sealed class CompetingWriter(Func<Task> commit) : SaveChangesInterceptor
    {
        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            await commit();
            return result;
        }
    }

    private sealed class CapturingLogger : ILogger<LogoutSessionHandler>
    {
        internal List<string> Messages { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Messages.Add(formatter(state, exception));
    }
}
