using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Wes.Platform.Domain;
using Wes.Platform.Features.Identity;
using Wes.Platform.Infrastructure.Persistence;
using Xunit;

namespace Wes.Persistence.Tests;

/// <summary>Real MySQL one-shot challenge races and faults; no renderer or HTTP endpoint is faked as complete.</summary>
[Collection("PlatformPersistence")]
[Trait("Provider", "MySQL")]
public sealed class LoginChallengePersistenceTests : IAsyncLifetime
{
    private static readonly DateTimeOffset Start = new(2026, 10, 24, 3, 0, 0, TimeSpan.Zero);
    private readonly Guid _tenant = Guid.NewGuid();
    private readonly Guid _otherTenant = Guid.NewGuid();
    private readonly LoginAccountKey _account = LoginAccountKey.FromNormalizedAccount("UNKNOWN-ACCOUNT");
    private readonly LoginSourceKey _source = LoginSourceKey.FromTrustedPeer(IPAddress.Parse("192.0.2.1"));
    private readonly LoginChallengeProtection _protection = new(Enumerable.Range(0, 32).Select(item => (byte)item).ToArray());
    private string _connection = "";
    private bool _migrated;

    /// <summary>Applies only the reviewed Platform owner migration to the explicitly authorized isolated target.</summary>
    public async Task InitializeAsync()
    {
        _connection = MySqlTestConnection.LoadAuthorizedTestConnection();
        await using var database = Open();
        await database.Database.MigrateAsync();
        _migrated = true;
    }

    /// <summary>Successful issuance commits a keyed answer without persisting plaintext or keeping a tracked delivery graph.</summary>
    [Fact]
    [Trait("CaseId", "IDENTITY-CHALLENGE-CRYPTO-001")]
    public async Task IssuancePersistsOnlyMac_AndSurvivesRestart()
    {
        var delivery = await IssueAsync();
        Assert.NotEqual(Guid.Empty, delivery.Id);
        Assert.Matches("^[A-HJ-NP-Z2-9]{6}$", delivery.Answer);
        Assert.DoesNotContain(delivery.Answer, delivery.ToString());
        Assert.DoesNotContain(delivery.Answer, JsonSerializer.Serialize(delivery));
        Assert.Equal(Start.AddSeconds(120), delivery.ExpiresAt);
        await using var database = Open();
        var row = await database.LoginChallenges.AsNoTracking().SingleAsync();
        Assert.Equal(delivery.Id, row.Id);
        Assert.Equal(_account.Digest, row.AccountDigest);
        Assert.Equal(_source.Digest, row.SourceDigest);
        Assert.Equal(_protection.ComputeMac(_tenant, delivery.Id, _account, _source, delivery.Answer), row.AnswerMac);
        Assert.NotEqual(delivery.Answer, row.AnswerMac);
        Assert.Equal(ChallengeDecision.Accepted,
            await Handler(database).ConsumeAsync(delivery.Id, _account, _source, delivery.Answer));
        Assert.Empty(database.ChangeTracker.Entries());
        Assert.Equal(ChallengeDecision.Rejected,
            await Handler(database).ConsumeAsync(delivery.Id, _account, _source, delivery.Answer));
    }

    /// <summary>Malformed, empty and wrong answers consume the same one attempt without allowing corrected replay.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("WRONG!")]
    [InlineData("abc234")]
    [Trait("CaseId", "IDENTITY-CHALLENGE-ONCE-001")]
    public async Task WrongOrMissingAnswerBurnsChallenge(string? answer)
    {
        var delivery = await IssueAsync();
        await using var database = Open();
        Assert.Equal(ChallengeDecision.Rejected, await Handler(database).ConsumeAsync(delivery.Id, _account, _source, answer));
        var row = await database.LoginChallenges.AsNoTracking().SingleAsync();
        Assert.Equal(Start, row.ConsumedAt);
        Assert.Equal(ChallengeDecision.Rejected,
            await Handler(database).ConsumeAsync(delivery.Id, _account, _source, delivery.Answer));
    }

