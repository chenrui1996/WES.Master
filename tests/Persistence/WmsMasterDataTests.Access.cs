using System.Collections.Immutable;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Wes.Platform.Domain;
using Wes.Platform.Features.Authorization;
using Wes.Platform.Features.Tenancy;
using Wes.Platform.Infrastructure.Persistence;
using Wes.Wms;
using Wes.Wms.Features.Ledger;
using Wes.Wms.Features.MasterData;
using Xunit;

namespace Wes.Persistence.Tests;

public sealed partial class WmsMasterDataTests
{
    private static readonly string[] WarehouseFields = ["Id", "Code", "Name", "Enabled", "Version", "CreatedAt"];
    private static readonly string[] LocationFields = [.. WarehouseFields, "WarehouseId", "PackId", "SchemaVersion"];
    private readonly Guid _creator = Guid.NewGuid();
    private bool _authorizationReady;

    [Fact]
    public async Task CurrentAllAuthorityReadsExactDisabledWarehouseAndPinnedLocationWithoutWriting()
    {
        var subject = await SeedAuthorityAsync(DataScope.All);
        var warehouse = await ExecuteAsync(Command());
        var location = await ExecuteAsync(Command("A1") with { WarehouseId = warehouse.Id, PackId = Guid.NewGuid(), SchemaVersion = "v1" },
            new Schema(LocationSchemaDecision.Valid));
        await using var platform = OpenAuthority();
        await using var owner = Open(_tenant);
        await using var access = new WmsMasterDataScope(_connection, _tenant, new PlatformAuthority(platform, _clock), NullLoggerFactory.Instance);
        var readWarehouse = await access.ReadWarehouseAsync(subject, warehouse.Id!.Value);
        Assert.Equal(MasterDataReadDecision.Success, readWarehouse.Decision);
        Assert.Equal((warehouse.Id, "WH-01", warehouse.Version, false),
            ((Guid?)readWarehouse.Warehouse!.Id, readWarehouse.Warehouse.Code, readWarehouse.Warehouse.Version, readWarehouse.Warehouse.Enabled));
        var readLocation = await access.ReadLocationAsync(subject, location.Id!.Value);
        Assert.Equal(MasterDataReadDecision.Success, readLocation.Decision);
        Assert.Equal((location.Id, warehouse.Id, "v1", false),
            ((Guid?)readLocation.Location!.Id, (Guid?)readLocation.Location.WarehouseId, readLocation.Location.SchemaVersion, readLocation.Location.Enabled));
        Assert.Equal(2, await owner.MasterDataChanges.CountAsync());
        Assert.Empty(await owner.Entries.ToListAsync());
        Assert.Empty(await platform.Audit.ToListAsync());
    }

    [Fact]
    public async Task OwnAuthorityUsesTheCreationFactNotTheReaderOrRequestedResourceId()
    {
        var subject = await SeedAuthorityAsync(DataScope.Own);
        var own = await ExecuteAsync(Command("OWN"));
        var other = await ExecuteAsync(Command("OTHER") with { ActorId = _creator });
        await using var platform = OpenAuthority();
        await using var owner = Open(_tenant);
        var access = Access(owner, new PlatformAuthority(platform, _clock));
        Assert.Equal(MasterDataReadDecision.Success, (await access.ReadWarehouseAsync(subject, own.Id!.Value)).Decision);
        var denied = await access.ReadWarehouseAsync(subject, other.Id!.Value);
        Assert.Equal(MasterDataReadDecision.Forbidden, denied.Decision);
        Assert.Null(denied.Warehouse);
    }

