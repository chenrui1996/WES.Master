using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using MySql.Data.MySqlClient;
using Wes.Notifications.Infrastructure.Persistence;

namespace Wes.Notifications.Features.Alarms;

/// <summary>Append-only tenant owner ingress; never changes equipment state or sends a notification.</summary>
internal sealed class AlarmOccurrenceStore(NotificationsDbContext database, TimeProvider clock)
{
    private static readonly Regex OwnerPattern = new("\\A[A-Za-z][A-Za-z0-9_]*\\z",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    private static readonly Regex CodePattern = new("\\A[A-Za-z][A-Za-z0-9_.-]*\\z",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    internal async Task<OccurrenceResult> RecordAsync(AlarmOccurrenceInput input, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        cancellationToken.ThrowIfCancellationRequested();
        if (database.ChangeTracker.Entries().Any() || database.Database.CurrentTransaction is not null ||
            System.Transactions.Transaction.Current is not null)
            throw new InvalidOperationException("Alarm ingress requires a clean independent Notifications owner scope.");
        var now = clock.GetUtcNow();
        var invalid = Validate(input, now);
        if (invalid is not null) return Invalid(invalid);

        var fingerprint = Fingerprint(input);
        try
        {
            var original = await OriginalAsync(input, cancellationToken);
            if (original is not null) return Replay(original, fingerprint);
            var fact = CreateFact(input, database.TenantId, now, fingerprint);
            database.Occurrences.Add(fact);
            try
            {
                await database.SaveChangesAsync(cancellationToken);
                return new(OccurrenceDecision.Recorded, Project(fact));
            }
            catch (DbUpdateException error) when (error.InnerException is MySqlException { Number: 1062 })
            {
                // Only a proven source-identity duplicate may resolve to the persisted winner; never retry the write.
                database.ChangeTracker.Clear();
                original = await OriginalAsync(input, cancellationToken);
                if (original is null)
                    throw new InvalidOperationException("Alarm uniqueness conflict has no matching source identity.");
                return Replay(original, fingerprint);
            }
        }
        catch (DbUpdateException error) when (error.InnerException is DbException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw new AlarmOccurrenceDatabaseException();
        }
        catch (DbException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw new AlarmOccurrenceDatabaseException();
        }
    }

    internal static string? Validate(AlarmOccurrenceInput input, DateTimeOffset now)
    {
        if (input.SourceEventId == Guid.Empty || input.ResourceId == Guid.Empty || input.CorrelationId == Guid.Empty)
            return "Alarm.Identity";
        if (input.SourceOwner is null || input.SourceOwner.Length is < 1 or > 64 || !OwnerPattern.IsMatch(input.SourceOwner))
            return "Alarm.SourceOwner";
        if (input.Code is null || input.Code.Length is < 1 or > 64 || !CodePattern.IsMatch(input.Code))
            return "Alarm.Code";
        if (!Enum.IsDefined(input.SourceKind) || !Enum.IsDefined(input.Severity))
            return "Alarm.Classification";
        if (string.IsNullOrWhiteSpace(input.Summary) || input.Summary.Length > 512)
            return "Alarm.Summary";
        if (input.OccurredAt.Offset != TimeSpan.Zero || input.OccurredAt > now)
            return "Alarm.OccurredAt";
        return null;
    }

    internal static string Fingerprint(AlarmOccurrenceInput input) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(input))));

    internal static AlarmOccurrenceEntity CreateFact(AlarmOccurrenceInput input, Guid tenant, DateTimeOffset now, string fingerprint) => new()
    {
        TenantId = tenant,
        Id = Guid.NewGuid(),
        SourceEventId = input.SourceEventId,
        SourceOwner = input.SourceOwner,
        SourceKind = input.SourceKind,
        ResourceId = input.ResourceId,
        Code = input.Code,
        Severity = input.Severity,
        OccurredAt = input.OccurredAt,
        ReceivedAt = new DateTimeOffset(now.UtcTicks - now.UtcTicks % 10, TimeSpan.Zero),
        CorrelationId = input.CorrelationId,
        Summary = input.Summary,
        Fingerprint = fingerprint
    };
    private Task<AlarmOccurrenceEntity?> OriginalAsync(AlarmOccurrenceInput input, CancellationToken ct) =>
        database.Occurrences.AsNoTracking().SingleOrDefaultAsync(
            fact => fact.SourceOwner == input.SourceOwner && fact.SourceEventId == input.SourceEventId, ct);

    private static OccurrenceResult Replay(AlarmOccurrenceEntity original, string fingerprint) =>
        original.Fingerprint == fingerprint
            ? new(OccurrenceDecision.Replayed, Project(original))
            : new(OccurrenceDecision.IdempotencyConflict, ErrorCode: "Alarm.SourceEventMismatch");

    private static OccurrenceResult Invalid(string code) => new(OccurrenceDecision.Invalid, ErrorCode: code);

    private static AlarmOccurrence Project(AlarmOccurrenceEntity fact) => new(
        fact.Id, fact.SourceEventId, fact.SourceOwner, fact.SourceKind, fact.ResourceId, fact.Code,
        fact.Severity, fact.OccurredAt, fact.ReceivedAt, fact.CorrelationId, fact.Summary);
}

/// <summary>Provider failure is explicit without exposing connection details or an inner provider exception.</summary>
internal sealed class AlarmOccurrenceDatabaseException() : Exception("Notifications alarm database operation failed.");
