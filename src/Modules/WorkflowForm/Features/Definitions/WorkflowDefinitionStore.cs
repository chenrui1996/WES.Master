using System.Data;
using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using MySql.Data.MySqlClient;
using Wes.WorkflowForm.Definitions;
using Wes.WorkflowForm.Infrastructure.Persistence;

namespace Wes.WorkflowForm.Features.Definitions;

internal sealed record WorkflowMutationContext(Guid ActorId, Guid CorrelationId, Guid OperationId);

internal enum WorkflowPersistenceDecision
{
    DraftSaved,
    Published,
    Replayed,
    Invalid,
    NotFound,
    Conflict,
    IdempotencyConflict
}

internal sealed record WorkflowPersistenceResult(
    WorkflowPersistenceDecision Decision,
    Guid? ResourceId = null,
    string? ResourceVersion = null,
    int? PublishedVersion = null);

/// <summary>Tenant-local immutable WorkflowForm publication and mutation receipts; no authorization is acquired here.</summary>
internal sealed class WorkflowDefinitionStore(WorkflowFormDbContext database, TimeProvider clock)
{
    private const string Owner = "WorkflowForm";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    internal async Task<WorkflowPersistenceResult> SaveSchemaDraftAsync(
        WorkflowMutationContext mutation, FormSchema schema, string? expectedVersion = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(mutation);
        ArgumentNullException.ThrowIfNull(schema);
        if (!ValidMutation(mutation) || schema.TenantId != database.TenantId || schema.Id == Guid.Empty
            || !ValidExpectedVersion(expectedVersion))
            return Result(WorkflowPersistenceDecision.Invalid);

        var fingerprintSchema = schema with { Version = 0 };
        var fingerprint = Fingerprint("schema.draft.save", schema.Id, expectedVersion, fingerprintSchema);
        var replay = await ReadReplayAsync(mutation.OperationId, fingerprint, ct);
        if (replay is not null) return replay;

        var normalized = FormValidation.Schema(schema with { Version = 1 });
        if (!normalized.IsValid) return Result(WorkflowPersistenceDecision.Invalid, schema.Id);
        try
        {
            await using var transaction = await database.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
            try
            {
                replay = await ReadReplayAsync(mutation.OperationId, fingerprint, ct);
                if (replay is not null) return await CommitAndReturnAsync(transaction, replay, ct);

                await FenceAsync("wf_schema_drafts", schema.Id, ct);
                var draft = await database.SchemaDrafts.SingleOrDefaultAsync(item => item.Id == schema.Id, ct);
                var published = await database.SchemaVersions.Where(item => item.Id == schema.Id)
                    .OrderByDescending(item => item.Version)
                    .Select(item => new { item.Version, item.BusinessType }).FirstOrDefaultAsync(ct);
                var businessType = normalized.Value!.BusinessType;
                if (published is not null && published.BusinessType != businessType)
                    return await CommitAndReturnAsync(transaction, Result(WorkflowPersistenceDecision.Conflict, schema.Id), ct);

                if (draft is null)
                {
                    if (expectedVersion is not null)
                        return await CommitAndReturnAsync(transaction, Result(WorkflowPersistenceDecision.Conflict, schema.Id), ct);
                    var version = (published?.Version ?? 0) + 1;
                    var snapshot = normalized.Value with { Version = version };
                    var versionToken = NewVersion();
                    var auditId = Guid.NewGuid();
                    var operation = BuildOperation(mutation, "schema.draft.save", schema.Id, fingerprint,
                        auditId, Result(WorkflowPersistenceDecision.DraftSaved, schema.Id, versionToken, version));
                    var audit = BuildAudit(mutation, auditId, schema.Id, "Schema", "SchemaDraftSaved",
                        before: null, versionToken);
                    database.SchemaDrafts.Add(new WorkflowSchemaDraftEntity
                    {
                        TenantId = database.TenantId,
                        Id = schema.Id,
                        BusinessType = businessType,
                        PlannedVersion = version,
                        DraftVersion = versionToken,
                        SnapshotJson = Serialize(snapshot),
                        LastOperationId = mutation.OperationId,
                        AuditFactId = auditId
                    });
                    database.AuditFacts.Add(audit);
                    database.OperationReceipts.Add(operation);
                    await database.SaveChangesAsync(ct);
                    await transaction.CommitAsync(ct);
                    return DeserializeResult(operation.ResultJson);
                }

                if (expectedVersion is null || draft.DraftVersion != expectedVersion
                    || draft.BusinessType != businessType)
                    return await CommitAndReturnAsync(transaction, Result(WorkflowPersistenceDecision.Conflict, schema.Id), ct);

                var updateToken = NewVersion();
                var updatedSnapshot = normalized.Value with { Version = draft.PlannedVersion };
                var updateAuditId = Guid.NewGuid();
                var updateOperation = BuildOperation(mutation, "schema.draft.save", schema.Id, fingerprint,
                    updateAuditId, Result(WorkflowPersistenceDecision.DraftSaved, schema.Id, updateToken, draft.PlannedVersion));
                database.AuditFacts.Add(BuildAudit(mutation, updateAuditId, schema.Id, "Schema", "SchemaDraftSaved",
                    draft.DraftVersion, updateToken));
                database.OperationReceipts.Add(updateOperation);
                draft.SnapshotJson = Serialize(updatedSnapshot);
                draft.DraftVersion = updateToken;
                draft.LastOperationId = mutation.OperationId;
                draft.AuditFactId = updateAuditId;
                await database.SaveChangesAsync(ct);
                await transaction.CommitAsync(ct);
                return DeserializeResult(updateOperation.ResultJson);
            }
            catch (DbUpdateConcurrencyException)
            {
                await RollbackAndClearAsync(transaction, ct);
                return await ResolveConflictAsync(mutation.OperationId, fingerprint, schema.Id, ct);
            }
            catch (DbUpdateException error) when (IsOperationRace(error.InnerException))
            {
                await RollbackAndClearAsync(transaction, ct);
                return await ResolveConflictAsync(mutation.OperationId, fingerprint, schema.Id, ct);
            }
            catch (MySqlException error) when (IsOperationRace(error))
            {
                await RollbackAndClearAsync(transaction, ct);
                return await ResolveConflictAsync(mutation.OperationId, fingerprint, schema.Id, ct);
            }
        }
        catch (DbUpdateException error) when (error.InnerException is DbException failure)
        {
            ct.ThrowIfCancellationRequested();
            throw WorkflowFormDatabaseException.Safe(failure);
        }
        catch (DbException error)
        {
            ct.ThrowIfCancellationRequested();
            throw WorkflowFormDatabaseException.Safe(error);
        }
    }

