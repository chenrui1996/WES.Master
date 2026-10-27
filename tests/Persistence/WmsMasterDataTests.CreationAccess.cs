using System.Collections.Immutable;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Wes.Platform.Domain;
using Wes.Wms;
using Wes.Wms.Features.MasterData;
using Wes.Wms.Features.Ledger;
using Xunit;

namespace Wes.Persistence.Tests;

public sealed partial class WmsMasterDataTests
{
    [Theory]
    [InlineData(DataScope.Own)]
    [InlineData(DataScope.Departments)]
    [InlineData(DataScope.All)]
    public async Task CurrentAuthorityCreatesDisabledDefinitionsUsingOnlyTheTrustedActor(DataScope scope)
    {
        var subject = await SeedWriterAsync(scope);
        await using var platform = OpenAuthority();
        MasterDataWriteResult warehouse;
        await using (var warehouseWriter = Writer(platform, new Schema(LocationSchemaDecision.Valid)))
            warehouse = await warehouseWriter.CreateAsync(subject, new(Guid.NewGuid(), Guid.NewGuid(), "wh-new", "Current warehouse"));
        Assert.Equal(MasterDataWriteDecision.Created, warehouse.Decision);
        var operation = Guid.NewGuid();
        var location = new MasterDataCreation(operation, Guid.NewGuid(), "a-new", "Current location", warehouse.Id, Guid.NewGuid(), "V1");
        await using var writer = Writer(platform, new Schema(LocationSchemaDecision.Valid));
        var created = await writer.CreateAsync(subject, location);
        Assert.Equal(MasterDataWriteDecision.Created, created.Decision);
        await using var replayWriter = Writer(platform, new Schema(LocationSchemaDecision.Unavailable));
        var replay = await replayWriter.CreateAsync(subject, location with { CorrelationId = Guid.NewGuid() });
        Assert.Equal(MasterDataWriteDecision.Replayed, replay.Decision);
        Assert.Equal((created.Id, created.Version), (replay.Id, replay.Version));
        await using var verify = Open(_tenant);
        Assert.All(await verify.MasterDataChanges.ToListAsync(), change => Assert.Equal(_actor, change.ActorId));
        Assert.False((await verify.Warehouses.SingleAsync()).Enabled);
        Assert.False((await verify.Locations.SingleAsync()).Enabled);
        Assert.Empty(await verify.Entries.ToListAsync());
    }

    [Theory]
    [InlineData("read-only")]
    [InlineData("field")]
    [InlineData("department")]
    [InlineData("wrong-case")]
    public async Task CurrentCreationRejectsIncompleteOrOutOfScopeAuthorityWithoutEffects(string invalid)
    {
        var subject = await SeedWriterAsync(DataScope.Own);
        await using (var update = OpenAuthority())
        {
            var role = await update.Roles.SingleAsync();
            var rules = role.Rules.Where(rule => rule.Permission != "wms.warehouses.write").ToList();
            if (invalid != "read-only") rules.Add(new("wms.warehouses.write",
                invalid == "department" ? DataScope.Departments : DataScope.Own,
                invalid == "department" ? ImmutableHashSet.Create(Guid.NewGuid()) : ImmutableHashSet<Guid>.Empty,
                invalid == "wrong-case" ? ImmutableHashSet.Create("code", "Name")
                    : invalid == "department" ? ImmutableHashSet.Create("Code", "Name") : ImmutableHashSet.Create("Code")));
            role.Change(role.Version, true, rules);
            await update.SaveChangesAsync();
        }
        await using var platform = OpenAuthority();
        await using var writer = Writer(platform, new Schema(LocationSchemaDecision.Valid));
        var denied = await writer.CreateAsync(subject, new(Guid.NewGuid(), Guid.NewGuid(), "WH-NEW", "New"));
        Assert.Equal(MasterDataWriteDecision.Forbidden, denied.Decision);
        Assert.Null(denied.Id); Assert.Null(denied.Version);
        await using var verify = Open(_tenant);
        Assert.Empty(await verify.Warehouses.ToListAsync());
        Assert.Empty(await verify.MasterDataChanges.ToListAsync());
    }

