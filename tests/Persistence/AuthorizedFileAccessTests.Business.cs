using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Wes.Platform.Domain;
using Wes.Platform.Features.Files;
using Wes.Platform.Features.Tenancy;
using Wes.Platform.Infrastructure.Persistence;
using Xunit;

namespace Wes.Persistence.Tests;

public sealed partial class AuthorizedFileAccessTests
{
    [Fact]
    public async Task BusinessMetadataRequiresActualBindingAndCurrentEntityAuthority()
    {
        var entity = new FileEntityReference("wms-warehouse", Guid.NewGuid());
        await BindBusinessAsync(entity);
        var authority = new BusinessAuthority();
        await using var db = Open(_tenant);
        var result = await Business(db, entity, authority).ReadAsync(_context, _file);
        Assert.Equal(FileAccessDecision.Success, result.Decision);
        Assert.Equal(_file, result.File!.Id);
        Assert.Equal(1, authority.Calls);
        Assert.Equal(entity, authority.LastEntity);
        Assert.Equal(_context, authority.LastContext);
        await using var staging = Open(_tenant);
        Assert.Equal(FileAccessDecision.Forbidden, (await Access(staging, new Store()).ReadAsync(_context, _file)).Decision);
    }

    [Fact]
    public async Task BusinessDownloadVerifiesContentAuditsBeforeDeliveryAndClearsOwnedBuffer()
    {
        var entity = new FileEntityReference("wms-location", Guid.NewGuid());
        await BindBusinessAsync(entity);
        var authority = new BusinessAuthority();
        await using var db = Open(_tenant);

        var result = await Business(db, entity, authority).DownloadAsync(_context, _file, Guid.NewGuid());

        Assert.Equal(FileAccessDecision.Success, result.Decision);
        using var download = Assert.IsType<FileDownload>(result.Download);
        Assert.Equal("safe"u8.ToArray(), download.Content.ToArray());
        Assert.Equal(4, authority.Calls);
        var memory = download.Content;
        download.Dispose();
        Assert.All(memory.ToArray(), value => Assert.Equal((byte)0, value));
        await using var verify = Open(_tenant);
        var audit = Assert.Single(await verify.Audit.ToListAsync());
        Assert.Equal(PlatformAuditAction.FileDownloaded, audit.Action);
        Assert.Equal(_actor, audit.ActorId);
        Assert.Equal(_file, audit.TargetId);
    }

