using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Wes.Notifications.Features.Alarms;
using Wes.Notifications.Infrastructure.Persistence;
using Xunit;

namespace Wes.Persistence.Tests;

/// <summary>Uses actual MySQL facts; no mock receiver or source authentication is substituted.</summary>
[Collection("PlatformPersistence")]
[Trait("Provider", "MySQL")]
public sealed class AlarmOccurrenceTests : IAsyncLifetime
{
    private readonly Guid _tenant = Guid.NewGuid();
    private readonly Guid _other = Guid.NewGuid();
    private readonly DateTimeOffset _now = new(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);
    private string _connection = "";
    private bool _ready;

    public async Task InitializeAsync()
    {
        _connection = MySqlTestConnection.LoadAuthorizedTestConnection();
        await using var database = Open(_tenant);
        await database.Database.MigrateAsync();
        _ready = true;
    }

    [Fact]
    [Trait("CaseId", "TC-T14-OCC-01")]
    public async Task RepeatReplaysOriginalFactButChangedInputConflicts()
    {
        var input = Input();
        var first = await Record(input);
        Assert.Equal(OccurrenceDecision.Recorded, first.Decision);
        Assert.Equal(OccurrenceDecision.Replayed, (await Record(input)).Decision);
        Assert.Equal(first.Value!.Id, (await Record(input)).Value!.Id);
        foreach (var changed in new[]
        {
            input with { Summary = "Changed" },
            input with { ResourceId = Guid.NewGuid() },
            input with { OccurredAt = input.OccurredAt.AddSeconds(-1) },
            input with { CorrelationId = Guid.NewGuid() },
            input with { Severity = AlarmSeverity.Critical },
            input with { SourceKind = AlarmSourceKind.Manual },
            input with { Code = "Other.Code" },
        })
        {
            var conflict = await Record(changed);
            Assert.Equal(OccurrenceDecision.IdempotencyConflict, conflict.Decision);
            Assert.Null(conflict.Value);
        }
        await using var verify = Open(_tenant);
        var fact = Assert.Single(await verify.Occurrences.AsNoTracking().ToListAsync());
        Assert.Equal(input.Summary, fact.Summary);
        Assert.Equal(input.ResourceId, fact.ResourceId);
        Assert.Equal(first.Value.Id, fact.Id);
    }

