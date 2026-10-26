using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Wes.Platform;
using Wes.Platform.Domain;
using Wes.Platform.Features.Directory;
using Wes.Platform.Features.Messaging;
using Wes.Platform.Features.Tenancy;
using Wes.Platform.Infrastructure.Messaging;
using Wes.Platform.Infrastructure.Persistence;
using Xunit;

namespace Wes.Persistence.Tests;

/// <summary>RET-MAINT-05..08: real MySQL transaction/expiry evidence on unique authorized test tenants.</summary>
[Collection("PlatformPersistence")]
[Trait("Provider", "MySQL")]
public sealed class RetentionMaintenancePersistenceTests : IAsyncLifetime
{
    private readonly Guid _tenant = Guid.NewGuid();
    private readonly Guid _other = Guid.NewGuid();
    private readonly Clock _clock = new();
    private string _connection = "";
    private bool _ready;

    public async Task InitializeAsync()
    {
        _connection = MySqlTestConnection.LoadAuthorizedTestConnection();
        await using var db = Open(_tenant);
        await db.Database.MigrateAsync();
        _ready = true;
        foreach (var tenant in new[] { _tenant, _other })
        {
            await using var seed = Open(tenant);
            seed.DirectoryTenants.Add(new DirectoryTenant(tenant, "Retention fixture"));
            await seed.SaveChangesAsync();
        }
    }

    [Fact]
    public async Task PublicOwnerCommitsBoundedExactNinetyDayExpiryOnlyForItsTenant()
    {
        var cutoff = _clock.GetUtcNow() - TimeSpan.FromDays(90);
        foreach (var instant in new[] { cutoff.AddDays(-2), cutoff.AddDays(-1), cutoff, cutoff.AddSeconds(1) })
            await SeedAsync(_tenant, instant);
        await SeedAsync(_other, cutoff);
        await using (var owner = new PlatformScope(_connection, _tenant, _clock, NullLoggerFactory.Instance))
            Assert.Equal(new(2, 2), await owner.RemoveExpiredReceiptsAsync(2));
        await AssertCountsAsync(_tenant, 2, 4);
        await AssertCountsAsync(_other, 1, 1);
        await using (var owner = new PlatformScope(_connection, _tenant, _clock, NullLoggerFactory.Instance))
            Assert.Equal(new(1, 1), await owner.RemoveExpiredReceiptsAsync(2));
        await AssertCountsAsync(_tenant, 1, 4);
        await using var verify = Open(_tenant);
        Assert.Equal(cutoff.AddSeconds(1), (await verify.CommandReceipts.SingleAsync()).CreatedAt);
        Assert.Equal(cutoff.AddSeconds(1), (await verify.InboxReceipts.SingleAsync()).ProcessedAt);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailureOrCancellationAfterBothDeletesRollsBackAtomically(bool cancel)
    {
        await SeedAsync(_tenant, _clock.GetUtcNow().AddDays(-90));
        using var cancellation = new CancellationTokenSource();
        var fault = new AfterSaveFault(cancellation, cancel);
        await using (var owner = Open(_tenant, fault))
        {
            if (cancel)
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                    PlatformRetentionMaintenance.RemoveExpiredAsync(owner, _clock, 1, cancellation.Token));
            else
                await Assert.ThrowsAsync<InvalidOperationException>(() =>
                    PlatformRetentionMaintenance.RemoveExpiredAsync(owner, _clock, 1, cancellation.Token));
        }
        Assert.True(fault.Reached);
        await AssertCountsAsync(_tenant, 1, 1);
    }

