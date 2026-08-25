using EePulse.Domain.Common;
using EePulse.Domain.Status;

namespace EePulse.UnitTests;

public sealed class ProbeStatusEvaluationKernelTests
{
    public static IEnumerable<object[]> EvaluationCases()
    {
        yield return Case(
            "ST-01 bootstrap healthy success with both quality dimensions unconfigured",
            Policy(), State(ProbeStatus.Unknown), Success(800m, 0.25m),
            ProbeResultClassification.HealthySuccess, State(ProbeStatus.Up, 0, 1),
            Transition(ProbeStatus.Unknown, ProbeStatus.Up, ProbeStatusTransitionReason.BootstrapSuccess));

        yield return Case(
            "ST-01 bootstrap degraded success",
            Policy(warningRttMilliseconds: 500), State(ProbeStatus.Unknown), Success(500m),
            ProbeResultClassification.DegradedSuccess, State(ProbeStatus.Degraded, 0, 1),
            Transition(ProbeStatus.Unknown, ProbeStatus.Degraded, ProbeStatusTransitionReason.BootstrapSuccess));

        yield return Case(
            "ST-02 below-threshold failure preserves unknown without transition",
            Policy(failureThreshold: 3), State(ProbeStatus.Unknown, failures: 1), Failure(),
            ProbeResultClassification.Failure, State(ProbeStatus.Unknown, 2, 0), null);

        yield return Case(
            "ST-02 below-threshold failure preserves up without transition",
            Policy(failureThreshold: 3), State(ProbeStatus.Up, failures: 1), Failure(),
            ProbeResultClassification.Failure, State(ProbeStatus.Up, 2, 0), null);

        yield return Case(
            "ST-02 below-threshold failure preserves degraded without transition",
            Policy(failureThreshold: 3), State(ProbeStatus.Degraded, failures: 1), Failure(),
            ProbeResultClassification.Failure, State(ProbeStatus.Degraded, 2, 0), null);

        yield return Case(
            "ST-02 below-threshold failure then success resets failure count without transition",
            Policy(failureThreshold: 3), State(ProbeStatus.Up, failures: 2), Success(),
            ProbeResultClassification.HealthySuccess, State(ProbeStatus.Up, 0, 1), null);

        yield return Case(
            "ST-03 failure threshold enters down",
            Policy(failureThreshold: 3), State(ProbeStatus.Up, failures: 2), Failure(),
            ProbeResultClassification.Failure, State(ProbeStatus.Down, 3, 0),
            Transition(ProbeStatus.Up, ProbeStatus.Down, ProbeStatusTransitionReason.FailureThresholdMet));

        yield return Case(
            "ST-04 first recovery success enters recovering",
            Policy(recoveryThreshold: 2), State(ProbeStatus.Down, failures: 2), Success(),
            ProbeResultClassification.HealthySuccess, State(ProbeStatus.Recovering, 0, 1),
            Transition(ProbeStatus.Down, ProbeStatus.Recovering, ProbeStatusTransitionReason.RecoveryPending));

        yield return Case(
            "ST-04 recovery threshold returns quality-derived degraded",
            Policy(recoveryThreshold: 2, warningRttMilliseconds: 500), State(ProbeStatus.Recovering, successes: 1), Success(500m),
            ProbeResultClassification.DegradedSuccess, State(ProbeStatus.Degraded, 0, 2),
            Transition(ProbeStatus.Recovering, ProbeStatus.Degraded, ProbeStatusTransitionReason.RecoveryThresholdMet));

        yield return Case(
            "ST-04 recovering success below threshold increments without transition",
            Policy(recoveryThreshold: 3), State(ProbeStatus.Recovering, successes: 1), Success(),
            ProbeResultClassification.HealthySuccess, State(ProbeStatus.Recovering, 0, 2), null);

        yield return Case(
            "ST-05 failure during recovering resets counts and immediately returns down",
            Policy(failureThreshold: 3, recoveryThreshold: 2), State(ProbeStatus.Recovering, successes: 1), Failure(),
            ProbeResultClassification.Failure, State(ProbeStatus.Down, 1, 0),
            Transition(ProbeStatus.Recovering, ProbeStatus.Down, ProbeStatusTransitionReason.RecoveryFailed));

        yield return Case(
            "ST-16 RTT equality is degraded",
            Policy(warningRttMilliseconds: 500), State(ProbeStatus.Up), Success(500m),
            ProbeResultClassification.DegradedSuccess, State(ProbeStatus.Degraded, 0, 1),
            Transition(ProbeStatus.Up, ProbeStatus.Degraded, ProbeStatusTransitionReason.QualityDegraded));

        yield return Case(
            "ST-16 packet loss equality is degraded",
            Policy(warningPacketLossRatio: 0.05m), State(ProbeStatus.Up), Success(packetLossRatio: 0.05m),
            ProbeResultClassification.DegradedSuccess, State(ProbeStatus.Degraded, 0, 1),
            Transition(ProbeStatus.Up, ProbeStatus.Degraded, ProbeStatusTransitionReason.QualityDegraded));

        yield return Case(
            "ST-16 null RTT still evaluates packet loss",
            Policy(warningRttMilliseconds: 500, warningPacketLossRatio: 0.05m), State(ProbeStatus.Up), Success(null, 0.05m),
            ProbeResultClassification.DegradedSuccess, State(ProbeStatus.Degraded, 0, 1),
            Transition(ProbeStatus.Up, ProbeStatus.Degraded, ProbeStatusTransitionReason.QualityDegraded));

        yield return Case(
            "ST-16 null RTT without a packet-loss breach is up",
            Policy(warningRttMilliseconds: 500, warningPacketLossRatio: 0.05m), State(ProbeStatus.Degraded), Success(null, 0.04m),
            ProbeResultClassification.HealthySuccess, State(ProbeStatus.Up, 0, 1),
            Transition(ProbeStatus.Degraded, ProbeStatus.Up, ProbeStatusTransitionReason.QualityRestored));

        yield return Case(
            "ST-17 failure count saturates while already down without transition",
            Policy(failureThreshold: 3), State(ProbeStatus.Down, failures: 3), Failure(),
            ProbeResultClassification.Failure, State(ProbeStatus.Down, 3, 0), null);

        yield return Case(
            "ST-17 success count saturates while up without transition",
            Policy(recoveryThreshold: 2), State(ProbeStatus.Up, successes: 2), Success(),
            ProbeResultClassification.HealthySuccess, State(ProbeStatus.Up, 0, 2), null);

        yield return Case(
            "ST-17 degraded quality is restored to up",
            Policy(warningRttMilliseconds: 500), State(ProbeStatus.Degraded, successes: 2), Success(499m),
            ProbeResultClassification.HealthySuccess, State(ProbeStatus.Up, 0, 2),
            Transition(ProbeStatus.Degraded, ProbeStatus.Up, ProbeStatusTransitionReason.QualityRestored));

        yield return Case(
            "ST-18 recovery threshold one recovers directly",
            Policy(recoveryThreshold: 1), State(ProbeStatus.Down, failures: 3), Success(),
            ProbeResultClassification.HealthySuccess, State(ProbeStatus.Up, 0, 1),
            Transition(ProbeStatus.Down, ProbeStatus.Up, ProbeStatusTransitionReason.RecoveryThresholdMet));

        yield return Case(
            "ST-18 recovery threshold one recovers directly to degraded",
            Policy(recoveryThreshold: 1, warningRttMilliseconds: 500), State(ProbeStatus.Down, failures: 3), Success(500m),
            ProbeResultClassification.DegradedSuccess, State(ProbeStatus.Degraded, 0, 1),
            Transition(ProbeStatus.Down, ProbeStatus.Degraded, ProbeStatusTransitionReason.RecoveryThresholdMet));
    }