    /// <summary>The exact expiry remains exclusive after datetime(6) persistence and a fresh handler.</summary>
    [Theory]
    [InlineData(-10, ChallengeDecision.Accepted)]
    [InlineData(0, ChallengeDecision.Rejected)]
    [Trait("CaseId", "IDENTITY-CHALLENGE-LIFETIME-001")]
    public async Task LastMicrosecondAndExactExpirySurviveRestart(long deltaTicks, ChallengeDecision expected)
    {
        var delivery = await IssueAsync(Start.AddTicks(19));
        await using var database = Open();
        var decision = await Handler(database, Start.AddSeconds(120).AddTicks(10 + deltaTicks))
            .ConsumeAsync(delivery.Id, _account, _source, delivery.Answer);
        Assert.Equal(expected, decision);
        var row = await database.LoginChallenges.AsNoTracking().SingleAsync();
        Assert.Equal(Start.AddTicks(10), row.CreatedAt);
        Assert.Equal(expected == ChallengeDecision.Accepted, row.ConsumedAt.HasValue);
    }

    /// <summary>Neither another trusted tenant nor a substituted account/source can consume or burn the original.</summary>
    [Fact]
    [Trait("CaseId", "IDENTITY-CHALLENGE-BINDING-001")]
    public async Task TenantAccountAndSourceBindingsCannotBeSubstituted()
    {
        var delivery = await IssueAsync();
        await using (var other = Open(_otherTenant))
        {
            Assert.Equal(ChallengeDecision.Rejected,
                await Handler(other).ConsumeAsync(delivery.Id, _account, _source, delivery.Answer));
            Assert.Empty(await other.LoginChallenges.ToListAsync());
            other.LoginChallenges.Add(new LoginChallenge(_tenant, Guid.NewGuid(), _account, _source, new string('A', 64), Start));
            await Assert.ThrowsAsync<InvalidOperationException>(() => other.SaveChangesAsync());
        }
        await using var own = Open();
        var handler = Handler(own);
        Assert.Equal(ChallengeDecision.Rejected, await handler.ConsumeAsync(delivery.Id,
            LoginAccountKey.FromNormalizedAccount("DIFFERENT"), _source, delivery.Answer));
        Assert.Equal(ChallengeDecision.Rejected, await handler.ConsumeAsync(delivery.Id,
            _account, LoginSourceKey.FromTrustedPeer(IPAddress.Parse("192.0.2.2")), delivery.Answer));
        Assert.Null((await own.LoginChallenges.AsNoTracking().SingleAsync()).ConsumedAt);
        Assert.Equal(ChallengeDecision.Accepted, await handler.ConsumeAsync(delivery.Id, _account, _source, delivery.Answer));
    }

    /// <summary>Both successful and wrong submissions compete for a single committed consumed version.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("CaseId", "IDENTITY-CHALLENGE-CONCURRENT-001")]
    public async Task ConcurrentSubmissionsNeverAcceptTwice(bool firstWrong)
    {
        var delivery = await IssueAsync();
        var barrier = new SaveBarrier();
        await using var first = Open(interceptor: barrier);
        await using var second = Open(interceptor: barrier);
        var results = await Task.WhenAll(
            Handler(first).ConsumeAsync(delivery.Id, _account, _source, firstWrong ? "WRONG!" : delivery.Answer),
            Handler(second).ConsumeAsync(delivery.Id, _account, _source, delivery.Answer));
        var accepted = results.Count(item => item == ChallengeDecision.Accepted);
        if (firstWrong) Assert.InRange(accepted, 0, 1);
        else Assert.Equal(1, accepted);
        await using var verify = Open();
        Assert.Equal(Start, (await verify.LoginChallenges.AsNoTracking().SingleAsync()).ConsumedAt);
        Assert.Equal(ChallengeDecision.Rejected,
            await Handler(verify).ConsumeAsync(delivery.Id, _account, _source, delivery.Answer));
        Assert.Empty(first.ChangeTracker.Entries());
        Assert.Empty(second.ChangeTracker.Entries());
    }