    [Fact]
    public async Task CancellationBeforeCommitAfterSuccessfulSaveRollsBack()
    {
        await SeedAsync(_tenant, _clock.GetUtcNow().AddDays(-90));
        using var cancellation = new CancellationTokenSource();
        await using (var owner = Open(_tenant, new CancelBeforeCommit(cancellation)))
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                PlatformRetentionMaintenance.RemoveExpiredAsync(owner, _clock, 1, cancellation.Token));
        await AssertCountsAsync(_tenant, 1, 1);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DatabaseFailureDuringInboxDeleteIsSanitizedAndRollsBackDirectoryDelete(bool transient)
    {
        await SeedAsync(_tenant, _clock.GetUtcNow().AddDays(-90));
        var fault = new InboxDeleteFault(transient);
        await using (var owner = Open(_tenant, fault))
        {
            var error = await Assert.ThrowsAsync<PlatformRetentionDatabaseException>(() =>
                PlatformRetentionMaintenance.RemoveExpiredAsync(owner, _clock, 1, CancellationToken.None));
            Assert.Equal(transient, error.IsTransient);
            Assert.Null(error.InnerException);
            Assert.DoesNotContain("injected-secret", error.ToString());
        }
        Assert.True(fault.Reached);
        await AssertCountsAsync(_tenant, 1, 1);
    }

