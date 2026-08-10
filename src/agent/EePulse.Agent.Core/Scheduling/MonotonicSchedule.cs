namespace EePulse.Agent.Core.Scheduling;

public sealed class MonotonicSchedule(IMonotonicClock clock)
{
    public static TimeSpan GetInitialDelay(Guid probeId, TimeSpan interval) =>
        StableJitter.ForProbe(probeId, interval);

    public TimeSpan GetRemainingDelay(long cycleStartedTimestamp, TimeSpan interval)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(interval, TimeSpan.Zero);

        var elapsed = clock.GetElapsedTime(cycleStartedTimestamp, clock.GetTimestamp());
        return elapsed >= interval ? TimeSpan.Zero : interval - elapsed;
    }

    public ValueTask DelayUntilNextCycleAsync(
        long cycleStartedTimestamp,
        TimeSpan interval,
        CancellationToken cancellationToken) =>
        clock.DelayAsync(GetRemainingDelay(cycleStartedTimestamp, interval), cancellationToken);
}
