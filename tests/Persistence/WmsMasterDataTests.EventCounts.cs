using System.Collections.Immutable;
using System.Transactions;
using Microsoft.EntityFrameworkCore;
using Wes.Platform.Domain;
using Wes.Wms.Features.Ledger;
using Wes.Wms.Features.MasterData;
using Xunit;

namespace Wes.Persistence.Tests;

public sealed partial class WmsMasterDataTests
{
    private static readonly string[] EventCountFields = ["Owner", "ObservedAt", "PendingCount"];

    [Theory]
    [InlineData(DataScope.Own, 1)]
    [InlineData(DataScope.Departments, 2)]
    [InlineData(DataScope.All, 2)]
    public async Task CreationEventCountsUseCurrentCompleteCreatorScope(DataScope scope, long expected)
    {
        var subject = await SeedEventAuthorityAsync(scope, EventCountFields);
        await ExecuteAsync(Command("OWN"));
        await ExecuteAsync(Command("OTHER") with { ActorId = _creator });
        await ExecuteAsync(Command("UNKNOWN") with { ActorId = Guid.NewGuid() });
        await using (var foreign = Open(_other))
            await new CreateMasterData(foreign, _clock, new Schema(LocationSchemaDecision.Unavailable)).ExecuteAsync(Command("FOREIGN"));
        await using var platform = OpenAuthority();
        await using var owner = Open(_tenant);
        var before = DateTimeOffset.UtcNow;
        var result = await Access(owner, new PlatformAuthority(platform, _clock)).ReadCreationEventSummaryAsync(subject);
        Assert.Equal(MasterDataReadDecision.Success, result.Decision);
        Assert.Equal("Wms", result.Summary!.Owner);
        Assert.Equal(expected, result.Summary.PendingCount);
        Assert.InRange(result.Summary.ObservedAt, before, DateTimeOffset.UtcNow);
        Assert.Empty(owner.ChangeTracker.Entries());
    }

    [Theory]
    [InlineData("Owner")]
    [InlineData("ObservedAt")]
    [InlineData("PendingCount")]
    public async Task CreationEventCountsRequireEveryIndependentCurrentField(string missing)
    {
        var subject = await SeedEventAuthorityAsync(DataScope.All, EventCountFields.Where(field => field != missing).ToArray());
        await using var platform = OpenAuthority();
        await using var owner = Open(_tenant);
        var result = await Access(owner, new PlatformAuthority(platform, _clock)).ReadCreationEventSummaryAsync(subject);
        Assert.Equal(MasterDataReadDecision.Forbidden, result.Decision);
        Assert.Null(result.Summary);
    }

    [Fact]
    public async Task CreationEventCountsDoNotInferStatisticsFromRowFieldsAndIntersectScopes()
    {
        var subject = await SeedEventAuthorityAsync(DataScope.All);
        await ExecuteAsync(Command("OWN"));
        await ExecuteAsync(Command("OTHER") with { ActorId = _creator });
        await using var platform = OpenAuthority();
        await using var owner = Open(_tenant);
        var access = Access(owner, new PlatformAuthority(platform, _clock));
        Assert.Equal(MasterDataReadDecision.Forbidden, (await access.ReadCreationEventSummaryAsync(subject)).Decision);
        await using (var writer = OpenAuthority())
        {
            var role = await writer.Roles.SingleAsync();
            role.Change(role.Version, true,
            [
                new("wms.messages.read", DataScope.All, ImmutableHashSet<Guid>.Empty, ImmutableHashSet.Create("Owner", "ObservedAt")),
                new("wms.messages.read", DataScope.Own, ImmutableHashSet<Guid>.Empty, ImmutableHashSet.Create("PendingCount"))
            ]);
            await writer.SaveChangesAsync();
        }
        Assert.Equal(1, (await access.ReadCreationEventSummaryAsync(subject)).Summary!.PendingCount);
    }

    [Theory]
    [InlineData("department")]
    [InlineData("role")]
    [InlineData("field")]
    [InlineData("membership")]
    [InlineData("session")]
    public async Task CreationEventCountsRecheckLiveScopeAndFields(string revoke)
    {
        var subject = await SeedEventAuthorityAsync(DataScope.Departments, EventCountFields);
        await ExecuteAsync(Command() with { ActorId = _creator });
        await using var platform = OpenAuthority();
        await using var owner = Open(_tenant);
        var access = Access(owner, new PlatformAuthority(platform, _clock));
        Assert.Equal(1, (await access.ReadCreationEventSummaryAsync(subject)).Summary!.PendingCount);
        await using (var writer = OpenAuthority())
        {
            if (revoke == "department")
            {
                var department = new DirectoryDepartment(_tenant, "OTHER", "Other");
                writer.Departments.Add(department);
                var creator = await writer.DirectoryUsers.SingleAsync(row => row.Id == _creator);
                writer.Entry(creator).Property(row => row.DepartmentId).CurrentValue = department.Id;
            }
            else if (revoke == "session") (await writer.Sessions.SingleAsync()).Revoke(_clock.GetUtcNow());
            else if (revoke == "membership") writer.Memberships.Remove(await writer.Memberships.SingleAsync());
            else
            {
                var role = await writer.Roles.SingleAsync();
                role.Change(role.Version, revoke != "role", revoke == "field"
                    ? role.Rules.Select(rule => rule with { Fields = rule.Fields.Remove("PendingCount") }) : role.Rules);
            }
            await writer.SaveChangesAsync();
        }
        var result = await access.ReadCreationEventSummaryAsync(subject);
        Assert.Equal(revoke == "department" ? MasterDataReadDecision.Success : MasterDataReadDecision.Forbidden, result.Decision);
        if (revoke == "department") Assert.Equal(0, result.Summary!.PendingCount);
        else Assert.Null(result.Summary);
    }

