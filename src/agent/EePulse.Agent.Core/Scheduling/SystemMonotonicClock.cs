namespace EePulse.Agent.Core.Scheduling;

public sealed class SystemMonotonicClock(TimeProvider timeProvider) : IMonotonicClock
{
    public static SystemMonotonicClock Instance { get; } = new(TimeProvider.System);

    public long GetTimestamp() => timeProvider.GetTimestamp();

    public TimeSpan GetElapsedTime(long startingTimestamp, long endingTimestamp) =>
        timeProvider.GetElapsedTime(startingTimestamp, endingTimestamp);

    public ValueTask DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(delay, TimeSpan.Zero);

        return new ValueTask(Task.Delay(delay, timeProvider, cancellationToken));
    }
}
