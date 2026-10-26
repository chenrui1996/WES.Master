using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Wes.Platform.Domain;
using Wes.Platform.Features.Files;
using Wes.Platform.Infrastructure.Files;
using Wes.Platform.Infrastructure.Persistence;
using Xunit;

namespace Wes.Persistence.Tests;

[Collection("PlatformPersistence")]
[Trait("Provider", "MySQL")]
public sealed class QuarantinedFilePersistenceTests : IAsyncLifetime
{
    private readonly Guid _tenant = Guid.NewGuid();
    private readonly Guid _other = Guid.NewGuid();
    private string _connection = "";
    private bool _ready;
    private static readonly DateTimeOffset Now = new(2026, 10, 26, 0, 0, 0, TimeSpan.Zero);
    private const string Digest = "0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF";

    public async Task InitializeAsync()
    {
        _connection = MySqlTestConnection.LoadAuthorizedTestConnection();
        await using var db = Open(_tenant);
        await db.Database.MigrateAsync();
        db.DirectoryTenants.Add(new DirectoryTenant(_tenant, "File test"));
        await db.SaveChangesAsync();
        await using var other = Open(_other);
        other.DirectoryTenants.Add(new DirectoryTenant(_other, "Other file test"));
        await other.SaveChangesAsync();
        _ready = true;
    }

    [Fact]
    public async Task StoredEvidenceAndScanFenceRoundTripInsideTenantOnly()
    {
        var file = Create();
        file.ConfirmStored(Digest, 4, Now);
        var attempt = file.BeginScan(Now);
        await using (var db = Open(_tenant))
        {
            db.Set<QuarantinedFile>().Add(file);
            await db.SaveChangesAsync();
        }
        await using (var other = Open(_other))
            Assert.Empty(await other.Set<QuarantinedFile>().ToListAsync());
        await using var verify = Open(_tenant);
        var actual = await verify.Set<QuarantinedFile>().SingleAsync();
        Assert.Equal(file.Id, actual.Id);
        Assert.Equal(attempt, actual.ScanAttemptId);
        Assert.Equal(Now, actual.ScanStartedAt);
        Assert.True(actual.Stored);
        Assert.True(actual.CompleteScan(attempt, FileScanVerdict.Clean, Digest, 4, Now.AddSeconds(1)));
        await verify.SaveChangesAsync();
    }

