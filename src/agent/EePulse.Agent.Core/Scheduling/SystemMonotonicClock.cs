namespace EePulse.Agent.Core.Scheduling;

public sealed class SystemMonotonicClock(TimeProvider timeProvider) : IMonotonicClock
{
    public static SystemMonotonicClock Instance { get; } = new(TimeProvider.System);

    public long GetTimestamp() => timeProvider.GetTimestamp();

    public TimeSpan GetElapsedTime(long startingTimestamp, long endingTimestamp) =>
        timeProvider.GetElapsedTime(startingTimestamp, endingTimestamp);

    public long GetTimestampDelta(TimeSpan duration)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(duration, TimeSpan.Zero);
        if (duration == TimeSpan.Zero) return 0;
        return checked((long)Math.Ceiling((decimal)duration.Ticks * timeProvider.TimestampFrequency / TimeSpan.TicksPerSecond));
    }

    public ValueTask DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(delay, TimeSpan.Zero);

        return new ValueTask(Task.Delay(delay, timeProvider, cancellationToken));
    }
}
