using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Wes.Platform.Domain;
using Wes.Platform.Features.Identity;
using Wes.Platform.Infrastructure.Persistence;
using Xunit;

namespace Wes.Persistence.Tests;

/// <summary>Real tenant-account risk tests with exact generated-tenant cleanup, not account directory fixtures.</summary>
[Collection("PlatformPersistence")]
[Trait("Provider", "MySQL")]
public sealed class LoginAccountPersistenceTests : IAsyncLifetime
{
    private static readonly DateTimeOffset Start = new(2026, 10, 24, 2, 0, 0, TimeSpan.Zero);
    private readonly Guid _tenant = Guid.NewGuid();
    private readonly Guid _otherTenant = Guid.NewGuid();
    private readonly LoginAccountKey _key = LoginAccountKey.FromNormalizedAccount("UNKNOWN-ACCOUNT");
    private string _connection = "";
    private bool _migrated;

    /// <summary>Only the separately reviewed Platform migration is applied to the authorized wes_test.</summary>
    public async Task InitializeAsync()
    {
        _connection = MySqlTestConnection.LoadAuthorizedTestConnection();
        await using var database = Open();
        await database.Database.MigrateAsync();
        _migrated = true;
    }

    /// <summary>Read-only unknown accounts do not cause unbounded empty-row creation or expose account existence.</summary>
    [Fact]
    [Trait("CaseId", "IDENTITY-ACCOUNT-DB-001")]
    public async Task UnknownAccountReadDoesNotCreateRow()
    {
        await using var database = Open();
        var snapshot = await Handler(database).ReadAsync(_key);
        Assert.Null(snapshot.Version);
        Assert.Equal(LoginGate.VerifyPassword, snapshot.Gate);
        Assert.False(await database.LoginAccounts.AnyAsync());
        Assert.Empty(database.ChangeTracker.Entries());
    }

    /// <summary>Restarts preserve approved gates; a challenge rejection cannot count as another password failure.</summary>
    [Fact]
    [Trait("CaseId", "IDENTITY-ACCOUNT-WINDOW-001")]
    public async Task FailuresPersist_ChallengeAndLockRejectWithoutChangingVersion()
    {
        await SeedAsync(3);
        await using var database = Open();
        var handler = Handler(database);
        var third = await handler.ReadAsync(_key);
        Assert.Equal(LoginGate.ChallengeRequired, third.Gate);
        Assert.Equal(AccountFailureDecision.ChallengeRequired,
            await handler.RecordFailureAsync(_key, third.Version, false));
        Assert.Equal(third.Version, (await handler.ReadAsync(_key)).Version);
        Assert.Equal(AccountFailureDecision.Recorded, await handler.RecordFailureAsync(_key, third.Version, true));
        var fourth = await handler.ReadAsync(_key);
        Assert.Equal(AccountFailureDecision.Recorded, await handler.RecordFailureAsync(_key, fourth.Version, true));
        var fifth = await handler.ReadAsync(_key);
        Assert.Equal(LoginGate.Locked, fifth.Gate);
        Assert.Equal(AccountFailureDecision.Locked, await handler.RecordFailureAsync(_key, fifth.Version, true));
        Assert.Equal(fifth.Version, (await handler.ReadAsync(_key)).Version);
        var row = await database.LoginAccounts.AsNoTracking().SingleAsync();
        Assert.Equal(5, row.Failures.Count);
        Assert.Equal(Start.AddSeconds(900), row.LockedUntil);
        Assert.Empty(database.ChangeTracker.Entries());
    }

    /// <summary>The same digest is isolated by trusted tenant, and tracked cross-tenant writes remain forbidden.</summary>
    [Fact]
    [Trait("CaseId", "IDENTITY-ACCOUNT-DB-001")]
    public async Task TenantFilterAndWriteGuardApplyToAccountRisks()
    {
        await SeedAsync(3);
        await using var other = Open(_otherTenant);
        Assert.Equal(LoginGate.VerifyPassword, (await Handler(other).ReadAsync(_key)).Gate);
        Assert.Empty(await other.LoginAccounts.ToListAsync());
        other.LoginAccounts.Add(new LoginAccountBudget(_tenant, _key));
        await Assert.ThrowsAsync<InvalidOperationException>(() => other.SaveChangesAsync());
        other.ChangeTracker.Clear();
        Assert.Equal(AccountFailureDecision.Recorded, await Handler(other).RecordFailureAsync(_key, null, false));
        await using var own = Open();
        Assert.Equal(3, (await own.LoginAccounts.SingleAsync()).Failures.Count);
        Assert.Single((await other.LoginAccounts.SingleAsync()).Failures);
        Assert.Empty(own.Model.FindEntityType(typeof(LoginAccountBudget))!.GetForeignKeys());
    }

