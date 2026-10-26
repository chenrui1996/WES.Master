using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Wes.Platform.Domain;
using Wes.Platform.Features.Files;
using Wes.Platform.Features.Tenancy;
using Wes.Platform.Infrastructure.Files;
using Wes.Platform.Infrastructure.Persistence;
using Xunit;

namespace Wes.Persistence.Tests;

[Collection("PlatformPersistence")]
[Trait("Provider", "MySQL")]
public sealed partial class AuthorizedFileAccessTests : IAsyncLifetime
{
    private readonly Guid _tenant = Guid.NewGuid();
    private readonly Guid _other = Guid.NewGuid();
    private readonly Guid _actor = Guid.NewGuid();
    private readonly Guid _creator = Guid.NewGuid();
    private readonly Clock _clock = new();
    private string _connection = "";
    private TenantContext _context = null!;
    private Guid _department;
    private Guid _file;
    private bool _ready;
    private static readonly string[] ReadFields =
        ["Id", "FileName", "ContentType", "ByteCount", "State", "Stored", "Version", "CreatedAt", "ProcessingProblem"];

    public async Task InitializeAsync()
    {
        _connection = MySqlTestConnection.LoadAuthorizedTestConnection();
        await using var db = Open(_tenant);
        await db.Database.MigrateAsync();
        var department = new DirectoryDepartment(_tenant, "files", "Files");
        _department = department.Id;
        var session = new Session(_tenant, _actor, SessionClient.Web, _clock.GetUtcNow(), new string('A', 64));
        _context = new(_tenant, _actor, session.Id);
        var role = new AccessRole(_tenant, "file-owner", Grants());
        db.DirectoryTenants.Add(new DirectoryTenant(_tenant, "File authorization"));
        db.Departments.Add(department);
        db.DirectoryUsers.AddRange(new DirectoryUser(_tenant, _actor, "ACTOR", "Actor", _department),
            new DirectoryUser(_tenant, _creator, "CREATOR", "Creator", _department));
        db.Sessions.Add(session);
        db.Roles.Add(role);
        db.Memberships.Add(new(_tenant, _actor, role.Id));
        var file = new QuarantinedFile(_tenant, _creator, Guid.NewGuid(), "private.txt", "text/plain", 4,
            Convert.ToHexString(SHA256.HashData("safe"u8)), _clock.GetUtcNow().AddDays(-1));
        file.ConfirmStored(file.Sha256, 4, _clock.GetUtcNow());
        var attempt = file.BeginScan(_clock.GetUtcNow());
        file.CompleteScan(attempt, FileScanVerdict.Clean, file.Sha256, 4, _clock.GetUtcNow());
        db.Files.Add(file);
        _file = file.Id;
        await db.SaveChangesAsync();
        _ready = true;
    }

    [Fact]
    public async Task DownloadReturnsVerifiedBytesOnlyAfterImmutableAuditAndNeverSerializesContent()
    {
        await using var db = Open(_tenant);
        var result = await Access(db, new Store()).DownloadAsync(_context, _file, Guid.NewGuid());
        Assert.Equal(FileAccessDecision.Success, result.Decision);
        using var download = Assert.IsType<FileDownload>(result.Download);
        Assert.Equal("safe"u8.ToArray(), download.Content.ToArray());
        Assert.Equal("private.txt", download.FileName);
        Assert.DoesNotContain("safe", JsonSerializer.Serialize(download), StringComparison.Ordinal);
        Assert.DoesNotContain("safe", download.ToString(), StringComparison.Ordinal);
        var memory = download.Content;
        download.Dispose();
        Assert.All(memory.ToArray(), value => Assert.Equal((byte)0, value));
        await using var verify = Open(_tenant);
        var audit = Assert.Single(await verify.Audit.ToListAsync());
        Assert.Equal(PlatformAuditAction.FileDownloaded, audit.Action);
        Assert.Equal(_actor, audit.ActorId);
        Assert.Equal(_file, audit.TargetId);
        Assert.Equal(audit.BeforeVersion, audit.AfterVersion);
    }

    [Theory]
    [InlineData(DataScope.Own, false)]
    [InlineData(DataScope.Departments, true)]
    [InlineData(DataScope.All, true)]
    public async Task MetadataUsesPersistedCreatorAndCurrentDepartment(DataScope scope, bool allowed)
    {
        await SetGrantsAsync(Grants(scope));
        await using var db = Open(_tenant);
        var result = await Access(db, new Store()).ReadAsync(_context, _file);
        Assert.Equal(allowed ? FileAccessDecision.Success : FileAccessDecision.Forbidden, result.Decision);
        Assert.Equal(allowed, result.File is not null);
        if (allowed)
        {
            Assert.Equal(_file, result.File!.Id);
            Assert.Equal(QuarantinedFileState.Available, result.File.State);
        }
    }

