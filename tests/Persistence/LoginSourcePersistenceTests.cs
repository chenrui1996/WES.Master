using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Wes.Platform.Domain;
using Wes.Platform.Features.Identity;
using Wes.Platform.Infrastructure.Persistence;
using Xunit;

namespace Wes.Persistence.Tests;

/// <summary>Real-source CAS and failure tests; every fixture deletes only its own generated source keys.</summary>
[Collection("PlatformPersistence")]
[Trait("Provider", "MySQL")]
public sealed class LoginSourcePersistenceTests : IAsyncLifetime
{
    private static readonly DateTimeOffset Start = new(2026, 10, 24, 1, 0, 0, TimeSpan.Zero);
    private readonly LoginSourceKey _source = RandomSource();
    private readonly LoginSourceKey _other = RandomSource();
    private string _connection = "";
    private bool _migrated;

    /// <summary>Applies only this owner context's reviewed migration to the authorized isolated database.</summary>
    public async Task InitializeAsync()
    {
        _connection = MySqlTestConnection.LoadAuthorizedTestConnection();
        await using var database = Open();
        await database.Database.MigrateAsync();
        _migrated = true;
    }

    /// <summary>New contexts observe all prior admissions; no tenant/account parameter can fragment source authority.</summary>
    [Fact]
    [Trait("CaseId", "IDENTITY-SOURCE-DB-001")]
    public async Task Budget_SurvivesNewHandlersAndContexts_AndOtherSourcesRemainIndependent()
    {
        for (var attempt = 0; attempt < 30; attempt++)
        {
            await using var write = Open();
            Assert.Equal(SourceAdmission.Admitted, await Handler(write).HandleAsync(_source));
        }
        await using var read = Open();
        Assert.Equal(SourceAdmission.RateLimited, await Handler(read).HandleAsync(_source));
        Assert.Equal(SourceAdmission.Admitted, await Handler(read).HandleAsync(_other));
        var stored = await read.Sources.AsNoTracking().SingleAsync(item => item.SourceDigest == _source.Digest);
        Assert.Equal(30, stored.Observations.Count);
        Assert.All(stored.Observations, instant => Assert.Equal(Start, instant));
        Assert.Single(read.Model.GetEntityTypes());
        Assert.Null(read.Model.FindEntityType(typeof(Session)));
        Assert.Null(read.Model.FindEntityType(typeof(AccessRole)));
        Assert.Null(read.Model.FindEntityType(typeof(LoginSourceBudget))!.FindProperty("TenantId"));
    }

    /// <summary>At capacity twenty-nine, two overlapping handlers commit one remaining admission, not two.</summary>
    [Fact]
    [Trait("CaseId", "IDENTITY-SOURCE-CONCURRENT-001")]
    public async Task ConcurrentThirtiethRequest_AdmitsOnlyOne()
    {
        await SeedAsync(29);
        var barrier = new SaveBarrier();
        await using var first = Open(barrier);
        await using var second = Open(barrier);
        var results = await Task.WhenAll(Handler(first).HandleAsync(_source), Handler(second).HandleAsync(_source));
        Assert.Equal(1, results.Count(item => item == SourceAdmission.Admitted));
        Assert.Equal(1, results.Count(item => item == SourceAdmission.RateLimited));
        await using var verify = Open();
        Assert.Equal(30, (await verify.Sources.SingleAsync(item => item.SourceDigest == _source.Digest)).Observations.Count);
        Assert.Empty(first.ChangeTracker.Entries());
        Assert.Empty(second.ChangeTracker.Entries());
    }

    /// <summary>Simultaneous first inserts converge on one row and retain both counted requests, not a success-shaped loss.</summary>
    [Fact]
    [Trait("CaseId", "IDENTITY-SOURCE-CONCURRENT-001")]
    public async Task ConcurrentFirstInsert_RereadsUniqueKeyWinnerAndCountsBoth()
    {
        var barrier = new SaveBarrier();
        await using var first = Open(barrier);
        await using var second = Open(barrier);
        var results = await Task.WhenAll(Handler(first).HandleAsync(_source), Handler(second).HandleAsync(_source));
        Assert.All(results, item => Assert.Equal(SourceAdmission.Admitted, item));
        await using var verify = Open();
        Assert.Equal(2, (await verify.Sources.SingleAsync(item => item.SourceDigest == _source.Digest)).Observations.Count);
    }

