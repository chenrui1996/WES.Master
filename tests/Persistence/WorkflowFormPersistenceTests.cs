using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Wes.WorkflowForm.Definitions;
using Wes.WorkflowForm.Features.Definitions;
using Wes.WorkflowForm.Infrastructure.Persistence;
using Xunit;

namespace Wes.Persistence.Tests;

[Collection("PlatformPersistence")]
[Trait("Provider", "MySQL")]
public sealed class WorkflowFormPersistenceTests : IAsyncLifetime
{
    private readonly Guid _tenant = Guid.NewGuid();
    private readonly Guid _otherTenant = Guid.NewGuid();
    private readonly Guid _actor = Guid.NewGuid();
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
    public async Task SchemaHistoryIsImmutableAcrossVersionsAndOperationReplayIsOwnerScoped()
    {
        var schemaId = Guid.NewGuid();
        var firstDraft = await SaveSchemaAsync(_tenant, schemaId, Schema(schemaId, "Quantity"));
        Assert.Equal(WorkflowPersistenceDecision.DraftSaved, firstDraft.Decision);
        var firstPublished = await PublishSchemaAsync(_tenant, schemaId, firstDraft.ResourceVersion!);
        Assert.Equal((WorkflowPersistenceDecision.Published, 1), (firstPublished.Decision, firstPublished.PublishedVersion));

        var replay = await PublishSchemaAsync(_tenant, schemaId, firstDraft.ResourceVersion!, firstPublishedOperation: true);
        Assert.Equal(WorkflowPersistenceDecision.Replayed, replay.Decision);
        Assert.Equal(firstPublished.ResourceVersion, replay.ResourceVersion);

        var secondDraft = await SaveSchemaAsync(_tenant, schemaId, Schema(schemaId, "Amount"));
        Assert.Equal(WorkflowPersistenceDecision.DraftSaved, secondDraft.Decision);
        Assert.NotEqual(firstDraft.ResourceVersion, secondDraft.ResourceVersion);
        var secondPublished = await PublishSchemaAsync(_tenant, schemaId, secondDraft.ResourceVersion!);
        Assert.Equal((WorkflowPersistenceDecision.Published, 2), (secondPublished.Decision, secondPublished.PublishedVersion));

        await using var tenant = Open(_tenant);
        var s1 = await new WorkflowDefinitionStore(tenant, TimeProvider.System).ReadPublishedSchemaAsync(schemaId, 1);
        var s2 = await new WorkflowDefinitionStore(tenant, TimeProvider.System).ReadPublishedSchemaAsync(schemaId, 2);
        Assert.Equal("Quantity", Assert.Single(s1!.Fields).Key);
        Assert.Equal("Amount", Assert.Single(s2!.Fields).Key);

        await using var other = Open(_otherTenant);
        Assert.Null(await new WorkflowDefinitionStore(other, TimeProvider.System).ReadPublishedSchemaAsync(schemaId, 1));
        Assert.Equal(2, await tenant.SchemaVersions.CountAsync());
        Assert.Equal(4, await tenant.AuditFacts.CountAsync());
        Assert.Equal(4, await tenant.OperationReceipts.CountAsync());
    }