    /// <summary>First insert and fifth-failure CAS both reject stale decisions rather than replaying verification.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    [Trait("CaseId", "IDENTITY-ACCOUNT-CONCURRENT-001")]
    public async Task ConcurrentFailureCommitsOnce_AndLoserMustRestart(int count)
    {
        if (count > 0) await SeedAsync(count);
        await using var read = Open();
        var version = (await Handler(read).ReadAsync(_key)).Version;
        var barrier = new SaveBarrier();
        await using var first = Open(interceptor: barrier);
        await using var second = Open(interceptor: barrier);
        var outcomes = await Task.WhenAll(
            Handler(first).RecordFailureAsync(_key, version, true),
            Handler(second).RecordFailureAsync(_key, version, true));
        Assert.Equal(1, outcomes.Count(item => item == AccountFailureDecision.Recorded));
        Assert.Equal(1, outcomes.Count(item => item == AccountFailureDecision.VersionConflict));
        Assert.Empty(first.ChangeTracker.Entries());
        Assert.Empty(second.ChangeTracker.Entries());
        await using var verify = Open();
        Assert.Equal(count + 1, (await verify.LoginAccounts.AsNoTracking().SingleAsync()).Failures.Count);
        if (count == 4) Assert.Equal(LoginGate.Locked, (await Handler(verify).ReadAsync(_key)).Gate);
    }

    /// <summary>Null and reused versions cannot append failures after the observed state changes.</summary>
    [Fact]
    [Trait("CaseId", "IDENTITY-ACCOUNT-CONCURRENT-001")]
    public async Task StaleVersionCannotReplayCredentialResult()
    {
        await using var database = Open();
        var handler = Handler(database);
        Assert.Equal(AccountFailureDecision.Recorded, await handler.RecordFailureAsync(_key, null, false));
        var first = await handler.ReadAsync(_key);
        Assert.Equal(AccountFailureDecision.VersionConflict, await handler.RecordFailureAsync(_key, null, false));
        Assert.Equal(AccountFailureDecision.Recorded, await handler.RecordFailureAsync(_key, first.Version, false));
        Assert.Equal(AccountFailureDecision.VersionConflict, await handler.RecordFailureAsync(_key, first.Version, false));
        Assert.Equal(2, (await database.LoginAccounts.AsNoTracking().SingleAsync()).Failures.Count);
    }

    /// <summary>Bad internal versions and keys are explicit argument faults, never an invented conflict or empty-account read.</summary>
    [Fact]
    [Trait("CaseId", "IDENTITY-ACCOUNT-FAULT-001")]
    public async Task InvalidInternalArgumentsFailBeforeCreatingState()
    {
        await using var database = Open();
        var handler = Handler(database);
        await Assert.ThrowsAsync<ArgumentNullException>(() => handler.ReadAsync(null!));
        await Assert.ThrowsAsync<ArgumentNullException>(() => handler.RecordFailureAsync(null!, null, false));
        await Assert.ThrowsAsync<ArgumentException>(() => handler.RecordFailureAsync(_key, "secret-marker", false));
        await Assert.ThrowsAsync<ArgumentException>(() => handler.RecordFailureAsync(_key, Guid.Empty.ToString("D"), false));
        Assert.Empty(database.ChangeTracker.Entries());
        Assert.False(await database.LoginAccounts.AnyAsync());
    }

    /// <summary>Round-trip datetime(6) retains both the last microsecond of lock and the exact unlock boundary.</summary>
    [Fact]
    [Trait("CaseId", "IDENTITY-ACCOUNT-LOCK-001")]
    public async Task LockMicrosecondBoundarySurvivesRestart()
    {
        await SeedAsync(5, Start.AddTicks(19));
        await using var database = Open();
        Assert.Equal(LoginGate.Locked,
            (await Handler(database, Start.AddSeconds(900)).ReadAsync(_key)).Gate);
        var expired = await Handler(database, Start.AddSeconds(900).AddTicks(10)).ReadAsync(_key);
        Assert.Equal(LoginGate.VerifyPassword, expired.Gate);
        Assert.Equal(AccountFailureDecision.Recorded,
            await Handler(database, Start.AddSeconds(900).AddTicks(10)).RecordFailureAsync(_key, expired.Version, false));
        var row = await database.LoginAccounts.AsNoTracking().SingleAsync();
        Assert.Single(row.Failures);
        Assert.Null(row.LockedUntil);
    }

