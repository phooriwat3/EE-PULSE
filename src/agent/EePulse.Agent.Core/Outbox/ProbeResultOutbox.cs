using EePulse.Agent.Core.Probing;

namespace EePulse.Agent.Core.Outbox;

public static class ProbeResultSchema
{
    public const int CurrentVersion = 1;
}

/// <summary>Immutable, delivery-ready representation of one completed local probe result.</summary>
public sealed record ProbeResultEnvelope(
    int ResultSchemaVersion,
    Guid ResultId,
    Guid AgentId,
    Guid ProbeId,
    long ConfigurationVersion,
    DateTimeOffset StartedAt,
    DateTimeOffset EndedAt,
    int AttemptCount,
    int SuccessfulAttemptCount,
    decimal PacketLossRatio,
    decimal? MinRttMilliseconds,
    decimal? AverageRttMilliseconds,
    decimal? MaxRttMilliseconds,
    ProbeErrorCategory? ErrorCategory)
{
    public static ProbeResultEnvelope Create(Guid agentId, LocalProbeResult result, Guid? resultId = null)
    {
        ArgumentNullException.ThrowIfNull(result);
        return new(
            ProbeResultSchema.CurrentVersion,
            resultId ?? Guid.NewGuid(),
            agentId,
            result.ProbeId,
            result.ConfigurationVersion,
            result.StartedAt,
            result.EndedAt,
            result.AttemptCount,
            result.SuccessfulAttemptCount,
            result.PacketLossRatio,
            result.MinRttMilliseconds,
            result.AverageRttMilliseconds,
            result.MaxRttMilliseconds,
            result.ErrorCategory);
    }
}

public enum ProbeResultOutboxState
{
    Pending,
    Acknowledged,
}

public sealed record ProbeResultOutboxRecord(
    long Sequence,
    ProbeResultEnvelope Envelope,
    ProbeResultOutboxState State,
    DateTimeOffset EnqueuedAt,
    DateTimeOffset? AcknowledgedAt,
    DateTimeOffset? CleanupEligibleAt,
    DateTimeOffset? CleanupDeadlineAt,
    int SerializedByteCount);

public sealed record ProbeResultOutboxReadLimit(int MaximumCount, int MaximumSerializedBytes)
{
    public void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(MaximumCount, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaximumSerializedBytes, 1);
    }
}

public interface IProbeResultOutbox : IAsyncDisposable
{
    ValueTask<ProbeResultOutboxRecord> EnqueueAsync(ProbeResultEnvelope envelope, CancellationToken cancellationToken);

    ValueTask<IReadOnlyList<ProbeResultOutboxRecord>> ReadPendingAsync(
        ProbeResultOutboxReadLimit limit,
        CancellationToken cancellationToken);

    ValueTask AcknowledgeAsync(IReadOnlyCollection<Guid> resultIds, DateTimeOffset acknowledgedAt, CancellationToken cancellationToken);

    ValueTask<int> CleanupAcknowledgedAsync(DateTimeOffset cleanupThrough, int maximumCount, CancellationToken cancellationToken);
}

public enum OutboxStoragePressureState
{
    Healthy,
    Degraded,
    Suspended,
}

public sealed record OutboxStoragePressureSnapshot(
    OutboxStoragePressureState State,
    long QuotaBytes,
    long ReserveBytes,
    bool IsReserveBreached);

public static class OutboxStoragePressure
{
    public const long DefaultQuotaBytes = 5L * 1024 * 1024 * 1024;
    public const long MinimumReserveBytes = 2L * 1024 * 1024 * 1024;

    public static OutboxStoragePressureSnapshot Calculate(
        long outboxBytes,
        long hostingVolumeBytes,
        long availableVolumeBytes,
        bool wasSuspended,
        long quotaBytes = DefaultQuotaBytes)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(outboxBytes, 0);
        ArgumentOutOfRangeException.ThrowIfLessThan(hostingVolumeBytes, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(availableVolumeBytes, 0);
        ArgumentOutOfRangeException.ThrowIfLessThan(quotaBytes, 1);

        var reserveBytes = Math.Max(MinimumReserveBytes, hostingVolumeBytes / 10);
        var reserveBreached = availableVolumeBytes < reserveBytes;
        var suspendedThreshold = PercentageOf(quotaBytes, 95);
        var resumeThreshold = PercentageOf(quotaBytes, 70);
        var degradedThreshold = PercentageOf(quotaBytes, 80);
        var suspended = outboxBytes >= suspendedThreshold || reserveBreached ||
            (wasSuspended && (outboxBytes >= resumeThreshold || reserveBreached));

        return new(
            suspended ? OutboxStoragePressureState.Suspended :
            outboxBytes >= degradedThreshold ? OutboxStoragePressureState.Degraded : OutboxStoragePressureState.Healthy,
            quotaBytes,
            reserveBytes,
            reserveBreached);
    }

    private static long PercentageOf(long value, int percentage) =>
        value / 100 * percentage + value % 100 * percentage / 100;
}