    internal async Task<WorkflowPersistenceResult> PublishSchemaAsync(
        WorkflowMutationContext mutation, Guid schemaId, string expectedDraftVersion, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(mutation);
        if (!ValidMutation(mutation) || schemaId == Guid.Empty || !ValidExpectedVersion(expectedDraftVersion))
            return Result(WorkflowPersistenceDecision.Invalid, schemaId);
        var fingerprint = Fingerprint("schema.publish", schemaId, expectedDraftVersion, body: null);
        var replay = await ReadReplayAsync(mutation.OperationId, fingerprint, ct);
        if (replay is not null) return replay;
        try
        {
            await using var transaction = await database.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
            try
            {
                replay = await ReadReplayAsync(mutation.OperationId, fingerprint, ct);
                if (replay is not null) return await CommitAndReturnAsync(transaction, replay, ct);
                await FenceAsync("wf_schema_drafts", schemaId, ct);
                var draft = await database.SchemaDrafts.SingleOrDefaultAsync(item => item.Id == schemaId, ct);
                if (draft is null || draft.DraftVersion != expectedDraftVersion)
                    return await CommitAndReturnAsync(transaction, Result(WorkflowPersistenceDecision.Conflict, schemaId), ct);

                var latest = await database.SchemaVersions.Where(item => item.Id == schemaId)
                    .OrderByDescending(item => item.Version).FirstOrDefaultAsync(ct);
                var nextVersion = (latest?.Version ?? 0) + 1;
                if (draft.PlannedVersion != nextVersion)
                    return await CommitAndReturnAsync(transaction, Result(WorkflowPersistenceDecision.Conflict, schemaId), ct);
                var snapshot = Deserialize<FormSchema>(draft.SnapshotJson);
                if (snapshot.TenantId != database.TenantId || snapshot.Id != schemaId
                    || snapshot.Version != nextVersion || snapshot.BusinessType != draft.BusinessType
                    || !FormValidation.Schema(snapshot).IsValid)
                    return await CommitAndReturnAsync(transaction, Result(WorkflowPersistenceDecision.Invalid, schemaId), ct);

                var versionToken = NewVersion();
                var auditId = Guid.NewGuid();
                var result = Result(WorkflowPersistenceDecision.Published, schemaId, versionToken, nextVersion);
                var operation = BuildOperation(mutation, "schema.publish", schemaId, fingerprint, auditId, result);
                database.SchemaVersions.Add(new WorkflowSchemaVersionEntity
                {
                    TenantId = database.TenantId,
                    Id = schemaId,
                    Version = nextVersion,
                    VersionToken = versionToken,
                    BusinessType = draft.BusinessType,
                    SnapshotJson = draft.SnapshotJson,
                    OperationId = mutation.OperationId,
                    PublishedAt = clock.GetUtcNow()
                });
                database.AuditFacts.Add(BuildAudit(mutation, auditId, schemaId, "Schema", "SchemaPublished",
                    draft.DraftVersion, versionToken));
                database.OperationReceipts.Add(operation);
                draft.LastOperationId = mutation.OperationId;
                draft.AuditFactId = auditId;
                database.SchemaDrafts.Remove(draft);
                await database.SaveChangesAsync(ct);
                await transaction.CommitAsync(ct);
                return DeserializeResult(operation.ResultJson);
            }
            catch (DbUpdateConcurrencyException)
            {
                await RollbackAndClearAsync(transaction, ct);
                return await ResolveConflictAsync(mutation.OperationId, fingerprint, schemaId, ct);
            }
            catch (DbUpdateException error) when (IsOperationRace(error.InnerException))
            {
                await RollbackAndClearAsync(transaction, ct);
                return await ResolveConflictAsync(mutation.OperationId, fingerprint, schemaId, ct);
            }
            catch (MySqlException error) when (IsOperationRace(error))
            {
                await RollbackAndClearAsync(transaction, ct);
                return await ResolveConflictAsync(mutation.OperationId, fingerprint, schemaId, ct);
            }
        }
        catch (DbUpdateException error) when (error.InnerException is DbException failure)
        {
            ct.ThrowIfCancellationRequested();
            throw WorkflowFormDatabaseException.Safe(failure);
        }
        catch (DbException error)
        {
            ct.ThrowIfCancellationRequested();
            throw WorkflowFormDatabaseException.Safe(error);
        }
    }

