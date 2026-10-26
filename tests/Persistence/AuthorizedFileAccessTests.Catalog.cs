using Microsoft.EntityFrameworkCore;
using Wes.Platform.Domain;
using Wes.Platform.Features.Files;
using Wes.Platform.Features.Tenancy;
using Xunit;

namespace Wes.Persistence.Tests;

public sealed partial class AuthorizedFileAccessTests
{
    [Fact]
    public async Task FileCatalog_FieldScopeIntersectionCannotBorrowAllFromAnotherField()
    {
        await SetGrantsAsync([
            Grant("platform.files.read", DataScope.All, ReadFields.Where(field => field != "FileName").ToArray()),
            Grant("platform.files.read", DataScope.Own, ["FileName"])
        ]);
        await using var owner = Open(_tenant);
        var page = await Access(owner, new Store()).QueryAsync(_context);
        Assert.Equal(FileAccessDecision.Success, page.Decision);
        Assert.Empty(page.Rows);
    }

    [Fact]
    public async Task FileCatalog_PagesAfterScopeFilteringAndNeverTouchesPhysicalAdapters()
    {
        await SetGrantsAsync(Grants(DataScope.Own));
        Guid own;
        await using (var seed = Open(_tenant))
        {
            var file = CatalogFile(_actor, "own.txt", _clock.GetUtcNow().AddHours(-1));
            own = file.Id;
            seed.Files.Add(file);
            for (var index = 0; index < 30; index++)
                seed.Files.Add(CatalogFile(_creator, $"outside-{index}.txt", _clock.GetUtcNow()));
            await seed.SaveChangesAsync();
        }
        var storage = new Store(); var scanner = new Scanner();
        await using var owner = Open(_tenant);
        var page = await Access(owner, storage, scanner).QueryAsync(_context);
        Assert.Equal(own, Assert.Single(page.Rows).Id);
        Assert.Equal(0, storage.Reads);
        Assert.Equal(0, scanner.Calls);
        Assert.Empty(await owner.Audit.ToListAsync());
        Assert.Empty(await owner.OutboxMessages.ToListAsync());
    }

    [Fact]
    public async Task FileCatalog_StableTieOrderAndAllApprovedPageSizesUseTheSameMembership()
    {
        await SetGrantsAsync(Grants(DataScope.Own));
        var files = Enumerable.Range(0, 26).Select(index => CatalogFile(_actor, $"own-{index}.txt", _clock.GetUtcNow())).ToArray();
        await using (var seed = Open(_tenant))
        {
            seed.Files.AddRange(files);
            await seed.SaveChangesAsync();
        }
        var expected = files.OrderBy(file => file.Id.ToString("D"), StringComparer.Ordinal).Select(file => file.Id).ToArray();
        await using var owner = Open(_tenant);
        var service = Access(owner, new Store());
        var first = await service.QueryAsync(_context);
        var second = await service.QueryAsync(_context, 25);
        Assert.Equal(expected[..25], first.Rows.Select(file => file.Id).ToArray());
        Assert.Equal(expected[25], Assert.Single(second.Rows).Id);
        foreach (var limit in new[] { 50, 100 })
            Assert.Equal(expected, (await service.QueryAsync(_context, limit: limit)).Rows.Select(file => file.Id).ToArray());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FileCatalog_NeitherCounterNorRealBusinessReferenceCanExposeAnAttachment(bool counter)
    {
        await using (var seed = Open(_tenant))
        {
            if (counter) (await seed.Files.SingleAsync()).AddReference(_clock.GetUtcNow());
            else seed.FileReferences.Add(new(_tenant, _file, "Wms", Guid.NewGuid()));
            await seed.SaveChangesAsync();
        }
        await using var owner = Open(_tenant);
        var page = await Access(owner, new Store()).QueryAsync(_context);
        Assert.Equal(FileAccessDecision.Success, page.Decision);
        Assert.Empty(page.Rows);
    }

    [Fact]
    public async Task FileCatalog_CurrentCreatorDepartmentChangeRemovesTheRow()
    {
        await SetGrantsAsync(Grants(DataScope.Departments));
        await using (var before = Open(_tenant))
            Assert.Single((await Access(before, new Store()).QueryAsync(_context)).Rows);
        await using (var change = Open(_tenant))
        {
            var outside = new DirectoryDepartment(_tenant, "catalog-outside", "Outside");
            change.Departments.Add(outside);
            var creator = await change.DirectoryUsers.SingleAsync(user => user.Id == _creator);
            creator.Change(creator.Version, creator.Name, outside.Id, creator.Enabled);
            await change.SaveChangesAsync();
        }
        await using var after = Open(_tenant);
        Assert.Empty((await Access(after, new Store()).QueryAsync(_context)).Rows);
    }

    [Fact]
    public async Task FileCatalog_TrustedContextMustMatchTheOwnerAndAnActiveCurrentSession()
    {
        await using (var owner = Open(_tenant))
            Assert.Equal(FileAccessDecision.Forbidden, (await Access(owner, new Store())
                .QueryAsync(new TenantContext(_other, _actor, _context.SessionId))).Decision);
        await using (var change = Open(_tenant))
        {
            var session = await change.Sessions.SingleAsync();
            session.Revoke(_clock.GetUtcNow());
            await change.SaveChangesAsync();
        }
        await using var after = Open(_tenant);
        Assert.Equal(FileAccessDecision.Forbidden, (await Access(after, new Store()).QueryAsync(_context)).Decision);
    }

    private QuarantinedFile CatalogFile(Guid creator, string name, DateTimeOffset createdAt) =>
        new(_tenant, creator, Guid.NewGuid(), name, "text/plain", 4,
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData("safe"u8)), createdAt);

    [Fact]
    public async Task FileCatalog_AllScopeCannotSeeAnotherTenantsFileEvenWithTheSameCreatorIdentity()
    {
        await using (var seed = Open(_other))
        {
            seed.DirectoryTenants.Add(new(_other, "Foreign catalog tenant"));
            seed.Files.Add(new QuarantinedFile(_other, _creator, Guid.NewGuid(), "foreign.txt", "text/plain", 4,
                Convert.ToHexString(System.Security.Cryptography.SHA256.HashData("safe"u8)), _clock.GetUtcNow()));
            await seed.SaveChangesAsync();
        }
        await using var owner = Open(_tenant);
        var page = await Access(owner, new Store()).QueryAsync(_context);
        Assert.Equal(_file, Assert.Single(page.Rows).Id);
        Assert.DoesNotContain(page.Rows, file => file.FileName == "foreign.txt");
    }
}
