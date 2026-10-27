using System.Collections.Immutable;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Wes.Platform.Domain;
using Wes.Platform.Features.Authorization;
using Wes.Platform.Features.Tenancy;

namespace Wes.Platform.Features.Files;

/// <summary>Current-authorized realtime metadata page; business instances additionally require explicit live entity authority.</summary>
public sealed record StagingFilePage(FileAccessDecision Decision, ImmutableArray<StagingFileView> Rows, int Offset, int Limit);

public sealed partial class StagingFileAccess
{
    /// <summary>Filters staging or exact authorized entity bindings and each required field's scope in SQL before bounded paging.</summary>
    public Task<StagingFilePage> QueryAsync(TenantContext context, int offset = 0, int limit = 25,
        int? state = null, CancellationToken ct = default) => RunAsync(async () =>
    {
        ArgumentNullException.ThrowIfNull(context);
        RequireCleanOwner(ct);
        if (offset is < 0 or > 10000 || limit is not (25 or 50 or 100)
            || state.HasValue && !Enum.IsDefined(typeof(QuarantinedFileState), state.Value))
        {
            _logger.LogWarning("File catalog rejected invalid paging or state.");
            return new StagingFilePage(FileAccessDecision.Invalid, [], offset, limit);
        }
        if (context.TenantId != _database.TenantId)
            return new(Denied().Decision, [], offset, limit);
        if (_entity is not null && !await _entityAuthorization!.CanReadAsync(context, _entity, ct))
            return new(Denied().Decision, [], offset, limit);
        var grants = await new CurrentAuthorization(_database, _clock).ReadGrantsAsync(context, ct);
        var query = _database.Files.AsNoTracking().Where(file =>
            _database.DirectoryUsers.Any(creator => creator.Id == file.CreatorId));
        query = _entity is null
            ? query.Where(file => file.ReferenceCount == 0
                && !_database.FileReferences.Any(reference => reference.FileId == file.Id))
            : query.Where(file => file.ReferenceCount > 0 && _database.FileReferences.Any(reference =>
                reference.FileId == file.Id && reference.Owner == _entity.Owner && reference.EntityId == _entity.EntityId));
        foreach (var field in ReadFields)
        {
            var matching = grants.Where(grant => grant.Permission == "platform.files.read" && grant.Fields.Contains(field)).ToArray();
            if (matching.Length == 0) return new(Denied().Decision, [], offset, limit);
            if (matching.Any(grant => grant.Scope == DataScope.All)) continue;
            var own = matching.Any(grant => grant.Scope == DataScope.Own);
            var departments = matching.Where(grant => grant.Scope == DataScope.Departments)
                .SelectMany(grant => grant.Departments);
            var creators = _database.DirectoryUsers.Where(ScopeExpressions.MatchIds<DirectoryUser>(creator => creator.DepartmentId, departments));
            query = query.Where(file => own && file.CreatorId == context.ActorId || creators.Any(creator => creator.Id == file.CreatorId));
        }
        if (state.HasValue)
        {
            var status = (QuarantinedFileState)state.Value;
            query = query.Where(file => file.State == status);
        }
        var problemBefore = _clock.GetUtcNow() - QuarantinedFile.ProcessingProblemAge;
        var rows = await query.OrderByDescending(file => file.CreatedAt).ThenBy(file => file.Id)
            .Skip(offset).Take(limit).Select(file => new StagingFileView(file.Id, file.FileName, file.ContentType,
                file.ByteCount, file.State, file.Stored, file.Version, file.CreatedAt,
                file.State == QuarantinedFileState.Quarantined && file.CreatedAt <= problemBefore)).ToListAsync(ct);
        if (_entity is not null && !await _entityAuthorization!.CanReadAsync(context, _entity, ct))
            return new(Denied().Decision, [], offset, limit);
        return new(FileAccessDecision.Success, rows.ToImmutableArray(), offset, limit);
    }, ct);
}