    /// <summary>Invalid persisted payload cannot be interpreted as no failures or echoed through an exception.</summary>
    [Theory]
    [InlineData("null")]
    [InlineData("secret-marker")]
    [InlineData("[]")]
    [InlineData("[\"2099-01-01T00:00:00Z\"]")]
    [Trait("CaseId", "IDENTITY-ACCOUNT-STATE-001")]
    public async Task CorruptSchemaFailsClosed_WithoutLeakingPayload(string payload)
    {
        await SeedAsync(1);
        await using (var write = Open())
        {
            var row = await write.LoginAccounts.SingleAsync();
            write.Entry(row).Property("FailuresData").CurrentValue = payload;
            await write.SaveChangesAsync();
        }
        await using var read = Open();
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Handler(read).ReadAsync(_key));
        Assert.DoesNotContain("secret-marker", error.ToString());
        Assert.Empty(read.ChangeTracker.Entries());
    }

    /// <summary>Caller graphs and outside transactions cannot be committed or cleared by the risk use case.</summary>
    [Fact]
    [Trait("CaseId", "IDENTITY-ACCOUNT-FAULT-001")]
    public async Task DirtyAndExternalTransactionsAreRejected()
    {
        await using var database = Open();
        database.LoginAccounts.Add(new LoginAccountBudget(_tenant, _key));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Handler(database).ReadAsync(_key));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Handler(database).RecordFailureAsync(_key, null, false));
        Assert.Single(database.ChangeTracker.Entries());
        database.ChangeTracker.Clear();
        await using (var transaction = await database.Database.BeginTransactionAsync())
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => Handler(database).ReadAsync(_key));
            await Assert.ThrowsAsync<InvalidOperationException>(() => Handler(database).RecordFailureAsync(_key, null, false));
            await transaction.RollbackAsync();
        }
        using var scope = new System.Transactions.TransactionScope(System.Transactions.TransactionScopeAsyncFlowOption.Enabled);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Handler(database).ReadAsync(_key));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Handler(database).RecordFailureAsync(_key, null, false));
    }

    /// <summary>Unknown database errors and cancellation are not disguised as an expected conflict or committed failure.</summary>
    [Fact]
    [Trait("CaseId", "IDENTITY-ACCOUNT-FAULT-001")]
    public async Task PersistenceFailureAndCancellationPropagateWithoutStagedWrites()
    {
        var failure = new DbUpdateException("Injected database failure.");
        var injection = new FailSave(failure);
        await using var database = Open(interceptor: injection);
        var observed = await Assert.ThrowsAsync<DbUpdateException>(() =>
            Handler(database).RecordFailureAsync(_key, null, false));
        Assert.Same(failure, observed);
        Assert.Equal(1, injection.Writes);
        Assert.Empty(database.ChangeTracker.Entries());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Handler(database).ReadAsync(_key, new CancellationToken(true)));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Handler(database).RecordFailureAsync(_key, null, false, new CancellationToken(true)));
        await using var verify = Open();
        Assert.False(await verify.LoginAccounts.AnyAsync());
    }

    /// <summary>Clock reversal fails both read and mutation without clearing the durable account budget.</summary>
    [Fact]
    [Trait("CaseId", "IDENTITY-ACCOUNT-STATE-001")]
    public async Task FutureFailureRejectsReadAndMutation()
    {
        await SeedAsync(1, Start.AddSeconds(1));
        await using var database = Open();
        var version = (await database.LoginAccounts.AsNoTracking().SingleAsync()).Version;
        await Assert.ThrowsAsync<InvalidOperationException>(() => Handler(database).ReadAsync(_key));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Handler(database).RecordFailureAsync(_key, version, true));
        Assert.Empty(database.ChangeTracker.Entries());
        Assert.Single((await database.LoginAccounts.AsNoTracking().SingleAsync()).Failures);
    }

    /// <summary>A failed update cannot persist a staged second observation through later reuse of the context.</summary>
    [Fact]
    [Trait("CaseId", "IDENTITY-ACCOUNT-FAULT-001")]
    public async Task FailedExistingUpdateLeavesVersionAndWindowUnchanged()
    {
        await SeedAsync(1);
        await using var database = Open(interceptor: new FailSave(new DbUpdateException("Injected update failure.")));
        var snapshot = await Handler(database).ReadAsync(_key);
        await Assert.ThrowsAsync<DbUpdateException>(() => Handler(database).RecordFailureAsync(_key, snapshot.Version, false));
        Assert.Empty(database.ChangeTracker.Entries());
        await database.SaveChangesAsync();
        await using var verify = Open();
        var row = await verify.LoginAccounts.SingleAsync();
        Assert.Single(row.Failures);
        Assert.Equal(snapshot.Version, row.Version);
    }

    /// <summary>Cancellation just before SQL cannot leave an added observation for a later accidental save.</summary>
    [Fact]
    [Trait("CaseId", "IDENTITY-ACCOUNT-FAULT-001")]
    public async Task CancellationDuringSaveDiscardsOwnedGraph()
    {
        using var cancellation = new CancellationTokenSource();
        await using var database = Open(interceptor: new CancelOnSave(cancellation));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Handler(database).RecordFailureAsync(_key, null, false, cancellation.Token));
        Assert.Empty(database.ChangeTracker.Entries());
        await using var verify = Open();
        Assert.False(await verify.LoginAccounts.AnyAsync());
    }

    /// <summary>An invented lock expiry inconsistent with the failure history cannot silently lock or unlock an account.</summary>
    [Fact]
    [Trait("CaseId", "IDENTITY-ACCOUNT-STATE-001")]
    public async Task CorruptLockStateIsRejected()
    {
        await SeedAsync(1);
        await using (var write = Open())
        {
            var row = await write.LoginAccounts.SingleAsync();
            write.Entry(row).Property(item => item.LockedUntil).CurrentValue = Start.AddMinutes(15);
            await write.SaveChangesAsync();
        }
        await using var read = Open();
        await Assert.ThrowsAsync<InvalidOperationException>(() => Handler(read).ReadAsync(_key));
        Assert.Empty(read.ChangeTracker.Entries());
    }

    /// <summary>Risk denial and conflict logs use fixed codes, not account material or serialized driver errors.</summary>
    [Fact]
    [Trait("CaseId", "IDENTITY-ACCOUNT-FAULT-001")]
    public async Task RiskLogsExcludeAccountMaterial()
    {
        await SeedAsync(3);
        await using var database = Open();
        var logger = new CapturingLogger();
        var handler = new AccountLoginRiskHandler(database, new FixedClock(Start), logger);
        var snapshot = await handler.ReadAsync(_key);
        await handler.RecordFailureAsync(_key, snapshot.Version, false);
        await handler.RecordFailureAsync(_key, null, false);
        Assert.Contains(logger.Messages, message => message.Contains("AccountChallengeRequired", StringComparison.Ordinal));
        Assert.Contains(logger.Messages, message => message.Contains("AccountRiskVersionConflict", StringComparison.Ordinal));
        Assert.All(logger.Messages, message =>
        {
            Assert.DoesNotContain("UNKNOWN-ACCOUNT", message);
            Assert.DoesNotContain(_key.Digest, message);
        });
    }

    private async Task SeedAsync(int count, DateTimeOffset? instant = null)
    {
        await using var database = Open();
        var budget = new LoginAccountBudget(_tenant, _key);
        for (var index = 0; index < count; index++) budget.RecordFailure(instant ?? Start, true);
        database.LoginAccounts.Add(budget);
        await database.SaveChangesAsync();
    }

    private PlatformDbContext Open(Guid? tenant = null, IInterceptor? interceptor = null)
    {
        MySqlTestConnection.ValidateTarget(_connection);
        var options = new DbContextOptionsBuilder<PlatformDbContext>()
            .UseMySQL(_connection, mysql => mysql.MigrationsHistoryTable("__WesPlatformMigrations"));
        if (interceptor is not null) options.AddInterceptors(interceptor);
        return new PlatformDbContext(options.Options, tenant ?? _tenant);
    }

    private static AccountLoginRiskHandler Handler(PlatformDbContext database, DateTimeOffset? instant = null) =>
        new(database, new FixedClock(instant ?? Start), NullLogger<AccountLoginRiskHandler>.Instance);

    /// <summary>Only this fixture's two random tenant scopes are removed from the account-risk table.</summary>
    public async Task DisposeAsync()
    {
        if (!_migrated) return;
        foreach (var tenant in new[] { _tenant, _otherTenant })
        {
            await using var database = Open(tenant);
            database.LoginAccounts.RemoveRange(await database.LoginAccounts.ToListAsync());
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

    private sealed class FailSave(DbUpdateException error) : SaveChangesInterceptor
    {
        internal int Writes { get; private set; }
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            Writes++;
            if (eventData.Context!.ChangeTracker.HasChanges()) throw error;
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

    private sealed class CapturingLogger : ILogger<AccountLoginRiskHandler>
    {
        internal List<string> Messages { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Messages.Add(formatter(state, exception));
    }
}