    [Theory]
    [InlineData("session")]
    [InlineData("role")]
    [InlineData("membership")]
    [InlineData("field")]
    public async Task CurrentCreationReplaysCannotBypassRevokedLiveAuthority(string revoke)
    {
        var subject = await SeedWriterAsync(DataScope.Own);
        var command = new MasterDataCreation(Guid.NewGuid(), Guid.NewGuid(), "WH-NEW", "New");
        await using var platform = OpenAuthority();
        await using (var writer = Writer(platform, new Schema(LocationSchemaDecision.Valid)))
            Assert.Equal(MasterDataWriteDecision.Created, (await writer.CreateAsync(subject, command)).Decision);
        await using (var update = OpenAuthority())
        {
            if (revoke == "session") (await update.Sessions.SingleAsync()).Revoke(_clock.GetUtcNow());
            else if (revoke == "membership") update.Memberships.Remove(await update.Memberships.SingleAsync());
            else
            {
                var role = await update.Roles.SingleAsync();
                role.Change(role.Version, revoke != "role", revoke == "field"
                    ? role.Rules.Select(rule => rule with { Fields = rule.Fields.Remove("Name") }) : role.Rules);
            }
            await update.SaveChangesAsync();
        }
        await using var replay = Writer(platform, new Schema(LocationSchemaDecision.Valid));
        var denied = await replay.CreateAsync(subject, command);
        Assert.Equal(MasterDataWriteDecision.Forbidden, denied.Decision);
        Assert.Null(denied.Id); Assert.Null(denied.Version);
        await using var verify = Open(_tenant);
        Assert.Single(await verify.Warehouses.ToListAsync());
        Assert.Single(await verify.MasterDataChanges.ToListAsync());
    }

    [Theory]
    [InlineData(LocationSchemaDecision.Rejected)]
    [InlineData(LocationSchemaDecision.Unavailable)]
    public async Task CurrentLocationCreationCannotIgnoreMissingSchemaEvidence(LocationSchemaDecision evidence)
    {
        var subject = await SeedWriterAsync(DataScope.Own);
        var warehouse = await ExecuteAsync(Command());
        await using var platform = OpenAuthority();
        await using var writer = Writer(platform, new Schema(evidence));
        var result = await writer.CreateAsync(subject,
            new(Guid.NewGuid(), Guid.NewGuid(), "A-NEW", "New", warehouse.Id, Guid.NewGuid(), "v1"));
        Assert.Equal(evidence == LocationSchemaDecision.Rejected ? MasterDataWriteDecision.SchemaRejected : MasterDataWriteDecision.SchemaUnavailable, result.Decision);
        Assert.Null(result.Id); Assert.Null(result.Version);
        await using var verify = Open(_tenant);
        Assert.Empty(await verify.Locations.ToListAsync());
        Assert.Single(await verify.MasterDataChanges.ToListAsync());
    }

    [Fact]
    public async Task CurrentLocationCreationRequiresParentWarehouseAuthorityBeforeInvokingSchema()
    {
        var subject = await SeedWriterAsync(DataScope.Own);
        var warehouse = await ExecuteAsync(Command() with { ActorId = _creator });
        var schema = new Schema(LocationSchemaDecision.Valid);
        await using var platform = OpenAuthority();
        await using var writer = Writer(platform, schema);
        var result = await writer.CreateAsync(subject,
            new(Guid.NewGuid(), Guid.NewGuid(), "A-NEW", "New", warehouse.Id, Guid.NewGuid(), "v1"));
        Assert.Equal(MasterDataWriteDecision.Forbidden, result.Decision);
        Assert.Empty(schema.Inputs);
        await using var verify = Open(_tenant);
        Assert.Empty(await verify.Locations.ToListAsync());
    }

    [Theory]
    [InlineData("tenant")]
    [InlineData("empty-actor")]
    [InlineData("empty-session")]
    [InlineData("unknown-actor")]
    [InlineData("unknown-session")]
    public async Task CurrentCreationRejectsUntrustedOrInactiveSubjectWithoutEffects(string invalid)
    {
        var subject = await SeedWriterAsync(DataScope.All);
        subject = invalid switch
        {
            "tenant" => subject with { TenantId = _other },
            "empty-actor" => subject with { ActorId = Guid.Empty },
            "empty-session" => subject with { SessionId = Guid.Empty },
            "unknown-actor" => subject with { ActorId = Guid.NewGuid() },
            _ => subject with { SessionId = Guid.NewGuid() }
        };
        await using var platform = OpenAuthority();
        await using var writer = Writer(platform, new Schema(LocationSchemaDecision.Valid));
        var denied = await writer.CreateAsync(subject, new(Guid.NewGuid(), Guid.NewGuid(), "NEW", "New"));
        Assert.Equal(MasterDataWriteDecision.Forbidden, denied.Decision);
        Assert.Null(denied.Id); Assert.Null(denied.Version);
        await using var verify = Open(_tenant);
        Assert.Empty(await verify.MasterDataChanges.ToListAsync());
    }

