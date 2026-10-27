using System.Collections.Immutable;
using System.Transactions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Wes.Platform.Domain;
using Wes.Wms;
using Wes.Wms.Features.Ledger;
using Wes.Wms.Features.MasterData;
using Xunit;

namespace Wes.Persistence.Tests;

public sealed partial class WmsMasterDataTests
{
    private static readonly string[] CreationEventFields =
        ["EventId", "Owner", "ContractName", "SchemaVersion", "AggregateId", "AggregateVersion", "CorrelationId", "OccurredAt", "Status"];

    [Theory]
    [InlineData(DataScope.Own, 1)]
    [InlineData(DataScope.Departments, 2)]
    [InlineData(DataScope.All, 2)]
    public async Task CreationEventReadsFilterCurrentRealCreatorBeforePaging(DataScope scope, int expected)
    {
        var subject = await SeedEventAuthorityAsync(scope);
        var own = Command("OWN");
        var other = Command("OTHER") with { ActorId = _creator };
        await ExecuteAsync(own); await ExecuteAsync(other);
        await ExecuteAsync(Command("UNKNOWN") with { ActorId = Guid.NewGuid() });
        await using (var foreign = Open(_other))
            await new CreateMasterData(foreign, _clock, new Schema(LocationSchemaDecision.Unavailable)).ExecuteAsync(Command("FOREIGN"));
        await using var platform = OpenAuthority();
        await using var owner = new WmsMasterDataScope(_connection, _tenant, new PlatformAuthority(platform, _clock), NullLoggerFactory.Instance);
        var page = await owner.QueryCreationEventsAsync(subject, new());
        Assert.Equal(MasterDataReadDecision.Success, page.Decision);
        Assert.Equal(expected, page.Rows.Length);
        Assert.All(page.Rows, row => Assert.Contains(row.EventId, new[] { own.OperationId, other.OperationId }));
        var next = await owner.QueryCreationEventsAsync(subject, new(Offset: 1));
        Assert.Equal(page.Rows.Skip(1), next.Rows);
    }

    [Theory]
    [InlineData("EventId")]
    [InlineData("Owner")]
    [InlineData("ContractName")]
    [InlineData("SchemaVersion")]
    [InlineData("AggregateId")]
    [InlineData("AggregateVersion")]
    [InlineData("CorrelationId")]
    [InlineData("OccurredAt")]
    [InlineData("Status")]
    public async Task CreationEventReadsRequireEachExactCurrentField(string missing)
    {
        var subject = await SeedEventAuthorityAsync(DataScope.All, CreationEventFields.Where(field => field != missing).ToArray());
        await ExecuteAsync(Command());
        await using var platform = OpenAuthority();
        await using var owner = Open(_tenant);
        var page = await Access(owner, new PlatformAuthority(platform, _clock)).QueryCreationEventsAsync(subject, new());
        Assert.Equal(MasterDataReadDecision.Forbidden, page.Decision);
        Assert.Empty(page.Rows);
    }

    [Fact]
    public async Task CreationEventReadsIntersectEachFieldScopeAndDoNotInferAuthorityFromMasterDataReads()
    {
        var subject = await SeedAuthorityAsync(DataScope.All);
        await ExecuteAsync(Command("OWN"));
        await ExecuteAsync(Command("OTHER") with { ActorId = _creator });
        await using var platform = OpenAuthority();
        await using var owner = Open(_tenant);
        var access = Access(owner, new PlatformAuthority(platform, _clock));
        Assert.Equal(MasterDataReadDecision.Forbidden, (await access.QueryCreationEventsAsync(subject, new())).Decision);
        await using (var writer = OpenAuthority())
        {
            var role = await writer.Roles.SingleAsync();
            role.Change(role.Version, true,
            [
                new("wms.messages.read", DataScope.All, ImmutableHashSet<Guid>.Empty, CreationEventFields.Where(field => field != "AggregateId").ToImmutableHashSet()),
                new("wms.messages.read", DataScope.Own, ImmutableHashSet<Guid>.Empty, ImmutableHashSet.Create("AggregateId"))
            ]);
            await writer.SaveChangesAsync();
        }
        var result = await access.QueryCreationEventsAsync(subject, new());
        Assert.Equal(MasterDataReadDecision.Success, result.Decision);
        Assert.Single(result.Rows);
    }

