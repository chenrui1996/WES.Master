using Microsoft.EntityFrameworkCore;
using Wes.Platform.Domain;
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

    private PlatformDbContext Open(Guid tenant)
    {
        MySqlTestConnection.ValidateTarget(_connection);
        return new(new DbContextOptionsBuilder<PlatformDbContext>()
            .UseMySQL(_connection, mysql => mysql.MigrationsHistoryTable("__WesPlatformMigrations")).Options, tenant);
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