    [Fact]
    [Trait("CaseId", "TC-T14-OCC-02")]
    public async Task SameResourceWindowPreservesEveryEventAndLateSourceTimestamp()
    {
        var input = Input();
        var late = input with { SourceEventId = Guid.NewGuid(), OccurredAt = _now.AddDays(-7) };
        var first = await Record(input);
        var second = await Record(late);
        Assert.Equal(OccurrenceDecision.Recorded, first.Decision);
        Assert.Equal(OccurrenceDecision.Recorded, second.Decision);
        Assert.NotEqual(first.Value!.Id, second.Value!.Id);
        Assert.Equal(_now.AddDays(-7), second.Value.OccurredAt);
        Assert.Equal(_now, second.Value.ReceivedAt);
        await using var verify = Open(_tenant);
        Assert.Equal(2, await verify.Occurrences.CountAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("CaseId", "TC-T14-OCC-03")]
    public async Task ConcurrentSourceIdentityHasExactlyOneRecordedFact(bool mismatch)
    {
        var input = Input();
        var other = mismatch ? input with { Summary = "Other winner" } : input;
        var results = await Task.WhenAll(Record(input), Record(other));
        Assert.Single(results, result => result.Decision == OccurrenceDecision.Recorded);
        Assert.Single(results, result => result.Decision ==
            (mismatch ? OccurrenceDecision.IdempotencyConflict : OccurrenceDecision.Replayed));
        await using var verify = Open(_tenant);
        var fact = Assert.Single(await verify.Occurrences.ToListAsync());
        Assert.Equal(results.Single(result => result.Decision == OccurrenceDecision.Recorded).Value!.Id, fact.Id);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BothReadersSeeMissingBeforeConcurrentInsertStillConverges(bool mismatch)
    {
        var input = Input();
        var barrier = new MissingReadBarrier();
        async Task<OccurrenceResult> Receive(AlarmOccurrenceInput value)
        {
            await using var database = Open(_tenant, barrier);
            return await new AlarmOccurrenceStore(database, new Clock(_now)).RecordAsync(value);
        }
        var results = await Task.WhenAll(Receive(input), Receive(mismatch ? input with { Summary = "Other" } : input));
        Assert.Equal(3, barrier.Readers);
        Assert.Single(results, result => result.Decision == OccurrenceDecision.Recorded);
        Assert.Single(results, result => result.Decision ==
            (mismatch ? OccurrenceDecision.IdempotencyConflict : OccurrenceDecision.Replayed));
        await using var verify = Open(_tenant);
        Assert.Single(await verify.Occurrences.ToListAsync());
    }

    [Fact]
    public async Task SourceSeventhFractionalDigitSurvivesPersistenceAndReplay()
    {
        var input = Input() with { OccurredAt = _now.AddTicks(-1) };
        var result = await Record(input);
        var replay = await Record(input);
        Assert.Equal(input.OccurredAt, result.Value!.OccurredAt);
        Assert.Equal(input.OccurredAt, replay.Value!.OccurredAt);
        Assert.Equal(OccurrenceDecision.Replayed, replay.Decision);
        Assert.Equal(OccurrenceDecision.IdempotencyConflict,
            (await Record(input with { OccurredAt = input.OccurredAt.AddTicks(-1) })).Decision);
        await using var database = Open(_tenant);
        Assert.Equal(input.OccurredAt, Assert.Single(await database.Occurrences.ToListAsync()).OccurredAt);
    }

    [Fact]
    public async Task ProviderFailureIsExplicitWithoutLeakingProviderMessage()
    {
        await using var database = Open(_tenant, new ProviderFault());
        var failure = await Assert.ThrowsAsync<AlarmOccurrenceDatabaseException>(() =>
            new AlarmOccurrenceStore(database, new Clock(_now)).RecordAsync(Input()));
        Assert.Null(failure.InnerException);
        Assert.DoesNotContain("controlled-provider-detail", failure.ToString());
        await using var verify = Open(_tenant);
        Assert.Empty(await verify.Occurrences.ToListAsync());
    }

    [Fact]
    public async Task ReceivedAtIsTruncatedToMicrosecondsBeforeReturningOrReplaying()
    {
        var input = Input();
        OccurrenceResult recorded;
        await using (var database = Open(_tenant))
            recorded = await new AlarmOccurrenceStore(database, new Clock(_now.AddTicks(7))).RecordAsync(input);
        var replay = await Record(input);
        Assert.Equal(_now, recorded.Value!.ReceivedAt);
        Assert.Equal(recorded.Value, replay.Value);
    }

    [Fact]
    public async Task AmbientTransactionCannotEnlistAlarmIngress()
    {
        var input = Input();
        using (var transaction = new System.Transactions.TransactionScope(
                   System.Transactions.TransactionScopeAsyncFlowOption.Enabled))
        {
            await using var database = Open(_tenant);
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                new AlarmOccurrenceStore(database, new Clock(_now)).RecordAsync(input));
            Assert.Contains("independent Notifications owner scope", error.Message);
        }
        await using var verify = Open(_tenant);
        Assert.Empty(await verify.Occurrences.ToListAsync());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task AllSaveOverloadsRejectCrossTenantAppends(int overload)
    {
        await Record(Input());
        await using var database = Open(_tenant);
        var fact = Assert.Single(await database.Occurrences.AsNoTracking().ToListAsync());
        fact.TenantId = _other;
        fact.Id = Guid.NewGuid();
        database.Occurrences.Add(fact);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Save(database, overload));
        await using var verify = Open(_other);
        Assert.Empty(await verify.Occurrences.ToListAsync());
    }

    [Fact]
    public async Task CaseDistinctSourceOwnersAreDistinctDurableIdentities()
    {
        var input = Input();
        var first = await Record(input);
        var second = await Record(input with { SourceOwner = "stacker" });
        Assert.Equal(OccurrenceDecision.Recorded, second.Decision);
        Assert.NotEqual(first.Value!.Id, second.Value!.Id);
        Assert.Equal(OccurrenceDecision.Replayed, (await Record(input)).Decision);
        Assert.Equal(OccurrenceDecision.Replayed, (await Record(input with { SourceOwner = "stacker" })).Decision);
        await using var database = Open(_tenant);
        Assert.Equal(2, await database.Occurrences.CountAsync());
    }

    [Fact]
    public async Task ActualDatabaseUsesApprovedBinaryColumnCollations()
    {
        await using var database = Open(_tenant);
        await database.Database.OpenConnectionAsync();
        await using var command = database.Database.GetDbConnection().CreateCommand();
        command.CommandText = """
            SELECT COLUMN_NAME, COLLATION_NAME FROM information_schema.COLUMNS
            WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'ntf_alarm_occurrences'
              AND COLUMN_NAME IN ('SourceOwner', 'Code', 'Summary', 'Fingerprint')
            """;
        await using var reader = await command.ExecuteReaderAsync();
        var collations = new Dictionary<string, string>(StringComparer.Ordinal);
        while (await reader.ReadAsync()) collations.Add(reader.GetString(0), reader.GetString(1));
        Assert.Equal(4, collations.Count);
        Assert.Equal("ascii_bin", collations["SourceOwner"]);
        Assert.Equal("ascii_bin", collations["Code"]);
        Assert.Equal("ascii_bin", collations["Fingerprint"]);
        Assert.Equal("utf8mb4_bin", collations["Summary"]);
    }

    [Fact]
    [Trait("CaseId", "TC-T14-OCC-04")]
    public async Task OwnerIdentityAndTenantBothPartitionSourceEvent()
    {
        var input = Input();
        await Record(input);
        await Record(input with { SourceOwner = "OtherOwner" });
        await using (var other = Open(_other))
        {
            var result = await new AlarmOccurrenceStore(other, new Clock(_now)).RecordAsync(input);
            Assert.Equal(OccurrenceDecision.Recorded, result.Decision);
            Assert.Single(await other.Occurrences.ToListAsync());
        }
        await using var verify = Open(_tenant);
        Assert.Equal(2, await verify.Occurrences.CountAsync());
    }

    [Fact]
    [Trait("CaseId", "TC-T14-OCC-05")]
    public async Task FailedSaveAndPreCancellationLeaveNoFacts()
    {
        await using (var database = Open(_tenant, new SaveFault()))
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                new AlarmOccurrenceStore(database, new Clock(_now)).RecordAsync(Input()));
        }
        await using (var database = Open(_tenant))
        {
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                new AlarmOccurrenceStore(database, new Clock(_now)).RecordAsync(Input(), cancellation.Token));
        }
        await using var verify = Open(_tenant);
        Assert.Empty(await verify.Occurrences.ToListAsync());
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(3, false)]
    [InlineData(0, true)]
    [InlineData(1, true)]
    [InlineData(2, true)]
    [InlineData(3, true)]
    [Trait("CaseId", "TC-T14-OCC-06")]
    public async Task AllSaveOverloadsRejectChangingOrDeletingFacts(int overload, bool delete)
    {
        var original = await Record(Input());
        await using (var database = Open(_tenant))
        {
            var fact = Assert.Single(await database.Occurrences.ToListAsync());
            if (delete) database.Occurrences.Remove(fact);
            else fact.Summary = "Rewritten history";
            await Assert.ThrowsAsync<InvalidOperationException>(() => Save(database, overload));
        }
        await using var verify = Open(_tenant);
        Assert.Equal(original.Value!.Summary, Assert.Single(await verify.Occurrences.ToListAsync()).Summary);
    }

    [Fact]
    public async Task CrossTenantAppendAndDirtyOwnerUnitAreRejected()
    {
        await Record(Input());
        await using (var database = Open(_tenant))
        {
            var fact = Assert.Single(await database.Occurrences.AsNoTracking().ToListAsync());
            fact.Id = Guid.NewGuid();
            fact.TenantId = _other;
            database.Occurrences.Add(fact);
            await Assert.ThrowsAsync<InvalidOperationException>(() => database.SaveChangesAsync());
        }
        await using (var database = Open(_tenant))
        {
            _ = await database.Occurrences.ToListAsync();
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                new AlarmOccurrenceStore(database, new Clock(_now)).RecordAsync(Input()));
        }
        await using (var database = Open(_tenant))
        {
            await using var transaction = await database.Database.BeginTransactionAsync();
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                new AlarmOccurrenceStore(database, new Clock(_now)).RecordAsync(Input()));
        }
        await using var verify = Open(_tenant);
        Assert.Single(await verify.Occurrences.ToListAsync());
    }

    [Theory]
    [InlineData("owner-newline")]
    [InlineData("owner-space")]
    [InlineData("owner-long")]
    [InlineData("event-empty")]
    [InlineData("resource-empty")]
    [InlineData("correlation-empty")]
    [InlineData("unknown-kind")]
    [InlineData("unknown-severity")]
    [InlineData("summary-empty")]
    [InlineData("summary-space")]
    [InlineData("summary-long")]
    [InlineData("code-newline")]
    [InlineData("code-long")]
    [InlineData("future-time")]
    [InlineData("non-utc")]
    [Trait("CaseId", "TC-T14-OCC-07")]
    public async Task InvalidInputDoesNotPersistAndReturnsExplicitError(string variant)
    {
        var input = Input();
        input = variant switch
        {
            "owner-newline" => input with { SourceOwner = "Pack\n" },
            "owner-space" => input with { SourceOwner = "Pack Other" },
            "owner-long" => input with { SourceOwner = new string('a', 65) },
            "event-empty" => input with { SourceEventId = Guid.Empty },
            "resource-empty" => input with { ResourceId = Guid.Empty },
            "correlation-empty" => input with { CorrelationId = Guid.Empty },
            "unknown-kind" => input with { SourceKind = (AlarmSourceKind)99 },
            "unknown-severity" => input with { Severity = (AlarmSeverity)99 },
            "summary-empty" => input with { Summary = "" },
            "summary-space" => input with { Summary = " \n\t" },
            "summary-long" => input with { Summary = new string('a', 513) },
            "code-newline" => input with { Code = "Blocked\n" },
            "code-long" => input with { Code = new string('a', 65) },
            "future-time" => input with { OccurredAt = _now.AddTicks(1) },
            _ => input with { OccurredAt = _now.ToOffset(TimeSpan.FromHours(8)) },
        };
        var result = await Record(input);
        Assert.Equal(OccurrenceDecision.Invalid, result.Decision);
        Assert.Null(result.Value);
        Assert.False(string.IsNullOrWhiteSpace(result.ErrorCode));
        await using var verify = Open(_tenant);
        Assert.Empty(await verify.Occurrences.ToListAsync());
    }

    [Theory]
    [InlineData(1)]
    [InlineData(512)]
    public async Task LegalSummaryLengthAndExactClockTimePersistWithoutTruncation(int length)
    {
        var input = Input() with { Summary = new string('a', length), OccurredAt = _now };
        var result = await Record(input);
        Assert.Equal(OccurrenceDecision.Recorded, result.Decision);
        Assert.Equal(input.Summary, result.Value!.Summary);
        await using var verify = Open(_tenant);
        Assert.Equal(input.Summary, Assert.Single(await verify.Occurrences.ToListAsync()).Summary);
    }

    public async Task DisposeAsync()
    {
        if (!_ready) return;
        foreach (var tenant in new[] { _tenant, _other })
        {
            await using var database = Open(tenant);
            await database.Database.ExecuteSqlInterpolatedAsync(
                $"DELETE FROM ntf_alarm_occurrences WHERE TenantId = {tenant.ToString("D")}");
        }
    }

    private AlarmOccurrenceInput Input() => new(
        Guid.NewGuid(), "Stacker", AlarmSourceKind.Device, Guid.NewGuid(), "Device.Blocked",
        AlarmSeverity.High, _now.AddSeconds(-1), Guid.NewGuid(), "Device reports blocked path");

    private async Task<OccurrenceResult> Record(AlarmOccurrenceInput input)
    {
        await using var database = Open(_tenant);
        return await new AlarmOccurrenceStore(database, new Clock(_now)).RecordAsync(input);
    }

    private NotificationsDbContext Open(Guid tenant, IInterceptor? interceptor = null)
    {
        MySqlTestConnection.ValidateTarget(_connection);
        var options = new DbContextOptionsBuilder<NotificationsDbContext>().UseMySQL(_connection,
            mysql => mysql.MigrationsHistoryTable("__WesNotificationsMigrations"));
        if (interceptor is not null) options.AddInterceptors(interceptor);
        return new(options.Options, tenant);
    }

    private static Task Save(NotificationsDbContext database, int overload) => overload switch
    {
        0 => Task.Run(() => database.SaveChanges()),
        1 => Task.Run(() => database.SaveChanges(acceptAllChangesOnSuccess: false)),
        2 => database.SaveChangesAsync(),
        _ => database.SaveChangesAsync(acceptAllChangesOnSuccess: false),
    };

    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class SaveFault : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default) =>
            ValueTask.FromException<InterceptionResult<int>>(new InvalidOperationException("Controlled save interruption."));
    }

    // Both initial SELECTs have completed before either receiver can reach INSERT.
    private sealed class MissingReadBarrier : DbCommandInterceptor
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _readers;
        internal int Readers => _readers;

        public override async ValueTask<DbDataReader> ReaderExecutedAsync(
            DbCommand command, CommandExecutedEventData eventData, DbDataReader result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.StartsWith("SELECT", StringComparison.Ordinal) &&
                command.CommandText.Contains("ntf_alarm_occurrences", StringComparison.Ordinal) &&
                Interlocked.Increment(ref _readers) <= 2)
            {
                if (_readers == 2) _release.TrySetResult();
                await _release.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
            }
            return result;
        }
    }

    private sealed class ControlledProviderException() : DbException("controlled-provider-detail");

    private sealed class ProviderFault : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default) =>
            ValueTask.FromException<InterceptionResult<int>>(new ControlledProviderException());
    }
}
