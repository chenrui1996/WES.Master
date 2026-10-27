using Microsoft.EntityFrameworkCore;
using Wes.Platform.Domain;
using Wes.Platform.Features.Files;
using Wes.Platform.Infrastructure.Persistence;
using Xunit;

namespace Wes.Persistence.Tests;

public sealed partial class AuthorizedFileAccessTests
{
    [Fact]
    public async Task BusinessCatalogRequiresExactBindingAndCurrentEntityAuthorityWithoutPhysicalIo()
    {
        var entity = new FileEntityReference("wms-warehouse", Guid.NewGuid());
        await BindBusinessAsync(entity);
        var authority = new BusinessAuthority();
        var store = new Store();
        await using var owner = Open(_tenant);

        var page = await Business(owner, entity, authority, store).QueryAsync(_context);

        Assert.Equal(FileAccessDecision.Success, page.Decision);
        Assert.Equal(_file, Assert.Single(page.Rows).Id);
        Assert.Equal(0, store.Reads);
        Assert.Empty(await owner.Audit.ToListAsync());
        Assert.Empty(await owner.OutboxMessages.ToListAsync());
        Assert.Equal(2, authority.Calls);
    }

    [Theory]
    [InlineData("owner")]
    [InlineData("case")]
    [InlineData("entity")]
    [InlineData("unbound")]
    [InlineData("zero-count")]
    [InlineData("counter-only")]
    public async Task BusinessCatalogNeverReturnsUnrelatedOrInconsistentReferences(string boundary)
    {
        var entity = new FileEntityReference("wms-warehouse", Guid.NewGuid());
        if (boundary is not ("unbound" or "counter-only")) await BindBusinessAsync(entity);
        if (boundary is "zero-count" or "counter-only")
        {
            await using var change = Open(_tenant);
            var file = await change.Files.SingleAsync();
            if (boundary == "zero-count") file.RemoveReference(_clock.GetUtcNow());
            else file.AddReference(_clock.GetUtcNow());
            await change.SaveChangesAsync();
        }
        var target = boundary switch
        {
            "owner" => new FileEntityReference("wms-location", entity.EntityId),
            "case" => new FileEntityReference("WMS-WAREHOUSE", entity.EntityId),
            "entity" => new FileEntityReference(entity.Owner, Guid.NewGuid()),
            _ => entity
        };
        await using var owner = Open(_tenant);

        var page = await Business(owner, target, new BusinessAuthority()).QueryAsync(_context);

        Assert.Equal(FileAccessDecision.Success, page.Decision);
        Assert.Empty(page.Rows);
    }

    [Theory]
    [InlineData("Id")]
    [InlineData("FileName")]
    [InlineData("ContentType")]
    [InlineData("ByteCount")]
    [InlineData("State")]
    [InlineData("Stored")]
    [InlineData("Version")]
    [InlineData("CreatedAt")]
    [InlineData("ProcessingProblem")]
    public async Task BusinessCatalogRequiresEveryCurrentFileMetadataField(string missing)
    {
        var entity = new FileEntityReference("wms-warehouse", Guid.NewGuid());
        await BindBusinessAsync(entity);
        await SetGrantsAsync([Grant("platform.files.read", DataScope.All, ReadFields.Where(field => field != missing).ToArray())]);
        await using var owner = Open(_tenant);

        var page = await Business(owner, entity, new BusinessAuthority()).QueryAsync(_context);

        Assert.Equal(FileAccessDecision.Forbidden, page.Decision);
        Assert.Empty(page.Rows);
    }

