using System.Collections.Immutable;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Wes.Platform.Domain;
using Wes.Platform.Features.Audit;
using Wes.Platform.Features.Tenancy;
using Xunit;

namespace Wes.Persistence.Tests;

public sealed partial class PlatformAccessPersistenceTests
{
    [Fact]
    public async Task AuditExportOwner_ExportsMoreThanOnePageWithExactOrderedMembership()
    {
        var (context, snapshot, _) = await ExportFixtureAsync(105);
        await using var owner = Open(_tenant);
        var result = await AuditQuery(owner).ExportSnapshotAsync(context, snapshot, Guid.NewGuid(),
            ["Id", "OccurredAt", "Action"], Guid.NewGuid());
        Assert.Equal(AuditExportDecision.Success, result.Decision);
        var csv = Encoding.UTF8.GetString(Assert.IsType<byte[]>(result.Content));
        Assert.Equal(106, csv.Split("\r\n", StringSplitOptions.RemoveEmptyEntries).Length);
        Assert.Equal(1, await owner.AuditExportReceipts.CountAsync());
    }

    [Fact]
    public async Task AuditExportOwner_EmptySnapshotStillRequiresPermissionAndReturnsOnlyHeader()
    {
        var (context, snapshot, _) = await ExportFixtureAsync(0);
        await using var owner = Open(_tenant);
        var result = await AuditQuery(owner).ExportSnapshotAsync(context, snapshot, Guid.NewGuid(),
            ["Id", "OccurredAt"], Guid.NewGuid());
        Assert.Equal(AuditExportDecision.Success, result.Decision);
        Assert.Equal("\"Id\",\"OccurredAt\"\r\n", Encoding.UTF8.GetString(result.Content!));
    }

    [Theory]
    [InlineData(DataScope.Own)]
    [InlineData(DataScope.Departments)]
    public async Task AuditExportOwner_ExportScopeCannotWidenAcrossCapturedActors(DataScope scope)
    {
        var (context, snapshot, _) = await ExportFixtureAsync(1, scope, otherActor: true);
        await using var owner = Open(_tenant);
        var result = await AuditQuery(owner).ExportSnapshotAsync(context, snapshot, Guid.NewGuid(),
            ["Id", "OccurredAt"], Guid.NewGuid());
        Assert.Equal(AuditExportDecision.Forbidden, result.Decision);
        Assert.Null(result.Content);
        Assert.False(await owner.AuditExportReceipts.AnyAsync());
    }

    [Fact]
    public async Task AuditExportOwner_CurrentReadProjectionCannotBeRestoredByBroaderExportFields()
    {
        var (context, snapshot, _) = await ExportFixtureAsync(1, hideAction: true);
        await using var owner = Open(_tenant);
        var result = await AuditQuery(owner).ExportSnapshotAsync(context, snapshot, Guid.NewGuid(),
            ["Id", "OccurredAt", "Action"], Guid.NewGuid());
        Assert.Equal(AuditExportDecision.Forbidden, result.Decision);
        Assert.Null(result.Content);
        Assert.False(await owner.AuditExportReceipts.AnyAsync());
    }

    [Fact]
    public async Task AuditExportOwner_AuditSaveFailureHasNoDeliveryOrOrphanReceiptAndOriginalIdentityRecovers()
    {
        var (context, snapshot, _) = await ExportFixtureAsync(1);
        var operation = Guid.NewGuid();
        await using (var failure = Open(_tenant, new FailAudit()))
            await Assert.ThrowsAsync<InvalidOperationException>(() => AuditQuery(failure).ExportSnapshotAsync(
                context, snapshot, operation, ["Id", "OccurredAt"], Guid.NewGuid()));
        await using (var verify = Open(_tenant))
        {
            Assert.False(await verify.AuditExportReceipts.AnyAsync());
            Assert.False(await verify.Audit.AnyAsync(item => item.TargetId == snapshot));
        }
        await using var recovery = Open(_tenant);
        Assert.Equal(AuditExportDecision.Success, (await AuditQuery(recovery).ExportSnapshotAsync(
            context, snapshot, operation, ["Id", "OccurredAt"], Guid.NewGuid())).Decision);
    }