    [Fact]
    public async Task CreatorDepartmentIsResolvedAgainAfterAnotherOwnerMovesTheCreator()
    {
        var subject = await SeedAuthorityAsync(DataScope.Departments);
        var warehouse = await ExecuteAsync(Command() with { ActorId = _creator });
        await using var platform = OpenAuthority();
        await using var owner = Open(_tenant);
        var access = Access(owner, new PlatformAuthority(platform, _clock));
        Assert.Equal(MasterDataReadDecision.Success, (await access.ReadWarehouseAsync(subject, warehouse.Id!.Value)).Decision);
        await using (var writer = OpenAuthority())
        {
            var department = new DirectoryDepartment(_tenant, "OTHER", "Other");
            writer.Departments.Add(department);
            var creator = await writer.DirectoryUsers.SingleAsync(item => item.Id == _creator);
            writer.Entry(creator).Property(item => item.DepartmentId).CurrentValue = department.Id;
            await writer.SaveChangesAsync();
        }
        Assert.Equal(MasterDataReadDecision.Forbidden, (await access.ReadWarehouseAsync(subject, warehouse.Id!.Value)).Decision);
    }

    [Theory]
    [InlineData("session")]
    [InlineData("membership")]
    [InlineData("role")]
    [InlineData("field")]
    public async Task ReusedReaderNeverCachesLiveAuthority(string revoke)
    {
        var subject = await SeedAuthorityAsync(DataScope.All);
        var warehouse = await ExecuteAsync(Command());
        await using var platform = OpenAuthority();
        await using var owner = Open(_tenant);
        var access = Access(owner, new PlatformAuthority(platform, _clock));
        Assert.Equal(MasterDataReadDecision.Success, (await access.ReadWarehouseAsync(subject, warehouse.Id!.Value)).Decision);
        await using (var writer = OpenAuthority())
        {
            if (revoke == "session") (await writer.Sessions.SingleAsync()).Revoke(_clock.GetUtcNow());
            else if (revoke == "membership") writer.Memberships.Remove(await writer.Memberships.SingleAsync());
            else
            {
                var role = await writer.Roles.SingleAsync();
                var rules = revoke == "field"
                    ? role.Rules.Select(rule => rule with { Fields = rule.Fields.Remove("Name") })
                    : role.Rules;
                role.Change(role.Version, revoke != "role", rules);
            }
            await writer.SaveChangesAsync();
        }
        Assert.Equal(MasterDataReadDecision.Forbidden, (await access.ReadWarehouseAsync(subject, warehouse.Id!.Value)).Decision);
    }

    [Theory]
    [InlineData("tenant")]
    [InlineData("actor")]
    [InlineData("session")]
    [InlineData("empty-actor")]
    [InlineData("empty-session")]
    public async Task WrongSubjectCannotReceiveMasterData(string wrong)
    {
        var subject = await SeedAuthorityAsync(DataScope.All);
        subject = wrong switch
        {
            "tenant" => subject with { TenantId = _other },
            "actor" => subject with { ActorId = Guid.NewGuid() },
            "session" => subject with { SessionId = Guid.NewGuid() },
            "empty-actor" => subject with { ActorId = Guid.Empty },
            _ => subject with { SessionId = Guid.Empty }
        };
        var warehouse = await ExecuteAsync(Command());
        await using var platform = OpenAuthority();
        await using var owner = Open(_tenant);
        var denied = await Access(owner, new PlatformAuthority(platform, _clock)).ReadWarehouseAsync(subject, warehouse.Id!.Value);
        Assert.Equal(MasterDataReadDecision.Forbidden, denied.Decision);
        Assert.Null(denied.Warehouse);
    }

    [Fact]
    public async Task MissingCreatorDoesNotTurnAnAllGrantIntoAnInferredOwner()
    {
        var subject = await SeedAuthorityAsync(DataScope.All);
        var warehouse = await ExecuteAsync(Command() with { ActorId = Guid.NewGuid() });
        await using var platform = OpenAuthority();
        await using var owner = Open(_tenant);
        Assert.Equal(MasterDataReadDecision.Forbidden,
            (await Access(owner, new PlatformAuthority(platform, _clock)).ReadWarehouseAsync(subject, warehouse.Id!.Value)).Decision);
    }

