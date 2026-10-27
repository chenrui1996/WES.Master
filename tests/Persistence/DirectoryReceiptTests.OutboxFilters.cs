using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Wes.Platform.Domain;
using Wes.Platform.Features.Messaging;
using Wes.Platform.Infrastructure.Persistence;
using Xunit;

namespace Wes.Persistence.Tests;

public sealed partial class DirectoryReceiptTests
{
    [Fact]
    public async Task OutboxFiltersApplyBeforePagingAndRespectHalfOpenUtcBoundaries()
    {
        await SeedFilteredOutboxAsync();
        await using var database = Open();
        var query = new QueryOutbox(database, _clock);
        var bounded = await query.ReadAsync(_context, limit: 1,
            filter: new OutboxQueryFilter(CreatedFrom: _clock.Now.AddSeconds(-1), CreatedBefore: _clock.Now));
        Assert.True(bounded.Authorized);
        Assert.Equal(_clock.Now.AddSeconds(-1), Assert.Single(bounded.Rows).CreatedAt);
        var pending = await query.ReadAsync(_context, offset: 1, limit: 1, filter: new OutboxQueryFilter(Status: 0));
        Assert.Equal(_clock.Now, Assert.Single(pending.Rows).CreatedAt);
        var quarantined = await query.ReadAsync(_context, filter: new OutboxQueryFilter(Status: 3));
        Assert.Equal(_clock.Now.AddSeconds(-2), Assert.Single(quarantined.Rows).CreatedAt);
        var before = await query.ReadAsync(_context, filter: new OutboxQueryFilter(CreatedBefore: _clock.Now));
        Assert.Equal(2, before.Rows.Length);
        var after = await query.ReadAsync(_context, filter: new OutboxQueryFilter(CreatedFrom: _clock.Now));
        Assert.Single(after.Rows);
        var unfiltered = await query.ReadAsync(_context);
        Assert.Equal(3, unfiltered.Rows.Length);
    }

    [Theory]
    [InlineData(0, 2)]
    [InlineData(1, 0)]
    [InlineData(2, 0)]
    [InlineData(3, 1)]
    public async Task OutboxFiltersSupportEveryFiniteDeliveryStatus(int status, int count)
    {
        await SeedFilteredOutboxAsync();
        await using var reader = Open();
        var result = await new QueryOutbox(reader, _clock).ReadAsync(_context, filter: new OutboxQueryFilter(Status: status));
        Assert.Equal(count, result.Rows.Length);
        Assert.All(result.Rows, row => Assert.Equal(status, (int)row.Status));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(4)]
    public async Task OutboxFiltersRejectUnknownStatus(int status)
    {
        await using var database = Open();
        await Assert.ThrowsAsync<ArgumentException>(() => new QueryOutbox(database, _clock)
            .ReadAsync(_context, filter: new OutboxQueryFilter(Status: status)));
    }

    [Theory]
    [InlineData("offset")]
    [InlineData("equal")]
    [InlineData("reverse")]
    [InlineData("early")]
    public async Task OutboxFiltersRejectInvalidUtcRanges(string kind)
    {
        var from = kind == "early" ? DateTimeOffset.MinValue : kind == "offset" ? _clock.Now.ToOffset(TimeSpan.FromHours(8)) : _clock.Now;
        var before = kind == "equal" ? _clock.Now : kind == "reverse" ? _clock.Now.AddSeconds(-1) : _clock.Now.AddSeconds(1);
        await using var database = Open();
        await Assert.ThrowsAsync<ArgumentException>(() => new QueryOutbox(database, _clock)
            .ReadAsync(_context, filter: new OutboxQueryFilter(CreatedFrom: from, CreatedBefore: before)));
    }

    [Theory]
    [InlineData("Status")]
    [InlineData("CreatedAt")]
    public async Task OutboxFiltersCannotBypassMandatoryCurrentProjection(string missing)
    {
        await SeedFilteredOutboxAsync();
        await using (var writer = Open())
        {
            var role = await writer.Roles.SingleAsync();
            role.Change(role.Version, true, [Grant("platform.messages.read",
                new[] { "EventId", "Status", "CreatedAt", "Owner" }.Where(field => field != missing).ToArray())]);
            await writer.SaveChangesAsync();
        }
        await using var reader = Open();
        var result = await new QueryOutbox(reader, _clock).ReadAsync(_context, filter: new OutboxQueryFilter(Status: 3));
        Assert.False(result.Authorized);
        Assert.Empty(result.Rows);
    }

    [Fact]
    public async Task OutboxFiltersCannotExposeAnotherTenant()
    {
        await SeedFilteredOutboxAsync();
        await using (var foreign = Open(tenant: _otherTenant))
        {
            foreign.Audit.Add(new PlatformAudit(_otherTenant, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
                PlatformAuditAction.UserChanged, null, Guid.NewGuid().ToString("D"), _clock.Now));
            await foreign.SaveChangesAsync();
        }
        await using var reader = Open();
        var rows = await new QueryOutbox(reader, _clock).ReadAsync(_context,
            filter: new OutboxQueryFilter(Status: 0, CreatedFrom: _clock.Now));
        Assert.Single(rows.Rows);
    }

    [Fact]
    public async Task OutboxFiltersDoNotDeliverAResultCancelledAfterSqlMaterialization()
    {
        await SeedFilteredOutboxAsync();
        using var cancellation = new CancellationTokenSource();
        await using var reader = Open(new CancelAfterOutboxRead(cancellation));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new QueryOutbox(reader, _clock)
            .ReadAsync(_context, cancellationToken: cancellation.Token, filter: new OutboxQueryFilter(Status: 3)));
    }

    private async Task SeedFilteredOutboxAsync()
    {
        await using var writer = Open();
        var role = await writer.Roles.SingleAsync();
        role.Change(role.Version, true, [Grant("platform.messages.read", "EventId", "Status", "CreatedAt", "Owner")]);
        writer.Audit.AddRange(Enumerable.Range(-2, 3).Select(index => new PlatformAudit(_tenant, _actor, Guid.NewGuid(), Guid.NewGuid(),
            PlatformAuditAction.UserChanged, null, Guid.NewGuid().ToString("D"), _clock.Now.AddSeconds(index))));
        await writer.SaveChangesAsync();
        var oldest = await writer.OutboxMessages.OrderBy(row => row.CreatedAt).FirstAsync();
        var lease = Guid.NewGuid();
        Assert.True(await new PlatformOutboxLeaseStore(writer).TryLeaseAsync(oldest.EventId, lease, _clock.Now, TimeSpan.FromSeconds(30)));
        oldest.RecordFailure(lease, _clock.Now, OutboxFailureCategory.InvalidContract);
        await writer.SaveChangesAsync();
    }

    private sealed class CancelAfterOutboxRead(CancellationTokenSource cancellation) : DbCommandInterceptor
    {
        public override ValueTask<InterceptionResult> DataReaderClosingAsync(DbCommand command,
            DataReaderClosingEventData eventData, InterceptionResult result)
        {
            if (command.CommandText.Contains("platform_outbox_messages", StringComparison.Ordinal)) cancellation.Cancel();
            return ValueTask.FromResult(result);
        }
    }
}