    /// <summary>A wrong answer winning the commit burns the challenge even if the correct contender read the old version.</summary>
    [Fact]
    [Trait("CaseId", "IDENTITY-CHALLENGE-CONCURRENT-001")]
    public async Task WrongWinnerPreventsStaleCorrectContenderFromAccepting()
    {
        var delivery = await IssueAsync();
        var bothStaged = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var wrongCommitted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var wrong = Open(interceptor: new OrderedSave(true, bothStaged, wrongCommitted));
        await using var correct = Open(interceptor: new OrderedSave(false, bothStaged, wrongCommitted));
        var results = await Task.WhenAll(
            Handler(wrong).ConsumeAsync(delivery.Id, _account, _source, "WRONG!"),
            Handler(correct).ConsumeAsync(delivery.Id, _account, _source, delivery.Answer));
        Assert.All(results, decision => Assert.Equal(ChallengeDecision.Rejected, decision));
        await using var verify = Open();
        Assert.Equal(Start, (await verify.LoginChallenges.AsNoTracking().SingleAsync()).ConsumedAt);
        Assert.Equal(ChallengeDecision.Rejected,
            await Handler(verify).ConsumeAsync(delivery.Id, _account, _source, delivery.Answer));
    }

    /// <summary>Oversized and malformed UTF-16 inputs cannot skip burning a real bound attempt or reach MAC encoding.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("CaseId", "IDENTITY-CHALLENGE-ONCE-001")]
    public async Task OversizedAndIllFormedAnswersAreRejectedAndConsumed(bool malformed)
    {
        var delivery = await IssueAsync();
        var answer = malformed ? "\ud80023456" : new string('A', 10000);
        await using var database = Open();
        Assert.Equal(ChallengeDecision.Rejected,
            await Handler(database).ConsumeAsync(delivery.Id, _account, _source, answer));
        Assert.Equal(Start, (await database.LoginChallenges.AsNoTracking().SingleAsync()).ConsumedAt);
    }