    [Fact]
    public async Task BusinessCatalogIntersectsEveryFieldsScopeBeforePaging()
    {
        var entity = new FileEntityReference("wms-warehouse", Guid.NewGuid());
        await SetGrantsAsync([
            Grant("platform.files.read", DataScope.All, ReadFields.Where(field => field != "FileName").ToArray()),
            Grant("platform.files.read", DataScope.Own, ["FileName"])
        ]);
        Guid own;
        await using (var seed = Open(_tenant))
        {
            own = CatalogAttachment(seed, _actor, entity, "own.txt", _clock.GetUtcNow().AddHours(-1)).Id;
            for (var index = 0; index < 30; index++)
                CatalogAttachment(seed, _creator, entity, $"outside-{index}.txt", _clock.GetUtcNow());
            await seed.SaveChangesAsync();
        }
        await using var owner = Open(_tenant);

        var page = await Business(owner, entity, new BusinessAuthority()).QueryAsync(_context);

        Assert.Equal(own, Assert.Single(page.Rows).Id);
        Assert.Equal(FileAccessDecision.Success, page.Decision);
    }

    [Fact]
    public async Task BusinessCatalogUsesStableTieOrderingAndApprovedPageSizes()
    {
        var entity = new FileEntityReference("wms-location", Guid.NewGuid());
        Guid[] expected;
        await using (var seed = Open(_tenant))
        {
            expected = Enumerable.Range(0, 26).Select(index =>
                CatalogAttachment(seed, _actor, entity, $"attached-{index}.txt", _clock.GetUtcNow()).Id)
                .OrderBy(id => id.ToString("D"), StringComparer.Ordinal).ToArray();
            await seed.SaveChangesAsync();
        }
        await using var owner = Open(_tenant);
        var access = Business(owner, entity, new BusinessAuthority());

        var first = await access.QueryAsync(_context);
        var second = await access.QueryAsync(_context, 25);

        Assert.Equal(expected[..25], first.Rows.Select(file => file.Id));
        Assert.Equal(expected[25], Assert.Single(second.Rows).Id);
        foreach (var size in new[] { 50, 100 })
            Assert.Equal(expected, (await access.QueryAsync(_context, limit: size)).Rows.Select(file => file.Id));
        Assert.Empty((await access.QueryAsync(_context, 10000)).Rows);
        Assert.Empty((await access.QueryAsync(_context, state: (int)QuarantinedFileState.Quarantined)).Rows);
        Assert.Equal(expected[..25], (await access.QueryAsync(_context, state: (int)QuarantinedFileState.Available)).Rows.Select(file => file.Id));
    }

