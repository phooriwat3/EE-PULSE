using EePulse.Domain.Agents;
using EePulse.Domain.Common;

namespace EePulse.Domain.Status;

public enum ProbeResultProcessingDispositionKind
{
    StateDriving,
    LateOrder,
    FutureOrSkewSuspect,
    BeyondApprovedLateness,
    Disabled,
    HistoricalOther,
}

public enum AvailabilityIncidentStatus
{
    Open,
    Acknowledged,
    Resolved,
}

public enum IncidentLifecycleEventType
{
    Opened,
    Resolved,
    Occurrence,
}

public enum NotificationSuppressionEligibility
{
    Eligible,
    Suppressed,
    PolicyUnapproved,
}

public sealed class ProbeStatusProjection
{
    private ProbeStatusProjection() { }

    public ProbeStatusProjection(
        Guid probeId,
        ProbeStatus underlyingStatus,
        int consecutiveFailureCount,
        int consecutiveSuccessCount,
        DateTimeOffset? lastFreshEventAt,
        DateTimeOffset? watermarkEventAt,
        Guid? watermarkAgentId,
        Guid? watermarkResultId,
        Guid? openIncidentId = null)
    {
        ProbeId = Required(probeId, nameof(probeId));
        UnderlyingStatus = underlyingStatus;
        ConsecutiveFailureCount = consecutiveFailureCount;
        ConsecutiveSuccessCount = consecutiveSuccessCount;
        LastFreshEventAt = OptionalUtc(lastFreshEventAt, nameof(lastFreshEventAt));
        WatermarkEventAt = OptionalUtc(watermarkEventAt, nameof(watermarkEventAt));
        WatermarkAgentId = watermarkAgentId;
        WatermarkResultId = watermarkResultId;
        OpenIncidentId = OptionalId(openIncidentId, nameof(openIncidentId));
        StateVersion = 0;
        ValidateStructure();
    }

    public Guid ProbeId { get; private set; }
    public ProbeStatus UnderlyingStatus { get; private set; }
    public int ConsecutiveFailureCount { get; private set; }
    public int ConsecutiveSuccessCount { get; private set; }
    public DateTimeOffset? LastFreshEventAt { get; private set; }
    public DateTimeOffset? WatermarkEventAt { get; private set; }
    public Guid? WatermarkAgentId { get; private set; }
    public Guid? WatermarkResultId { get; private set; }
    public Guid? OpenIncidentId { get; private set; }
    public long StateVersion { get; private set; }

    public void ApplyResult(ProbeStatusState state, DateTimeOffset eventAt, Guid agentId, Guid resultId)
    {
        ArgumentNullException.ThrowIfNull(state);

        UnderlyingStatus = state.Status;
        ConsecutiveFailureCount = state.ConsecutiveFailureCount;
        ConsecutiveSuccessCount = state.ConsecutiveSuccessCount;
        LastFreshEventAt = Guard.Utc(eventAt, nameof(eventAt));
        WatermarkEventAt = Guard.Utc(eventAt, nameof(eventAt));
        WatermarkAgentId = Required(agentId, nameof(agentId));
        WatermarkResultId = Required(resultId, nameof(resultId));
        ValidateStructure();
    }

    private void ValidateStructure()
    {
        if (!Enum.IsDefined(UnderlyingStatus)) throw new DomainValidationException(nameof(UnderlyingStatus), "Probe status is invalid.");
        if (ConsecutiveFailureCount < 0 || ConsecutiveSuccessCount < 0) throw new DomainValidationException(nameof(ConsecutiveFailureCount), "Status counters cannot be negative.");
        if (ConsecutiveFailureCount > 0 && ConsecutiveSuccessCount > 0) throw new DomainValidationException(nameof(ConsecutiveFailureCount), "Status counters cannot both be positive.");
        var watermarkValueCount = new[] { WatermarkEventAt.HasValue, WatermarkAgentId.HasValue, WatermarkResultId.HasValue }.Count(value => value);
        if (watermarkValueCount is not 0 and not 3) throw new DomainValidationException(nameof(WatermarkEventAt), "Watermark values must be supplied together.");
        if (UnderlyingStatus == ProbeStatus.Down && (ConsecutiveFailureCount < 1 || ConsecutiveSuccessCount != 0)) throw new DomainValidationException(nameof(UnderlyingStatus), "Down status requires failures and no successes.");
        if (UnderlyingStatus == ProbeStatus.Recovering && (ConsecutiveFailureCount != 0 || ConsecutiveSuccessCount < 1)) throw new DomainValidationException(nameof(UnderlyingStatus), "Recovering status requires successes and no failures.");
    }