    internal async Task<WorkflowPersistenceResult> SaveDefinitionDraftAsync(
        WorkflowMutationContext mutation, WorkflowDefinition definition, string? expectedVersion = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(mutation);
        ArgumentNullException.ThrowIfNull(definition);
        if (!ValidMutation(mutation) || definition.TenantId != database.TenantId || definition.Id == Guid.Empty
            || !ValidExpectedVersion(expectedVersion))
            return Result(WorkflowPersistenceDecision.Invalid, definition.Id);
        var fingerprint = Fingerprint("definition.draft.save", definition.Id, expectedVersion,
            definition with { Version = 0 });
        var replay = await ReadReplayAsync(mutation.OperationId, fingerprint, ct);
        if (replay is not null) return replay;
        if (definition.Priority < 0 || !FormValidation.IsKey(definition.BusinessType)
            || definition.Nodes is null || definition.Nodes.Count is < 3 or > 100)
            return Result(WorkflowPersistenceDecision.Invalid, definition.Id);
        try
        {
            await using var transaction = await database.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
            try
            {
                replay = await ReadReplayAsync(mutation.OperationId, fingerprint, ct);
                if (replay is not null) return await CommitAndReturnAsync(transaction, replay, ct);
                await FenceAsync("wf_definition_drafts", definition.Id, ct);
                var draft = await database.DefinitionDrafts.SingleOrDefaultAsync(item => item.Id == definition.Id, ct);
                var published = await database.DefinitionVersions.Where(item => item.Id == definition.Id)
                    .OrderByDescending(item => item.Version)
                    .Select(item => new { item.Version, item.BusinessType }).FirstOrDefaultAsync(ct);
                if (published is not null && published.BusinessType != definition.BusinessType)
                    return await CommitAndReturnAsync(transaction, Result(WorkflowPersistenceDecision.Conflict, definition.Id), ct);

                var schemaVersion = await database.SchemaVersions.AsNoTracking().SingleOrDefaultAsync(
                    item => item.Id == definition.SchemaId && item.Version == definition.SchemaVersion, ct);
                if (schemaVersion is null || schemaVersion.BusinessType != definition.BusinessType)
                    return await CommitAndReturnAsync(transaction, Result(WorkflowPersistenceDecision.NotFound, definition.Id), ct);

                int plannedVersion;
                if (draft is null)
                {
                    if (expectedVersion is not null)
                        return await CommitAndReturnAsync(transaction, Result(WorkflowPersistenceDecision.Conflict, definition.Id), ct);
                    plannedVersion = (published?.Version ?? 0) + 1;
                    draft = new WorkflowDefinitionDraftEntity
                    {
                        TenantId = database.TenantId,
                        Id = definition.Id,
                        BusinessType = definition.BusinessType,
                        PlannedVersion = plannedVersion
                    };
                    database.DefinitionDrafts.Add(draft);
                }
                else
                {
                    if (expectedVersion is null || draft.DraftVersion != expectedVersion
                        || draft.BusinessType != definition.BusinessType)
                        return await CommitAndReturnAsync(transaction, Result(WorkflowPersistenceDecision.Conflict, definition.Id), ct);
                    plannedVersion = draft.PlannedVersion;
                }

                var snapshot = definition with { Version = plannedVersion };
                var versionToken = NewVersion();
                var auditId = Guid.NewGuid();
                var result = Result(WorkflowPersistenceDecision.DraftSaved, definition.Id, versionToken, plannedVersion);
                var operation = BuildOperation(mutation, "definition.draft.save", definition.Id, fingerprint, auditId, result);
                database.AuditFacts.Add(BuildAudit(mutation, auditId, definition.Id, "Definition", "DefinitionDraftSaved",
                    draft.DraftVersion.Length == 0 ? null : draft.DraftVersion, versionToken));
                database.OperationReceipts.Add(operation);
                draft.BusinessType = definition.BusinessType;
                draft.PlannedVersion = plannedVersion;
                draft.DraftVersion = versionToken;
                draft.SnapshotJson = Serialize(snapshot);
                draft.LastOperationId = mutation.OperationId;
                draft.AuditFactId = auditId;
                await database.SaveChangesAsync(ct);
                await transaction.CommitAsync(ct);
                return DeserializeResult(operation.ResultJson);
            }
            catch (DbUpdateConcurrencyException)
            {
                await RollbackAndClearAsync(transaction, ct);
                return await ResolveConflictAsync(mutation.OperationId, fingerprint, definition.Id, ct);
            }
            catch (DbUpdateException error) when (IsOperationRace(error.InnerException))
            {
                await RollbackAndClearAsync(transaction, ct);
                return await ResolveConflictAsync(mutation.OperationId, fingerprint, definition.Id, ct);
            }
            catch (MySqlException error) when (IsOperationRace(error))
            {
                await RollbackAndClearAsync(transaction, ct);
                return await ResolveConflictAsync(mutation.OperationId, fingerprint, definition.Id, ct);
            }
        }
        catch (DbUpdateException error) when (error.InnerException is DbException failure)
        {
            ct.ThrowIfCancellationRequested();
            throw WorkflowFormDatabaseException.Safe(failure);
        }
        catch (DbException error)
        {
            ct.ThrowIfCancellationRequested();
            throw WorkflowFormDatabaseException.Safe(error);
        }
    }

