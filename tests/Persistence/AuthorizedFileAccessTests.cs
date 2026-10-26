using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Wes.Platform.Domain;
using Wes.Platform.Features.Files;
using Wes.Platform.Features.Tenancy;
using Wes.Platform.Infrastructure.Persistence;
using Xunit;

namespace Wes.Persistence.Tests;

[Collection("PlatformPersistence")]
[Trait("Provider", "MySQL")]
public sealed class AuthorizedFileAccessTests : IAsyncLifetime
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

    private StagingFileAccess Access(PlatformDbContext db, Store store, Scanner? scanner = null) =>
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
        Grant("platform.files.write", scope, ["Scan", "Delete"])
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
        await using var db = Open(_tenant);
        await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM platform_file_references WHERE TenantId = {_tenant.ToString("D")}");
        await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM platform_files WHERE TenantId = {_tenant.ToString("D")}");
        await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM platform_audit WHERE TenantId = {_tenant.ToString("D")}");
        db.Memberships.RemoveRange(await db.Memberships.ToListAsync());
        db.Roles.RemoveRange(await db.Roles.ToListAsync());
        db.Sessions.RemoveRange(await db.Sessions.ToListAsync());
        db.DirectoryUsers.RemoveRange(await db.DirectoryUsers.ToListAsync());
        db.Departments.RemoveRange(await db.Departments.ToListAsync());
        db.DirectoryTenants.RemoveRange(await db.DirectoryTenants.ToListAsync());
        await db.SaveChangesAsync();
    }

    private sealed class Clock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(2026, 10, 27, 0, 0, 0, TimeSpan.Zero);
    }

    private sealed class Store(Func<Task>? onRead = null) : IQuarantineStore
    {
        public int Reads { get; private set; }
        public int Deletes { get; private set; }
        public byte[]? LastBuffer { get; private set; }
        public byte[] Content { get; set; } = "safe"u8.ToArray();
        public Task<QuarantineStorageEvidence> WriteAsync(Guid tenant, Guid file, ReadOnlyMemory<byte> content, CancellationToken ct) =>
            throw new NotSupportedException("Access must not write uploaded bytes.");
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
}
