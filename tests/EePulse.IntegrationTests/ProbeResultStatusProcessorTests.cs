using EePulse.Application.Time;
using EePulse.Domain.Agents;
using EePulse.Domain.Inventory;
using EePulse.Domain.Status;
using EePulse.Infrastructure.Persistence;
using EePulse.Infrastructure.Persistence.ProbeProcessing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;

namespace EePulse.IntegrationTests;

public sealed class ProbeResultStatusProcessorTests
{
    public static TheoryData<bool, decimal, ProbeStatus> ConfirmedRecoveryVariants
    {
        get
        {
            var variants = new TheoryData<bool, decimal, ProbeStatus>();
            variants.Add(true, 1m, ProbeStatus.Up);
            variants.Add(false, 500m, ProbeStatus.Degraded);
            return variants;
        }
    }

    [Theory]
    [MemberData(nameof(ConfirmedRecoveryVariants))]
    public async Task St04ConfirmedRecoveryResolvesOpenOrAcknowledgedIncidentWithTheExpectedQuality(
        bool acknowledgeIncident,
        decimal averageRtt,
        ProbeStatus expectedStatus)
    {
        await using var fixture = await CreateFixtureAsync();
        await AddLedgerAsync(fixture, fixture.Now, fixture.Now, 3, 0m);
        await AddLedgerAsync(fixture, fixture.Now.AddSeconds(1), fixture.Now.AddSeconds(1), 0, 1m);
        await AddLedgerAsync(fixture, fixture.Now.AddSeconds(2), fixture.Now.AddSeconds(2), 0, 1m);
        await using (var opening = new EePulseDbContext(fixture.Options))
        {
            var processor = new ProbeResultStatusProcessor(opening, new FixedClock(fixture.Now));
            for (var index = 0; index < 3; index++) await processor.ProcessNextAsync(fixture.ProbeId, TestContext.Current.CancellationToken);
        }
        if (acknowledgeIncident)
        {
            await using var acknowledge = new EePulseDbContext(fixture.Options);
            await acknowledge.Database.ExecuteSqlInterpolatedAsync($"UPDATE availability_incidents SET status = {"Acknowledged"}, acknowledged_at = {fixture.Now.AddSeconds(2)}, acknowledged_by = {"operator"}, acknowledgement_comment = {"investigating"} WHERE probe_id = {fixture.ProbeId} AND status = {"Open"}", TestContext.Current.CancellationToken);
        }

        await AddLedgerAsync(fixture, fixture.Now.AddSeconds(3), fixture.Now.AddSeconds(3), 3, 0m, averageRtt: averageRtt);
        var resolvedResultId = await AddLedgerAsync(fixture, fixture.Now.AddSeconds(4), fixture.Now.AddSeconds(4), 3, 0m, averageRtt: averageRtt);
        await using (var recovery = new EePulseDbContext(fixture.Options))
        {
            var processor = new ProbeResultStatusProcessor(recovery, new FixedClock(fixture.Now));
            await processor.ProcessNextAsync(fixture.ProbeId, TestContext.Current.CancellationToken);
            await processor.ProcessNextAsync(fixture.ProbeId, TestContext.Current.CancellationToken);
        }

        await using var verify = new EePulseDbContext(fixture.Options);
        var transition = await verify.ProbeResultStatusTransitions.SingleAsync(row => row.AgentId == fixture.AgentId && row.ResultId == resolvedResultId, TestContext.Current.CancellationToken);
        var disposition = await verify.ProbeResultProcessingDispositions.SingleAsync(row => row.AgentId == fixture.AgentId && row.ResultId == resolvedResultId, TestContext.Current.CancellationToken);
        var incident = await verify.AvailabilityIncidents.SingleAsync(TestContext.Current.CancellationToken);
        var lifecycleEvent = await verify.IncidentLifecycleEvents.SingleAsync(row => row.LifecycleEventType == IncidentLifecycleEventType.Resolved, TestContext.Current.CancellationToken);
        var context = await verify.NotificationSuppressionContexts.SingleAsync(row => row.EventId == lifecycleEvent.EventId, TestContext.Current.CancellationToken);
        var projection = await verify.ProbeStatusProjections.SingleAsync(TestContext.Current.CancellationToken);

        Assert.Equal((ProbeStatus.Recovering, expectedStatus, "recovery-threshold-met"), (transition.FromStatus, transition.ToStatus, transition.ReasonCode));
        Assert.Equal(ProbeResultProcessingDispositionKind.StateDriving, disposition.Disposition);
        Assert.Equal(AvailabilityIncidentStatus.Resolved, incident.Status);
        Assert.Equal(fixture.Now.AddSeconds(4), incident.ResolvedAt);
        Assert.Equal(AvailabilityIncident.SystemPolicyActor, incident.ResolvedBy);
        Assert.Equal(AvailabilityIncident.ConfirmedRecoveryReason, incident.ResolutionNote);
        Assert.Null(projection.OpenIncidentId);
        Assert.Equal((incident.Id, fixture.ProbeId, fixture.AgentId, resolvedResultId, ProbeStatus.Recovering, expectedStatus, "recovery-threshold-met"),
            (lifecycleEvent.IncidentId, lifecycleEvent.ProbeId, lifecycleEvent.SourceAgentId, lifecycleEvent.SourceResultId, lifecycleEvent.SourceFromStatus, lifecycleEvent.SourceToStatus, lifecycleEvent.SourceReasonCode));
        Assert.Equal((disposition.ResolvedPolicySnapshotId, disposition.ResolvedPolicyVersion), (lifecycleEvent.PolicySnapshotId, lifecycleEvent.PolicyVersion));
        Assert.Equal((IncidentLifecycleEventType.Resolved, IncidentLifecycleEvent.ResolvedLifecycleEventKey), (lifecycleEvent.LifecycleEventType, lifecycleEvent.LifecycleEventKey));
        Assert.Equal((NotificationSuppressionEligibility.Eligible, AvailabilityIncident.ConfirmedRecoveryReason), (context.Eligibility, context.ReasonCode));
        Assert.Equal(lifecycleEvent.EventId, context.EventId);
    }

    [Fact]
    public async Task St04ConfirmedRecoveryAtomicallyResolvesTheActiveAvailabilityIncident()
    {
        await using var fixture = await CreateFixtureAsync();
        await AddLedgerAsync(fixture, fixture.Now, fixture.Now, 3, 0m);
        await AddLedgerAsync(fixture, fixture.Now.AddSeconds(1), fixture.Now.AddSeconds(1), 0, 1m);
        await AddLedgerAsync(fixture, fixture.Now.AddSeconds(2), fixture.Now.AddSeconds(2), 0, 1m);
        var recoveringResultId = await AddLedgerAsync(fixture, fixture.Now.AddSeconds(3), fixture.Now.AddSeconds(3), 3, 0m);
        var resolvedResultId = await AddLedgerAsync(fixture, fixture.Now.AddSeconds(4), fixture.Now.AddSeconds(4), 3, 0m);

        await using (var processing = new EePulseDbContext(fixture.Options))
        {
            var processor = new ProbeResultStatusProcessor(processing, new FixedClock(fixture.Now));
            for (var index = 0; index < 4; index++) await processor.ProcessNextAsync(fixture.ProbeId, TestContext.Current.CancellationToken);
        }

        await using (var recovering = new EePulseDbContext(fixture.Options))
        {
            var recoveringProjection = await recovering.ProbeStatusProjections.SingleAsync(TestContext.Current.CancellationToken);
            Assert.Equal(ProbeStatus.Recovering, recoveringProjection.UnderlyingStatus);
            Assert.NotNull(recoveringProjection.OpenIncidentId);
            Assert.Single(await recovering.AvailabilityIncidents.Where(incident => incident.Status == AvailabilityIncidentStatus.Open).ToListAsync(TestContext.Current.CancellationToken));
            Assert.Single(await recovering.IncidentLifecycleEvents.ToListAsync(TestContext.Current.CancellationToken));
        }

        await using (var processing = new EePulseDbContext(fixture.Options))
            Assert.Equal(resolvedResultId, (await new ProbeResultStatusProcessor(processing, new FixedClock(fixture.Now)).ProcessNextAsync(fixture.ProbeId, TestContext.Current.CancellationToken)).ResultId);

        await using var verify = new EePulseDbContext(fixture.Options);
        var transition = await verify.ProbeResultStatusTransitions.SingleAsync(row => row.AgentId == fixture.AgentId && row.ResultId == resolvedResultId, TestContext.Current.CancellationToken);
        var disposition = await verify.ProbeResultProcessingDispositions.SingleAsync(row => row.AgentId == fixture.AgentId && row.ResultId == resolvedResultId, TestContext.Current.CancellationToken);
        var incident = await verify.AvailabilityIncidents.SingleAsync(TestContext.Current.CancellationToken);
        var lifecycleEvent = await verify.IncidentLifecycleEvents.SingleAsync(row => row.LifecycleEventType == IncidentLifecycleEventType.Resolved, TestContext.Current.CancellationToken);
        var context = await verify.NotificationSuppressionContexts.SingleAsync(row => row.EventId == lifecycleEvent.EventId, TestContext.Current.CancellationToken);
        var projection = await verify.ProbeStatusProjections.SingleAsync(TestContext.Current.CancellationToken);

        Assert.Equal((ProbeStatus.Recovering, ProbeStatus.Up, "recovery-threshold-met"), (transition.FromStatus, transition.ToStatus, transition.ReasonCode));
        Assert.Equal(ProbeResultProcessingDispositionKind.StateDriving, disposition.Disposition);
        Assert.Equal(AvailabilityIncidentStatus.Resolved, incident.Status);
        Assert.Equal((fixture.Now.AddSeconds(4), AvailabilityIncident.SystemPolicyActor, AvailabilityIncident.ConfirmedRecoveryReason), (incident.ResolvedAt, incident.ResolvedBy, incident.ResolutionNote));
        Assert.Null(projection.OpenIncidentId);
        Assert.Equal((incident.Id, fixture.AgentId, resolvedResultId, ProbeStatus.Recovering, ProbeStatus.Up, "recovery-threshold-met"),
            (lifecycleEvent.IncidentId, lifecycleEvent.SourceAgentId, lifecycleEvent.SourceResultId, lifecycleEvent.SourceFromStatus, lifecycleEvent.SourceToStatus, lifecycleEvent.SourceReasonCode));
        Assert.Equal((disposition.ResolvedPolicySnapshotId, disposition.ResolvedPolicyVersion), (lifecycleEvent.PolicySnapshotId, lifecycleEvent.PolicyVersion));
        Assert.Equal(IncidentLifecycleEventType.Resolved, lifecycleEvent.LifecycleEventType);
        Assert.Equal(IncidentLifecycleEvent.ResolvedLifecycleEventKey, lifecycleEvent.LifecycleEventKey);
        Assert.Equal((NotificationSuppressionEligibility.Eligible, AvailabilityIncident.ConfirmedRecoveryReason), (context.Eligibility, context.ReasonCode));
        Assert.Equal(recoveringResultId, (await verify.ProbeResultStatusTransitions.SingleAsync(row => row.AgentId == fixture.AgentId && row.ResultId == recoveringResultId, TestContext.Current.CancellationToken)).ResultId);
    }