    [Fact]
    public async Task DefinitionPublicationPinsSchemaVersionAndNeverReinterpretsPublishedSnapshot()
    {
        var schemaId = Guid.NewGuid();
        var schema = Schema(schemaId, "Quantity") with { TenantId = _tenant };
        var schemaDraft = await SaveSchemaAsync(_tenant, schemaId, schema);
        await PublishSchemaAsync(_tenant, schemaId, schemaDraft.ResourceVersion!);

        var definitionId = Guid.NewGuid();
        var definition = Definition(definitionId, schemaId, 1);
        var saved = await SaveDefinitionAsync(definition);
        Assert.Equal(WorkflowPersistenceDecision.DraftSaved, saved.Decision);
        var compiled = WorkflowValidation.Compile(definition, schema, new HashSet<Guid> { CandidateId });
        Assert.True(compiled.IsValid);
        var publishOperation = Guid.NewGuid();
        var published = await PublishDefinitionAsync(definitionId, saved.ResourceVersion!, compiled.Value!, publishOperation);
        Assert.Equal((WorkflowPersistenceDecision.Published, 1), (published.Decision, published.PublishedVersion));
        published = await PublishDefinitionAsync(definitionId, saved.ResourceVersion!, compiled.Value!, publishOperation);
        Assert.Equal(WorkflowPersistenceDecision.Replayed, published.Decision);

        var nextSchemaDraft = await SaveSchemaAsync(_tenant, schemaId, Schema(schemaId, "Amount"));
        await PublishSchemaAsync(_tenant, schemaId, nextSchemaDraft.ResourceVersion!);
        var definitionV2 = Definition(definitionId, schemaId, 2);
        var savedV2 = await SaveDefinitionAsync(definitionV2);
        await using var schemaDatabase = Open(_tenant);
        var schemaV2 = await new WorkflowDefinitionStore(schemaDatabase, TimeProvider.System).ReadPublishedSchemaAsync(schemaId, 2);
        Assert.NotNull(schemaV2);
        var compiledV2 = WorkflowValidation.Compile(definitionV2 with { Version = savedV2.PublishedVersion!.Value },
            schemaV2!, new HashSet<Guid> { CandidateId });
        Assert.True(compiledV2.IsValid);
        Assert.Equal(WorkflowPersistenceDecision.Published,
            (await PublishDefinitionAsync(definitionId, savedV2.ResourceVersion!, compiledV2.Value!)).Decision);

        var historical = await ReadDefinitionAndSchemaAsync(definitionId, 1);
        Assert.Equal((1, "Quantity"), (historical.Definition.Version, Assert.Single(historical.Schema.Fields).Key));
        Assert.Equal((schemaId, 1), (historical.Definition.SchemaId, historical.Definition.SchemaVersion));
        var current = await ReadDefinitionAndSchemaAsync(definitionId, 2);
        Assert.Equal((2, "Amount"), (current.Definition.Version, Assert.Single(current.Schema.Fields).Key));
    }

    [Fact]
    public async Task ExpectedVersionConflictDoesNotChangeDraftOrWriteAudit()
    {
        var schemaId = Guid.NewGuid();
        var created = await SaveSchemaAsync(_tenant, schemaId, Schema(schemaId, "Quantity"));
        var updated = await SaveSchemaAsync(_tenant, schemaId, Schema(schemaId, "Amount"), created.ResourceVersion);
        var conflict = await SaveSchemaAsync(_tenant, schemaId, Schema(schemaId, "Weight"), created.ResourceVersion,
            operationId: Guid.NewGuid());

        Assert.Equal(WorkflowPersistenceDecision.DraftSaved, updated.Decision);
        Assert.Equal(WorkflowPersistenceDecision.Conflict, conflict.Decision);
        await using var database = Open(_tenant);
        var draft = await new WorkflowDefinitionStore(database, TimeProvider.System).ReadSchemaDraftAsync(schemaId);
        Assert.Equal("Amount", Assert.Single(draft!.Fields).Key);
        Assert.Equal(2, await database.AuditFacts.CountAsync());
        Assert.Equal(2, await database.OperationReceipts.CountAsync());
    }