    [Theory]
    [InlineData("owner")]
    [InlineData("entity")]
    [InlineData("unbound")]
    [InlineData("zero-count")]
    public async Task BusinessReadNeverAcceptsUnrelatedOrInconsistentBinding(string boundary)
    {
        var entity = new FileEntityReference("wms-warehouse", Guid.NewGuid());
        if (boundary != "unbound") await BindBusinessAsync(entity);
        if (boundary == "zero-count")
        {
            await using var change = Open(_tenant);
            (await change.Files.SingleAsync()).RemoveReference(_clock.GetUtcNow());
            await change.SaveChangesAsync();
        }
        var target = boundary switch
        {
            "owner" => new FileEntityReference("wms-location", entity.EntityId),
            "entity" => new FileEntityReference(entity.Owner, Guid.NewGuid()),
            _ => entity
        };
        var authority = new BusinessAuthority();
        var store = new Store();
        await using var db = Open(_tenant);

        var result = await Business(db, target, authority, store).DownloadAsync(_context, _file, Guid.NewGuid());

        Assert.Equal(FileAccessDecision.Forbidden, result.Decision);
        Assert.Null(result.Download);
        Assert.Equal(0, authority.Calls);
        Assert.Equal(0, store.Reads);
        await using var verify = Open(_tenant);
        Assert.Empty(await verify.Audit.ToListAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BusinessReadIntersectsCurrentEntityAndFileAuthority(bool fileAuthority)
    {
        var entity = new FileEntityReference("wms-warehouse", Guid.NewGuid());
        await BindBusinessAsync(entity);
        if (fileAuthority) await SetGrantsAsync([]);
        var authority = new BusinessAuthority { Allowed = fileAuthority };
        var store = new Store();
        await using var db = Open(_tenant);

        var result = await Business(db, entity, authority, store).DownloadAsync(_context, _file, Guid.NewGuid());

        Assert.Equal(FileAccessDecision.Forbidden, result.Decision);
        Assert.Null(result.Download);
        Assert.Equal(0, store.Reads);
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
    [InlineData("Content")]
    public async Task BusinessDownloadRequiresEveryCurrentFileField(string removed)
    {
        var entity = new FileEntityReference("wms-location", Guid.NewGuid());
        await BindBusinessAsync(entity);
        await SetGrantsAsync(Grants().Select(grant => grant with { Fields = grant.Fields.Remove(removed) }));
        var store = new Store();
        await using var db = Open(_tenant);

        var result = await Business(db, entity, new BusinessAuthority(), store).DownloadAsync(_context, _file, Guid.NewGuid());

        Assert.Equal(FileAccessDecision.Forbidden, result.Decision);
        Assert.Null(result.Download);
        Assert.Equal(0, store.Reads);
    }

    [Theory]
    [InlineData("entity-revoked")]
    [InlineData("file-revoked")]
    [InlineData("unbound")]
    [InlineData("rebound")]
    [InlineData("entity-failure")]
    public async Task BusinessDownloadRechecksAuthorityBindingAndVersionAfterStorageIo(string mutation)
    {
        var entity = new FileEntityReference("wms-warehouse", Guid.NewGuid());
        await BindBusinessAsync(entity);
        var authority = new BusinessAuthority();
        var store = new Store(async () =>
        {
            if (mutation == "entity-revoked") authority.Allowed = false;
            else if (mutation == "entity-failure") authority.Failure = new FileAccessException(true);
            else if (mutation == "file-revoked") await SetGrantsAsync([]);
            else
            {
                await using var change = Open(_tenant);
                var file = await change.Files.SingleAsync();
                file.RemoveReference(_clock.GetUtcNow());
                change.FileReferences.Remove(await change.FileReferences.SingleAsync());
                await change.SaveChangesAsync();
                if (mutation == "rebound") await BindBusinessAsync(entity);
            }
        });
        await using var db = Open(_tenant);

        if (mutation == "entity-failure")
            Assert.True((await Assert.ThrowsAsync<FileAccessException>(() =>
                Business(db, entity, authority, store).DownloadAsync(_context, _file, Guid.NewGuid()))).IsTransient);
        else
        {
            var result = await Business(db, entity, authority, store).DownloadAsync(_context, _file, Guid.NewGuid());
            Assert.Equal(mutation == "rebound" ? FileAccessDecision.VersionConflict : FileAccessDecision.Forbidden, result.Decision);
            Assert.Null(result.Download);
        }

        Assert.All(Assert.IsType<byte[]>(store.LastBuffer), value => Assert.Equal((byte)0, value));
        await using var verify = Open(_tenant);
        Assert.Empty(await verify.Audit.ToListAsync());
    }

    [Fact]
    public async Task BusinessDownloadCancellationAfterAuditRollsBackAndZeroesBytes()
    {
        var entity = new FileEntityReference("wms-warehouse", Guid.NewGuid());
        await BindBusinessAsync(entity);
        using var cancellation = new CancellationTokenSource();
        var store = new Store();
        await using var db = Open(_tenant, new CancelAudit(cancellation));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Business(db, entity, new BusinessAuthority(), store).DownloadAsync(_context, _file, Guid.NewGuid(), cancellation.Token));

        Assert.All(Assert.IsType<byte[]>(store.LastBuffer), value => Assert.Equal((byte)0, value));
        await using var verify = Open(_tenant);
        Assert.Empty(await verify.Audit.ToListAsync());
    }

    [Fact]
    public async Task BusinessReadRejectsMismatchedTenantAndDoesNotCallEntityAdapter()
    {
        var entity = new FileEntityReference("wms-warehouse", Guid.NewGuid());
        await BindBusinessAsync(entity);
        var authority = new BusinessAuthority();
        await using var db = Open(_tenant);

        var result = await Business(db, entity, authority).ReadAsync(new(_other, _actor, _context.SessionId), _file);

        Assert.Equal(FileAccessDecision.Forbidden, result.Decision);
        Assert.Null(result.File);
        Assert.Equal(0, authority.Calls);
    }

    [Fact]
    public async Task BusinessReadMissingFileDoesNotInvokeEntityAdapterOrStorage()
    {
        var entity = new FileEntityReference("wms-warehouse", Guid.NewGuid());
        var authority = new BusinessAuthority();
        var store = new Store();
        await using var db = Open(_tenant);

        var result = await Business(db, entity, authority, store).DownloadAsync(_context, Guid.NewGuid(), Guid.NewGuid());

        Assert.Equal(FileAccessDecision.NotFound, result.Decision);
        Assert.Null(result.Download);
        Assert.Equal(0, store.Reads);
        Assert.Equal(0, authority.Calls);
    }

    private async Task BindBusinessAsync(FileEntityReference entity)
    {
        await using var db = Open(_tenant);
        var file = await db.Files.SingleAsync(item => item.Id == _file);
        file.AddReference(_clock.GetUtcNow());
        db.FileReferences.Add(new(_tenant, _file, entity.Owner, entity.EntityId));
        await db.SaveChangesAsync();
    }

    private BusinessFileAccess Business(PlatformDbContext db, FileEntityReference entity,
        IBusinessFileAuthorization authority, Store? store = null) =>
        new(db, entity, authority, store ?? new Store(), new Scanner(), _clock, NullLoggerFactory.Instance);

    private sealed class BusinessAuthority : IBusinessFileAuthorization
    {
        public bool Allowed { get; set; } = true;
        public int Calls { get; private set; }
        public FileEntityReference? LastEntity { get; private set; }
        public TenantContext? LastContext { get; private set; }
        public Exception? Failure { get; set; }

        public Task<bool> CanReadAsync(TenantContext context, FileEntityReference entity, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (Failure is not null) throw Failure;
            Calls++;
            LastEntity = entity;
            LastContext = context;
            return Task.FromResult(Allowed);
        }
    }
}