    [Fact]
    public async Task St04ConfirmedRecoveryWithoutAnActiveIncidentCommitsOnlyNormalResultProcessing()
    {
        await using var fixture = await CreateFixtureAsync();
        await using (var seed = new EePulseDbContext(fixture.Options))
        {
            seed.Add(new ProbeStatusProjection(fixture.ProbeId, ProbeStatus.Down, 2, 0, fixture.Now,
                fixture.Now, fixture.AgentId, Guid.NewGuid()));
            await seed.SaveChangesAsync(TestContext.Current.CancellationToken);
        }
        await AddLedgerAsync(fixture, fixture.Now.AddSeconds(1), fixture.Now.AddSeconds(1), 3, 0m);
        var resolvedResultId = await AddLedgerAsync(fixture, fixture.Now.AddSeconds(2), fixture.Now.AddSeconds(2), 3, 0m);

        await using (var processing = new EePulseDbContext(fixture.Options))
        {
            var processor = new ProbeResultStatusProcessor(processing, new FixedClock(fixture.Now));
            await processor.ProcessNextAsync(fixture.ProbeId, TestContext.Current.CancellationToken);
            await processor.ProcessNextAsync(fixture.ProbeId, TestContext.Current.CancellationToken);
        }

        await using var verify = new EePulseDbContext(fixture.Options);
        var projection = await verify.ProbeStatusProjections.SingleAsync(TestContext.Current.CancellationToken);
        Assert.Equal(ProbeStatus.Up, projection.UnderlyingStatus);
        Assert.Null(projection.OpenIncidentId);
        Assert.NotNull(await verify.ProbeResultProcessingDispositions.SingleOrDefaultAsync(row => row.AgentId == fixture.AgentId && row.ResultId == resolvedResultId, TestContext.Current.CancellationToken));
        Assert.NotNull(await verify.ProbeResultStatusTransitions.SingleOrDefaultAsync(row => row.AgentId == fixture.AgentId && row.ResultId == resolvedResultId && row.ReasonCode == "recovery-threshold-met", TestContext.Current.CancellationToken));
        Assert.Empty(await verify.AvailabilityIncidents.ToListAsync(TestContext.Current.CancellationToken));
        Assert.Empty(await verify.IncidentLifecycleEvents.ToListAsync(TestContext.Current.CancellationToken));
        Assert.Empty(await verify.NotificationSuppressionContexts.ToListAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task St04ConfirmedRecoveryWithAnInconsistentPointerRollsBackEveryRecoveryMutation()
    {
        await using var fixture = await CreateFixtureAsync();
        await AddLedgerAsync(fixture, fixture.Now, fixture.Now, 3, 0m);
        await AddLedgerAsync(fixture, fixture.Now.AddSeconds(1), fixture.Now.AddSeconds(1), 0, 1m);
        await AddLedgerAsync(fixture, fixture.Now.AddSeconds(2), fixture.Now.AddSeconds(2), 0, 1m);
        await AddLedgerAsync(fixture, fixture.Now.AddSeconds(3), fixture.Now.AddSeconds(3), 3, 0m);
        await using (var processing = new EePulseDbContext(fixture.Options))
        {
            var processor = new ProbeResultStatusProcessor(processing, new FixedClock(fixture.Now));
            for (var index = 0; index < 4; index++) await processor.ProcessNextAsync(fixture.ProbeId, TestContext.Current.CancellationToken);
        }

        var mismatchedIncidentId = Guid.NewGuid();
        await using (var corrupt = new EePulseDbContext(fixture.Options))
        {
            await corrupt.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO availability_incidents (id, probe_id, rule_key, status, opened_at, resolved_at, resolved_by, resolution_note) VALUES ({mismatchedIncidentId}, {fixture.ProbeId}, {"availability-down"}, {"Resolved"}, {fixture.Now}, {fixture.Now}, {"system-policy"}, {"confirmed-recovery"})", TestContext.Current.CancellationToken);
            var projection = await corrupt.ProbeStatusProjections.SingleAsync(row => row.ProbeId == fixture.ProbeId, TestContext.Current.CancellationToken);
            corrupt.Entry(projection).Property(nameof(ProbeStatusProjection.OpenIncidentId)).CurrentValue = mismatchedIncidentId;
            await corrupt.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var recoveryResultId = await AddLedgerAsync(fixture, fixture.Now.AddSeconds(4), fixture.Now.AddSeconds(4), 3, 0m);
        ProbeStatusProjection baselineProjection;
        (Guid AgentId, Guid ResultId)[] baselineDispositions;
        (Guid AgentId, Guid ResultId, ProbeStatus FromStatus, ProbeStatus ToStatus, string ReasonCode)[] baselineTransitions;
        (Guid Id, AvailabilityIncidentStatus Status, DateTimeOffset? ResolvedAt, string? ResolvedBy, string? ResolutionNote)[] baselineIncidents;
        Guid[] baselineEventIds;
        Guid[] baselineContextIds;
        await using (var baseline = new EePulseDbContext(fixture.Options))
        {
            baselineProjection = await baseline.ProbeStatusProjections.AsNoTracking().SingleAsync(row => row.ProbeId == fixture.ProbeId, TestContext.Current.CancellationToken);
            baselineDispositions = (await baseline.ProbeResultProcessingDispositions.AsNoTracking().OrderBy(row => row.AgentId).ThenBy(row => row.ResultId).ToListAsync(TestContext.Current.CancellationToken)).Select(row => (row.AgentId, row.ResultId)).ToArray();
            baselineTransitions = (await baseline.ProbeResultStatusTransitions.AsNoTracking().OrderBy(row => row.AgentId).ThenBy(row => row.ResultId).ToListAsync(TestContext.Current.CancellationToken)).Select(row => (row.AgentId, row.ResultId, row.FromStatus, row.ToStatus, row.ReasonCode)).ToArray();
            baselineIncidents = (await baseline.AvailabilityIncidents.AsNoTracking().OrderBy(row => row.Id).ToListAsync(TestContext.Current.CancellationToken)).Select(row => (row.Id, row.Status, row.ResolvedAt, row.ResolvedBy, row.ResolutionNote)).ToArray();
            baselineEventIds = await baseline.IncidentLifecycleEvents.AsNoTracking().OrderBy(row => row.EventId).Select(row => row.EventId).ToArrayAsync(TestContext.Current.CancellationToken);
            baselineContextIds = await baseline.NotificationSuppressionContexts.AsNoTracking().OrderBy(row => row.EventId).Select(row => row.EventId).ToArrayAsync(TestContext.Current.CancellationToken);
        }

        await using (var processing = new EePulseDbContext(fixture.Options))
            await Assert.ThrowsAsync<InvalidOperationException>(() => new ProbeResultStatusProcessor(processing, new FixedClock(fixture.Now)).ProcessNextAsync(fixture.ProbeId, TestContext.Current.CancellationToken));

        await using var verify = new EePulseDbContext(fixture.Options);
        var projectionAfterFailure = await verify.ProbeStatusProjections.SingleAsync(row => row.ProbeId == fixture.ProbeId, TestContext.Current.CancellationToken);
        Assert.Equal(baselineProjection.UnderlyingStatus, projectionAfterFailure.UnderlyingStatus);
        Assert.Equal(baselineProjection.ConsecutiveFailureCount, projectionAfterFailure.ConsecutiveFailureCount);
        Assert.Equal(baselineProjection.ConsecutiveSuccessCount, projectionAfterFailure.ConsecutiveSuccessCount);
        Assert.Equal(baselineProjection.LastFreshEventAt, projectionAfterFailure.LastFreshEventAt);
        Assert.Equal(baselineProjection.WatermarkEventAt, projectionAfterFailure.WatermarkEventAt);
        Assert.Equal(baselineProjection.WatermarkAgentId, projectionAfterFailure.WatermarkAgentId);
        Assert.Equal(baselineProjection.WatermarkResultId, projectionAfterFailure.WatermarkResultId);
        Assert.Equal(baselineProjection.StateVersion, projectionAfterFailure.StateVersion);
        Assert.Equal(baselineProjection.OpenIncidentId, projectionAfterFailure.OpenIncidentId);
        Assert.Equal(baselineDispositions, (await verify.ProbeResultProcessingDispositions.OrderBy(row => row.AgentId).ThenBy(row => row.ResultId).ToListAsync(TestContext.Current.CancellationToken)).Select(row => (row.AgentId, row.ResultId)).ToArray());
        Assert.Equal(baselineTransitions, (await verify.ProbeResultStatusTransitions.OrderBy(row => row.AgentId).ThenBy(row => row.ResultId).ToListAsync(TestContext.Current.CancellationToken)).Select(row => (row.AgentId, row.ResultId, row.FromStatus, row.ToStatus, row.ReasonCode)).ToArray());
        Assert.Equal(baselineIncidents, (await verify.AvailabilityIncidents.OrderBy(row => row.Id).ToListAsync(TestContext.Current.CancellationToken)).Select(row => (row.Id, row.Status, row.ResolvedAt, row.ResolvedBy, row.ResolutionNote)).ToArray());
        Assert.Equal(baselineEventIds, await verify.IncidentLifecycleEvents.OrderBy(row => row.EventId).Select(row => row.EventId).ToArrayAsync(TestContext.Current.CancellationToken));
        Assert.Equal(baselineContextIds, await verify.NotificationSuppressionContexts.OrderBy(row => row.EventId).Select(row => row.EventId).ToArrayAsync(TestContext.Current.CancellationToken));
        Assert.Null(await verify.ProbeResultProcessingDispositions.SingleOrDefaultAsync(row => row.AgentId == fixture.AgentId && row.ResultId == recoveryResultId, TestContext.Current.CancellationToken));
        Assert.Null(await verify.ProbeResultStatusTransitions.SingleOrDefaultAsync(row => row.AgentId == fixture.AgentId && row.ResultId == recoveryResultId, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task St04ConfirmedRecoveryWithADanglingInactivePointerRollsBackEveryRecoveryMutation()
    {
        await using var fixture = await CreateFixtureAsync();
        var inactiveIncidentId = Guid.NewGuid();
        await using (var seed = new EePulseDbContext(fixture.Options))
        {
            await seed.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO availability_incidents (id, probe_id, rule_key, status, opened_at, resolved_at, resolved_by, resolution_note) VALUES ({inactiveIncidentId}, {fixture.ProbeId}, {"availability-down"}, {"Resolved"}, {fixture.Now}, {fixture.Now}, {"system-policy"}, {"confirmed-recovery"})", TestContext.Current.CancellationToken);
            seed.Add(new ProbeStatusProjection(fixture.ProbeId, ProbeStatus.Recovering, 0, 1, fixture.Now,
                fixture.Now, fixture.AgentId, Guid.NewGuid(), inactiveIncidentId));
            await seed.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var recoveryResultId = await AddLedgerAsync(fixture, fixture.Now.AddSeconds(1), fixture.Now.AddSeconds(1), 3, 0m);
        ProbeStatusProjection baselineProjection;
        (Guid AgentId, Guid ResultId)[] baselineDispositions;
        (Guid AgentId, Guid ResultId, ProbeStatus FromStatus, ProbeStatus ToStatus, string ReasonCode)[] baselineTransitions;
        (Guid Id, AvailabilityIncidentStatus Status, DateTimeOffset? ResolvedAt, string? ResolvedBy, string? ResolutionNote)[] baselineIncidents;
        Guid[] baselineEventIds;
        Guid[] baselineContextIds;
        await using (var baseline = new EePulseDbContext(fixture.Options))
        {
            baselineProjection = await baseline.ProbeStatusProjections.AsNoTracking().SingleAsync(row => row.ProbeId == fixture.ProbeId, TestContext.Current.CancellationToken);
            baselineDispositions = (await baseline.ProbeResultProcessingDispositions.AsNoTracking().OrderBy(row => row.AgentId).ThenBy(row => row.ResultId).ToListAsync(TestContext.Current.CancellationToken)).Select(row => (row.AgentId, row.ResultId)).ToArray();
            baselineTransitions = (await baseline.ProbeResultStatusTransitions.AsNoTracking().OrderBy(row => row.AgentId).ThenBy(row => row.ResultId).ToListAsync(TestContext.Current.CancellationToken)).Select(row => (row.AgentId, row.ResultId, row.FromStatus, row.ToStatus, row.ReasonCode)).ToArray();
            baselineIncidents = (await baseline.AvailabilityIncidents.AsNoTracking().OrderBy(row => row.Id).ToListAsync(TestContext.Current.CancellationToken)).Select(row => (row.Id, row.Status, row.ResolvedAt, row.ResolvedBy, row.ResolutionNote)).ToArray();
            baselineEventIds = await baseline.IncidentLifecycleEvents.AsNoTracking().OrderBy(row => row.EventId).Select(row => row.EventId).ToArrayAsync(TestContext.Current.CancellationToken);
            baselineContextIds = await baseline.NotificationSuppressionContexts.AsNoTracking().OrderBy(row => row.EventId).Select(row => row.EventId).ToArrayAsync(TestContext.Current.CancellationToken);
        }

        await using (var processing = new EePulseDbContext(fixture.Options))
        {
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => new ProbeResultStatusProcessor(processing, new FixedClock(fixture.Now)).ProcessNextAsync(fixture.ProbeId, TestContext.Current.CancellationToken));
            Assert.Contains("inconsistent active availability incident", exception.Message, StringComparison.Ordinal);
        }

        await using var verify = new EePulseDbContext(fixture.Options);
        var projectionAfterFailure = await verify.ProbeStatusProjections.SingleAsync(row => row.ProbeId == fixture.ProbeId, TestContext.Current.CancellationToken);
        Assert.Equal(baselineProjection.UnderlyingStatus, projectionAfterFailure.UnderlyingStatus);
        Assert.Equal(baselineProjection.ConsecutiveFailureCount, projectionAfterFailure.ConsecutiveFailureCount);
        Assert.Equal(baselineProjection.ConsecutiveSuccessCount, projectionAfterFailure.ConsecutiveSuccessCount);
        Assert.Equal(baselineProjection.LastFreshEventAt, projectionAfterFailure.LastFreshEventAt);
        Assert.Equal(baselineProjection.WatermarkEventAt, projectionAfterFailure.WatermarkEventAt);
        Assert.Equal(baselineProjection.WatermarkAgentId, projectionAfterFailure.WatermarkAgentId);
        Assert.Equal(baselineProjection.WatermarkResultId, projectionAfterFailure.WatermarkResultId);
        Assert.Equal(baselineProjection.StateVersion, projectionAfterFailure.StateVersion);
        Assert.Equal(baselineProjection.OpenIncidentId, projectionAfterFailure.OpenIncidentId);
        Assert.Equal(baselineDispositions, (await verify.ProbeResultProcessingDispositions.OrderBy(row => row.AgentId).ThenBy(row => row.ResultId).ToListAsync(TestContext.Current.CancellationToken)).Select(row => (row.AgentId, row.ResultId)).ToArray());
        Assert.Equal(baselineTransitions, (await verify.ProbeResultStatusTransitions.OrderBy(row => row.AgentId).ThenBy(row => row.ResultId).ToListAsync(TestContext.Current.CancellationToken)).Select(row => (row.AgentId, row.ResultId, row.FromStatus, row.ToStatus, row.ReasonCode)).ToArray());
        Assert.Equal(baselineIncidents, (await verify.AvailabilityIncidents.OrderBy(row => row.Id).ToListAsync(TestContext.Current.CancellationToken)).Select(row => (row.Id, row.Status, row.ResolvedAt, row.ResolvedBy, row.ResolutionNote)).ToArray());
        Assert.Equal(baselineEventIds, await verify.IncidentLifecycleEvents.OrderBy(row => row.EventId).Select(row => row.EventId).ToArrayAsync(TestContext.Current.CancellationToken));
        Assert.Equal(baselineContextIds, await verify.NotificationSuppressionContexts.OrderBy(row => row.EventId).Select(row => row.EventId).ToArrayAsync(TestContext.Current.CancellationToken));
        Assert.Null(await verify.ProbeResultProcessingDispositions.SingleOrDefaultAsync(row => row.AgentId == fixture.AgentId && row.ResultId == recoveryResultId, TestContext.Current.CancellationToken));
        Assert.Null(await verify.ProbeResultStatusTransitions.SingleOrDefaultAsync(row => row.AgentId == fixture.AgentId && row.ResultId == recoveryResultId, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task St04PreCommitFailureRollsBackResolutionThenRetryAndReplayRemainIdempotent()
    {
        await using var fixture = await CreateFixtureAsync();
        await AddLedgerAsync(fixture, fixture.Now, fixture.Now, 3, 0m);
        await AddLedgerAsync(fixture, fixture.Now.AddSeconds(1), fixture.Now.AddSeconds(1), 0, 1m);
        await AddLedgerAsync(fixture, fixture.Now.AddSeconds(2), fixture.Now.AddSeconds(2), 0, 1m);
        await AddLedgerAsync(fixture, fixture.Now.AddSeconds(3), fixture.Now.AddSeconds(3), 3, 0m);
        await using (var processing = new EePulseDbContext(fixture.Options))
        {
            var processor = new ProbeResultStatusProcessor(processing, new FixedClock(fixture.Now));
            for (var index = 0; index < 4; index++) await processor.ProcessNextAsync(fixture.ProbeId, TestContext.Current.CancellationToken);
        }

        var recoveryResultId = await AddLedgerAsync(fixture, fixture.Now.AddSeconds(4), fixture.Now.AddSeconds(4), 3, 0m);
        await using (var baseline = new EePulseDbContext(fixture.Options))
        {
            var projection = await baseline.ProbeStatusProjections.AsNoTracking().SingleAsync(TestContext.Current.CancellationToken);
            var incident = await baseline.AvailabilityIncidents.AsNoTracking().SingleAsync(TestContext.Current.CancellationToken);
            var failingOptions = new DbContextOptionsBuilder<EePulseDbContext>().UseNpgsql(fixture.ConnectionString).AddInterceptors(new ThrowBeforeSaveInterceptor()).Options;
            await using (var failing = new EePulseDbContext(failingOptions))
                await Assert.ThrowsAsync<InvalidOperationException>(() => new ProbeResultStatusProcessor(failing, new FixedClock(fixture.Now)).ProcessNextAsync(fixture.ProbeId, TestContext.Current.CancellationToken));

            await using var afterFailure = new EePulseDbContext(fixture.Options);
            var projectionAfterFailure = await afterFailure.ProbeStatusProjections.SingleAsync(TestContext.Current.CancellationToken);
            var incidentAfterFailure = await afterFailure.AvailabilityIncidents.SingleAsync(TestContext.Current.CancellationToken);
            Assert.Equal((projection.UnderlyingStatus, projection.ConsecutiveFailureCount, projection.ConsecutiveSuccessCount, projection.LastFreshEventAt, projection.WatermarkEventAt, projection.WatermarkAgentId, projection.WatermarkResultId, projection.StateVersion, projection.OpenIncidentId),
                (projectionAfterFailure.UnderlyingStatus, projectionAfterFailure.ConsecutiveFailureCount, projectionAfterFailure.ConsecutiveSuccessCount, projectionAfterFailure.LastFreshEventAt, projectionAfterFailure.WatermarkEventAt, projectionAfterFailure.WatermarkAgentId, projectionAfterFailure.WatermarkResultId, projectionAfterFailure.StateVersion, projectionAfterFailure.OpenIncidentId));
            Assert.Equal((incident.Status, incident.ResolvedAt, incident.ResolvedBy, incident.ResolutionNote), (incidentAfterFailure.Status, incidentAfterFailure.ResolvedAt, incidentAfterFailure.ResolvedBy, incidentAfterFailure.ResolutionNote));
            Assert.Null(await afterFailure.ProbeResultProcessingDispositions.SingleOrDefaultAsync(row => row.AgentId == fixture.AgentId && row.ResultId == recoveryResultId, TestContext.Current.CancellationToken));
            Assert.Null(await afterFailure.ProbeResultStatusTransitions.SingleOrDefaultAsync(row => row.AgentId == fixture.AgentId && row.ResultId == recoveryResultId, TestContext.Current.CancellationToken));
            Assert.Single(await afterFailure.IncidentLifecycleEvents.ToListAsync(TestContext.Current.CancellationToken));
            Assert.Single(await afterFailure.NotificationSuppressionContexts.ToListAsync(TestContext.Current.CancellationToken));
        }

        await using (var retry = new EePulseDbContext(fixture.Options))
            Assert.Equal(recoveryResultId, (await new ProbeResultStatusProcessor(retry, new FixedClock(fixture.Now)).ProcessNextAsync(fixture.ProbeId, TestContext.Current.CancellationToken)).ResultId);
        await using (var replay = new EePulseDbContext(fixture.Options))
            Assert.Equal(ProbeResultStatusProcessorOutcomeKind.NoPending, (await new ProbeResultStatusProcessor(replay, new FixedClock(fixture.Now)).ProcessNextAsync(fixture.ProbeId, TestContext.Current.CancellationToken)).Kind);

        await using var verify = new EePulseDbContext(fixture.Options);
        Assert.Equal(AvailabilityIncidentStatus.Resolved, (await verify.AvailabilityIncidents.SingleAsync(TestContext.Current.CancellationToken)).Status);
        Assert.Single(await verify.IncidentLifecycleEvents.Where(row => row.LifecycleEventType == IncidentLifecycleEventType.Resolved).ToListAsync(TestContext.Current.CancellationToken));
        Assert.Single(await verify.NotificationSuppressionContexts.Where(row => row.LifecycleEventKey == IncidentLifecycleEvent.ResolvedLifecycleEventKey).ToListAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task St03OpeningAtomicallyPersistsOneCompleteAvailabilityIncidentSet()
    {
        await using var fixture = await CreateFixtureAsync();
        await AddLedgerAsync(fixture, fixture.Now, fixture.Now, 3, 0m);
        await AddLedgerAsync(fixture, fixture.Now.AddSeconds(1), fixture.Now.AddSeconds(1), 0, 1m);
        var openingResultId = await AddLedgerAsync(fixture, fixture.Now.AddSeconds(2), fixture.Now.AddSeconds(2), 0, 1m);
        var alreadyDownResultId = await AddLedgerAsync(fixture, fixture.Now.AddSeconds(3), fixture.Now.AddSeconds(3), 0, 1m);

        await using (var db = new EePulseDbContext(fixture.Options))
        {
            var processor = new ProbeResultStatusProcessor(db, new FixedClock(fixture.Now.AddMinutes(1)));
            for (var index = 0; index < 2; index++) await processor.ProcessNextAsync(fixture.ProbeId, TestContext.Current.CancellationToken);
        }

        await using (var belowThreshold = new EePulseDbContext(fixture.Options))
        {
            Assert.Empty(await belowThreshold.AvailabilityIncidents.ToListAsync(TestContext.Current.CancellationToken));
            Assert.Empty(await belowThreshold.IncidentLifecycleEvents.ToListAsync(TestContext.Current.CancellationToken));
            Assert.Empty(await belowThreshold.NotificationSuppressionContexts.ToListAsync(TestContext.Current.CancellationToken));
            Assert.Null((await belowThreshold.ProbeStatusProjections.SingleAsync(row => row.ProbeId == fixture.ProbeId, TestContext.Current.CancellationToken)).OpenIncidentId);
        }

        await using (var db = new EePulseDbContext(fixture.Options))
        {
            var processor = new ProbeResultStatusProcessor(db, new FixedClock(fixture.Now.AddMinutes(1)));
            for (var index = 0; index < 2; index++) await processor.ProcessNextAsync(fixture.ProbeId, TestContext.Current.CancellationToken);
            Assert.Equal(ProbeResultStatusProcessorOutcomeKind.NoPending, (await processor.ProcessNextAsync(fixture.ProbeId, TestContext.Current.CancellationToken)).Kind);
        }

        var openingLedger = await ReadLedgerAsync(fixture, openingResultId);
        await using var verify = new EePulseDbContext(fixture.Options);
        var transition = await verify.ProbeResultStatusTransitions.SingleAsync(row => row.AgentId == fixture.AgentId && row.ResultId == openingResultId, TestContext.Current.CancellationToken);
        var disposition = await verify.ProbeResultProcessingDispositions.SingleAsync(row => row.AgentId == fixture.AgentId && row.ResultId == openingResultId, TestContext.Current.CancellationToken);
        var incident = await verify.AvailabilityIncidents.SingleAsync(TestContext.Current.CancellationToken);
        var lifecycleEvent = await verify.IncidentLifecycleEvents.SingleAsync(TestContext.Current.CancellationToken);
        var context = await verify.NotificationSuppressionContexts.SingleAsync(TestContext.Current.CancellationToken);
        var projection = await verify.ProbeStatusProjections.SingleAsync(row => row.ProbeId == fixture.ProbeId, TestContext.Current.CancellationToken);

        Assert.Equal(ProbeStatus.Up, transition.FromStatus);
        Assert.Equal(ProbeStatus.Down, transition.ToStatus);
        Assert.Equal("failure-threshold-met", transition.ReasonCode);
        Assert.Equal(ProbeResultProcessingDispositionKind.StateDriving, disposition.Disposition);
        Assert.DoesNotContain(await verify.ProbeResultStatusTransitions.ToListAsync(TestContext.Current.CancellationToken), row => row.ResultId == alreadyDownResultId);
        Assert.Equal(incident.Id, projection.OpenIncidentId);
        Assert.Equal(incident.Id, lifecycleEvent.IncidentId);
        Assert.Equal(incident.ProbeId, lifecycleEvent.ProbeId);
        Assert.Equal((fixture.AgentId, openingResultId), (lifecycleEvent.SourceAgentId, lifecycleEvent.SourceResultId));
        Assert.Equal((disposition.ResolvedPolicySnapshotId, disposition.ResolvedPolicyVersion), (lifecycleEvent.PolicySnapshotId, lifecycleEvent.PolicyVersion));
        Assert.Equal(openingLedger.EndedAt, incident.OpenedAt);
        Assert.Equal(openingLedger.EndedAt, lifecycleEvent.OccurredAt);
        Assert.Equal(openingLedger.ReceivedAt, context.EvaluatedAt);
        Assert.Equal(lifecycleEvent.EventId, context.EventId);
        Assert.Equal(NotificationSuppressionEligibility.Eligible, context.Eligibility);
        Assert.Equal("availability-down", context.ReasonCode);

        verify.Add(new AvailabilityIncident(Guid.NewGuid(), fixture.ProbeId, fixture.Now.AddMinutes(1)));
        await Assert.ThrowsAsync<DbUpdateException>(() => verify.SaveChangesAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task St03OpeningRetainsAnExistingActiveIncidentAndRepairsItsMissingProjectionPointer()
    {
        await using var fixture = await CreateFixtureAsync();
        await AddLedgerAsync(fixture, fixture.Now, fixture.Now, 3, 0m);
        await AddLedgerAsync(fixture, fixture.Now.AddSeconds(1), fixture.Now.AddSeconds(1), 0, 1m);
        await using (var beforeOpening = new EePulseDbContext(fixture.Options))
        {
            var processor = new ProbeResultStatusProcessor(beforeOpening, new FixedClock(fixture.Now));
            for (var index = 0; index < 2; index++) await processor.ProcessNextAsync(fixture.ProbeId, TestContext.Current.CancellationToken);
        }

        var existingIncident = new AvailabilityIncident(Guid.NewGuid(), fixture.ProbeId, fixture.Now.AddSeconds(1));
        await using (var seedActive = new EePulseDbContext(fixture.Options))
        {
            seedActive.Add(existingIncident);
            await seedActive.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await AddLedgerAsync(fixture, fixture.Now.AddSeconds(2), fixture.Now.AddSeconds(2), 0, 1m);
        await using (var opening = new EePulseDbContext(fixture.Options))
            await new ProbeResultStatusProcessor(opening, new FixedClock(fixture.Now)).ProcessNextAsync(fixture.ProbeId, TestContext.Current.CancellationToken);

        await using var verify = new EePulseDbContext(fixture.Options);
        Assert.Equal(existingIncident.Id, (await verify.ProbeStatusProjections.SingleAsync(row => row.ProbeId == fixture.ProbeId, TestContext.Current.CancellationToken)).OpenIncidentId);
        Assert.Single(await verify.AvailabilityIncidents.ToListAsync(TestContext.Current.CancellationToken));
        Assert.Empty(await verify.IncidentLifecycleEvents.ToListAsync(TestContext.Current.CancellationToken));
        Assert.Empty(await verify.NotificationSuppressionContexts.ToListAsync(TestContext.Current.CancellationToken));
        Assert.Contains(await verify.ProbeResultStatusTransitions.ToListAsync(TestContext.Current.CancellationToken), transition =>
            transition.ToStatus == ProbeStatus.Down && transition.ReasonCode == "failure-threshold-met");
    }

    [Fact]
    public async Task St03OpeningRetainsAMatchingActiveIncidentProjectionPointer()
    {
        await using var fixture = await CreateFixtureAsync();
        await AddLedgerAsync(fixture, fixture.Now, fixture.Now, 3, 0m);
        await AddLedgerAsync(fixture, fixture.Now.AddSeconds(1), fixture.Now.AddSeconds(1), 0, 1m);
        await using (var beforeOpening = new EePulseDbContext(fixture.Options))
        {
            var processor = new ProbeResultStatusProcessor(beforeOpening, new FixedClock(fixture.Now));
            for (var index = 0; index < 2; index++) await processor.ProcessNextAsync(fixture.ProbeId, TestContext.Current.CancellationToken);
        }

        var existingIncident = new AvailabilityIncident(Guid.NewGuid(), fixture.ProbeId, fixture.Now.AddSeconds(1));
        await using (var seedActive = new EePulseDbContext(fixture.Options))
        {
            seedActive.Add(existingIncident);
            var projection = await seedActive.ProbeStatusProjections.SingleAsync(row => row.ProbeId == fixture.ProbeId, TestContext.Current.CancellationToken);
            seedActive.Entry(projection).Property(nameof(ProbeStatusProjection.OpenIncidentId)).CurrentValue = existingIncident.Id;
            await seedActive.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var openingResultId = await AddLedgerAsync(fixture, fixture.Now.AddSeconds(2), fixture.Now.AddSeconds(2), 0, 1m);
        await using (var opening = new EePulseDbContext(fixture.Options))
            await new ProbeResultStatusProcessor(opening, new FixedClock(fixture.Now)).ProcessNextAsync(fixture.ProbeId, TestContext.Current.CancellationToken);

        await using var verify = new EePulseDbContext(fixture.Options);
        Assert.Equal(existingIncident.Id, (await verify.ProbeStatusProjections.SingleAsync(row => row.ProbeId == fixture.ProbeId, TestContext.Current.CancellationToken)).OpenIncidentId);
        Assert.Single(await verify.AvailabilityIncidents.ToListAsync(TestContext.Current.CancellationToken));
        Assert.Empty(await verify.IncidentLifecycleEvents.ToListAsync(TestContext.Current.CancellationToken));
        Assert.Empty(await verify.NotificationSuppressionContexts.ToListAsync(TestContext.Current.CancellationToken));
        Assert.NotNull(await verify.ProbeResultProcessingDispositions.SingleOrDefaultAsync(row => row.AgentId == fixture.AgentId && row.ResultId == openingResultId, TestContext.Current.CancellationToken));
        Assert.Contains(await verify.ProbeResultStatusTransitions.ToListAsync(TestContext.Current.CancellationToken), transition =>
            transition.AgentId == fixture.AgentId && transition.ResultId == openingResultId && transition.ToStatus == ProbeStatus.Down);
    }

    [Fact]
    public async Task St03OpeningFailsAndRollsBackWhenProjectionPointsToAnotherDatabaseValidIncident()
    {
        await using var fixture = await CreateFixtureAsync();
        await AddLedgerAsync(fixture, fixture.Now, fixture.Now, 3, 0m);
        var belowThresholdResultId = await AddLedgerAsync(fixture, fixture.Now.AddSeconds(1), fixture.Now.AddSeconds(1), 0, 1m);
        await using (var beforeOpening = new EePulseDbContext(fixture.Options))
        {
            var processor = new ProbeResultStatusProcessor(beforeOpening, new FixedClock(fixture.Now));
            for (var index = 0; index < 2; index++) await processor.ProcessNextAsync(fixture.ProbeId, TestContext.Current.CancellationToken);
        }

        var activeIncident = new AvailabilityIncident(Guid.NewGuid(), fixture.ProbeId, fixture.Now.AddSeconds(1));
        var resolvedIncidentId = Guid.NewGuid();
        await using (var seedInconsistent = new EePulseDbContext(fixture.Options))
        {
            seedInconsistent.Add(activeIncident);
            await seedInconsistent.SaveChangesAsync(TestContext.Current.CancellationToken);
            await seedInconsistent.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO availability_incidents (id, probe_id, rule_key, status, opened_at, resolved_at, resolved_by, resolution_note) VALUES ({resolvedIncidentId}, {fixture.ProbeId}, {"availability-down"}, {"Resolved"}, {fixture.Now}, {fixture.Now}, {"system-policy"}, {"confirmed-recovery"})", TestContext.Current.CancellationToken);
            var projection = await seedInconsistent.ProbeStatusProjections.SingleAsync(row => row.ProbeId == fixture.ProbeId, TestContext.Current.CancellationToken);
            seedInconsistent.Entry(projection).Property(nameof(ProbeStatusProjection.OpenIncidentId)).CurrentValue = resolvedIncidentId;
            await seedInconsistent.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var openingResultId = await AddLedgerAsync(fixture, fixture.Now.AddSeconds(2), fixture.Now.AddSeconds(2), 0, 1m);
        ProbeStatusProjection baselineProjection;
        Guid[] baselineIncidentIds;
        Guid[] baselineLifecycleEventIds;
        Guid[] baselineSuppressionContextEventIds;
        await using (var beforeFailure = new EePulseDbContext(fixture.Options))
        {
            baselineProjection = await beforeFailure.ProbeStatusProjections.AsNoTracking()
                .SingleAsync(row => row.ProbeId == fixture.ProbeId, TestContext.Current.CancellationToken);
            baselineIncidentIds = await beforeFailure.AvailabilityIncidents.AsNoTracking().OrderBy(incident => incident.Id)
                .Select(incident => incident.Id).ToArrayAsync(TestContext.Current.CancellationToken);
            baselineLifecycleEventIds = await beforeFailure.IncidentLifecycleEvents.AsNoTracking().OrderBy(lifecycleEvent => lifecycleEvent.EventId)
                .Select(lifecycleEvent => lifecycleEvent.EventId).ToArrayAsync(TestContext.Current.CancellationToken);
            baselineSuppressionContextEventIds = await beforeFailure.NotificationSuppressionContexts.AsNoTracking().OrderBy(context => context.EventId)
                .Select(context => context.EventId).ToArrayAsync(TestContext.Current.CancellationToken);
        }
        await using (var opening = new EePulseDbContext(fixture.Options))
        {
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => new ProbeResultStatusProcessor(opening, new FixedClock(fixture.Now)).ProcessNextAsync(fixture.ProbeId, TestContext.Current.CancellationToken));
            Assert.Contains("inconsistent active availability incident", exception.Message, StringComparison.Ordinal);
        }

        await using var verify = new EePulseDbContext(fixture.Options);
        var projectionAfterFailure = await verify.ProbeStatusProjections.SingleAsync(row => row.ProbeId == fixture.ProbeId, TestContext.Current.CancellationToken);
        Assert.Equal(baselineProjection.OpenIncidentId, projectionAfterFailure.OpenIncidentId);
        Assert.Equal(baselineProjection.UnderlyingStatus, projectionAfterFailure.UnderlyingStatus);
        Assert.Equal(baselineProjection.ConsecutiveFailureCount, projectionAfterFailure.ConsecutiveFailureCount);
        Assert.Equal(baselineProjection.ConsecutiveSuccessCount, projectionAfterFailure.ConsecutiveSuccessCount);
        Assert.Equal(baselineProjection.LastFreshEventAt, projectionAfterFailure.LastFreshEventAt);
        Assert.Equal(baselineProjection.WatermarkEventAt, projectionAfterFailure.WatermarkEventAt);
        Assert.Equal(baselineProjection.WatermarkAgentId, projectionAfterFailure.WatermarkAgentId);
        Assert.Equal(baselineProjection.WatermarkResultId, projectionAfterFailure.WatermarkResultId);
        Assert.Equal(baselineProjection.StateVersion, projectionAfterFailure.StateVersion);
        Assert.Equal(resolvedIncidentId, projectionAfterFailure.OpenIncidentId);
        Assert.Equal(ProbeStatus.Up, projectionAfterFailure.UnderlyingStatus);
        Assert.Equal(1, projectionAfterFailure.ConsecutiveFailureCount);
        Assert.Equal(belowThresholdResultId, projectionAfterFailure.WatermarkResultId);
        Assert.Equal(2, await verify.ProbeResultProcessingDispositions.CountAsync(TestContext.Current.CancellationToken));
        Assert.Single(await verify.ProbeResultStatusTransitions.ToListAsync(TestContext.Current.CancellationToken));
        Assert.Null(await verify.ProbeResultStatusTransitions.SingleOrDefaultAsync(
            row => row.AgentId == fixture.AgentId && row.ResultId == openingResultId,
            TestContext.Current.CancellationToken));
        Assert.NotNull(await verify.ProbeResultLedgerEntries.SingleOrDefaultAsync(row => row.AgentId == fixture.AgentId && row.ResultId == openingResultId, TestContext.Current.CancellationToken));
        Assert.Null(await verify.ProbeResultProcessingDispositions.SingleOrDefaultAsync(row => row.AgentId == fixture.AgentId && row.ResultId == openingResultId, TestContext.Current.CancellationToken));
        Assert.Equal(baselineIncidentIds, await verify.AvailabilityIncidents.OrderBy(incident => incident.Id).Select(incident => incident.Id).ToArrayAsync(TestContext.Current.CancellationToken));
        Assert.Equal(baselineLifecycleEventIds, await verify.IncidentLifecycleEvents.OrderBy(lifecycleEvent => lifecycleEvent.EventId).Select(lifecycleEvent => lifecycleEvent.EventId).ToArrayAsync(TestContext.Current.CancellationToken));
        Assert.Equal(baselineSuppressionContextEventIds, await verify.NotificationSuppressionContexts.OrderBy(context => context.EventId).Select(context => context.EventId).ToArrayAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ConcurrentThresholdOpeningCreatesOneAvailabilityIncidentSet()
    {
        await using var fixture = await CreateFixtureAsync();
        await AddLedgerAsync(fixture, fixture.Now, fixture.Now, 3, 0m);
        await AddLedgerAsync(fixture, fixture.Now.AddSeconds(1), fixture.Now.AddSeconds(1), 0, 1m);
        await using (var beforeOpening = new EePulseDbContext(fixture.Options))
        {
            var processor = new ProbeResultStatusProcessor(beforeOpening, new FixedClock(fixture.Now));
            for (var index = 0; index < 2; index++) await processor.ProcessNextAsync(fixture.ProbeId, TestContext.Current.CancellationToken);
        }

        await AddLedgerAsync(fixture, fixture.Now.AddSeconds(2), fixture.Now.AddSeconds(2), 0, 1m);
        await using var first = new EePulseDbContext(fixture.Options);
        await using var second = new EePulseDbContext(fixture.Options);
        var outcomes = await Task.WhenAll(
            new ProbeResultStatusProcessor(first, new FixedClock(fixture.Now)).ProcessNextAsync(fixture.ProbeId, TestContext.Current.CancellationToken),
            new ProbeResultStatusProcessor(second, new FixedClock(fixture.Now)).ProcessNextAsync(fixture.ProbeId, TestContext.Current.CancellationToken));

        Assert.Contains(outcomes, outcome => outcome.Kind == ProbeResultStatusProcessorOutcomeKind.Processed);
        Assert.Contains(outcomes, outcome => outcome.Kind == ProbeResultStatusProcessorOutcomeKind.NoPending);
        await using var verify = new EePulseDbContext(fixture.Options);
        var incident = await verify.AvailabilityIncidents.SingleAsync(TestContext.Current.CancellationToken);
        var projection = await verify.ProbeStatusProjections.SingleAsync(row => row.ProbeId == fixture.ProbeId, TestContext.Current.CancellationToken);
        Assert.Equal(incident.Id, projection.OpenIncidentId);
        Assert.Single(await verify.IncidentLifecycleEvents.ToListAsync(TestContext.Current.CancellationToken));
        Assert.Single(await verify.NotificationSuppressionContexts.ToListAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task St03PreCommitFailureRollsBackTheCompleteOpeningSetThenRetryCreatesItOnce()
    {
        await using var fixture = await CreateFixtureAsync();
        await AddLedgerAsync(fixture, fixture.Now, fixture.Now, 3, 0m);
        await using (var bootstrap = new EePulseDbContext(fixture.Options))
            await new ProbeResultStatusProcessor(bootstrap, new FixedClock(fixture.Now)).ProcessNextAsync(fixture.ProbeId, TestContext.Current.CancellationToken);

        await AddLedgerAsync(fixture, fixture.Now.AddSeconds(1), fixture.Now.AddSeconds(1), 0, 1m);
        await using (var belowThreshold = new EePulseDbContext(fixture.Options))
            await new ProbeResultStatusProcessor(belowThreshold, new FixedClock(fixture.Now)).ProcessNextAsync(fixture.ProbeId, TestContext.Current.CancellationToken);

        var openingResultId = await AddLedgerAsync(fixture, fixture.Now.AddSeconds(2), fixture.Now.AddSeconds(2), 0, 1m);
        var failingOptions = new DbContextOptionsBuilder<EePulseDbContext>().UseNpgsql(fixture.ConnectionString)
            .AddInterceptors(new ThrowBeforeSaveInterceptor()).Options;
        await using (var failing = new EePulseDbContext(failingOptions))
            await Assert.ThrowsAsync<InvalidOperationException>(() => new ProbeResultStatusProcessor(failing, new FixedClock(fixture.Now)).ProcessNextAsync(fixture.ProbeId, TestContext.Current.CancellationToken));

        await using (var afterFailure = new EePulseDbContext(fixture.Options))
        {
            Assert.Equal(2, await afterFailure.ProbeResultProcessingDispositions.CountAsync(TestContext.Current.CancellationToken));
            Assert.Single(await afterFailure.ProbeResultStatusTransitions.ToListAsync(TestContext.Current.CancellationToken));
            Assert.Empty(await afterFailure.AvailabilityIncidents.ToListAsync(TestContext.Current.CancellationToken));
            Assert.Empty(await afterFailure.IncidentLifecycleEvents.ToListAsync(TestContext.Current.CancellationToken));
            Assert.Empty(await afterFailure.NotificationSuppressionContexts.ToListAsync(TestContext.Current.CancellationToken));
            Assert.Null((await afterFailure.ProbeStatusProjections.SingleAsync(TestContext.Current.CancellationToken)).OpenIncidentId);
        }

        await using (var retry = new EePulseDbContext(fixture.Options))
            Assert.Equal(openingResultId, (await new ProbeResultStatusProcessor(retry, new FixedClock(fixture.Now)).ProcessNextAsync(fixture.ProbeId, TestContext.Current.CancellationToken)).ResultId);

        await using var verify = new EePulseDbContext(fixture.Options);
        Assert.Equal(3, await verify.ProbeResultProcessingDispositions.CountAsync(TestContext.Current.CancellationToken));
        Assert.Equal(2, await verify.ProbeResultStatusTransitions.CountAsync(TestContext.Current.CancellationToken));
        Assert.Single(await verify.AvailabilityIncidents.ToListAsync(TestContext.Current.CancellationToken));
        Assert.Single(await verify.IncidentLifecycleEvents.ToListAsync(TestContext.Current.CancellationToken));
        Assert.Single(await verify.NotificationSuppressionContexts.ToListAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ProcessesKernelOutcomesAndAdvancesStateVersionForEveryAppliedResult()
    {
        await using var fixture = await CreateFixtureAsync();
        var bootstrapResultId = await AddLedgerAsync(fixture, fixture.Now.AddSeconds(-4), fixture.Now, successes: 3, packetLossRatio: 0m);
        var degradationResultId = await AddLedgerAsync(fixture, fixture.Now.AddSeconds(-3), fixture.Now.AddSeconds(1), successes: 3, packetLossRatio: 0m, averageRtt: 500m);
        var sameStateFailureResultId = await AddLedgerAsync(fixture, fixture.Now.AddSeconds(-2), fixture.Now.AddSeconds(2), successes: 0, packetLossRatio: 1m);
        var downResultId = await AddLedgerAsync(fixture, fixture.Now.AddSeconds(-1), fixture.Now.AddSeconds(3), successes: 0, packetLossRatio: 1m);
        var recoveryPendingResultId = await AddLedgerAsync(fixture, fixture.Now, fixture.Now.AddSeconds(4), successes: 3, packetLossRatio: 0m);
        var recoveredResultId = await AddLedgerAsync(fixture, fixture.Now.AddSeconds(1), fixture.Now.AddSeconds(5), successes: 3, packetLossRatio: 0m);

        await using var db = new EePulseDbContext(fixture.Options);
        var processor = new ProbeResultStatusProcessor(db, new FixedClock(fixture.Now.AddMinutes(1)));
        for (var index = 0; index < 6; index++) Assert.Equal(ProbeResultStatusProcessorOutcomeKind.Processed, (await processor.ProcessNextAsync(fixture.ProbeId, TestContext.Current.CancellationToken)).Kind);

        await using var verify = new EePulseDbContext(fixture.Options);
        var projection = await verify.ProbeStatusProjections.SingleAsync(row => row.ProbeId == fixture.ProbeId, TestContext.Current.CancellationToken);
        Assert.Equal(ProbeStatus.Up, projection.UnderlyingStatus);
        Assert.Equal(0, projection.ConsecutiveFailureCount);
        Assert.Equal(2, projection.ConsecutiveSuccessCount);
        Assert.Equal(6, projection.StateVersion);
        Assert.Equal(6, await verify.ProbeResultProcessingDispositions.CountAsync(TestContext.Current.CancellationToken));
        var transitions = await verify.ProbeResultStatusTransitions.OrderBy(row => row.EventAt).ToListAsync(TestContext.Current.CancellationToken);
        Assert.Equal(5, transitions.Count);
        Assert.DoesNotContain(transitions, row => row.ResultId == sameStateFailureResultId);
        Assert.Collection(transitions,
            row => Assert.Equal((bootstrapResultId, "bootstrap-success"), (row.ResultId, row.ReasonCode)),
            row => Assert.Equal((degradationResultId, "quality-degraded"), (row.ResultId, row.ReasonCode)),
            row => Assert.Equal((downResultId, "failure-threshold-met"), (row.ResultId, row.ReasonCode)),
            row => Assert.Equal((recoveryPendingResultId, "recovery-pending"), (row.ResultId, row.ReasonCode)),
            row => Assert.Equal((recoveredResultId, "recovery-threshold-met"), (row.ResultId, row.ReasonCode)));
        var bootstrapLedger = await ReadLedgerAsync(fixture, bootstrapResultId);
        var bootstrapTransition = transitions.Single(row => row.ResultId == bootstrapResultId);
        Assert.Equal(bootstrapLedger.AgentId, bootstrapTransition.AgentId);
        Assert.Equal(bootstrapLedger.ResultId, bootstrapTransition.ResultId);
        Assert.Equal(bootstrapLedger.ProbeId, bootstrapTransition.ProbeId);
        Assert.Equal(bootstrapLedger.EndedAt, bootstrapTransition.EventAt);
        Assert.Equal(bootstrapLedger.ReceivedAt, bootstrapTransition.ReceivedAt);
        var bootstrapDisposition = await verify.ProbeResultProcessingDispositions.SingleAsync(row => row.AgentId == bootstrapTransition.AgentId && row.ResultId == bootstrapTransition.ResultId, TestContext.Current.CancellationToken);
        Assert.Equal(bootstrapDisposition.ResolvedPolicySnapshotId, fixture.PolicyId);
        Assert.Equal(1, bootstrapDisposition.ResolvedPolicyVersion);
    }

    [Fact]
    public async Task PersistsQualityRestorationAndRecoveryFailureTransitions()
    {
        await using var fixture = await CreateFixtureAsync();
        await AddLedgerAsync(fixture, fixture.Now, fixture.Now, 3, 0m, averageRtt: 500m);
        var restoredResultId = await AddLedgerAsync(fixture, fixture.Now.AddSeconds(1), fixture.Now.AddSeconds(1), 3, 0m);
        await AddLedgerAsync(fixture, fixture.Now.AddSeconds(2), fixture.Now.AddSeconds(2), 0, 1m);
        await AddLedgerAsync(fixture, fixture.Now.AddSeconds(3), fixture.Now.AddSeconds(3), 0, 1m);
        await AddLedgerAsync(fixture, fixture.Now.AddSeconds(4), fixture.Now.AddSeconds(4), 3, 0m);
        var recoveryFailedResultId = await AddLedgerAsync(fixture, fixture.Now.AddSeconds(5), fixture.Now.AddSeconds(5), 0, 1m);

        await using (var db = new EePulseDbContext(fixture.Options))
        {
            var processor = new ProbeResultStatusProcessor(db, new FixedClock(fixture.Now));
            for (var index = 0; index < 6; index++) await processor.ProcessNextAsync(fixture.ProbeId, TestContext.Current.CancellationToken);
        }

        await using var verify = new EePulseDbContext(fixture.Options);
        var transitions = await verify.ProbeResultStatusTransitions.ToListAsync(TestContext.Current.CancellationToken);
        Assert.Contains(transitions, row => row.ResultId == restoredResultId && row.ReasonCode == "quality-restored" && row.FromStatus == ProbeStatus.Degraded && row.ToStatus == ProbeStatus.Up);
        Assert.Contains(transitions, row => row.ResultId == recoveryFailedResultId && row.ReasonCode == "recovery-failed" && row.FromStatus == ProbeStatus.Recovering && row.ToStatus == ProbeStatus.Down);
    }

    [Fact]
    public async Task UsesDeterministicCursorOrderIncludingEqualEndedAtAndCommittedReplayIsIdempotent()
    {
        await using var fixture = await CreateFixtureAsync();
        var eventAt = fixture.Now;
        var ids = new[] { Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid() }.OrderBy(id => id.ToString("D"), StringComparer.Ordinal).ToArray();
        await AddLedgerAsync(fixture, eventAt, eventAt, 3, 0m, resultId: ids[2]);
        await AddLedgerAsync(fixture, eventAt, eventAt, 3, 0m, resultId: ids[0]);
        await AddLedgerAsync(fixture, eventAt, eventAt, 3, 0m, resultId: ids[1]);

        await using (var db = new EePulseDbContext(fixture.Options))
        {
            var processor = new ProbeResultStatusProcessor(db, new FixedClock(fixture.Now));
            var processedIds = new List<Guid>();
            for (var index = 0; index < 3; index++)
                processedIds.Add((await processor.ProcessNextAsync(fixture.ProbeId, TestContext.Current.CancellationToken)).ResultId!.Value);
            Assert.Equal(ids, processedIds);
            Assert.Equal(ProbeResultStatusProcessorOutcomeKind.NoPending, (await processor.ProcessNextAsync(fixture.ProbeId, TestContext.Current.CancellationToken)).Kind);
        }

        await using var verify = new EePulseDbContext(fixture.Options);
        Assert.Equal(3, await verify.ProbeResultProcessingDispositions.CountAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ConcurrentSameProbeCallsDoNotDuplicateApplication()
    {
        await using var fixture = await CreateFixtureAsync();
        await AddLedgerAsync(fixture, fixture.Now, fixture.Now, 3, 0m);
        await AddLedgerAsync(fixture, fixture.Now.AddSeconds(1), fixture.Now.AddSeconds(1), 3, 0m);

        await using var first = new EePulseDbContext(fixture.Options);
        await using var second = new EePulseDbContext(fixture.Options);
        await Task.WhenAll(
            new ProbeResultStatusProcessor(first, new FixedClock(fixture.Now)).ProcessNextAsync(fixture.ProbeId, TestContext.Current.CancellationToken),
            new ProbeResultStatusProcessor(second, new FixedClock(fixture.Now)).ProcessNextAsync(fixture.ProbeId, TestContext.Current.CancellationToken));

        await using var verify = new EePulseDbContext(fixture.Options);
        Assert.Equal(2, await verify.ProbeResultProcessingDispositions.CountAsync(TestContext.Current.CancellationToken));
        Assert.Equal(1, await verify.ProbeResultStatusTransitions.CountAsync(TestContext.Current.CancellationToken));
        Assert.Equal(2, (await verify.ProbeStatusProjections.SingleAsync(TestContext.Current.CancellationToken)).StateVersion);
    }

    [Fact]
    public async Task PreCommitFailureRollsBackProjectionAndDispositionThenRetrySucceeds()
    {
        await using var fixture = await CreateFixtureAsync();
        await AddLedgerAsync(fixture, fixture.Now, fixture.Now, 3, 0m);
        var interceptor = new ThrowBeforeSaveInterceptor();
        var failingOptions = new DbContextOptionsBuilder<EePulseDbContext>().UseNpgsql(fixture.ConnectionString).AddInterceptors(interceptor).Options;

        await using (var failing = new EePulseDbContext(failingOptions))
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => new ProbeResultStatusProcessor(failing, new FixedClock(fixture.Now)).ProcessNextAsync(fixture.ProbeId, TestContext.Current.CancellationToken));
        }

        await using (var afterFailure = new EePulseDbContext(fixture.Options))
        {
            Assert.Empty(await afterFailure.ProbeResultProcessingDispositions.ToListAsync(TestContext.Current.CancellationToken));
            Assert.Empty(await afterFailure.ProbeStatusProjections.ToListAsync(TestContext.Current.CancellationToken));
            Assert.Empty(await afterFailure.ProbeResultStatusTransitions.ToListAsync(TestContext.Current.CancellationToken));
        }

        await using var retry = new EePulseDbContext(fixture.Options);
        Assert.Equal(ProbeResultStatusProcessorOutcomeKind.Processed, (await new ProbeResultStatusProcessor(retry, new FixedClock(fixture.Now)).ProcessNextAsync(fixture.ProbeId, TestContext.Current.CancellationToken)).Kind);
    }

    [Fact]
    public async Task CancellationBeforeCommitLeavesTheLedgerRowPendingForRetry()
    {
        await using var fixture = await CreateFixtureAsync();
        var resultId = await AddLedgerAsync(fixture, fixture.Now, fixture.Now, 3, 0m);
        var blocker = new BlockingSaveInterceptor();
        var options = new DbContextOptionsBuilder<EePulseDbContext>().UseNpgsql(fixture.ConnectionString).AddInterceptors(blocker).Options;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);

        await using (var interrupted = new EePulseDbContext(options))
        {
            var processing = new ProbeResultStatusProcessor(interrupted, new FixedClock(fixture.Now)).ProcessNextAsync(fixture.ProbeId, cancellation.Token);
            await blocker.Entered.Task.WaitAsync(TestContext.Current.CancellationToken);
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => processing);
        }

        await using (var afterCancellation = new EePulseDbContext(fixture.Options))
        {
            Assert.Empty(await afterCancellation.ProbeResultProcessingDispositions.ToListAsync(TestContext.Current.CancellationToken));
            Assert.Empty(await afterCancellation.ProbeStatusProjections.ToListAsync(TestContext.Current.CancellationToken));
            Assert.Empty(await afterCancellation.ProbeResultStatusTransitions.ToListAsync(TestContext.Current.CancellationToken));
        }

        await using var retry = new EePulseDbContext(fixture.Options);
        Assert.Equal(resultId, (await new ProbeResultStatusProcessor(retry, new FixedClock(fixture.Now)).ProcessNextAsync(fixture.ProbeId, TestContext.Current.CancellationToken)).ResultId);
        Assert.Equal(1, await retry.ProbeResultStatusTransitions.CountAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task IngestionTransactionWaitsForProcessorLockAndPostReleaseRowIsProcessedNext()
    {
        await using var fixture = await CreateFixtureAsync();
        var firstResultId = await AddLedgerAsync(fixture, fixture.Now, fixture.Now, 3, 0m);
        var blocker = new BlockingSaveInterceptor();
        var processorOptions = new DbContextOptionsBuilder<EePulseDbContext>().UseNpgsql(fixture.ConnectionString).AddInterceptors(blocker).Options;
        await using var processorDb = new EePulseDbContext(processorOptions);
        var processing = new ProbeResultStatusProcessor(processorDb, new FixedClock(fixture.Now)).ProcessNextAsync(fixture.ProbeId, TestContext.Current.CancellationToken);
        await blocker.Entered.Task.WaitAsync(TestContext.Current.CancellationToken);

        await using var ingestionDb = new EePulseDbContext(fixture.Options);
        await using var ingestionTransaction = await ingestionDb.Database.BeginTransactionAsync(TestContext.Current.CancellationToken);
        var ingestionBackendProcessId = await ingestionDb.Database.SqlQueryRaw<int>("SELECT pg_backend_pid() AS \"Value\"").SingleAsync(TestContext.Current.CancellationToken);
        var ingestionLock = ProbeTransactionLock.AcquireAsync(ingestionDb, fixture.ProbeId, TestContext.Current.CancellationToken);
        await using (var observer = new NpgsqlConnection(fixture.ConnectionString))
        {
            await observer.OpenAsync(TestContext.Current.CancellationToken);
            await WaitForUngrantedProbeLockAsync(observer, ingestionBackendProcessId, fixture.ProbeId, ingestionLock, TestContext.Current.CancellationToken);
        }

        blocker.Release.TrySetResult();
        Assert.Equal(firstResultId, (await processing).ResultId);
        await ingestionLock;
        var secondResultId = Guid.NewGuid();
        ingestionDb.Add(CreateLedger(fixture, fixture.Now.AddSeconds(1), fixture.Now.AddSeconds(1), 3, 0m, 1, secondResultId, 1m));
        await ingestionDb.SaveChangesAsync(TestContext.Current.CancellationToken);
        await ingestionTransaction.CommitAsync(TestContext.Current.CancellationToken);

        await using (var verify = new EePulseDbContext(fixture.Options))
            Assert.Equal(1, await verify.ProbeResultProcessingDispositions.CountAsync(TestContext.Current.CancellationToken));
        await using var nextDb = new EePulseDbContext(fixture.Options);
        Assert.Equal(secondResultId, (await new ProbeResultStatusProcessor(nextDb, new FixedClock(fixture.Now)).ProcessNextAsync(fixture.ProbeId, TestContext.Current.CancellationToken)).ResultId);
    }

    [Fact]
    public async Task ResolvesExactSnapshotAndEffectiveBoundaryUsingPersistedPostgresTimestamps()
    {
        await using var fixture = await CreateFixtureAsync();
        var second = await AddConfigurationVersionAsync(fixture, 2, failureThreshold: 1, recoveryThreshold: 1);
        var boundary = await ReadBoundaryAsync(fixture, fixture.AgentId, 2);
        var effectiveBoundaryResultId = await AddLedgerAsync(fixture, boundary, boundary, 3, 0m, configurationVersion: 2);
        var failureResultId = await AddLedgerAsync(fixture, boundary.Add(TimeSpan.FromMicroseconds(1)), boundary.Add(TimeSpan.FromMicroseconds(1)), 0, 1m, configurationVersion: 2);
        var recoveryResultId = await AddLedgerAsync(fixture, boundary.Add(TimeSpan.FromMicroseconds(2)), boundary.Add(TimeSpan.FromMicroseconds(2)), 3, 0m, configurationVersion: 2);
        var earlierBoundaryResultId = await AddLedgerAsync(fixture, boundary.Add(TimeSpan.FromMicroseconds(-1)), boundary.Add(TimeSpan.FromMicroseconds(-1)), 3, 0m, configurationVersion: 2);
        var persistedBoundary = await ReadBoundaryAsync(fixture, fixture.AgentId, 2);
        var effectiveBoundaryLedger = await ReadLedgerAsync(fixture, effectiveBoundaryResultId);
        var failureLedger = await ReadLedgerAsync(fixture, failureResultId);
        var recoveryLedger = await ReadLedgerAsync(fixture, recoveryResultId);
        var earlierBoundaryLedger = await ReadLedgerAsync(fixture, earlierBoundaryResultId);

        Assert.Equal(persistedBoundary, effectiveBoundaryLedger.ReceivedAt);
        Assert.Equal(persistedBoundary.Add(TimeSpan.FromMicroseconds(-1)), earlierBoundaryLedger.ReceivedAt);
        Assert.Equal(persistedBoundary.Add(TimeSpan.FromMicroseconds(1)), failureLedger.ReceivedAt);
        Assert.Equal(persistedBoundary.Add(TimeSpan.FromMicroseconds(2)), recoveryLedger.ReceivedAt);

        await using var db = new EePulseDbContext(fixture.Options);
        var processor = new ProbeResultStatusProcessor(db, new FixedClock(fixture.Now));
        for (var index = 0; index < 4; index++) await processor.ProcessNextAsync(fixture.ProbeId, TestContext.Current.CancellationToken);

        await using var verify = new EePulseDbContext(fixture.Options);
        var dispositions = await verify.ProbeResultProcessingDispositions.OrderBy(row => row.EventAt).ToListAsync(TestContext.Current.CancellationToken);
        Assert.Contains(dispositions, row => row.ResultId == earlierBoundaryResultId && row.Disposition == ProbeResultProcessingDispositionKind.HistoricalOther && row.ReasonCode == "config-not-effective");
        Assert.Contains(dispositions, row => row.ResultId == effectiveBoundaryResultId && row.Disposition == ProbeResultProcessingDispositionKind.StateDriving);
        Assert.All(dispositions.Where(row => row.ResolvedPolicySnapshotId.HasValue), row => Assert.Equal(second.PolicyId, row.ResolvedPolicySnapshotId));
        Assert.Equal(ProbeStatus.Up, (await verify.ProbeStatusProjections.SingleAsync(TestContext.Current.CancellationToken)).UnderlyingStatus);
        Assert.Contains(await verify.ProbeResultStatusTransitions.ToListAsync(TestContext.Current.CancellationToken), row =>
            row.ResultId == recoveryResultId && row.ReasonCode == "recovery-threshold-met" && row.FromStatus == ProbeStatus.Down && row.ToStatus == ProbeStatus.Up);
    }

    [Fact]
    public async Task HistoricalOnlyLineageAndCursorAndTimeOutcomesDoNotCreateOrMutateProjection()
    {
        await using var fixture = await CreateFixtureAsync(includeBinding: false);
        await AddLedgerAsync(fixture, fixture.Now, fixture.Now, 3, 0m);
        await using (var db = new EePulseDbContext(fixture.Options))
            await new ProbeResultStatusProcessor(db, new FixedClock(fixture.Now)).ProcessNextAsync(fixture.ProbeId, TestContext.Current.CancellationToken);
        await using (var verify = new EePulseDbContext(fixture.Options))
        {
            Assert.Empty(await verify.ProbeStatusProjections.ToListAsync(TestContext.Current.CancellationToken));
            var disposition = await verify.ProbeResultProcessingDispositions.SingleAsync(TestContext.Current.CancellationToken);
            Assert.Equal("policy-lineage-unresolved", disposition.ReasonCode);
            Assert.Empty(await verify.ProbeResultStatusTransitions.ToListAsync(TestContext.Current.CancellationToken));
            Assert.Empty(await verify.AvailabilityIncidents.ToListAsync(TestContext.Current.CancellationToken));
            Assert.Empty(await verify.IncidentLifecycleEvents.ToListAsync(TestContext.Current.CancellationToken));
            Assert.Empty(await verify.NotificationSuppressionContexts.ToListAsync(TestContext.Current.CancellationToken));
        }

        await using var timed = await CreateFixtureAsync();
        var boundary = await ReadBoundaryAsync(timed, timed.AgentId, 1);
        var latenessEqualityResultId = await AddLedgerAsync(timed, boundary.AddMinutes(-5), boundary, 3, 0m);
        var beyondLatenessResultId = await AddLedgerAsync(timed, boundary.AddMinutes(-5).Add(TimeSpan.FromMicroseconds(-1)), boundary, 3, 0m);
        var futureSkewEqualityResultId = await AddLedgerAsync(timed, boundary.AddSeconds(60), boundary, 3, 0m);
        var futureSkewResultId = await AddLedgerAsync(timed, boundary.AddSeconds(60).Add(TimeSpan.FromMicroseconds(1)), boundary, 3, 0m);
        var persistedBoundary = await ReadBoundaryAsync(timed, timed.AgentId, 1);
        var latenessEqualityLedger = await ReadLedgerAsync(timed, latenessEqualityResultId);
        var beyondLatenessLedger = await ReadLedgerAsync(timed, beyondLatenessResultId);
        var futureSkewEqualityLedger = await ReadLedgerAsync(timed, futureSkewEqualityResultId);
        var futureSkewLedger = await ReadLedgerAsync(timed, futureSkewResultId);

        Assert.Equal(persistedBoundary, latenessEqualityLedger.ReceivedAt);
        Assert.Equal(latenessEqualityLedger.ReceivedAt.AddMinutes(-5), latenessEqualityLedger.EndedAt);
        Assert.Equal(beyondLatenessLedger.ReceivedAt.AddMinutes(-5).Add(TimeSpan.FromMicroseconds(-1)), beyondLatenessLedger.EndedAt);
        Assert.Equal(futureSkewEqualityLedger.ReceivedAt.AddSeconds(60), futureSkewEqualityLedger.EndedAt);
        Assert.Equal(futureSkewLedger.ReceivedAt.AddSeconds(60).Add(TimeSpan.FromMicroseconds(1)), futureSkewLedger.EndedAt);
        await using (var db = new EePulseDbContext(timed.Options))
        {
            var processor = new ProbeResultStatusProcessor(db, new FixedClock(timed.Now));
            for (var index = 0; index < 4; index++) await processor.ProcessNextAsync(timed.ProbeId, TestContext.Current.CancellationToken);
        }
        await using var timeVerify = new EePulseDbContext(timed.Options);
        Assert.Equal(2, (await timeVerify.ProbeStatusProjections.SingleAsync(TestContext.Current.CancellationToken)).StateVersion);
        var timedDispositions = await timeVerify.ProbeResultProcessingDispositions.ToListAsync(TestContext.Current.CancellationToken);
        Assert.Contains(timedDispositions, row => row.ResultId == latenessEqualityResultId && row.Disposition == ProbeResultProcessingDispositionKind.StateDriving);
        Assert.Contains(timedDispositions, row => row.ResultId == beyondLatenessResultId && row.ReasonCode == "beyond-approved-lateness");
        Assert.Contains(timedDispositions, row => row.ResultId == futureSkewEqualityResultId && row.Disposition == ProbeResultProcessingDispositionKind.StateDriving);
        Assert.Contains(timedDispositions, row => row.ResultId == futureSkewResultId && row.ReasonCode == "future-or-skew-suspect");
        var timedTransitions = await timeVerify.ProbeResultStatusTransitions.ToListAsync(TestContext.Current.CancellationToken);
        Assert.Single(timedTransitions);
        Assert.DoesNotContain(timedTransitions, row => row.ResultId == beyondLatenessResultId || row.ResultId == futureSkewResultId);
        Assert.Empty(await timeVerify.AvailabilityIncidents.ToListAsync(TestContext.Current.CancellationToken));
        Assert.Empty(await timeVerify.IncidentLifecycleEvents.ToListAsync(TestContext.Current.CancellationToken));
        Assert.Empty(await timeVerify.NotificationSuppressionContexts.ToListAsync(TestContext.Current.CancellationToken));

        await using var cursor = await CreateFixtureAsync();
        await AddLedgerAsync(cursor, cursor.Now, cursor.Now, 3, 0m);
        await using (var db = new EePulseDbContext(cursor.Options))
            await new ProbeResultStatusProcessor(db, new FixedClock(cursor.Now)).ProcessNextAsync(cursor.ProbeId, TestContext.Current.CancellationToken);
        await AddLedgerAsync(cursor, cursor.Now.AddSeconds(-1), cursor.Now, 3, 0m);
        await using (var db = new EePulseDbContext(cursor.Options))
            await new ProbeResultStatusProcessor(db, new FixedClock(cursor.Now)).ProcessNextAsync(cursor.ProbeId, TestContext.Current.CancellationToken);
        await using (var cursorVerify = new EePulseDbContext(cursor.Options))
        {
            Assert.Contains(await cursorVerify.ProbeResultProcessingDispositions.ToListAsync(TestContext.Current.CancellationToken), row => row.ReasonCode == "late-order");
            Assert.Single(await cursorVerify.ProbeResultStatusTransitions.ToListAsync(TestContext.Current.CancellationToken));
            Assert.Empty(await cursorVerify.AvailabilityIncidents.ToListAsync(TestContext.Current.CancellationToken));
            Assert.Empty(await cursorVerify.IncidentLifecycleEvents.ToListAsync(TestContext.Current.CancellationToken));
            Assert.Empty(await cursorVerify.NotificationSuppressionContexts.ToListAsync(TestContext.Current.CancellationToken));
        }

        await using var noBoundary = await CreateFixtureAsync(includeBoundary: false);
        await AddLedgerAsync(noBoundary, noBoundary.Now, noBoundary.Now, 3, 0m);
        await using (var db = new EePulseDbContext(noBoundary.Options))
            await new ProbeResultStatusProcessor(db, new FixedClock(noBoundary.Now)).ProcessNextAsync(noBoundary.ProbeId, TestContext.Current.CancellationToken);
        await using (var boundaryVerify = new EePulseDbContext(noBoundary.Options))
        {
            Assert.Empty(await boundaryVerify.ProbeStatusProjections.ToListAsync(TestContext.Current.CancellationToken));
            Assert.Equal("policy-lineage-unresolved", (await boundaryVerify.ProbeResultProcessingDispositions.SingleAsync(TestContext.Current.CancellationToken)).ReasonCode);
            Assert.Empty(await boundaryVerify.ProbeResultStatusTransitions.ToListAsync(TestContext.Current.CancellationToken));
            Assert.Empty(await boundaryVerify.AvailabilityIncidents.ToListAsync(TestContext.Current.CancellationToken));
            Assert.Empty(await boundaryVerify.IncidentLifecycleEvents.ToListAsync(TestContext.Current.CancellationToken));
            Assert.Empty(await boundaryVerify.NotificationSuppressionContexts.ToListAsync(TestContext.Current.CancellationToken));
        }
    }

    private static async Task<Fixture> CreateFixtureAsync(bool includeBinding = true, bool includeBoundary = true)
    {
        var postgres = await PostgresTestDatabase.StartAsync(TestContext.Current.CancellationToken);
        var options = new DbContextOptionsBuilder<EePulseDbContext>().UseNpgsql(postgres.ConnectionString).Options;
        await using (var migration = new EePulseDbContext(options)) await migration.Database.MigrateAsync(TestContext.Current.CancellationToken);
        var now = new DateTimeOffset(2026, 8, 26, 12, 0, 0, TimeSpan.Zero);
        var site = new Site(Guid.NewGuid(), "P" + Guid.NewGuid().ToString("N")[..5], "Processor", "UTC", now);
        var group = new AgentGroup(Guid.NewGuid(), "Processor", null, now);
        var device = new Device(Guid.NewGuid(), site.Id, "Processor", "192.0.2.70", null, "Server", null, null, Criticality.Normal, [], now);
        var probe = new Probe(Guid.NewGuid(), device.Id, group.Id, 30, 2000, 3, 500, null, 2, 2);
        var agent = new EePulse.Domain.Agents.Agent(Guid.NewGuid(), group.Id, Guid.NewGuid(), "processor", "1.0.0", 20, now);
        var configuration = new AgentConfigurationSnapshot(group.Id, 1, "{}", new byte[32], now, null);
        var acknowledgement = new AgentConfigurationAcknowledgement(Guid.NewGuid(), agent.Id, 1, AgentAcknowledgementStatus.Applied, now, now, now, null, 1, 1);
        var policy = new ProbeStatusPolicySnapshot(Guid.NewGuid(), 1, 2, 2, 500, null, now);
        var boundary = new AgentConfigurationEffectiveBoundary(agent.Id, 1, acknowledgement.Id, AgentAcknowledgementStatus.Applied, acknowledgement.ReceivedAt);
        await using (var db = new EePulseDbContext(options))
        {
            db.AddRange(site, group, device, probe, agent, configuration, acknowledgement, policy);
            if (includeBoundary) db.Add(boundary);
            if (includeBinding) db.Add(new ProbeStatusPolicyBinding(probe.Id, 1, group.Id, policy.Id));
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }
        return new(postgres, postgres.ConnectionString, options, now, group.Id, probe.Id, agent.Id, policy.Id);
    }

    private static async Task<(Guid PolicyId, DateTimeOffset Boundary)> AddConfigurationVersionAsync(Fixture fixture, long version, int failureThreshold, int recoveryThreshold)
    {
        var at = fixture.Now.AddMinutes(1);
        var policy = new ProbeStatusPolicySnapshot(Guid.NewGuid(), (int)version, failureThreshold, recoveryThreshold, null, null, at);
        await using var db = new EePulseDbContext(fixture.Options);
        var configuration = new AgentConfigurationSnapshot(fixture.GroupId, version, "{}", new byte[32], at, null);
        var acknowledgement = new AgentConfigurationAcknowledgement(Guid.NewGuid(), fixture.AgentId, version, AgentAcknowledgementStatus.Applied, at, at, at, null, version, version);
        db.AddRange(configuration, acknowledgement, policy,
            new AgentConfigurationEffectiveBoundary(fixture.AgentId, version, acknowledgement.Id, AgentAcknowledgementStatus.Applied, acknowledgement.ReceivedAt),
            new ProbeStatusPolicyBinding(fixture.ProbeId, version, fixture.GroupId, policy.Id));
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        return (policy.Id, acknowledgement.ReceivedAt);
    }

    private static async Task<DateTimeOffset> ReadBoundaryAsync(Fixture fixture, Guid agentId, long version)
    {
        await using var db = new EePulseDbContext(fixture.Options);
        return await db.AgentConfigurationEffectiveBoundaries.Where(row => row.AgentId == agentId && row.ConfigurationVersion == version)
            .Select(row => row.AppliedAcknowledgementReceivedAt).SingleAsync(TestContext.Current.CancellationToken);
    }

    private static async Task<ProbeResultLedgerEntry> ReadLedgerAsync(Fixture fixture, Guid resultId)
    {
        await using var db = new EePulseDbContext(fixture.Options);
        return await db.ProbeResultLedgerEntries.AsNoTracking()
            .SingleAsync(row => row.AgentId == fixture.AgentId && row.ResultId == resultId, TestContext.Current.CancellationToken);
    }

    private static async Task<Guid> AddLedgerAsync(Fixture fixture, DateTimeOffset endedAt, DateTimeOffset receivedAt, int successes, decimal packetLossRatio, long configurationVersion = 1, Guid? resultId = null, decimal? averageRtt = 1m)
    {
        await using var db = new EePulseDbContext(fixture.Options);
        var id = resultId ?? Guid.NewGuid();
        db.Add(CreateLedger(fixture, endedAt, receivedAt, successes, packetLossRatio, configurationVersion, id, averageRtt));
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        return id;
    }

    private static ProbeResultLedgerEntry CreateLedger(Fixture fixture, DateTimeOffset endedAt, DateTimeOffset receivedAt, int successes, decimal packetLossRatio, long configurationVersion, Guid resultId, decimal? averageRtt) =>
        new(fixture.AgentId, resultId, fixture.ProbeId, configurationVersion,
            endedAt.AddSeconds(-1), endedAt, 3, successes, packetLossRatio, averageRtt, averageRtt, averageRtt, null, new byte[32], receivedAt);

    private static async Task WaitForUngrantedProbeLockAsync(NpgsqlConnection observer, int backendProcessId, Guid probeId, Task acquiring, CancellationToken cancellationToken)
    {
        var canonicalProbeId = probeId.ToString("D");
        while (true)
        {
            await using var command = new NpgsqlCommand("""
                SELECT EXISTS (
                    SELECT 1 FROM pg_locks
                    WHERE locktype = 'advisory' AND pid = @backendProcessId AND NOT granted
                      AND (lpad(to_hex(classid::bigint), 8, '0') || lpad(to_hex(objid::bigint), 8, '0')) = lpad(to_hex(hashtextextended(@probeId, 0)), 16, '0'))
                """, observer);
            command.Parameters.AddWithValue("backendProcessId", backendProcessId);
            command.Parameters.AddWithValue("probeId", canonicalProbeId);
            if ((bool)(await command.ExecuteScalarAsync(cancellationToken))!) return;
            if (acquiring.IsCompleted)
            {
                await acquiring;
                throw new Xunit.Sdk.XunitException("The ingestion transaction acquired the Probe lock before the processor released it.");
            }
            await Task.Delay(TimeSpan.FromMilliseconds(20), cancellationToken);
        }
    }

    private sealed record Fixture(IAsyncDisposable Postgres, string ConnectionString, DbContextOptions<EePulseDbContext> Options, DateTimeOffset Now, Guid GroupId, Guid ProbeId, Guid AgentId, Guid PolicyId) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => Postgres.DisposeAsync();
    }
    private sealed class FixedClock(DateTimeOffset now) : IUtcClock { public DateTimeOffset UtcNow => now; }
    private sealed class ThrowBeforeSaveInterceptor : SaveChangesInterceptor
    {
        public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result) => throw new InvalidOperationException("test pre-commit failure");
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default) => ValueTask.FromException<InterceptionResult<int>>(new InvalidOperationException("test pre-commit failure"));
    }
    private sealed class BlockingSaveInterceptor : SaveChangesInterceptor
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            Entered.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            return result;
        }
    }
}