    [Fact]
    [Trait("CaseId", "TC-T12-PUBLISH-01")]
    public async Task PublicationAcceptsCompilerCandidateDeduplicationAndReplaysOriginalVersion()
    {
        var schemaId = Guid.NewGuid();
        var schema = Schema(schemaId, "Quantity") with { TenantId = _tenant };
        var schemaDraft = await SaveSchemaAsync(_tenant, schemaId, schema);
        await PublishSchemaAsync(_tenant, schemaId, schemaDraft.ResourceVersion!);
        var definitionId = Guid.NewGuid();
        var definition = Definition(definitionId, schemaId, 1);
        var nodes = definition.Nodes.ToArray();
        nodes[1] = nodes[1] with { Candidates = [CandidateId, CandidateId] };
        definition = definition with { Nodes = nodes };
        var saved = await SaveDefinitionAsync(definition);
        Assert.Equal(WorkflowPersistenceDecision.DraftSaved, saved.Decision);
        var compiled = WorkflowValidation.Compile(definition, schema, new HashSet<Guid> { CandidateId });
        Assert.True(compiled.IsValid);
        var operationId = Guid.NewGuid();
        var published = await PublishDefinitionAsync(definitionId, saved.ResourceVersion!, compiled.Value!, operationId);
        Assert.Equal(WorkflowPersistenceDecision.Published, published.Decision);
        var replay = await PublishDefinitionAsync(definitionId, saved.ResourceVersion!, compiled.Value!, operationId);
        Assert.Equal(WorkflowPersistenceDecision.Replayed, replay.Decision);
        Assert.Equal(published.ResourceVersion, replay.ResourceVersion);
        await using var verify = Open(_tenant);
        var historical = await new WorkflowDefinitionStore(verify, TimeProvider.System).ReadPublishedDefinitionAsync(definitionId, 1);
        Assert.Equal(CandidateId, Assert.Single(historical!.Nodes[1].Candidates));
        Assert.Empty(await verify.DefinitionDrafts.ToListAsync());
        Assert.Single(await verify.DefinitionVersions.ToListAsync());
        Assert.Equal(4, await verify.OperationReceipts.CountAsync());
        Assert.Equal(4, await verify.AuditFacts.CountAsync());
    }

    [Theory]
    [InlineData("cycle")]
    [InlineData("empty-candidates")]
    [InlineData("unknown-node")]
    [Trait("CaseId", "TC-T12-PUBLISH-02")]
    public async Task PublicationRejectsInvalidGraphEvenWithConstructedCompiledRecord(string variant)
    {
        var schemaId = Guid.NewGuid();
        var schema = Schema(schemaId, "Quantity") with { TenantId = _tenant };
        var schemaDraft = await SaveSchemaAsync(_tenant, schemaId, schema);
        await PublishSchemaAsync(_tenant, schemaId, schemaDraft.ResourceVersion!);
        var definitionId = Guid.NewGuid();
        var definition = Definition(definitionId, schemaId, 1);
        var nodes = definition.Nodes.ToArray();
        nodes[1] = variant switch
        {
            "cycle" => nodes[1] with { Edges = [new("start")] },
            "empty-candidates" => nodes[1] with { Candidates = [] },
            _ => nodes[1] with { Kind = (NodeKind)99 },
        };
        definition = definition with { Nodes = nodes };
        var saved = await SaveDefinitionAsync(definition);
        Assert.Equal(WorkflowPersistenceDecision.DraftSaved, saved.Decision);
        var result = await PublishDefinitionAsync(definitionId, saved.ResourceVersion!, new(definition, schema));
        Assert.Equal(WorkflowPersistenceDecision.Invalid, result.Decision);
        await using var verify = Open(_tenant);
        Assert.Empty(await verify.DefinitionVersions.ToListAsync());
        var remaining = Assert.Single(await verify.DefinitionDrafts.ToListAsync());
        Assert.Equal(saved.ResourceVersion, remaining.DraftVersion);
        Assert.Equal(3, await verify.OperationReceipts.CountAsync());
        Assert.Equal(3, await verify.AuditFacts.CountAsync());
    }

