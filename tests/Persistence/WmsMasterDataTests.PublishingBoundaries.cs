using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Wes.Wms.Domain;
using Wes.Wms.Infrastructure.Persistence.Migrations;
using Wes.Wms.Messaging;
using Xunit;

namespace Wes.Persistence.Tests;

public sealed partial class WmsMasterDataTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WmsPublisherRechecksLeaseAndBudgetAfterClaimCommitBeforeTransport(bool budget)
    {
        var command = Command();
        await ExecuteAsync(command);
        var clock = new PublishingClock(_clock.GetUtcNow());
        if (budget)
        {
            await using var previous = Open(_tenant);
            await Publisher(previous, new(_ => Task.FromResult(WmsOutboxTransportOutcome.TemporaryFailure)), clock).PublishAsync(command.OperationId);
            clock.Now = clock.Now.AddSeconds(899);
        }
        var transport = new WmsTransport();
        await using (var owner = Open(_tenant, new AdvanceAfterWmsClaim(() => clock.Now = clock.Now.AddSeconds(budget ? 1 : 90))))
            Assert.Equal(budget ? Wes.Wms.Infrastructure.Messaging.WmsOutboxPublishResult.Quarantined
                : Wes.Wms.Infrastructure.Messaging.WmsOutboxPublishResult.Unconfirmed,
                await Publisher(owner, transport, clock).PublishAsync(command.OperationId));
        Assert.Empty(transport.Envelopes);
        await using var verify = Open(_tenant);
        Assert.Equal(budget ? WmsOutboxStatus.Quarantined : WmsOutboxStatus.Leased, (await verify.OutboxMessages.SingleAsync()).Status);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WmsPublisherDatabaseFailureIsSafeAndNeverBecomesAnEmptyBatch(bool transient)
    {
        var command = Command();
        await ExecuteAsync(command);
        await using var owner = Open(_tenant, new QueryFailure(transient));
        var transport = new WmsTransport();
        var error = await Assert.ThrowsAsync<Wes.Wms.Features.Ledger.WmsDatabaseException>(() =>
            Publisher(owner, transport, new(_clock.GetUtcNow())).PublishAsync(command.OperationId));
        Assert.Equal(transient, error.IsTransient);
        Assert.DoesNotContain("sensitive-sentinel", error.ToString(), StringComparison.Ordinal);
        Assert.Empty(transport.Envelopes);
        await using var verify = Open(_tenant);
        Assert.Equal(WmsOutboxStatus.Pending, (await verify.OutboxMessages.SingleAsync()).Status);
    }

    [Fact]
    public async Task WmsPublisherAckCasConflictRemainsUnconfirmedInsteadOfSuccess()
    {
        var command = Command();
        await ExecuteAsync(command);
        var transport = new WmsTransport(async _ =>
        {
            await using var competing = Open(_tenant);
            await competing.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE wms_outbox_messages SET DeliveryVersion = {Guid.NewGuid().ToString("D")} WHERE TenantId = {_tenant.ToString("D")}");
            return WmsOutboxTransportOutcome.Acknowledged;
        });
        await using (var owner = Open(_tenant))
            Assert.Equal(Wes.Wms.Infrastructure.Messaging.WmsOutboxPublishResult.Unconfirmed,
                await Publisher(owner, transport, new(_clock.GetUtcNow())).PublishAsync(command.OperationId));
        await using var verify = Open(_tenant);
        var row = await verify.OutboxMessages.SingleAsync();
        Assert.Equal(WmsOutboxStatus.Leased, row.Status);
        Assert.Null(row.PublishedAt);
    }

    [Fact]
    public async Task WmsPublisherUsesDurableCasWhenBothOwnersReadPending()
    {
        var command = Command();
        await ExecuteAsync(command);
        var pause = new PauseWmsClaim();
        var firstTransport = new WmsTransport();
        var clock = new PublishingClock(_clock.GetUtcNow());
        await using var first = Open(_tenant, pause);
        var attempt = Publisher(first, firstTransport, clock).PublishAsync(command.OperationId);
        await pause.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            await using var second = Open(_tenant);
            Assert.Equal(Wes.Wms.Infrastructure.Messaging.WmsOutboxPublishResult.Published,
                await Publisher(second, new(), clock).PublishAsync(command.OperationId));
        }
        finally { pause.Release.TrySetResult(); }
        Assert.Equal(Wes.Wms.Infrastructure.Messaging.WmsOutboxPublishResult.NotClaimed, await attempt);
        Assert.Empty(firstTransport.Envelopes);
        await using var verify = Open(_tenant);
        Assert.Equal(1, (await verify.OutboxMessages.SingleAsync()).Attempts);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WmsPublisherUnconfirmedClaimsCannotEscapeAttemptOrTimeBudget(bool elapsed)
    {
        var command = Command();
        await ExecuteAsync(command);
        var clock = new PublishingClock(_clock.GetUtcNow());
        var transport = new WmsTransport(_ => throw new InvalidOperationException("Controlled send interruption."));
        for (var index = 0; index < (elapsed ? 1 : 6); index++)
        {
            await using var owner = Open(_tenant);
            await Assert.ThrowsAsync<InvalidOperationException>(() => Publisher(owner, transport, clock).PublishAsync(command.OperationId));
            clock.Now = clock.Now.AddSeconds(90);
        }
        if (elapsed) clock.Now = clock.Now.AddMinutes(15);
        await using (var owner = Open(_tenant))
            Assert.Equal(Wes.Wms.Infrastructure.Messaging.WmsOutboxPublishResult.Quarantined,
                await Publisher(owner, transport, clock).PublishAsync(command.OperationId));
        Assert.Equal(elapsed ? 1 : 6, transport.Envelopes.Count);
        await using var verify = Open(_tenant);
        var row = await verify.OutboxMessages.SingleAsync();
        Assert.Equal(elapsed ? "BudgetExpired" : "RetryExhausted", row.LastErrorCategory);
    }

    [Theory]
    [InlineData(0d, 10_000_000L)]
    [InlineData(1d, 12_000_000L)]
    public async Task WmsPublisherJitterHasExactApprovedBounds(double sample, long delayTicks)
    {
        var command = Command();
        await ExecuteAsync(command);
        await using var owner = Open(_tenant);
        var publisher = new Wes.Wms.Infrastructure.Messaging.WmsOutboxPublisher(owner,
            new WmsTransport(_ => Task.FromResult(WmsOutboxTransportOutcome.TemporaryFailure)), _clock, () => sample,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<Wes.Wms.Infrastructure.Messaging.WmsOutboxPublisher>.Instance);
        await publisher.PublishAsync(command.OperationId);
        await using var verify = Open(_tenant);
        Assert.Equal(_clock.GetUtcNow().AddTicks(delayTicks), (await verify.OutboxMessages.SingleAsync()).NextAttemptAt);
    }

    [Fact]
    public async Task WmsPublisherCancellationBeforeClaimHasNoDurableOrTransportEffect()
    {
        var command = Command();
        await ExecuteAsync(command);
        var transport = new WmsTransport();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await using (var owner = Open(_tenant))
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                Publisher(owner, transport, new(_clock.GetUtcNow())).PublishAsync(command.OperationId, cancellation.Token));
        Assert.Empty(transport.Envelopes);
        await using var verify = Open(_tenant);
        Assert.Equal(0, (await verify.OutboxMessages.SingleAsync()).Attempts);
    }

    [Theory]
    [InlineData("ordinary")]
    [InlineData("payload")]
    [InlineData("version")]
    public async Task WmsDeliverySaveGateCannotModifyEnvelopeOrBypassItsPrivateEntry(string invalid)
    {
        await ExecuteAsync(Command());
        await using var owner = Open(_tenant);
        var message = await owner.OutboxMessages.SingleAsync();
        Assert.True(message.TryLease(_clock.GetUtcNow()));
        if (invalid == "payload") owner.Entry(message).Property(item => item.Payload).CurrentValue = "forged";
        if (invalid == "version") owner.Entry(message).Property(item => item.DeliveryVersion).CurrentValue =
            owner.Entry(message).Property(item => item.DeliveryVersion).OriginalValue;
        if (invalid == "ordinary") await Assert.ThrowsAsync<InvalidOperationException>(() => owner.SaveChangesAsync());
        else await Assert.ThrowsAsync<InvalidOperationException>(() => owner.SaveOutboxDeliveryAsync(default));
        await using var verify = Open(_tenant);
        Assert.Equal(WmsOutboxStatus.Pending, (await verify.OutboxMessages.SingleAsync()).Status);
    }

    [Theory]
    [InlineData("Payload")]
    [InlineData("Status")]
    public async Task WmsPublisherNeverSendsUnsupportedStoredEnvelopeOrState(string field)
    {
        var command = Command();
        await ExecuteAsync(command);
        await using var owner = Open(_tenant);
        await owner.Database.ExecuteSqlRawAsync(field == "Payload"
            ? "UPDATE wms_outbox_messages SET Payload = 'forged' WHERE TenantId = {0}"
            : "UPDATE wms_outbox_messages SET Status = '99' WHERE TenantId = {0}", _tenant.ToString("D"));
        var transport = new WmsTransport();
        await Assert.ThrowsAsync<InvalidOperationException>(() => Publisher(owner, transport, new(_clock.GetUtcNow())).PublishAsync(command.OperationId));
        Assert.Empty(transport.Envelopes);
    }

    [Fact]
    public async Task WmsPublisherMigrationBackfillsOldPendingWithoutInventingDeliveryEvidence()
    {
        await ExecuteAsync(Command());
        await using var owner = Open(_tenant);
        await owner.Database.OpenConnectionAsync();
        // A unique table inside the authorized test database exercises the actual migration operations on the old shape.
        var probe = "wms_outbox_probe_" + Guid.NewGuid().ToString("N");
        var delivery = new[] { "Attempts", "DeliveryVersion", "FirstFailureAt", "LastErrorCategory", "LeaseId", "LeaseUntil", "NextAttemptAt", "PublishedAt" };
        const string columns = "TenantId,EventId,ContractName,SchemaVersion,Owner,AggregateId,AggregateVersion,CorrelationId,CausationId,OccurredAt,Payload,Status";
        await Execute($"CREATE TABLE `{probe}` LIKE wms_outbox_messages");
        try
        {
            await Execute($"ALTER TABLE `{probe}` DROP INDEX IX_wms_outbox_messages_TenantId_Status_NextAttemptAt_EventId, "
                + string.Join(", ", delivery.Select(column => $"DROP COLUMN `{column}`")));
            await using (var insert = owner.Database.GetDbConnection().CreateCommand())
            {
                insert.CommandText = $"INSERT INTO `{probe}` ({columns}) SELECT {columns} FROM wms_outbox_messages WHERE TenantId = @tenant";
                var parameter = insert.CreateParameter(); parameter.ParameterName = "@tenant"; parameter.Value = _tenant.ToString("D");
                insert.Parameters.Add(parameter);
                Assert.Equal(1, await insert.ExecuteNonQueryAsync());
            }
            var migration = new WmsOutboxDelivery();
            var commands = owner.GetService<IMigrationsSqlGenerator>().Generate(migration.UpOperations, owner.Model);
            foreach (var sql in commands) await Execute(sql.CommandText
                .Replace("`wms_outbox_messages`", $"`{probe}`", StringComparison.Ordinal)
                .Replace("UPDATE wms_outbox_messages ", $"UPDATE `{probe}` ", StringComparison.Ordinal));
            await using var inspect = owner.Database.GetDbConnection().CreateCommand();
            inspect.CommandText = $"SELECT DeliveryVersion=EventId,NextAttemptAt=OccurredAt,Attempts,Status,FirstFailureAt,LeaseId,LeaseUntil,PublishedAt,LastErrorCategory FROM `{probe}`";
            await using var rows = await inspect.ExecuteReaderAsync();
            Assert.True(await rows.ReadAsync());
            Assert.True(rows.GetBoolean(0)); Assert.True(rows.GetBoolean(1));
            Assert.Equal(0, rows.GetInt32(2)); Assert.Equal("Pending", rows.GetString(3));
            for (var index = 4; index <= 8; index++) Assert.True(rows.IsDBNull(index));
            Assert.False(await rows.ReadAsync());
        }
        finally { await Execute($"DROP TABLE `{probe}`"); }
        async Task Execute(string sql)
        {
            await using var command = owner.Database.GetDbConnection().CreateCommand();
            command.CommandText = sql;
            await command.ExecuteNonQueryAsync();
        }
    }

    private sealed class PauseWmsClaim : SaveChangesInterceptor
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            Entered.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            return result;
        }
    }

    private sealed class AdvanceAfterWmsClaim(Action advance) : SaveChangesInterceptor
    {
        private int _saves;
        public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
        {
            if (++_saves == 1) advance();
            return ValueTask.FromResult(result);
        }
    }
}
