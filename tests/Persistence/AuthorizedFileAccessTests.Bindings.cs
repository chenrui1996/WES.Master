using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
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
    public void BusinessBindingMigrationMetadataUsesActualOwnerHistoryWithoutAConnection()
    {
        using var design = new PlatformDesignTimeFactory().CreateDbContext([]);

        var history = design.GetService<Microsoft.EntityFrameworkCore.Migrations.IHistoryRepository>().GetCreateScript();

        Assert.Contains("__WesPlatformMigrations", history, StringComparison.Ordinal);
        Assert.Empty(design.Database.GetDbConnection().ConnectionString);
        Assert.False(design.Database.HasPendingModelChanges());
    }

    [Fact]
    public async Task BusinessBindingAndUnbindingCommitReferenceFenceAuditAndPermanentReceipt()
    {
        await BindingGrantsAsync();
        var entity = new FileEntityReference("wms-warehouse", Guid.NewGuid());
        var authority = new BusinessBindingAuthority();
        string version;
        await using (var read = Open(_tenant)) version = (await read.Files.SingleAsync()).Version;
        FileBindingResult bound;
        await using (var db = Open(_tenant))
            bound = await Bindings(db, entity, authority).BindAsync(_context, _file, Guid.NewGuid(), Guid.NewGuid(), version);

        Assert.Equal(FileAccessDecision.Success, bound.Decision);
        Assert.Equal(_file, bound.Id);
        Assert.True(bound.Attached);
        Assert.NotEqual(version, bound.Version);
        await using (var verify = Open(_tenant))
        {
            Assert.Equal(1, (await verify.Files.SingleAsync()).ReferenceCount);
            Assert.Single(await verify.FileReferences.ToListAsync());
            Assert.Single(await verify.FileBindingOperations.ToListAsync());
            Assert.Equal(PlatformAuditAction.FileAttached, (await verify.Audit.SingleAsync()).Action);
        }
        await using (var db = Open(_tenant))
        {
            var detached = await Bindings(db, entity, authority)
                .UnbindAsync(_context, _file, Guid.NewGuid(), Guid.NewGuid(), bound.Version);
            Assert.Equal(FileAccessDecision.Success, detached.Decision);
            Assert.False(detached.Attached);
            Assert.NotEqual(bound.Version, detached.Version);
        }
        await using var final = Open(_tenant);
        Assert.Equal(0, (await final.Files.SingleAsync()).ReferenceCount);
        Assert.Empty(await final.FileReferences.ToListAsync());
        Assert.Equal(2, await final.FileBindingOperations.CountAsync());
        Assert.Equal(2, await final.Audit.CountAsync());
    }

    [Fact]
    public async Task BusinessBindingReplayReturnsOriginalReceiptWithoutRestoringLaterMembership()
    {
        await BindingGrantsAsync();
        var entity = new FileEntityReference("wms-location", Guid.NewGuid());
        var authority = new BusinessBindingAuthority();
        var operation = Guid.NewGuid();
        var version = await BindingVersionAsync();
        FileBindingResult first;
        await using (var db = Open(_tenant))
            first = await Bindings(db, entity, authority).BindAsync(_context, _file, operation, Guid.NewGuid(), version);
        await using (var db = Open(_tenant))
            Assert.Equal(FileAccessDecision.Success, (await Bindings(db, entity, authority)
                .UnbindAsync(_context, _file, Guid.NewGuid(), Guid.NewGuid(), first.Version)).Decision);

        await using (var db = Open(_tenant))
        {
            var replay = await Bindings(db, entity, authority).BindAsync(_context, _file, operation, Guid.NewGuid(), version);
            Assert.Equal(first with { Replayed = true }, replay);
        }

        await using var verify = Open(_tenant);
        Assert.Empty(await verify.FileReferences.ToListAsync());
        Assert.Equal(0, (await verify.Files.SingleAsync()).ReferenceCount);
        Assert.Equal(2, await verify.Audit.CountAsync());
        Assert.Equal(2, await verify.FileBindingOperations.CountAsync());
    }

    [Theory]
    [InlineData("owner")]
    [InlineData("entity")]
    [InlineData("file")]
    [InlineData("version")]
    [InlineData("action")]
    [InlineData("actor")]
    public async Task BusinessBindingPermanentKeyRejectsEveryChangedSemanticArgument(string changed)
    {
        await BindingGrantsAsync();
        var entity = new FileEntityReference("wms-warehouse", Guid.NewGuid());
        var operation = Guid.NewGuid();
        var version = await BindingVersionAsync();
        var authority = new BusinessBindingAuthority();
        await using (var db = Open(_tenant))
            Assert.Equal(FileAccessDecision.Success, (await Bindings(db, entity, authority)
                .BindAsync(_context, _file, operation, Guid.NewGuid(), version)).Decision);
        var target = changed switch
        {
            "owner" => new FileEntityReference("wms-location", entity.EntityId),
            "entity" => new FileEntityReference(entity.Owner, Guid.NewGuid()),
            _ => entity
        };
        var file = changed == "file" ? await BindingFileAsync(available: true) : _file;
        var context = _context;
        if (changed == "actor")
        {
            await using var seed = Open(_tenant);
            var session = new Session(_tenant, _creator, SessionClient.Web, _clock.GetUtcNow(), new string('B', 64));
            seed.Sessions.Add(session);
            seed.Memberships.Add(new(_tenant, _creator, (await seed.Roles.SingleAsync()).Id));
            await seed.SaveChangesAsync();
            context = new(_tenant, _creator, session.Id);
        }
        await using var owner = Open(_tenant);

        var result = changed == "action"
            ? await Bindings(owner, target, authority).UnbindAsync(context, file, operation, Guid.NewGuid(), version)
            : await Bindings(owner, target, authority).BindAsync(context, file, operation, Guid.NewGuid(),
                changed == "version" ? Guid.NewGuid().ToString("D") : version);

        Assert.Equal(FileAccessDecision.IdempotencyConflict, result.Decision);
        Assert.Null(result.Version);
        await using var verify = Open(_tenant);
        Assert.Single(await verify.FileReferences.ToListAsync());
        Assert.Single(await verify.Audit.ToListAsync());
        Assert.Single(await verify.FileBindingOperations.ToListAsync());
    }

    [Theory]
    [InlineData("entity")]
    [InlineData("file")]
    [InlineData("session")]
    public async Task BusinessBindingReplayAlwaysRechecksLiveAuthority(string revoked)
    {
        await BindingGrantsAsync();
        var entity = new FileEntityReference("wms-warehouse", Guid.NewGuid());
        var authority = new BusinessBindingAuthority();
        var operation = Guid.NewGuid();
        var version = await BindingVersionAsync();
        await using (var db = Open(_tenant))
            Assert.Equal(FileAccessDecision.Success, (await Bindings(db, entity, authority)
                .BindAsync(_context, _file, operation, Guid.NewGuid(), version)).Decision);
        if (revoked == "entity") authority.Allowed = false;
        else if (revoked == "file") await SetGrantsAsync(Grants());
        else
        {
            await using var change = Open(_tenant);
            (await change.Sessions.SingleAsync()).Revoke(_clock.GetUtcNow());
            await change.SaveChangesAsync();
        }
        await using var owner = Open(_tenant);

        var result = await Bindings(owner, entity, authority).BindAsync(_context, _file, operation, Guid.NewGuid(), version);

        Assert.Equal(FileAccessDecision.Forbidden, result.Decision);
        Assert.Null(result.Version);
        await using var verify = Open(_tenant);
        Assert.Single(await verify.FileReferences.ToListAsync());
        Assert.Single(await verify.FileBindingOperations.ToListAsync());
    }

    [Theory]
    [InlineData(null, FileAccessDecision.VersionRequired)]
    [InlineData("", FileAccessDecision.VersionRequired)]
    [InlineData("invalid", FileAccessDecision.Invalid)]
    [InlineData("00000000-0000-0000-0000-000000000000", FileAccessDecision.Invalid)]
    [InlineData("11111111-1111-1111-1111-111111111111", FileAccessDecision.VersionConflict)]
    public async Task BusinessBindingRequiresExactOpaqueFileVersion(string? version, FileAccessDecision decision)
    {
        await BindingGrantsAsync();
        var entity = new FileEntityReference("wms-warehouse", Guid.NewGuid());
        await using var db = Open(_tenant);

        var result = await Bindings(db, entity, new BusinessBindingAuthority())
            .BindAsync(_context, _file, Guid.NewGuid(), Guid.NewGuid(), version);

        Assert.Equal(decision, result.Decision);
        await AssertNoBindingEffectAsync();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BusinessBindingDoesNotTreatExistingMembershipAsANewOperation(bool attached)
    {
        await BindingGrantsAsync();
        var entity = new FileEntityReference("wms-warehouse", Guid.NewGuid());
        if (attached) await BindBusinessAsync(entity);
        var version = await BindingVersionAsync();
        await using var db = Open(_tenant);

        var result = attached
            ? await Bindings(db, entity, new BusinessBindingAuthority()).BindAsync(_context, _file, Guid.NewGuid(), Guid.NewGuid(), version)
            : await Bindings(db, entity, new BusinessBindingAuthority()).UnbindAsync(_context, _file, Guid.NewGuid(), Guid.NewGuid(), version);

        Assert.Equal(FileAccessDecision.StateConflict, result.Decision);
        await using var verify = Open(_tenant);
        Assert.Empty(await verify.FileBindingOperations.ToListAsync());
        Assert.Empty(await verify.Audit.ToListAsync());
        Assert.Equal(attached ? 1 : 0, await verify.FileReferences.CountAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BusinessBindingDoesNotAttachUnscannedOrUnstoredContent(bool stored)
    {
        await BindingGrantsAsync();
        var file = await BindingFileAsync(available: false, stored);
        var version = await BindingVersionAsync(file);
        await using var db = Open(_tenant);

        var result = await Bindings(db, new("wms-warehouse", Guid.NewGuid()), new BusinessBindingAuthority())
            .BindAsync(_context, file, Guid.NewGuid(), Guid.NewGuid(), version);

        Assert.Equal(FileAccessDecision.StateConflict, result.Decision);
        await AssertNoBindingEffectAsync();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BusinessBindingFailureRollsBackReferenceCountVersionAuditAndReceipt(bool cancel)
    {
        await BindingGrantsAsync();
        var version = await BindingVersionAsync();
        using var cancellation = new CancellationTokenSource();
        await using var db = Open(_tenant, cancel ? new CancelAudit(cancellation) : new AuditFailure());
        var binding = Bindings(db, new("wms-warehouse", Guid.NewGuid()), new BusinessBindingAuthority());

        if (cancel)
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                binding.BindAsync(_context, _file, Guid.NewGuid(), Guid.NewGuid(), version, cancellation.Token));
        else
        {
            var error = await Assert.ThrowsAsync<FileAccessException>(() =>
                binding.BindAsync(_context, _file, Guid.NewGuid(), Guid.NewGuid(), version));
            Assert.DoesNotContain("sensitive-sentinel", error.ToString(), StringComparison.Ordinal);
        }

        await AssertNoBindingEffectAsync();
        Assert.Equal(version, await BindingVersionAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BusinessBindingConcurrentSameVersionCommitsOnlyOneEffectAndRecoversOnlySameOperation(bool sameOperation)
    {
        await BindingGrantsAsync();
        var entity = new FileEntityReference("wms-warehouse", Guid.NewGuid());
        var version = await BindingVersionAsync();
        var operation = Guid.NewGuid();
        var rendezvous = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var arrivals = 0;
        var failures = new System.Collections.Concurrent.ConcurrentQueue<int>();
        async Task<FileBindingResult> Run(Guid key)
        {
            var authority = new BusinessBindingAuthority
            {
                BeforeDecision = async call =>
                {
                    if (call != 2) return;
                    if (Interlocked.Increment(ref arrivals) == 2) rendezvous.SetResult();
                    await rendezvous.Task.WaitAsync(TimeSpan.FromSeconds(15));
                }
            };
            await using var owner = Open(_tenant, new BindingFailureCodes(failures));
            return await Bindings(owner, entity, authority).BindAsync(_context, _file, key, Guid.NewGuid(), version);
        }

        FileBindingResult[] results;
        try { results = await Task.WhenAll(Run(operation), Run(sameOperation ? operation : Guid.NewGuid())); }
        catch (FileAccessException error)
        {
            throw new Xunit.Sdk.XunitException($"Binding concurrency failed; transient={error.IsTransient}; provider codes={string.Join(',', failures)}.");
        }

        Assert.Equal(sameOperation ? 2 : 1, results.Count(result => result.Decision == FileAccessDecision.Success));
        Assert.Single(results, result => result.Decision == FileAccessDecision.Success && !result.Replayed);
        if (sameOperation)
        {
            Assert.Single(results, result => result.Replayed);
            Assert.Equal(results[0].Version, results[1].Version);
        }
        else Assert.Single(results, result => result.Decision == FileAccessDecision.VersionConflict);
        await using var verify = Open(_tenant);
        Assert.Equal(1, (await verify.Files.SingleAsync()).ReferenceCount);
        Assert.Single(await verify.FileReferences.ToListAsync());
        Assert.Single(await verify.FileBindingOperations.ToListAsync());
        Assert.Single(await verify.Audit.ToListAsync());
    }

    [Fact]
    public async Task BusinessBindingDetectsReferenceCountCorruptionWithoutRepairingItAsSuccess()
    {
        await BindingGrantsAsync();
        await using (var corrupt = Open(_tenant))
        {
            corrupt.FileReferences.Add(new(_tenant, _file, "other-owner", Guid.NewGuid()));
            await corrupt.SaveChangesAsync();
        }
        await using var db = Open(_tenant);
        var version = await BindingVersionAsync();

        await Assert.ThrowsAsync<FileAccessException>(() => Bindings(db, new("wms-warehouse", Guid.NewGuid()),
            new BusinessBindingAuthority()).BindAsync(_context, _file, Guid.NewGuid(), Guid.NewGuid(), version));

        await using var verify = Open(_tenant);
        Assert.Equal(0, (await verify.Files.SingleAsync()).ReferenceCount);
        Assert.Single(await verify.FileReferences.ToListAsync());
        Assert.Empty(await verify.FileBindingOperations.ToListAsync());
        Assert.Empty(await verify.Audit.ToListAsync());
    }

    [Fact]
    public async Task BusinessBindingPermanentReceiptCannotBeEditedOrDeletedByNormalOwnerWrites()
    {
        await BindingGrantsAsync();
        var entity = new FileEntityReference("wms-warehouse", Guid.NewGuid());
        var version = await BindingVersionAsync();
        await using (var db = Open(_tenant))
            await Bindings(db, entity, new BusinessBindingAuthority()).BindAsync(_context, _file, Guid.NewGuid(), Guid.NewGuid(), version);
        await using (var edit = Open(_tenant))
        {
            var receipt = await edit.FileBindingOperations.SingleAsync();
            edit.Entry(receipt).Property(item => item.ResultVersion).CurrentValue = Guid.NewGuid().ToString("D");
            await Assert.ThrowsAsync<InvalidOperationException>(() => edit.SaveChangesAsync());
        }
        await using (var remove = Open(_tenant))
        {
            remove.FileBindingOperations.Remove(await remove.FileBindingOperations.SingleAsync());
            await Assert.ThrowsAsync<InvalidOperationException>(() => remove.SaveChangesAsync());
        }
        await using var verify = Open(_tenant);
        Assert.Single(await verify.FileBindingOperations.ToListAsync());
    }

    [Fact]
    public async Task BusinessBindingReceiptSurvivesOrdinaryNinetyDayRetentionAndUsesANewLiveSession()
    {
        await BindingGrantsAsync();
        var entity = new FileEntityReference("wms-location", Guid.NewGuid());
        var operation = Guid.NewGuid();
        var version = await BindingVersionAsync();
        FileBindingResult first;
        await using (var db = Open(_tenant))
            first = await Bindings(db, entity, new BusinessBindingAuthority())
                .BindAsync(_context, _file, operation, Guid.NewGuid(), version);
        _clock.Now = _clock.GetUtcNow().AddDays(91);
        TenantContext context;
        await using (var renew = Open(_tenant))
        {
            var session = new Session(_tenant, _actor, SessionClient.Web, _clock.GetUtcNow(), new string('C', 64));
            renew.Sessions.Add(session);
            await renew.SaveChangesAsync();
            context = new(_tenant, _actor, session.Id);
        }
        await using var owner = Open(_tenant);

        var replay = await Bindings(owner, entity, new BusinessBindingAuthority())
            .BindAsync(context, _file, operation, Guid.NewGuid(), version);

        Assert.Equal(first with { Replayed = true }, replay);
        await using var verify = Open(_tenant);
        Assert.Single(await verify.FileBindingOperations.ToListAsync());
        Assert.Single(await verify.Audit.ToListAsync());
        Assert.Single(await verify.FileReferences.ToListAsync());
    }

    [Fact]
    public async Task BusinessBindingRechecksEntityAuthorityAfterParentLockBeforeAnyEffect()
    {
        await BindingGrantsAsync();
        var version = await BindingVersionAsync();
        var authority = new BusinessBindingAuthority();
        authority.BeforeDecision = call =>
        {
            if (call == 3) authority.Allowed = false;
            return Task.CompletedTask;
        };
        await using var db = Open(_tenant);

        var result = await Bindings(db, new("wms-warehouse", Guid.NewGuid()), authority)
            .BindAsync(_context, _file, Guid.NewGuid(), Guid.NewGuid(), version);

        Assert.Equal(FileAccessDecision.Forbidden, result.Decision);
        await AssertNoBindingEffectAsync();
        Assert.Equal(version, await BindingVersionAsync());
    }

    [Fact]
    public async Task BusinessBindingCannotExposeBoundContentThroughANewTargetAfterSourceReadIsRevoked()
    {
        await BindingGrantsAsync();
        var source = new FileEntityReference("wms-warehouse", Guid.NewGuid());
        var target = new FileEntityReference("wms-location", Guid.NewGuid());
        await BindBusinessAsync(source);
        var version = await BindingVersionAsync();
        var authority = new BusinessBindingAuthority();
        authority.ForbiddenReads.Add(source.EntityId);
        var operation = Guid.NewGuid();
        await using (var db = Open(_tenant))
        {
            var denied = await Bindings(db, target, authority).BindAsync(_context, _file, operation, Guid.NewGuid(), version);
            Assert.Equal(FileAccessDecision.Forbidden, denied.Decision);
        }
        await using (var verify = Open(_tenant))
        {
            Assert.Equal(1, (await verify.Files.SingleAsync()).ReferenceCount);
            Assert.Single(await verify.FileReferences.ToListAsync());
            Assert.Empty(await verify.FileBindingOperations.ToListAsync());
        }
        authority.ForbiddenReads.Clear();

        await using (var db = Open(_tenant))
            Assert.Equal(FileAccessDecision.Success, (await Bindings(db, target, authority)
                .BindAsync(_context, _file, operation, Guid.NewGuid(), version)).Decision);

        var current = await BindingVersionAsync();
        authority.ForbiddenReads.Add(target.EntityId);
        await using (var db = Open(_tenant))
            Assert.Equal(FileAccessDecision.Forbidden, (await Bindings(db, new("wms-warehouse", Guid.NewGuid()), authority)
                .BindAsync(_context, _file, Guid.NewGuid(), Guid.NewGuid(), current)).Decision);
        await using (var verify = Open(_tenant))
        {
            Assert.Equal(2, (await verify.Files.SingleAsync()).ReferenceCount);
            Assert.Equal(2, await verify.FileReferences.CountAsync());
            Assert.Single(await verify.FileBindingOperations.ToListAsync());
        }
        authority.ForbiddenReads.Clear();
        authority.ForbiddenReads.Add(source.EntityId);
        await using (var db = Open(_tenant))
            Assert.Equal(FileAccessDecision.Success, (await Bindings(db, target, authority)
                .UnbindAsync(_context, _file, Guid.NewGuid(), Guid.NewGuid(), current)).Decision);
        await using var final = Open(_tenant);
        Assert.Equal(1, (await final.Files.SingleAsync()).ReferenceCount);
        Assert.Equal(source.EntityId, (await final.FileReferences.SingleAsync()).EntityId);
        Assert.Equal(2, await final.FileBindingOperations.CountAsync());
    }

    private async Task<string> BindingVersionAsync(Guid? id = null)
    {
        await using var db = Open(_tenant);
        return (await db.Files.SingleAsync(item => item.Id == (id ?? _file))).Version;
    }

    private async Task AssertNoBindingEffectAsync()
    {
        await using var verify = Open(_tenant);
        Assert.Equal(0, (await verify.Files.SingleAsync(item => item.Id == _file)).ReferenceCount);
        Assert.Empty(await verify.FileReferences.ToListAsync());
        Assert.Empty(await verify.FileBindingOperations.ToListAsync());
        Assert.Empty(await verify.Audit.ToListAsync());
    }

    private async Task<Guid> BindingFileAsync(bool available, bool stored = true)
    {
        var file = new QuarantinedFile(_tenant, _creator, Guid.NewGuid(), "another.txt", "text/plain", 4,
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData("safe"u8)), _clock.GetUtcNow().AddDays(-1));
        if (stored) file.ConfirmStored(file.Sha256, 4, _clock.GetUtcNow());
        if (available)
        {
            var attempt = file.BeginScan(_clock.GetUtcNow());
            file.CompleteScan(attempt, FileScanVerdict.Clean, file.Sha256, 4, _clock.GetUtcNow());
        }
        await using var db = Open(_tenant);
        db.Files.Add(file);
        await db.SaveChangesAsync();
        return file.Id;
    }

    private async Task BindingGrantsAsync() =>
        await SetGrantsAsync(Grants().Select(grant => grant.Permission == "platform.files.write"
            ? grant with { Fields = grant.Fields.Union(["Attach", "Detach"]) } : grant));

    private BusinessFileBindings Bindings(PlatformDbContext db, FileEntityReference entity,
        IBusinessFileBindingAuthorization authority) => new(db, entity, authority, _clock, NullLoggerFactory.Instance);

    private sealed class BusinessBindingAuthority : IBusinessFileBindingAuthorization
    {
        private int _calls;
        public bool Allowed { get; set; } = true;
        public HashSet<Guid> ForbiddenReads { get; } = [];
        public Func<int, Task>? BeforeDecision { get; set; }
        public Task<bool> CanReadAsync(TenantContext context, FileEntityReference entity, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(Allowed && !ForbiddenReads.Contains(entity.EntityId));
        }
        public async Task<bool> CanChangeAsync(TenantContext context, FileEntityReference entity, FileBindingAction action, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            var call = Interlocked.Increment(ref _calls);
            if (BeforeDecision is not null) await BeforeDecision(call);
            ct.ThrowIfCancellationRequested();
            return Allowed;
        }
    }

    private sealed class BindingFailureCodes(System.Collections.Concurrent.ConcurrentQueue<int> failures) : SaveChangesInterceptor
    {
        public override Task SaveChangesFailedAsync(DbContextErrorEventData eventData,
            CancellationToken cancellationToken = default)
        {
            for (Exception? error = eventData.Exception; error is not null; error = error.InnerException)
                if (error is MySql.Data.MySqlClient.MySqlException mysql) failures.Enqueue(mysql.Number);
            return Task.CompletedTask;
        }
    }
}
