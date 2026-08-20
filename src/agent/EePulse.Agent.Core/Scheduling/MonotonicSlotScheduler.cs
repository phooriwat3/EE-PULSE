namespace EePulse.Agent.Core.Scheduling;

/// <summary>Computes the next future slot only; missed slots are deliberately coalesced.</summary>
public sealed class MonotonicSlotScheduler(IMonotonicClock clock)
{
    public long GetInitialFutureSlot(Guid installationId, Guid probeId, long configurationVersion, TimeSpan interval)
    {
        var now = clock.GetTimestamp();
        var jitter = StableJitter.ForProbe(installationId, probeId, configurationVersion, interval);
        var delta = clock.GetTimestampDelta(jitter);
        if (delta == 0)
        {
            delta = clock.GetTimestampDelta(interval);
        }

        return checked(now + delta);
    }

    public long GetNextFutureSlot(long anchorTimestamp, long lastScheduledTimestamp, TimeSpan interval)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(interval, TimeSpan.Zero);
        var now = clock.GetTimestamp();
        var elapsedSinceLastSlot = clock.GetElapsedTime(lastScheduledTimestamp, now);
        if (elapsedSinceLastSlot < TimeSpan.Zero)
        {
            return lastScheduledTimestamp;
        }

        if (elapsedSinceLastSlot == TimeSpan.Zero && anchorTimestamp == lastScheduledTimestamp)
        {
            return checked(now + clock.GetTimestampDelta(interval));
        }

        var intervalTimestampDelta = GetTimestampDelta(interval, lastScheduledTimestamp, now, anchorTimestamp);
        var intervalsToAdvance = (elapsedSinceLastSlot.Ticks / interval.Ticks) + 1;
        return checked(lastScheduledTimestamp + (intervalsToAdvance * intervalTimestampDelta));
    }

    private long GetTimestampDelta(TimeSpan interval, long lastScheduledTimestamp, long now, long anchorTimestamp)
    {
        var sampleStart = lastScheduledTimestamp;
        var sampleEnd = now;
        var elapsed = clock.GetElapsedTime(sampleStart, sampleEnd);
        if (elapsed <= TimeSpan.Zero)
        {
            sampleStart = anchorTimestamp;
            sampleEnd = lastScheduledTimestamp;
            elapsed = clock.GetElapsedTime(sampleStart, sampleEnd);
        }

        if (elapsed <= TimeSpan.Zero)
        {
            throw new ArgumentException("A positive monotonic elapsed-time sample is required.", nameof(anchorTimestamp));
        }

        return checked((long)Math.Round(
            (double)(sampleEnd - sampleStart) * interval.Ticks / elapsed.Ticks,
            MidpointRounding.AwayFromZero));
    }
}