    internal async Task<WorkflowPersistenceResult> PublishDefinitionAsync(
        WorkflowMutationContext mutation, Guid definitionId, string expectedDraftVersion,
        CompiledWorkflow validated, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(mutation);
        ArgumentNullException.ThrowIfNull(validated);
        if (!ValidMutation(mutation) || definitionId == Guid.Empty || !ValidExpectedVersion(expectedDraftVersion)
            || validated.Definition.TenantId != database.TenantId || validated.Definition.Id != definitionId)
            return Result(WorkflowPersistenceDecision.Invalid, definitionId);
        var fingerprint = Fingerprint("definition.publish", definitionId, expectedDraftVersion, validated);
        var replay = await ReadReplayAsync(mutation.OperationId, fingerprint, ct);
        if (replay is not null) return replay;
        try
        {
            await using var transaction = await database.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
            try
            {
                replay = await ReadReplayAsync(mutation.OperationId, fingerprint, ct);
                if (replay is not null) return await CommitAndReturnAsync(transaction, replay, ct);
                await FenceAsync("wf_definition_drafts", definitionId, ct);
                var draft = await database.DefinitionDrafts.SingleOrDefaultAsync(item => item.Id == definitionId, ct);
                if (draft is null || draft.DraftVersion != expectedDraftVersion)
                    return await CommitAndReturnAsync(transaction, Result(WorkflowPersistenceDecision.Conflict, definitionId), ct);

                var definition = Deserialize<WorkflowDefinition>(draft.SnapshotJson);
                if (Serialize(definition) != Serialize(validated.Definition)
                    || definition.Version != draft.PlannedVersion || definition.BusinessType != draft.BusinessType)
                    return await CommitAndReturnAsync(transaction, Result(WorkflowPersistenceDecision.Invalid, definitionId), ct);
                var schemaEntity = await database.SchemaVersions.AsNoTracking().SingleOrDefaultAsync(
                    item => item.Id == definition.SchemaId && item.Version == definition.SchemaVersion, ct);
                if (schemaEntity is null)
                    return await CommitAndReturnAsync(transaction, Result(WorkflowPersistenceDecision.NotFound, definitionId), ct);
                var publishedSchema = Deserialize<FormSchema>(schemaEntity.SnapshotJson);
                if (Serialize(publishedSchema) != Serialize(validated.Schema)
                    || publishedSchema.TenantId != database.TenantId
                    || publishedSchema.BusinessType != definition.BusinessType)
                    return await CommitAndReturnAsync(transaction, Result(WorkflowPersistenceDecision.Invalid, definitionId), ct);

                var latest = await database.DefinitionVersions.Where(item => item.Id == definitionId)
                    .OrderByDescending(item => item.Version).Select(item => (int?)item.Version).FirstOrDefaultAsync(ct);
                if (definition.Version != (latest ?? 0) + 1)
                    return await CommitAndReturnAsync(transaction, Result(WorkflowPersistenceDecision.Conflict, definitionId), ct);

                var versionToken = NewVersion();
                var auditId = Guid.NewGuid();
                var result = Result(WorkflowPersistenceDecision.Published, definitionId, versionToken, definition.Version);
                var operation = BuildOperation(mutation, "definition.publish", definitionId, fingerprint, auditId, result);
                database.DefinitionVersions.Add(new WorkflowDefinitionVersionEntity
                {
                    TenantId = database.TenantId,
                    Id = definitionId,
                    Version = definition.Version,
                    VersionToken = versionToken,
                    BusinessType = definition.BusinessType,
                    SchemaId = definition.SchemaId,
                    SchemaVersion = definition.SchemaVersion,
                    SnapshotJson = Serialize(definition),
                    OperationId = mutation.OperationId,
                    PublishedAt = clock.GetUtcNow()
                });
                database.AuditFacts.Add(BuildAudit(mutation, auditId, definitionId, "Definition", "DefinitionPublished",
                    draft.DraftVersion, versionToken));
                database.OperationReceipts.Add(operation);
                draft.LastOperationId = mutation.OperationId;
                draft.AuditFactId = auditId;
                database.DefinitionDrafts.Remove(draft);
                await database.SaveChangesAsync(ct);
                await transaction.CommitAsync(ct);
                return DeserializeResult(operation.ResultJson);
            }
            catch (DbUpdateConcurrencyException)
            {
                await RollbackAndClearAsync(transaction, ct);
                return await ResolveConflictAsync(mutation.OperationId, fingerprint, definitionId, ct);
            }
            catch (DbUpdateException error) when (IsOperationRace(error.InnerException))
            {
                await RollbackAndClearAsync(transaction, ct);
                return await ResolveConflictAsync(mutation.OperationId, fingerprint, definitionId, ct);
            }
            catch (MySqlException error) when (IsOperationRace(error))
            {
                await RollbackAndClearAsync(transaction, ct);
                return await ResolveConflictAsync(mutation.OperationId, fingerprint, definitionId, ct);
            }
        }
        catch (DbUpdateException error) when (error.InnerException is DbException failure)
        {
            ct.ThrowIfCancellationRequested();
            throw WorkflowFormDatabaseException.Safe(failure);
        }
        catch (DbException error)
        {
            ct.ThrowIfCancellationRequested();
            throw WorkflowFormDatabaseException.Safe(error);
        }
    }