    [Fact]
    public async Task InvalidAndAbsentIdentityHaveNoSuccessfulProjection()
    {
        var subject = await SeedAuthorityAsync(DataScope.All);
        await using var platform = OpenAuthority();
        await using var owner = Open(_tenant);
        var access = Access(owner, new PlatformAuthority(platform, _clock));
        Assert.Equal(MasterDataReadDecision.Invalid, (await access.ReadWarehouseAsync(subject, Guid.Empty)).Decision);
        var absent = await access.ReadLocationAsync(subject, Guid.NewGuid());
        Assert.Equal(MasterDataReadDecision.NotFound, absent.Decision);
        Assert.Null(absent.Location);
    }

    [Theory]
    [InlineData("warehouse")]
    [InlineData("location")]
    public async Task ResourcePermissionsCannotSubstituteForEachOther(string resource)
    {
        var subject = await SeedAuthorityAsync(DataScope.All);
        var warehouse = await ExecuteAsync(Command());
        var location = await ExecuteAsync(Command("A1") with { WarehouseId = warehouse.Id, PackId = Guid.NewGuid(), SchemaVersion = "v1" },
            new Schema(LocationSchemaDecision.Valid));
        await using (var writer = OpenAuthority())
        {
            var role = await writer.Roles.SingleAsync();
            role.Change(role.Version, true, role.Rules.Where(rule =>
                rule.Permission == (resource == "warehouse" ? "wms.locations.read" : "wms.warehouses.read")));
            await writer.SaveChangesAsync();
        }
        await using var platform = OpenAuthority();
        await using var access = new WmsMasterDataScope(_connection, _tenant, new PlatformAuthority(platform, _clock), NullLoggerFactory.Instance);
        var result = resource == "warehouse"
            ? await access.ReadWarehouseAsync(subject, warehouse.Id!.Value)
            : await access.ReadLocationAsync(subject, location.Id!.Value);
        Assert.Equal(MasterDataReadDecision.Forbidden, result.Decision);
        Assert.Null(result.Warehouse); Assert.Null(result.Location);
    }

    [Fact]
    public async Task AnActualForeignResourceAndCreatorCannotBeResolvedThroughTheCurrentTenant()
    {
        var subject = await SeedAuthorityAsync(DataScope.All);
        await using var foreign = Open(_other);
        var created = await new CreateMasterData(foreign, _clock, new Schema(LocationSchemaDecision.Unavailable)).ExecuteAsync(Command());
        await using var platform = OpenAuthority();
        await using var owner = Open(_tenant);
        Assert.Equal(MasterDataReadDecision.NotFound,
            (await Access(owner, new PlatformAuthority(platform, _clock)).ReadWarehouseAsync(subject, created.Id!.Value)).Decision);
        Assert.Empty(await new CurrentAuthorization(platform, _clock).VisibleOwnedFieldsAsync(
            new TenantContext(subject.TenantId, subject.ActorId, subject.SessionId), "wms.warehouses.read", _other, _actor));
    }

    [Fact]
    public async Task PortFieldComparersCannotTurnLowercaseIdentityIntoPermissionForId()
    {
        var created = await ExecuteAsync(Command());
        var fields = WarehouseFields.Where(field => field != "Id").Append("id").ToHashSet(StringComparer.OrdinalIgnoreCase);
        await using var owner = Open(_tenant);
        Assert.Equal(MasterDataReadDecision.Forbidden,
            (await Access(owner, new ControlledAuthority(fields)).ReadWarehouseAsync(new(_tenant, _actor, Guid.NewGuid()), created.Id!.Value)).Decision);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingPortOrPortResultIsAnExplicitIntegrationError(bool missingResult)
    {
        var created = await ExecuteAsync(Command());
        await using var owner = Open(_tenant);
        if (!missingResult)
            Assert.Throws<ArgumentNullException>(() => Access(owner, null!));
        else
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                Access(owner, new ControlledAuthority(null!)).ReadWarehouseAsync(new(_tenant, _actor, Guid.NewGuid()), created.Id!.Value));
    }