    [Fact]
    public async Task AuditExportOwner_SimultaneousSameOperationCreatesExactlyOneAuditAndReceipt()
    {
        var (context, snapshot, _) = await ExportFixtureAsync(1);
        var operation = Guid.NewGuid();
        var barrier = new SaveBarrier();
        await using var first = Open(_tenant, barrier);
        await using var second = Open(_tenant, barrier);
        var results = await Task.WhenAll(AuditQuery(first).ExportSnapshotAsync(context, snapshot, operation,
                ["Id", "OccurredAt"], Guid.NewGuid()),
            AuditQuery(second).ExportSnapshotAsync(context, snapshot, operation, ["Id", "OccurredAt"], Guid.NewGuid()));
        Assert.All(results, result => Assert.Equal(AuditExportDecision.Success, result.Decision));
        Assert.Equal(results[0].Content, results[1].Content);
        await using var verify = Open(_tenant);
        Assert.Equal(1, await verify.AuditExportReceipts.CountAsync());
        Assert.Equal(1, await verify.Audit.CountAsync(item => item.TargetId == snapshot));
    }

    [Fact]
    public async Task AuditExportOwner_RevokedExportGrantCannotReplayAnExistingSuccess()
    {
        var (context, snapshot, roleId) = await ExportFixtureAsync(1);
        var operation = Guid.NewGuid();
        await using (var first = Open(_tenant))
            Assert.Equal(AuditExportDecision.Success, (await AuditQuery(first).ExportSnapshotAsync(context, snapshot,
                operation, ["Id", "OccurredAt"], Guid.NewGuid())).Decision);
        await using (var revoke = Open(_tenant))
        {
            var role = await revoke.Roles.SingleAsync(item => item.Id == roleId);
            role.Change(role.Version, true, role.Rules.Where(grant => grant.Permission != "platform.audit.export"));
            await revoke.SaveChangesAsync();
        }
        await using var owner = Open(_tenant);
        var denied = await AuditQuery(owner).ExportSnapshotAsync(context, snapshot, operation,
            ["Id", "OccurredAt"], Guid.NewGuid());
        Assert.Equal(AuditExportDecision.Forbidden, denied.Decision);
        Assert.Null(denied.Content);
        Assert.Equal(1, await owner.AuditExportReceipts.CountAsync());
    }

