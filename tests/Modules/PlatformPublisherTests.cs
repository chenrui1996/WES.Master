using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Wes.Host.Platform;
using Wes.Platform.Messaging;
using Xunit;

namespace Wes.Modules.Tests;

public sealed class PlatformPublisherTests
{
    private static readonly Guid Tenant = Guid.NewGuid();
    private static readonly Guid Other = Guid.NewGuid();

    [Theory]
    [InlineData(null)]
    [InlineData("false")]
    public void DisabledRegistersNothing(string? enabled)
    {
        var services = new ServiceCollection();
        services.AddWesPlatformPublisher(Config(enabled is null ? [] : new() { ["Enabled"] = enabled }));
        Assert.DoesNotContain(services, item => item.ServiceType == typeof(IHostedService));
        Assert.DoesNotContain(services, item => item.ServiceType == typeof(IPlatformPublisherRunner));
    }

    [Theory]
    [InlineData("Enabled", "sensitive-sentinel")]
    [InlineData("IntervalSeconds", "0")]
    [InlineData("IntervalSeconds", "3601")]
    [InlineData("IntervalSeconds", "1.0")]
    [InlineData("BatchSize", "0")]
    [InlineData("BatchSize", "101")]
    [InlineData("BatchSize:child", "1")]
    [InlineData("TenantIds:0", "00000000-0000-0000-0000-000000000000")]
    [InlineData("TenantIds:0", "invalid")]
    [InlineData("TenantIds", "*")]
    [InlineData("TenantIds:2", "11111111-1111-1111-1111-111111111111")]
    [InlineData("BatchSze", "1")]
    public void SuppliedMalformedSettingsFailWithoutLeakingValues(string key, string value)
    {
        var values = Enabled();
        values[key] = value;
        var error = Assert.Throws<InvalidOperationException>(() => PlatformPublisherSettings.Load(Config(values)));
        Assert.DoesNotContain("sensitive-sentinel", error.ToString());
    }

    [Theory]
    [InlineData("1", "1")]
    [InlineData("3600", "100")]
    public void ExactConfigurationBoundsAndKeyCasingAreAccepted(string seconds, string batch)
    {
        var values = Enabled();
        values["IntervalSeconds"] = seconds;
        values["BatchSize"] = batch;
        var settings = PlatformPublisherSettings.Load(Config(values.ToDictionary(
            item => item.Key.ToLowerInvariant(), item => item.Value)));
        Assert.Equal(TimeSpan.FromSeconds(int.Parse(seconds)), settings.Interval);
        Assert.Equal(int.Parse(batch), settings.BatchSize);
    }

    [Theory]
    [InlineData("Wes:Identity:Enabled")]
    [InlineData("Features:Capabilities:MessageQueue")]
    [InlineData("Features:Capabilities:Outbox")]
    public void PublishingRequiresEveryDeploymentCapability(string key)
    {
        var configuration = Config(Enabled());
        configuration[key] = "false";
        Assert.Throws<InvalidOperationException>(() => PlatformPublisherSettings.Load(configuration));
    }

    [Fact]
    public void DisabledMalformedSettingsDuplicateTenantsAndOversizedAllowlistsFail()
    {
        Assert.Throws<InvalidOperationException>(() => PlatformPublisherSettings.Load(
            Config(new() { ["Enabled"] = "false", ["BatchSize"] = "invalid" })));
        var values = Enabled();
        values["TenantIds:1"] = Tenant.ToString("D");
        Assert.Throws<InvalidOperationException>(() => PlatformPublisherSettings.Load(Config(values)));
        for (var index = 1; index < 100; index++) values[$"TenantIds:{index}"] = Guid.NewGuid().ToString("D");
        Assert.Equal(100, PlatformPublisherSettings.Load(Config(values)).TenantIds.Length);
        values["TenantIds:100"] = Guid.NewGuid().ToString("D");
        Assert.Throws<InvalidOperationException>(() => PlatformPublisherSettings.Load(Config(values)));
    }

    [Fact]
    public void EnabledRequiresExactlyOneSingletonTransport()
    {
        var services = new ServiceCollection();
        Assert.Throws<InvalidOperationException>(() => services.AddWesPlatformPublisher(Config(Enabled())));
        services.AddScoped<IPlatformOutboxTransport, Transport>();
        Assert.Throws<InvalidOperationException>(() => services.AddWesPlatformPublisher(Config(Enabled())));
        services.Clear();
        services.AddSingleton<IPlatformOutboxTransport, Transport>();
        services.AddSingleton<IPlatformOutboxTransport, Transport>();
        Assert.Throws<InvalidOperationException>(() => services.AddWesPlatformPublisher(Config(Enabled())));
        services.Clear();
        services.AddSingleton<IPlatformOutboxTransport, Transport>();
        services.AddWesPlatformPublisher(Config(Enabled()));
        Assert.Single(services, item => item.ServiceType == typeof(IHostedService)
            && item.ImplementationType == typeof(PlatformPublisherWorker));
    }

    [Fact]
    public async Task TickUsesOnlyAllowlistAndOneBoundedBatchPerTenant()
    {
        var runner = new Runner((_, _, _) => Task.FromResult(new PlatformPublishBatchResult(1, 2, 3, 4)));
        var logs = new Logs();
        using var worker = Worker(runner, logs);
        await worker.RunTickAsync(CancellationToken.None);
        Assert.Equal([Tenant, Other], runner.Calls.Select(item => item.Tenant));
        Assert.All(runner.Calls, item => Assert.Equal(7, item.Batch));
        Assert.Equal(2, logs.Messages.Count(message => message.Contains("persisted", StringComparison.Ordinal)));
        Assert.All(logs.Exceptions, Assert.Null);
    }

