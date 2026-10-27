using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Wes.Wms;
using Wes.Wms.Domain;
using Wes.Wms.Features.MasterData;
using Wes.Wms.Infrastructure.Messaging;
using Wes.Wms.Messaging;
using Xunit;

namespace Wes.Persistence.Tests;

public sealed partial class WmsMasterDataTests
{
    [Fact]
    public async Task WmsPublisherStatesRemainReadableAndCountsOnlyPending()
    {
        var subject = await SeedEventAuthorityAsync(Wes.Platform.Domain.DataScope.All,
            CreationEventFields.Concat(EventCountFields).Distinct().ToArray());
        var pending = Command("PENDING"); var leased = Command("LEASED");
        var published = Command("PUBLISHED"); var quarantined = Command("QUARANTINED");
        foreach (var command in new[] { pending, leased, published, quarantined }) await ExecuteAsync(command);
        var clock = new PublishingClock(_clock.GetUtcNow());
        await using (var owner = Open(_tenant))
            await Assert.ThrowsAsync<InvalidOperationException>(() => Publisher(owner,
                new(_ => throw new InvalidOperationException("Controlled send interruption.")), clock).PublishAsync(leased.OperationId));
        await using (var owner = Open(_tenant))
            await Publisher(owner, new(), clock).PublishAsync(published.OperationId);
        await using (var owner = Open(_tenant))
            await Publisher(owner, new(_ => Task.FromResult(WmsOutboxTransportOutcome.PermanentFailure)), clock).PublishAsync(quarantined.OperationId);
        await using var platform = OpenAuthority();
        await using var reader = Open(_tenant);
        var access = Access(reader, new PlatformAuthority(platform, _clock));
        var page = await access.QueryCreationEventsAsync(subject, new());
        Assert.Equal(4, page.Rows.Length);
        Assert.Equal(new[] { WmsOutboxStatus.Pending, WmsOutboxStatus.Leased, WmsOutboxStatus.Published, WmsOutboxStatus.Quarantined },
            page.Rows.Select(row => row.Status).Order());
        Assert.Equal(1, (await access.ReadCreationEventSummaryAsync(subject)).Summary!.PendingCount);
    }

    [Fact]
    public async Task WmsPublisherPersistsAcknowledgementWithoutChangingTheSourceOrReplayingCreation()
    {
        var command = Command();
        await ExecuteAsync(command);
        var transport = new WmsTransport();
        await using (var scope = new WmsPublishingScope(_connection, _tenant, transport, _clock, NullLoggerFactory.Instance))
        {
            Assert.Equal(new WmsPublishBatchResult(1, 0, 0, 0, 0), await scope.DispatchDueAsync(25));
            Assert.Equal(new WmsPublishBatchResult(0, 0, 0, 0, 0), await scope.DispatchDueAsync(25));
        }
        Assert.Equal(MasterDataDecision.Replayed, (await ExecuteAsync(command)).Decision);
        var envelope = Assert.Single(transport.Envelopes);
        Assert.Equal((_tenant, command.OperationId, "Wms", "wms.master-data.created", 1),
            (envelope.TenantId, envelope.EventId, envelope.Owner, envelope.ContractName, envelope.SchemaVersion));
        await using var verify = Open(_tenant);
        var row = await verify.OutboxMessages.SingleAsync();
        Assert.Equal((WmsOutboxStatus.Published, 1, _clock.GetUtcNow()), (row.Status, row.Attempts, row.PublishedAt));
        Assert.Equal(row.Payload, envelope.Payload);
        Assert.Null(row.LeaseId); Assert.Null(row.LeaseUntil);
        Assert.False((await verify.Warehouses.SingleAsync()).Enabled);
        Assert.Empty(await verify.Entries.ToListAsync());
    }