    [Fact]
    public async Task AuditExportOwner_CancelledReadCannotCreateAnAudit()
    {
        var (context, snapshot, _) = await ExportFixtureAsync(1);
        await using var owner = Open(_tenant);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => AuditQuery(owner).ExportSnapshotAsync(context,
            snapshot, Guid.NewGuid(), ["Id", "OccurredAt"], Guid.NewGuid(), cancelled.Token));
        Assert.False(await owner.AuditExportReceipts.AnyAsync());
    }

    [Fact]
    public async Task AuditExportOwner_FailureAfterActualAuditSqlRollsBackReceiptAndOutboxBeforeRetry()
    {
        var (context, snapshot, _) = await ExportFixtureAsync(1);
        var operation = Guid.NewGuid();
        await using var baseline = Open(_tenant);
        var outboxCount = await baseline.OutboxMessages.CountAsync();
        await using (var failure = Open(_tenant, new FailAfterAuditSql()))
        {
            var error = await Assert.ThrowsAsync<DbUpdateException>(() => AuditQuery(failure).ExportSnapshotAsync(
                context, snapshot, operation, ["Id", "OccurredAt"], Guid.NewGuid()));
            Assert.IsType<InjectedAuditSqlFailureException>(error.InnerException);
        }
        await using var verify = Open(_tenant);
        Assert.False(await verify.AuditExportReceipts.AnyAsync());
        Assert.False(await verify.Audit.AnyAsync(item => item.TargetId == snapshot));
        Assert.Equal(outboxCount, await verify.OutboxMessages.CountAsync());
    }

    [Fact]
    public async Task AuditExportOwner_WithdrawalDuringActualReadDropsPreparedBytesBeforeAudit()
    {
        var (context, snapshot, roleId) = await ExportFixtureAsync(1);
        var hook = new WithdrawExportDuringRead(async () =>
        {
            await using var revoke = Open(_tenant);
            var role = await revoke.Roles.SingleAsync(item => item.Id == roleId);
            role.Change(role.Version, true, role.Rules.Where(grant => grant.Permission != "platform.audit.export"));
            await revoke.SaveChangesAsync();
        });
        await using var owner = Open(_tenant, hook);
        var result = await AuditQuery(owner).ExportSnapshotAsync(context, snapshot, Guid.NewGuid(),
            ["Id", "OccurredAt"], Guid.NewGuid());
        Assert.Equal(AuditExportDecision.Forbidden, result.Decision);
        Assert.Null(result.Content);
        Assert.True(hook.Fired);
        Assert.False(await owner.AuditExportReceipts.AnyAsync());
        Assert.False(await owner.Audit.AnyAsync(item => item.TargetId == snapshot));
    }

    [Fact]
    public async Task AuditExportOwner_ASecondReceiptCannotReuseAHistoricalDeliveryAudit()
    {
        var (context, snapshot, _) = await ExportFixtureAsync(1);
        await using (var export = Open(_tenant))
            await AuditQuery(export).ExportSnapshotAsync(context, snapshot, Guid.NewGuid(), ["Id", "OccurredAt"], Guid.NewGuid());
        await using var owner = Open(_tenant);
        var clone = await owner.AuditExportReceipts.AsNoTracking().SingleAsync();
        owner.AuditExportReceipts.Add(clone);
        owner.Entry(clone).Property(item => item.OperationId).CurrentValue = Guid.NewGuid();
        await Assert.ThrowsAsync<InvalidOperationException>(() => owner.SaveChangesAsync());
    }

    [Fact]
    public async Task AuditExportOwner_TrustedOtherTenantCannotResolveTheOriginalSnapshot()
    {
        var (context, snapshot, _) = await ExportFixtureAsync(1);
        await using var owner = Open(_other);
        var result = await AuditQuery(owner).ExportSnapshotAsync(new TenantContext(_other, context.ActorId, context.SessionId),
            snapshot, Guid.NewGuid(), ["Id", "OccurredAt"], Guid.NewGuid());
        Assert.Equal(AuditExportDecision.NotFound, result.Decision);
        Assert.Null(result.Content);
        Assert.False(await owner.AuditExportReceipts.AnyAsync());
    }

    [Fact]
    public async Task AuditExportOwner_NullMetadataAndUtcTimestampHaveAnExactSafeCsvRepresentation()
    {
        var (context, snapshot, _) = await ExportFixtureAsync(1, includeNullColumn: true);
        await using var owner = Open(_tenant);
        var result = await AuditQuery(owner).ExportSnapshotAsync(context, snapshot, Guid.NewGuid(),
            ["Id", "OccurredAt", "BeforeVersion"], Guid.NewGuid());
        Assert.Equal(AuditExportDecision.Success, result.Decision);
        var csv = Encoding.UTF8.GetString(result.Content!);
        Assert.StartsWith("\"Id\",\"OccurredAt\",\"BeforeVersion\"\r\n\"", csv);
        Assert.EndsWith("\",\"2026-10-23T16:00:00.0000000+00:00\",\"\"\r\n", csv);
        Assert.Equal(2, csv.Split("\r\n", StringSplitOptions.RemoveEmptyEntries).Length);
    }

    private sealed class WithdrawExportDuringRead(Func<Task> withdraw) : DbCommandInterceptor
    {
        public bool Fired { get; private set; }
        public override async ValueTask<System.Data.Common.DbDataReader> ReaderExecutedAsync(
            System.Data.Common.DbCommand command, CommandExecutedEventData eventData,
            System.Data.Common.DbDataReader result, CancellationToken cancellationToken = default)
        {
            if (!Fired && command.CommandText.Contains("FROM `platform_audit`", StringComparison.Ordinal))
            {
                Fired = true;
                await withdraw();
            }
            return result;
        }
    }

    private async Task<(TenantContext Context, Guid Snapshot, Guid Role)> ExportFixtureAsync(int count,
        DataScope exportScope = DataScope.All, bool otherActor = false, bool hideAction = false, bool includeNullColumn = false)
    {
        var (context, roleId, _) = await SeedAsync();
        await using var seed = Open(_tenant);
        var role = await seed.Roles.SingleAsync(item => item.Id == roleId);
        var fields = ImmutableHashSet.Create("Id", "OccurredAt", "Action");
        if (includeNullColumn) fields = fields.Add("BeforeVersion");
        role.Change(role.Version, true,
        [
            new PermissionGrant("platform.audit.read", DataScope.All, ImmutableHashSet<Guid>.Empty,
                hideAction ? ImmutableHashSet.Create("Id", "OccurredAt") : fields),
            new PermissionGrant("platform.audit.export", exportScope,
                exportScope == DataScope.Departments ? ImmutableHashSet.Create(Guid.NewGuid()) : ImmutableHashSet<Guid>.Empty,
                fields)
        ]);
        seed.Audit.AddRange(Enumerable.Range(0, count).Select(index => new PlatformAudit(_tenant,
            otherActor ? Guid.NewGuid() : _actor, Guid.NewGuid(), Guid.NewGuid(), PlatformAuditAction.RoleChanged,
            null, Guid.NewGuid().ToString("D"), Now.AddSeconds(-index))));
        await seed.SaveChangesAsync();
        var result = await AuditQuery(seed).CreateSnapshotAsync(context, Now.AddHours(-1), Now.AddMinutes(1));
        Assert.Equal(AuditSnapshotDecision.Success, result.Decision);
        return (context, result.Page!.SnapshotId, roleId);
    }
}