    [Fact]
    public async Task CreationEventCountsDoNotStopAtAListPageAndDistinguishAuthorizedZero()
    {
        await using (var seed = Open(_tenant))
        {
            for (var index = 0; index < 27; index++)
            {
                var warehouse = new Wes.Wms.Domain.Warehouse(_tenant, $"COUNT-{index:D2}", "Count", _clock.GetUtcNow());
                seed.Warehouses.Add(warehouse);
                seed.MasterDataChanges.Add(new(Guid.NewGuid(), _actor, Guid.NewGuid(), warehouse));
            }
            await seed.SaveChangesAsync();
        }
        await using var owner = Open(_tenant);
        var subject = new MasterDataSubject(_tenant, _actor, Guid.NewGuid());
        Assert.Equal(27, (await Access(owner, new CatalogAuthority(new(true, new HashSet<Guid> { _actor })))
            .ReadCreationEventSummaryAsync(subject)).Summary!.PendingCount);
        Assert.Equal(0, (await Access(owner, new CatalogAuthority(new(true, new HashSet<Guid>())))
            .ReadCreationEventSummaryAsync(subject)).Summary!.PendingCount);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Access(owner, new CatalogAuthority(null!)).ReadCreationEventSummaryAsync(subject));
    }

    [Theory]
    [InlineData("dirty")]
    [InlineData("transaction")]
    [InlineData("ambient")]
    public async Task CreationEventCountsRejectDirtyAndTransactionalOwners(string invalid)
    {
        await using var owner = Open(_tenant);
        await using var transaction = invalid == "transaction" ? await owner.Database.BeginTransactionAsync() : null;
        using var ambient = invalid == "ambient" ? new TransactionScope(TransactionScopeAsyncFlowOption.Enabled) : null;
        if (invalid == "dirty") owner.Warehouses.Add(new(_tenant, "DIRTY", "Dirty", _clock.GetUtcNow()));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Access(owner, new CatalogAuthority(new(true, new HashSet<Guid> { _actor })))
            .ReadCreationEventSummaryAsync(new(_tenant, _actor, Guid.NewGuid())));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CreationEventCountsDoNotDeliverAfterAuthorityOrSqlCancellation(bool afterSql)
    {
        await ExecuteAsync(Command());
        using var cancellation = new CancellationTokenSource();
        await using var owner = Open(_tenant, afterSql ? new CancelAfterCatalogMaterialization(cancellation) : null);
        var authority = new CatalogAuthority(new(true, new HashSet<Guid> { _actor }), afterSql ? null : cancellation.Cancel);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Access(owner, authority)
            .ReadCreationEventSummaryAsync(new(_tenant, _actor, Guid.NewGuid()), cancellation.Token));
    }

    [Fact]
    public async Task CreationEventCountsDoNotReturnZeroOnProviderFailure()
    {
        await using var owner = Open(_tenant, new QueryFailure(false));
        var error = await Assert.ThrowsAsync<WmsDatabaseException>(() =>
            Access(owner, new CatalogAuthority(new(true, new HashSet<Guid> { _actor })))
                .ReadCreationEventSummaryAsync(new(_tenant, _actor, Guid.NewGuid())));
        Assert.Null(error.InnerException);
        Assert.DoesNotContain("sensitive", error.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("tenant")]
    [InlineData("actor")]
    [InlineData("session")]
    public async Task CreationEventCountsRejectUntrustedSubjectsBeforeAuthority(string invalid)
    {
        await using var owner = Open(_tenant);
        var subject = new MasterDataSubject(invalid == "tenant" ? _other : _tenant,
            invalid == "actor" ? Guid.Empty : _actor, invalid == "session" ? Guid.Empty : Guid.NewGuid());
        var result = await Access(owner, new ControlledAuthority(null!)).ReadCreationEventSummaryAsync(subject);
        Assert.Equal(MasterDataReadDecision.Forbidden, result.Decision);
        Assert.Null(result.Summary);
    }

    [Theory]
    [InlineData("Owner")]
    [InlineData("ContractName")]
    [InlineData("SchemaVersion")]
    [InlineData("Status")]
    public async Task CreationEventCountsRejectUnsupportedStoredEnvelopes(string field)
    {
        await ExecuteAsync(Command());
        await using (var setup = Open(_tenant))
        {
            var sql = field switch
            {
                "Owner" => "UPDATE wms_outbox_messages SET Owner = 'Foreign' WHERE TenantId = {0}",
                "ContractName" => "UPDATE wms_outbox_messages SET ContractName = 'unknown.contract' WHERE TenantId = {0}",
                "SchemaVersion" => "UPDATE wms_outbox_messages SET SchemaVersion = 2 WHERE TenantId = {0}",
                _ => "UPDATE wms_outbox_messages SET Status = '99' WHERE TenantId = {0}"
            };
            await setup.Database.ExecuteSqlRawAsync(sql, _tenant.ToString("D"));
        }
        await using var owner = Open(_tenant);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Access(owner, new CatalogAuthority(new(true, new HashSet<Guid> { _actor })))
                .ReadCreationEventSummaryAsync(new(_tenant, _actor, Guid.NewGuid())));
    }
}