    [Fact]
    public async Task TransientFailureContinuesButPermanentAndUnexpectedErrorsPropagate()
    {
        var logs = new Logs();
        var transient = new Runner((tenant, _, _) => tenant == Tenant
            ? throw new PlatformPublisherDatabaseException(true)
            : Task.FromResult(new PlatformPublishBatchResult(0, 0, 0, 0)));
        using var worker = Worker(transient, logs);
        await worker.RunTickAsync(CancellationToken.None);
        Assert.Equal(2, transient.Calls.Count);
        Assert.Contains(logs.Messages, message => message.Contains("database-transient", StringComparison.Ordinal));
        Assert.Single(logs.Messages, message => message.Contains("persisted", StringComparison.Ordinal));
        var permanent = new Runner((_, _, _) => throw new PlatformPublisherDatabaseException(false));
        using var broken = Worker(permanent, new());
        await Assert.ThrowsAsync<PlatformPublisherDatabaseException>(() => broken.RunTickAsync(CancellationToken.None));
        Assert.Single(permanent.Calls);
        using var bug = Worker(new Runner((_, _, _) => throw new InvalidOperationException("bug")), new());
        await Assert.ThrowsAsync<InvalidOperationException>(() => bug.RunTickAsync(CancellationToken.None));
    }

    [Fact]
    public async Task StopCancelsActiveTenantWithoutStartingNextOrClaimingSuccess()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runner = new Runner(async (_, _, ct) =>
        {
            entered.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return new(0, 0, 0, 0);
        });
        var logs = new Logs();
        using var worker = Worker(runner, logs);
        await worker.StartAsync(CancellationToken.None);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await worker.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Single(runner.Calls);
        Assert.DoesNotContain(logs.Messages, message => message.Contains("persisted", StringComparison.Ordinal));
        Assert.All(logs.Exceptions, Assert.Null);
    }

    [Fact]
    public async Task TenantDeadlineIsExplicitAndContinuesWithFreshBudgetForNextTenant()
    {
        var clock = new DeadlineClock();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var logs = new Logs();
        var runner = new Runner(async (tenant, _, ct) =>
        {
            if (tenant == Tenant)
            {
                entered.SetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            }
            return new(0, 0, 0, 0);
        });
        using var worker = Worker(runner, logs, clock);
        var tick = worker.RunTickAsync(CancellationToken.None);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        clock.Timers[0].Fire();
        await tick.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(2, runner.Calls.Count);
        Assert.Equal(2, clock.Timers.Count);
        Assert.All(clock.Timers, timer => Assert.True(timer.Disposed));
        Assert.Contains(logs.Messages, message => message.Contains("tick-deadline", StringComparison.Ordinal));
        Assert.Single(logs.Messages, message => message.Contains("persisted", StringComparison.Ordinal));
    }

    private static PlatformPublisherWorker Worker(Runner runner, Logs logs, TimeProvider? clock = null)
    {
        var values = Enabled();
        values["TenantIds:1"] = Other.ToString("D");
        return new(PlatformPublisherSettings.Load(Config(values)), runner, clock ?? TimeProvider.System, logs);
    }

    private static Dictionary<string, string?> Enabled() => new()
    {
        ["Enabled"] = "true",
        ["IntervalSeconds"] = "1",
        ["BatchSize"] = "7",
        ["TenantIds:0"] = Tenant.ToString("D")
    };

    private static IConfiguration Config(Dictionary<string, string?> values)
    {
        var entries = values.ToDictionary(item => $"Wes:PlatformPublisher:{item.Key}", item => item.Value);
        entries["Wes:Identity:Enabled"] = "true";
        entries["Features:Capabilities:MessageQueue"] = "true";
        entries["Features:Capabilities:Outbox"] = "true";
        return new ConfigurationBuilder().AddInMemoryCollection(entries).Build();
    }

    private sealed class Runner(Func<Guid, int, CancellationToken, Task<PlatformPublishBatchResult>> run) : IPlatformPublisherRunner
    {
        public List<(Guid Tenant, int Batch)> Calls { get; } = [];
        public Task<PlatformPublishBatchResult> RunAsync(Guid tenant, int batch, CancellationToken ct)
        {
            Calls.Add((tenant, batch));
            return run(tenant, batch, ct);
        }
    }

    private sealed class Transport : IPlatformOutboxTransport
    {
        public Task<PlatformOutboxTransportOutcome> PublishAsync(PlatformOutboxEnvelope envelope, CancellationToken ct) =>
            throw new InvalidOperationException("Test transport must not be invoked during registration.");
    }

    private sealed class Logs : ILogger<PlatformPublisherWorker>
    {
        public List<string> Messages { get; } = [];
        public List<Exception?> Exceptions { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Messages.Add(formatter(state, exception));
            Exceptions.Add(exception);
        }
    }

    private sealed class DeadlineClock : TimeProvider
    {
        public List<DeadlineTimer> Timers { get; } = [];
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            Assert.Equal(TimeSpan.FromSeconds(60), dueTime);
            Assert.Equal(Timeout.InfiniteTimeSpan, period);
            var timer = new DeadlineTimer(callback, state);
            Timers.Add(timer);
            return timer;
        }
    }

    private sealed class DeadlineTimer(TimerCallback callback, object? state) : ITimer
    {
        public bool Disposed { get; private set; }
        public void Fire() => callback(state);
        public bool Change(TimeSpan dueTime, TimeSpan period) => true;
        public void Dispose() => Disposed = true;
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }
}
