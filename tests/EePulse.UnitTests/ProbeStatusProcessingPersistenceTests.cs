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
}