    private static Guid Required(Guid value, string name) => value == Guid.Empty ? throw new DomainValidationException(name, $"{name} is required.") : value;
    private static Guid? OptionalId(Guid? value, string name) => value == Guid.Empty ? throw new DomainValidationException(name, $"{name} is invalid.") : value;
    private static DateTimeOffset? OptionalUtc(DateTimeOffset? value, string name) => value.HasValue ? Guard.Utc(value.Value, name) : null;
}

public sealed class AvailabilityIncident
{
    public const string AvailabilityDownRuleKey = "availability-down";
    public const string SystemPolicyActor = "system-policy";
    public const string ConfirmedRecoveryReason = "confirmed-recovery";
    public const string RecoveryFailedReason = "recovery-failed";

    private AvailabilityIncident() { }

    public AvailabilityIncident(Guid id, Guid probeId, DateTimeOffset openedAt)
    {
        Id = Required(id, nameof(id));
        ProbeId = Required(probeId, nameof(probeId));
        RuleKey = AvailabilityDownRuleKey;
        Status = AvailabilityIncidentStatus.Open;
        OpenedAt = Guard.Utc(openedAt, nameof(openedAt));
        OccurrenceCount = 1;
    }

    public Guid Id { get; private set; }
    public Guid ProbeId { get; private set; }
    public string RuleKey { get; private set; } = string.Empty;
    public AvailabilityIncidentStatus Status { get; private set; }
    public DateTimeOffset OpenedAt { get; private set; }
    public DateTimeOffset? AcknowledgedAt { get; private set; }
    public string? AcknowledgedBy { get; private set; }
    public string? AcknowledgementComment { get; private set; }
    public DateTimeOffset? ResolvedAt { get; private set; }
    public string? ResolvedBy { get; private set; }
    public string? ResolutionNote { get; private set; }
    public int OccurrenceCount { get; private set; }

    public void RecordRecoveryFailedOccurrence()
    {
        if (Status is not (AvailabilityIncidentStatus.Open or AvailabilityIncidentStatus.Acknowledged))
        {
            throw new DomainValidationException(nameof(Status), "Only an active availability incident can record an occurrence.");
        }

        if (OccurrenceCount == int.MaxValue) throw new DomainValidationException(nameof(OccurrenceCount), "Occurrence count cannot overflow.");
        OccurrenceCount++;
    }

    public void ResolveForConfirmedRecovery(DateTimeOffset resolvedAt)
    {
        if (Status is not (AvailabilityIncidentStatus.Open or AvailabilityIncidentStatus.Acknowledged))
        {
            throw new DomainValidationException(nameof(Status), "Only an active availability incident can be resolved.");
        }

        ResolvedAt = Guard.Utc(resolvedAt, nameof(resolvedAt));
        if (ResolvedAt < OpenedAt) throw new DomainValidationException(nameof(resolvedAt), "Resolution cannot precede opening.");
        Status = AvailabilityIncidentStatus.Resolved;
        ResolvedBy = SystemPolicyActor;
        ResolutionNote = ConfirmedRecoveryReason;
    }

    private static Guid Required(Guid value, string name) => value == Guid.Empty ? throw new DomainValidationException(name, $"{name} is required.") : value;
}

public sealed class IncidentLifecycleEvent
{
    public const string OpenedLifecycleEventKey = "opened";
    public const string ResolvedLifecycleEventKey = "resolved";
    public const string OccurrenceLifecycleEventKeyPrefix = "occurrence:";

    private IncidentLifecycleEvent() { }