    internal async Task<FormSchema?> ReadPublishedSchemaAsync(Guid id, int version, CancellationToken ct = default)
    {
        var json = await database.SchemaVersions.AsNoTracking()
            .Where(item => item.Id == id && item.Version == version)
            .Select(item => item.SnapshotJson).SingleOrDefaultAsync(ct);
        return json is null ? null : Deserialize<FormSchema>(json);
    }

    internal async Task<FormSchema?> ReadSchemaDraftAsync(Guid id, CancellationToken ct = default)
    {
        var json = await database.SchemaDrafts.AsNoTracking().Where(item => item.Id == id)
            .Select(item => item.SnapshotJson).SingleOrDefaultAsync(ct);
        return json is null ? null : Deserialize<FormSchema>(json);
    }

    internal async Task<WorkflowDefinition?> ReadPublishedDefinitionAsync(Guid id, int version, CancellationToken ct = default)
    {
        var json = await database.DefinitionVersions.AsNoTracking().Where(item => item.Id == id && item.Version == version)
            .Select(item => item.SnapshotJson).SingleOrDefaultAsync(ct);
        return json is null ? null : Deserialize<WorkflowDefinition>(json);
    }

    private async Task<WorkflowPersistenceResult?> ReadReplayAsync(Guid operationId, string fingerprint, CancellationToken ct)
    {
        var existing = await database.OperationReceipts.AsNoTracking()
            .SingleOrDefaultAsync(item => item.OperationId == operationId, ct);
        if (existing is null) return null;
        if (!CryptographicOperations.FixedTimeEquals(
                Convert.FromHexString(existing.Fingerprint), Convert.FromHexString(fingerprint)))
            return Result(WorkflowPersistenceDecision.IdempotencyConflict, existing.ResourceId);
        var original = DeserializeResult(existing.ResultJson);
        return original with { Decision = WorkflowPersistenceDecision.Replayed };
    }

