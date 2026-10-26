using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore;
using MySql.Data.MySqlClient;
using Wes.Platform.Domain;
using Wes.Platform.Features.Authorization;
using Wes.Platform.Features.Tenancy;

namespace Wes.Platform.Features.Audit;

/// <summary>Finite export outcomes; bytes exist only after successful current authorization and audit commit.</summary>
public enum AuditExportDecision
{
    /// <summary>An audited delivery is ready.</summary>
    Success,
    /// <summary>Current read/export scope or fields do not cover the whole fixed membership.</summary>
    Forbidden,
    /// <summary>The snapshot is absent from the original owner/actor/session.</summary>
    NotFound,
    /// <summary>The fixed membership expired.</summary>
    Expired,
    /// <summary>The operation already describes a different export.</summary>
    IdempotencyConflict
}

/// <summary>Metadata-only rejection or fully prepared UTF-8 CSV after durable audit.</summary>
public sealed record AuditExportResult(AuditExportDecision Decision, byte[]? Content = null);

public sealed partial class QueryAudit
{
    private static readonly ImmutableHashSet<string> ExportColumns = ImmutableHashSet.Create(StringComparer.Ordinal,
        "Id", "OccurredAt", "ActorId", "TargetId", "CorrelationId", "Action", "BeforeVersion", "AfterVersion");

    /// <summary>Exports one unexpired snapshot with explicit ordered columns; original operation replay still revalidates authorization.</summary>
    public async Task<AuditExportResult> ExportSnapshotAsync(TenantContext context, Guid snapshotId, Guid operationId,
        IReadOnlyList<string> columns, Guid correlation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(columns);
        if (snapshotId == Guid.Empty || operationId == Guid.Empty || correlation == Guid.Empty
            || columns.Count is < 2 or > 8 || !columns.Contains("Id") || !columns.Contains("OccurredAt")
            || columns.Distinct(StringComparer.Ordinal).Count() != columns.Count || columns.Any(column => !ExportColumns.Contains(column)))
            throw new ArgumentException("An export requires explicit unique safe columns, identity/time and nonempty operation references.");
        if (database.ChangeTracker.HasChanges())
            throw new InvalidOperationException("Audit export requires a clean owner unit of work.");
        var selection = string.Join(",", columns);
        var snapshot = await database.AuditQuerySnapshots.AsNoTracking().SingleOrDefaultAsync(item => item.Id == snapshotId, cancellationToken);
        if (snapshot is null || snapshot.ActorId != context.ActorId || snapshot.SessionId != context.SessionId)
            return new(AuditExportDecision.NotFound);
        if (clock.GetUtcNow() >= snapshot.ExpiresAt) return new(AuditExportDecision.Expired);
        var grants = await new CurrentAuthorization(database, clock).ReadGrantsAsync(context, cancellationToken);
        var fingerprint = ExportFingerprint(context, grants, snapshot);
        var receipt = await database.AuditExportReceipts.AsNoTracking().SingleOrDefaultAsync(
            item => item.OperationId == operationId, cancellationToken);
        if (receipt is not null && !Matches(receipt)) return new(AuditExportDecision.IdempotencyConflict);

        var csv = new StringBuilder();
        AppendCsv(csv, columns);
        for (var offset = 0; offset < Math.Max(1, snapshot.Count); offset += 100)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var page = await ReadSnapshotAsync(context, snapshotId, offset, 100, cancellationToken);
            if (page.Decision != AuditSnapshotDecision.Success) return Rejected(page.Decision);
            var ids = page.Page!.Rows.Select(row => row.Id).ToArray();
            var owners = await database.Audit.AsNoTracking()
                .Where(ScopeExpressions.MatchIds<PlatformAudit>(item => item.Id, ids))
                .Select(item => new { item.Id, item.ActorId }).ToListAsync(cancellationToken);
            if (owners.Count != ids.Length)
                throw new InvalidOperationException("Export membership no longer resolves to immutable audit facts.");
            foreach (var owner in owners.DefaultIfEmpty())
            {
                var resource = new ResourceScope(context.TenantId, owner?.ActorId ?? context.ActorId, Guid.Empty);
                if (new[] { "platform.audit.read", "platform.audit.export" }.Any(permission =>
                    !columns.All(RolePolicy.VisibleFields(context.TenantId, context.ActorId, permission, resource, grants).Contains)))
                    return new(AuditExportDecision.Forbidden);
            }
            foreach (var row in page.Page.Rows) AppendCsv(csv, columns.Select(column => Cell(row, column)));
        }

