using EePulse.Domain.Common;

namespace EePulse.Domain.Status;

public enum ProbeStatus
{
    Unknown,
    Up,
    Degraded,
    Down,
    Recovering,
}

public enum ProbeResultClassification
{
    Failure,
    HealthySuccess,
    DegradedSuccess,
}

public enum ProbeStatusTransitionReason
{
    BootstrapSuccess,
    QualityDegraded,
    QualityRestored,
    FailureThresholdMet,
    RecoveryPending,
    RecoveryThresholdMet,
    RecoveryFailed,
}

public sealed record ProbeStatusEvaluationPolicy(
    int FailureThreshold,
    int RecoveryThreshold,
    int? WarningRttMilliseconds,
    decimal? WarningPacketLossRatio)
{
    public void Validate()
    {
        _ = Guard.Range(FailureThreshold, nameof(FailureThreshold), 1, 100);
        _ = Guard.Range(RecoveryThreshold, nameof(RecoveryThreshold), 1, 100);

        if (WarningRttMilliseconds is <= 0)
        {
            throw new DomainValidationException(nameof(WarningRttMilliseconds), "Warning RTT must be positive when supplied.");
        }

        if (WarningPacketLossRatio is <= 0 or > 1)
        {
            throw new DomainValidationException(nameof(WarningPacketLossRatio), "Warning packet loss ratio must be greater than zero and at most one when supplied.");
        }
    }
}

public sealed record ProbeStatusState(
    ProbeStatus Status,
    int ConsecutiveFailureCount,
    int ConsecutiveSuccessCount);

public sealed record ProbeStatusObservation(
    bool IsSuccess,
    decimal? AverageRttMilliseconds,
    decimal PacketLossRatio);

public sealed record ProbeStatusTransition(
    ProbeStatus From,
    ProbeStatus To,
    ProbeStatusTransitionReason Reason);

public sealed record ProbeStatusEvaluationResult(
    ProbeResultClassification Classification,
    ProbeStatusState State,
    ProbeStatusTransition? Transition);

public static class ProbeStatusEvaluationKernel
{
    public static ProbeStatusEvaluationResult Evaluate(
        ProbeStatusEvaluationPolicy policy,
        ProbeStatusState state,
        ProbeStatusObservation observation)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(observation);

        policy.Validate();
        ValidateState(policy, state);
        ValidateObservation(observation);