    [Theory]
    [InlineData("department")]
    [InlineData("role")]
    [InlineData("field")]
    [InlineData("membership")]
    [InlineData("session")]
    public async Task CreationEventReadsRecheckCurrentAuthorityWithoutCachingCreators(string revoke)
    {
        var subject = await SeedEventAuthorityAsync(DataScope.Departments);
        await ExecuteAsync(Command() with { ActorId = _creator });
        await using var platform = OpenAuthority();
        await using var owner = Open(_tenant);
        var access = Access(owner, new PlatformAuthority(platform, _clock));
        Assert.Single((await access.QueryCreationEventsAsync(subject, new())).Rows);
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
                    ? role.Rules.Select(rule => rule with { Fields = rule.Fields.Remove("Owner") }) : role.Rules);
            }
            await writer.SaveChangesAsync();
        }
        var result = await access.QueryCreationEventsAsync(subject, new());
        Assert.Equal(revoke == "department" ? MasterDataReadDecision.Success : MasterDataReadDecision.Forbidden, result.Decision);
        Assert.Empty(result.Rows);
    }

    [Theory]
    [InlineData(-1, 25)]
    [InlineData(10001, 25)]
    [InlineData(0, 0)]
    [InlineData(0, 101)]
    [InlineData(0, 10)]
    public async Task CreationEventReadsRejectInvalidPageBounds(int offset, int limit)
    {
        await using var owner = Open(_tenant);
        var result = await Access(owner, new ControlledAuthority(null!))
            .QueryCreationEventsAsync(new(_tenant, _actor, Guid.NewGuid()), new(offset, limit));
        Assert.Equal(MasterDataReadDecision.Invalid, result.Decision);
        Assert.Empty(result.Rows);
    }

    [Fact]
    public async Task CreationEventReadsHonorTwentyFiveRowThresholdAndStableSqlOrder()
    {
        await using (var seed = Open(_tenant))
        {
            for (var index = 0; index < 27; index++)
            {
                var warehouse = new Wes.Wms.Domain.Warehouse(_tenant, $"EVENT-{index:D2}", "Event", _clock.GetUtcNow().AddSeconds(index));
                seed.Warehouses.Add(warehouse);
                seed.MasterDataChanges.Add(new(Guid.NewGuid(), _actor, Guid.NewGuid(), warehouse));
            }
            await seed.SaveChangesAsync();
        }
        await using var owner = Open(_tenant);
        var access = Access(owner, new CatalogAuthority(new(true, new HashSet<Guid> { _actor })));
        var first = await access.QueryCreationEventsAsync(new(_tenant, _actor, Guid.NewGuid()), new());
        var last = await access.QueryCreationEventsAsync(new(_tenant, _actor, Guid.NewGuid()), new(25));
        Assert.Equal(25, first.Rows.Length); Assert.Equal(2, last.Rows.Length);
        Assert.True(first.Rows[^1].OccurredAt < last.Rows[0].OccurredAt);
        Assert.Empty(first.Rows.Select(row => row.EventId).Intersect(last.Rows.Select(row => row.EventId)));
    }

    [Fact]
    public async Task CreationEventReadsKeepAuthorizedEmptyDistinctFromDeniedOrBrokenAuthority()
    {
        await using var owner = Open(_tenant);
        var subject = new MasterDataSubject(_tenant, _actor, Guid.NewGuid());
        var empty = await Access(owner, new CatalogAuthority(new(true, new HashSet<Guid>()))).QueryCreationEventsAsync(subject, new());
        Assert.Equal(MasterDataReadDecision.Success, empty.Decision);
        Assert.Empty(empty.Rows);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Access(owner, new CatalogAuthority(null!)).QueryCreationEventsAsync(subject, new()));
    }

    [Theory]
    [InlineData("dirty")]
    [InlineData("transaction")]
    [InlineData("ambient")]
    public async Task CreationEventReadsRejectDirtyOrTransactionalOwners(string invalid)
    {
        await using var owner = Open(_tenant);
        await using var transaction = invalid == "transaction" ? await owner.Database.BeginTransactionAsync() : null;
        using var ambient = invalid == "ambient" ? new TransactionScope(TransactionScopeAsyncFlowOption.Enabled) : null;
        if (invalid == "dirty") owner.Warehouses.Add(new(_tenant, "DIRTY", "Dirty", _clock.GetUtcNow()));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Access(owner, new CatalogAuthority(new(true, new HashSet<Guid> { _actor })))
            .QueryCreationEventsAsync(new(_tenant, _actor, Guid.NewGuid()), new()));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CreationEventReadsDoNotDeliverAfterAuthorityOrSqlCancellation(bool afterSql)
    {
        await ExecuteAsync(Command());
        using var cancellation = new CancellationTokenSource();
        await using var owner = Open(_tenant, afterSql ? new CancelAfterCatalogMaterialization(cancellation) : null);
        var authority = new CatalogAuthority(new(true, new HashSet<Guid> { _actor }), afterSql ? null : cancellation.Cancel);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Access(owner, authority)
            .QueryCreationEventsAsync(new(_tenant, _actor, Guid.NewGuid()), new(), cancellation.Token));
    }

    [Fact]
    public async Task CreationEventReadsDoNotReplaceProviderFailureWithSuccessfulEmptyRows()
    {
        await using var owner = Open(_tenant, new QueryFailure(false));
        var error = await Assert.ThrowsAsync<WmsDatabaseException>(() =>
            Access(owner, new CatalogAuthority(new(true, new HashSet<Guid> { _actor })))
                .QueryCreationEventsAsync(new(_tenant, _actor, Guid.NewGuid()), new()));
        Assert.Null(error.InnerException);
        Assert.DoesNotContain("sensitive", error.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("tenant")]
    [InlineData("actor")]
    [InlineData("session")]
    public async Task CreationEventReadsRejectUntrustedSubjectsBeforeAuthority(string invalid)
    {
        await using var owner = Open(_tenant);
        var subject = new MasterDataSubject(invalid == "tenant" ? _other : _tenant,
            invalid == "actor" ? Guid.Empty : _actor, invalid == "session" ? Guid.Empty : Guid.NewGuid());
        var page = await Access(owner, new ControlledAuthority(null!)).QueryCreationEventsAsync(subject, new());
        Assert.Equal(MasterDataReadDecision.Forbidden, page.Decision);
        Assert.Empty(page.Rows);
    }

    [Theory]
    [InlineData("Owner")]
    [InlineData("ContractName")]
    [InlineData("SchemaVersion")]
    [InlineData("Status")]
    public async Task CreationEventReadsRejectUnsupportedStoredEnvelopeInsteadOfReturningUnknownAsPending(string field)
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
                .QueryCreationEventsAsync(new(_tenant, _actor, Guid.NewGuid()), new()));
    }

    private async Task<MasterDataSubject> SeedEventAuthorityAsync(DataScope scope, string[]? fields = null)
    {
        var subject = await SeedAuthorityAsync(scope);
        await using var writer = OpenAuthority();
        var role = await writer.Roles.SingleAsync();
        var rule = role.Rules.First();
        role.Change(role.Version, true, [new("wms.messages.read", scope, rule.Departments,
            (fields ?? CreationEventFields).ToImmutableHashSet(StringComparer.Ordinal))]);
        await writer.SaveChangesAsync();
        return subject;
    }
}
