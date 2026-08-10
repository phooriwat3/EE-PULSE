using EePulse.Agent.Core.Scheduling;

namespace EePulse.Agent.Tests;

public sealed class MonotonicScheduleTests
{
    [Fact]
    public void RemainingDelayUsesMonotonicElapsedTime()
    {
        var clock = new FakeMonotonicClock(timestamp: 125, timestampFrequency: 10);
        var schedule = new MonotonicSchedule(clock);

        var delay = schedule.GetRemainingDelay(cycleStartedTimestamp: 100, TimeSpan.FromSeconds(3));

        Assert.Equal(TimeSpan.FromMilliseconds(500), delay);
    }

    [Fact]
    public void OverdueCycleHasNoAdditionalDelay()
    {
        var clock = new FakeMonotonicClock(timestamp: 200, timestampFrequency: 10);
        var schedule = new MonotonicSchedule(clock);

        var delay = schedule.GetRemainingDelay(cycleStartedTimestamp: 100, TimeSpan.FromSeconds(3));

        Assert.Equal(TimeSpan.Zero, delay);
    }

    [Fact]
    public async Task DelayIsCancellationAware()
    {
        var clock = new FakeMonotonicClock(timestamp: 10, timestampFrequency: 10);
        var schedule = new MonotonicSchedule(clock);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await schedule.DelayUntilNextCycleAsync(10, TimeSpan.FromSeconds(1), cancellation.Token));
    }

    private sealed class FakeMonotonicClock(long timestamp, long timestampFrequency) : IMonotonicClock
    {
        public long GetTimestamp() => timestamp;

        public TimeSpan GetElapsedTime(long startingTimestamp, long endingTimestamp) =>
            TimeSpan.FromSeconds((double)(endingTimestamp - startingTimestamp) / timestampFrequency);

        public ValueTask DelayAsync(TimeSpan delay, CancellationToken cancellationToken) =>
            new(Task.Delay(delay, cancellationToken));
    }
}
