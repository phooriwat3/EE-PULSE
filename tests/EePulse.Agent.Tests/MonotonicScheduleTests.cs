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

    [Fact]
    public void TimestampDeltaUsesClockFrequencyAndRoundsUp()
    {
        var clock = new FakeMonotonicClock(timestamp: 100, timestampFrequency: 3);

        Assert.Equal(3, clock.GetTimestampDelta(TimeSpan.FromSeconds(1)));
        Assert.Equal(1, clock.GetTimestampDelta(TimeSpan.FromTicks(1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => clock.GetTimestampDelta(TimeSpan.FromTicks(-1)));
    }

    [Fact]
    public void StableJitterUsesInstallationProbeAndConfigurationVersion()
    {
        var installation = Guid.Parse("11111111-2222-3333-4444-555555555555");
        var probe = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
        var interval = TimeSpan.FromSeconds(10);

        var jitter = StableJitter.ForProbe(installation, probe, 7, interval);

        Assert.Equal(TimeSpan.FromTicks(55_171_962), jitter);
        Assert.Equal(jitter, StableJitter.ForProbe(installation, probe, 7, interval));
        Assert.Equal(TimeSpan.FromTicks(48_255_742), StableJitter.ForProbe(Guid.Parse("21111111-2222-3333-4444-555555555555"), probe, 7, interval));
        Assert.Equal(TimeSpan.FromTicks(66_220_425), StableJitter.ForProbe(installation, Guid.Parse("baaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"), 7, interval));
        Assert.Equal(TimeSpan.FromTicks(38_597_280), StableJitter.ForProbe(installation, probe, 8, interval));
        Assert.InRange(jitter, TimeSpan.Zero, interval - TimeSpan.FromTicks(1));
    }

    [Fact]
    public void InitialSlotUsesConvertedPositiveInterval()
    {
        var clock = new FakeMonotonicClock(timestamp: 100, timestampFrequency: 10);
        var scheduler = new MonotonicSlotScheduler(clock);

        Assert.Equal(110, scheduler.GetNextFutureSlot(100, 100, TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public void InitialJitterSlotIsStrictlyFuture()
    {
        var clock = new FakeMonotonicClock(timestamp: 100, timestampFrequency: 10);
        var scheduler = new MonotonicSlotScheduler(clock);
        var interval = TimeSpan.FromSeconds(10);

        var slot = scheduler.GetInitialFutureSlot(
            Guid.Parse("11111111-2222-3333-4444-555555555555"),
            Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"),
            7,
            interval);

        Assert.Equal(100 + clock.GetTimestampDelta(StableJitter.ForProbe(
            Guid.Parse("11111111-2222-3333-4444-555555555555"),
            Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"),
            7,
            interval)), slot);
        Assert.True(slot > clock.GetTimestamp());
    }

    [Theory]
    [InlineData(95, 100, 105)]
    [InlineData(90, 100, 110)]
    [InlineData(100, 100, 110)]
    [InlineData(90, 135, 140)]
    [InlineData(110, 100, 110)]
    public void NextFutureSlotPreservesAlignmentAndCoalescesMissedSlots(long lastScheduled, long now, long expected)
    {
        var clock = new FakeMonotonicClock(timestamp: now, timestampFrequency: 1);
        var scheduler = new MonotonicSlotScheduler(clock);

        var slot = scheduler.GetNextFutureSlot(anchorTimestamp: 0, lastScheduled, TimeSpan.FromSeconds(10));

        Assert.Equal(expected, slot);
        Assert.True(slot > now);
    }

    private sealed class FakeMonotonicClock(long timestamp, long timestampFrequency) : IMonotonicClock
    {
        public long GetTimestamp() => timestamp;

        public TimeSpan GetElapsedTime(long startingTimestamp, long endingTimestamp) =>
            TimeSpan.FromSeconds((double)(endingTimestamp - startingTimestamp) / timestampFrequency);

        public long GetTimestampDelta(TimeSpan duration)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(duration, TimeSpan.Zero);
            return duration == TimeSpan.Zero ? 0 : checked((long)Math.Ceiling((decimal)duration.Ticks * timestampFrequency / TimeSpan.TicksPerSecond));
        }

        public ValueTask DelayAsync(TimeSpan delay, CancellationToken cancellationToken) =>
            new(Task.Delay(delay, cancellationToken));
    }
}