    /// <summary>datetime(6) round-trip cannot move the microsecond window boundary after a restart.</summary>
    [Fact]
    [Trait("CaseId", "IDENTITY-SOURCE-WINDOW-001")]
    public async Task Persistence_RetainsMicrosecondRollingBoundary()
    {
        var instant = Start.AddTicks(19);
        await SeedAsync(30, instant);
        await using (var early = Open())
        {
            Assert.Equal(SourceAdmission.RateLimited,
                await Handler(early, Start.AddSeconds(60)).HandleAsync(_source));
        }
        await using (var exact = Open())
        {
            Assert.Equal(SourceAdmission.Admitted,
                await Handler(exact, Start.AddSeconds(60).AddTicks(10)).HandleAsync(_source));
        }
        await using var verify = Open();
        Assert.Equal(Start.AddSeconds(60).AddTicks(10),
            Assert.Single((await verify.Sources.SingleAsync(item => item.SourceDigest == _source.Digest)).Observations));
    }

    /// <summary>Cancellation cannot count a request or be translated into either expected admission result.</summary>
    [Fact]
    [Trait("CaseId", "IDENTITY-SOURCE-FAULT-001")]
    public async Task CanceledRequest_PropagatesWithoutConsumption()
    {
        await using var write = Open();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Handler(write).HandleAsync(_source, new CancellationToken(true)));
        Assert.Empty(write.ChangeTracker.Entries());
        Assert.False(await write.Sources.AnyAsync(item => item.SourceDigest == _source.Digest));
    }

    /// <summary>Unexpected persistence faults propagate; neither a staged admission nor an invented rejection hides them.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("CaseId", "IDENTITY-SOURCE-FAULT-001")]
    public async Task SaveFailure_LeavesNoAdmissionOrPendingMutation(bool existing)
    {
        if (existing) await SeedAsync(1);
        await using (var write = Open(new FailSave()))
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => Handler(write).HandleAsync(_source));
            Assert.Empty(write.ChangeTracker.Entries());
        }
        await using var verify = Open();
        var stored = await verify.Sources.AsNoTracking().SingleOrDefaultAsync(item => item.SourceDigest == _source.Digest);
        Assert.Equal(existing ? 1 : 0, stored?.Observations.Count ?? 0);
    }

    /// <summary>Unrelenting real CAS conflicts have a hard stop; the losing request is never counted or reported as admitted.</summary>
    [Fact]
    [Trait("CaseId", "IDENTITY-SOURCE-FAULT-001")]
    public async Task RepeatedRealConflicts_ExhaustBoundedRetriesWithoutAdmission()
    {
        await SeedAsync(1);
        var conflicts = new CompetingWriter(async () =>
        {
            await using var winner = Open();
            var current = await winner.Sources.SingleAsync(item => item.SourceDigest == _source.Digest);
            Assert.Equal(SourceAdmission.Admitted, current.Admit(Start));
            await winner.SaveChangesAsync();
        });
        var logger = new CapturingLogger();
        await using (var loser = Open(conflicts))
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                new AdmitLoginSourceHandler(loser, new FixedClock(Start), logger).HandleAsync(_source));
            Assert.Empty(loser.ChangeTracker.Entries());
        }
        Assert.Equal(8, conflicts.Writes);
        Assert.Equal(8, logger.Messages.Count(message => message.Contains("LoginSourceVersionConflict", StringComparison.Ordinal)));
        Assert.Contains(logger.Messages, message => message.Contains("LoginSourceContentionExhausted", StringComparison.Ordinal));
        Assert.All(logger.Messages, message => Assert.DoesNotContain(_source.Digest, message));
        await using var verify = Open();
        Assert.Equal(9, (await verify.Sources.SingleAsync(item => item.SourceDigest == _source.Digest)).Observations.Count);
    }

    /// <summary>An unrelated persistence exception is not treated as a retryable version or primary-key race.</summary>
    [Fact]
    [Trait("CaseId", "IDENTITY-SOURCE-FAULT-001")]
    public async Task UnclassifiedDatabaseFault_IsPropagatedWithoutRetry()
    {
        var error = new DbUpdateException("Injected unclassified persistence fault.");
        var failure = new ThrowOnSave(error);
        await using var write = Open(failure);
        Assert.Same(error, await Assert.ThrowsAsync<DbUpdateException>(() => Handler(write).HandleAsync(_source)));
        Assert.Equal(1, failure.Writes);
        Assert.Empty(write.ChangeTracker.Entries());
        Assert.False(await write.Sources.AnyAsync(item => item.SourceDigest == _source.Digest));
    }

    /// <summary>Cancellation after staging an observation still prevents a success result and durable consumption.</summary>
    [Fact]
    [Trait("CaseId", "IDENTITY-SOURCE-FAULT-001")]
    public async Task CancellationDuringSave_DoesNotDiscloseAdmission()
    {
        using var cancellation = new CancellationTokenSource();
        await using (var write = Open(new CancelOnSave(cancellation)))
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                Handler(write).HandleAsync(_source, cancellation.Token));
            Assert.Empty(write.ChangeTracker.Entries());
        }
        await using var verify = Open();
        Assert.False(await verify.Sources.AnyAsync(item => item.SourceDigest == _source.Digest));
    }

    /// <summary>A later process with a lagging clock cannot expire durable observations early by clamping or resetting state.</summary>
    [Fact]
    [Trait("CaseId", "IDENTITY-SOURCE-CLOCK-001")]
    public async Task ClockBehindDurableState_IsExplicitFault()
    {
        await SeedAsync(1, Start.AddSeconds(1));
        await using (var write = Open())
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => Handler(write).HandleAsync(_source));
            Assert.Empty(write.ChangeTracker.Entries());
        }
        await using var verify = Open();
        Assert.Equal(Start.AddSeconds(1),
            Assert.Single((await verify.Sources.SingleAsync(item => item.SourceDigest == _source.Digest)).Observations));
    }

    /// <summary>The source context applies only its own migration and cannot silently reuse session/role migration identity.</summary>
    [Fact]
    [Trait("CaseId", "IDENTITY-SOURCE-MIGRATION-001")]
    public async Task SourceMigrationHistory_IsSeparateFromTenantPlatformHistory()
    {
        await using var source = Open();
        var applied = Assert.Single(await source.Database.GetAppliedMigrationsAsync());
        Assert.EndsWith("_InitialLoginSources", applied);
        using var tenant = new PlatformDesignTimeFactory().CreateDbContext([]);
        Assert.DoesNotContain(applied, tenant.Database.GetMigrations());
        Assert.Contains(tenant.Database.GetMigrations(), item => item.EndsWith("_InitialSessions", StringComparison.Ordinal));
        Assert.Contains(tenant.Database.GetMigrations(), item => item.EndsWith("_AccessSettingsAudit", StringComparison.Ordinal));
    }

    /// <summary>A failed source budget must never clear already pending work or join another owner's transaction.</summary>
    [Fact]
    [Trait("CaseId", "IDENTITY-SOURCE-FAULT-001")]
    public async Task DirtyContextOrAmbientTransaction_IsRejectedWithoutDiscardingCallersState()
    {
        await using var dirty = Open();
        dirty.Sources.Add(new LoginSourceBudget(_source));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Handler(dirty).HandleAsync(_source));
        Assert.Single(dirty.ChangeTracker.Entries());
        await using var transactional = Open();
        await using var transaction = await transactional.Database.BeginTransactionAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => Handler(transactional).HandleAsync(_source));
    }

    /// <summary>System.Transactions must not delay source commitment until after password verification or response disclosure.</summary>
    [Fact]
    [Trait("CaseId", "IDENTITY-SOURCE-FAULT-001")]
    public async Task AmbientTransaction_IsRejectedBeforeOpeningConnection()
    {
        using var transaction = new System.Transactions.TransactionScope(System.Transactions.TransactionScopeAsyncFlowOption.Enabled);
        await using var write = Open();
        await Assert.ThrowsAsync<InvalidOperationException>(() => Handler(write).HandleAsync(_source));
        Assert.Equal(System.Data.ConnectionState.Closed, write.Database.GetDbConnection().State);
    }

    /// <summary>Corrupt storage fails closed even if it looks like an empty or expired budget.</summary>
    [Theory]
    [InlineData("null")]
    [InlineData("secret-marker")]
    [InlineData("[]")]
    [InlineData("[\"2026-10-24T01:00:01Z\"]")]
    [Trait("CaseId", "IDENTITY-SOURCE-CLOCK-001")]
    public async Task CorruptObservations_AreNotReplacedWithEmptyBudget(string data)
    {
        await SeedAsync(1);
        await using (var seed = Open())
        {
            var stored = await seed.Sources.SingleAsync(item => item.SourceDigest == _source.Digest);
            seed.Entry(stored).Property("ObservationsData").CurrentValue = data;
            await seed.SaveChangesAsync();
        }
        await using (var read = Open())
        {
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Handler(read).HandleAsync(_source));
            Assert.DoesNotContain("secret-marker", error.ToString());
            Assert.Empty(read.ChangeTracker.Entries());
        }
        await using var verify = Open();
        var row = await verify.Sources.SingleAsync(item => item.SourceDigest == _source.Digest);
        Assert.Equal(data, verify.Entry(row).Property("ObservationsData").CurrentValue);
    }

    /// <summary>Logs carry fixed outcomes, never IPs, source digests, serialized observations or driver exception payloads.</summary>
    [Fact]
    [Trait("CaseId", "IDENTITY-SOURCE-FAULT-001")]
    public async Task RejectionLogs_ExcludeSourceIdentifiers()
    {
        await SeedAsync(30);
        var logger = new CapturingLogger();
        await using var write = Open();
        Assert.Equal(SourceAdmission.RateLimited,
            await new AdmitLoginSourceHandler(write, new FixedClock(Start), logger).HandleAsync(_source));
        var message = Assert.Single(logger.Messages);
        Assert.Contains("LoginSourceRateLimited", message);
        Assert.DoesNotContain(_source.Digest, message);
        Assert.DoesNotContain("ObservationsData", message);
    }

    private async Task SeedAsync(int count, DateTimeOffset? instant = null)
    {
        await using var database = Open();
        var budget = new LoginSourceBudget(_source);
        for (var index = 0; index < count; index++) budget.Admit(instant ?? Start);
        database.Sources.Add(budget);
        await database.SaveChangesAsync();
    }

    private LoginSourceDbContext Open(IInterceptor? interceptor = null)
    {
        MySqlTestConnection.ValidateTarget(_connection);
        var options = new DbContextOptionsBuilder<LoginSourceDbContext>()
            .UseMySQL(_connection, mysql => mysql.MigrationsHistoryTable("__WesLoginSourceMigrations"));
        if (interceptor is not null) options.AddInterceptors(interceptor);
        return new LoginSourceDbContext(options.Options);
    }

    private static AdmitLoginSourceHandler Handler(LoginSourceDbContext database, DateTimeOffset? instant = null) =>
        new(database, new FixedClock(instant ?? Start), NullLogger<AdmitLoginSourceHandler>.Instance);

    private static LoginSourceKey RandomSource() => LoginSourceKey.FromTrustedPeer(new IPAddress(Guid.NewGuid().ToByteArray()));

    /// <summary>Deletes only the two random source digests owned by this fixture, never clears a global budget table.</summary>
    public async Task DisposeAsync()
    {
        if (!_migrated) return;
        await using var database = Open();
        database.Sources.RemoveRange(await database.Sources
            .Where(item => item.SourceDigest == _source.Digest || item.SourceDigest == _other.Digest).ToListAsync());
        await database.SaveChangesAsync();
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

    private sealed class FailSave : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Injected source persistence failure.");
    }

    private sealed class CompetingWriter(Func<Task> commit) : SaveChangesInterceptor
    {
        internal int Writes { get; private set; }
        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            Writes++;
            await commit();
            return result;
        }
    }

    private sealed class ThrowOnSave(DbUpdateException error) : SaveChangesInterceptor
    {
        internal int Writes { get; private set; }
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            Writes++;
            throw error;
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

    private sealed class CapturingLogger : ILogger<AdmitLoginSourceHandler>
    {
        internal List<string> Messages { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Messages.Add(formatter(state, exception));
    }
}