    public IncidentLifecycleEvent(Guid eventId, Guid incidentId, Guid probeId, Guid sourceAgentId, Guid sourceResultId,
        ProbeStatus sourceFromStatus, Guid policySnapshotId, int policyVersion, DateTimeOffset occurredAt)
    {
        EventId = Required(eventId, nameof(eventId));
        IncidentId = Required(incidentId, nameof(incidentId));
        ProbeId = Required(probeId, nameof(probeId));
        SourceAgentId = Required(sourceAgentId, nameof(sourceAgentId));
        SourceResultId = Required(sourceResultId, nameof(sourceResultId));
        if (!Enum.IsDefined(sourceFromStatus) || sourceFromStatus == ProbeStatus.Down) throw new DomainValidationException(nameof(sourceFromStatus), "An opening source transition must enter Down from a non-Down status.");
        SourceFromStatus = sourceFromStatus;
        SourceToStatus = ProbeStatus.Down;
        SourceReasonCode = "failure-threshold-met";
        PolicySnapshotId = Required(policySnapshotId, nameof(policySnapshotId));
        PolicyVersion = Guard.Range(policyVersion, nameof(policyVersion), 1, int.MaxValue);
        LifecycleEventType = IncidentLifecycleEventType.Opened;
        LifecycleEventKey = OpenedLifecycleEventKey;
        ProcessingDisposition = ProbeResultProcessingDispositionKind.StateDriving;
        OccurredAt = Guard.Utc(occurredAt, nameof(occurredAt));
    }

    public static IncidentLifecycleEvent ForConfirmedRecovery(
        Guid eventId,
        Guid incidentId,
        Guid probeId,
        Guid sourceAgentId,
        Guid sourceResultId,
        ProbeStatus sourceToStatus,
        Guid policySnapshotId,
        int policyVersion,
        DateTimeOffset occurredAt) =>
        new(eventId, incidentId, probeId, sourceAgentId, sourceResultId, sourceToStatus, policySnapshotId, policyVersion, occurredAt, true);

    public static IncidentLifecycleEvent ForRecoveryFailedOccurrence(
        Guid eventId,
        Guid incidentId,
        Guid probeId,
        Guid sourceAgentId,
        Guid sourceResultId,
        Guid policySnapshotId,
        int policyVersion,
        DateTimeOffset occurredAt) =>
        new(eventId, incidentId, probeId, sourceAgentId, sourceResultId, policySnapshotId, policyVersion, occurredAt, true, true);

    private IncidentLifecycleEvent(Guid eventId, Guid incidentId, Guid probeId, Guid sourceAgentId, Guid sourceResultId,
        ProbeStatus sourceToStatus, Guid policySnapshotId, int policyVersion, DateTimeOffset occurredAt, bool confirmedRecovery)
    {
        EventId = Required(eventId, nameof(eventId));
        IncidentId = Required(incidentId, nameof(incidentId));
        ProbeId = Required(probeId, nameof(probeId));
        SourceAgentId = Required(sourceAgentId, nameof(sourceAgentId));
        SourceResultId = Required(sourceResultId, nameof(sourceResultId));
        if (!confirmedRecovery || sourceToStatus is not (ProbeStatus.Up or ProbeStatus.Degraded)) throw new DomainValidationException(nameof(sourceToStatus), "A confirmed recovery source transition must enter Up or Degraded from Recovering.");
        SourceFromStatus = ProbeStatus.Recovering;
        SourceToStatus = sourceToStatus;
        SourceReasonCode = "recovery-threshold-met";
        PolicySnapshotId = Required(policySnapshotId, nameof(policySnapshotId));
        PolicyVersion = Guard.Range(policyVersion, nameof(policyVersion), 1, int.MaxValue);
        LifecycleEventType = IncidentLifecycleEventType.Resolved;
        LifecycleEventKey = ResolvedLifecycleEventKey;
        ProcessingDisposition = ProbeResultProcessingDispositionKind.StateDriving;
        OccurredAt = Guard.Utc(occurredAt, nameof(occurredAt));
    }