        var finalPage = await ReadSnapshotAsync(context, snapshotId, 0, 1, cancellationToken);
        if (finalPage.Decision != AuditSnapshotDecision.Success) return Rejected(finalPage.Decision);
        var currentGrants = await new CurrentAuthorization(database, clock).ReadGrantsAsync(context, cancellationToken);
        if (fingerprint != ExportFingerprint(context, currentGrants, snapshot)) return new(AuditExportDecision.Forbidden);
        cancellationToken.ThrowIfCancellationRequested();
        if (receipt is null)
        {
            var audit = new PlatformAudit(context.TenantId, context.ActorId, snapshotId, correlation,
                PlatformAuditAction.AuditExported, null, operationId.ToString("D"), clock.GetUtcNow());
            database.Audit.Add(audit);
            database.AuditExportReceipts.Add(new PlatformAuditExportReceipt(context.TenantId, operationId,
                context.ActorId, context.SessionId, snapshotId, selection, audit.Id, clock.GetUtcNow()));
            try { await database.SaveChangesAsync(cancellationToken); }
            catch (DbUpdateException error) when (error.InnerException is MySqlException { Number: 1062 })
            {
                database.ChangeTracker.Clear();
                var winner = await database.AuditExportReceipts.AsNoTracking().SingleOrDefaultAsync(
                    item => item.OperationId == operationId, cancellationToken);
                if (winner is null) throw;
                if (!Matches(winner)) return new(AuditExportDecision.IdempotencyConflict);
            }
        }
        var delivered = await ReadSnapshotAsync(context, snapshotId, 0, 1, cancellationToken);
        if (delivered.Decision != AuditSnapshotDecision.Success) return Rejected(delivered.Decision);
        if (fingerprint != ExportFingerprint(context,
            await new CurrentAuthorization(database, clock).ReadGrantsAsync(context, cancellationToken), snapshot))
            return new(AuditExportDecision.Forbidden);
        return new(AuditExportDecision.Success, Encoding.UTF8.GetBytes(csv.ToString()));

        bool Matches(PlatformAuditExportReceipt existing) => existing.ActorId == context.ActorId
            && existing.SessionId == context.SessionId && existing.SnapshotId == snapshotId && existing.Columns == selection;
    }

    private static string ExportFingerprint(TenantContext context, ImmutableArray<PermissionGrant> grants,
        PlatformAuditQuerySnapshot snapshot) =>
        CreateScope(context, grants, snapshot.From, snapshot.To, snapshot.FilterAction, snapshot.CorrelationId,
            "platform.audit.export").AuthorizationFingerprint;

    private static AuditExportResult Rejected(AuditSnapshotDecision decision) => new(decision switch
    {
        AuditSnapshotDecision.Forbidden => AuditExportDecision.Forbidden,
        AuditSnapshotDecision.NotFound => AuditExportDecision.NotFound,
        AuditSnapshotDecision.Expired => AuditExportDecision.Expired,
        _ => throw new InvalidOperationException("Unexpected audit snapshot export outcome.")
    });

    private static void AppendCsv(StringBuilder csv, IEnumerable<string> values) =>
        csv.AppendJoin(",", values.Select(value => "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"")).Append("\r\n");

    private static string Cell(AuditRow row, string column) => column switch
    {
        "Id" => row.Id.ToString("D"),
        "OccurredAt" => row.OccurredAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
        "ActorId" => row.ActorId?.ToString("D") ?? "",
        "TargetId" => row.TargetId?.ToString("D") ?? "",
        "CorrelationId" => row.CorrelationId?.ToString("D") ?? "",
        "Action" => row.Action?.ToString() ?? "",
        "BeforeVersion" => row.BeforeVersion ?? "",
        "AfterVersion" => row.AfterVersion ?? "",
        _ => throw new ArgumentException("Unknown export column.", nameof(column))
    };
}
