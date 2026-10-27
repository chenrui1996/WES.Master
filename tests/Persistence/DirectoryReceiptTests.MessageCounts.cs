using System.Collections.Immutable;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Wes.Platform.Domain;
using Wes.Platform.Features.Messaging;
using Wes.Platform.Infrastructure.Messaging;
using Wes.Platform.Infrastructure.Persistence;
using Wes.Platform.Messaging;
using Xunit;

namespace Wes.Persistence.Tests;

public sealed partial class DirectoryReceiptTests
{
    private static readonly string[] CountFields =
        ["Owner", "ObservedAt", "PendingCount", "LeasedCount", "PublishedCount", "QuarantinedCount", "InboxReceiptCount"];

    [Fact]
    public async Task MessageCountsReadRealOwnerStatusesAndConsumerReceiptsWithoutCrossTenantData()
    {
        await GrantCountsAsync();
        await using (var seed = Open())
        {
            seed.Audit.AddRange(Enumerable.Range(0, 4).Select(_ => new PlatformAudit(_tenant, _actor, Guid.NewGuid(), Guid.NewGuid(),
                PlatformAuditAction.UserChanged, null, Guid.NewGuid().ToString("D"), _clock.Now)));
            await seed.SaveChangesAsync();
            var messages = await seed.OutboxMessages.OrderBy(row => row.EventId).ToArrayAsync();
            for (var index = 1; index < messages.Length; index++)
            {
                var lease = Guid.NewGuid();
                Assert.True(await new PlatformOutboxLeaseStore(seed).TryLeaseAsync(messages[index].EventId, lease, _clock.Now, TimeSpan.FromSeconds(30)));
                if (index == 2) messages[index].MarkPublished(lease, _clock.Now);
                if (index == 3) messages[index].RecordFailure(lease, _clock.Now, OutboxFailureCategory.InvalidContract);
            }
            await seed.SaveChangesAsync();
        }
        var envelope = new PlatformOutboxEnvelope(_tenant, Guid.NewGuid(), "platform.audit", 1, "Platform", Guid.NewGuid(),
            "v1", Guid.NewGuid(), null, _clock.Now, "{}");
        foreach (var consumer in new[] { "one", "two" })
        {
            await using var inbox = Open();
            await new PlatformInboxConsumer(inbox, _clock).ProcessAsync(envelope, consumer, (_, _, _) => Task.CompletedTask);
        }
        await using (var foreign = Open(tenant: _otherTenant))
        {
            foreign.Audit.Add(new PlatformAudit(_otherTenant, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
                PlatformAuditAction.UserChanged, null, Guid.NewGuid().ToString("D"), _clock.Now));
            await foreign.SaveChangesAsync();
        }
        await using var reader = Open();
        var result = await new QueryMessageSummary(reader, _clock).ReadAsync(_context);
        Assert.True(result.Authorized);
        Assert.NotNull(result.Summary);
        Assert.Equal("Platform", result.Summary.Owner);
        Assert.Equal(_clock.Now, result.Summary.ObservedAt);
        Assert.Equal(1, result.Summary.PendingCount);
        Assert.Equal(1, result.Summary.LeasedCount);
        Assert.Equal(1, result.Summary.PublishedCount);
        Assert.Equal(1, result.Summary.QuarantinedCount);
        Assert.Equal(2, result.Summary.InboxReceiptCount);
    }

    [Fact]
    public async Task MessageCountsAuthorizedEmptyOwnerIsExplicitZeroNotDenied()
    {
        await GrantCountsAsync();
        await using var reader = Open();
        var result = await new QueryMessageSummary(reader, _clock).ReadAsync(_context);
        Assert.True(result.Authorized);
        Assert.NotNull(result.Summary);
        Assert.Equal(0, result.Summary.PendingCount + result.Summary.LeasedCount + result.Summary.PublishedCount
            + result.Summary.QuarantinedCount + result.Summary.InboxReceiptCount);
    }

    [Theory]
    [InlineData("Owner")]
    [InlineData("ObservedAt")]
    [InlineData("PendingCount")]
    [InlineData("LeasedCount")]
    [InlineData("PublishedCount")]
    [InlineData("QuarantinedCount")]
    [InlineData("InboxReceiptCount")]
    public async Task MessageCountsRequireEveryCurrentCountField(string missing)
    {
        await GrantCountsAsync(missing);
        await using var reader = Open();
        var result = await new QueryMessageSummary(reader, _clock).ReadAsync(_context);
        Assert.False(result.Authorized);
        Assert.Null(result.Summary);
    }