    [Fact]
    public async Task AnotherOwnerDeletingTheSelectedBatchIsATransientConflictNotAHostFailure()
    {
        await SeedAsync(_tenant, _clock.GetUtcNow().AddDays(-90));
        RetentionMaintenanceResult? competingResult = null;
        var competitor = new CompetingSave(async () =>
        {
            await using var competingOwner = new PlatformScope(_connection, _tenant, _clock, NullLoggerFactory.Instance);
            competingResult = await competingOwner.RemoveExpiredReceiptsAsync(1);
        });
        await using (var staleOwner = Open(_tenant, competitor))
        {
            var error = await Assert.ThrowsAsync<PlatformRetentionDatabaseException>(() =>
                PlatformRetentionMaintenance.RemoveExpiredAsync(staleOwner, _clock, 1, CancellationToken.None));
            Assert.True(error.IsTransient);
            Assert.Null(error.InnerException);
        }
        Assert.Equal(new RetentionMaintenanceResult(1, 1), competingResult);
        await AssertCountsAsync(_tenant, 0, 1);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(201)]
    public async Task InvalidBatchesDoNotTouchReceipts(int batch)
    {
        await SeedAsync(_tenant, _clock.GetUtcNow().AddDays(-90));
        await using var owner = new PlatformScope(_connection, _tenant, _clock, NullLoggerFactory.Instance);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => owner.RemoveExpiredReceiptsAsync(batch));
        await AssertCountsAsync(_tenant, 1, 1);
    }

    [Fact]
    public async Task PreCancelledOwnerDoesNotCommit()
    {
        await SeedAsync(_tenant, _clock.GetUtcNow().AddDays(-90));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await using var owner = new PlatformScope(_connection, _tenant, _clock, NullLoggerFactory.Instance);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => owner.RemoveExpiredReceiptsAsync(1, cancellation.Token));
        await AssertCountsAsync(_tenant, 1, 1);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NonCleanScopeIsRejected(bool existingTransaction)
    {
        await SeedAsync(_tenant, _clock.GetUtcNow().AddDays(-90));
        await using var owner = Open(_tenant);
        if (existingTransaction) await owner.Database.BeginTransactionAsync();
        else await owner.CommandReceipts.ToListAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            PlatformRetentionMaintenance.RemoveExpiredAsync(owner, _clock, 1, CancellationToken.None));
        await AssertCountsAsync(_tenant, 1, 1);
    }

    private async Task SeedAsync(Guid tenant, DateTimeOffset instant)
    {
        var actor = Guid.NewGuid();
        var department = new DirectoryDepartment(tenant, "ret-" + Guid.NewGuid().ToString("N"), "Retained identity");
        await using (var db = Open(tenant))
        {
            await using var transaction = await db.Database.BeginTransactionAsync();
            db.Departments.Add(department);
            db.Audit.Add(new PlatformAudit(tenant, actor, department.Id, Guid.NewGuid(),
                PlatformAuditAction.DepartmentChanged, null, department.Version, instant));
            DirectoryCommandReceipts.Stage(db, new TenantContext(tenant, actor, Guid.NewGuid()),
                new DirectoryOperation(Guid.NewGuid()), DirectoryCommandKind.CreateDepartment, new string('A', 64),
                department.Id, department.Version, instant);
            await db.SaveChangesAsync();
            await transaction.CommitAsync();
        }
        await using var inbox = Open(tenant);
        var envelope = new PlatformOutboxEnvelope(tenant, Guid.NewGuid(), "platform.audit", 1, "Platform",
            department.Id, department.Version, Guid.NewGuid(), null, instant, "{}");
        await new PlatformInboxConsumer(inbox, new Clock(instant)).ProcessAsync(envelope,
            "platform.retention-test", (_, _, _) => Task.CompletedTask);
    }

    private async Task AssertCountsAsync(Guid tenant, int receipts, int facts)
    {
        await using var db = Open(tenant);
        Assert.Equal(receipts, await db.CommandReceipts.CountAsync());
        Assert.Equal(receipts, await db.InboxReceipts.CountAsync());
        Assert.Equal(facts, await db.Departments.CountAsync());
        Assert.Equal(facts, await db.Audit.CountAsync());
        Assert.Equal(facts, await db.OutboxMessages.CountAsync());
    }

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
            await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM platform_inbox_receipts WHERE TenantId = {tenant.ToString("D")}");
            await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM platform_audit WHERE TenantId = {tenant.ToString("D")}");
            db.OutboxMessages.RemoveRange(await db.OutboxMessages.ToListAsync());
            db.CommandReceipts.RemoveRange(await db.CommandReceipts.ToListAsync());
            db.Departments.RemoveRange(await db.Departments.ToListAsync());
            db.DirectoryTenants.RemoveRange(await db.DirectoryTenants.ToListAsync());
            await db.SaveChangesAsync();
        }
    }

    private sealed class Clock(DateTimeOffset? now = null) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now ?? new DateTimeOffset(2026, 10, 26, 0, 0, 0, TimeSpan.Zero);
    }

    private sealed class AfterSaveFault(CancellationTokenSource cancellation, bool cancel) : SaveChangesInterceptor
    {
        public bool Reached { get; private set; }
        public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result,
            CancellationToken cancellationToken = default)
        {
            Reached = true;
            if (!cancel) throw new InvalidOperationException("Injected after-save fault.");
            cancellation.Cancel();
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(result);
        }
    }

    private sealed class CancelBeforeCommit(CancellationTokenSource cancellation) : DbTransactionInterceptor
    {
        public override ValueTask<InterceptionResult> TransactionCommittingAsync(System.Data.Common.DbTransaction transaction,
            TransactionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default)
        {
            cancellation.Cancel();
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(result);
        }
    }

    private sealed class InjectedDatabaseFailure(bool transient) : System.Data.Common.DbException("injected-secret")
    {
        public override bool IsTransient => transient;
    }

    private sealed class CompetingSave(Func<Task> competingOwner) : SaveChangesInterceptor
    {
        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            await competingOwner();
            return result;
        }
    }

    private sealed class InboxDeleteFault(bool transient) : DbCommandInterceptor
    {
        public bool Reached { get; private set; }
        private void Inspect(System.Data.Common.DbCommand command)
        {
            if (!command.CommandText.Contains("DELETE FROM `platform_inbox_receipts`", StringComparison.Ordinal)) return;
            Reached = true;
            throw new InjectedDatabaseFailure(transient);
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(System.Data.Common.DbCommand command,
            CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            Inspect(command);
            return ValueTask.FromResult(result);
        }

        public override ValueTask<InterceptionResult<System.Data.Common.DbDataReader>> ReaderExecutingAsync(
            System.Data.Common.DbCommand command, CommandEventData eventData,
            InterceptionResult<System.Data.Common.DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Inspect(command);
            return ValueTask.FromResult(result);
        }
    }
}