    private IncidentLifecycleEvent(Guid eventId, Guid incidentId, Guid probeId, Guid sourceAgentId, Guid sourceResultId,
        Guid policySnapshotId, int policyVersion, DateTimeOffset occurredAt, bool recoveryFailed, bool occurrence)
    {
        EventId = Required(eventId, nameof(eventId));
        IncidentId = Required(incidentId, nameof(incidentId));
        ProbeId = Required(probeId, nameof(probeId));
        SourceAgentId = Required(sourceAgentId, nameof(sourceAgentId));
        SourceResultId = Required(sourceResultId, nameof(sourceResultId));
        if (!recoveryFailed || !occurrence) throw new DomainValidationException(nameof(recoveryFailed), "A recovery-failed occurrence requires its fixed lifecycle shape.");
        SourceFromStatus = ProbeStatus.Recovering;
        SourceToStatus = ProbeStatus.Down;
        SourceReasonCode = AvailabilityIncident.RecoveryFailedReason;
        PolicySnapshotId = Required(policySnapshotId, nameof(policySnapshotId));
        PolicyVersion = Guard.Range(policyVersion, nameof(policyVersion), 1, int.MaxValue);
        LifecycleEventType = IncidentLifecycleEventType.Occurrence;
        LifecycleEventKey = OccurrenceLifecycleEventKey(sourceResultId);
        ProcessingDisposition = ProbeResultProcessingDispositionKind.StateDriving;
        OccurredAt = Guard.Utc(occurredAt, nameof(occurredAt));
    }

    public static string OccurrenceLifecycleEventKey(Guid sourceResultId) =>
        OccurrenceLifecycleEventKeyPrefix + Required(sourceResultId, nameof(sourceResultId)).ToString("D").ToLowerInvariant();

    public Guid EventId { get; private set; }
    public Guid IncidentId { get; private set; }
    public Guid ProbeId { get; private set; }
    public Guid SourceAgentId { get; private set; }
    public Guid SourceResultId { get; private set; }
    public ProbeStatus SourceFromStatus { get; private set; }
    public ProbeStatus SourceToStatus { get; private set; }
    public string SourceReasonCode { get; private set; } = string.Empty;
    public Guid PolicySnapshotId { get; private set; }
    public int PolicyVersion { get; private set; }
    public IncidentLifecycleEventType LifecycleEventType { get; private set; }
    public string LifecycleEventKey { get; private set; } = string.Empty;
    public ProbeResultProcessingDispositionKind ProcessingDisposition { get; private set; }
    public DateTimeOffset OccurredAt { get; private set; }

    private static Guid Required(Guid value, string name) => value == Guid.Empty ? throw new DomainValidationException(name, $"{name} is required.") : value;
}

public sealed class NotificationSuppressionContext
{
    private NotificationSuppressionContext() { }

    public static NotificationSuppressionContext ForAvailabilityDownOpened(
        IncidentLifecycleEvent lifecycleEvent, DateTimeOffset evaluatedAt) =>
        Create(lifecycleEvent, IncidentLifecycleEventType.Opened, IncidentLifecycleEvent.OpenedLifecycleEventKey,
            NotificationSuppressionEligibility.Eligible, AvailabilityIncident.AvailabilityDownRuleKey, evaluatedAt);

    public static NotificationSuppressionContext ForConfirmedRecovery(
        IncidentLifecycleEvent lifecycleEvent, DateTimeOffset evaluatedAt) =>
        Create(lifecycleEvent, IncidentLifecycleEventType.Resolved, IncidentLifecycleEvent.ResolvedLifecycleEventKey,
            NotificationSuppressionEligibility.Eligible, AvailabilityIncident.ConfirmedRecoveryReason, evaluatedAt);

    public static NotificationSuppressionContext ForSuppressedRecoveryFailed(
        IncidentLifecycleEvent lifecycleEvent, DateTimeOffset evaluatedAt) =>
        Create(lifecycleEvent, IncidentLifecycleEventType.Occurrence,
            IncidentLifecycleEvent.OccurrenceLifecycleEventKey(lifecycleEvent.SourceResultId),
            NotificationSuppressionEligibility.Suppressed, AvailabilityIncident.RecoveryFailedReason, evaluatedAt);