    [Fact]
    public async Task AuthorizationCancellationCannotDeliverALateFullProjection()
    {
        var created = await ExecuteAsync(Command());
        using var cancellation = new CancellationTokenSource();
        await using var owner = Open(_tenant);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Access(owner, new ControlledAuthority(WarehouseFields.ToHashSet(StringComparer.Ordinal), cancellation.Cancel))
                .ReadWarehouseAsync(new(_tenant, _actor, Guid.NewGuid()), created.Id!.Value, cancellation.Token));
    }

    [Fact]
    public async Task MasterDataReadDatabaseErrorsDoNotExposeProviderDetails()
    {
        var created = await ExecuteAsync(Command());
        await using var owner = Open(_tenant, new QueryFailure(false));
        var error = await Assert.ThrowsAsync<WmsDatabaseException>(() =>
            Access(owner, new ControlledAuthority(WarehouseFields.ToHashSet(StringComparer.Ordinal)))
                .ReadWarehouseAsync(new(_tenant, _actor, Guid.NewGuid()), created.Id!.Value));
        Assert.Null(error.InnerException);
        Assert.DoesNotContain("sensitive", error.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    private MasterDataAccess Access(Wes.Wms.Infrastructure.Persistence.WmsDbContext owner, IMasterDataAuthorization authorization) =>
        new(owner, authorization, NullLogger<MasterDataAccess>.Instance);

    private PlatformDbContext OpenAuthority() => new(new DbContextOptionsBuilder<PlatformDbContext>()
        .UseMySQL(_connection, mysql => mysql.MigrationsHistoryTable("__WesPlatformMigrations")).Options, _tenant);

    private async Task<MasterDataSubject> SeedAuthorityAsync(DataScope scope)
    {
        await using var database = OpenAuthority();
        await database.Database.MigrateAsync();
        _authorizationReady = true;
        DirectoryTestFixture.Add(database, _actor, _creator);
        var department = database.Departments.Local.Single().Id;
        var departments = scope == DataScope.Departments ? ImmutableHashSet.Create(department) : ImmutableHashSet<Guid>.Empty;
        var role = new AccessRole(_tenant, "WMS-READER",
        [
            new("wms.warehouses.read", scope, departments, WarehouseFields.ToImmutableHashSet(StringComparer.Ordinal)),
            new("wms.locations.read", scope, departments, LocationFields.ToImmutableHashSet(StringComparer.Ordinal))
        ]);
        var session = new Session(_tenant, _actor, SessionClient.Web, _clock.GetUtcNow(), new string('A', 64));
        database.Sessions.Add(session);
        database.Roles.Add(role);
        database.Memberships.Add(new(_tenant, _actor, role.Id));
        await database.SaveChangesAsync();
        return new(_tenant, _actor, session.Id);
    }

    private async Task RemoveAuthorizationAsync()
    {
        if (!_authorizationReady) return;
        await using var database = OpenAuthority();
        database.Memberships.RemoveRange(await database.Memberships.ToListAsync());
        database.Sessions.RemoveRange(await database.Sessions.ToListAsync());
        database.Roles.RemoveRange(await database.Roles.ToListAsync());
        await DirectoryTestFixture.RemoveAsync(database);
        await database.SaveChangesAsync();
    }

    private sealed class PlatformAuthority(PlatformDbContext database, TimeProvider clock) : IMasterDataAuthorization
    {
        public async Task<IReadOnlySet<string>> ReadFieldsAsync(MasterDataSubject subject, string permission,
            Guid creator, CancellationToken cancellationToken)
        {
            await using var scope = new Wes.Platform.PlatformScope(database.Database.GetConnectionString()!, database.TenantId,
                clock, NullLoggerFactory.Instance);
            return await scope.VisibleOwnedResourceFieldsAsync(
                new TenantContext(subject.TenantId, subject.ActorId, subject.SessionId), permission, subject.TenantId, creator, cancellationToken);
        }
    }

    private sealed class ControlledAuthority(IReadOnlySet<string> fields, Action? beforeReturn = null) : IMasterDataAuthorization
    {
        public Task<IReadOnlySet<string>> ReadFieldsAsync(MasterDataSubject subject, string permission, Guid creator,
            CancellationToken cancellationToken)
        {
            beforeReturn?.Invoke();
            return Task.FromResult(fields);
        }
    }
}