    [Theory]
    [InlineData("candidate")]
    [InlineData("mode")]
    [InlineData("schema")]
    [Trait("CaseId", "TC-T12-PUBLISH-03")]
    public async Task PublicationRejectsDifferentCompiledSnapshotWithoutDeletingDraft(string variant)
    {
        var schemaId = Guid.NewGuid();
        var schema = Schema(schemaId, "Quantity") with { TenantId = _tenant };
        var schemaDraft = await SaveSchemaAsync(_tenant, schemaId, schema);
        await PublishSchemaAsync(_tenant, schemaId, schemaDraft.ResourceVersion!);
        var definitionId = Guid.NewGuid();
        var definition = Definition(definitionId, schemaId, 1);
        var saved = await SaveDefinitionAsync(definition);
        var nodes = definition.Nodes.ToArray();
        var otherCandidate = Guid.NewGuid();
        if (variant == "candidate") nodes[1] = nodes[1] with { Candidates = [otherCandidate] };
        if (variant == "mode") nodes[1] = nodes[1] with { Mode = ApprovalMode.Any };
        var suppliedSchema = variant == "schema" ? schema with { Fields = [schema.Fields[0] with { MaxLength = 32 }] } : schema;
        var compiled = WorkflowValidation.Compile(definition with { Nodes = nodes }, suppliedSchema,
            new HashSet<Guid> { CandidateId, otherCandidate });
        Assert.True(compiled.IsValid);
        Assert.Equal(WorkflowPersistenceDecision.Invalid,
            (await PublishDefinitionAsync(definitionId, saved.ResourceVersion!, compiled.Value!)).Decision);
        await using var verify = Open(_tenant);
        Assert.Empty(await verify.DefinitionVersions.ToListAsync());
        Assert.Equal(saved.ResourceVersion, Assert.Single(await verify.DefinitionDrafts.ToListAsync()).DraftVersion);
        Assert.Equal(3, await verify.OperationReceipts.CountAsync());
        Assert.Equal(3, await verify.AuditFacts.CountAsync());
    }

    [Fact]
    public async Task SameOperationWithDifferentPayloadConflictsAndDoesNotMutateDraft()
    {
        var schemaId = Guid.NewGuid();
        var operationId = Guid.NewGuid();
        var first = await SaveSchemaAsync(_tenant, schemaId, Schema(schemaId, "Quantity"), operationId: operationId);
        var replay = await SaveSchemaAsync(_tenant, schemaId, Schema(schemaId, "Quantity"), operationId: operationId);
        var mismatch = await SaveSchemaAsync(_tenant, schemaId, Schema(schemaId, "Amount"), operationId: operationId);

        Assert.Equal(WorkflowPersistenceDecision.DraftSaved, first.Decision);
        Assert.Equal(WorkflowPersistenceDecision.Replayed, replay.Decision);
        Assert.Equal(first.ResourceVersion, replay.ResourceVersion);
        Assert.Equal(WorkflowPersistenceDecision.IdempotencyConflict, mismatch.Decision);
        await using var database = Open(_tenant);
        Assert.Equal("Quantity", Assert.Single((await new WorkflowDefinitionStore(database, TimeProvider.System)
            .ReadSchemaDraftAsync(schemaId))!.Fields).Key);
        Assert.Single(await database.AuditFacts.ToListAsync());
        Assert.Single(await database.OperationReceipts.ToListAsync());
    }

    [Fact]
    public async Task ConcurrentPublicationOfOneDraftHasOneWinnerAndOneAuditFact()
    {
        var schemaId = Guid.NewGuid();
        var draft = await SaveSchemaAsync(_tenant, schemaId, Schema(schemaId, "Quantity"));
        var results = await Task.WhenAll(
            PublishSchemaAsync(_tenant, schemaId, draft.ResourceVersion!, operationId: Guid.NewGuid()),
            PublishSchemaAsync(_tenant, schemaId, draft.ResourceVersion!, operationId: Guid.NewGuid()));

        Assert.Single(results, item => item.Decision == WorkflowPersistenceDecision.Published);
        Assert.Single(results, item => item.Decision == WorkflowPersistenceDecision.Conflict);
        await using var database = Open(_tenant);
        Assert.Single(await database.SchemaVersions.ToListAsync());
        Assert.Equal(2, await database.AuditFacts.CountAsync());
    }