        var classification = Classify(policy, observation);
        return classification == ProbeResultClassification.Failure
            ? EvaluateFailure(policy, state, classification)
            : EvaluateSuccess(policy, state, classification);
    }

    public static ProbeResultClassification Classify(
        ProbeStatusEvaluationPolicy policy,
        ProbeStatusObservation observation)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(observation);

        policy.Validate();
        ValidateObservation(observation);

        if (!observation.IsSuccess)
        {
            return ProbeResultClassification.Failure;
        }

        var rttBreached = policy.WarningRttMilliseconds.HasValue &&
            observation.AverageRttMilliseconds.HasValue &&
            observation.AverageRttMilliseconds.Value >= policy.WarningRttMilliseconds.Value;
        var packetLossBreached = policy.WarningPacketLossRatio.HasValue &&
            observation.PacketLossRatio >= policy.WarningPacketLossRatio.Value;

        return rttBreached || packetLossBreached
            ? ProbeResultClassification.DegradedSuccess
            : ProbeResultClassification.HealthySuccess;
    }

    private static ProbeStatusEvaluationResult EvaluateFailure(
        ProbeStatusEvaluationPolicy policy,
        ProbeStatusState state,
        ProbeResultClassification classification)
    {
        if (state.Status == ProbeStatus.Recovering)
        {
            var recoveryFailedState = new ProbeStatusState(ProbeStatus.Down, 1, 0);
            return new(classification, recoveryFailedState, new(state.Status, recoveryFailedState.Status, ProbeStatusTransitionReason.RecoveryFailed));
        }

        var failureCount = Math.Min(state.ConsecutiveFailureCount + 1, policy.FailureThreshold);
        var nextStatus = failureCount >= policy.FailureThreshold ? ProbeStatus.Down : state.Status;
        var failureState = new ProbeStatusState(nextStatus, failureCount, 0);
        return new(classification, failureState, Transition(state.Status, nextStatus, ProbeStatusTransitionReason.FailureThresholdMet));
    }

    private static ProbeStatusEvaluationResult EvaluateSuccess(
        ProbeStatusEvaluationPolicy policy,
        ProbeStatusState state,
        ProbeResultClassification classification)
    {
        var qualityStatus = classification == ProbeResultClassification.DegradedSuccess
            ? ProbeStatus.Degraded
            : ProbeStatus.Up;
        var successCount = Math.Min(state.ConsecutiveSuccessCount + 1, policy.RecoveryThreshold);

        if (state.Status is ProbeStatus.Down or ProbeStatus.Recovering)
        {
            var recoveryStatus = successCount < policy.RecoveryThreshold ? ProbeStatus.Recovering : qualityStatus;
            var recoveryReason = recoveryStatus == ProbeStatus.Recovering
                ? ProbeStatusTransitionReason.RecoveryPending
                : ProbeStatusTransitionReason.RecoveryThresholdMet;
            var recoveryState = new ProbeStatusState(recoveryStatus, 0, successCount);
            return new(classification, recoveryState, Transition(state.Status, recoveryStatus, recoveryReason));
        }

        var qualityState = new ProbeStatusState(qualityStatus, 0, successCount);
        var qualityReason = state.Status switch
        {
            ProbeStatus.Unknown => ProbeStatusTransitionReason.BootstrapSuccess,
            ProbeStatus.Up when qualityStatus == ProbeStatus.Degraded => ProbeStatusTransitionReason.QualityDegraded,
            ProbeStatus.Degraded when qualityStatus == ProbeStatus.Up => ProbeStatusTransitionReason.QualityRestored,
            _ => ProbeStatusTransitionReason.BootstrapSuccess,
        };
        return new(classification, qualityState, Transition(state.Status, qualityStatus, qualityReason));
    }

    private static ProbeStatusTransition? Transition(
        ProbeStatus from,
        ProbeStatus to,
        ProbeStatusTransitionReason reason) =>
        from == to ? null : new(from, to, reason);

    private static void ValidateState(ProbeStatusEvaluationPolicy policy, ProbeStatusState state)
    {
        if (!Enum.IsDefined(state.Status))
        {
            throw new DomainValidationException(nameof(state.Status), "Probe status is invalid.");
        }

        if (state.ConsecutiveFailureCount < 0 || state.ConsecutiveFailureCount > policy.FailureThreshold)
        {
            throw new DomainValidationException(nameof(state.ConsecutiveFailureCount), "Consecutive failure count is outside the policy bounds.");
        }

        if (state.ConsecutiveSuccessCount < 0 || state.ConsecutiveSuccessCount > policy.RecoveryThreshold)
        {
            throw new DomainValidationException(nameof(state.ConsecutiveSuccessCount), "Consecutive success count is outside the policy bounds.");
        }

        if (state.ConsecutiveFailureCount > 0 && state.ConsecutiveSuccessCount > 0)
        {
            throw new DomainValidationException(nameof(state), "Failure and success counters cannot both be positive.");
        }

        switch (state.Status)
        {
            case ProbeStatus.Unknown when state.ConsecutiveSuccessCount > 0:
                throw new DomainValidationException(nameof(state.ConsecutiveSuccessCount), "Unknown status cannot carry a positive success count.");
            case ProbeStatus.Unknown or ProbeStatus.Up or ProbeStatus.Degraded
                when state.ConsecutiveFailureCount >= policy.FailureThreshold:
                throw new DomainValidationException(nameof(state.ConsecutiveFailureCount), "Non-down status requires a below-threshold failure count.");
            case ProbeStatus.Down when state.ConsecutiveSuccessCount != 0 || state.ConsecutiveFailureCount < 1:
                throw new DomainValidationException(nameof(state), "Down status requires zero successes and at least one failure.");
            case ProbeStatus.Recovering when
                policy.RecoveryThreshold <= 1 ||
                state.ConsecutiveFailureCount != 0 ||
                state.ConsecutiveSuccessCount < 1 ||
                state.ConsecutiveSuccessCount >= policy.RecoveryThreshold:
                throw new DomainValidationException(nameof(state), "Recovering status requires a partial recovery success count and no failures.");
        }
    }

    private static void ValidateObservation(ProbeStatusObservation observation)
    {
        if (observation.AverageRttMilliseconds is < 0)
        {
            throw new DomainValidationException(nameof(observation.AverageRttMilliseconds), "Average RTT cannot be negative when supplied.");
        }

        if (observation.PacketLossRatio is < 0 or > 1)
        {
            throw new DomainValidationException(nameof(observation.PacketLossRatio), "Packet loss ratio must be between zero and one.");
        }
    }
}