    [Theory]
    [InlineData("operation")]
    [InlineData("correlation")]
    [InlineData("code")]
    [InlineData("name")]
    [InlineData("warehouse")]
    [InlineData("pack")]
    public async Task CurrentCreationRejectsInvalidInputBeforeRequestingAuthority(string invalid)
    {
        var request = new MasterDataCreation(Guid.NewGuid(), Guid.NewGuid(), "NEW", "New");
        request = invalid switch
        {
            "operation" => request with { OperationId = Guid.Empty },
            "correlation" => request with { CorrelationId = Guid.Empty },
            "code" => request with { Code = "BAD CODE" },
            "name" => request with { Name = " New " },
            "warehouse" => request with { WarehouseId = Guid.Empty, PackId = Guid.NewGuid(), SchemaVersion = "v1" },
            _ => request with { PackId = Guid.NewGuid() }
        };
        await using var writer = new WmsMasterDataWriter(_connection, _tenant, new ControlledAuthority(null!),
            new Schema(LocationSchemaDecision.Valid), _clock, NullLoggerFactory.Instance);
        var result = await writer.CreateAsync(new(_tenant, _actor, Guid.NewGuid()), request);
        Assert.Equal(MasterDataWriteDecision.Invalid, result.Decision);
        Assert.Null(result.Id); Assert.Null(result.Version);
    }

    [Fact]
    public async Task CurrentLocationCreationCannotTargetAnActualForeignTenantWarehouse()
    {
        var subject = await SeedWriterAsync(DataScope.All);
        Guid parent;
        await using (var foreign = Open(_other))
            parent = (await new CreateMasterData(foreign, _clock, new Schema(LocationSchemaDecision.Unavailable)).ExecuteAsync(Command())).Id!.Value;
        var schema = new Schema(LocationSchemaDecision.Valid);
        await using var platform = OpenAuthority();
        await using var writer = Writer(platform, schema);
        var result = await writer.CreateAsync(subject, new(Guid.NewGuid(), Guid.NewGuid(), "NEW", "New", parent, Guid.NewGuid(), "v1"));
        Assert.Equal(MasterDataWriteDecision.NotFound, result.Decision);
        Assert.Null(result.Id);
        Assert.Empty(schema.Inputs);
        await using var verify = Open(_tenant);
        Assert.Empty(await verify.MasterDataChanges.ToListAsync());
    }

    [Fact]
    public async Task CurrentLocationCreationCannotUseWarehouseWriteInPlaceOfLocationWrite()
    {
        var subject = await SeedWriterAsync(DataScope.All);
        var parent = await ExecuteAsync(Command());
        await using (var update = OpenAuthority())
        {
            var role = await update.Roles.SingleAsync();
            role.Change(role.Version, true, role.Rules.Where(rule => rule.Permission != "wms.locations.write"));
            await update.SaveChangesAsync();
        }
        var schema = new Schema(LocationSchemaDecision.Valid);
        await using var platform = OpenAuthority();
        await using var writer = Writer(platform, schema);
        var result = await writer.CreateAsync(subject, new(Guid.NewGuid(), Guid.NewGuid(), "NEW", "New", parent.Id, Guid.NewGuid(), "v1"));
        Assert.Equal(MasterDataWriteDecision.Forbidden, result.Decision);
        Assert.Empty(schema.Inputs);
    }