    [Fact]
    public async Task ConcurrentSameOperationSaveHasOneFactAndReplaysSameResult()
    {
        var schemaId = Guid.NewGuid();
        var operationId = Guid.NewGuid();
        var results = await Task.WhenAll(
            SaveSchemaAsync(_tenant, schemaId, Schema(schemaId, "Quantity"), operationId: operationId),
            SaveSchemaAsync(_tenant, schemaId, Schema(schemaId, "Quantity"), operationId: operationId));

        Assert.Single(results, item => item.Decision == WorkflowPersistenceDecision.DraftSaved);
        Assert.Single(results, item => item.Decision == WorkflowPersistenceDecision.Replayed);
        Assert.Equal(results[0].ResourceVersion, results[1].ResourceVersion);
        await using var database = Open(_tenant);
        Assert.Single(await database.SchemaDrafts.ToListAsync());
        Assert.Single(await database.AuditFacts.ToListAsync());
        Assert.Single(await database.OperationReceipts.ToListAsync());
    }

    [Fact]
    public async Task PublishedSnapshotAndAuditFactRejectMutation()
    {
        var schemaId = Guid.NewGuid();
        var draft = await SaveSchemaAsync(_tenant, schemaId, Schema(schemaId, "Quantity"));
        await PublishSchemaAsync(_tenant, schemaId, draft.ResourceVersion!);

        await using var database = Open(_tenant);
        var version = await database.SchemaVersions.SingleAsync();
        version.SnapshotJson = "{}";
        await Assert.ThrowsAsync<InvalidOperationException>(() => database.SaveChangesAsync());
        database.ChangeTracker.Clear();
        var audit = await database.AuditFacts.FirstAsync();
        audit.Action = "Changed";
        await Assert.ThrowsAsync<InvalidOperationException>(() => database.SaveChangesAsync());
    }