    [Theory]
    [MemberData(nameof(EvaluationCases))]
    public void EvaluateAppliesTheApprovedStatusPolicy(
        string _,
        ProbeStatusEvaluationPolicy policy,
        ProbeStatusState state,
        ProbeStatusObservation observation,
        ProbeResultClassification expectedClassification,
        ProbeStatusState expectedState,
        ProbeStatusTransition? expectedTransition)
    {
        var actual = ProbeStatusEvaluationKernel.Evaluate(policy, state, observation);

        Assert.Equal(expectedClassification, actual.Classification);
        Assert.Equal(expectedState, actual.State);
        Assert.Equal(expectedTransition, actual.Transition);
    }

    public static IEnumerable<object[]> InvalidInputs()
    {
        yield return Invalid("warning RTT is zero", Policy(warningRttMilliseconds: 0), State(ProbeStatus.Unknown), Success());
        yield return Invalid("warning packet loss is zero", Policy(warningPacketLossRatio: 0m), State(ProbeStatus.Unknown), Success());
        yield return Invalid("warning packet loss exceeds one", Policy(warningPacketLossRatio: 1.01m), State(ProbeStatus.Unknown), Success());
        yield return Invalid("failure threshold is zero", Policy(failureThreshold: 0), State(ProbeStatus.Unknown), Success());
        yield return Invalid("failure threshold exceeds maximum", Policy(failureThreshold: 101), State(ProbeStatus.Unknown), Success());
        yield return Invalid("recovery threshold is zero", Policy(recoveryThreshold: 0), State(ProbeStatus.Unknown), Success());
        yield return Invalid("recovery threshold exceeds maximum", Policy(recoveryThreshold: 101), State(ProbeStatus.Unknown), Success());
        yield return Invalid("failure count exceeds policy", Policy(), State(ProbeStatus.Up, failures: 4), Success());
        yield return Invalid("unknown reaches failure threshold", Policy(), State(ProbeStatus.Unknown, failures: 3), Success());
        yield return Invalid("up reaches failure threshold", Policy(), State(ProbeStatus.Up, failures: 3), Success());
        yield return Invalid("degraded reaches failure threshold", Policy(), State(ProbeStatus.Degraded, failures: 3), Success());
        yield return Invalid("average RTT is negative", Policy(), State(ProbeStatus.Up), Success(averageRttMilliseconds: -1m));
        yield return Invalid("packet loss is negative", Policy(), State(ProbeStatus.Up), Success(packetLossRatio: -0.01m));
        yield return Invalid("packet loss exceeds one", Policy(), State(ProbeStatus.Up), Success(packetLossRatio: 1.01m));
        yield return Invalid("status is undefined", Policy(), State((ProbeStatus)99), Success());
        yield return Invalid("both counters are positive", Policy(), State(ProbeStatus.Up, failures: 1, successes: 1), Success());
        yield return Invalid("unknown has success count", Policy(), State(ProbeStatus.Unknown, successes: 1), Success());
        yield return Invalid("down has no failure", Policy(), State(ProbeStatus.Down), Success());
        yield return Invalid("down has success", Policy(), State(ProbeStatus.Down, failures: 1, successes: 1), Success());
        yield return Invalid("recovering has a failure", Policy(recoveryThreshold: 3), State(ProbeStatus.Recovering, failures: 1), Success());
        yield return Invalid("recovering has no success", Policy(recoveryThreshold: 3), State(ProbeStatus.Recovering), Success());
        yield return Invalid("recovering reaches recovery threshold", Policy(recoveryThreshold: 3), State(ProbeStatus.Recovering, successes: 3), Success());
        yield return Invalid("recovering is impossible at threshold one", Policy(recoveryThreshold: 1), State(ProbeStatus.Recovering, successes: 1), Success());
    }