    [Theory]
    [InlineData(DataScope.Own)]
    [InlineData(DataScope.Departments)]
    public async Task MessageCountsCannotBorrowNonAllScope(DataScope scope)
    {
        await using (var writer = Open())
        {
            var role = await writer.Roles.SingleAsync();
            role.Change(role.Version, true, [new("platform.messages.read", scope,
                scope == DataScope.Departments ? System.Collections.Immutable.ImmutableHashSet.Create(_department)
                    : System.Collections.Immutable.ImmutableHashSet<Guid>.Empty, CountFields.ToImmutableHashSet())]);
            await writer.SaveChangesAsync();
        }
        await using var reader = Open();
        Assert.False((await new QueryMessageSummary(reader, _clock).ReadAsync(_context)).Authorized);
    }

    [Fact]
    public async Task MessageCountsCannotUseARevokedSession()
    {
        await GrantCountsAsync();
        await using (var writer = Open())
        {
            (await writer.Sessions.SingleAsync()).Revoke(_clock.Now);
            await writer.SaveChangesAsync();
        }
        await using var reader = Open();
        Assert.False((await new QueryMessageSummary(reader, _clock).ReadAsync(_context)).Authorized);
    }

    [Theory]
    [InlineData("platform_outbox_messages")]
    [InlineData("platform_inbox_receipts")]
    public async Task MessageCountsCancellationAfterSqlCannotReturnZeroOrSuccess(string table)
    {
        await GrantCountsAsync();
        using var cancellation = new CancellationTokenSource();
        await using var reader = Open(new CancelCountRead(cancellation, table));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new QueryMessageSummary(reader, _clock).ReadAsync(_context, cancellation.Token));
    }

    [Fact]
    public async Task MessageCountsRejectDirtyOwnerRatherThanClearingUncommittedWork()
    {
        await GrantCountsAsync();
        await using var reader = Open();
        await reader.Roles.SingleAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => new QueryMessageSummary(reader, _clock).ReadAsync(_context));
        Assert.NotEmpty(reader.ChangeTracker.Entries());
    }

    [Theory]
    [InlineData("platform_outbox_messages")]
    [InlineData("platform_inbox_receipts")]
    public async Task MessageCountsProviderFailuresAreNotConvertedToAuthorizedZero(string table)
    {
        await GrantCountsAsync();
        await using var reader = Open(new FailCountRead(table));
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new QueryMessageSummary(reader, _clock).ReadAsync(_context));
        Assert.Equal("count-provider-test-failure", exception.Message);
    }

    [Fact]
    public async Task MessageCountsPreCancelledReadCannotReturnDeniedOrSuccess()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await using var reader = Open();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new QueryMessageSummary(reader, _clock).ReadAsync(_context, cancellation.Token));
    }

    [Theory]
    [InlineData("role")]
    [InlineData("membership")]
    public async Task MessageCountsRevokedAuthorityCannotReusePreviousCounts(string target)
    {
        await GrantCountsAsync();
        await using (var first = Open())
            Assert.True((await new QueryMessageSummary(first, _clock).ReadAsync(_context)).Authorized);
        await using (var writer = Open())
        {
            if (target == "role")
            {
                var role = await writer.Roles.SingleAsync();
                role.Change(role.Version, false, role.Rules);
            }
            else writer.Memberships.Remove(await writer.Memberships.SingleAsync());
            await writer.SaveChangesAsync();
        }
        await using var reader = Open();
        var result = await new QueryMessageSummary(reader, _clock).ReadAsync(_context);
        Assert.False(result.Authorized);
        Assert.Null(result.Summary);
    }

    private async Task GrantCountsAsync(string? missing = null)
    {
        await using var writer = Open();
        var role = await writer.Roles.SingleAsync();
        role.Change(role.Version, true, [Grant("platform.messages.read", CountFields.Where(field => field != missing).ToArray())]);
        await writer.SaveChangesAsync();
    }

    private sealed class CancelCountRead(CancellationTokenSource cancellation, string table) : DbCommandInterceptor
    {
        public override ValueTask<InterceptionResult> DataReaderClosingAsync(DbCommand command,
            DataReaderClosingEventData eventData, InterceptionResult result)
        {
            if (command.CommandText.Contains(table, StringComparison.Ordinal)) cancellation.Cancel();
            return ValueTask.FromResult(result);
        }

    }

    private sealed class FailCountRead(string table) : DbCommandInterceptor
    {
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains(table, StringComparison.Ordinal))
                throw new InvalidOperationException("count-provider-test-failure");
            return ValueTask.FromResult(result);
        }
    }
}
