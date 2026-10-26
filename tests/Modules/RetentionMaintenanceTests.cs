using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Wes.Host.Platform;
using Wes.Platform;
using Xunit;

namespace Wes.Modules.Tests;

/// <summary>RET-MAINT-01..04: strict opt-in composition, one bounded tick and cooperative stop.</summary>
public sealed class RetentionMaintenanceTests
{
    private static readonly Guid Tenant = Guid.NewGuid();
    private static readonly Guid Other = Guid.NewGuid();

    [Theory]
    [InlineData(null)]
    [InlineData("false")]
    public void DisabledDoesNotRegisterMaintenance(string? enabled)
    {
        var services = new ServiceCollection();
        services.AddWesPlatformRetention(Config(enabled is null ? [] : new() { ["Enabled"] = enabled }, identity: null));
        Assert.DoesNotContain(services, item => item.ServiceType == typeof(IHostedService));
        Assert.DoesNotContain(services, item => item.ServiceType == typeof(IPlatformRetentionRunner));
        Assert.DoesNotContain(services, item => item.ServiceType == typeof(PlatformRetentionSettings));
    }

    [Theory]
    [InlineData("Enabled", "yes")]
    [InlineData("Enabled", "")]
    [InlineData("IntervalSeconds", null)]
    [InlineData("IntervalSeconds", "60.0")]
    [InlineData("IntervalSeconds", "59")]
    [InlineData("IntervalSeconds", "86401")]
    [InlineData("IntervalSeconds", "garbage-secret")]
    [InlineData("BatchSize", null)]
    [InlineData("BatchSize", "0")]
    [InlineData("BatchSize", "201")]
    [InlineData("BatchSize", "2147483648")]
    [InlineData("TenantIds:0", null)]
    [InlineData("TenantIds:0", "00000000-0000-0000-0000-000000000000")]
    [InlineData("TenantIds:0", "garbage-secret")]
    [InlineData("TenantIds:2", "11111111-1111-1111-1111-111111111111")]
    [InlineData("TenantIds", "garbage-secret")]
    [InlineData("BatchSze", "10")]
    [InlineData("BatchSize:child", "10")]
    public void MalformedConfigurationFailsSafely(string key, string? value)
    {
        var values = Enabled();
        if (value is null) values.Remove(key);
        else values[key] = value;
        var error = Assert.Throws<InvalidOperationException>(() => PlatformRetentionSettings.Load(Config(values)));
        Assert.DoesNotContain("garbage-secret", error.ToString());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("false")]
    [InlineData("malformed")]
    public void EnabledRequiresIdentity(string? identity)
    {
        Assert.Throws<InvalidOperationException>(() => PlatformRetentionSettings.Load(Config(Enabled(), identity)));
    }

    [Fact]
    public void DuplicateTenantsAndMalformedDisabledValuesAreRejected()
    {
        var duplicate = Enabled();
        duplicate["TenantIds:1"] = Tenant.ToString("D");
        Assert.Throws<InvalidOperationException>(() => PlatformRetentionSettings.Load(Config(duplicate)));
        Assert.Throws<InvalidOperationException>(() => PlatformRetentionSettings.Load(
            Config(new() { ["Enabled"] = "false", ["BatchSize"] = "invalid" })));
    }

    [Fact]
    public void AllowlistHasAnExplicitHundredTenantCeiling()
    {
        var values = Enabled();
        for (var index = 1; index < 100; index++) values[$"TenantIds:{index}"] = Guid.NewGuid().ToString("D");
        Assert.Equal(100, PlatformRetentionSettings.Load(Config(values)).TenantIds.Length);
        values["TenantIds:100"] = Guid.NewGuid().ToString("D");
        Assert.Throws<InvalidOperationException>(() => PlatformRetentionSettings.Load(Config(values)));
    }

