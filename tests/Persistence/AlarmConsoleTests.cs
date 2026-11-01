using System.Data.Common;
using System.Collections.Immutable;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Wes.Notifications;
using Wes.Notifications.Features.Alarms;
using Wes.Notifications.Infrastructure.Persistence;
using Wes.Platform;
using Wes.Platform.Domain;
using Wes.Platform.Infrastructure.Persistence;
using Xunit;

namespace Wes.Persistence.Tests;

[Collection("PlatformPersistence")]
[Trait("Provider", "MySQL")]
public sealed class AlarmConsoleTests : IAsyncLifetime
{
    private readonly Guid _tenant = Guid.NewGuid();
    private readonly Guid _creator = Guid.NewGuid();
    private readonly Clock _clock = new(new(2026, 11, 1, 12, 0, 0, TimeSpan.Zero));
    private string _connection = "";
    private Authority _authority = null!;
    private bool _platformReady;
    private AlarmSubject Subject => new(_tenant, _creator, Guid.NewGuid());

    public async Task InitializeAsync()
    {
        _connection = MySqlTestConnection.LoadAuthorizedTestConnection();
        MySqlTestConnection.ValidateTarget(_connection);
        _authority = new(_creator);
        await using var database = Open();
        await database.Database.MigrateAsync();
    }

    [Fact]
    public async Task AuthorizedMySqlHasAlarmAggregateSchema()
    {
        await using var database = Open();
        Assert.Contains(database.Model.GetEntityTypes(), entity => entity.GetTableName() == "ntf_alarms");
        await database.Database.OpenConnectionAsync();
        using var command = database.Database.GetDbConnection().CreateCommand();
        command.CommandText = "SELECT COLUMN_NAME,COLLATION_NAME FROM information_schema.COLUMNS " +
            "WHERE TABLE_SCHEMA=DATABASE() AND TABLE_NAME='ntf_alarm_operations' AND COLUMN_NAME IN ('Kind','Fingerprint','Body')";
        using var reader = await command.ExecuteReaderAsync();
        var collations = new Dictionary<string, string>();
        while (await reader.ReadAsync()) collations.Add(reader.GetString(0), reader.GetString(1));
        Assert.Equal("ascii_bin", collations["Kind"]);
        Assert.Equal("ascii_bin", collations["Fingerprint"]);
        Assert.Equal("utf8mb4_bin", collations["Body"]);
    }

    [Fact]
    public async Task ReceivedTime29_30_31BucketsKeepDistinctMembershipsAndLateSourceFacts()
    {
        var resource = Guid.NewGuid();
        _clock.UtcNow = _clock.UtcNow.AddSeconds(29);
        var first = await Raw(resource);
        var second = await Raw(resource);
        var a = await Process(first);
        var b = await Process(second);
        Assert.Equal(a.Id, b.Id);
        Assert.Equal(2, b.OccurrenceCount);
        _clock.UtcNow = _clock.UtcNow.AddSeconds(1);
        var third = await Raw(resource, occurredAt: _clock.UtcNow.AddDays(-7));
        var c = await Process(third);
        Assert.NotEqual(a.Id, c.Id);
        _clock.UtcNow = _clock.UtcNow.AddSeconds(1);
        var fourth = await Raw(resource);
        var d = await Process(fourth);
        Assert.Equal(c.Id, d.Id);
        Assert.Equal(2, d.OccurrenceCount);
        Assert.Equal(c.FirstOccurredAt, d.FirstOccurredAt);
        Assert.True(d.LastOccurredAt > c.LastOccurredAt);
        await using var verify = Open();
        Assert.Equal(4, await verify.Occurrences.CountAsync());
        Assert.Equal(4, await verify.Memberships.CountAsync());
        Assert.Equal(2, await verify.Deliveries.CountAsync());
    }

    [Theory]
    [InlineData("High")]
    [InlineData("Critical")]
    public async Task SevereManualIsImmediatelyDurableSentNotRead(string severity)
    {
        var result = await Manual(severity);
        Assert.Equal("Applied", result.Decision);
        var alarm = result.Value!;
        var delivery = Assert.Single((await Deliveries()).Value!.Rows);
        Assert.Equal(_clock.UtcNow, delivery.DueAt);
        Assert.Equal("Sent", delivery.State);
        Assert.Equal("InApp", delivery.Channel);
        Assert.Equal(1, delivery.AttemptCount);
        await using var verify = Open();
        Assert.Single(await verify.Attempts.ToListAsync());
        Assert.Equal("Active", alarm.State);
        Assert.Null(alarm.AcknowledgedAt);
        Assert.Equal("Forbidden", (await Silence(alarm, 900)).Decision);
    }

