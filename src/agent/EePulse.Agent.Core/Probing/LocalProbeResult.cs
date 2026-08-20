namespace EePulse.Agent.Core.Probing;

/// <summary>Immutable, local-only output of a single scheduled probe run.</summary>
public sealed record LocalProbeResult(
    long ConfigurationVersion,
    Guid ProbeId,
    DateTimeOffset StartedAt,
    DateTimeOffset EndedAt,
    int AttemptCount,
    int SuccessfulAttemptCount,
    decimal PacketLossRatio,
    decimal? MinRttMilliseconds,
    decimal? AverageRttMilliseconds,
    decimal? MaxRttMilliseconds,
    ProbeErrorCategory? ErrorCategory);

public enum ProbeErrorCategory
{
    Timeout,
    Unreachable,
    PermissionDenied,
    InvalidTarget,
    NetworkUnavailable,
    Cancelled,
    TransportError,
}

public sealed record LocalProbeExecution(
    long ConfigurationVersion,
    Guid ProbeId,
    string NormalizedTarget,
    int AttemptCount,
    TimeSpan Timeout,
    TimeSpan InterAttemptDelay)
{
    public static readonly TimeSpan DefaultInterAttemptDelay = TimeSpan.FromMilliseconds(250);
}

public interface IProbeExecutionClock
{
    DateTimeOffset GetUtcNow();

    long GetTimestamp();

    TimeSpan GetElapsedTime(long startingTimestamp, long endingTimestamp);

    ValueTask DelayAsync(TimeSpan delay, CancellationToken cancellationToken);
}

public sealed class SystemProbeExecutionClock(TimeProvider timeProvider) : IProbeExecutionClock
{
    public DateTimeOffset GetUtcNow() => timeProvider.GetUtcNow();

    public long GetTimestamp() => timeProvider.GetTimestamp();

    public TimeSpan GetElapsedTime(long startingTimestamp, long endingTimestamp) =>
        timeProvider.GetElapsedTime(startingTimestamp, endingTimestamp);

    public ValueTask DelayAsync(TimeSpan delay, CancellationToken cancellationToken) =>
        new(Task.Delay(delay, timeProvider, cancellationToken));
}

/// <summary>Consumes an immutable local result synchronously within its owning schedule worker.</summary>
public interface ILocalProbeResultSink
{
    void Publish(LocalProbeResult result);
}