    private static NotificationSuppressionContext Create(
        IncidentLifecycleEvent lifecycleEvent,
        IncidentLifecycleEventType expectedType,
        string expectedKey,
        NotificationSuppressionEligibility eligibility,
        string reasonCode,
        DateTimeOffset evaluatedAt)
    {
        ArgumentNullException.ThrowIfNull(lifecycleEvent);
        if (lifecycleEvent.LifecycleEventType != expectedType || lifecycleEvent.LifecycleEventKey != expectedKey)
        {
            throw new DomainValidationException(nameof(lifecycleEvent), "The lifecycle event does not match the required suppression context shape.");
        }

        return new(lifecycleEvent.EventId, lifecycleEvent.IncidentId, expectedKey, lifecycleEvent.PolicyVersion, evaluatedAt, eligibility, reasonCode);
    }

    private NotificationSuppressionContext(Guid eventId, Guid incidentId, string lifecycleEventKey, int policyVersion,
        DateTimeOffset evaluatedAt, NotificationSuppressionEligibility eligibility, string reasonCode)
    {
        EventId = Required(eventId, nameof(eventId));
        IncidentId = Required(incidentId, nameof(incidentId));
        LifecycleEventKey = Guard.Required(lifecycleEventKey, nameof(lifecycleEventKey), 128);
        PolicyVersion = Guard.Range(policyVersion, nameof(policyVersion), 1, int.MaxValue);
        Eligibility = eligibility;
        ReasonCode = Guard.Required(reasonCode, nameof(reasonCode), 64);
        EvaluatedAt = Guard.Utc(evaluatedAt, nameof(evaluatedAt));
    }

    public Guid EventId { get; private set; }
    public Guid IncidentId { get; private set; }
    public string LifecycleEventKey { get; private set; } = string.Empty;
    public int PolicyVersion { get; private set; }
    public NotificationSuppressionEligibility Eligibility { get; private set; }
    public string ReasonCode { get; private set; } = string.Empty;
    public DateTimeOffset EvaluatedAt { get; private set; }

    private static Guid Required(Guid value, string name) => value == Guid.Empty ? throw new DomainValidationException(name, $"{name} is required.") : value;
}

public sealed class ProbeStatusPolicySnapshot
{
    private ProbeStatusPolicySnapshot() { }

    public ProbeStatusPolicySnapshot(Guid id, int policyVersion, int failureThreshold, int recoveryThreshold,
        int? warningRttMilliseconds, decimal? warningPacketLossRatio, DateTimeOffset createdAt)
    {
        Id = Required(id, nameof(id));
        PolicyVersion = Guard.Range(policyVersion, nameof(policyVersion), 1, int.MaxValue);
        FailureThreshold = Guard.Range(failureThreshold, nameof(failureThreshold), 1, 100);
        RecoveryThreshold = Guard.Range(recoveryThreshold, nameof(recoveryThreshold), 1, 100);
        if (warningRttMilliseconds is <= 0) throw new DomainValidationException(nameof(warningRttMilliseconds), "Warning RTT must be positive when supplied.");
        if (warningPacketLossRatio is <= 0 or > 1) throw new DomainValidationException(nameof(warningPacketLossRatio), "Warning packet loss ratio must be greater than zero and at most one when supplied.");
        WarningRttMilliseconds = warningRttMilliseconds;
        WarningPacketLossRatio = warningPacketLossRatio;
        ApprovedLatenessSeconds = 300;
        ApprovedFutureSkewSeconds = 60;
        CreatedAt = Guard.Utc(createdAt, nameof(createdAt));
    }

    public Guid Id { get; private set; }
    public int PolicyVersion { get; private set; }
    public int FailureThreshold { get; private set; }
    public int RecoveryThreshold { get; private set; }
    public int? WarningRttMilliseconds { get; private set; }
    public decimal? WarningPacketLossRatio { get; private set; }
    public int ApprovedLatenessSeconds { get; private set; }
    public int ApprovedFutureSkewSeconds { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    private static Guid Required(Guid value, string name) => value == Guid.Empty ? throw new DomainValidationException(name, $"{name} is required.") : value;
}

public sealed class ProbeStatusPolicyBinding
{
    private ProbeStatusPolicyBinding() { }

    public ProbeStatusPolicyBinding(Guid probeId, long configurationVersion, Guid agentGroupId, Guid policySnapshotId)
    {
        ProbeId = Required(probeId, nameof(probeId));
        ConfigurationVersion = configurationVersion < 1 ? throw new DomainValidationException(nameof(configurationVersion), "Configuration version must be positive.") : configurationVersion;
        AgentGroupId = Required(agentGroupId, nameof(agentGroupId));
        PolicySnapshotId = Required(policySnapshotId, nameof(policySnapshotId));
    }

