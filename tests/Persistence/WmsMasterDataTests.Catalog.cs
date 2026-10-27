using System.Collections.Immutable;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
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
    [InlineData(DataScope.Own, 1)]
    [InlineData(DataScope.Departments, 2)]
    [InlineData(DataScope.All, 2)]
    public async Task CatalogFiltersCurrentCreatorScopeBeforePagingWithoutLeakingMissingCreators(DataScope scope, int expected)
    {
        var subject = await SeedAuthorityAsync(scope);
        await ExecuteAsync(Command("A-OWN"));
        await ExecuteAsync(Command("B-OTHER") with { ActorId = _creator });
        await ExecuteAsync(Command("C-MISSING") with { ActorId = Guid.NewGuid() });
        await using var platform = OpenAuthority();
        await using var owner = new WmsMasterDataScope(_connection, _tenant, new PlatformAuthority(platform, _clock), NullLoggerFactory.Instance);
        var page = await owner.QueryWarehousesAsync(subject, new());
        Assert.Equal(MasterDataReadDecision.Success, page.Decision);
        Assert.Equal(expected, page.Warehouses.Length);
        Assert.Equal("A-OWN", page.Warehouses[0].Code);
        Assert.Empty(page.Locations);
        Assert.DoesNotContain(page.Warehouses, row => row.Code == "C-MISSING");
        var next = await owner.QueryWarehousesAsync(subject, new(Offset: 1));
        Assert.Equal(expected - 1, next.Warehouses.Length);
    }

    [Fact]
    public async Task CatalogIntersectsPerFieldScopesBeforeStableCodePaging()
    {
        var subject = await SeedAuthorityAsync(DataScope.All);
        await ExecuteAsync(Command("Z-OWN"));
        await ExecuteAsync(Command("A-OTHER") with { ActorId = _creator });
        await using (var writer = OpenAuthority())
        {
            var role = await writer.Roles.SingleAsync();
            role.Change(role.Version, true,
            [
                new("wms.warehouses.read", DataScope.All, ImmutableHashSet<Guid>.Empty,
                    WarehouseFields.Where(field => field != "Name").ToImmutableHashSet()),
                new("wms.warehouses.read", DataScope.Own, ImmutableHashSet<Guid>.Empty, ImmutableHashSet.Create("Name"))
            ]);
            await writer.SaveChangesAsync();
        }
        await using var platform = OpenAuthority();
        await using var database = Open(_tenant);
        var page = await Access(database, new PlatformAuthority(platform, _clock)).QueryWarehousesAsync(subject, new());
        Assert.Equal(MasterDataReadDecision.Success, page.Decision);
        Assert.Equal("Z-OWN", Assert.Single(page.Warehouses).Code);
        Assert.Empty((await Access(database, new PlatformAuthority(platform, _clock))
            .QueryWarehousesAsync(subject, new(Offset: 1))).Warehouses);
    }

    [Theory]
    [InlineData("department")]
    [InlineData("field")]
    [InlineData("role")]
    [InlineData("session")]
    [InlineData("membership")]
    public async Task CatalogRechecksCurrentDirectoryAndRoleInsteadOfCachingCreatorSets(string change)
    {
        var subject = await SeedAuthorityAsync(DataScope.Departments);
        await ExecuteAsync(Command() with { ActorId = _creator });
        await using var platform = OpenAuthority();
        await using var database = Open(_tenant);
        var access = Access(database, new PlatformAuthority(platform, _clock));
        Assert.Single((await access.QueryWarehousesAsync(subject, new())).Warehouses);
        await using (var writer = OpenAuthority())
        {
            if (change == "department")
            {
                var department = new DirectoryDepartment(_tenant, "OTHER", "Other");
                writer.Departments.Add(department);
                var creator = await writer.DirectoryUsers.SingleAsync(item => item.Id == _creator);
                writer.Entry(creator).Property(item => item.DepartmentId).CurrentValue = department.Id;
            }
            else if (change == "session") (await writer.Sessions.SingleAsync()).Revoke(_clock.GetUtcNow());
            else if (change == "membership") writer.Memberships.Remove(await writer.Memberships.SingleAsync());
            else
            {
                var role = await writer.Roles.SingleAsync();
                role.Change(role.Version, change != "role", change == "field"
                    ? role.Rules.Select(rule => rule with { Fields = rule.Fields.Remove("Name") }) : role.Rules);
            }
            await writer.SaveChangesAsync();
        }
        var result = await access.QueryWarehousesAsync(subject, new());
        Assert.Empty(result.Warehouses);
        Assert.Equal(change == "department" ? MasterDataReadDecision.Success : MasterDataReadDecision.Forbidden, result.Decision);
    }

    [Fact]
    public async Task CatalogFiltersExactCodeEnabledAndWarehouseAndNeverChangesFacts()
    {
        var subject = await SeedAuthorityAsync(DataScope.All);
        var first = await ExecuteAsync(Command("WH-A"));
        var second = await ExecuteAsync(Command("WH-B"));
        await ExecuteAsync(Command("L-A") with { WarehouseId = first.Id, PackId = Guid.NewGuid(), SchemaVersion = "V1" },
            new Schema(LocationSchemaDecision.Valid));
        await ExecuteAsync(Command("L-B") with { WarehouseId = second.Id, PackId = Guid.NewGuid(), SchemaVersion = "v1" },
            new Schema(LocationSchemaDecision.Valid));
        await using var platform = OpenAuthority();
        await using var database = Open(_tenant);
        var access = Access(database, new PlatformAuthority(platform, _clock));
        Assert.Equal("WH-A", Assert.Single((await access.QueryWarehousesAsync(subject, new(Code: "wh-a", Enabled: false))).Warehouses).Code);
        Assert.Empty((await access.QueryWarehousesAsync(subject, new(Enabled: true))).Warehouses);
        var location = Assert.Single((await access.QueryLocationsAsync(subject, new(WarehouseId: first.Id))).Locations);
        Assert.Equal(("L-A", "V1", first.Id), (location.Code, location.SchemaVersion, (Guid?)location.WarehouseId));
        Assert.Empty((await access.QueryLocationsAsync(subject, new(Code: "L-A", WarehouseId: second.Id))).Locations);
        Assert.Equal(4, await database.MasterDataChanges.CountAsync());
        Assert.Empty(await database.Entries.ToListAsync());
    }

    [Theory]
    [InlineData(-1, 25, null)]
    [InlineData(10001, 25, null)]
    [InlineData(0, 1, null)]
    [InlineData(0, 25, "BAD CODE")]
    [InlineData(0, 25, "")]
    public async Task CatalogRejectsInvalidQueriesInsteadOfReturningAnEmptySuccessfulPage(int offset, int limit, string? code)
    {
        await using var database = Open(_tenant);
        var result = await Access(database, new ControlledAuthority(WarehouseFields.ToHashSet()))
            .QueryWarehousesAsync(new(_tenant, _actor, Guid.NewGuid()), new(offset, limit, code));
        Assert.Equal(MasterDataReadDecision.Invalid, result.Decision);
        Assert.Empty(result.Warehouses);
    }

    [Theory]
    [InlineData(25)]
    [InlineData(50)]
    [InlineData(100)]
    public async Task CatalogAcceptsOnlyDocumentedPageSizesAndStableBoundary(int limit)
    {
        var subject = await SeedAuthorityAsync(DataScope.All);
        for (var index = 29; index >= 0; index--) await ExecuteAsync(Command($"WH-{index:D2}"));
        await using var platform = OpenAuthority();
        await using var database = Open(_tenant);
        var access = Access(database, new PlatformAuthority(platform, _clock));
        var page = await access.QueryWarehousesAsync(subject, new(Limit: limit));
        Assert.Equal(Math.Min(30, limit), page.Warehouses.Length);
        Assert.Equal("WH-00", page.Warehouses[0].Code);
        var next = await access.QueryWarehousesAsync(subject, new(Offset: 25, Limit: limit));
        Assert.Equal("WH-25", next.Warehouses[0].Code);
        Assert.Equal(5, next.Warehouses.Length);
    }

    [Theory]
    [InlineData("tenant")]
    [InlineData("actor")]
    [InlineData("session")]
    public async Task CatalogRejectsMismatchedOrEmptyTrustedSubjectBeforeCallingAuthority(string invalid)
    {
        await using var database = Open(_tenant);
        var subject = new MasterDataSubject(invalid == "tenant" ? _other : _tenant,
            invalid == "actor" ? Guid.Empty : _actor, invalid == "session" ? Guid.Empty : Guid.NewGuid());
        var result = await Access(database, new ControlledAuthority(WarehouseFields.ToHashSet())).QueryLocationsAsync(subject, new());
        Assert.Equal(MasterDataReadDecision.Forbidden, result.Decision);
        Assert.Empty(result.Locations);
    }

    [Fact]
    public async Task CatalogCannotReturnForeignTenantRowsEvenWithTheSameRealCreator()
    {
        var subject = await SeedAuthorityAsync(DataScope.All);
        await ExecuteAsync(Command("OWN"));
        await using (var foreign = Open(_other))
            await new CreateMasterData(foreign, _clock, new Schema(LocationSchemaDecision.Unavailable)).ExecuteAsync(Command("FOREIGN"));
        await using var platform = OpenAuthority();
        await using var owner = Open(_tenant);
        Assert.Equal("OWN", Assert.Single((await Access(owner, new PlatformAuthority(platform, _clock))
            .QueryWarehousesAsync(subject, new())).Warehouses).Code);
    }

    [Fact]
    public async Task CatalogDoesNotTreatAnAuthorizedEmptyCreatorScopeAsMissingAuthority()
    {
        await ExecuteAsync(Command());
        await using var database = Open(_tenant);
        var page = await Access(database, new CatalogAuthority(new(true, new HashSet<Guid>())))
            .QueryWarehousesAsync(new(_tenant, _actor, Guid.NewGuid()), new());
        Assert.Equal(MasterDataReadDecision.Success, page.Decision);
        Assert.Empty(page.Warehouses);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("null-creators")]
    [InlineData("empty-id")]
    public async Task CatalogRejectsBrokenAuthorityResultsInsteadOfReturningAnEmptyPage(string failure)
    {
        var result = failure switch
        {
            "null" => null!,
            "null-creators" => new MasterDataCreatorAccess(true, null!),
            _ => new MasterDataCreatorAccess(true, new HashSet<Guid> { Guid.Empty })
        };
        await using var database = Open(_tenant);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Access(database, new CatalogAuthority(result))
            .QueryWarehousesAsync(new(_tenant, _actor, Guid.NewGuid()), new()));
    }

    [Fact]
    public async Task CatalogCancellationAfterAuthorityCannotDeliverALateProjection()
    {
        using var cancellation = new CancellationTokenSource();
        await using var database = Open(_tenant);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Access(database,
            new CatalogAuthority(new(true, new HashSet<Guid> { _actor }), cancellation.Cancel))
            .QueryWarehousesAsync(new(_tenant, _actor, Guid.NewGuid()), new(), cancellation.Token));
    }

    [Fact]
    public async Task CatalogProviderErrorsRemainSafeAndDoNotTurnIntoSuccessfulEmptyPages()
    {
        await using var database = Open(_tenant, new QueryFailure(false));
        var failure = await Assert.ThrowsAsync<WmsDatabaseException>(() => Access(database,
            new CatalogAuthority(new(true, new HashSet<Guid> { _actor })))
            .QueryLocationsAsync(new(_tenant, _actor, Guid.NewGuid()), new()));
        Assert.Null(failure.InnerException);
        Assert.DoesNotContain("sensitive", failure.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CatalogCancellationAfterSqlMaterializationCannotDeliverALatePage(bool location)
    {
        await ExecuteAsync(Command());
        using var cancellation = new CancellationTokenSource();
        await using var database = Open(_tenant, new CancelAfterCatalogMaterialization(cancellation));
        var access = Access(database, new CatalogAuthority(new(true, new HashSet<Guid> { _actor })));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => location
            ? access.QueryLocationsAsync(new(_tenant, _actor, Guid.NewGuid()), new(), cancellation.Token)
            : access.QueryWarehousesAsync(new(_tenant, _actor, Guid.NewGuid()), new(), cancellation.Token));
    }

    private sealed class CancelAfterCatalogMaterialization(CancellationTokenSource cancellation) : DbCommandInterceptor
    {
        public override ValueTask<InterceptionResult> DataReaderClosingAsync(DbCommand command,
            DataReaderClosingEventData eventData, InterceptionResult result)
        {
            cancellation.Cancel();
            return ValueTask.FromResult(result);
        }
    }

    private sealed class CatalogAuthority(MasterDataCreatorAccess result, Action? beforeReturn = null) : IMasterDataAuthorization
    {
        public Task<IReadOnlySet<string>> ReadFieldsAsync(MasterDataSubject subject, string permission, Guid creator, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("This catalog-only test authority does not implement detail reads.");
        public Task<MasterDataCreatorAccess> ReadCreatorsAsync(MasterDataSubject subject, string permission,
            IReadOnlyCollection<string> requiredFields, CancellationToken cancellationToken)
        {
            beforeReturn?.Invoke();
            return Task.FromResult(result);
        }
    }
}