    [Theory]
    [InlineData("60", "1")]
    [InlineData("86400", "200")]
    public void EnabledRegistersOnlyWithExplicitValidBounds(string interval, string batch)
    {
        var values = Enabled();
        values["IntervalSeconds"] = interval;
        values["BatchSize"] = batch;
        var configuration = Config(values);
        var settings = PlatformRetentionSettings.Load(configuration);
        Assert.Equal(TimeSpan.FromSeconds(int.Parse(interval)), settings.Interval);
        Assert.Equal(int.Parse(batch), settings.BatchSize);
        Assert.Equal(Tenant, Assert.Single(settings.TenantIds));
        var services = new ServiceCollection();
        services.AddWesPlatformRetention(configuration);
        Assert.Single(services, item => item.ServiceType == typeof(IHostedService)
            && item.ImplementationType == typeof(PlatformRetentionWorker));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DeploymentConfigurationKeysFollowCaseInsensitiveProviderSemantics(bool upperCase)
    {
        var values = Enabled().ToDictionary(
            item => upperCase ? item.Key.ToUpperInvariant() : item.Key.ToLowerInvariant(),
            item => item.Value);
        var settings = PlatformRetentionSettings.Load(Config(values));

        Assert.True(settings.Enabled);
        Assert.Equal(Tenant, Assert.Single(settings.TenantIds));
        Assert.Equal(7, settings.BatchSize);
    }

    [Fact]
    public async Task TickProcessesOnlyAllowlistOnceAndLogsCommittedCounts()
    {
        var runner = new Runner((_, _, _) => Task.FromResult(new RetentionMaintenanceResult(2, 3)));
        var logs = new Logs();
        using var worker = Worker(runner, logs);
        await worker.RunTickAsync(CancellationToken.None);
        Assert.Equal([Tenant, Other], runner.Calls.Select(call => call.Tenant));
        Assert.All(runner.Calls, call => Assert.Equal(7, call.Batch));
        Assert.Equal(2, logs.Messages.Count(message => message.Contains("committed", StringComparison.Ordinal)));
        Assert.All(logs.Messages, message => Assert.Contains("directory 2, inbox 3", message));
        Assert.All(logs.Exceptions, Assert.Null);
    }

    [Fact]
    public async Task TransientFailureIsExplicitAndDoesNotSkipNextTenant()
    {
        var runner = new Runner((tenant, _, _) => tenant == Tenant
            ? throw new PlatformRetentionDatabaseException(true)
            : Task.FromResult(new RetentionMaintenanceResult(0, 0)));
        var logs = new Logs();
        using var worker = Worker(runner, logs);
        await worker.RunTickAsync(CancellationToken.None);
        Assert.Equal(2, runner.Calls.Count);
        Assert.Contains(logs.Messages, message => message.Contains("database-transient", StringComparison.Ordinal));
        Assert.Single(logs.Messages, message => message.Contains("committed", StringComparison.Ordinal));
        Assert.All(logs.Exceptions, Assert.Null);
    }

    [Fact]
    public async Task PermanentDatabaseFailureAndUnexpectedBugPropagate()
    {
        var logs = new Logs();
        var permanent = new Runner((_, _, _) => throw new PlatformRetentionDatabaseException(false));
        using var worker = Worker(permanent, logs);
        await Assert.ThrowsAsync<PlatformRetentionDatabaseException>(() => worker.RunTickAsync(CancellationToken.None));
        Assert.Single(permanent.Calls);
        Assert.Contains(logs.Messages, message => message.Contains("database-permanent", StringComparison.Ordinal));
        var bug = new Runner((_, _, _) => throw new InvalidOperationException("injected bug"));
        using var broken = Worker(bug, new());
        await Assert.ThrowsAsync<InvalidOperationException>(() => broken.RunTickAsync(CancellationToken.None));
        Assert.Single(bug.Calls);
    }

    [Fact]
    public async Task StopCancelsCurrentTenantAndDoesNotClaimSuccess()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runner = new Runner(async (_, _, ct) =>
        {
            entered.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return new(0, 0);
        });
        var logs = new Logs();
        using var worker = Worker(runner, logs);
        await worker.StartAsync(CancellationToken.None);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await worker.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Single(runner.Calls);
        Assert.Contains(logs.Messages, message => message.Contains("cancelled", StringComparison.Ordinal));
        Assert.DoesNotContain(logs.Messages, message => message.Contains("committed", StringComparison.Ordinal));
    }

    [Fact]
    public async Task HostedTickUsesTimeProviderAndStopDisposesWaitingTimer()
    {
        var clock = new TimerClock();
        var runner = new Runner((_, _, _) => Task.FromResult(new RetentionMaintenanceResult(0, 0)));
        using var worker = Worker(runner, new(), clock);
        await worker.StartAsync(CancellationToken.None);
        await clock.Created.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(2, runner.Calls.Count);
        Assert.Equal(TimeSpan.FromSeconds(60), clock.DueTime);
        await worker.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(clock.Timer.Disposed);
        Assert.Equal(2, runner.Calls.Count);
    }

    private static PlatformRetentionWorker Worker(Runner runner, Logs logs, TimeProvider? clock = null)
    {
        var values = Enabled();
        values["TenantIds:1"] = Other.ToString("D");
        return new(PlatformRetentionSettings.Load(Config(values)), runner, clock ?? TimeProvider.System, logs);
    }

    private static Dictionary<string, string?> Enabled() => new()
    {
        ["Enabled"] = "true",
        ["IntervalSeconds"] = "60",
        ["BatchSize"] = "7",
        ["TenantIds:0"] = Tenant.ToString("D")
    };

    private static IConfiguration Config(Dictionary<string, string?> values, string? identity = "true")
    {
        var prefixed = values.ToDictionary(pair => $"Wes:PlatformRetention:{pair.Key}", pair => pair.Value);
        if (identity is not null) prefixed["Wes:Identity:Enabled"] = identity;
        return new ConfigurationBuilder().AddInMemoryCollection(prefixed).Build();
    }

    private sealed class Runner(Func<Guid, int, CancellationToken, Task<RetentionMaintenanceResult>> action) : IPlatformRetentionRunner
    {
        public List<(Guid Tenant, int Batch)> Calls { get; } = [];
        public Task<RetentionMaintenanceResult> RunAsync(Guid tenant, int batch, CancellationToken ct)
        {
            Calls.Add((tenant, batch));
            return action(tenant, batch, ct);
        }
    }

    private sealed class Logs : ILogger<PlatformRetentionWorker>
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

    private sealed class TimerClock : TimeProvider
    {
        public TaskCompletionSource Created { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public WaitingTimer Timer { get; } = new();
        public TimeSpan DueTime { get; private set; }
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            DueTime = dueTime;
            Created.SetResult();
            return Timer;
        }
    }

    private sealed class WaitingTimer : ITimer
    {
        public bool Disposed { get; private set; }
        public bool Change(TimeSpan dueTime, TimeSpan period) => true;
        public void Dispose() => Disposed = true;
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }
}