    public Guid ProbeId { get; private set; }
    public long ConfigurationVersion { get; private set; }
    public Guid AgentGroupId { get; private set; }
    public Guid PolicySnapshotId { get; private set; }

    private static Guid Required(Guid value, string name) => value == Guid.Empty ? throw new DomainValidationException(name, $"{name} is required.") : value;
}

public sealed class AgentConfigurationEffectiveBoundary
{
    private AgentConfigurationEffectiveBoundary() { }

    public AgentConfigurationEffectiveBoundary(Guid agentId, long configurationVersion, Guid sourceAcknowledgementId,
        AgentAcknowledgementStatus sourceAcknowledgementStatus, DateTimeOffset appliedAcknowledgementReceivedAt)
    {
        AgentId = Required(agentId, nameof(agentId));
        ConfigurationVersion = configurationVersion < 1 ? throw new DomainValidationException(nameof(configurationVersion), "Configuration version must be positive.") : configurationVersion;
        SourceAcknowledgementId = Required(sourceAcknowledgementId, nameof(sourceAcknowledgementId));
        if (sourceAcknowledgementStatus != AgentAcknowledgementStatus.Applied) throw new DomainValidationException(nameof(sourceAcknowledgementStatus), "The source acknowledgement must be Applied.");
        SourceAcknowledgementStatus = sourceAcknowledgementStatus;
        AppliedAcknowledgementReceivedAt = Guard.Utc(appliedAcknowledgementReceivedAt, nameof(appliedAcknowledgementReceivedAt));
    }

    public Guid AgentId { get; private set; }
    public long ConfigurationVersion { get; private set; }
    public Guid SourceAcknowledgementId { get; private set; }
    public AgentAcknowledgementStatus SourceAcknowledgementStatus { get; private set; }
    public DateTimeOffset AppliedAcknowledgementReceivedAt { get; private set; }

    private static Guid Required(Guid value, string name) => value == Guid.Empty ? throw new DomainValidationException(name, $"{name} is required.") : value;
}

public sealed class ProbeResultProcessingDisposition
{
    private ProbeResultProcessingDisposition() { }

    public ProbeResultProcessingDisposition(Guid agentId, Guid resultId, Guid probeId, DateTimeOffset eventAt,
        ProbeResultProcessingDispositionKind disposition, string reasonCode, Guid? resolvedPolicySnapshotId,
        int? resolvedPolicyVersion, DateTimeOffset decidedAt)
    {
        AgentId = Required(agentId, nameof(agentId));
        ResultId = Required(resultId, nameof(resultId));
        ProbeId = Required(probeId, nameof(probeId));
        EventAt = Guard.Utc(eventAt, nameof(eventAt));
        if (!Enum.IsDefined(disposition)) throw new DomainValidationException(nameof(disposition), "Processing disposition is invalid.");
        Disposition = disposition;
        ReasonCode = Guard.Required(reasonCode, nameof(reasonCode), 64);
        if (resolvedPolicySnapshotId.HasValue != resolvedPolicyVersion.HasValue) throw new DomainValidationException(nameof(resolvedPolicySnapshotId), "Resolved policy snapshot identity and version must be supplied together.");
        if (resolvedPolicySnapshotId == Guid.Empty) throw new DomainValidationException(nameof(resolvedPolicySnapshotId), "Resolved policy snapshot id is invalid.");
        if (resolvedPolicyVersion is <= 0) throw new DomainValidationException(nameof(resolvedPolicyVersion), "Resolved policy version must be positive when supplied.");
        if (disposition == ProbeResultProcessingDispositionKind.StateDriving && !resolvedPolicySnapshotId.HasValue) throw new DomainValidationException(nameof(resolvedPolicySnapshotId), "State-driving dispositions require resolved policy lineage.");
        ResolvedPolicySnapshotId = resolvedPolicySnapshotId;
        ResolvedPolicyVersion = resolvedPolicyVersion;
        DecidedAt = Guard.Utc(decidedAt, nameof(decidedAt));
    }