    [Fact]
    public async Task CurrentCreationNormalizesAuthorityFieldComparisonInsteadOfTrustingItsSetComparer()
    {
        var fields = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "code", "Name" };
        await using var writer = new WmsMasterDataWriter(_connection, _tenant, new ControlledAuthority(fields),
            new Schema(LocationSchemaDecision.Valid), _clock, NullLoggerFactory.Instance);
        var result = await writer.CreateAsync(new(_tenant, _actor, Guid.NewGuid()), new(Guid.NewGuid(), Guid.NewGuid(), "NEW", "New"));
        Assert.Equal(MasterDataWriteDecision.Forbidden, result.Decision);
        await using var verify = Open(_tenant);
        Assert.Empty(await verify.MasterDataChanges.ToListAsync());
    }

    [Fact]
    public async Task CurrentCreationCannotTreatNullAuthorityAsAnEmptyOrSuccessfulDecision()
    {
        await using var writer = new WmsMasterDataWriter(_connection, _tenant, new ControlledAuthority(null!),
            new Schema(LocationSchemaDecision.Valid), _clock, NullLoggerFactory.Instance);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            writer.CreateAsync(new(_tenant, _actor, Guid.NewGuid()), new(Guid.NewGuid(), Guid.NewGuid(), "NEW", "New")));
    }

    [Fact]
    public async Task CurrentCreationRequiresAnExplicitSchemaDependencyEvenForTheWriterAssembly()
    {
        Assert.Throws<ArgumentNullException>(() => new WmsMasterDataWriter(_connection, _tenant,
            new ControlledAuthority(new HashSet<string> { "Code", "Name" }), null!, _clock, NullLoggerFactory.Instance));
        await using var verify = Open(_tenant);
        Assert.Empty(await verify.MasterDataChanges.ToListAsync());
    }

    [Fact]
    public async Task CurrentCreationRequiresAFreshOwnerAfterACommittedCommand()
    {
        var subject = await SeedWriterAsync(DataScope.All);
        await using var platform = OpenAuthority();
        await using var writer = Writer(platform, new Schema(LocationSchemaDecision.Valid));
        var request = new MasterDataCreation(Guid.NewGuid(), Guid.NewGuid(), "NEW", "New");
        Assert.Equal(MasterDataWriteDecision.Created, (await writer.CreateAsync(subject, request)).Decision);
        await Assert.ThrowsAsync<InvalidOperationException>(() => writer.CreateAsync(subject, request));
    }

    [Fact]
    public async Task CurrentCreationPreservesCodeAndOperationConflictDecisionsWithoutLeakingIdentity()
    {
        var subject = await SeedWriterAsync(DataScope.All);
        var request = new MasterDataCreation(Guid.NewGuid(), Guid.NewGuid(), "NEW", "New");
        await using var platform = OpenAuthority();
        await using (var first = Writer(platform, new Schema(LocationSchemaDecision.Valid)))
            Assert.Equal(MasterDataWriteDecision.Created, (await first.CreateAsync(subject, request)).Decision);
        await using (var second = Writer(platform, new Schema(LocationSchemaDecision.Valid)))
        {
            var conflict = await second.CreateAsync(subject, request with { OperationId = Guid.NewGuid() });
            Assert.Equal(MasterDataWriteDecision.CodeConflict, conflict.Decision);
            Assert.Null(conflict.Id); Assert.Null(conflict.Version);
        }
        await using (var changed = Writer(platform, new Schema(LocationSchemaDecision.Valid)))
        {
            var conflict = await changed.CreateAsync(subject, request with { Name = "Other" });
            Assert.Equal(MasterDataWriteDecision.IdempotencyConflict, conflict.Decision);
            Assert.Null(conflict.Id); Assert.Null(conflict.Version);
        }
        await using var verify = Open(_tenant);
        Assert.Single(await verify.MasterDataChanges.ToListAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CurrentCreationPropagatesCancellationFromAuthorityOrSchemaWithoutNewFacts(bool fromSchema)
    {
        using var cancellation = new CancellationTokenSource();
        var subject = fromSchema ? await SeedWriterAsync(DataScope.All) : new MasterDataSubject(_tenant, _actor, Guid.NewGuid());
        var parent = fromSchema ? await ExecuteAsync(Command()) : null;
        await using var platform = OpenAuthority();
        IMasterDataAuthorization authority = fromSchema ? new PlatformAuthority(platform, _clock)
            : new ControlledAuthority(new HashSet<string> { "Code", "Name" }, cancellation.Cancel);
        await using var writer = new WmsMasterDataWriter(_connection, _tenant, authority,
            new Schema(LocationSchemaDecision.Valid, fromSchema ? cancellation.Cancel : null), _clock, NullLoggerFactory.Instance);
        var request = new MasterDataCreation(Guid.NewGuid(), Guid.NewGuid(), "NEW", "New",
            parent?.Id, fromSchema ? Guid.NewGuid() : null, fromSchema ? "v1" : null);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => writer.CreateAsync(subject, request, cancellation.Token));
        await using var verify = Open(_tenant);
        Assert.Equal(fromSchema ? 1 : 0, await verify.MasterDataChanges.CountAsync());
        Assert.Empty(await verify.Locations.ToListAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CurrentCreationAuthorityDatabaseFailuresDoNotExposeProviderDetails(bool transient)
    {
        await using var writer = new WmsMasterDataWriter(_connection, _tenant, new FailingCreationAuthority(transient),
            new Schema(LocationSchemaDecision.Valid), _clock, NullLoggerFactory.Instance);
        var failure = await Assert.ThrowsAsync<WmsDatabaseException>(() =>
            writer.CreateAsync(new(_tenant, _actor, Guid.NewGuid()), new(Guid.NewGuid(), Guid.NewGuid(), "NEW", "New")));
        Assert.Equal(transient, failure.IsTransient);
        Assert.Null(failure.InnerException);
        Assert.DoesNotContain("sensitive-sentinel", failure.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CurrentCreationCompetingWritersPreserveSingleEffectAndExplicitRecovery(bool sameOperation)
    {
        var subject = await SeedWriterAsync(DataScope.All);
        var request = new MasterDataCreation(Guid.NewGuid(), Guid.NewGuid(), "NEW", "New");
        await using var platform = OpenAuthority();
        await using var first = Writer(platform, new Schema(LocationSchemaDecision.Valid));
        await using var second = Writer(platform, new Schema(LocationSchemaDecision.Valid));
        var results = await Task.WhenAll(first.CreateAsync(subject, request),
            second.CreateAsync(subject, sameOperation ? request : request with { OperationId = Guid.NewGuid() }));
        Assert.Single(results, result => result.Decision == MasterDataWriteDecision.Created);
        Assert.Single(results, result => result.Decision ==
            (sameOperation ? MasterDataWriteDecision.Replayed : MasterDataWriteDecision.CodeConflict));
        if (sameOperation) Assert.Equal(results[0].Id, results[1].Id);
        else Assert.Null(results.Single(result => result.Decision == MasterDataWriteDecision.CodeConflict).Id);
        await using var verify = Open(_tenant);
        Assert.Single(await verify.Warehouses.ToListAsync());
        Assert.Single(await verify.MasterDataChanges.ToListAsync());
        Assert.Empty(await verify.Entries.ToListAsync());
    }

    private sealed class FailingCreationAuthority(bool transient) : IMasterDataAuthorization
    {
        public Task<IReadOnlySet<string>> ReadFieldsAsync(MasterDataSubject subject, string permission, Guid creator, CancellationToken cancellationToken) =>
            throw new DatabaseFailure(transient);
        public Task<MasterDataCreatorAccess> ReadCreatorsAsync(MasterDataSubject subject, string permission,
            IReadOnlyCollection<string> requiredFields, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("This creation-only test authority does not implement catalogs.");
    }

    private WmsMasterDataWriter Writer(Wes.Platform.Infrastructure.Persistence.PlatformDbContext platform, IWarehouseLocationSchema schema) =>
        new(_connection, _tenant, new PlatformAuthority(platform, _clock), schema, _clock, NullLoggerFactory.Instance);

    private async Task<MasterDataSubject> SeedWriterAsync(DataScope scope)
    {
        var subject = await SeedAuthorityAsync(scope);
        await using var update = OpenAuthority();
        var role = await update.Roles.SingleAsync();
        var departments = scope == DataScope.Departments
            ? ImmutableHashSet.Create((await update.DirectoryUsers.SingleAsync(user => user.Id == _actor)).DepartmentId)
            : ImmutableHashSet<Guid>.Empty;
        role.Change(role.Version, true, role.Rules.Concat(
        [
            new("wms.warehouses.write", scope, departments, ImmutableHashSet.Create("Code", "Name")),
            new("wms.locations.write", scope, departments, ImmutableHashSet.Create("Code", "Name", "WarehouseId", "PackId", "SchemaVersion"))
        ]));
        await update.SaveChangesAsync();
        return subject;
    }
}