    [Fact]
    public async Task OwnScopeAuthorizesOnlyTheRealUploader()
    {
        await SetGrantsAsync(Grants(DataScope.Own));
        await using (var db = Open(_tenant))
        {
            var session = new Session(_tenant, _creator, SessionClient.Web, _clock.GetUtcNow(), new string('B', 64));
            db.Sessions.Add(session);
            db.Memberships.Add(new(_tenant, _creator, (await db.Roles.SingleAsync()).Id));
            await db.SaveChangesAsync();
            _context = new(_tenant, _creator, session.Id);
        }
        await using var read = Open(_tenant);
        Assert.Equal(FileAccessDecision.Success, (await Access(read, new Store()).ReadAsync(_context, _file)).Decision);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingContentOrMetadataGrantCannotReachStorage(bool missingRead)
    {
        var grants = missingRead
            ? new[] { Grant("platform.files.read", DataScope.All, ["Id"]), Grant("platform.files.download", DataScope.All, ["Content"]) }
            : new[] { Grant("platform.files.read", DataScope.All, ReadFields) };
        await SetGrantsAsync(grants);
        var store = new Store();
        await using var db = Open(_tenant);
        var result = await Access(db, store).DownloadAsync(_context, _file, Guid.NewGuid());
        Assert.Equal(FileAccessDecision.Forbidden, result.Decision);
        Assert.Null(result.Download);
        Assert.Equal(0, store.Reads);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RevokedSessionOrRoleCannotReadFileMetadata(bool session)
    {
        await using (var db = Open(_tenant))
        {
            if (session) (await db.Sessions.SingleAsync()).Revoke(_clock.GetUtcNow());
            else
            {
                var role = await db.Roles.SingleAsync();
                role.Change(role.Version, false, role.Rules);
            }
            await db.SaveChangesAsync();
        }
        await using var read = Open(_tenant);
        Assert.Equal(FileAccessDecision.Forbidden, (await Access(read, new Store()).ReadAsync(_context, _file)).Decision);
    }

    [Fact]
    public async Task ExistingBusinessReferenceIsNotAuthorizedByFileCreatorPermission()
    {
        await using (var db = Open(_tenant))
        {
            db.FileReferences.Add(new(_tenant, _file, "Wms", Guid.NewGuid()));
            await db.SaveChangesAsync();
        }
        var store = new Store();
        await using var read = Open(_tenant);
        Assert.Equal(FileAccessDecision.Forbidden, (await Access(read, store).DownloadAsync(_context, _file, Guid.NewGuid())).Decision);
        Assert.Equal(0, store.Reads);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AuthorityOrBindingChangesDuringStorageReadPreventDelivery(bool binding)
    {
        var store = new Store(async () =>
        {
            await using var change = Open(_tenant);
            if (binding) change.FileReferences.Add(new(_tenant, _file, "Wms", Guid.NewGuid()));
            else
            {
                var role = await change.Roles.SingleAsync();
                role.Change(role.Version, false, role.Rules);
            }
            await change.SaveChangesAsync();
        });
        await using var db = Open(_tenant);
        var result = await Access(db, store).DownloadAsync(_context, _file, Guid.NewGuid());
        Assert.Equal(FileAccessDecision.Forbidden, result.Decision);
        Assert.Null(result.Download);
        Assert.All(store.LastBuffer!, value => Assert.Equal((byte)0, value));
        await using var verify = Open(_tenant);
        Assert.Empty(await verify.Audit.ToListAsync());
    }

    [Fact]
    public async Task AuditSaveFailureCannotDeliverDownloadedBytesOrProviderDetails()
    {
        var store = new Store();
        await using var db = Open(_tenant, new AuditFailure());
        var error = await Assert.ThrowsAsync<FileAccessException>(() =>
            Access(db, store).DownloadAsync(_context, _file, Guid.NewGuid()));
        Assert.DoesNotContain("sensitive-sentinel", error.ToString(), StringComparison.Ordinal);
        Assert.All(store.LastBuffer!, value => Assert.Equal((byte)0, value));
        await using var verify = Open(_tenant);
        Assert.Empty(await verify.Audit.ToListAsync());
    }

    [Fact]
    public async Task ReadPermissionCannotTriggerScanOrDeletion()
    {
        await SetGrantsAsync([Grant("platform.files.read", DataScope.All, ReadFields)]);
        var store = new Store();
        var scanner = new Scanner();
        await using var db = Open(_tenant);
        var version = (await db.Files.AsNoTracking().SingleAsync()).Version;
        var service = Access(db, store, scanner);
        Assert.Equal(FileAccessDecision.Forbidden, (await service.ScanAsync(_context, _file, version)).Decision);
        Assert.Equal(FileAccessDecision.Forbidden, (await service.DeleteOrphanAsync(_context, _file, version)).Decision);
        Assert.Equal(0, store.Deletes);
        Assert.Equal(0, scanner.Calls);
    }

    [Fact]
    public async Task AFileFromAnotherTenantIsNotFoundAndCannotReachStorage()
    {
        var store = new Store();
        await using var db = Open(_other);
        Assert.Equal(FileAccessDecision.NotFound,
            (await Access(db, store).ReadAsync(new(_other, _actor, _context.SessionId), _file)).Decision);
        Assert.Equal(0, store.Reads);
    }

    [Fact]
    public async Task MovingTheCreatorOutOfAnAuthorizedDepartmentRevokesMetadataAccess()
    {
        await SetGrantsAsync(Grants(DataScope.Departments));
        await using (var db = Open(_tenant))
        {
            var department = new DirectoryDepartment(_tenant, "other", "Other");
            db.Departments.Add(department);
            var user = await db.DirectoryUsers.SingleAsync(item => item.Id == _creator);
            user.Change(user.Version, user.Name, department.Id, user.Enabled);
            await db.SaveChangesAsync();
        }
        await using var read = Open(_tenant);
        Assert.Equal(FileAccessDecision.Forbidden, (await Access(read, new Store()).ReadAsync(_context, _file)).Decision);
    }

    [Fact]
    public async Task AvailableVersionChangeDuringReadReturnsNoContentAndClearsBuffer()
    {
        var store = new Store(async () =>
        {
            await using var change = Open(_tenant);
            var file = await change.Files.SingleAsync();
            file.AddReference(_clock.GetUtcNow());
            file.RemoveReference(_clock.GetUtcNow());
            await change.SaveChangesAsync();
        });
        await using var read = Open(_tenant);
        var result = await Access(read, store).DownloadAsync(_context, _file, Guid.NewGuid());
        Assert.Equal(FileAccessDecision.VersionConflict, result.Decision);
        Assert.Null(result.Download);
        Assert.All(store.LastBuffer!, value => Assert.Equal((byte)0, value));
        await using var verify = Open(_tenant);
        Assert.Empty(await verify.Audit.ToListAsync());
    }

    [Fact]
    public async Task EvenAStoragePortReturningWrongBytesCannotDeliverOrAuditThem()
    {
        var store = new Store { Content = "evil"u8.ToArray() };
        await using var read = Open(_tenant);
        await Assert.ThrowsAsync<FileAccessException>(() =>
            Access(read, store).DownloadAsync(_context, _file, Guid.NewGuid()));
        Assert.All(store.LastBuffer!, value => Assert.Equal((byte)0, value));
        await using var verify = Open(_tenant);
        Assert.Empty(await verify.Audit.ToListAsync());
    }

    [Fact]
    public async Task QuarantinedFileDoesNotReachStorageUntilAuthorizedScanHasCommittedClean()
    {
        Guid pendingId;
        string version;
        await using (var seed = Open(_tenant))
        {
            var file = new QuarantinedFile(_tenant, _creator, Guid.NewGuid(), "pending.txt", "text/plain", 4,
                Convert.ToHexString(SHA256.HashData("safe"u8)), _clock.GetUtcNow());
            file.ConfirmStored(file.Sha256, 4, _clock.GetUtcNow());
            pendingId = file.Id;
            version = file.Version;
            seed.Files.Add(file);
            await seed.SaveChangesAsync();
        }
        var store = new Store();
        var scanner = new Scanner();
        await using (var read = Open(_tenant))
            Assert.Equal(FileAccessDecision.StateConflict,
                (await Access(read, store, scanner).DownloadAsync(_context, pendingId, Guid.NewGuid())).Decision);
        Assert.Equal(0, store.Reads);
        await using (var owner = Open(_tenant))
        {
            var result = await Access(owner, store, scanner).ScanAsync(_context, pendingId, version);
            Assert.Equal(FileAccessDecision.Success, result.Decision);
            Assert.Equal(QuarantinedFileState.Available, result.File!.State);
        }
        Assert.Equal(1, scanner.Calls);
        await using (var read = Open(_tenant))
        {
            var result = await Access(read, store).DownloadAsync(_context, pendingId, Guid.NewGuid());
            Assert.Equal(FileAccessDecision.Success, result.Decision);
            result.Download!.Dispose();
        }
    }

    [Fact]
    public async Task AuthorizedDeleteCommitsTombstoneAndDoesNotCallScanner()
    {
        var store = new Store();
        var scanner = new Scanner();
        await using var owner = Open(_tenant);
        var version = (await owner.Files.AsNoTracking().SingleAsync()).Version;
        var result = await Access(owner, store, scanner).DeleteOrphanAsync(_context, _file, version);
        Assert.Equal(FileAccessDecision.Success, result.Decision);
        Assert.Equal(QuarantinedFileState.Deleted, result.File!.State);
        Assert.Equal(1, store.Deletes);
        Assert.Equal(0, scanner.Calls);
        await using var verify = Open(_tenant);
        Assert.Equal(QuarantinedFileState.Deleted, (await verify.Files.SingleAsync()).State);
    }

    [Fact]
    public async Task CancellationAfterAuditSaveRollsBackAuditAndClearsUndeliveredContent()
    {
        using var cancellation = new CancellationTokenSource();
        var store = new Store();
        await using (var owner = Open(_tenant, new CancelAudit(cancellation)))
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                Access(owner, store).DownloadAsync(_context, _file, Guid.NewGuid(), cancellation.Token));
        Assert.All(store.LastBuffer!, value => Assert.Equal((byte)0, value));
        await using var verify = Open(_tenant);
        Assert.Empty(await verify.Audit.ToListAsync());
    }

    [Fact]
    public async Task AuthorizedUploadCommitsQuarantineAndOperationBeforeStorageAndNeverScansAutomatically()
    {
        await SetGrantsAsync([Grant("platform.files.write", DataScope.Own, ["Upload"])]);
        var operation = Guid.NewGuid();
        var correlation = Guid.NewGuid();
        var store = new Store
        {
            OnWrite = async id =>
            {
                await using var verifyClaim = Open(_tenant);
                var pending = await verifyClaim.Files.SingleAsync(item => item.Id == id);
                Assert.Equal(operation, pending.UploadOperationId);
                Assert.Equal(_actor, pending.CreatorId);
                Assert.False(pending.Stored);
                Assert.Equal(QuarantinedFileState.Quarantined, pending.State);
                Assert.Single(await verifyClaim.Audit.ToListAsync());
            }
        };
        var scanner = new Scanner();
        FileUploadResult uploaded;
        await using (var owner = Open(_tenant))
            uploaded = await Access(owner, store, scanner).UploadAsync(_context, operation, correlation,
                "new.txt", "Text/Plain; charset=utf-8", "safe"u8.ToArray());
        Assert.Equal(FileAccessDecision.Success, uploaded.Decision);
        Assert.Equal(QuarantinedFileState.Quarantined, uploaded.State);
        Assert.NotNull(uploaded.Id);
        Assert.Equal(1, store.Writes);
        Assert.Equal(0, scanner.Calls);
        await using var verify = Open(_tenant);
        var actual = await verify.Files.SingleAsync(item => item.Id == uploaded.Id);
        Assert.True(actual.Stored);
        Assert.Equal("text/plain", actual.ContentType);
        Assert.Equal("new.txt", actual.FileName);
        Assert.Equal(correlation, actual.CorrelationId);
        Assert.Equal(new[] { PlatformAuditAction.FileUploadCreated, PlatformAuditAction.FileUploadStored },
            (await verify.Audit.OrderBy(item => item.Action).ToListAsync()).Select(item => item.Action).ToArray());
    }

    [Fact]
    public async Task SameUploadOperationReturnsOriginalIdentityWhileChangedContentConflicts()
    {
        await SetGrantsAsync([Grant("platform.files.write", DataScope.Own, ["Upload"])]);
        var store = new Store();
        var operation = Guid.NewGuid();
        FileUploadResult first;
        await using (var owner = Open(_tenant))
            first = await Access(owner, store).UploadAsync(_context, operation, Guid.NewGuid(), "new.txt", "text/plain", "safe"u8.ToArray());
        await using (var owner = Open(_tenant))
        {
            var replay = await Access(owner, store).UploadAsync(_context, operation, Guid.NewGuid(), "new.txt", "text/plain", "safe"u8.ToArray());
            Assert.Equal(first.Id, replay.Id);
            Assert.Equal(first.Version, replay.Version);
            Assert.Equal(FileAccessDecision.Success, replay.Decision);
        }
        await using (var owner = Open(_tenant))
            Assert.Equal(FileAccessDecision.IdempotencyConflict, (await Access(owner, store).UploadAsync(
                _context, operation, Guid.NewGuid(), "new.txt", "text/plain", "evil"u8.ToArray())).Decision);
        Assert.Equal(1, store.Writes);
        await using var verify = Open(_tenant);
        Assert.Single(await verify.Files.Where(item => item.UploadOperationId == operation).ToListAsync());
        Assert.Equal(2, await verify.Audit.CountAsync());
    }

    [Fact]
    public async Task ReadGrantDoesNotCreateUploadIdentityOrTouchStorage()
    {
        await SetGrantsAsync([Grant("platform.files.read", DataScope.All, ReadFields)]);
        var store = new Store();
        await using var owner = Open(_tenant);
        Assert.Equal(FileAccessDecision.Forbidden, (await Access(owner, store).UploadAsync(
            _context, Guid.NewGuid(), Guid.NewGuid(), "new.txt", "text/plain", "safe"u8.ToArray())).Decision);
        Assert.Equal(0, store.Writes);
        await using var verify = Open(_tenant);
        Assert.Single(await verify.Files.ToListAsync());
        Assert.Empty(await verify.Audit.ToListAsync());
    }

    [Theory]
    [InlineData("stream:new.txt", "text/plain", "safe")]
    [InlineData("new.txt ", "text/plain", "safe")]
    [InlineData("new.pdf", "application/pdf", "safe")]
    [InlineData("new.txt", "text/plain", "<html>unsafe</html>")]
    [InlineData("new.txt", "text/plain", "")]
    public async Task InvalidUploadNeverCreatesAnOperationOrTouchesStorage(string name, string type, string text)
    {
        var store = new Store();
        await using var owner = Open(_tenant);
        var result = await Access(owner, store).UploadAsync(_context, Guid.NewGuid(), Guid.NewGuid(),
            name, type, System.Text.Encoding.UTF8.GetBytes(text));
        Assert.Equal(FileAccessDecision.Invalid, result.Decision);
        Assert.NotNull(result.Rejection);
        Assert.Equal(0, store.Writes);
        await using var verify = Open(_tenant);
        Assert.Single(await verify.Files.ToListAsync());
        Assert.Empty(await verify.Audit.ToListAsync());
    }

    [Fact]
    public async Task UploadOverTwentyMiBDoesNotAllocateADurableIdentity()
    {
        var store = new Store();
        await using var owner = Open(_tenant);
        var result = await Access(owner, store).UploadAsync(_context, Guid.NewGuid(), Guid.NewGuid(),
            "new.txt", "text/plain", new byte[20971521]);
        Assert.Equal(FileContentRejection.TooLarge, result.Rejection);
        Assert.Equal(0, store.Writes);
        await using var verify = Open(_tenant);
        Assert.Single(await verify.Files.ToListAsync());
    }

    [Fact]
    public async Task FailedCreationAuditCannotWriteAnExternalObjectOrLeaveAnUploadIdentity()
    {
        var store = new Store();
        await using (var owner = Open(_tenant, new AuditFailure()))
            await Assert.ThrowsAsync<FileAccessException>(() =>
                Access(owner, store).UploadAsync(_context, Guid.NewGuid(), Guid.NewGuid(), "new.txt", "text/plain", "safe"u8.ToArray()));
        Assert.Equal(0, store.Writes);
        await using var verify = Open(_tenant);
        Assert.Single(await verify.Files.ToListAsync());
        Assert.Empty(await verify.Audit.ToListAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RealStoredObjectSurvivesFailedConfirmationAndCanBeRecoveredWithoutOverwriting(bool cancel)
    {
        using var storage = new PrivateStore();
        using var cancellation = new CancellationTokenSource();
        var operation = Guid.NewGuid();
        await using (var owner = Open(_tenant, new StoredAuditFailure(cancel, cancellation)))
        {
            if (cancel)
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Access(owner, storage.Store).UploadAsync(
                    _context, operation, Guid.NewGuid(), "new.txt", "text/plain", "safe"u8.ToArray(), cancellation.Token));
            else
                await Assert.ThrowsAsync<FileAccessException>(() => Access(owner, storage.Store).UploadAsync(
                    _context, operation, Guid.NewGuid(), "new.txt", "text/plain", "safe"u8.ToArray()));
        }
        Guid fileId;
        await using (var verify = Open(_tenant))
        {
            var pending = await verify.Files.SingleAsync(item => item.UploadOperationId == operation);
            fileId = pending.Id;
            Assert.False(pending.Stored);
            Assert.Equal(QuarantinedFileState.Quarantined, pending.State);
            Assert.Single(await verify.Audit.ToListAsync());
            Assert.Equal("safe"u8.ToArray(), await storage.Store.ReadVerifiedAsync(_tenant, fileId, pending.Sha256, 4, CancellationToken.None));
        }
        await using (var owner = Open(_tenant))
        {
            var recovered = await Access(owner, storage.Store).UploadAsync(_context, operation, Guid.NewGuid(),
                "new.txt", "text/plain", "safe"u8.ToArray());
            Assert.Equal(FileAccessDecision.Success, recovered.Decision);
            Assert.Equal(fileId, recovered.Id);
            Assert.Equal(QuarantinedFileState.Quarantined, recovered.State);
        }
        await using var final = Open(_tenant);
        Assert.True((await final.Files.SingleAsync(item => item.Id == fileId)).Stored);
        Assert.Equal(2, await final.Audit.CountAsync());
    }

    [Fact]
    public async Task APartialObjectCannotBeReplacedOrConfirmedByAnUploadRetry()
    {
        using var storage = new PrivateStore();
        var fault = new PartialWrite(storage.Store);
        var operation = Guid.NewGuid();
        await using (var owner = Open(_tenant))
            await Assert.ThrowsAsync<QuarantineStorageException>(() => Access(owner, fault).UploadAsync(
                _context, operation, Guid.NewGuid(), "new.txt", "text/plain", "safe"u8.ToArray()));
        await using (var owner = Open(_tenant))
            await Assert.ThrowsAsync<QuarantineStorageException>(() => Access(owner, storage.Store).UploadAsync(
                _context, operation, Guid.NewGuid(), "new.txt", "text/plain", "safe"u8.ToArray()));
        await using var verify = Open(_tenant);
        var pending = await verify.Files.SingleAsync(item => item.UploadOperationId == operation);
        Assert.False(pending.Stored);
        Assert.Equal("bad"u8.ToArray(), await storage.Store.ReadVerifiedAsync(_tenant, pending.Id,
            Convert.ToHexString(SHA256.HashData("bad"u8)), 3, CancellationToken.None));
        Assert.Single(await verify.Audit.ToListAsync());
    }

    [Fact]
    public async Task UploadUsesOnePreAwaitSnapshotAndClearsItsPrivateWorkingBuffer()
    {
        var original = "safe"u8.ToArray();
        var store = new Store
        {
            OnWrite = _ =>
            {
                "evil"u8.CopyTo(original);
                return Task.CompletedTask;
            }
        };
        await using var owner = Open(_tenant);
        var result = await Access(owner, store).UploadAsync(_context, Guid.NewGuid(), Guid.NewGuid(),
            "new.txt", "text/plain", original);
        Assert.Equal(FileAccessDecision.Success, result.Decision);
        Assert.Equal("evil"u8.ToArray(), original);
        Assert.All(store.WrittenMemory.ToArray(), value => Assert.Equal((byte)0, value));
        await using var verify = Open(_tenant);
        Assert.Equal(Convert.ToHexString(SHA256.HashData("safe"u8)),
            (await verify.Files.SingleAsync(item => item.Id == result.Id)).Sha256);
    }

    [Fact]
    public async Task RevocationDuringUploadLeavesQuarantineInsteadOfConfirmingStorage()
    {
        var operation = Guid.NewGuid();
        var store = new Store
        {
            OnWrite = async _ => await SetGrantsAsync([Grant("platform.files.read", DataScope.All, ReadFields)])
        };
        await using var owner = Open(_tenant);
        var result = await Access(owner, store).UploadAsync(_context, operation, Guid.NewGuid(),
            "new.txt", "text/plain", "safe"u8.ToArray());
        Assert.Equal(FileAccessDecision.Forbidden, result.Decision);
        Assert.Null(result.Id);
        await using var verify = Open(_tenant);
        var pending = await verify.Files.SingleAsync(item => item.UploadOperationId == operation);
        Assert.False(pending.Stored);
        Assert.Single(await verify.Audit.ToListAsync());
    }

    [Fact]
    public async Task DeletedUploadIdentityCannotBeRecreatedByReplayingItsOperation()
    {
        var operation = Guid.NewGuid();
        var store = new Store();
        FileUploadResult uploaded;
        await using (var owner = Open(_tenant))
            uploaded = await Access(owner, store).UploadAsync(_context, operation, Guid.NewGuid(),
                "new.txt", "text/plain", "safe"u8.ToArray());
        await using (var owner = Open(_tenant))
        {
            var file = await owner.Files.SingleAsync(item => item.Id == uploaded.Id);
            file.BeginDeletion(true, _clock.GetUtcNow().AddDays(1));
            file.ConfirmDeleted(_clock.GetUtcNow().AddDays(1));
            await owner.SaveChangesAsync();
        }
        await using (var owner = Open(_tenant))
            Assert.Equal(FileAccessDecision.StateConflict, (await Access(owner, store).UploadAsync(
                _context, operation, Guid.NewGuid(), "new.txt", "text/plain", "safe"u8.ToArray())).Decision);
        Assert.Equal(1, store.Writes);
        await using var verify = Open(_tenant);
        Assert.Single(await verify.Files.Where(item => item.UploadOperationId == operation).ToListAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OriginalFileMetadataAndUploadOperationCannotBeRewritten(bool operation)
    {
        await using var owner = Open(_tenant);
        var file = await owner.Files.SingleAsync();
        if (operation) owner.Entry(file).Property(item => item.UploadOperationId).CurrentValue = Guid.NewGuid();
        else owner.Entry(file).Property(item => item.FileName).CurrentValue = "changed.txt";
        await Assert.ThrowsAsync<InvalidOperationException>(() => owner.SaveChangesAsync());
        await using var verify = Open(_tenant);
        var original = await verify.Files.SingleAsync();
        Assert.Equal("private.txt", original.FileName);
        Assert.Null(original.UploadOperationId);
    }

    [Fact]
    public async Task ConcurrentSameOperationCreatesOnePermanentFileAndOnePairOfAudits()
    {
        using var storage = new PrivateStore();
        var operation = Guid.NewGuid();
        await using var first = Open(_tenant);
        await using var second = Open(_tenant);
        async Task<FileUploadResult?> ExecuteAsync(PlatformDbContext db)
        {
            try
            {
                return await Access(db, storage.Store).UploadAsync(_context, operation, Guid.NewGuid(),
                    "new.txt", "text/plain", "safe"u8.ToArray());
            }
            catch (QuarantineStorageException error) when (error.Failure == QuarantineStorageFailure.Unavailable)
            {
                return null;
            }
        }
        var results = await Task.WhenAll(ExecuteAsync(first), ExecuteAsync(second));
        Assert.Contains(results, item => item?.Decision == FileAccessDecision.Success);
        Assert.All(results.Where(item => item is not null), item =>
            Assert.True(item!.Decision is FileAccessDecision.Success or FileAccessDecision.VersionConflict));
        await using var verify = Open(_tenant);
        var file = Assert.Single(await verify.Files.Where(item => item.UploadOperationId == operation).ToListAsync());
        Assert.True(file.Stored);
        Assert.Equal(2, await verify.Audit.CountAsync());
        Assert.All(results.Where(item => item?.Decision == FileAccessDecision.Success), item => Assert.Equal(file.Id, item!.Id));
    }

    [Fact]
    public async Task SameOperationInAnotherTenantHasIndependentIdentityAndPrivateObject()
    {
        using var storage = new PrivateStore();
        var operation = Guid.NewGuid();
        FileUploadResult first;
        await using (var owner = Open(_tenant))
            first = await Access(owner, storage.Store).UploadAsync(_context, operation, Guid.NewGuid(),
                "new.txt", "text/plain", "safe"u8.ToArray());
        TenantContext otherContext;
        await using (var seed = Open(_other))
        {
            var department = new DirectoryDepartment(_other, "files", "Files");
            var session = new Session(_other, _actor, SessionClient.Web, _clock.GetUtcNow(), new string('C', 64));
            var role = new AccessRole(_other, "file-owner", Grants());
            seed.DirectoryTenants.Add(new(_other, "Other upload tenant"));
            seed.Departments.Add(department);
            seed.DirectoryUsers.Add(new(_other, _actor, "ACTOR", "Actor", department.Id));
            seed.Sessions.Add(session);
            seed.Roles.Add(role);
            seed.Memberships.Add(new(_other, _actor, role.Id));
            await seed.SaveChangesAsync();
            otherContext = new(_other, _actor, session.Id);
        }
        await using (var other = Open(_other))
        {
            var uploaded = await Access(other, storage.Store).UploadAsync(otherContext, operation, Guid.NewGuid(),
                "new.txt", "text/plain", "evil"u8.ToArray());
            Assert.Equal(FileAccessDecision.Success, uploaded.Decision);
            Assert.NotEqual(first.Id, uploaded.Id);
            Assert.Equal("evil"u8.ToArray(), await storage.Store.ReadVerifiedAsync(_other, uploaded.Id!.Value,
                Convert.ToHexString(SHA256.HashData("evil"u8)), 4, CancellationToken.None));
        }
        Assert.Equal("safe"u8.ToArray(), await storage.Store.ReadVerifiedAsync(_tenant, first.Id!.Value,
            Convert.ToHexString(SHA256.HashData("safe"u8)), 4, CancellationToken.None));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SameOperationWithAnotherActorOrExactNameCannotReturnOriginalUpload(bool actor)
    {
        var operation = Guid.NewGuid();
        var store = new Store();
        await using (var owner = Open(_tenant))
            await Access(owner, store).UploadAsync(_context, operation, Guid.NewGuid(), "new.txt", "text/plain", "safe"u8.ToArray());
        if (actor)
        {
            await using var seed = Open(_tenant);
            var session = new Session(_tenant, _creator, SessionClient.Web, _clock.GetUtcNow(), new string('D', 64));
            seed.Sessions.Add(session);
            seed.Memberships.Add(new(_tenant, _creator, (await seed.Roles.SingleAsync()).Id));
            await seed.SaveChangesAsync();
            _context = new(_tenant, _creator, session.Id);
        }
        await using var replay = Open(_tenant);
        Assert.Equal(FileAccessDecision.IdempotencyConflict, (await Access(replay, store).UploadAsync(_context, operation,
            Guid.NewGuid(), actor ? "new.txt" : "NEW.txt", "text/plain", "safe"u8.ToArray())).Decision);
        Assert.Equal(1, store.Writes);
    }

    [Fact]
    public async Task ExactTwentyMiBUploadPersistsWithoutRoundingSizeOrClearingQuarantine()
    {
        using var storage = new PrivateStore();
        var bytes = new byte[20971520];
        "%PDF-1.7"u8.CopyTo(bytes);
        await using var owner = Open(_tenant);
        var result = await Access(owner, storage.Store).UploadAsync(_context, Guid.NewGuid(), Guid.NewGuid(),
            "maximum.pdf", "application/pdf", bytes);
        Assert.Equal(FileAccessDecision.Success, result.Decision);
        Assert.Equal(QuarantinedFileState.Quarantined, result.State);
        await using var verify = Open(_tenant);
        var file = await verify.Files.SingleAsync(item => item.Id == result.Id);
        Assert.Equal(20971520L, file.ByteCount);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(bytes)), file.Sha256);
        Assert.True(file.Stored);
        var stored = await storage.Store.ReadVerifiedAsync(_tenant, file.Id, file.Sha256, file.ByteCount, CancellationToken.None);
        Assert.Equal(bytes, stored);
        CryptographicOperations.ZeroMemory(stored);
    }

    private StagingFileAccess Access(PlatformDbContext db, IQuarantineStore store, IFileScanner? scanner = null) =>
        new(db, store, scanner ?? new Scanner(), _clock, NullLoggerFactory.Instance);

    private async Task SetGrantsAsync(IEnumerable<PermissionGrant> grants)
    {
        await using var db = Open(_tenant);
        var role = await db.Roles.SingleAsync();
        role.Change(role.Version, true, grants);
        await db.SaveChangesAsync();
    }

    private IEnumerable<PermissionGrant> Grants(DataScope scope = DataScope.All) =>
    [
        Grant("platform.files.read", scope, ReadFields),
        Grant("platform.files.download", scope, ["Content"]),
        Grant("platform.files.write", scope, ["Scan", "Delete", "Upload"])
    ];

    private PermissionGrant Grant(string permission, DataScope scope, string[] fields) =>
        new(permission, scope, scope == DataScope.Departments ? ImmutableHashSet.Create(_department) : ImmutableHashSet<Guid>.Empty,
            fields.ToImmutableHashSet(StringComparer.Ordinal));

    private PlatformDbContext Open(Guid tenant, IInterceptor? interceptor = null)
    {
        MySqlTestConnection.ValidateTarget(_connection);
        var options = new DbContextOptionsBuilder<PlatformDbContext>().UseMySQL(_connection,
            mysql => mysql.MigrationsHistoryTable("__WesPlatformMigrations"));
        if (interceptor is not null) options.AddInterceptors(interceptor);
        return new(options.Options, tenant);
    }

    public async Task DisposeAsync()
    {
        if (!_ready) return;
        foreach (var tenant in new[] { _tenant, _other })
        {
            await using var db = Open(tenant);
            await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM platform_file_references WHERE TenantId = {tenant.ToString("D")}");
            await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM platform_files WHERE TenantId = {tenant.ToString("D")}");
            await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM platform_audit WHERE TenantId = {tenant.ToString("D")}");
            db.Memberships.RemoveRange(await db.Memberships.ToListAsync());
            db.Roles.RemoveRange(await db.Roles.ToListAsync());
            db.Sessions.RemoveRange(await db.Sessions.ToListAsync());
            db.DirectoryUsers.RemoveRange(await db.DirectoryUsers.ToListAsync());
            db.Departments.RemoveRange(await db.Departments.ToListAsync());
            db.DirectoryTenants.RemoveRange(await db.DirectoryTenants.ToListAsync());
            await db.SaveChangesAsync();
        }
    }

    private sealed class Clock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(2026, 10, 27, 0, 0, 0, TimeSpan.Zero);
    }

    private sealed class Store(Func<Task>? onRead = null) : IQuarantineStore
    {
        public int Reads { get; private set; }
        public int Deletes { get; private set; }
        public int Writes { get; private set; }
        public ReadOnlyMemory<byte> WrittenMemory { get; private set; }
        public Func<Guid, Task>? OnWrite { get; set; }
        public byte[]? LastBuffer { get; private set; }
        public byte[] Content { get; set; } = "safe"u8.ToArray();
        public async Task<QuarantineStorageEvidence> WriteAsync(Guid tenant, Guid file, ReadOnlyMemory<byte> content, CancellationToken ct)
        {
            Writes++;
            WrittenMemory = content;
            if (OnWrite is not null) await OnWrite(file);
            ct.ThrowIfCancellationRequested();
            return new(Convert.ToHexString(SHA256.HashData(content.Span)), content.Length);
        }
        public async Task<byte[]> ReadVerifiedAsync(Guid tenant, Guid file, string sha256, long byteCount, CancellationToken ct)
        {
            Reads++;
            if (onRead is not null) await onRead();
            ct.ThrowIfCancellationRequested();
            return LastBuffer = Content.ToArray();
        }
        public Task DeleteAsync(Guid tenant, Guid file, CancellationToken ct)
        {
            Deletes++;
            return Task.CompletedTask;
        }
    }

    private sealed class Scanner : IFileScanner
    {
        public int Calls { get; private set; }
        public Task<FileScanEvidence> ScanAsync(ReadOnlyMemory<byte> content, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(new FileScanEvidence(FileScanVerdict.Clean,
                Convert.ToHexString(SHA256.HashData(content.Span)), content.Length));
        }
    }

    private sealed class AuditFailure : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (eventData.Context!.ChangeTracker.Entries<PlatformAudit>().Any(item => item.State == EntityState.Added))
                throw new AuditDatabaseFault();
            return ValueTask.FromResult(result);
        }
    }

    private sealed class AuditDatabaseFault : System.Data.Common.DbException
    {
        public AuditDatabaseFault() : base("sensitive-sentinel") { }
    }

    private sealed class CancelAudit(CancellationTokenSource cancellation) : SaveChangesInterceptor
    {
        public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result,
            CancellationToken cancellationToken = default)
        {
            cancellation.Cancel();
            return ValueTask.FromResult(result);
        }
    }

