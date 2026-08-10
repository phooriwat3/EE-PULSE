using EePulse.Domain.Common;

namespace EePulse.Domain.Inventory;

public sealed class Probe
{
    private Probe()
    {
    }

    public Probe(
        Guid id,
        Guid deviceId,
        Guid agentGroupId,
        int intervalSeconds,
        int timeoutMilliseconds,
        int attemptCount,
        int? warningRttMilliseconds,
        int? criticalRttMilliseconds,
        int failureThreshold,
        int recoveryThreshold)
    {
        Id = id == Guid.Empty ? throw new DomainValidationException(nameof(id), "Probe id is required.") : id;
        DeviceId = deviceId == Guid.Empty ? throw new DomainValidationException(nameof(deviceId), "Device id is required.") : deviceId;
        AgentGroupId = agentGroupId == Guid.Empty ? throw new DomainValidationException(nameof(agentGroupId), "Agent group id is required.") : agentGroupId;
        Type = ProbeType.Icmp;
        ApplyConfiguration(intervalSeconds, timeoutMilliseconds, attemptCount, warningRttMilliseconds,
            criticalRttMilliseconds, failureThreshold, recoveryThreshold);
        Enabled = true;
        ConfigVersion = 1;
    }

    public Guid Id { get; private set; }
    public Guid DeviceId { get; private set; }
    public Guid AgentGroupId { get; private set; }
    public ProbeType Type { get; private set; }
    public int IntervalSeconds { get; private set; }
    public int TimeoutMilliseconds { get; private set; }
    public int AttemptCount { get; private set; }
    public int? WarningRttMilliseconds { get; private set; }
    public int? CriticalRttMilliseconds { get; private set; }
    public int FailureThreshold { get; private set; }
    public int RecoveryThreshold { get; private set; }
    public bool Enabled { get; private set; }
    public long ConfigVersion { get; private set; }
    public long RowVersion { get; private set; }

    public void Update(
        Guid agentGroupId,
        int intervalSeconds,
        int timeoutMilliseconds,
        int attemptCount,
        int? warningRttMilliseconds,
        int? criticalRttMilliseconds,
        int failureThreshold,
        int recoveryThreshold,
        bool enabled)
    {
        AgentGroupId = agentGroupId == Guid.Empty
            ? throw new DomainValidationException(nameof(agentGroupId), "Agent group id is required.")
            : agentGroupId;
        ApplyConfiguration(intervalSeconds, timeoutMilliseconds, attemptCount, warningRttMilliseconds,
            criticalRttMilliseconds, failureThreshold, recoveryThreshold);
        Enabled = enabled;
        ConfigVersion++;
    }

    private void ApplyConfiguration(
        int intervalSeconds,
        int timeoutMilliseconds,
        int attemptCount,
        int? warningRttMilliseconds,
        int? criticalRttMilliseconds,
        int failureThreshold,
        int recoveryThreshold)
    {
        IntervalSeconds = Guard.Range(intervalSeconds, nameof(intervalSeconds), 5, 3_600);
        TimeoutMilliseconds = Guard.Range(timeoutMilliseconds, nameof(timeoutMilliseconds), 100, 60_000);
        AttemptCount = Guard.Range(attemptCount, nameof(attemptCount), 1, 10);
        FailureThreshold = Guard.Range(failureThreshold, nameof(failureThreshold), 1, 100);
        RecoveryThreshold = Guard.Range(recoveryThreshold, nameof(recoveryThreshold), 1, 100);

        if (timeoutMilliseconds > intervalSeconds * 1_000)
        {
            throw new DomainValidationException(nameof(timeoutMilliseconds), "Probe timeout cannot exceed its interval.");
        }

        if (warningRttMilliseconds is <= 0)
        {
            throw new DomainValidationException(nameof(warningRttMilliseconds), "Warning RTT must be positive when supplied.");
        }

        if (criticalRttMilliseconds is <= 0)
        {
            throw new DomainValidationException(nameof(criticalRttMilliseconds), "Critical RTT must be positive when supplied.");
        }

        if (warningRttMilliseconds.HasValue && criticalRttMilliseconds.HasValue &&
            criticalRttMilliseconds.Value <= warningRttMilliseconds.Value)
        {
            throw new DomainValidationException(nameof(criticalRttMilliseconds), "Critical RTT must be greater than warning RTT.");
        }

        WarningRttMilliseconds = warningRttMilliseconds;
        CriticalRttMilliseconds = criticalRttMilliseconds;
    }
}