    private async Task<WorkflowPersistenceResult> ResolveConflictAsync(
        Guid operationId, string fingerprint, Guid resourceId, CancellationToken ct)
    {
        database.ChangeTracker.Clear();
        return await ReadReplayAsync(operationId, fingerprint, ct)
            ?? Result(WorkflowPersistenceDecision.Conflict, resourceId);
    }

    private async Task FenceAsync(string table, Guid id, CancellationToken ct)
    {
        await using var command = database.Database.GetDbConnection().CreateCommand();
        command.Transaction = database.Database.CurrentTransaction!.GetDbTransaction();
        command.CommandText = $"SELECT DraftVersion FROM {table} WHERE TenantId = @tenant AND Id = @id FOR UPDATE";
        var tenant = command.CreateParameter();
        tenant.ParameterName = "@tenant";
        tenant.Value = database.TenantId.ToString("D");
        command.Parameters.Add(tenant);
        var resource = command.CreateParameter();
        resource.ParameterName = "@id";
        resource.Value = id.ToString("D");
        command.Parameters.Add(resource);
        _ = await command.ExecuteScalarAsync(ct);
    }

    private async Task<WorkflowPersistenceResult> CommitAndReturnAsync(
        Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction transaction,
        WorkflowPersistenceResult result, CancellationToken ct)
    {
        await transaction.CommitAsync(ct);
        return result;
    }