    [Fact]
    public async Task CompetingStorageOrScanTransitionsRequireCurrentVersion()
    {
        var id = await SeedAsync();
        await using var first = Open(_tenant);
        await using var stale = Open(_tenant);
        var winner = await first.Set<QuarantinedFile>().SingleAsync(item => item.Id == id);
        var loser = await stale.Set<QuarantinedFile>().SingleAsync(item => item.Id == id);
        winner.ConfirmStored(Digest, 4, Now);
        loser.ConfirmStored(Digest, 4, Now);
        await first.SaveChangesAsync();
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => stale.SaveChangesAsync());
    }

    [Fact]
    public async Task BindingUniquenessAndTenantForeignKeyAreEnforcedByRealProvider()
    {
        var id = await SeedAsync(available: true);
        var entity = Guid.NewGuid();
        await using (var db = Open(_tenant))
        {
            var file = await db.Set<QuarantinedFile>().SingleAsync();
            file.AddReference(Now.AddSeconds(2));
            db.Set<FileReference>().Add(new(_tenant, id, "Platform", entity));
            await db.SaveChangesAsync();
        }
        await using (var duplicate = Open(_tenant))
        {
            duplicate.Set<FileReference>().Add(new(_tenant, id, "Platform", entity));
            await Assert.ThrowsAsync<DbUpdateException>(() => duplicate.SaveChangesAsync());
        }
        await using (var crossTenant = Open(_other))
        {
            crossTenant.Set<FileReference>().Add(new(_other, id, "Platform", entity));
            await Assert.ThrowsAsync<DbUpdateException>(() => crossTenant.SaveChangesAsync());
        }
        await using var verify = Open(_tenant);
        Assert.Equal(1, (await verify.Set<QuarantinedFile>().SingleAsync()).ReferenceCount);
        Assert.Single(await verify.Set<FileReference>().ToListAsync());
    }

    [Fact]
    public async Task TenantWriteGuardAndFileTombstoneCannotBeBypassedByNormalDelete()
    {
        var id = await SeedAsync();
        await using (var wrong = Open(_other))
        {
            wrong.Set<QuarantinedFile>().Add(Create());
            await Assert.ThrowsAsync<InvalidOperationException>(() => wrong.SaveChangesAsync());
        }
        await using (var deleted = Open(_tenant))
        {
            var file = await deleted.Set<QuarantinedFile>().SingleAsync(item => item.Id == id);
            deleted.Remove(file);
            await Assert.ThrowsAsync<InvalidOperationException>(() => deleted.SaveChangesAsync());
        }
        await using var verify = Open(_tenant);
        Assert.Single(await verify.Set<QuarantinedFile>().ToListAsync());
    }

    [Fact]
    public async Task FailedOwnerTransactionDoesNotCommitReferenceOrAvailabilityChanges()
    {
        await SeedAsync(available: true);
        await using (var db = Open(_tenant))
        {
            await using var transaction = await db.Database.BeginTransactionAsync();
            var file = await db.Set<QuarantinedFile>().SingleAsync();
            file.AddReference(Now.AddSeconds(2));
            db.Set<FileReference>().Add(new(_tenant, file.Id, "Platform", Guid.NewGuid()));
            await db.SaveChangesAsync();
            await transaction.RollbackAsync();
        }
        await using var verify = Open(_tenant);
        Assert.Equal(0, (await verify.Set<QuarantinedFile>().SingleAsync()).ReferenceCount);
        Assert.Empty(await verify.Set<FileReference>().ToListAsync());
    }

    [Fact]
    public async Task BusinessOwnerIdentityUsesOrdinalComparisonInQueriesAndUniqueKeys()
    {
        await SeedAsync(available: true);
        var entity = Guid.NewGuid();
        await using (var first = Open(_tenant))
        {
            var file = await first.Files.SingleAsync();
            file.AddReference(Now.AddMinutes(1));
            first.FileReferences.Add(new(_tenant, file.Id, "Platform", entity));
            await first.SaveChangesAsync();
        }
        await using (var second = Open(_tenant))
        {
            var collation = await second.Database.SqlQueryRaw<string>(
                "SELECT COLLATION_NAME AS Value FROM information_schema.COLUMNS WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'platform_file_references' AND COLUMN_NAME = 'Owner'")
                .SingleAsync();
            Assert.Equal("utf8mb4_bin", collation);
            Assert.Empty(await second.FileReferences.Where(item => item.Owner == "platform").ToListAsync());
            var file = await second.Files.SingleAsync();
            file.AddReference(Now.AddMinutes(2));
            second.FileReferences.Add(new(_tenant, file.Id, "platform", entity));
            await second.SaveChangesAsync();
        }
        await using var verify = Open(_tenant);
        Assert.Equal(2, (await verify.Files.SingleAsync()).ReferenceCount);
        Assert.Single(await verify.FileReferences.Where(item => item.Owner == "Platform").ToListAsync());
        Assert.Single(await verify.FileReferences.Where(item => item.Owner == "platform").ToListAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NewBindingAndOrphanDeletionCannotBothCommitFromTheSameVersion(bool deleteFirst)
    {
        await SeedAsync(available: true);
        await using var binding = Open(_tenant);
        await using var deleting = Open(_tenant);
        var linked = await binding.Files.SingleAsync();
        var orphan = await deleting.Files.SingleAsync();
        linked.AddReference(Now.AddDays(1));
        binding.FileReferences.Add(new(_tenant, linked.Id, "Platform", Guid.NewGuid()));
        orphan.BeginDeletion(ownerConfirmedUnreferenced: true, Now.AddDays(1));

        if (deleteFirst)
        {
            await deleting.SaveChangesAsync();
            await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => binding.SaveChangesAsync());
        }
        else
        {
            await binding.SaveChangesAsync();
            await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => deleting.SaveChangesAsync());
        }
        await using var verify = Open(_tenant);
        var actual = await verify.Files.SingleAsync();
        Assert.Equal(deleteFirst ? QuarantinedFileState.Deleting : QuarantinedFileState.Available, actual.State);
        Assert.Equal(deleteFirst ? 0 : 1, actual.ReferenceCount);
        Assert.Equal(deleteFirst ? 0 : 1, await verify.FileReferences.CountAsync());
    }

    [Theory]
    [InlineData(FileScanVerdict.Clean, QuarantinedFileState.Available)]
    [InlineData(FileScanVerdict.Infected, QuarantinedFileState.Rejected)]
    [InlineData(FileScanVerdict.Unavailable, QuarantinedFileState.Quarantined)]
    [InlineData(FileScanVerdict.TimedOut, QuarantinedFileState.Quarantined)]
    public async Task CoordinatedScanPersistsClaimBeforeExternalReadAndOnlyThenAppliesEvidence(
        FileScanVerdict verdict, QuarantinedFileState expected)
    {
        var (id, version) = await SeedStoredAsync();
        await using var db = Open(_tenant);
        var store = new ProcessingStore(async () =>
        {
            Assert.Null(db.Database.CurrentTransaction);
            await using var verifyClaim = Open(_tenant);
            var pending = await verifyClaim.Files.SingleAsync(item => item.Id == id);
            Assert.NotNull(pending.ScanAttemptId);
            Assert.Equal(QuarantinedFileState.Quarantined, pending.State);
        });
        var scanner = new ProcessingScanner(verdict);
        var result = await Processor(db, store, scanner, new ProcessingClock()).ScanAsync(id, version);
        Assert.Equal(expected, result.State);
        await using var verify = Open(_tenant);
        var file = await verify.Files.SingleAsync(item => item.Id == id);
        Assert.Equal(expected, file.State);
        Assert.Equal(verdict, file.LastScanVerdict);
        Assert.Null(file.ScanAttemptId);
        Assert.Equal(1, scanner.Calls);
    }

    [Fact]
    public async Task CoordinatedScanRejectsMismatchedEvidenceWithoutClearingQuarantine()
    {
        var (id, version) = await SeedStoredAsync();
        await using var db = Open(_tenant);
        var scanner = new ProcessingScanner(FileScanVerdict.Clean) { EvidenceDigest = Digest };
        await Assert.ThrowsAsync<FileProcessingException>(() =>
            Processor(db, new ProcessingStore(), scanner, new ProcessingClock()).ScanAsync(id, version));
        await using var verify = Open(_tenant);
        Assert.Equal(QuarantinedFileState.Quarantined, (await verify.Files.SingleAsync()).State);
    }

    [Fact]
    public async Task OrphanDeletionFailureRetainsFenceThenRetriesMissingObjectToTombstone()
    {
        var id = await SeedAsync(available: true);
        var clock = new ProcessingClock { Now = Now.AddDays(1) };
        var store = new ProcessingStore { DeleteFailures = 1 };
        await using (var db = Open(_tenant))
        {
            var version = (await db.Files.AsNoTracking().SingleAsync()).Version;
            store.OnDelete = async () =>
            {
                Assert.Null(db.Database.CurrentTransaction);
                await using var verifyFence = Open(_tenant);
                Assert.Equal(QuarantinedFileState.Deleting, (await verifyFence.Files.SingleAsync()).State);
            };
            await Assert.ThrowsAsync<QuarantineStorageException>(() =>
                Processor(db, store, new ProcessingScanner(FileScanVerdict.Clean), clock).DeleteOrphanAsync(id, version));
        }
        await using (var db = Open(_tenant))
        {
            var current = await db.Files.AsNoTracking().SingleAsync();
            Assert.Equal(QuarantinedFileState.Deleting, current.State);
            store.OnDelete = null;
            var result = await Processor(db, store, new ProcessingScanner(FileScanVerdict.Clean), clock)
                .DeleteOrphanAsync(id, current.Version);
            Assert.Equal(QuarantinedFileState.Deleted, result.State);
        }
        await using var verify = Open(_tenant);
        Assert.Equal(QuarantinedFileState.Deleted, (await verify.Files.SingleAsync()).State);
        Assert.Equal(2, store.Deletes);
    }

    [Fact]
    public async Task OrphanCleanupChecksActualReferenceRowsEvenWhenCountIsIncorrectlyZero()
    {
        var id = await SeedAsync(available: true);
        await using (var binding = Open(_tenant))
        {
            binding.FileReferences.Add(new(_tenant, id, "Platform", Guid.NewGuid()));
            await binding.SaveChangesAsync();
        }
        await using var db = Open(_tenant);
        var version = (await db.Files.AsNoTracking().SingleAsync()).Version;
        var store = new ProcessingStore();
        var result = await Processor(db, store, new ProcessingScanner(FileScanVerdict.Clean),
            new ProcessingClock { Now = Now.AddDays(1) }).DeleteOrphanAsync(id, version);
        Assert.Equal(FileProcessingDecision.Referenced, result.Decision);
        Assert.Equal(0, store.Deletes);
        await using var verify = Open(_tenant);
        Assert.Equal(QuarantinedFileState.Available, (await verify.Files.SingleAsync()).State);
    }

    [Fact]
    public async Task ASlowDurableClaimCannotGrantAnotherSixtySecondsOfExternalWork()
    {
        var (id, version) = await SeedStoredAsync();
        var clock = new ProcessingClock();
        var hook = new ProcessingSaveHook(1, () =>
        {
            clock.Now = Now.AddSeconds(60);
            return Task.CompletedTask;
        }, after: true);
        await using var db = Open(_tenant, hook);
        var store = new ProcessingStore();
        var scanner = new ProcessingScanner(FileScanVerdict.Clean);
        var result = await Processor(db, store, scanner, clock).ScanAsync(id, version);
        Assert.Equal(QuarantinedFileState.Quarantined, result.State);
        Assert.Equal(0, store.Reads);
        Assert.Equal(0, scanner.Calls);
        await using var verify = Open(_tenant);
        Assert.Equal(FileScanVerdict.TimedOut, (await verify.Files.SingleAsync()).LastScanVerdict);
    }

    [Fact]
    public async Task LateCleanAndCallerCancellationNeverClearQuarantine()
    {
        var (id, version) = await SeedStoredAsync();
        var clock = new ProcessingClock();
        var scanner = new ProcessingScanner(FileScanVerdict.Clean)
        {
            OnScan = () => { clock.Now = Now.AddSeconds(60); return Task.CompletedTask; }
        };
        await using (var db = Open(_tenant))
        {
            var result = await Processor(db, new ProcessingStore(), scanner, clock).ScanAsync(id, version);
            Assert.Equal(QuarantinedFileState.Quarantined, result.State);
        }
        using var cancellation = new CancellationTokenSource();
        clock.Now = Now.AddSeconds(61);
        scanner.OnScan = () => { cancellation.Cancel(); return Task.CompletedTask; };
        await using (var db = Open(_tenant))
        {
            var current = await db.Files.AsNoTracking().SingleAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                Processor(db, new ProcessingStore(), scanner, clock).ScanAsync(id, current.Version, cancellation.Token));
        }
        await using var verify = Open(_tenant);
        var quarantined = await verify.Files.SingleAsync();
        Assert.Equal(QuarantinedFileState.Quarantined, quarantined.State);
        Assert.NotNull(quarantined.ScanAttemptId);
        Assert.Equal(FileScanVerdict.TimedOut, quarantined.LastScanVerdict);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OldScanCannotOverrideAReplacementAttemptOrADeletionFence(bool deleting)
    {
        var (id, version) = await SeedStoredAsync();
        var clock = new ProcessingClock();
        Guid? replacement = null;
        var scanner = new ProcessingScanner(FileScanVerdict.Clean)
        {
            OnScan = async () =>
            {
                clock.Now = deleting ? Now.AddDays(1) : Now.AddSeconds(60);
                await using var otherOwner = Open(_tenant);
                var current = await otherOwner.Files.SingleAsync();
                if (deleting) current.BeginDeletion(ownerConfirmedUnreferenced: true, clock.Now);
                else replacement = current.BeginScan(clock.Now);
                await otherOwner.SaveChangesAsync();
            }
        };
        await using var db = Open(_tenant);
        var result = await Processor(db, new ProcessingStore(), scanner, clock).ScanAsync(id, version);
        Assert.Equal(FileProcessingDecision.ObsoleteAttempt, result.Decision);
        await using var verify = Open(_tenant);
        var file = await verify.Files.SingleAsync();
        Assert.Equal(deleting ? QuarantinedFileState.Deleting : QuarantinedFileState.Quarantined, file.State);
        Assert.Equal(replacement, file.ScanAttemptId);
        Assert.Null(file.LastScanVerdict);
    }

    [Fact]
    public async Task StorageReadFailureLeavesRecoverableClaimAndDoesNotDisableOtherAvailableFiles()
    {
        var (id, version) = await SeedStoredAsync();
        var available = await SeedAsync(available: true);
        var clock = new ProcessingClock();
        var broken = new ProcessingStore(() => throw new QuarantineStorageException(QuarantineStorageFailure.Unavailable));
        await using (var db = Open(_tenant))
            await Assert.ThrowsAsync<QuarantineStorageException>(() =>
                Processor(db, broken, new ProcessingScanner(FileScanVerdict.Clean), clock).ScanAsync(id, version));
        await using (var db = Open(_tenant))
        {
            var clean = await db.Files.AsNoTracking().SingleAsync(item => item.Id == available);
            Assert.Equal(FileProcessingDecision.Available,
                (await Processor(db, broken, new ProcessingScanner(FileScanVerdict.Unavailable), clock).ScanAsync(available, clean.Version)).Decision);
        }
        clock.Now = Now.AddSeconds(61);
        await using (var db = Open(_tenant))
        {
            var pending = await db.Files.AsNoTracking().SingleAsync(item => item.Id == id);
            Assert.NotNull(pending.ScanAttemptId);
            Assert.Equal(FileProcessingDecision.Available,
                (await Processor(db, new ProcessingStore(), new ProcessingScanner(FileScanVerdict.Clean), clock)
                    .ScanAsync(id, pending.Version)).Decision);
        }
    }

    [Fact]
    public async Task TamperedStorageCannotReachScannerEvenIfThePortClaimsToHaveVerifiedIt()
    {
        var (id, version) = await SeedStoredAsync();
        await using var db = Open(_tenant);
        var scanner = new ProcessingScanner(FileScanVerdict.Clean);
        var store = new ProcessingStore { Content = "evil"u8.ToArray() };
        var error = await Assert.ThrowsAsync<FileProcessingException>(() =>
            Processor(db, store, scanner, new ProcessingClock()).ScanAsync(id, version));
        Assert.Equal(FileProcessingFailure.Evidence, error.Failure);
        Assert.Equal(0, scanner.Calls);
    }

    [Fact]
    public async Task VersionAndTenantAndLiveAttemptRejectionsHaveNoExternalEffects()
    {
        var (id, version) = await SeedStoredAsync();
        var store = new ProcessingStore();
        var scanner = new ProcessingScanner(FileScanVerdict.Clean);
        var clock = new ProcessingClock();
        await using (var db = Open(_other))
            Assert.Equal(FileProcessingDecision.NotFound, (await Processor(db, store, scanner, clock).ScanAsync(id, version)).Decision);
        await using (var db = Open(_tenant))
            Assert.Equal(FileProcessingDecision.VersionConflict,
                (await Processor(db, store, scanner, clock).ScanAsync(id, Guid.NewGuid().ToString("D"))).Decision);
        await using (var db = Open(_tenant))
        {
            var current = await db.Files.SingleAsync();
            current.BeginScan(clock.Now);
            await db.SaveChangesAsync();
        }
        await using (var db = Open(_tenant))
        {
            var current = await db.Files.AsNoTracking().SingleAsync();
            Assert.Equal(FileProcessingDecision.StateConflict,
                (await Processor(db, store, scanner, clock).ScanAsync(id, current.Version)).Decision);
        }
        Assert.Equal(0, store.Reads);
        Assert.Equal(0, scanner.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task YoungOrCurrentlyScanningObjectsAreNotOrphanDeletionCandidates(bool liveScan)
    {
        var (id, version) = await SeedStoredAsync();
        var clock = new ProcessingClock { Now = liveScan ? Now.AddDays(1) : Now.AddHours(23) };
        if (liveScan)
        {
            await using var claim = Open(_tenant);
            var current = await claim.Files.SingleAsync();
            current.BeginScan(clock.Now);
            await claim.SaveChangesAsync();
            version = current.Version;
        }
        await using var db = Open(_tenant);
        var store = new ProcessingStore();
        Assert.Equal(liveScan ? FileProcessingDecision.StateConflict : FileProcessingDecision.TooYoung,
            (await Processor(db, store, new ProcessingScanner(FileScanVerdict.Clean), clock).DeleteOrphanAsync(id, version)).Decision);
        Assert.Equal(0, store.Deletes);
    }

    [Fact]
    public async Task FailedScanClaimCannotStartStorageOrScannerAndSanitizesDatabaseFault()
    {
        var (id, version) = await SeedStoredAsync();
        var store = new ProcessingStore();
        var scanner = new ProcessingScanner(FileScanVerdict.Clean);
        await using (var db = Open(_tenant, new ProcessingSaveHook(1, () => throw new ProcessingDatabaseFault())))
        {
            var error = await Assert.ThrowsAsync<FileProcessingException>(() =>
                Processor(db, store, scanner, new ProcessingClock()).ScanAsync(id, version));
            Assert.Equal(FileProcessingFailure.Database, error.Failure);
            Assert.True(error.IsTransient);
            Assert.DoesNotContain("sensitive-sentinel", error.ToString(), StringComparison.Ordinal);
        }
        Assert.Equal(0, store.Reads);
        Assert.Equal(0, scanner.Calls);
        await using var verify = Open(_tenant);
        var unchanged = await verify.Files.SingleAsync();
        Assert.Null(unchanged.ScanAttemptId);
        Assert.Equal(version, unchanged.Version);
    }

    [Fact]
    public async Task DeletedObjectWithFailedDatabaseConfirmationRemainsRecoverableAndDoesNotDeleteTwiceAfterTombstone()
    {
        var id = await SeedAsync(available: true);
        var clock = new ProcessingClock { Now = Now.AddDays(1) };
        var store = new ProcessingStore();
        await using (var db = Open(_tenant, new ProcessingSaveHook(2, () => throw new ProcessingDatabaseFault())))
        {
            var current = await db.Files.AsNoTracking().SingleAsync();
            await Assert.ThrowsAsync<FileProcessingException>(() =>
                Processor(db, store, new ProcessingScanner(FileScanVerdict.Clean), clock).DeleteOrphanAsync(id, current.Version));
        }
        Assert.False(store.Present);
        await using (var db = Open(_tenant))
        {
            var current = await db.Files.AsNoTracking().SingleAsync();
            Assert.Equal(QuarantinedFileState.Deleting, current.State);
            Assert.Equal(FileProcessingDecision.Deleted,
                (await Processor(db, store, new ProcessingScanner(FileScanVerdict.Clean), clock).DeleteOrphanAsync(id, current.Version)).Decision);
        }
        await using (var db = Open(_tenant))
        {
            var current = await db.Files.AsNoTracking().SingleAsync();
            Assert.Equal(FileProcessingDecision.Deleted,
                (await Processor(db, store, new ProcessingScanner(FileScanVerdict.Clean), clock).DeleteOrphanAsync(id, current.Version)).Decision);
        }
        Assert.Equal(2, store.Deletes);
    }

    [Fact]
    public async Task RealPrivateStorageAndDatabaseCoordinateVerifiedReadThenPhysicalDeletion()
    {
        var (id, version) = await SeedStoredAsync();
        var root = Path.Combine(Path.GetTempPath(), $"wes-file-processing-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var store = new LocalQuarantineStore(root, NullLogger<LocalQuarantineStore>.Instance);
            var stored = await store.WriteAsync(_tenant, id, "safe"u8.ToArray(), CancellationToken.None);
            var scanner = new ProcessingScanner(FileScanVerdict.Clean);
            var clock = new ProcessingClock();
            await using var db = Open(_tenant);
            var processor = new FileProcessing(db, store, scanner, clock, NullLogger<FileProcessing>.Instance);
            var scanned = await processor.ScanAsync(id, version);
            Assert.Equal(FileProcessingDecision.Available, scanned.Decision);
            clock.Now = Now.AddDays(1);
            var deleted = await processor.DeleteOrphanAsync(id, scanned.Version);
            Assert.Equal(FileProcessingDecision.Deleted, deleted.Decision);
            await Assert.ThrowsAsync<QuarantineStorageException>(() =>
                store.ReadVerifiedAsync(_tenant, id, stored.Sha256, stored.ByteCount, CancellationToken.None));
            await using var verify = Open(_tenant);
            Assert.Equal(QuarantinedFileState.Deleted, (await verify.Files.SingleAsync()).State);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ConcurrentScanClaimsCannotBothReachStorageAndScanner()
    {
        var (id, version) = await SeedStoredAsync();
        var store = new ProcessingStore(async () => { await Task.Yield(); });
        var scanner = new ProcessingScanner(FileScanVerdict.Clean);
        var clock = new ProcessingClock();
        await using var first = Open(_tenant);
        await using var second = Open(_tenant);
        var results = await Task.WhenAll(
            Processor(first, store, scanner, clock).ScanAsync(id, version),
            Processor(second, store, scanner, clock).ScanAsync(id, version));
        Assert.Contains(results, item => item.Decision == FileProcessingDecision.Available);
        Assert.All(results, item => Assert.True(item.Decision is FileProcessingDecision.Available
            or FileProcessingDecision.VersionConflict or FileProcessingDecision.StateConflict));
        Assert.Equal(1, store.Reads);
        Assert.Equal(1, scanner.Calls);
        await using var verify = Open(_tenant);
        Assert.Equal(QuarantinedFileState.Available, (await verify.Files.SingleAsync()).State);
    }

    private static FileProcessing Processor(PlatformDbContext db, ProcessingStore store, ProcessingScanner scanner, ProcessingClock clock) =>
        new(db, store, scanner, clock, NullLogger<FileProcessing>.Instance);

    private async Task<(Guid Id, string Version)> SeedStoredAsync()
    {
        var file = new QuarantinedFile(_tenant, Guid.NewGuid(), Guid.NewGuid(), "file.txt", "text/plain", 4,
            Convert.ToHexString(SHA256.HashData("safe"u8)), Now);
        file.ConfirmStored(file.Sha256, 4, Now);
        await using var db = Open(_tenant);
        db.Files.Add(file);
        await db.SaveChangesAsync();
        return (file.Id, file.Version);
    }

    private sealed class ProcessingClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = QuarantinedFilePersistenceTests.Now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class ProcessingScanner(FileScanVerdict verdict) : IFileScanner
    {
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);
        public string? EvidenceDigest { get; set; }
        public Func<Task>? OnScan { get; set; }
        public async Task<FileScanEvidence> ScanAsync(ReadOnlyMemory<byte> content, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            if (OnScan is not null) await OnScan();
            cancellationToken.ThrowIfCancellationRequested();
            return new(verdict, EvidenceDigest ?? Convert.ToHexString(SHA256.HashData(content.Span)), content.Length);
        }
    }

    private sealed class ProcessingStore(Func<Task>? onRead = null) : IQuarantineStore
    {
        private int _reads;
        private int _deletes;
        public int Reads => Volatile.Read(ref _reads);
        public int Deletes => Volatile.Read(ref _deletes);
        public int DeleteFailures { get; set; }
        public bool Present { get; private set; } = true;
        public byte[] Content { get; set; } = "safe"u8.ToArray();
        public Func<Task>? OnDelete { get; set; }
        public Task<QuarantineStorageEvidence> WriteAsync(Guid tenant, Guid file, ReadOnlyMemory<byte> content, CancellationToken ct) =>
            throw new NotSupportedException("Processing must never rewrite quarantined content.");
        public async Task<byte[]> ReadVerifiedAsync(Guid tenant, Guid file, string sha256, long byteCount, CancellationToken ct)
        {
            Interlocked.Increment(ref _reads);
            if (onRead is not null) await onRead();
            ct.ThrowIfCancellationRequested();
            return Content.ToArray();
        }
        public async Task DeleteAsync(Guid tenant, Guid file, CancellationToken ct)
        {
            Interlocked.Increment(ref _deletes);
            if (OnDelete is not null) await OnDelete();
            ct.ThrowIfCancellationRequested();
            if (DeleteFailures-- > 0) throw new QuarantineStorageException(QuarantineStorageFailure.Unavailable);
            Present = false;
        }
    }

    private sealed class ProcessingSaveHook(int at, Func<Task> action, bool after = false) : SaveChangesInterceptor
    {
        private int _calls;
        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (!after && ++_calls == at) await action();
            return result;
        }
        public override async ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result,
            CancellationToken cancellationToken = default)
        {
            if (after && ++_calls == at) await action();
            return result;
        }
    }

    private sealed class ProcessingDatabaseFault : System.Data.Common.DbException
    {
        public ProcessingDatabaseFault() : base("sensitive-sentinel") { }
        public override bool IsTransient => true;
    }

    private async Task<Guid> SeedAsync(bool available = false)
    {
        var file = Create();
        if (available)
        {
            file.ConfirmStored(Digest, 4, Now);
            var attempt = file.BeginScan(Now);
            file.CompleteScan(attempt, FileScanVerdict.Clean, Digest, 4, Now.AddSeconds(1));
        }
        await using var db = Open(_tenant);
        db.Set<QuarantinedFile>().Add(file);
        await db.SaveChangesAsync();
        return file.Id;
    }

    private QuarantinedFile Create() => new(_tenant, Guid.NewGuid(), Guid.NewGuid(), "file.txt", "text/plain", 4, Digest, Now);

    private PlatformDbContext Open(Guid tenant, IInterceptor? interceptor = null)
    {
        MySqlTestConnection.ValidateTarget(_connection);
        var options = new DbContextOptionsBuilder<PlatformDbContext>()
            .UseMySQL(_connection, mysql => mysql.MigrationsHistoryTable("__WesPlatformMigrations"));
        if (interceptor is not null) options.AddInterceptors(interceptor);
        return new(options.Options, tenant);
    }

    public async Task DisposeAsync()
    {
        if (!_ready) return;
        foreach (var tenant in new[] { _tenant, _other })
        {
            await using var db = Open(tenant);
            if (db.Model.FindEntityType(typeof(QuarantinedFile)) is not null)
            {
                await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM platform_file_references WHERE TenantId = {tenant.ToString("D")}");
                await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM platform_files WHERE TenantId = {tenant.ToString("D")}");
            }
            db.DirectoryTenants.RemoveRange(await db.DirectoryTenants.ToListAsync());
            await db.SaveChangesAsync();
        }
    }
}
