using EePulse.Domain.Agents;
using EePulse.Domain.Common;
using EePulse.Domain.Status;

namespace EePulse.UnitTests;

public sealed class ProbeStatusProcessingPersistenceTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 25, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ProjectionRequiresStructuralCounterAndCursorValidity()
    {
        var probeId = Guid.NewGuid();

        Assert.Throws<DomainValidationException>(() => new ProbeStatusProjection(
            probeId, ProbeStatus.Up, 1, 1, null, null, null, null));
        Assert.Throws<DomainValidationException>(() => new ProbeStatusProjection(
            probeId, ProbeStatus.Unknown, 0, 0, null, Now, Guid.NewGuid(), null));
        Assert.Throws<DomainValidationException>(() => new ProbeStatusProjection(
            probeId, ProbeStatus.Down, 0, 0, null, null, null, null));
        Assert.Throws<DomainValidationException>(() => new ProbeStatusProjection(
            probeId, ProbeStatus.Recovering, 1, 0, null, null, null, null));

        var valid = new ProbeStatusProjection(probeId, ProbeStatus.Down, 1, 0, Now, Now, Guid.NewGuid(), Guid.NewGuid());
        Assert.Equal(0, valid.StateVersion);
    }

    [Fact]
    public void ProjectionAppliesResultStateAndCursorWithoutViolatingInvariants()
    {
        var projection = new ProbeStatusProjection(Guid.NewGuid(), ProbeStatus.Unknown, 0, 0, null, null, null, null);
        var eventAt = Now.AddSeconds(1);
        var agentId = Guid.NewGuid();
        var resultId = Guid.NewGuid();

        projection.ApplyResult(new ProbeStatusState(ProbeStatus.Recovering, 0, 1), eventAt, agentId, resultId);

        Assert.Equal(ProbeStatus.Recovering, projection.UnderlyingStatus);
        Assert.Equal(0, projection.ConsecutiveFailureCount);
        Assert.Equal(1, projection.ConsecutiveSuccessCount);
        Assert.Equal(eventAt, projection.LastFreshEventAt);
        Assert.Equal(eventAt, projection.WatermarkEventAt);
        Assert.Equal(agentId, projection.WatermarkAgentId);
        Assert.Equal(resultId, projection.WatermarkResultId);
        Assert.Equal(0, projection.StateVersion);
    }

    [Theory]
    [InlineData(0, 2, null, null)]
    [InlineData(3, 101, null, null)]
    [InlineData(3, 2, 0, null)]
    [InlineData(3, 2, null, 0d)]
    [InlineData(3, 2, null, 1.01)]
    public void PolicySnapshotRejectsOutOfRangePolicyValues(int failures, int recoveries, int? warningRtt, double? warningLoss)
    {
        Assert.Throws<DomainValidationException>(() => new ProbeStatusPolicySnapshot(
            Guid.NewGuid(), 1, failures, recoveries, warningRtt,
            warningLoss.HasValue ? (decimal)warningLoss.Value : null, Now));
    }

    [Fact]
    public void BindingAndBoundaryRequireValidImmutableIdentity()
    {
        Assert.Throws<DomainValidationException>(() => new ProbeStatusPolicyBinding(Guid.NewGuid(), 0, Guid.NewGuid(), Guid.NewGuid()));
        Assert.Throws<DomainValidationException>(() => new AgentConfigurationEffectiveBoundary(
            Guid.NewGuid(), 1, Guid.NewGuid(), AgentAcknowledgementStatus.Rejected, Now));
    }

    [Fact]
    public void DispositionRequiresCompleteResolvedLineageAndStateDrivingPolicy()
    {
        Assert.Throws<DomainValidationException>(() => new ProbeResultProcessingDisposition(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Now,
            ProbeResultProcessingDispositionKind.HistoricalOther, "policy-lineage-unresolved", Guid.NewGuid(), null, Now));
        Assert.Throws<DomainValidationException>(() => new ProbeResultProcessingDisposition(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Now,
            ProbeResultProcessingDispositionKind.StateDriving, "state-driving", null, null, Now));

        var historical = new ProbeResultProcessingDisposition(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Now,
            ProbeResultProcessingDispositionKind.HistoricalOther, "policy-lineage-unresolved", null, null, Now);
        Assert.Null(historical.ResolvedPolicySnapshotId);
    }

    [Fact]
    public void ResultStatusTransitionRequiresImmutableChangedStateAndFrozenReasonCode()
    {
        var agentId = Guid.NewGuid();
        var resultId = Guid.NewGuid();
        var probeId = Guid.NewGuid();

        Assert.Throws<DomainValidationException>(() => new ProbeResultStatusTransition(
            agentId, resultId, probeId, ProbeStatus.Up, ProbeStatus.Up, "bootstrap-success", Now, Now,
            ProbeResultProcessingDispositionKind.StateDriving));
        Assert.Throws<DomainValidationException>(() => new ProbeResultStatusTransition(
            agentId, resultId, probeId, ProbeStatus.Up, ProbeStatus.Down, "BootstrapSuccess", Now, Now,
            ProbeResultProcessingDispositionKind.StateDriving));
        Assert.Throws<DomainValidationException>(() => new ProbeResultStatusTransition(
            agentId, resultId, probeId, ProbeStatus.Up, ProbeStatus.Down, "failure-threshold-met", Now, Now,
            ProbeResultProcessingDispositionKind.HistoricalOther));

        var transition = new ProbeResultStatusTransition(
            agentId, resultId, probeId, ProbeStatus.Unknown, ProbeStatus.Up, "bootstrap-success", Now, Now,
            ProbeResultProcessingDispositionKind.StateDriving);
        Assert.Equal(resultId, transition.ResultId);
        Assert.Equal("bootstrap-success", transition.ReasonCode);
        Assert.Equal(ProbeResultProcessingDispositionKind.StateDriving, transition.ProcessingDisposition);
    }

    [Fact]
    public void St03aOpeningRecordsRequireAvailabilityDownEvidenceAndEligibleContext()
    {
        var incident = new AvailabilityIncident(Guid.NewGuid(), Guid.NewGuid(), Now);
        var lifecycleEvent = new IncidentLifecycleEvent(Guid.NewGuid(), incident.Id, incident.ProbeId,
            Guid.NewGuid(), Guid.NewGuid(), ProbeStatus.Up, Guid.NewGuid(), 1, Now);
        var context = new NotificationSuppressionContext(lifecycleEvent.EventId, incident.Id,
            lifecycleEvent.LifecycleEventKey, lifecycleEvent.PolicyVersion, Now);

        Assert.Equal(AvailabilityIncident.AvailabilityDownRuleKey, incident.RuleKey);
        Assert.Equal(AvailabilityIncidentStatus.Open, incident.Status);
        Assert.Equal(IncidentLifecycleEventType.Opened, lifecycleEvent.LifecycleEventType);
        Assert.Equal(IncidentLifecycleEvent.OpenedLifecycleEventKey, lifecycleEvent.LifecycleEventKey);
        Assert.Equal(ProbeResultProcessingDispositionKind.StateDriving, lifecycleEvent.ProcessingDisposition);
        Assert.Equal(ProbeStatus.Up, lifecycleEvent.SourceFromStatus);
        Assert.Equal(ProbeStatus.Down, lifecycleEvent.SourceToStatus);
        Assert.Equal("failure-threshold-met", lifecycleEvent.SourceReasonCode);
        Assert.Equal(NotificationSuppressionEligibility.Eligible, context.Eligibility);
        Assert.Equal("availability-down", context.ReasonCode);

        Assert.Throws<DomainValidationException>(() => new AvailabilityIncident(Guid.Empty, Guid.NewGuid(), Now));
        Assert.Throws<DomainValidationException>(() => new IncidentLifecycleEvent(Guid.NewGuid(), incident.Id, incident.ProbeId,
            Guid.Empty, Guid.NewGuid(), ProbeStatus.Up, Guid.NewGuid(), 1, Now));
        Assert.Throws<DomainValidationException>(() => new IncidentLifecycleEvent(Guid.NewGuid(), incident.Id, incident.ProbeId,
            Guid.NewGuid(), Guid.NewGuid(), ProbeStatus.Down, Guid.NewGuid(), 1, Now));
        Assert.Throws<DomainValidationException>(() => new NotificationSuppressionContext(Guid.NewGuid(), incident.Id, "", 1, Now));
    }

    [Theory]
    [InlineData(ProbeStatusTransitionReason.BootstrapSuccess, "bootstrap-success")]
    [InlineData(ProbeStatusTransitionReason.QualityDegraded, "quality-degraded")]
    [InlineData(ProbeStatusTransitionReason.QualityRestored, "quality-restored")]
    [InlineData(ProbeStatusTransitionReason.FailureThresholdMet, "failure-threshold-met")]
    [InlineData(ProbeStatusTransitionReason.RecoveryPending, "recovery-pending")]
    [InlineData(ProbeStatusTransitionReason.RecoveryThresholdMet, "recovery-threshold-met")]
    [InlineData(ProbeStatusTransitionReason.RecoveryFailed, "recovery-failed")]
    public void ResultStatusTransitionMapsEveryKernelReasonToItsFrozenCode(
        ProbeStatusTransitionReason reason, string expectedCode) =>
        Assert.Equal(expectedCode, ProbeResultStatusTransition.ReasonCodeFor(reason));
}