    [Fact]
    public async Task OrdinaryDeliveryIsPendingUntilDueThenRecoverableWithExactlyOneAttempt()
    {
        await Manual();
        var before = Assert.Single((await Deliveries()).Value!.Rows);
        Assert.Equal("Pending", before.State);
        Assert.Equal(_clock.UtcNow.AddSeconds(30), before.DueAt);
        _clock.UtcNow = _clock.UtcNow.AddSeconds(30);
        var after = Assert.Single((await Deliveries()).Value!.Rows);
        Assert.Equal("Sent", after.State);
        Assert.Equal(1, after.AttemptCount);
        Assert.Null(after.NextAttemptAt);
        await using var owner = Scope();
        var detail = await owner.ReadDeliveryAsync(Subject, after.Id);
        Assert.Equal("Success", detail.Decision);
        Assert.Single(detail.Value!.Attempts);
        Assert.Single((await Deliveries()).Value!.Rows);
        await using var verify = Open();
        Assert.Single(await verify.Attempts.ToListAsync());
    }

    [Fact]
    public async Task DuplicateAndConcurrentProjectionCannotLoseOccurrencesOrChangeCreator()
    {
        var resource = Guid.NewGuid();
        var ids = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Raw(resource)));
        await Task.WhenAll(ids.Select(id => Process(id)));
        var replay = await Process(ids[0]);
        Assert.Equal(8, replay.OccurrenceCount);
        await using (var owner = Scope())
            await Assert.ThrowsAsync<InvalidOperationException>(() => owner.ProcessOccurrenceAsync(ids[0], Guid.NewGuid()));
        await using var verify = Open();
        Assert.Single(await verify.Alarms.ToListAsync());
        Assert.Equal(8, await verify.Memberships.CountAsync());
    }

    [Fact]
    public async Task CreatorAndCaseSensitiveCodeAndSeverityAreDifferentAggregates()
    {
        var resource = Guid.NewGuid();
        var one = await Raw(resource, code: "Alarm.Code");
        var two = await Raw(resource, code: "Alarm.code");
        var three = await Raw(resource, severity: AlarmSeverity.High);
        var four = await Raw(resource);
        await Process(one);
        await Process(two);
        await Process(three);
        await using (var owner = Scope()) await owner.ProcessOccurrenceAsync(four, Guid.NewGuid());
        await using var verify = Open();
        Assert.Equal(4, await verify.Alarms.CountAsync());
    }

    [Fact]
    public async Task ManualIdentityPermanentBodyActorAndFreshReplayAuthorization()
    {
        var subject = Subject;
        var command = Command();
        AlarmResult<AlarmRow> first;
        await using (var owner = Scope()) first = await owner.CreateManualAsync(subject, command);
        await using (var owner = Scope()) Assert.Equal("Replayed", (await owner.CreateManualAsync(subject, command)).Decision);
        await using (var owner = Scope()) Assert.Equal("IdempotencyConflict",
            (await owner.CreateManualAsync(subject, command with { Summary = "Changed" })).Decision);
        _authority.Denied = true;
        await using (var owner = Scope()) Assert.Equal("Forbidden", (await owner.CreateManualAsync(subject, command)).Decision);
        Assert.NotNull(first.Value);
        await using var verify = Open();
        Assert.Single(await verify.Occurrences.ToListAsync());
        Assert.Single(await verify.Operations.ToListAsync());
        Assert.Single(await verify.Audits.ToListAsync());
    }

    [Theory]
    [InlineData("acknowledge")]
    [InlineData("manual.clear")]
    [InlineData("silence")]
    public async Task VersionedLifecycleHasAtomicAuditPermanentReceiptAndCurrentReplay(string kind)
    {
        var alarm = (await Manual()).Value!;
        var subject = Subject;
        var command = new AlarmActionCommand(Guid.NewGuid(), alarm.Version, "Reviewed operator action",
            kind == "silence" ? 900 : null);
        var result = await Action(subject, alarm.Id, command, kind);
        Assert.Equal("Applied", result.Decision);
        Assert.NotEqual(alarm.Version, result.Value!.Version);
        Assert.Equal(kind switch { "acknowledge" => "Acknowledged", "manual.clear" => "Cleared", _ => "Active" }, result.Value.State);
        Assert.Equal("Replayed", (await Action(subject, alarm.Id, command, kind)).Decision);
        Assert.Equal("IdempotencyConflict", (await Action(subject, alarm.Id, command with { Reason = "Different" }, kind)).Decision);
        _authority.Denied = true;
        Assert.Equal("Forbidden", (await Action(subject, alarm.Id, command, kind)).Decision);
        await using var verify = Open();
        Assert.Equal(2, await verify.Operations.CountAsync());
        var audit = Assert.Single(await verify.Audits.Where(item => item.Kind == kind).ToListAsync());
        Assert.Equal(subject.ActorId, audit.ActorId);
        Assert.Equal(command.Reason, audit.Reason);
        if (kind == "silence") Assert.Equal(_clock.UtcNow.AddSeconds(900), audit.ExpiresAt);
    }

    [Theory]
    [InlineData(null, "Applied")]
    [InlineData(900, "Applied")]
    [InlineData(3600, "Applied")]
    [InlineData(899, "Invalid")]
    [InlineData(3601, "Invalid")]
    public async Task OrdinarySilenceHasExplicitBoundsAndDelaysOnlyPendingNotification(int? seconds, string expected)
    {
        var alarm = (await Manual()).Value!;
        var result = await Silence(alarm, seconds);
        Assert.Equal(expected, result.Decision);
        if (expected != "Applied") return;
        _clock.UtcNow = _clock.UtcNow.AddSeconds(30);
        Assert.Equal("Pending", Assert.Single((await Deliveries()).Value!.Rows).State);
        _clock.UtcNow = result.Value!.SilenceUntil!.Value;
        Assert.Equal("Sent", Assert.Single((await Deliveries()).Value!.Rows).State);
    }

    [Fact]
    public async Task ManualClearCannotClearDeviceAndNewFactDoesNotReviveClearedManual()
    {
        var raw = await Raw(Guid.NewGuid());
        var device = await Process(raw);
        Assert.Equal("Forbidden", (await Action(Subject, device.Id,
            new(Guid.NewGuid(), device.Version, "Not physical recovery"), "manual.clear")).Decision);
        var command = Command();
        AlarmResult<AlarmRow> manual;
        await using (var owner = Scope()) manual = await owner.CreateManualAsync(Subject, command);
        var cleared = await Action(Subject, manual.Value!.Id, new(Guid.NewGuid(), manual.Value.Version, "Manual close"), "manual.clear");
        await using (var owner = Scope())
        {
            var newer = await owner.CreateManualAsync(Subject, command with { OperationId = Guid.NewGuid() });
            Assert.NotEqual(cleared.Value!.Id, newer.Value!.Id);
            Assert.Equal("Active", newer.Value.State);
        }
        await using var verify = Open();
        Assert.Equal("Cleared", (await verify.Alarms.SingleAsync(item => item.Id == manual.Value.Id)).State);
        Assert.Equal("Pending", (await verify.Deliveries.SingleAsync(item => item.AlarmId == manual.Value.Id)).State);
    }

    [Fact]
    public async Task StaleVersionAndCrossOperationKindConflictNeverWriteReceipts()
    {
        var alarm = (await Manual()).Value!;
        var command = new AlarmActionCommand(Guid.NewGuid(), 999, "Stale");
        Assert.Equal("VersionConflict", (await Action(Subject, alarm.Id, command, "acknowledge")).Decision);
        var applied = await Action(Subject, alarm.Id, command with { ExpectedVersion = alarm.Version }, "acknowledge");
        Assert.Equal("Applied", applied.Decision);
        Assert.Equal("IdempotencyConflict",
            (await Action(Subject, alarm.Id, command with { ExpectedVersion = alarm.Version }, "manual.clear")).Decision);
        await using var verify = Open();
        Assert.Equal(2, await verify.Operations.CountAsync());
    }

    [Theory]
    [InlineData("save")]
    [InlineData("commit")]
    [InlineData("cancel")]
    [InlineData("revoke")]
    public async Task SaveCommitCancellationAndFreshAuthorizationRollbackEveryManualRow(string fault)
    {
        using var cancellation = new CancellationTokenSource();
        IInterceptor interceptor = fault == "commit" ? new CommitFault() : new AfterSave(() =>
        {
            if (fault == "save") throw new ProviderFault();
            if (fault == "cancel") cancellation.Cancel();
            if (fault == "revoke") _authority.Denied = true;
        });
        await using (var owner = Scope(interceptor))
        {
            if (fault is "save" or "commit")
            {
                var error = await Assert.ThrowsAsync<AlarmConsoleDatabaseException>(() => owner.CreateManualAsync(Subject, Command()));
                Assert.Null(error.InnerException);
                Assert.DoesNotContain("provider-sensitive", error.ToString());
            }
            else if (fault == "cancel")
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => owner.CreateManualAsync(Subject, Command(), cancellation.Token));
            else Assert.Equal("Forbidden", (await owner.CreateManualAsync(Subject, Command())).Decision);
        }
        await using var verify = Open();
        Assert.Empty(await verify.Occurrences.ToListAsync());
        Assert.Empty(await verify.Alarms.ToListAsync());
        Assert.Empty(await verify.Memberships.ToListAsync());
        Assert.Empty(await verify.Deliveries.ToListAsync());
        Assert.Empty(await verify.Operations.ToListAsync());
        Assert.Empty(await verify.Audits.ToListAsync());
        Assert.Empty(await verify.Attempts.ToListAsync());
    }

    [Fact]
    public async Task RevokeAfterLifecycleSaveRollsBackStateVersionAuditAndReceipt()
    {
        var alarm = (await Manual()).Value!;
        await using (var owner = Scope(new AfterSave(() => _authority.Denied = true)))
            Assert.Equal("Forbidden", (await owner.AcknowledgeAsync(Subject, alarm.Id, new(Guid.NewGuid(), alarm.Version, "Review"))).Decision);
        await using var verify = Open();
        var stored = await verify.Alarms.SingleAsync();
        Assert.Equal("Active", stored.State);
        Assert.Equal(alarm.Version, stored.Version);
        Assert.Single(await verify.Audits.ToListAsync());
        Assert.Single(await verify.Operations.ToListAsync());
    }

    [Theory]
    [InlineData("save")]
    [InlineData("revoke")]
    public async Task IndependentDeliveryFailureRollsBackAttemptAndAllowsLaterRecovery(string fault)
    {
        await Manual();
        _clock.UtcNow = _clock.UtcNow.AddSeconds(30);
        await using (var owner = Scope(new AfterSave(() =>
        {
            if (fault == "save") throw new ProviderFault();
            _authority.Denied = true;
        })))
        {
            if (fault == "save") await Assert.ThrowsAsync<AlarmConsoleDatabaseException>(() => owner.QueryDeliveriesAsync(Subject));
            else Assert.Equal("Forbidden", (await owner.QueryDeliveriesAsync(Subject)).Decision);
        }
        await using (var verify = Open())
        {
            Assert.Equal("Pending", (await verify.Deliveries.SingleAsync()).State);
            Assert.Empty(await verify.Attempts.ToListAsync());
            Assert.Single(await verify.Occurrences.ToListAsync());
        }
        _authority.Denied = false;
        Assert.Equal("Sent", Assert.Single((await Deliveries()).Value!.Rows).State);
    }

    [Fact]
    public async Task RetryIsVersionedAuditedPermanentAndDoesNotDuplicateSent()
    {
        await Manual();
        var delivery = Assert.Single((await Deliveries()).Value!.Rows);
        var subject = Subject;
        var command = new AlarmActionCommand(Guid.NewGuid(), delivery.Version, "Explicit retry");
        await using (var owner = Scope()) Assert.Equal("Applied", (await owner.RetryDeliveryAsync(subject, delivery.Id, command)).Decision);
        await using (var owner = Scope()) Assert.Equal("Replayed", (await owner.RetryDeliveryAsync(subject, delivery.Id, command)).Decision);
        await using (var owner = Scope()) Assert.Equal("IdempotencyConflict",
            (await owner.RetryDeliveryAsync(subject, delivery.Id, command with { Reason = "Other" })).Decision);
        var sent = Assert.Single((await Deliveries()).Value!.Rows);
        await using (var owner = Scope()) Assert.Equal("Invalid",
            (await owner.RetryDeliveryAsync(subject, sent.Id, new(Guid.NewGuid(), sent.Version, "No duplicate"))).Decision);
        _authority.Denied = true;
        await using (var owner = Scope()) Assert.Equal("Forbidden", (await owner.RetryDeliveryAsync(subject, delivery.Id, command)).Decision);
        await using var verify = Open();
        Assert.Single(await verify.Attempts.ToListAsync());
        Assert.Equal(2, await verify.Audits.CountAsync());
    }

    [Fact]
    public async Task MissingReadFieldCreatorAndTenantFailClosedInsteadOfEmptySuccess()
    {
        var alarm = (await Manual()).Value!;
        _authority.MissingField = "Summary";
        await using (var owner = Scope()) Assert.Equal("Forbidden", (await owner.ReadAlarmAsync(Subject, alarm.Id)).Decision);
        _authority.MissingField = null;
        _authority.Creators.Clear();
        await using (var owner = Scope()) Assert.Equal("Forbidden", (await owner.ReadAlarmAsync(Subject, alarm.Id)).Decision);
        _authority.Creators.Add(_creator);
        await using var other = new AlarmConsoleScope(_connection, Guid.NewGuid(), _authority, _clock);
        var subject = Subject with { TenantId = Guid.NewGuid() };
        await Assert.ThrowsAsync<ArgumentException>(() => other.ReadAlarmAsync(subject, alarm.Id));
    }

    [Fact]
    public async Task ConcurrentManualAndLifecycleExactIdentitiesConverge()
    {
        var command = Command();
        var subject = Subject;
        async Task<AlarmResult<AlarmRow>> Create()
        {
            await using var owner = Scope();
            return await owner.CreateManualAsync(subject, command);
        }
        var results = await Task.WhenAll(Create(), Create());
        Assert.Single(results, item => item.Decision == "Applied");
        Assert.Single(results, item => item.Decision == "Replayed");
        var alarm = results[0].Value!;
        var action = new AlarmActionCommand(Guid.NewGuid(), alarm.Version, "Concurrent");
        var actions = await Task.WhenAll(Action(subject, alarm.Id, action, "acknowledge"), Action(subject, alarm.Id, action, "acknowledge"));
        Assert.Single(actions, item => item.Decision == "Applied");
        Assert.Single(actions, item => item.Decision == "Replayed");
    }

    [Fact]
    public async Task AtomicTrustedSourceIngressPreservesOriginalBodyAndMembershipUnderReplay()
    {
        var input = new AlarmOccurrenceInput(Guid.NewGuid(), "WcsFourWay", AlarmSourceKind.Device, Guid.NewGuid(),
            "Atomic.Source", AlarmSeverity.High, _clock.UtcNow.AddTicks(-1), Guid.NewGuid(), "Trusted immutable observation");
        async Task<AlarmResult<AlarmRow>> Receive(AlarmOccurrenceInput body, Guid? creator = null)
        {
            await using var owner = Scope();
            return await owner.RecordAndProcessAsync(body, creator ?? _creator);
        }
        var results = await Task.WhenAll(Receive(input), Receive(input));
        Assert.Single(results, item => item.Decision == "Applied");
        Assert.Single(results, item => item.Decision == "Replayed");
        Assert.Equal("IdempotencyConflict", (await Receive(input with { Summary = "Mismatch" })).Decision);
        Assert.Equal("IdempotencyConflict", (await Receive(input, Guid.NewGuid())).Decision);
        await using var verify = Open();
        Assert.Single(await verify.Occurrences.ToListAsync());
        Assert.Single(await verify.Memberships.ToListAsync());
        Assert.Single(await verify.Deliveries.ToListAsync());
        Assert.Single(await verify.Attempts.ToListAsync());
        Assert.Equal(input.OccurredAt, (await verify.Occurrences.SingleAsync()).OccurredAt);
    }

    [Theory]
    [InlineData("save")]
    [InlineData("commit")]
    public async Task AtomicTrustedIngressFailureNeverSplitsRawFactMembershipAndNotification(string fault)
    {
        var input = new AlarmOccurrenceInput(Guid.NewGuid(), "WcsStacker", AlarmSourceKind.Device, Guid.NewGuid(),
            "Atomic.Failure", AlarmSeverity.Critical, _clock.UtcNow, Guid.NewGuid(), "Atomic source fact");
        await using (var owner = Scope(fault == "commit" ? new CommitFault() : new AfterSave(() => throw new ProviderFault())))
            await Assert.ThrowsAsync<AlarmConsoleDatabaseException>(() => owner.RecordAndProcessAsync(input, _creator));
        await using var verify = Open();
        Assert.Empty(await verify.Occurrences.ToListAsync());
        Assert.Empty(await verify.Memberships.ToListAsync());
        Assert.Empty(await verify.Alarms.ToListAsync());
        Assert.Empty(await verify.Deliveries.ToListAsync());
        Assert.Empty(await verify.Attempts.ToListAsync());
    }

    [Fact]
    public async Task ReadDetailHasBoundedLatestFactsButAggregateNeverLosesAnyMembership()
    {
        var resource = Guid.NewGuid();
        for (var i = 0; i < 101; i++)
        {
            await using var owner = Scope();
            Assert.Equal("Applied", (await owner.RecordAndProcessAsync(new(Guid.NewGuid(), "WcsStacker", AlarmSourceKind.Device,
                resource, "Bounded.Facts", AlarmSeverity.Warning, _clock.UtcNow, Guid.NewGuid(), $"Fact {i}"), _creator)).Decision);
        }
        await using var read = Scope();
        var page = await read.QueryAlarmsAsync(Subject);
        var row = Assert.Single(page.Value!.Rows);
        await using var detailOwner = Scope();
        var detail = await detailOwner.ReadAlarmAsync(Subject, row.Id);
        Assert.Equal(101, detail.Value!.OccurrenceCount);
        Assert.Equal(100, detail.Value.Occurrences.Count);
        await using var verify = Open();
        Assert.Equal(101, await verify.Occurrences.CountAsync());
        Assert.Equal(101, await verify.Memberships.CountAsync());
    }

    [Fact]
    public async Task RetryFreshPrecommitAuthorityRollbackKeepsOriginalPendingVersionAndNoAttempt()
    {
        await Manual();
        var delivery = Assert.Single((await Deliveries()).Value!.Rows);
        await using (var owner = Scope(new AfterSave(() => _authority.Denied = true)))
            Assert.Equal("Forbidden", (await owner.RetryDeliveryAsync(Subject, delivery.Id, new(Guid.NewGuid(), delivery.Version, "Fresh authority"))).Decision);
        await using var verify = Open();
        var stored = await verify.Deliveries.SingleAsync();
        Assert.Equal("Pending", stored.State);
        Assert.Equal(delivery.Version, stored.Version);
        Assert.Empty(await verify.Attempts.ToListAsync());
        Assert.Single(await verify.Operations.ToListAsync());
    }

    [Theory]
    [InlineData(-1L)]
    [InlineData(0L)]
    [InlineData(9007199254740992L)]
    public async Task VersionsArePositiveJavascriptSafeCounters(long invalid)
    {
        var alarm = (await Manual()).Value!;
        Assert.Equal("Invalid", (await Action(Subject, alarm.Id, new(Guid.NewGuid(), invalid, "Unsafe counter"), "acknowledge")).Decision);
    }

    [Fact]
    public async Task MaximumUnicodeSummaryAndReasonPreservePermanentEscapedBodyWithoutProviderTruncation()
    {
        var command = Command() with { Summary = new string('测', 512) };
        var subject = Subject;
        AlarmRow alarm;
        await using (var owner = Scope()) alarm = (await owner.CreateManualAsync(subject, command)).Value!;
        var action = new AlarmActionCommand(Guid.NewGuid(), alarm.Version, new string('理', 512));
        Assert.Equal("Applied", (await Action(subject, alarm.Id, action, "acknowledge")).Decision);
        Assert.Equal("Replayed", (await Action(subject, alarm.Id, action, "acknowledge")).Decision);
        await using var verify = Open();
        Assert.Equal(2, await verify.Operations.CountAsync());
        Assert.All(await verify.Operations.ToListAsync(), receipt => Assert.True(receipt.Body.Length > 2048));
    }

    [Theory]
    [InlineData("create")]
    [InlineData("acknowledge")]
    [InlineData("silence")]
    [InlineData("retry")]
    public async Task RealPlatformRevocationAfterNotificationsSaveRollsBackAndDeniesReplay(string kind)
    {
        await using (var platform = OpenPlatform())
        {
            await platform.Database.MigrateAsync();
            _platformReady = true;
            DirectoryTestFixture.Add(platform, _creator);
            var allFields = AlarmConsoleFields.AlarmRead.Concat(AlarmConsoleFields.DeliveryRead)
                .Concat(AlarmConsoleFields.ManualCreate).Concat(new[] { "ExpectedVersion", "Reason", "Seconds" }).ToImmutableHashSet();
            var permissions = new[] { "notifications.alarms.read", "notifications.alarms.manual.create",
                "notifications.alarms.acknowledge", "notifications.alarms.silence", "notifications.deliveries.read",
                "notifications.deliveries.retry" };
            var role = new AccessRole(_tenant, "alarm-real-authority", permissions.Select(permission =>
                new PermissionGrant(permission, DataScope.Own, ImmutableHashSet<Guid>.Empty, allFields)));
            var session = new Session(_tenant, _creator, SessionClient.Web, _clock.UtcNow, new string('A', 64));
            platform.Roles.Add(role); platform.Sessions.Add(session); platform.Memberships.Add(new(_tenant, _creator, role.Id));
            await platform.SaveChangesAsync();
        }
        AlarmSubject subject;
        await using (var platform = OpenPlatform()) subject = new(_tenant, _creator, (await platform.Sessions.SingleAsync()).Id);
        var authority = new LivePlatformAuthority(_connection, _clock);
        var manual = Command();
        AlarmRow? alarm = null;
        AlarmDeliveryRow? delivery = null;
        if (kind != "create")
        {
            await using (var owner = new AlarmConsoleScope(Open(), authority, _clock))
                alarm = (await owner.CreateManualAsync(subject, manual)).Value!;
            await using (var owner = new AlarmConsoleScope(Open(), authority, _clock))
                delivery = Assert.Single((await owner.QueryDeliveriesAsync(subject)).Value!.Rows);
        }
        var operation = Guid.NewGuid();
        var command = new AlarmActionCommand(operation, kind == "retry" ? delivery!.Version : alarm?.Version ?? 1,
            "Live owner revoke after save", kind == "silence" ? 900 : null);
        async Task Revoke()
        {
            await using var platform = OpenPlatform();
            var role = await platform.Roles.SingleAsync();
            role.Change(role.Version, enabled: false, role.Rules);
            await platform.SaveChangesAsync();
        }
        async Task<string> Execute(AlarmConsoleScope owner) => kind switch
        {
            "create" => (await owner.CreateManualAsync(subject, manual)).Decision,
            "acknowledge" => (await owner.AcknowledgeAsync(subject, alarm!.Id, command)).Decision,
            "silence" => (await owner.SilenceAsync(subject, alarm!.Id, command)).Decision,
            "retry" => (await owner.RetryDeliveryAsync(subject, delivery!.Id, command)).Decision,
            _ => throw new InvalidOperationException()
        };
        await using (var owner = new AlarmConsoleScope(Open(new AsyncAfterSave(Revoke)), authority, _clock))
            Assert.Equal("Forbidden", await Execute(owner));
        await using (var replay = new AlarmConsoleScope(Open(), authority, _clock))
            Assert.Equal("Forbidden", await Execute(replay));
        await using var verify = Open();
        Assert.Equal(kind == "create" ? 0 : 1, await verify.Operations.CountAsync());
        Assert.Equal(kind == "create" ? 0 : 1, await verify.Audits.CountAsync());
        Assert.Empty(await verify.Attempts.ToListAsync());
        if (alarm is not null)
        {
            var original = await verify.Alarms.SingleAsync();
            Assert.Equal(alarm.Version, original.Version);
            Assert.Equal("Active", original.State);
            Assert.Null(original.SilenceUntil);
        }
    }

    private PlatformDbContext OpenPlatform() => new(new DbContextOptionsBuilder<PlatformDbContext>().UseMySQL(_connection,
        mysql => mysql.MigrationsHistoryTable("__WesPlatformMigrations")).Options, _tenant);
    private sealed class LivePlatformAuthority(string connection, TimeProvider clock) : IAlarmAuthorization
    {
        public async Task<AlarmCreatorAccess> ReadCreatorsAsync(AlarmSubject subject, string permission,
            IReadOnlyCollection<string> fields, CancellationToken cancellationToken)
        {
            await using var platform = new PlatformScope(connection, subject.TenantId, clock, NullLoggerFactory.Instance);
            HashSet<Guid>? creators = null;
            foreach (var chunk in fields.Chunk(16))
            {
                var access = await platform.VisibleOwnedResourceCreatorsAsync(new(subject.TenantId, subject.ActorId, subject.SessionId),
                    permission, chunk, cancellationToken);
                if (!access.Authorized) return new(false, new HashSet<Guid>());
                if (creators is null) creators = access.CreatorIds.ToHashSet();
                else creators.IntersectWith(access.CreatorIds);
            }
            return new(creators is not null, creators ?? new HashSet<Guid>());
        }
        public async Task<IReadOnlySet<string>> ReadFieldsAsync(AlarmSubject subject, string permission, Guid creatorId,
            CancellationToken cancellationToken)
        {
            await using var platform = new PlatformScope(connection, subject.TenantId, clock, NullLoggerFactory.Instance);
            return await platform.VisibleOwnedResourceFieldsAsync(new(subject.TenantId, subject.ActorId, subject.SessionId),
                permission, subject.TenantId, creatorId, cancellationToken);
        }
    }

    private NotificationsDbContext Open(params IInterceptor[] interceptors) => new(
        new DbContextOptionsBuilder<NotificationsDbContext>().UseMySQL(_connection,
            options => options.MigrationsHistoryTable("__WesNotificationsMigrations")).AddInterceptors(interceptors).Options, _tenant);
    private AlarmConsoleScope Scope(params IInterceptor[] interceptors) => new(Open(interceptors), _authority, _clock);
    private ManualAlarmCommand Command(string severity = "Warning") => new(Guid.NewGuid(), Guid.NewGuid(), "Manual.Test", severity, "Manual test");
    private async Task<AlarmResult<AlarmRow>> Manual(string severity = "Warning")
    {
        await using var owner = Scope();
        return await owner.CreateManualAsync(Subject, Command(severity));
    }
    private async Task<Guid> Raw(Guid resource, string code = "Alarm.Code", AlarmSeverity severity = AlarmSeverity.Warning,
        DateTimeOffset? occurredAt = null)
    {
        await using var database = Open();
        var result = await new AlarmOccurrenceStore(database, _clock).RecordAsync(new(Guid.NewGuid(), "WcsStacker",
            AlarmSourceKind.Device, resource, code, severity, occurredAt ?? _clock.UtcNow, Guid.NewGuid(), "Trusted fact"));
        Assert.Equal(OccurrenceDecision.Recorded, result.Decision);
        return result.Value!.Id;
    }
    private async Task<AlarmRow> Process(Guid id)
    {
        await using var owner = Scope();
        return await owner.ProcessOccurrenceAsync(id, _creator);
    }
    private async Task<AlarmResult<AlarmPage<AlarmDeliveryRow>>> Deliveries()
    {
        await using var owner = Scope();
        return await owner.QueryDeliveriesAsync(Subject);
    }
    private Task<AlarmResult<AlarmRow>> Silence(AlarmRow alarm, int? seconds) =>
        Action(Subject, alarm.Id, new(Guid.NewGuid(), alarm.Version, "Quiet ordinary", seconds), "silence");
    private async Task<AlarmResult<AlarmRow>> Action(AlarmSubject subject, Guid id, AlarmActionCommand command, string kind)
    {
        await using var owner = Scope();
        return kind switch
        {
            "acknowledge" => await owner.AcknowledgeAsync(subject, id, command),
            "manual.clear" => await owner.ClearManualAsync(subject, id, command),
            "silence" => await owner.SilenceAsync(subject, id, command),
            _ => throw new InvalidOperationException()
        };
    }
    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        internal DateTimeOffset UtcNow { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => UtcNow;
    }
    private sealed class Authority(Guid creator) : IAlarmAuthorization
    {
        internal bool Denied { get; set; }
        internal string? MissingField { get; set; }
        internal HashSet<Guid> Creators { get; } = [creator];
        public Task<AlarmCreatorAccess> ReadCreatorsAsync(AlarmSubject subject, string permission,
            IReadOnlyCollection<string> fields, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new AlarmCreatorAccess(!Denied && !fields.Contains(MissingField), Creators));
        }
        public Task<IReadOnlySet<string>> ReadFieldsAsync(AlarmSubject subject, string permission, Guid creatorId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var fields = AlarmConsoleFields.AlarmRead.Concat(AlarmConsoleFields.DeliveryRead)
                .Concat(AlarmConsoleFields.ManualCreate).Concat(new[] { "ExpectedVersion", "Reason", "Seconds" })
                .Where(field => field != MissingField).ToHashSet();
            return Task.FromResult<IReadOnlySet<string>>(Denied || !Creators.Contains(creatorId) ? new HashSet<string>() : fields);
        }
    }
    private sealed class ProviderFault() : DbException("provider-sensitive");
    private sealed class AfterSave(Action action) : SaveChangesInterceptor
    {
        public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
        {
            action();
            return ValueTask.FromResult(result);
        }
    }
    private sealed class AsyncAfterSave(Func<Task> action) : SaveChangesInterceptor
    {
        public override async ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
        {
            await action();
            return result;
        }
    }
    private sealed class CommitFault : DbTransactionInterceptor
    {
        public override ValueTask<InterceptionResult> TransactionCommittingAsync(DbTransaction transaction,
            TransactionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default) =>
            throw new ProviderFault();
    }
    public async Task DisposeAsync()
    {
        if (_connection.Length == 0) return;
        await using var database = Open();
        foreach (var sql in new[]
        {
            "DELETE FROM ntf_alarm_delivery_attempts WHERE TenantId = {0}",
            "DELETE FROM ntf_alarm_memberships WHERE TenantId = {0}",
            "DELETE FROM ntf_alarm_deliveries WHERE TenantId = {0}",
            "DELETE FROM ntf_alarm_audits WHERE TenantId = {0}",
            "DELETE FROM ntf_alarm_operations WHERE TenantId = {0}",
            "DELETE FROM ntf_alarms WHERE TenantId = {0}",
            "DELETE FROM ntf_alarm_occurrences WHERE TenantId = {0}",
            "DELETE FROM ntf_alarm_console_locks WHERE TenantId = {0}"
        })
            await database.Database.ExecuteSqlRawAsync(sql, _tenant.ToString("D"));
        if (_platformReady)
        {
            await using var platform = OpenPlatform();
            platform.Memberships.RemoveRange(await platform.Memberships.ToListAsync());
            platform.Sessions.RemoveRange(await platform.Sessions.ToListAsync());
            platform.Roles.RemoveRange(await platform.Roles.ToListAsync());
            await DirectoryTestFixture.RemoveAsync(platform);
            await platform.SaveChangesAsync();
        }
    }
}