    [Theory]
    [MemberData(nameof(InvalidInputs))]
    public void EvaluateRejectsInvalidPolicyOrInput(
        string _,
        ProbeStatusEvaluationPolicy policy,
        ProbeStatusState state,
        ProbeStatusObservation observation)
    {
        Assert.Throws<DomainValidationException>(() => ProbeStatusEvaluationKernel.Evaluate(policy, state, observation));
    }

    private static object[] Case(
        string name,
        ProbeStatusEvaluationPolicy policy,
        ProbeStatusState state,
        ProbeStatusObservation observation,
        ProbeResultClassification classification,
        ProbeStatusState expectedState,
        ProbeStatusTransition? transition) =>
        [name, policy, state, observation, classification, expectedState, transition!];

    private static object[] Invalid(
        string name,
        ProbeStatusEvaluationPolicy policy,
        ProbeStatusState state,
        ProbeStatusObservation observation) =>
        [name, policy, state, observation];

    private static ProbeStatusEvaluationPolicy Policy(
        int failureThreshold = 3,
        int recoveryThreshold = 2,
        int? warningRttMilliseconds = null,
        decimal? warningPacketLossRatio = null) =>
        new(failureThreshold, recoveryThreshold, warningRttMilliseconds, warningPacketLossRatio);

    private static ProbeStatusState State(
        ProbeStatus status,
        int failures = 0,
        int successes = 0) =>
        new(status, failures, successes);

    private static ProbeStatusObservation Success(
        decimal? averageRttMilliseconds = 1m,
        decimal packetLossRatio = 0m) =>
        new(true, averageRttMilliseconds, packetLossRatio);

    private static ProbeStatusObservation Failure() => new(false, null, 0m);

    private static ProbeStatusTransition Transition(
        ProbeStatus from,
        ProbeStatus to,
        ProbeStatusTransitionReason reason) =>
        new(from, to, reason);
}