    private async Task RollbackAndClearAsync(
        Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction transaction, CancellationToken ct)
    {
        await transaction.RollbackAsync(ct);
        database.ChangeTracker.Clear();
    }

    private WorkflowOperationReceiptEntity BuildOperation(WorkflowMutationContext mutation, string action, Guid resourceId,
        string fingerprint, Guid auditId, WorkflowPersistenceResult result) => new()
        {
            TenantId = database.TenantId,
            OperationId = mutation.OperationId,
            ActorId = mutation.ActorId,
            CorrelationId = mutation.CorrelationId,
            Action = action,
            ResourceId = resourceId,
            Fingerprint = fingerprint,
            AuditFactId = auditId,
            ResultJson = Serialize(result),
            OccurredAt = clock.GetUtcNow()
        };

    private WorkflowAuditFactEntity BuildAudit(WorkflowMutationContext mutation, Guid auditId, Guid resourceId,
        string resourceKind, string action, string? before, string after) => new()
        {
            TenantId = database.TenantId,
            Id = auditId,
            OperationId = mutation.OperationId,
            ActorId = mutation.ActorId,
            CorrelationId = mutation.CorrelationId,
            ResourceId = resourceId,
            ResourceKind = resourceKind,
            Action = action,
            BeforeVersion = before,
            AfterVersion = after,
            OccurredAt = clock.GetUtcNow()
        };

    private static WorkflowPersistenceResult Result(WorkflowPersistenceDecision decision, Guid? id = null,
        string? version = null, int? publishedVersion = null) => new(decision, id, version, publishedVersion);

    private static bool ValidMutation(WorkflowMutationContext mutation) =>
        mutation.ActorId != Guid.Empty && mutation.CorrelationId != Guid.Empty && mutation.OperationId != Guid.Empty;

    private static bool ValidExpectedVersion(string? version) => version is null
        || Guid.TryParseExact(version, "D", out var value) && value != Guid.Empty;

    private static bool IsOperationRace(Exception? exception) =>
        exception is MySqlException { Number: 1062 or 1205 or 1213 };

    private static string NewVersion() => Guid.NewGuid().ToString("D");

    private static string Fingerprint(string action, Guid resourceId, string? expectedVersion, object? body)
    {
        var payload = JsonSerializer.Serialize(new { Owner, action, resourceId, expectedVersion, body }, JsonOptions);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));
    }

    private static string Serialize<T>(T value) => JsonSerializer.Serialize(value, JsonOptions);

    private static T Deserialize<T>(string value) =>
        JsonSerializer.Deserialize<T>(value, JsonOptions)
        ?? throw new InvalidOperationException("Stored WorkflowForm snapshot is invalid.");

    private static WorkflowPersistenceResult DeserializeResult(string value) => Deserialize<WorkflowPersistenceResult>(value);
}

internal sealed class WorkflowFormDatabaseException : Exception
{
    private WorkflowFormDatabaseException() : base("WorkflowForm persistence is unavailable.") { }
    internal static WorkflowFormDatabaseException Safe(DbException _) => new();
}