    [Fact]
    public async Task WmsPublisherSchedulesExactlyFiveBoundedRetriesAndThenQuarantines()
    {
        var command = Command();
        await ExecuteAsync(command);
        var clock = new PublishingClock(_clock.GetUtcNow());
        var transport = new WmsTransport(_ => Task.FromResult(WmsOutboxTransportOutcome.TemporaryFailure));
        foreach (var delay in new[] { 1, 5, 30, 120, 600 })
        {
            await using var owner = Open(_tenant);
            Assert.Equal(WmsOutboxPublishResult.RetryScheduled, await Publisher(owner, transport, clock).PublishAsync(command.OperationId));
            await using var verify = Open(_tenant);
            var row = await verify.OutboxMessages.SingleAsync();
            Assert.Equal(clock.Now.AddSeconds(delay), row.NextAttemptAt);
            Assert.Equal(WmsOutboxPublishResult.NotClaimed, await Publisher(owner, transport, clock).PublishAsync(command.OperationId));
            clock.Now = row.NextAttemptAt;
        }
        await using (var owner = Open(_tenant))
            Assert.Equal(WmsOutboxPublishResult.Quarantined, await Publisher(owner, transport, clock).PublishAsync(command.OperationId));
        await using var final = Open(_tenant);
        Assert.Equal(6, (await final.OutboxMessages.SingleAsync()).Attempts);
        Assert.Equal(6, transport.Envelopes.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WmsPublisherQuarantinesPermanentFailureOrExpiredAutomaticBudget(bool budget)
    {
        var command = Command();
        await ExecuteAsync(command);
        var clock = new PublishingClock(_clock.GetUtcNow());
        var transport = new WmsTransport(_ => Task.FromResult(budget ? WmsOutboxTransportOutcome.TemporaryFailure : WmsOutboxTransportOutcome.PermanentFailure));
        await using (var owner = Open(_tenant))
            Assert.Equal(budget ? WmsOutboxPublishResult.RetryScheduled : WmsOutboxPublishResult.Quarantined,
                await Publisher(owner, transport, clock).PublishAsync(command.OperationId));
        if (budget)
        {
            clock.Now = clock.Now.AddMinutes(15);
            await using var owner = Open(_tenant);
            Assert.Equal(WmsOutboxPublishResult.Quarantined, await Publisher(owner, transport, clock).PublishAsync(command.OperationId));
        }
        Assert.Single(transport.Envelopes);
        await using var verify = Open(_tenant);
        Assert.Equal(WmsOutboxStatus.Quarantined, (await verify.OutboxMessages.SingleAsync()).Status);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WmsPublisherCompetingOwnerCannotUseAnActiveLeaseAndLateAckCannotOverwriteTakeover(bool takeover)
    {
        var command = Command();
        await ExecuteAsync(command);
        var clock = new PublishingClock(_clock.GetUtcNow());
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<WmsOutboxTransportOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstTransport = new WmsTransport(_ => { entered.SetResult(); return release.Task; });
        await using var first = Open(_tenant);
        var firstAttempt = Publisher(first, firstTransport, clock).PublishAsync(command.OperationId);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        if (takeover) clock.Now = clock.Now.AddSeconds(90);
        await using var second = Open(_tenant);
        var otherTransport = new WmsTransport();
        Assert.Equal(takeover ? WmsOutboxPublishResult.Published : WmsOutboxPublishResult.NotClaimed,
            await Publisher(second, otherTransport, clock).PublishAsync(command.OperationId));
        release.SetResult(WmsOutboxTransportOutcome.Acknowledged);
        Assert.Equal(takeover ? WmsOutboxPublishResult.Unconfirmed : WmsOutboxPublishResult.Published, await firstAttempt);
        Assert.Equal(takeover ? 1 : 0, otherTransport.Envelopes.Count);
        await using var verify = Open(_tenant);
        var row = await verify.OutboxMessages.SingleAsync();
        Assert.Equal(WmsOutboxStatus.Published, row.Status);
        Assert.Equal(takeover ? 2 : 1, row.Attempts);
    }

    [Theory]
    [InlineData("cancel")]
    [InlineData("transport")]
    [InlineData("save")]
    public async Task WmsPublisherRecoversTheSameEventAfterUnconfirmedTransportOrAckSave(string fault)
    {
        var command = Command();
        await ExecuteAsync(command);
        var clock = new PublishingClock(_clock.GetUtcNow());
        using var cancellation = new CancellationTokenSource();
        var transport = new WmsTransport(_ =>
        {
            if (fault == "cancel") { cancellation.Cancel(); cancellation.Token.ThrowIfCancellationRequested(); }
            if (fault == "transport") throw new InvalidOperationException("Controlled transport interruption.");
            return Task.FromResult(WmsOutboxTransportOutcome.Acknowledged);
        });
        await using (var owner = Open(_tenant, fault == "save" ? new FailWmsAckSave() : null))
        {
            var publisher = Publisher(owner, transport, clock);
            if (fault == "cancel") await Assert.ThrowsAnyAsync<OperationCanceledException>(() => publisher.PublishAsync(command.OperationId, cancellation.Token));
            else await Assert.ThrowsAsync<InvalidOperationException>(() => publisher.PublishAsync(command.OperationId));
        }
        await using (var verify = Open(_tenant))
        {
            var row = await verify.OutboxMessages.SingleAsync();
            Assert.Equal((WmsOutboxStatus.Leased, 1), (row.Status, row.Attempts));
            Assert.Null(row.PublishedAt);
        }
        clock.Now = clock.Now.AddSeconds(90);
        var recovery = new WmsTransport();
        await using (var owner = Open(_tenant))
            Assert.Equal(WmsOutboxPublishResult.Published, await Publisher(owner, recovery, clock).PublishAsync(command.OperationId));
        Assert.Equal(Assert.Single(transport.Envelopes), Assert.Single(recovery.Envelopes));
    }

    [Theory]
    [InlineData("tracked")]
    [InlineData("transaction")]
    [InlineData("ambient")]
    public async Task WmsPublisherRequiresAnIndependentCleanOwner(string invalid)
    {
        await ExecuteAsync(Command());
        await using var owner = Open(_tenant);
        if (invalid == "tracked") await owner.OutboxMessages.FirstAsync();
        await using var transaction = invalid == "transaction" ? await owner.Database.BeginTransactionAsync() : null;
        using var ambient = invalid == "ambient" ? new System.Transactions.TransactionScope(System.Transactions.TransactionScopeAsyncFlowOption.Enabled) : null;
        var transport = new WmsTransport();
        await Assert.ThrowsAsync<InvalidOperationException>(() => Publisher(owner, transport, new(_clock.GetUtcNow())).DispatchDueAsync(25));
        Assert.Empty(transport.Envelopes);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(101)]
    public async Task WmsPublisherRejectsInvalidBatchBeforeIo(int limit)
    {
        await using var owner = Open(_tenant);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => Publisher(owner, new(), new(_clock.GetUtcNow())).DispatchDueAsync(limit));
    }

    [Fact]
    public async Task WmsPublisherDoesNotScanAnotherTenantAndHasNoModelDrift()
    {
        await ExecuteAsync(Command());
        await using var foreign = Open(_other);
        var transport = new WmsTransport();
        Assert.Empty(await Publisher(foreign, transport, new(_clock.GetUtcNow())).DispatchDueAsync(25));
        Assert.Empty(transport.Envelopes);
        Assert.False(foreign.Database.HasPendingModelChanges());
    }

    private static WmsOutboxPublisher Publisher(Wes.Wms.Infrastructure.Persistence.WmsDbContext database,
        WmsTransport transport, PublishingClock clock) => new(database, transport, clock, () => 0d, NullLogger<WmsOutboxPublisher>.Instance);
    private sealed class PublishingClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }
    private sealed class WmsTransport(Func<WmsOutboxEnvelope, Task<WmsOutboxTransportOutcome>>? publish = null) : IWmsOutboxTransport
    {
        public List<WmsOutboxEnvelope> Envelopes { get; } = [];
        public Task<WmsOutboxTransportOutcome> PublishAsync(WmsOutboxEnvelope envelope, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Envelopes.Add(envelope);
            return publish?.Invoke(envelope) ?? Task.FromResult(WmsOutboxTransportOutcome.Acknowledged);
        }
    }
    private sealed class FailWmsAckSave : SaveChangesInterceptor
    {
        private int _saves;
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (++_saves == 2) throw new InvalidOperationException("Controlled acknowledgement save failure.");
            return ValueTask.FromResult(result);
        }
    }
}
