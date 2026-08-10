namespace EePulse.Agent.Core.Scheduling;

/// <summary>
/// Provides elapsed-time measurement and delays without relying on wall-clock time.
/// </summary>
public interface IMonotonicClock
{
    long GetTimestamp();

    TimeSpan GetElapsedTime(long startingTimestamp, long endingTimestamp);

    ValueTask DelayAsync(TimeSpan delay, CancellationToken cancellationToken);
}