    /// <summary>Missing internal binding and an unavailable secret are explicit failures, never unprotected issuance.</summary>
    [Fact]
    [Trait("CaseId", "IDENTITY-CHALLENGE-FAULT-001")]
    public async Task InvalidInternalBindingOrUnavailableKeyDoesNotCreateOrConsumeState()
    {
        var delivery = await IssueAsync();
        await using var database = Open();
        var handler = Handler(database);
        await Assert.ThrowsAsync<ArgumentNullException>(() => handler.IssueAsync(null!, _source));
        await Assert.ThrowsAsync<ArgumentNullException>(() => handler.IssueAsync(_account, null!));
        await Assert.ThrowsAsync<ArgumentNullException>(() => handler.ConsumeAsync(delivery.Id, null!, _source, delivery.Answer));
        await Assert.ThrowsAsync<ArgumentNullException>(() => handler.ConsumeAsync(delivery.Id, _account, null!, delivery.Answer));
        _protection.Dispose();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => handler.IssueAsync(_account, _source));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => handler.ConsumeAsync(delivery.Id, _account, _source, delivery.Answer));
        Assert.Empty(database.ChangeTracker.Entries());
        Assert.Null((await database.LoginChallenges.AsNoTracking().SingleAsync()).ConsumedAt);
    }

    /// <summary>A separate protection instance using the same deployed secret can verify a committed challenge after restart.</summary>
    [Fact]
    [Trait("CaseId", "IDENTITY-CHALLENGE-CRYPTO-001")]
    public async Task FreshProtectionInstanceCanConsumePersistedChallenge()
    {
        var delivery = await IssueAsync();
        using var restartedProtection = new LoginChallengeProtection(Enumerable.Range(0, 32).Select(item => (byte)item).ToArray());
        await using var database = Open();
        var handler = new LoginChallengeHandler(database, new FixedClock(Start), restartedProtection,
            NullLogger<LoginChallengeHandler>.Instance);
        Assert.Equal(ChallengeDecision.Accepted,
            await handler.ConsumeAsync(delivery.Id, _account, _source, delivery.Answer));
    }

    /// <summary>Save failures cannot return a renderable issuance or a valid consumed fact; later saves retain no staged state.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("CaseId", "IDENTITY-CHALLENGE-FAULT-001")]
    public async Task SaveFailurePropagatesWithoutDeliveryOrAcceptedDecision(bool consume)
    {
        var delivery = consume ? await IssueAsync() : null;
        var failure = new DbUpdateException("Injected challenge persistence failure.");
        await using var database = Open(interceptor: new FailSave(failure));
        var handler = Handler(database);
        var error = consume
            ? await Assert.ThrowsAsync<DbUpdateException>(() => handler.ConsumeAsync(delivery!.Id, _account, _source, delivery.Answer))
            : await Assert.ThrowsAsync<DbUpdateException>(() => handler.IssueAsync(_account, _source));
        Assert.Same(failure, error);
        Assert.Empty(database.ChangeTracker.Entries());
        await database.SaveChangesAsync();
        await using var verify = Open();
        var rows = await verify.LoginChallenges.AsNoTracking().ToListAsync();
        if (consume) Assert.Null(Assert.Single(rows).ConsumedAt);
        else Assert.Empty(rows);
    }

    /// <summary>Cancellation before or during SQL remains an error rather than a fabricated challenge outcome.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("CaseId", "IDENTITY-CHALLENGE-FAULT-001")]
    public async Task CancellationPropagatesAndDiscardsOnlyOwnedGraph(bool consume)
    {
        var delivery = consume ? await IssueAsync() : null;
        using var cancellation = new CancellationTokenSource();
        await using var database = Open(interceptor: new CancelOnSave(cancellation));
        var handler = Handler(database);
        if (consume)
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                handler.ConsumeAsync(delivery!.Id, _account, _source, delivery.Answer, cancellation.Token));
        else
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => handler.IssueAsync(_account, _source, cancellation.Token));
        Assert.Empty(database.ChangeTracker.Entries());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => handler.IssueAsync(_account, _source, new CancellationToken(true)));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => handler.ConsumeAsync(Guid.NewGuid(), _account, _source, "",
            new CancellationToken(true)));
        await using var verify = Open();
        if (consume) Assert.Null((await verify.LoginChallenges.SingleAsync()).ConsumedAt);
        else Assert.Empty(await verify.LoginChallenges.ToListAsync());
    }

    /// <summary>Outside transactions and caller-owned graphs cannot defer the one-shot commit or be cleared on rejection.</summary>
    [Fact]
    [Trait("CaseId", "IDENTITY-CHALLENGE-FAULT-001")]
    public async Task DirtyAndExternalTransactionsAreRejectedWithoutClearingCallerGraph()
    {
        await using var database = Open();
        database.LoginAccounts.Add(new LoginAccountBudget(_tenant, _account));
        var handler = Handler(database);
        await Assert.ThrowsAsync<InvalidOperationException>(() => handler.IssueAsync(_account, _source));
        await Assert.ThrowsAsync<InvalidOperationException>(() => handler.ConsumeAsync(Guid.NewGuid(), _account, _source, ""));
        Assert.Single(database.ChangeTracker.Entries());
        database.ChangeTracker.Clear();
        await using (var transaction = await database.Database.BeginTransactionAsync())
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => handler.IssueAsync(_account, _source));
            await Assert.ThrowsAsync<InvalidOperationException>(() => handler.ConsumeAsync(Guid.NewGuid(), _account, _source, ""));
            await transaction.RollbackAsync();
        }
        using var scope = new System.Transactions.TransactionScope(System.Transactions.TransactionScopeAsyncFlowOption.Enabled);
        await Assert.ThrowsAsync<InvalidOperationException>(() => handler.IssueAsync(_account, _source));
        await Assert.ThrowsAsync<InvalidOperationException>(() => handler.ConsumeAsync(Guid.NewGuid(), _account, _source, ""));
    }

    /// <summary>Corruption and clock reversal cannot be translated into either an empty challenge or a valid consumption.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("CaseId", "IDENTITY-CHALLENGE-FAULT-001")]
    public async Task CorruptMacOrFutureCreationFailsExplicitly(bool corruptMac)
    {
        var delivery = await IssueAsync();
        await using (var write = Open())
        {
            var row = await write.LoginChallenges.SingleAsync();
            if (corruptMac) write.Entry(row).Property(item => item.AnswerMac).CurrentValue = "secret-marker";
            else write.Entry(row).Property(item => item.CreatedAt).CurrentValue = Start.AddSeconds(1);
            await write.SaveChangesAsync();
        }
        await using var database = Open();
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Handler(database).ConsumeAsync(delivery.Id, _account, _source, delivery.Answer));
        Assert.DoesNotContain("secret-marker", error.ToString());
        Assert.Empty(database.ChangeTracker.Entries());
        Assert.Null((await database.LoginChallenges.AsNoTracking().SingleAsync()).ConsumedAt);
    }

    /// <summary>Unknown IDs and denial logs have a single expected shape with no secret or binding fields.</summary>
    [Fact]
    [Trait("CaseId", "IDENTITY-CHALLENGE-FAULT-001")]
    public async Task GenericRejectionsAndLogsExcludeChallengeMaterial()
    {
        var delivery = await IssueAsync();
        var logger = new CapturingLogger();
        await using var database = Open();
        var handler = new LoginChallengeHandler(database, new FixedClock(Start), _protection, logger);
        Assert.Equal(ChallengeDecision.Rejected, await handler.ConsumeAsync(Guid.Empty, _account, _source, ""));
        Assert.Equal(ChallengeDecision.Rejected, await handler.ConsumeAsync(Guid.NewGuid(), _account, _source, delivery.Answer));
        Assert.Equal(ChallengeDecision.Rejected, await handler.ConsumeAsync(delivery.Id, _account, _source, ""));
        Assert.Equal(3, logger.Messages.Count);
        Assert.All(logger.Messages, message =>
        {
            Assert.Contains("LoginChallengeRejected", message);
            Assert.DoesNotContain(delivery.Id.ToString("D"), message);
            Assert.DoesNotContain(delivery.Answer, message);
            Assert.DoesNotContain(_account.Digest, message);
            Assert.DoesNotContain(_source.Digest, message);
        });
    }

    private async Task<LoginChallengeDelivery> IssueAsync(DateTimeOffset? instant = null)
    {
        await using var database = Open();
        var delivery = await Handler(database, instant).IssueAsync(_account, _source);
        Assert.Empty(database.ChangeTracker.Entries());
        return delivery;
    }

    private PlatformDbContext Open(Guid? tenant = null, IInterceptor? interceptor = null)
    {
        MySqlTestConnection.ValidateTarget(_connection);
        var options = new DbContextOptionsBuilder<PlatformDbContext>()
            .UseMySQL(_connection, mysql => mysql.MigrationsHistoryTable("__WesPlatformMigrations"));
        if (interceptor is not null) options.AddInterceptors(interceptor);
        return new PlatformDbContext(options.Options, tenant ?? _tenant);
    }

    private LoginChallengeHandler Handler(PlatformDbContext database, DateTimeOffset? instant = null) =>
        new(database, new FixedClock(instant ?? Start), _protection, NullLogger<LoginChallengeHandler>.Instance);

    /// <summary>Deletes only challenges from this fixture's two random tenant scopes and erases its test-only key.</summary>
    public async Task DisposeAsync()
    {
        try
        {
            if (!_migrated) return;
            foreach (var tenant in new[] { _tenant, _otherTenant })
            {
                await using var database = Open(tenant);
                database.LoginChallenges.RemoveRange(await database.LoginChallenges.ToListAsync());
                await database.SaveChangesAsync();
            }
        }
        finally
        {
            _protection.Dispose();
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
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (eventData.Context!.ChangeTracker.HasChanges()) throw error;
            return ValueTask.FromResult(result);
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

    private sealed class CapturingLogger : ILogger<LoginChallengeHandler>
    {
        internal List<string> Messages { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Messages.Add(formatter(state, exception));
    }
}
