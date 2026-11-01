using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Wes.Notifications.Features.Alarms;
using Wes.Notifications.Infrastructure.Persistence;

namespace Wes.Notifications;

/// <summary>Safe explicit owner provider failure, with no connection details or provider inner exception.</summary>
public sealed class AlarmConsoleDatabaseException() : Exception("Notifications alarm operation failed.");

/// <summary>Public Notifications application boundary. Owns independent atomic projection, authorization and delivery.</summary>
public sealed class AlarmConsoleScope : IAsyncDisposable
{
    private readonly NotificationsDbContext _database;
    private readonly IAlarmAuthorization _authorization;
    private readonly TimeProvider _clock;
    private static readonly Regex CodePattern = new("\\A[A-Za-z][A-Za-z0-9_.-]*\\z",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    /// <summary>Creates one clean owner scope from deployment-only database configuration.</summary>
    public AlarmConsoleScope(string connection, Guid tenant, IAlarmAuthorization authorization, TimeProvider clock)
        : this(new NotificationsDbContext(new DbContextOptionsBuilder<NotificationsDbContext>().UseMySQL(connection,
            mysql => mysql.MigrationsHistoryTable("__WesNotificationsMigrations")).Options, tenant), authorization, clock)
    { }

    internal AlarmConsoleScope(NotificationsDbContext database, IAlarmAuthorization authorization, TimeProvider clock)
    {
        _database = database;
        _authorization = authorization;
        _clock = clock;
    }

    /// <summary>Creates a manual occurrence, membership, alarm, delivery, receipt and audit in one owner transaction.</summary>
    public Task<AlarmResult<AlarmRow>> CreateManualAsync(AlarmSubject subject, ManualAlarmCommand command,
        CancellationToken cancellationToken = default) => SafeAsync<AlarmRow>(async () =>
    {
        RequireClean(subject);
        if (command.OperationId == Guid.Empty || command.ResourceId == Guid.Empty ||
            command.Code is null || command.Code.Length is < 1 or > 64 || !CodePattern.IsMatch(command.Code) ||
            !TrySeverity(command.Severity, out var severity) ||
            string.IsNullOrWhiteSpace(command.Summary) || command.Summary.Length > 512)
            return new("Invalid");
        if (!await Allowed(subject, "notifications.alarms.manual.create", subject.ActorId, AlarmConsoleFields.ManualCreate, cancellationToken) ||
            !await Allowed(subject, "notifications.alarms.read", subject.ActorId, AlarmConsoleFields.AlarmRead, cancellationToken))
            return new("Forbidden");
        await using var transaction = await _database.Database.BeginTransactionAsync(cancellationToken);
        await LockAsync(cancellationToken);
        var receipt = await _database.Operations.SingleOrDefaultAsync(item => item.OperationId == command.OperationId, cancellationToken);
        var body = JsonSerializer.Serialize(command);
        if (receipt is not null)
        {
            if (!Matches(receipt, subject, "manual.create", body)) return new("IdempotencyConflict");
            var original = await _database.Alarms.SingleAsync(item => item.Id == receipt.TargetId, cancellationToken);
            if (!await Allowed(subject, "notifications.alarms.manual.create", original.CreatorId, AlarmConsoleFields.ManualCreate, cancellationToken) ||
                !await Allowed(subject, "notifications.alarms.read", original.CreatorId, AlarmConsoleFields.AlarmRead, cancellationToken))
                return new("Forbidden");
            await transaction.CommitAsync(cancellationToken);
            return new("Replayed", Project(original));
        }
        var now = Now;
        var input = new AlarmOccurrenceInput(command.OperationId, "Notifications", AlarmSourceKind.Manual, command.ResourceId,
            command.Code, severity, now, command.OperationId, command.Summary);
        var fact = AlarmOccurrenceStore.CreateFact(input, _database.TenantId, now, AlarmOccurrenceStore.Fingerprint(input));
        _database.Occurrences.Add(fact);
        var alarm = await AttachAsync(fact, subject.ActorId, cancellationToken);
        Receipt(subject, command.OperationId, alarm.Id, alarm.CreatorId, "manual.create", body, command.Summary);
        await _database.SaveChangesAsync(cancellationToken);
        if (!await Allowed(subject, "notifications.alarms.manual.create", alarm.CreatorId, AlarmConsoleFields.ManualCreate, cancellationToken) ||
            !await Allowed(subject, "notifications.alarms.read", alarm.CreatorId, AlarmConsoleFields.AlarmRead, cancellationToken))
            return new("Forbidden");
        await transaction.CommitAsync(cancellationToken);
        return new("Applied", Project(alarm));
    }, cancellationToken);

    /// <summary>Acknowledges only the operational alarm, never source equipment or inventory.</summary>
    public Task<AlarmResult<AlarmRow>> AcknowledgeAsync(AlarmSubject subject, Guid id, AlarmActionCommand command,
        CancellationToken cancellationToken = default) => ChangeAlarmAsync(subject, id, command, "acknowledge", cancellationToken);
    /// <summary>Clears Manual alarms only; device clear is unavailable at this boundary.</summary>
    public Task<AlarmResult<AlarmRow>> ClearManualAsync(AlarmSubject subject, Guid id, AlarmActionCommand command,
        CancellationToken cancellationToken = default) => ChangeAlarmAsync(subject, id, command, "manual.clear", cancellationToken);
    /// <summary>Silences ordinary alarms for 900..3600 seconds, with durable actor/reason/expiry audit.</summary>
    public Task<AlarmResult<AlarmRow>> SilenceAsync(AlarmSubject subject, Guid id, AlarmActionCommand command,
        CancellationToken cancellationToken = default) => ChangeAlarmAsync(subject, id, command, "silence", cancellationToken);

    private Task<AlarmResult<AlarmRow>> ChangeAlarmAsync(AlarmSubject subject, Guid id, AlarmActionCommand command,
        string kind, CancellationToken ct) => SafeAsync<AlarmRow>(async () =>
    {
        RequireClean(subject);
        if (id == Guid.Empty || !ValidAction(command) ||
            (kind == "silence" ? (command.Seconds ?? 900) is < 900 or > 3600 : command.Seconds is not null))
            return new("Invalid");
        var permission = $"notifications.alarms.{kind}";
        var fields = ActionFields(kind == "silence");
        var access = await _authorization.ReadCreatorsAsync(subject, permission, fields, ct);
        if (!access.Authorized) return new("Forbidden");
        await using var transaction = await _database.Database.BeginTransactionAsync(ct);
        await LockAsync(ct);
        var alarm = await _database.Alarms.SingleOrDefaultAsync(item => item.Id == id, ct);
        if (alarm is null) return new("NotFound");
        if (!access.CreatorIds.Contains(alarm.CreatorId) || !await AlarmActionAllowed(subject, permission, alarm.CreatorId, fields, ct))
            return new("Forbidden");
        var body = JsonSerializer.Serialize(new { Id = id, Command = command });
        var receipt = await _database.Operations.SingleOrDefaultAsync(item => item.OperationId == command.OperationId, ct);
        if (receipt is not null)
        {
            if (!Matches(receipt, subject, kind, body)) return new("IdempotencyConflict");
            if (!await AlarmActionAllowed(subject, permission, alarm.CreatorId, fields, ct)) return new("Forbidden");
            await transaction.CommitAsync(ct);
            return new("Replayed", Project(alarm));
        }
        if (alarm.Version != command.ExpectedVersion) return new("VersionConflict");
        if (kind == "manual.clear" && alarm.SourceKind != AlarmSourceKind.Manual ||
            kind == "silence" && alarm.Severity is AlarmSeverity.High or AlarmSeverity.Critical)
            return new("Forbidden");
        if (alarm.State == "Cleared" || kind == "acknowledge" && alarm.State == "Acknowledged")
            return new("Invalid");
        var delivery = await _database.Deliveries.SingleAsync(item => item.AlarmId == alarm.Id, ct);
        switch (kind)
        {
            case "acknowledge":
                alarm.State = "Acknowledged"; alarm.AcknowledgedBy = subject.ActorId; alarm.AcknowledgedAt = Now;
                break;
            case "manual.clear":
                alarm.State = "Cleared";
                break;
            case "silence":
                alarm.SilenceUntil = Now.AddSeconds(command.Seconds ?? 900);
                if (delivery.State == "Pending")
                {
                    delivery.DueAt = alarm.SilenceUntil.Value; delivery.NextAttemptAt = delivery.DueAt; delivery.Version = Version(delivery.Version);
                }
                break;
        }
        alarm.Version = Version(alarm.Version);
        Receipt(subject, command.OperationId, id, alarm.CreatorId, kind, body, command.Reason, alarm.SilenceUntil);
        await _database.SaveChangesAsync(ct);
        if (!await AlarmActionAllowed(subject, permission, alarm.CreatorId, fields, ct)) return new("Forbidden");
        await transaction.CommitAsync(ct);
        return new("Applied", Project(alarm));
    }, ct);

    /// <summary>Freshly authorized alarm page; failed authorization or provider queries are never empty success.</summary>
    public Task<AlarmResult<AlarmPage<AlarmRow>>> QueryAlarmsAsync(AlarmSubject subject, int offset = 0, int limit = 50,
        CancellationToken cancellationToken = default) => SafeAsync<AlarmPage<AlarmRow>>(async () =>
    {
        RequireClean(subject);
        if (!ValidPage(offset, limit)) return new("Invalid");
        var access = await _authorization.ReadCreatorsAsync(subject, "notifications.alarms.read", AlarmConsoleFields.AlarmRead, cancellationToken);
        if (!access.Authorized) return new("Forbidden");
        var creators = access.CreatorIds.ToArray();
        var rows = await _database.Alarms.AsNoTracking().Where(item => creators.Contains(item.CreatorId))
            .OrderByDescending(item => item.LastOccurredAt).ThenBy(item => item.Id).Skip(offset).Take(limit).ToListAsync(cancellationToken);
        foreach (var row in rows)
            if (!await Allowed(subject, "notifications.alarms.read", row.CreatorId, AlarmConsoleFields.AlarmRead, cancellationToken))
                return new("Forbidden");
        if (!(await _authorization.ReadCreatorsAsync(subject, "notifications.alarms.read", AlarmConsoleFields.AlarmRead, cancellationToken)).Authorized)
            return new("Forbidden");
        return new("Success", new(rows.Select(Project).ToArray(), offset, limit));
    }, cancellationToken);

    /// <summary>Reads only a current-authorized complete detail and its immutable memberships.</summary>
    public Task<AlarmResult<AlarmDetail>> ReadAlarmAsync(AlarmSubject subject, Guid id,
        CancellationToken cancellationToken = default) => SafeAsync<AlarmDetail>(async () =>
    {
        RequireClean(subject);
        var access = await _authorization.ReadCreatorsAsync(subject, "notifications.alarms.read", AlarmConsoleFields.AlarmRead, cancellationToken);
        if (!access.Authorized) return new("Forbidden");
        var alarm = await _database.Alarms.AsNoTracking().SingleOrDefaultAsync(item => item.Id == id, cancellationToken);
        if (alarm is null) return new("NotFound");
        if (!access.CreatorIds.Contains(alarm.CreatorId) ||
            !await Allowed(subject, "notifications.alarms.read", alarm.CreatorId, AlarmConsoleFields.AlarmRead, cancellationToken))
            return new("Forbidden");
        var facts = await (from membership in _database.Memberships.AsNoTracking()
                           join fact in _database.Occurrences.AsNoTracking() on membership.OccurrenceId equals fact.Id
                           where membership.AlarmId == id
                           orderby fact.ReceivedAt descending, fact.Id
                           select new AlarmOccurrenceRow(fact.Id, fact.SourceEventId, fact.OccurredAt, fact.ReceivedAt,
                               fact.CorrelationId, fact.Summary)).Take(100).ToListAsync(cancellationToken);
        if (!await Allowed(subject, "notifications.alarms.read", alarm.CreatorId, AlarmConsoleFields.AlarmRead, cancellationToken))
            return new("Forbidden");
        var row = Project(alarm);
        return new("Success", new(row.Id, row.SourceOwner, row.SourceKind, row.ResourceId, row.Code, row.Severity,
            row.FirstOccurredAt, row.LastOccurredAt, row.OccurrenceCount, row.State, row.Version, row.AcknowledgedBy,
            row.AcknowledgedAt, row.SilenceUntil, facts));
    }, cancellationToken);

    /// <summary>Bounded lazy due processing makes durable in-app delivery functional without an external transport or worker.</summary>
    public Task<AlarmResult<AlarmPage<AlarmDeliveryRow>>> QueryDeliveriesAsync(AlarmSubject subject, int offset = 0, int limit = 50,
        CancellationToken cancellationToken = default) => SafeAsync<AlarmPage<AlarmDeliveryRow>>(async () =>
    {
        RequireClean(subject);
        if (!ValidPage(offset, limit)) return new("Invalid");
        var access = await _authorization.ReadCreatorsAsync(subject, "notifications.deliveries.read", AlarmConsoleFields.DeliveryRead, cancellationToken);
        if (!access.Authorized) return new("Forbidden");
        if (!await CompleteDueAsync(subject, access.CreatorIds, cancellationToken)) return new("Forbidden");
        var creators = access.CreatorIds.ToArray();
        var rows = await (from delivery in _database.Deliveries.AsNoTracking()
                          join alarm in _database.Alarms.AsNoTracking() on delivery.AlarmId equals alarm.Id
                          where creators.Contains(alarm.CreatorId)
                          orderby delivery.DueAt descending, delivery.Id
                          select new { Delivery = delivery, alarm.CreatorId }).Skip(offset).Take(limit).ToListAsync(cancellationToken);
        foreach (var row in rows)
            if (!await DeliveryAllowed(subject, "notifications.deliveries.read", row.CreatorId, AlarmConsoleFields.DeliveryRead, cancellationToken))
                return new("Forbidden");
        if (!(await _authorization.ReadCreatorsAsync(subject, "notifications.deliveries.read", AlarmConsoleFields.DeliveryRead, cancellationToken)).Authorized)
            return new("Forbidden");
        return new("Success", new(rows.Select(item => Project(item.Delivery)).ToArray(), offset, limit));
    }, cancellationToken);

    /// <summary>Recovers persisted in-app delivery and immutable attempts, without marking anything read.</summary>
    public Task<AlarmResult<AlarmDeliveryDetail>> ReadDeliveryAsync(AlarmSubject subject, Guid id,
        CancellationToken cancellationToken = default) => SafeAsync<AlarmDeliveryDetail>(async () =>
    {
        RequireClean(subject);
        var access = await _authorization.ReadCreatorsAsync(subject, "notifications.deliveries.read", AlarmConsoleFields.DeliveryRead, cancellationToken);
        if (!access.Authorized) return new("Forbidden");
        if (!await CompleteDueAsync(subject, access.CreatorIds, cancellationToken)) return new("Forbidden");
        var delivery = await _database.Deliveries.AsNoTracking().SingleOrDefaultAsync(item => item.Id == id, cancellationToken);
        if (delivery is null) return new("NotFound");
        var alarm = await _database.Alarms.AsNoTracking().SingleAsync(item => item.Id == delivery.AlarmId, cancellationToken);
        if (!access.CreatorIds.Contains(alarm.CreatorId) ||
            !await DeliveryAllowed(subject, "notifications.deliveries.read", alarm.CreatorId, AlarmConsoleFields.DeliveryRead, cancellationToken))
            return new("Forbidden");
        var attempts = await _database.Attempts.AsNoTracking().Where(item => item.DeliveryId == id)
            .OrderBy(item => item.AttemptedAt).ThenBy(item => item.Id)
            .Select(item => new AlarmDeliveryAttemptRow(item.Id, item.AttemptedAt, item.State, item.ErrorCode)).Take(100).ToListAsync(cancellationToken);
        if (!await DeliveryAllowed(subject, "notifications.deliveries.read", alarm.CreatorId, AlarmConsoleFields.DeliveryRead, cancellationToken))
            return new("Forbidden");
        var row = Project(delivery);
        return new("Success", new(row.Id, row.AlarmId, row.Channel, row.State, row.OccurrenceCount, row.DueAt,
            row.AttemptCount, row.NextAttemptAt, row.ErrorCode, row.Version, attempts));
    }, cancellationToken);

    /// <summary>Retries a pending or failed durable in-app notification; does not duplicate Sent notifications or claim email success.</summary>
    public Task<AlarmResult<AlarmDeliveryRow>> RetryDeliveryAsync(AlarmSubject subject, Guid id, AlarmActionCommand command,
        CancellationToken cancellationToken = default) => SafeAsync<AlarmDeliveryRow>(async () =>
    {
        RequireClean(subject);
        if (id == Guid.Empty || !ValidAction(command) || command.Seconds is not null) return new("Invalid");
        var fields = AlarmConsoleFields.DeliveryRead.Concat(new[] { "OperationId", "ExpectedVersion", "Reason" }).ToArray();
        var access = await _authorization.ReadCreatorsAsync(subject, "notifications.deliveries.retry", fields, cancellationToken);
        if (!access.Authorized) return new("Forbidden");
        await using var transaction = await _database.Database.BeginTransactionAsync(cancellationToken);
        await LockAsync(cancellationToken);
        var delivery = await _database.Deliveries.SingleOrDefaultAsync(item => item.Id == id, cancellationToken);
        if (delivery is null) return new("NotFound");
        var alarm = await _database.Alarms.SingleAsync(item => item.Id == delivery.AlarmId, cancellationToken);
        if (!access.CreatorIds.Contains(alarm.CreatorId) ||
            !await DeliveryAllowed(subject, "notifications.deliveries.retry", alarm.CreatorId, fields, cancellationToken))
            return new("Forbidden");
        var body = JsonSerializer.Serialize(new { Id = id, Command = command });
        var receipt = await _database.Operations.SingleOrDefaultAsync(item => item.OperationId == command.OperationId, cancellationToken);
        if (receipt is not null)
        {
            if (!Matches(receipt, subject, "delivery.retry", body)) return new("IdempotencyConflict");
            if (!await DeliveryAllowed(subject, "notifications.deliveries.retry", alarm.CreatorId, fields, cancellationToken))
                return new("Forbidden");
            await transaction.CommitAsync(cancellationToken);
            return new("Replayed", Project(delivery));
        }
        if (delivery.Version != command.ExpectedVersion) return new("VersionConflict");
        if (delivery.State is not ("Pending" or "Failed" or "Unavailable") || alarm.State == "Cleared" || alarm.SilenceUntil > Now)
            return new("Invalid");
        if (delivery.Channel != "InApp") return new("Unavailable");
        delivery.DueAt = Now;
        Deliver(delivery);
        Receipt(subject, command.OperationId, id, alarm.CreatorId, "delivery.retry", body, command.Reason);
        await _database.SaveChangesAsync(cancellationToken);
        if (!await DeliveryAllowed(subject, "notifications.deliveries.retry", alarm.CreatorId, fields, cancellationToken))
            return new("Forbidden");
        await transaction.CommitAsync(cancellationToken);
        return new("Applied", Project(delivery));
    }, cancellationToken);

    /// <summary>Atomic trusted-source path, not a public device authentication or equipment recovery adapter.</summary>
    internal Task<AlarmResult<AlarmRow>> RecordAndProcessAsync(AlarmOccurrenceInput input, Guid trustedCreator,
        CancellationToken cancellationToken = default) => SafeAsync<AlarmRow>(async () =>
    {
        RequireClean();
        if (trustedCreator == Guid.Empty || AlarmOccurrenceStore.Validate(input, Now) is not null) return new("Invalid");
        await using var transaction = await _database.Database.BeginTransactionAsync(cancellationToken);
        await LockAsync(cancellationToken);
        var fingerprint = AlarmOccurrenceStore.Fingerprint(input);
        var fact = await _database.Occurrences.AsNoTracking().SingleOrDefaultAsync(
            item => item.SourceOwner == input.SourceOwner && item.SourceEventId == input.SourceEventId, cancellationToken);
        var replayed = fact is not null;
        if (fact is not null && fact.Fingerprint != fingerprint) return new("IdempotencyConflict");
        fact ??= AlarmOccurrenceStore.CreateFact(input, _database.TenantId, Now, fingerprint);
        var member = await _database.Memberships.SingleOrDefaultAsync(item => item.OccurrenceId == fact.Id, cancellationToken);
        AlarmEntity alarm;
        if (member is not null)
        {
            alarm = await _database.Alarms.SingleAsync(item => item.Id == member.AlarmId, cancellationToken);
            if (alarm.CreatorId != trustedCreator) return new("IdempotencyConflict");
        }
        else
        {
            if (!replayed) _database.Occurrences.Add(fact);
            alarm = await AttachAsync(fact, trustedCreator, cancellationToken);
            await _database.SaveChangesAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        return new(replayed ? "Replayed" : "Applied", Project(alarm));
    }, cancellationToken);

    /// <summary>Bounded internal projection of already admitted raw facts; creator metadata must come from the trusted source owner.</summary>
    internal Task<AlarmRow> ProcessOccurrenceAsync(Guid occurrenceId, Guid trustedCreator, CancellationToken cancellationToken = default) =>
        SafeValueAsync(async () =>
        {
            if (trustedCreator == Guid.Empty || occurrenceId == Guid.Empty) throw new ArgumentException("Trusted occurrence provenance is required.");
            RequireClean();
            await using var transaction = await _database.Database.BeginTransactionAsync(cancellationToken);
            await LockAsync(cancellationToken);
            var fact = await _database.Occurrences.AsNoTracking().SingleAsync(item => item.Id == occurrenceId, cancellationToken);
            var member = await _database.Memberships.SingleOrDefaultAsync(item => item.OccurrenceId == occurrenceId, cancellationToken);
            if (member is not null)
            {
                var original = await _database.Alarms.SingleAsync(item => item.Id == member.AlarmId, cancellationToken);
                if (original.CreatorId != trustedCreator) throw new InvalidOperationException("Occurrence creator provenance cannot change.");
                await transaction.CommitAsync(cancellationToken);
                return Project(original);
            }
            var alarm = await AttachAsync(fact, trustedCreator, cancellationToken);
            await _database.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return Project(alarm);
        }, cancellationToken);

    private async Task<AlarmEntity> AttachAsync(AlarmOccurrenceEntity fact, Guid creator, CancellationToken ct)
    {
        var bucket = fact.ReceivedAt.UtcTicks / TimeSpan.FromSeconds(30).Ticks;
        var key = Hash(JsonSerializer.Serialize(new
        {
            fact.SourceOwner,
            fact.SourceKind,
            fact.ResourceId,
            fact.Code,
            fact.Severity,
            TenantId = _database.TenantId,
            CreatorId = creator,
            Bucket = bucket
        }));
        var alarm = await _database.Alarms.Where(item => item.AggregateKey == key && item.State != "Cleared")
            .OrderByDescending(item => item.FirstOccurredAt).ThenBy(item => item.Id).FirstOrDefaultAsync(ct);
        AlarmDeliveryEntity delivery;
        if (alarm is null)
        {
            alarm = new()
            {
                TenantId = _database.TenantId,
                Id = Guid.NewGuid(),
                CreatorId = creator,
                AggregateKey = key,
                SourceOwner = fact.SourceOwner,
                SourceKind = fact.SourceKind,
                ResourceId = fact.ResourceId,
                Code = fact.Code,
                Severity = fact.Severity,
                FirstOccurredAt = fact.OccurredAt,
                LastOccurredAt = fact.OccurredAt
            };
            _database.Alarms.Add(alarm);
            var due = fact.Severity is AlarmSeverity.High or AlarmSeverity.Critical ? fact.ReceivedAt :
                new DateTimeOffset((bucket + 1) * TimeSpan.FromSeconds(30).Ticks, TimeSpan.Zero);
            delivery = new()
            {
                TenantId = _database.TenantId,
                Id = Guid.NewGuid(),
                AlarmId = alarm.Id,
                DueAt = due,
                NextAttemptAt = due
            };
            _database.Deliveries.Add(delivery);
        }
        else delivery = await _database.Deliveries.SingleAsync(item => item.AlarmId == alarm.Id, ct);
        _database.Memberships.Add(new() { TenantId = _database.TenantId, OccurrenceId = fact.Id, AlarmId = alarm.Id });
        alarm.OccurrenceCount++;
        if (fact.OccurredAt < alarm.FirstOccurredAt) alarm.FirstOccurredAt = fact.OccurredAt;
        if (fact.OccurredAt > alarm.LastOccurredAt) alarm.LastOccurredAt = fact.OccurredAt;
        alarm.Version = Version(alarm.Version);
        delivery.OccurrenceCount = alarm.OccurrenceCount;
        delivery.Version = Version(delivery.Version);
        if (delivery.State == "Pending" && delivery.DueAt <= Now && !(alarm.SilenceUntil > Now)) Deliver(delivery);
        return alarm;
    }

    private async Task<bool> CompleteDueAsync(AlarmSubject subject, IReadOnlySet<Guid> creators, CancellationToken ct)
    {
        await using var transaction = await _database.Database.BeginTransactionAsync(ct);
        await LockAsync(ct);
        var ids = creators.ToArray();
        var due = await (from delivery in _database.Deliveries
                         join alarm in _database.Alarms on delivery.AlarmId equals alarm.Id
                         where ids.Contains(alarm.CreatorId) && delivery.State == "Pending" && delivery.DueAt <= Now
                         orderby delivery.DueAt, delivery.Id
                         select new { Delivery = delivery, Alarm = alarm }).Take(100).ToListAsync(ct);
        foreach (var item in due)
        {
            if (!await DeliveryAllowed(subject, "notifications.deliveries.read", item.Alarm.CreatorId, AlarmConsoleFields.DeliveryRead, ct))
                return false;
            if (!(item.Alarm.SilenceUntil > Now)) Deliver(item.Delivery);
        }
        await _database.SaveChangesAsync(ct);
        foreach (var item in due)
            if (!await DeliveryAllowed(subject, "notifications.deliveries.read", item.Alarm.CreatorId, AlarmConsoleFields.DeliveryRead, ct))
                return false;
        await transaction.CommitAsync(ct);
        _database.ChangeTracker.Clear();
        return true;
    }

    private void Deliver(AlarmDeliveryEntity delivery)
    {
        if (delivery.Channel != "InApp") throw new InvalidOperationException("External delivery adapter is unavailable.");
        delivery.State = "Sent";
        delivery.AttemptCount++;
        delivery.NextAttemptAt = null;
        delivery.ErrorCode = null;
        delivery.Version = Version(delivery.Version);
        _database.Attempts.Add(new()
        {
            TenantId = _database.TenantId,
            Id = Guid.NewGuid(),
            DeliveryId = delivery.Id,
            AttemptedAt = Now,
            State = "Sent"
        });
    }

    private async Task LockAsync(CancellationToken ct)
    {
        // A tenant-local transactional mutex covers missing aggregate/receipt rows and concurrent memberships.
        var tenant = _database.TenantId.ToString("D");
        await _database.Database.ExecuteSqlInterpolatedAsync(
            $"INSERT INTO ntf_alarm_console_locks (TenantId) VALUES ({tenant}) ON DUPLICATE KEY UPDATE TenantId = TenantId", ct);
        await _database.Database.SqlQuery<int>(
            $"SELECT 1 AS Value FROM ntf_alarm_console_locks WHERE TenantId = {tenant} FOR UPDATE").ToListAsync(ct);
    }

    private void Receipt(AlarmSubject subject, Guid operation, Guid target, Guid creator, string kind, string body,
        string reason, DateTimeOffset? expiry = null)
    {
        _database.Operations.Add(new()
        {
            TenantId = _database.TenantId,
            OperationId = operation,
            ActorId = subject.ActorId,
            TargetId = target,
            CreatorId = creator,
            Kind = kind,
            Fingerprint = Hash(body),
            Body = body
        });
        _database.Audits.Add(new()
        {
            TenantId = _database.TenantId,
            Id = Guid.NewGuid(),
            OperationId = operation,
            ActorId = subject.ActorId,
            TargetId = target,
            Kind = kind,
            Reason = reason,
            At = Now,
            ExpiresAt = expiry
        });
    }
    private static bool Matches(AlarmOperationEntity receipt, AlarmSubject subject, string kind, string body) =>
        receipt.ActorId == subject.ActorId && receipt.Kind == kind && receipt.Fingerprint == Hash(body) && receipt.Body == body;
    private async Task<bool> Allowed(AlarmSubject subject, string permission, Guid creator, IReadOnlyCollection<string> fields, CancellationToken ct)
    {
        var granted = await _authorization.ReadFieldsAsync(subject, permission, creator, ct);
        return fields.All(granted.Contains);
    }
    private async Task<bool> AlarmActionAllowed(AlarmSubject subject, string permission, Guid creator, IReadOnlyCollection<string> fields, CancellationToken ct) =>
        await Allowed(subject, permission, creator, fields, ct) &&
        await Allowed(subject, "notifications.alarms.read", creator, AlarmConsoleFields.AlarmRead, ct);
    private async Task<bool> DeliveryAllowed(AlarmSubject subject, string permission, Guid creator, IReadOnlyCollection<string> fields, CancellationToken ct) =>
        await Allowed(subject, permission, creator, fields, ct) &&
        await Allowed(subject, "notifications.deliveries.read", creator, AlarmConsoleFields.DeliveryRead, ct) &&
        await Allowed(subject, "notifications.alarms.read", creator, AlarmConsoleFields.AlarmRead, ct);
    private static string[] ActionFields(bool silence) => AlarmConsoleFields.AlarmRead
        .Concat(silence ? new[] { "OperationId", "ExpectedVersion", "Reason", "Seconds" } : new[] { "OperationId", "ExpectedVersion", "Reason" }).ToArray();
    private static bool ValidAction(AlarmActionCommand command) => command.OperationId != Guid.Empty &&
        command.ExpectedVersion is > 0 and <= 9007199254740991L &&
        !string.IsNullOrWhiteSpace(command.Reason) && command.Reason.Length <= 512;
    private static bool ValidPage(int offset, int limit) => offset is >= 0 and <= 10000 && limit is >= 1 and <= 100;
    private static bool TrySeverity(string severity, out AlarmSeverity value) =>
        Enum.TryParse(severity, ignoreCase: false, out value) && Enum.IsDefined(value) && value.ToString() == severity;
    private DateTimeOffset Now => new(_clock.GetUtcNow().UtcTicks / 10 * 10, TimeSpan.Zero);
    private static long Version(long previous) => previous < 9007199254740991L
        ? checked(previous + 1) : throw new InvalidOperationException("Alarm version counter is exhausted.");
    private static string Hash(string input) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(input)));
    private void RequireClean(AlarmSubject? subject = null)
    {
        if (subject is not null && (subject.TenantId != _database.TenantId || subject.ActorId == Guid.Empty || subject.SessionId == Guid.Empty))
            throw new ArgumentException("A current tenant subject is required.");
        if (_database.ChangeTracker.Entries().Any() || _database.Database.CurrentTransaction is not null ||
            System.Transactions.Transaction.Current is not null)
            throw new InvalidOperationException("Alarm console requires a clean independent Notifications owner scope.");
    }
    private static AlarmRow Project(AlarmEntity alarm) => new(alarm.Id, alarm.SourceOwner, alarm.SourceKind.ToString(),
        alarm.ResourceId, alarm.Code, alarm.Severity.ToString(), alarm.FirstOccurredAt, alarm.LastOccurredAt,
        alarm.OccurrenceCount, alarm.State, alarm.Version, alarm.AcknowledgedBy, alarm.AcknowledgedAt, alarm.SilenceUntil);
    private static AlarmDeliveryRow Project(AlarmDeliveryEntity delivery) => new(delivery.Id, delivery.AlarmId,
        delivery.Channel, delivery.State, delivery.OccurrenceCount, delivery.DueAt, delivery.AttemptCount,
        delivery.NextAttemptAt, delivery.ErrorCode, delivery.Version);
    private static Task<AlarmResult<T>> SafeAsync<T>(Func<Task<AlarmResult<T>>> action, CancellationToken ct) => SafeValueAsync(action, ct);
    private static async Task<T> SafeValueAsync<T>(Func<Task<T>> action, CancellationToken ct)
    {
        try { ct.ThrowIfCancellationRequested(); return await action(); }
        catch (DbUpdateConcurrencyException) { throw new AlarmConsoleDatabaseException(); }
        catch (DbUpdateException error) when (error.InnerException is DbException) { ct.ThrowIfCancellationRequested(); throw new AlarmConsoleDatabaseException(); }
        catch (DbException) { ct.ThrowIfCancellationRequested(); throw new AlarmConsoleDatabaseException(); }
    }
    /// <inheritdoc />
    public ValueTask DisposeAsync() => _database.DisposeAsync();
}
