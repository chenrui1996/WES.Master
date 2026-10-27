using System.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Wes.Host.Wms;
using Wes.Wms;
using Wes.Wms.Features.Ledger;
using Wes.Wms.Messaging;
using Xunit;

namespace Wes.Modules.Tests;

public sealed class WmsPublisherTests
{
    private static readonly Guid Tenant = Guid.NewGuid();
    private static readonly Guid Other = Guid.NewGuid();

    [Fact]
    public async Task ActualHostKeepsUnregisteredBrokerCapabilitiesBlocked()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "WES.slnx"))) root = root.Parent;
        Assert.NotNull(root);
        var start = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = Path.Combine(root.FullName, "src", "Host"),
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        start.ArgumentList.Add(typeof(Program).Assembly.Location);
        foreach (var argument in new[]
        {
            "--Wes:Identity:Enabled=true", "--Wes:WmsPublisher:Enabled=true", "--Wes:WmsPublisher:IntervalSeconds=1",
            "--Wes:WmsPublisher:BatchSize=1", $"--Wes:WmsPublisher:TenantIds:0={Tenant:D}",
            "--Features:Capabilities:Outbox=true", "--Features:Capabilities:MessageQueue=true"
        }) start.ArgumentList.Add(argument);
        start.Environment["DOTNET_ENVIRONMENT"] = "Development";
        start.Environment["ASPNETCORE_ENVIRONMENT"] = "Development";
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEndAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try { await process.WaitForExitAsync(deadline.Token); }
        finally
        {
            if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); }
        }
        Assert.NotEqual(0, process.ExitCode);
        var text = await output + await errors;
        Assert.True(text.Contains("Features:Capabilities:MessageQueue: no runtime owner is registered.", StringComparison.Ordinal), text);
        Assert.True(text.Contains("Features:Capabilities:Outbox: no runtime owner is registered.", StringComparison.Ordinal), text);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("false")]
    public void DisabledRegistersNoWorkerOrRunner(string? enabled)
    {
        var services = new ServiceCollection();
        services.AddWesWmsPublisher(Config(enabled is null ? [] : new() { ["Enabled"] = enabled }));
        Assert.DoesNotContain(services, item => item.ServiceType == typeof(IHostedService)
            || item.ServiceType == typeof(IWmsPublisherRunner) || item.ServiceType == typeof(IWmsOutboxTransport));
    }

    [Theory]
    [InlineData("Enabled", "sensitive-sentinel")]
    [InlineData("BatchSze", "1")]
    [InlineData("BatchSize", "0")]
    [InlineData("BatchSize", "101")]
    [InlineData("IntervalSeconds", "0")]
    [InlineData("IntervalSeconds", "3601")]
    [InlineData("TenantIds", "*")]
    [InlineData("TenantIds:0", "00000000-0000-0000-0000-000000000000")]
    public void SuppliedInvalidSettingsFailSafelyEvenWhenDisabled(string key, string value)
    {
        var values = Enabled(); values[key] = value;
        var error = Assert.Throws<InvalidOperationException>(() => WmsPublisherSettings.Load(Config(values)));
        Assert.DoesNotContain("sensitive-sentinel", error.ToString());
        values["Enabled"] = "false";
        if (key != "Enabled") Assert.Throws<InvalidOperationException>(() => WmsPublisherSettings.Load(Config(values)));
    }

    [Theory]
    [InlineData("1", "1")]
    [InlineData("3600", "100")]
    public void SharedScheduleRetainsExactWmsBounds(string seconds, string batch)
    {
        var values = Enabled(); values["IntervalSeconds"] = seconds; values["BatchSize"] = batch;
        var settings = WmsPublisherSettings.Load(Config(values));
        Assert.Equal(TimeSpan.FromSeconds(int.Parse(seconds)), settings.Interval);
        Assert.Equal(int.Parse(batch), settings.BatchSize);
        values["TenantIds:1"] = Tenant.ToString("D");
        Assert.Throws<InvalidOperationException>(() => WmsPublisherSettings.Load(Config(values)));
    }

    [Theory]
    [InlineData("Wes:Identity:Enabled")]
    [InlineData("Features:Capabilities:Outbox")]
    [InlineData("Features:Capabilities:MessageQueue")]
    public void RequiresEveryDeploymentDependency(string key)
    {
        var configuration = Config(Enabled()); configuration[key] = "false";
        Assert.Throws<InvalidOperationException>(() => WmsPublisherSettings.Load(configuration));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("scoped")]
    [InlineData("duplicate")]
    [InlineData("keyed")]
    public void EnabledRequiresOneExplicitUnkeyedSingletonWmsTransport(string invalid)
    {
        var services = new ServiceCollection();
        if (invalid == "scoped") services.AddScoped<IWmsOutboxTransport, Transport>();
        if (invalid == "duplicate") { services.AddSingleton<IWmsOutboxTransport, Transport>(); services.AddSingleton<IWmsOutboxTransport, Transport>(); }
        if (invalid == "keyed") services.AddKeyedSingleton<IWmsOutboxTransport, Transport>("not-default");
        Assert.Throws<InvalidOperationException>(() => services.AddWesWmsPublisher(Config(Enabled())));
        Assert.DoesNotContain(services, item => item.ServiceType == typeof(IHostedService));
    }

    [Fact]
    public void EnabledRegistersExactlyOneWmsWorkerWithoutPlatformTransport()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IWmsOutboxTransport, Transport>();
        services.AddWesWmsPublisher(Config(Enabled()));
        Assert.Single(services, item => item.ServiceType == typeof(IHostedService) && item.ImplementationType == typeof(WmsPublisherWorker));
        Assert.Single(services, item => item.ServiceType == typeof(IWmsPublisherRunner));
    }

    [Fact]
    public async Task TickIsSerialBoundedAndReportsUnconfirmedInsteadOfBusinessSuccess()
    {
        var runner = new Runner((_, _, _) => Task.FromResult(new WmsPublishBatchResult(1, 1, 1, 1, 1)));
        var logs = new Logs();
        using var worker = Worker(runner, logs);
        await worker.RunTickAsync(CancellationToken.None);
        Assert.Equal(new[] { Tenant, Other }, runner.Calls.Select(call => call.Tenant));
        Assert.All(runner.Calls, call => Assert.Equal(7, call.Batch));
        Assert.Equal(2, logs.Entries.Count(entry => entry.Level == LogLevel.Warning && entry.Text.Contains("unconfirmed", StringComparison.Ordinal)));
        Assert.All(logs.Entries, entry => Assert.Null(entry.Exception));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SafeDatabaseErrorsContinueOnlyWhenTransient(bool transient)
    {
        var runner = new Runner((tenant, _, _) => tenant == Tenant ? throw new WmsDatabaseException(transient) : Task.FromResult(new WmsPublishBatchResult(0, 0, 0, 0, 0)));
        var logs = new Logs();
        using var worker = Worker(runner, logs);
        if (transient) await worker.RunTickAsync(CancellationToken.None);
        else await Assert.ThrowsAsync<WmsDatabaseException>(() => worker.RunTickAsync(CancellationToken.None));
        Assert.Equal(transient ? 2 : 1, runner.Calls.Count);
        Assert.Contains(logs.Entries, entry => entry.Text.Contains(transient ? "database-transient" : "database-permanent", StringComparison.Ordinal));
        Assert.All(logs.Entries, entry => Assert.Null(entry.Exception));
    }

    [Fact]
    public async Task UnexpectedErrorsPropagateWithoutStartingAnotherTenant()
    {
        var runner = new Runner((_, _, _) => throw new InvalidOperationException("Controlled implementation fault."));
        using var worker = Worker(runner, new());
        await Assert.ThrowsAsync<InvalidOperationException>(() => worker.RunTickAsync(CancellationToken.None));
        Assert.Single(runner.Calls);
    }

    [Fact]
    public async Task StopCancelsActiveTenantWithoutStartingAnotherOrReportingACompletedBatch()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runner = new Runner(async (_, _, ct) =>
        {
            entered.SetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, ct); return new(0, 0, 0, 0, 0);
        });
        var logs = new Logs();
        using var worker = Worker(runner, logs);
        await worker.StartAsync(CancellationToken.None);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await worker.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Single(runner.Calls);
        Assert.DoesNotContain(logs.Entries, entry => entry.Text.Contains("batch for", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SixtySecondDeadlineIsExplicitAndNextTenantGetsAFreshBudget()
    {
        var clock = new PublisherDeadlineClock();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runner = new Runner(async (tenant, _, ct) =>
        {
            if (tenant == Tenant) { entered.SetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, ct); }
            return new(0, 0, 0, 0, 0);
        });
        var logs = new Logs();
        using var worker = Worker(runner, logs, clock);
        var tick = worker.RunTickAsync(CancellationToken.None);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        clock.Timers[0].Fire();
        await tick.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(2, runner.Calls.Count);
        Assert.Equal(2, clock.Timers.Count);
        Assert.All(clock.Timers, timer => Assert.True(timer.Disposed));
        Assert.Contains(logs.Entries, entry => entry.Text.Contains("tick-deadline", StringComparison.Ordinal));
    }

    private static WmsPublisherWorker Worker(Runner runner, Logs logs, TimeProvider? clock = null)
    {
        var values = Enabled(); values["TenantIds:1"] = Other.ToString("D");
        return new(WmsPublisherSettings.Load(Config(values)), runner, clock ?? TimeProvider.System, logs);
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
        var entries = values.ToDictionary(item => $"Wes:WmsPublisher:{item.Key}", item => item.Value);
        entries["Wes:Identity:Enabled"] = "true";
        entries["Features:Capabilities:Outbox"] = "true"; entries["Features:Capabilities:MessageQueue"] = "true";
        return new ConfigurationBuilder().AddInMemoryCollection(entries).Build();
    }
    private sealed class Runner(Func<Guid, int, CancellationToken, Task<WmsPublishBatchResult>> run) : IWmsPublisherRunner
    {
        public List<(Guid Tenant, int Batch)> Calls { get; } = [];
        public Task<WmsPublishBatchResult> RunAsync(Guid tenant, int batch, CancellationToken ct)
        { Calls.Add((tenant, batch)); return run(tenant, batch, ct); }
    }
    private sealed class Transport : IWmsOutboxTransport
    {
        public Task<WmsOutboxTransportOutcome> PublishAsync(WmsOutboxEnvelope envelope, CancellationToken ct) =>
            throw new InvalidOperationException("Registration test must never send.");
    }
    private sealed class Logs : ILogger<WmsPublisherWorker>
    {
        public List<(LogLevel Level, string Text, Exception? Exception)> Entries { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception), exception));
    }
}