    [Theory]
    [InlineData(null)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SaveChangesOverloadsEnforcePublishedAndDraftAuditGuards(bool? acceptAllChangesOnSuccess)
    {
        var schemaId = Guid.NewGuid();
        var draft = await SaveSchemaAsync(_tenant, schemaId, Schema(schemaId, "Quantity"));
        await PublishSchemaAsync(_tenant, schemaId, draft.ResourceVersion!);

        await using var database = Open(_tenant);
        var published = await database.SchemaVersions.SingleAsync(item => item.Id == schemaId);
        published.SnapshotJson = "{}";
        Assert.Throws<InvalidOperationException>(() =>
        {
            if (acceptAllChangesOnSuccess.HasValue) database.SaveChanges(acceptAllChangesOnSuccess.Value);
            else database.SaveChanges();
        });

        database.ChangeTracker.Clear();
        database.SchemaDrafts.Add(new WorkflowSchemaDraftEntity
        {
            TenantId = _tenant,
            Id = Guid.NewGuid(),
            BusinessType = "Order",
            PlannedVersion = 1,
            DraftVersion = Guid.NewGuid().ToString("D"),
            SnapshotJson = "{}",
            LastOperationId = Guid.NewGuid(),
            AuditFactId = Guid.NewGuid()
        });
        Assert.Throws<InvalidOperationException>(() =>
        {
            if (acceptAllChangesOnSuccess.HasValue) database.SaveChanges(acceptAllChangesOnSuccess.Value);
            else database.SaveChanges();
        });
    }

    [Theory]
    [InlineData(null)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SaveChangesAsyncOverloadsEnforcePublishedAndDraftAuditGuards(bool? acceptAllChangesOnSuccess)
    {
        var schemaId = Guid.NewGuid();
        var draft = await SaveSchemaAsync(_tenant, schemaId, Schema(schemaId, "Quantity"));
        await PublishSchemaAsync(_tenant, schemaId, draft.ResourceVersion!);

        await using var database = Open(_tenant);
        var published = await database.SchemaVersions.SingleAsync(item => item.Id == schemaId);
        published.SnapshotJson = "{}";
        await Assert.ThrowsAsync<InvalidOperationException>(() => acceptAllChangesOnSuccess.HasValue
            ? database.SaveChangesAsync(acceptAllChangesOnSuccess.Value, CancellationToken.None)
            : database.SaveChangesAsync(CancellationToken.None));

        database.ChangeTracker.Clear();
        database.SchemaDrafts.Add(new WorkflowSchemaDraftEntity
        {
            TenantId = _tenant,
            Id = Guid.NewGuid(),
            BusinessType = "Order",
            PlannedVersion = 1,
            DraftVersion = Guid.NewGuid().ToString("D"),
            SnapshotJson = "{}",
            LastOperationId = Guid.NewGuid(),
            AuditFactId = Guid.NewGuid()
        });
        await Assert.ThrowsAsync<InvalidOperationException>(() => acceptAllChangesOnSuccess.HasValue
            ? database.SaveChangesAsync(acceptAllChangesOnSuccess.Value, CancellationToken.None)
            : database.SaveChangesAsync(CancellationToken.None));
    }

    [Fact]
    public async Task InterruptedSaveRollsBackDraftReceiptAndAuditTogether()
    {
        var schemaId = Guid.NewGuid();
        await using (var database = Open(_tenant, new AfterSaveFault()))
        {
            var store = new WorkflowDefinitionStore(database, TimeProvider.System);
            await Assert.ThrowsAsync<InvalidOperationException>(() => store.SaveSchemaDraftAsync(
                new(_actor, Guid.NewGuid(), Guid.NewGuid()), Schema(schemaId, "Quantity") with { TenantId = _tenant }));
        }

        await using var verify = Open(_tenant);
        Assert.Empty(await verify.SchemaDrafts.ToListAsync());
        Assert.Empty(await verify.OperationReceipts.ToListAsync());
        Assert.Empty(await verify.AuditFacts.ToListAsync());
    }

    [Fact]
    public async Task SameOperationCanBeUsedByAnotherTenantWithoutCrossTenantReadOrMutation()
    {
        var schemaId = Guid.NewGuid();
        var operationId = Guid.NewGuid();
        var first = await SaveSchemaAsync(_tenant, schemaId, Schema(schemaId, "Quantity"), operationId: operationId);
        var other = await SaveSchemaAsync(_otherTenant, schemaId, Schema(schemaId, "Amount"), operationId: operationId);

        Assert.Equal(WorkflowPersistenceDecision.DraftSaved, first.Decision);
        Assert.Equal(WorkflowPersistenceDecision.DraftSaved, other.Decision);
        await using var firstDb = Open(_tenant);
        await using var otherDb = Open(_otherTenant);
        Assert.Equal("Quantity", Assert.Single((await new WorkflowDefinitionStore(firstDb, TimeProvider.System)
            .ReadSchemaDraftAsync(schemaId))!.Fields).Key);
        Assert.Equal("Amount", Assert.Single((await new WorkflowDefinitionStore(otherDb, TimeProvider.System)
            .ReadSchemaDraftAsync(schemaId))!.Fields).Key);
    }

    public async Task DisposeAsync()
    {
        if (!_ready) return;
        foreach (var tenantId in new[] { _tenant, _otherTenant })
        {
            await using var database = Open(tenantId);
            await database.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM wf_definition_versions WHERE TenantId = {tenantId.ToString("D")}");
            await database.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM wf_schema_versions WHERE TenantId = {tenantId.ToString("D")}");
            await database.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM wf_definition_drafts WHERE TenantId = {tenantId.ToString("D")}");
            await database.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM wf_schema_drafts WHERE TenantId = {tenantId.ToString("D")}");
            await database.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM wf_operation_receipts WHERE TenantId = {tenantId.ToString("D")}");
            await database.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM wf_audit_facts WHERE TenantId = {tenantId.ToString("D")}");
        }
    }

    private static readonly Guid CandidateId = Guid.Parse("da5059c0-6c8c-4826-a21f-625a11634c3c");

    private static FormSchema Schema(Guid id, string fieldKey) =>
        new(Guid.Empty, id, 1, "Order", [new FormField(fieldKey, FieldKind.Text, "Order.label", true, true, MaxLength: 64)]);

    private WorkflowDefinition Definition(Guid id, Guid schemaId, int schemaVersion) =>
        new(_tenant, id, 1, "Order", 0, schemaId, schemaVersion,
        [
            new("start", NodeKind.Start, [new("approval")], []),
            new("approval", NodeKind.Approval, [new("end")], [CandidateId]),
            new("end", NodeKind.End, [], [])
        ]);

    private async Task<WorkflowPersistenceResult> SaveSchemaAsync(Guid tenant, Guid id, FormSchema schema,
        string? expectedVersion = null, Guid? operationId = null)
    {
        await using var database = Open(tenant);
        return await new WorkflowDefinitionStore(database, TimeProvider.System).SaveSchemaDraftAsync(
            new(_actor, Guid.NewGuid(), operationId ?? Guid.NewGuid()), schema with { TenantId = tenant },
            expectedVersion);
    }

    private async Task<WorkflowPersistenceResult> PublishSchemaAsync(Guid tenant, Guid id, string expectedVersion,
        Guid? operationId = null, bool firstPublishedOperation = false)
    {
        await using var database = Open(tenant);
        var operation = firstPublishedOperation ? await FindPublishedSchemaOperationAsync(database, id) : null;
        return await new WorkflowDefinitionStore(database, TimeProvider.System).PublishSchemaAsync(
            new(_actor, Guid.NewGuid(), operation ?? operationId ?? Guid.NewGuid()), id, expectedVersion);
    }

    private async Task<Guid?> FindPublishedSchemaOperationAsync(WorkflowFormDbContext database, Guid id) =>
        await database.SchemaVersions.Where(item => item.Id == id).OrderBy(item => item.Version)
            .Select(item => (Guid?)item.OperationId).FirstOrDefaultAsync();

    private async Task<WorkflowPersistenceResult> SaveDefinitionAsync(WorkflowDefinition definition)
    {
        await using var database = Open(_tenant);
        return await new WorkflowDefinitionStore(database, TimeProvider.System).SaveDefinitionDraftAsync(
            new(_actor, Guid.NewGuid(), Guid.NewGuid()), definition);
    }

    private async Task<WorkflowPersistenceResult> PublishDefinitionAsync(Guid id, string expectedVersion,
        CompiledWorkflow compiled, Guid? operationId = null)
    {
        await using var database = Open(_tenant);
        return await new WorkflowDefinitionStore(database, TimeProvider.System).PublishDefinitionAsync(
            new(_actor, Guid.NewGuid(), operationId ?? Guid.NewGuid()), id, expectedVersion, compiled);
    }

    private async Task<(WorkflowDefinition Definition, FormSchema Schema)> ReadDefinitionAndSchemaAsync(Guid id, int version)
    {
        await using var database = Open(_tenant);
        var store = new WorkflowDefinitionStore(database, TimeProvider.System);
        var definition = await store.ReadPublishedDefinitionAsync(id, version);
        Assert.NotNull(definition);
        var schema = await store.ReadPublishedSchemaAsync(definition!.SchemaId, definition.SchemaVersion);
        Assert.NotNull(schema);
        return (definition, schema!);
    }

    private WorkflowFormDbContext Open(Guid tenant, IInterceptor? interceptor = null)
    {
        MySqlTestConnection.ValidateTarget(_connection);
        var builder = new DbContextOptionsBuilder<WorkflowFormDbContext>().UseMySQL(_connection,
            mysql => mysql.MigrationsHistoryTable("__WesWorkflowFormMigrations"));
        if (interceptor is not null) builder.AddInterceptors(interceptor);
        var options = builder.Options;
        return new(options, tenant);
    }

    private sealed class AfterSaveFault : SaveChangesInterceptor
    {
        public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromException<int>(new InvalidOperationException("Controlled transaction interruption."));
    }
}
