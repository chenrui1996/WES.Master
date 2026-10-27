using Xunit;

namespace Wes.Modules.Tests;

internal sealed class PublisherDeadlineClock : TimeProvider
{
    internal List<PublisherDeadlineTimer> Timers { get; } = [];
    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        Assert.Equal(TimeSpan.FromSeconds(60), dueTime);
        Assert.Equal(Timeout.InfiniteTimeSpan, period);
        var timer = new PublisherDeadlineTimer(callback, state);
        Timers.Add(timer);
        return timer;
    }
}

internal sealed class PublisherDeadlineTimer(TimerCallback callback, object? state) : ITimer
{
    internal bool Disposed { get; private set; }
    internal void Fire() => callback(state);
    public bool Change(TimeSpan dueTime, TimeSpan period) => true;
    public void Dispose() => Disposed = true;
    public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
}