    public Guid AgentId { get; private set; }
    public Guid ResultId { get; private set; }
    public Guid ProbeId { get; private set; }
    public DateTimeOffset EventAt { get; private set; }
    public ProbeResultProcessingDispositionKind Disposition { get; private set; }
    public string ReasonCode { get; private set; } = string.Empty;
    public Guid? ResolvedPolicySnapshotId { get; private set; }
    public int? ResolvedPolicyVersion { get; private set; }
    public DateTimeOffset DecidedAt { get; private set; }

    private static Guid Required(Guid value, string name) => value == Guid.Empty ? throw new DomainValidationException(name, $"{name} is required.") : value;
}

// This persisted history is intentionally distinct from the evaluation kernel's ProbeStatusTransition.
public sealed class ProbeResultStatusTransition
{
    private static readonly string[] ValidReasonCodes =
    [
        "bootstrap-success",
        "quality-degraded",
        "quality-restored",
        "failure-threshold-met",
        "recovery-pending",
        "recovery-threshold-met",
        "recovery-failed",
    ];

    private ProbeResultStatusTransition() { }

    public ProbeResultStatusTransition(Guid agentId, Guid resultId, Guid probeId, ProbeStatus fromStatus,
        ProbeStatus toStatus, string reasonCode, DateTimeOffset eventAt, DateTimeOffset receivedAt,
        ProbeResultProcessingDispositionKind processingDisposition)
    {
        AgentId = Required(agentId, nameof(agentId));
        ResultId = Required(resultId, nameof(resultId));
        ProbeId = Required(probeId, nameof(probeId));
        if (!Enum.IsDefined(fromStatus)) throw new DomainValidationException(nameof(fromStatus), "From status is invalid.");
        if (!Enum.IsDefined(toStatus)) throw new DomainValidationException(nameof(toStatus), "To status is invalid.");
        if (fromStatus == toStatus) throw new DomainValidationException(nameof(toStatus), "A status transition must change status.");
        FromStatus = fromStatus;
        ToStatus = toStatus;
        ReasonCode = Guard.Required(reasonCode, nameof(reasonCode), 64);
        if (!ValidReasonCodes.Contains(ReasonCode, StringComparer.Ordinal)) throw new DomainValidationException(nameof(reasonCode), "Transition reason code is invalid.");
        EventAt = Guard.Utc(eventAt, nameof(eventAt));
        ReceivedAt = Guard.Utc(receivedAt, nameof(receivedAt));
        if (processingDisposition != ProbeResultProcessingDispositionKind.StateDriving) throw new DomainValidationException(nameof(processingDisposition), "Status transitions require a state-driving processing disposition.");
        ProcessingDisposition = processingDisposition;
    }

    public Guid AgentId { get; private set; }
    public Guid ResultId { get; private set; }
    public Guid ProbeId { get; private set; }
    public ProbeStatus FromStatus { get; private set; }
    public ProbeStatus ToStatus { get; private set; }
    public string ReasonCode { get; private set; } = string.Empty;
    public DateTimeOffset EventAt { get; private set; }
    public DateTimeOffset ReceivedAt { get; private set; }
    public ProbeResultProcessingDispositionKind ProcessingDisposition { get; private set; }

    public static string ReasonCodeFor(ProbeStatusTransitionReason reason) => reason switch
    {
        ProbeStatusTransitionReason.BootstrapSuccess => "bootstrap-success",
        ProbeStatusTransitionReason.QualityDegraded => "quality-degraded",
        ProbeStatusTransitionReason.QualityRestored => "quality-restored",
        ProbeStatusTransitionReason.FailureThresholdMet => "failure-threshold-met",
        ProbeStatusTransitionReason.RecoveryPending => "recovery-pending",
        ProbeStatusTransitionReason.RecoveryThresholdMet => "recovery-threshold-met",
        ProbeStatusTransitionReason.RecoveryFailed => "recovery-failed",
        _ => throw new ArgumentOutOfRangeException(nameof(reason), reason, "Transition reason is invalid."),
    };

    private static Guid Required(Guid value, string name) => value == Guid.Empty ? throw new DomainValidationException(name, $"{name} is required.") : value;
}