    [Theory]
    [InlineData(-1, 25, null)]
    [InlineData(10001, 25, null)]
    [InlineData(0, 0, null)]
    [InlineData(0, 26, null)]
    [InlineData(0, 101, null)]
    [InlineData(0, 25, -1)]
    [InlineData(0, 25, 5)]
    public async Task BusinessCatalogRejectsInvalidPageAndState(int offset, int limit, int? state)
    {
        var authority = new BusinessAuthority();
        await using var owner = Open(_tenant);

        var page = await Business(owner, new("wms-location", Guid.NewGuid()), authority)
            .QueryAsync(_context, offset, limit, state);

        Assert.Equal(FileAccessDecision.Invalid, page.Decision);
        Assert.Empty(page.Rows);
        Assert.Equal(0, authority.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BusinessCatalogRejectsCurrentEntityDenialBeforeAndAfterReadingRows(bool afterQuery)
    {
        var entity = new FileEntityReference("wms-warehouse", Guid.NewGuid());
        await BindBusinessAsync(entity);
        var authority = new BusinessAuthority { Allowed = afterQuery };
        authority.BeforeRead = call => { if (call == 2) authority.Allowed = false; };
        await using var owner = Open(_tenant);

        var page = await Business(owner, entity, authority).QueryAsync(_context);

        Assert.Equal(FileAccessDecision.Forbidden, page.Decision);
        Assert.Empty(page.Rows);
        Assert.Equal(afterQuery ? 2 : 1, authority.Calls);
    }

    [Fact]
    public async Task BusinessCatalogCancellationAndAuthorityErrorsCannotReturnASuccessPage()
    {
        var entity = new FileEntityReference("wms-warehouse", Guid.NewGuid());
        await BindBusinessAsync(entity);
        using var cancel = new CancellationTokenSource();
        var authority = new BusinessAuthority { BeforeRead = call => { if (call == 2) cancel.Cancel(); } };
        await using var owner = Open(_tenant);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Business(owner, entity, authority).QueryAsync(_context, ct: cancel.Token));
        var sentinel = new InvalidOperationException("test entity dependency failure");
        var failure = new BusinessAuthority { Failure = sentinel };
        Assert.Same(sentinel, await Assert.ThrowsAsync<InvalidOperationException>(() => Business(owner, entity, failure).QueryAsync(_context)));
        Assert.Empty(await owner.Audit.ToListAsync());
    }

    [Fact]
    public async Task BusinessCatalogUsesCurrentCreatorDepartmentAndRejectsMismatchedTenant()
    {
        var entity = new FileEntityReference("wms-warehouse", Guid.NewGuid());
        await BindBusinessAsync(entity);
        await SetGrantsAsync(Grants(DataScope.Departments));
        await using (var before = Open(_tenant))
            Assert.Single((await Business(before, entity, new BusinessAuthority()).QueryAsync(_context)).Rows);
        await using (var change = Open(_tenant))
        {
            var department = new DirectoryDepartment(_tenant, "catalog-new", "Outside");
            change.Departments.Add(department);
            var creator = await change.DirectoryUsers.SingleAsync(item => item.Id == _creator);
            creator.Change(creator.Version, creator.Name, department.Id, creator.Enabled);
            await change.SaveChangesAsync();
        }
        var authority = new BusinessAuthority();
        await using var owner = Open(_tenant);

        Assert.Empty((await Business(owner, entity, authority).QueryAsync(_context)).Rows);
        var calls = authority.Calls;
        var foreign = await Business(owner, entity, authority).QueryAsync(new(_other, _actor, _context.SessionId));

        Assert.Equal(FileAccessDecision.Forbidden, foreign.Decision);
        Assert.Empty(foreign.Rows);
        Assert.Equal(calls, authority.Calls);
    }

    [Fact]
    public async Task BusinessCatalogExcludesForeignTenantReferencesEvenForTheSameEntityAndCreator()
    {
        var entity = new FileEntityReference("wms-location", Guid.NewGuid());
        await BindBusinessAsync(entity);
        await using (var foreign = Open(_other))
        {
            var department = new DirectoryDepartment(_other, "files", "Foreign");
            foreign.DirectoryTenants.Add(new(_other, "Foreign file catalog"));
            foreign.Departments.Add(department);
            foreign.DirectoryUsers.Add(new(_other, _creator, "CREATOR", "Foreign", department.Id));
            CatalogAttachment(foreign, _creator, entity, "foreign.txt", _clock.GetUtcNow());
            await foreign.SaveChangesAsync();
        }
        await using var owner = Open(_tenant);

        var page = await Business(owner, entity, new BusinessAuthority()).QueryAsync(_context);

        Assert.Equal(_file, Assert.Single(page.Rows).Id);
    }

    private QuarantinedFile CatalogAttachment(PlatformDbContext owner, Guid creator, FileEntityReference entity,
        string name, DateTimeOffset created)
    {
        var file = new QuarantinedFile(owner.TenantId, creator, Guid.NewGuid(), name, "text/plain", 4,
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData("safe"u8)), created);
        file.ConfirmStored(file.Sha256, 4, _clock.GetUtcNow());
        var attempt = file.BeginScan(_clock.GetUtcNow());
        file.CompleteScan(attempt, FileScanVerdict.Clean, file.Sha256, 4, _clock.GetUtcNow());
        file.AddReference(_clock.GetUtcNow());
        owner.Files.Add(file);
        owner.FileReferences.Add(new(owner.TenantId, file.Id, entity.Owner, entity.EntityId));
        return file;
    }
}