    private sealed class StoredAuditFailure(bool cancel, CancellationTokenSource cancellation) : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (!cancel && eventData.Context!.ChangeTracker.Entries<PlatformAudit>().Any(item =>
                item.State == EntityState.Added && item.Entity.Action == PlatformAuditAction.FileUploadStored))
                throw new AuditDatabaseFault();
            return ValueTask.FromResult(result);
        }
        public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result,
            CancellationToken cancellationToken = default)
        {
            if (cancel && eventData.Context!.ChangeTracker.Entries<PlatformAudit>()
                .Any(item => item.Entity.Action == PlatformAuditAction.FileUploadStored))
                cancellation.Cancel();
            return ValueTask.FromResult(result);
        }
    }

    private sealed class PrivateStore : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), $"wes-authorized-upload-{Guid.NewGuid():N}");
        public LocalQuarantineStore Store { get; }
        public PrivateStore()
        {
            Directory.CreateDirectory(_root);
            Store = new(_root, NullLogger<LocalQuarantineStore>.Instance);
        }
        public void Dispose() => Directory.Delete(_root, recursive: true);
    }

    private sealed class PartialWrite(IQuarantineStore store) : IQuarantineStore
    {
        public async Task<QuarantineStorageEvidence> WriteAsync(Guid tenant, Guid file, ReadOnlyMemory<byte> content, CancellationToken ct)
        {
            await store.WriteAsync(tenant, file, "bad"u8.ToArray(), ct);
            throw new QuarantineStorageException(QuarantineStorageFailure.Unavailable);
        }
        public Task<byte[]> ReadVerifiedAsync(Guid tenant, Guid file, string sha256, long byteCount, CancellationToken ct) =>
            store.ReadVerifiedAsync(tenant, file, sha256, byteCount, ct);
        public Task DeleteAsync(Guid tenant, Guid file, CancellationToken ct) => store.DeleteAsync(tenant, file, ct);
    }
}
