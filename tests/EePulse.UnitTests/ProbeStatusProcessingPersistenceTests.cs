using EePulse.Domain.Agents;
using EePulse.Domain.Common;
using EePulse.Domain.Status;
using System.Reflection;

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
        Assert.Equal(ProbeStatus.Down, valid.VisibleStatus);
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
        Assert.Equal(ProbeStatus.Recovering, projection.VisibleStatus);
        Assert.Equal(0, projection.ConsecutiveFailureCount);
        Assert.Equal(1, projection.ConsecutiveSuccessCount);
        Assert.Equal(eventAt, projection.LastFreshEventAt);
        Assert.Equal(eventAt, projection.WatermarkEventAt);
        Assert.Equal(agentId, projection.WatermarkAgentId);
        Assert.Equal(resultId, projection.WatermarkResultId);
        Assert.Equal(0, projection.StateVersion);
    }

    [Fact]
    public void ResultFreshnessExpiryChangesOnlyTheVisibleStatusAndAResultResetsIt()
    {
        var probeId = Guid.NewGuid();
        var agentId = Guid.NewGuid();
        var resultId = Guid.NewGuid();
        var projection = new ProbeStatusProjection(probeId, ProbeStatus.Down, 2, 0, Now, Now, agentId, resultId, Guid.NewGuid());
        var before = (projection.UnderlyingStatus, projection.ConsecutiveFailureCount, projection.ConsecutiveSuccessCount,
            projection.LastFreshEventAt, projection.WatermarkEventAt, projection.WatermarkAgentId, projection.WatermarkResultId,
            projection.OpenIncidentId, projection.StateVersion);

        projection.ExpireResultFreshness();

        Assert.Equal(ProbeStatus.Unknown, projection.VisibleStatus);
        Assert.Equal(before, (projection.UnderlyingStatus, projection.ConsecutiveFailureCount, projection.ConsecutiveSuccessCount,
            projection.LastFreshEventAt, projection.WatermarkEventAt, projection.WatermarkAgentId, projection.WatermarkResultId,
            projection.OpenIncidentId, projection.StateVersion));

        projection.ApplyResult(new ProbeStatusState(ProbeStatus.Up, 0, 1), Now.AddSeconds(1), Guid.NewGuid(), Guid.NewGuid());
        Assert.Equal(ProbeStatus.Up, projection.VisibleStatus);
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
        var context = NotificationSuppressionContext.ForAvailabilityDownOpened(lifecycleEvent, Now);

        Assert.Equal(AvailabilityIncident.AvailabilityDownRuleKey, incident.RuleKey);
        Assert.Equal(AvailabilityIncidentStatus.Open, incident.Status);
        Assert.Equal(1, incident.OccurrenceCount);
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
        Assert.Empty(typeof(NotificationSuppressionContext).GetConstructors());
    }

    [Fact]
    public void St05RecoveryFailedOccurrenceRetainsTheActiveIncidentAndUsesTheDeterministicSuppressedHandoff()
    {
        var incident = new AvailabilityIncident(Guid.NewGuid(), Guid.NewGuid(), Now);
        var resultId = Guid.NewGuid();
        var lifecycleEvent = IncidentLifecycleEvent.ForRecoveryFailedOccurrence(Guid.NewGuid(), incident.Id, incident.ProbeId,
            Guid.NewGuid(), resultId, Guid.NewGuid(), 1, Now);
        var context = NotificationSuppressionContext.ForSuppressedRecoveryFailed(lifecycleEvent, Now);

        incident.RecordRecoveryFailedOccurrence();

        Assert.Equal(2, incident.OccurrenceCount);
        Assert.Equal(IncidentLifecycleEventType.Occurrence, lifecycleEvent.LifecycleEventType);
        Assert.Equal($"occurrence:{resultId:D}".ToLowerInvariant(), lifecycleEvent.LifecycleEventKey);
        Assert.Equal((ProbeStatus.Recovering, ProbeStatus.Down, "recovery-failed"),
            (lifecycleEvent.SourceFromStatus, lifecycleEvent.SourceToStatus, lifecycleEvent.SourceReasonCode));
        Assert.Equal((NotificationSuppressionEligibility.Suppressed, "recovery-failed"), (context.Eligibility, context.ReasonCode));

        incident.ResolveForConfirmedRecovery(Now);
        Assert.Throws<DomainValidationException>(() => incident.RecordRecoveryFailedOccurrence());

        var opening = new IncidentLifecycleEvent(Guid.NewGuid(), incident.Id, incident.ProbeId, Guid.NewGuid(), Guid.NewGuid(),
            ProbeStatus.Up, Guid.NewGuid(), 1, Now);
        var resolved = IncidentLifecycleEvent.ForConfirmedRecovery(Guid.NewGuid(), incident.Id, incident.ProbeId,
            Guid.NewGuid(), Guid.NewGuid(), ProbeStatus.Up, Guid.NewGuid(), 1, Now);
        Assert.Equal((NotificationSuppressionEligibility.Eligible, "availability-down"),
            (NotificationSuppressionContext.ForAvailabilityDownOpened(opening, Now).Eligibility, NotificationSuppressionContext.ForAvailabilityDownOpened(opening, Now).ReasonCode));
        Assert.Equal((NotificationSuppressionEligibility.Eligible, "confirmed-recovery"),
            (NotificationSuppressionContext.ForConfirmedRecovery(resolved, Now).Eligibility, NotificationSuppressionContext.ForConfirmedRecovery(resolved, Now).ReasonCode));
        Assert.Throws<DomainValidationException>(() => NotificationSuppressionContext.ForConfirmedRecovery(opening, Now));
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

    [Fact]
    public void St09bFreshnessExpiryCauseHasFixedSourceShapeAndDatabaseGeneratedRequestedAt()
    {
        var sourceAgentId = Guid.NewGuid();
        var sourceResultId = Guid.NewGuid();
        var cause = new ProbeFreshnessExpiryCause(Guid.NewGuid(), Guid.NewGuid(), sourceAgentId, sourceResultId,
            Now, Now, 1, Guid.NewGuid(), Guid.NewGuid(), 1, 30, 60, Now.AddSeconds(60));

        Assert.Equal(ProbeFreshnessExpiryCauseType.ResultFreshnessExpiry, cause.CauseType);
        Assert.Equal(ProbeResultProcessingDispositionKind.StateDriving, cause.SourceDisposition);
        Assert.Equal(default, cause.RequestedAt);
        Assert.DoesNotContain(typeof(ProbeFreshnessExpiryCause).GetConstructors()
            .SelectMany(constructor => constructor.GetParameters()), parameter => parameter.Name == "requestedAt");
        var requestedAtSetter = typeof(ProbeFreshnessExpiryCause).GetProperty(nameof(ProbeFreshnessExpiryCause.RequestedAt))!.GetSetMethod(nonPublic: true);
        Assert.NotNull(requestedAtSetter);
        Assert.False(requestedAtSetter!.IsPublic);

        Assert.Throws<DomainValidationException>(() => new ProbeFreshnessExpiryCause(Guid.NewGuid(), Guid.NewGuid(), sourceAgentId, sourceResultId,
            Now, Now.AddTicks(10), 1, Guid.NewGuid(), Guid.NewGuid(), 1, 30, 60, Now.AddSeconds(60)));
        Assert.Throws<DomainValidationException>(() => new ProbeFreshnessExpiryCause(Guid.NewGuid(), Guid.NewGuid(), sourceAgentId, sourceResultId,
            Now, Now, 0, Guid.NewGuid(), Guid.NewGuid(), 1, 30, 60, Now.AddSeconds(60)));
        Assert.Throws<DomainValidationException>(() => new ProbeFreshnessExpiryCause(Guid.NewGuid(), Guid.NewGuid(), sourceAgentId, sourceResultId,
            Now, Now, 1, Guid.NewGuid(), Guid.NewGuid(), 1, 0, 60, Now.AddSeconds(60)));
        Assert.Throws<DomainValidationException>(() => new ProbeFreshnessExpiryCause(Guid.NewGuid(), Guid.NewGuid(), sourceAgentId, sourceResultId,
            Now, Now, 1, Guid.NewGuid(), Guid.NewGuid(), 1, 30, 60, Now.AddTicks(-1)));
    }

    [Fact]
    public void St10FreshnessExpiryDispositionAndTransitionHaveClosedConstructionShapes()
    {
        var causeId = Guid.NewGuid();
        var probeId = Guid.NewGuid();
        var policySnapshotId = Guid.NewGuid();
        var applied = ProbeFreshnessExpiryCauseDisposition.Applied(causeId, probeId, policySnapshotId, 1, Now);
        var projectionMissing = ProbeFreshnessExpiryCauseDisposition.NoOp(Guid.NewGuid(), probeId, policySnapshotId, 1,
            ProbeFreshnessExpiryCauseDisposition.ProjectionMissingReasonCode, Now);
        var superseded = ProbeFreshnessExpiryCauseDisposition.NoOp(Guid.NewGuid(), probeId, policySnapshotId, 1,
            ProbeFreshnessExpiryCauseDisposition.FreshnessSourceSupersededReasonCode, Now);
        var alreadyUnknown = ProbeFreshnessExpiryCauseDisposition.NoOp(Guid.NewGuid(), probeId, policySnapshotId, 1,
            ProbeFreshnessExpiryCauseDisposition.VisibleAlreadyUnknownReasonCode, Now);

        Assert.Equal((ProbeFreshnessExpiryCauseDispositionOutcome.Applied, "result-freshness-expired", Now),
            (applied.Outcome, applied.ReasonCode, applied.AppliedAt));
        Assert.All(new[] { projectionMissing, superseded, alreadyUnknown }, disposition =>
        {
            Assert.Equal(ProbeFreshnessExpiryCauseDispositionOutcome.NoOp, disposition.Outcome);
            Assert.Null(disposition.AppliedAt);
        });
        Assert.Empty(typeof(ProbeFreshnessExpiryCauseDisposition).GetConstructors());
        Assert.Throws<DomainValidationException>(() => ProbeFreshnessExpiryCauseDisposition.NoOp(Guid.NewGuid(), probeId, policySnapshotId, 1,
            "unrecognized", Now));
        Assert.Throws<DomainValidationException>(() => ProbeFreshnessExpiryCauseDisposition.Applied(Guid.NewGuid(), probeId, policySnapshotId, 1,
            Now.ToOffset(TimeSpan.FromHours(1))));

        var transition = new ProbeFreshnessExpiryCauseTransition(causeId, probeId, policySnapshotId, 1, ProbeStatus.Down, Now);
        Assert.Equal(ProbeFreshnessExpiryCauseDispositionOutcome.Applied, transition.DispositionOutcome);
        Assert.Equal(ProbeStatus.Unknown, transition.ToVisibleStatus);
        Assert.Equal("result-freshness-expired", transition.ReasonCode);
        Assert.Throws<DomainValidationException>(() => new ProbeFreshnessExpiryCauseTransition(Guid.NewGuid(), probeId, policySnapshotId, 1, ProbeStatus.Unknown, Now));
        Assert.Throws<DomainValidationException>(() => new ProbeFreshnessExpiryCauseTransition(Guid.NewGuid(), probeId, policySnapshotId, 1, ProbeStatus.Up,
            Now.ToOffset(TimeSpan.FromHours(1))));
    }

    [Fact]
    public void St10bHeartbeatExpiryTypesFreezeAuthoritySourceAndClosedOutcomeShapes()
    {
        var causeId = Guid.NewGuid();
        var probeId = Guid.NewGuid();
        var authorityAgentId = Guid.NewGuid();
        var sourceResultId = Guid.NewGuid();
        var policySnapshotId = Guid.NewGuid();
        var sourceAgentGroupId = Guid.NewGuid();
        var cause = new ProbeHeartbeatExpiryCause(causeId, probeId, authorityAgentId, sourceResultId, Now,
            Now, 15, 1, sourceAgentGroupId, policySnapshotId, 1);
        var maximumIntervalCause = new ProbeHeartbeatExpiryCause(Guid.NewGuid(), probeId, authorityAgentId, Guid.NewGuid(), Now,
            Now, 30, 1, sourceAgentGroupId, policySnapshotId, 1);

        Assert.Equal(ProbeHeartbeatExpiryCauseType.AgentHeartbeatExpiry, cause.CauseType);
        Assert.Equal(ProbeResultProcessingDispositionKind.StateDriving, cause.SourceDisposition);
        Assert.Equal(Now.AddSeconds(60), cause.DueAt);
        Assert.Equal(Now.AddSeconds(90), maximumIntervalCause.DueAt);
        Assert.Equal(default, cause.RequestedAt);
        Assert.DoesNotContain(typeof(ProbeHeartbeatExpiryCause).GetConstructors().Single().GetParameters(), x => x.Name is "dueAt" or "requestedAt");
        Assert.False(typeof(ProbeHeartbeatExpiryCause).GetProperty(nameof(ProbeHeartbeatExpiryCause.RequestedAt))!.GetSetMethod(true)!.IsPublic);
        Assert.Throws<DomainValidationException>(() => new ProbeHeartbeatExpiryCause(Guid.Empty, probeId, authorityAgentId, sourceResultId, Now, Now, 15, 1, sourceAgentGroupId, policySnapshotId, 1));
        Assert.Throws<DomainValidationException>(() => new ProbeHeartbeatExpiryCause(causeId, Guid.Empty, authorityAgentId, sourceResultId, Now, Now, 15, 1, sourceAgentGroupId, policySnapshotId, 1));
        Assert.Throws<DomainValidationException>(() => new ProbeHeartbeatExpiryCause(causeId, probeId, Guid.Empty, sourceResultId, Now, Now, 15, 1, sourceAgentGroupId, policySnapshotId, 1));
        Assert.Throws<DomainValidationException>(() => new ProbeHeartbeatExpiryCause(causeId, probeId, authorityAgentId, Guid.Empty, Now, Now, 15, 1, sourceAgentGroupId, policySnapshotId, 1));
        Assert.Throws<DomainValidationException>(() => new ProbeHeartbeatExpiryCause(causeId, probeId, authorityAgentId, sourceResultId, Now, Now, 15, 1, Guid.Empty, policySnapshotId, 1));
        Assert.Throws<DomainValidationException>(() => new ProbeHeartbeatExpiryCause(causeId, probeId, authorityAgentId, sourceResultId, Now, Now, 15, 1, sourceAgentGroupId, Guid.Empty, 1));
        Assert.Throws<DomainValidationException>(() => new ProbeHeartbeatExpiryCause(causeId, probeId, authorityAgentId, sourceResultId, Now.ToOffset(TimeSpan.FromHours(7)), Now, 15, 1, sourceAgentGroupId, policySnapshotId, 1));
        Assert.Throws<DomainValidationException>(() => new ProbeHeartbeatExpiryCause(causeId, probeId, authorityAgentId, sourceResultId, Now, Now.ToOffset(TimeSpan.FromHours(7)), 15, 1, sourceAgentGroupId, policySnapshotId, 1));
        Assert.Throws<DomainValidationException>(() => new ProbeHeartbeatExpiryCause(causeId, probeId, authorityAgentId, sourceResultId, Now, Now, 15, 0, sourceAgentGroupId, policySnapshotId, 1));
        Assert.Throws<DomainValidationException>(() => new ProbeHeartbeatExpiryCause(causeId, probeId, authorityAgentId, sourceResultId, Now, Now, 15, 1, sourceAgentGroupId, policySnapshotId, 0));
        Assert.Throws<DomainValidationException>(() => new ProbeHeartbeatExpiryCause(causeId, probeId, authorityAgentId, sourceResultId, Now, Now, 14, 1, sourceAgentGroupId, policySnapshotId, 1));
        Assert.Throws<DomainValidationException>(() => new ProbeHeartbeatExpiryCause(causeId, probeId, authorityAgentId, sourceResultId, Now, Now, 31, 1, sourceAgentGroupId, policySnapshotId, 1));
        Assert.Throws<DomainValidationException>(() => new ProbeHeartbeatExpiryCause(causeId, probeId, authorityAgentId, sourceResultId, Now, DateTimeOffset.MaxValue.AddSeconds(-59), 15, 1, sourceAgentGroupId, policySnapshotId, 1));

        var applied = ProbeHeartbeatExpiryCauseDisposition.Applied(causeId, probeId, policySnapshotId, 1, Now);
        var noOps = new[]
        {
            ProbeHeartbeatExpiryCauseDisposition.ProjectionMissing(Guid.NewGuid(), probeId, policySnapshotId, 1, Now),
            ProbeHeartbeatExpiryCauseDisposition.AuthorityWatermarkSuperseded(Guid.NewGuid(), probeId, policySnapshotId, 1, Now),
            ProbeHeartbeatExpiryCauseDisposition.AuthorityHeartbeatAdvanced(Guid.NewGuid(), probeId, policySnapshotId, 1, Now),
            ProbeHeartbeatExpiryCauseDisposition.VisibleAlreadyUnknown(Guid.NewGuid(), probeId, policySnapshotId, 1, Now),
        };
        Assert.Equal((ProbeHeartbeatExpiryCauseDispositionOutcome.Applied, "agent-heartbeat-expired", (DateTimeOffset?)Now), (applied.Outcome, applied.ReasonCode, applied.AppliedAt));
        Assert.All(noOps, x => { Assert.Equal(ProbeHeartbeatExpiryCauseDispositionOutcome.NoOp, x.Outcome); Assert.Null(x.AppliedAt); });
        Assert.Empty(typeof(ProbeHeartbeatExpiryCauseDisposition).GetConstructors());
        var factories = typeof(ProbeHeartbeatExpiryCauseDisposition).GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(x => x.ReturnType == typeof(ProbeHeartbeatExpiryCauseDisposition)).Select(x => x.Name).Order().ToArray();
        Assert.Equal(["Applied", "AuthorityHeartbeatAdvanced", "AuthorityWatermarkSuperseded", "ProjectionMissing", "VisibleAlreadyUnknown"], factories);
        Assert.DoesNotContain(typeof(ProbeHeartbeatExpiryCauseDisposition).GetMethods(), x => x.Name == "NoOp" && x.IsPublic);
        foreach (var factory in typeof(ProbeHeartbeatExpiryCauseDisposition).GetMethods(BindingFlags.Public | BindingFlags.Static).Where(x => x.ReturnType == typeof(ProbeHeartbeatExpiryCauseDisposition)))
        {
            var invalidIdArguments = new object?[] { Guid.Empty, probeId, policySnapshotId, 1, Now };
            AssertFactoryValidation(factory, invalidIdArguments);
            AssertFactoryValidation(factory, [causeId, Guid.Empty, policySnapshotId, 1, Now]);
            AssertFactoryValidation(factory, [causeId, probeId, Guid.Empty, 1, Now]);
            AssertFactoryValidation(factory, [causeId, probeId, policySnapshotId, 0, Now]);
            AssertFactoryValidation(factory, [causeId, probeId, policySnapshotId, 1, Now.ToOffset(TimeSpan.FromHours(7))]);
        }
        var privateDispositionConstructor = typeof(ProbeHeartbeatExpiryCauseDisposition).GetConstructors(BindingFlags.NonPublic | BindingFlags.Instance).Single(x => x.GetParameters().Length == 8);
        var invalidShape = Assert.Throws<TargetInvocationException>(() => privateDispositionConstructor.Invoke([causeId, probeId, policySnapshotId, 1, ProbeHeartbeatExpiryCauseDispositionOutcome.Applied, "wrong", Now, (DateTimeOffset?)null]));
        Assert.IsType<DomainValidationException>(invalidShape.InnerException);
        var invalidNoOpShape = Assert.Throws<TargetInvocationException>(() => privateDispositionConstructor.Invoke([causeId, probeId, policySnapshotId, 1, ProbeHeartbeatExpiryCauseDispositionOutcome.NoOp, ProbeHeartbeatExpiryCauseDisposition.ProjectionMissingReasonCode, Now, (DateTimeOffset?)Now]));
        Assert.IsType<DomainValidationException>(invalidNoOpShape.InnerException);
        var invalidOutcomeShape = Assert.Throws<TargetInvocationException>(() => privateDispositionConstructor.Invoke([causeId, probeId, policySnapshotId, 1, (ProbeHeartbeatExpiryCauseDispositionOutcome)999, "wrong", Now, (DateTimeOffset?)null]));
        Assert.IsType<DomainValidationException>(invalidOutcomeShape.InnerException);

        var transition = new ProbeHeartbeatExpiryCauseTransition(causeId, probeId, policySnapshotId, 1, ProbeStatus.Recovering, Now);
        Assert.Equal((ProbeHeartbeatExpiryCauseDispositionOutcome.Applied, ProbeStatus.Recovering, ProbeStatus.Unknown, "agent-heartbeat-expired"), (transition.DispositionOutcome, transition.FromVisibleStatus, transition.ToVisibleStatus, transition.ReasonCode));
        Assert.Throws<DomainValidationException>(() => new ProbeHeartbeatExpiryCauseTransition(causeId, probeId, policySnapshotId, 1, ProbeStatus.Unknown, Now));
        // Maintenance is visual-only and not a ProbeStatus kernel enum member; its persisted enum value is invalid here too.
        Assert.Throws<DomainValidationException>(() => new ProbeHeartbeatExpiryCauseTransition(causeId, probeId, policySnapshotId, 1, (ProbeStatus)5, Now));
        Assert.Throws<DomainValidationException>(() => new ProbeHeartbeatExpiryCauseTransition(causeId, probeId, policySnapshotId, 1, (ProbeStatus)999, Now));
        Assert.Throws<DomainValidationException>(() => new ProbeHeartbeatExpiryCauseTransition(Guid.Empty, probeId, policySnapshotId, 1, ProbeStatus.Up, Now));
        Assert.Throws<DomainValidationException>(() => new ProbeHeartbeatExpiryCauseTransition(causeId, Guid.Empty, policySnapshotId, 1, ProbeStatus.Up, Now));
        Assert.Throws<DomainValidationException>(() => new ProbeHeartbeatExpiryCauseTransition(causeId, probeId, Guid.Empty, 1, ProbeStatus.Up, Now));
        Assert.Throws<DomainValidationException>(() => new ProbeHeartbeatExpiryCauseTransition(causeId, probeId, policySnapshotId, 0, ProbeStatus.Up, Now));
        Assert.Throws<DomainValidationException>(() => new ProbeHeartbeatExpiryCauseTransition(causeId, probeId, policySnapshotId, 1, ProbeStatus.Up, Now.ToOffset(TimeSpan.FromHours(7))));
        var transitionConstructor = typeof(ProbeHeartbeatExpiryCauseTransition).GetConstructors().Single();
        var transitionParameters = transitionConstructor.GetParameters();
        Assert.All(transitionParameters, parameter => Assert.NotNull(parameter.Name));
        var transitionParameterNames = transitionParameters.Select(parameter => parameter.Name).OfType<string>().ToArray();
        Assert.Equal(transitionParameters.Length, transitionParameterNames.Length);
        string[] expectedTransitionParameterNames = ["causeId", "probeId", "policySnapshotId", "policyVersion", "fromVisibleStatus", "appliedAt"];
        Assert.True(expectedTransitionParameterNames.SequenceEqual(transitionParameterNames, StringComparer.Ordinal));
        Assert.DoesNotContain(transitionConstructor.GetParameters(), x => x.Name is "dispositionOutcome" or "toVisibleStatus" or "reasonCode");

        static void AssertFactoryValidation(MethodInfo factory, object?[] arguments)
        {
            var exception = Assert.Throws<TargetInvocationException>(() => factory.Invoke(null, arguments));
            Assert.IsType<DomainValidationException>(exception.InnerException);
        }
    }
}
