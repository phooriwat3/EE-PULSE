using EePulse.Domain.Agents;
using EePulse.Domain.Inventory;
using EePulse.Domain.Status;
using EePulse.Infrastructure.Persistence;
using EePulse.Infrastructure.Persistence.ProbeProcessing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using System.Runtime.ExceptionServices;

namespace EePulse.IntegrationTests;

public sealed class Wp06StatusProcessingPersistenceTests
{
    [Fact]
    public async Task Wp06PersistenceFoundationEnforcesLineageConstraintsAndAppendOnlyRows()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var postgres = await PostgresTestDatabase.StartAsync(ct);
        var options = new DbContextOptionsBuilder<EePulseDbContext>().UseNpgsql(postgres.ConnectionString).Options;
        await using var migrationContext = new EePulseDbContext(options);
        await migrationContext.Database.MigrateAsync(ct);

        var seed = await SeedAsync(options, ct);
        var policy = new ProbeStatusPolicySnapshot(Guid.NewGuid(), 1, 3, 2, 500, null, seed.Now);
        var projection = new ProbeStatusProjection(seed.ProbeId, ProbeStatus.Up, 0, 0, seed.Now, null, null, null);
        var binding = new ProbeStatusPolicyBinding(seed.ProbeId, 1, seed.AgentGroupId, policy.Id);
        var boundary = new AgentConfigurationEffectiveBoundary(seed.AgentId, 1, seed.AcknowledgementId,
            AgentAcknowledgementStatus.Applied, seed.AcknowledgementReceivedAt);
        var disposition = new ProbeResultProcessingDisposition(seed.AgentId, seed.ResultId, seed.ProbeId,
            seed.EventAt, ProbeResultProcessingDispositionKind.HistoricalOther, "policy-lineage-unresolved", null, null, seed.Now);
        var stateDrivingDisposition = new ProbeResultProcessingDisposition(seed.AgentId, seed.SecondResultId, seed.ProbeId,
            seed.SecondEventAt, ProbeResultProcessingDispositionKind.StateDriving, "state-driving", policy.Id, policy.PolicyVersion, seed.Now);
        var transition = new ProbeResultStatusTransition(seed.AgentId, seed.SecondResultId, seed.ProbeId,
            ProbeStatus.Unknown, ProbeStatus.Up, "bootstrap-success", seed.SecondEventAt, seed.Now,
            ProbeResultProcessingDispositionKind.StateDriving);

        await using (var write = new EePulseDbContext(options))
        {
            write.AddRange(policy, projection, binding, boundary, disposition, stateDrivingDisposition, transition);
            await write.SaveChangesAsync(ct);
        }

        var reusedProbe = new ProbeStatusPolicyBinding(seed.OtherProbeId, 1, seed.AgentGroupId, policy.Id);
        await using (var reuse = new EePulseDbContext(options))
        {
            reuse.Add(reusedProbe);
            await reuse.SaveChangesAsync(ct);
        }

        await using (var constraints = new EePulseDbContext(options))
        {
            AssertTransitionDispositionModel(constraints);
            await AssertProjectionConstraintsAsync(constraints, seed, ct);
            await AssertPolicyConstraintsAsync(constraints, seed.Now, ct);
            await AssertBindingConstraintsAsync(constraints, seed, policy.Id, ct);
            await AssertEffectiveBoundaryConstraintsAsync(constraints, seed, ct);
            await AssertDispositionConstraintsAsync(constraints, seed, policy.Id, ct);
            await AssertTransitionConstraintsAsync(constraints, seed, ct);
        }

        await AssertDbContextRejectsAppendOnlyMutationsAsync(options, seed, policy.Id, ct);

        await using (var directImmutable = new EePulseDbContext(options))
        {
            await Assert.ThrowsAsync<PostgresException>(() => directImmutable.Database.ExecuteSqlInterpolatedAsync($"UPDATE probe_status_policy_snapshots SET failure_threshold = {4} WHERE id = {policy.Id}", ct));
            await Assert.ThrowsAsync<PostgresException>(() => directImmutable.Database.ExecuteSqlInterpolatedAsync($"UPDATE probe_status_policy_bindings SET agent_group_id = {Guid.NewGuid()} WHERE probe_id = {seed.ProbeId} AND configuration_version = {1L}", ct));
            await Assert.ThrowsAsync<PostgresException>(() => directImmutable.Database.ExecuteSqlInterpolatedAsync($"UPDATE agent_configuration_effective_boundaries SET source_acknowledgement_status = {"Rejected"} WHERE agent_id = {seed.AgentId} AND configuration_version = {1L}", ct));
            await Assert.ThrowsAsync<PostgresException>(() => directImmutable.Database.ExecuteSqlInterpolatedAsync($"UPDATE probe_result_processing_dispositions SET reason_code = {"tampered"} WHERE agent_id = {seed.AgentId} AND result_id = {seed.ResultId}", ct));
            await Assert.ThrowsAsync<PostgresException>(() => directImmutable.Database.ExecuteSqlInterpolatedAsync($"UPDATE probe_result_status_transitions SET reason_code = {"tampered"} WHERE agent_id = {seed.AgentId} AND result_id = {seed.SecondResultId}", ct));
            await Assert.ThrowsAsync<PostgresException>(() => directImmutable.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM probe_status_policy_snapshots WHERE id = {policy.Id}", ct));
            await Assert.ThrowsAsync<PostgresException>(() => directImmutable.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM probe_status_policy_bindings WHERE probe_id = {seed.ProbeId} AND configuration_version = {1L}", ct));
            await Assert.ThrowsAsync<PostgresException>(() => directImmutable.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM agent_configuration_effective_boundaries WHERE agent_id = {seed.AgentId} AND configuration_version = {1L}", ct));
            await Assert.ThrowsAsync<PostgresException>(() => directImmutable.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM probe_result_processing_dispositions WHERE agent_id = {seed.AgentId} AND result_id = {seed.ResultId}", ct));
            await Assert.ThrowsAsync<PostgresException>(() => directImmutable.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM probe_result_status_transitions WHERE agent_id = {seed.AgentId} AND result_id = {seed.SecondResultId}", ct));
        }

        await using (var indexContext = new EePulseDbContext(options))
        {
            var indexes = await indexContext.Database.SqlQueryRaw<string>("SELECT indexname AS \"Value\" FROM pg_indexes WHERE schemaname = 'public'").ToListAsync(ct);
            Assert.Contains("ix_probe_result_ledger_state_order", indexes);
            Assert.Contains("ix_probe_result_status_transitions_probe_event", indexes);
        }
    }

    [Fact]
    public async Task St03aPersistenceEnforcesOpeningEvidenceUniquenessAndAppendOnlyHandoff()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var postgres = await PostgresTestDatabase.StartAsync(ct);
        var options = new DbContextOptionsBuilder<EePulseDbContext>().UseNpgsql(postgres.ConnectionString).Options;
        await using (var migrationContext = new EePulseDbContext(options)) await migrationContext.Database.MigrateAsync(ct);

        var seed = await SeedAsync(options, ct);
        var policy = new ProbeStatusPolicySnapshot(Guid.NewGuid(), 1, 3, 2, 500, null, seed.Now);
        var disposition = new ProbeResultProcessingDisposition(seed.AgentId, seed.SecondResultId, seed.ProbeId,
            seed.SecondEventAt, ProbeResultProcessingDispositionKind.StateDriving, "state-driving", policy.Id, policy.PolicyVersion, seed.Now);
        var transition = new ProbeResultStatusTransition(seed.AgentId, seed.SecondResultId, seed.ProbeId,
            ProbeStatus.Up, ProbeStatus.Down, "failure-threshold-met", seed.SecondEventAt, seed.Now,
            ProbeResultProcessingDispositionKind.StateDriving);
        var bootstrapDisposition = new ProbeResultProcessingDisposition(seed.AgentId, seed.ResultId, seed.ProbeId,
            seed.EventAt, ProbeResultProcessingDispositionKind.StateDriving, "state-driving", policy.Id, policy.PolicyVersion, seed.Now);
        var bootstrapTransition = new ProbeResultStatusTransition(seed.AgentId, seed.ResultId, seed.ProbeId,
            ProbeStatus.Unknown, ProbeStatus.Up, "bootstrap-success", seed.EventAt, seed.Now,
            ProbeResultProcessingDispositionKind.StateDriving);
        var mismatchResultId = Guid.NewGuid();
        var mismatchEventAt = seed.SecondEventAt.AddSeconds(1);
        var mismatchLedger = new ProbeResultLedgerEntry(seed.AgentId, mismatchResultId, seed.ProbeId, 1,
            mismatchEventAt.AddSeconds(-1), mismatchEventAt, 1, 0, 1m, null, null, null, "timeout", new byte[32], seed.Now);
        var mismatchDisposition = new ProbeResultProcessingDisposition(seed.AgentId, mismatchResultId, seed.ProbeId,
            mismatchEventAt, ProbeResultProcessingDispositionKind.StateDriving, "state-driving", policy.Id, policy.PolicyVersion, seed.Now);
        var mismatchTransition = new ProbeResultStatusTransition(seed.AgentId, mismatchResultId, seed.ProbeId,
            ProbeStatus.Degraded, ProbeStatus.Down, "failure-threshold-met", mismatchEventAt, seed.Now,
            ProbeResultProcessingDispositionKind.StateDriving);
        var unrelatedPolicy = new ProbeStatusPolicySnapshot(Guid.NewGuid(), 2, 3, 2, 500, null, seed.Now);
        var incident = new AvailabilityIncident(Guid.NewGuid(), seed.ProbeId, seed.SecondEventAt);
        var lifecycleEvent = new IncidentLifecycleEvent(Guid.NewGuid(), incident.Id, incident.ProbeId, seed.AgentId,
            seed.SecondResultId, ProbeStatus.Up, policy.Id, policy.PolicyVersion, seed.SecondEventAt);
        var context = NotificationSuppressionContext.ForAvailabilityDownOpened(lifecycleEvent, seed.Now);
        var projection = new ProbeStatusProjection(seed.ProbeId, ProbeStatus.Down, 1, 0, seed.SecondEventAt,
            seed.SecondEventAt, seed.AgentId, seed.SecondResultId, incident.Id);

        await using (var write = new EePulseDbContext(options))
        {
            write.AddRange(policy, unrelatedPolicy, disposition, transition, bootstrapDisposition, bootstrapTransition,
                mismatchLedger, mismatchDisposition, mismatchTransition, incident, lifecycleEvent, context, projection);
            await write.SaveChangesAsync(ct);
        }

        await using var direct = new EePulseDbContext(options);
        AssertSt03aModel(direct);
        await Assert.ThrowsAsync<PostgresException>(() => direct.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO availability_incidents (id, probe_id, rule_key, status, opened_at) VALUES ({Guid.NewGuid()}, {seed.ProbeId}, {"availability-down"}, {"Open"}, {seed.Now})", ct));
        await Assert.ThrowsAsync<PostgresException>(() => direct.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO incident_lifecycle_events (event_id, incident_id, probe_id, source_agent_id, source_result_id, source_from_status, source_to_status, source_reason_code, policy_snapshot_id, policy_version, lifecycle_event_type, lifecycle_event_key, processing_disposition, occurred_at) VALUES ({Guid.NewGuid()}, {incident.Id}, {seed.ProbeId}, {seed.AgentId}, {seed.ResultId}, {"Unknown"}, {"Up"}, {"bootstrap-success"}, {policy.Id}, {policy.PolicyVersion}, {"Opened"}, {"opened"}, {"StateDriving"}, {seed.Now})", ct));
        await Assert.ThrowsAsync<PostgresException>(() => direct.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO incident_lifecycle_events (event_id, incident_id, probe_id, source_agent_id, source_result_id, source_from_status, source_to_status, source_reason_code, policy_snapshot_id, policy_version, lifecycle_event_type, lifecycle_event_key, processing_disposition, occurred_at) VALUES ({Guid.NewGuid()}, {incident.Id}, {seed.ProbeId}, {seed.AgentId}, {mismatchResultId}, {"Degraded"}, {"Down"}, {"failure-threshold-met"}, {unrelatedPolicy.Id}, {unrelatedPolicy.PolicyVersion}, {"Opened"}, {"opened"}, {"StateDriving"}, {seed.Now})", ct));
        var resolvedIncidentId = Guid.NewGuid();
        await direct.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO availability_incidents (id, probe_id, rule_key, status, opened_at, resolved_at, resolved_by, resolution_note) VALUES ({resolvedIncidentId}, {seed.ProbeId}, {"availability-down"}, {"Resolved"}, {seed.Now}, {seed.Now}, {"system-policy"}, {"confirmed-recovery"})", ct);
        await Assert.ThrowsAsync<PostgresException>(() => direct.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO incident_lifecycle_events (event_id, incident_id, probe_id, source_agent_id, source_result_id, source_from_status, source_to_status, source_reason_code, policy_snapshot_id, policy_version, lifecycle_event_type, lifecycle_event_key, processing_disposition, occurred_at) VALUES ({Guid.NewGuid()}, {resolvedIncidentId}, {seed.ProbeId}, {seed.AgentId}, {seed.SecondResultId}, {"Up"}, {"Down"}, {"failure-threshold-met"}, {policy.Id}, {policy.PolicyVersion}, {"Opened"}, {"opened"}, {"StateDriving"}, {seed.Now})", ct));
        await Assert.ThrowsAsync<PostgresException>(() => direct.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO notification_suppression_contexts (event_id, incident_id, lifecycle_event_key, policy_version, eligibility, reason_code, evaluated_at) VALUES ({lifecycleEvent.EventId}, {incident.Id}, {"opened"}, {policy.PolicyVersion}, {"Eligible"}, {"availability-down"}, {seed.Now})", ct));
        await Assert.ThrowsAsync<PostgresException>(() => direct.Database.ExecuteSqlInterpolatedAsync($"UPDATE incident_lifecycle_events SET occurred_at = {seed.Now.AddSeconds(1)} WHERE event_id = {lifecycleEvent.EventId}", ct));
        await Assert.ThrowsAsync<PostgresException>(() => direct.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM incident_lifecycle_events WHERE event_id = {lifecycleEvent.EventId}", ct));
        await Assert.ThrowsAsync<PostgresException>(() => direct.Database.ExecuteSqlInterpolatedAsync($"UPDATE notification_suppression_contexts SET reason_code = {"tampered"} WHERE event_id = {lifecycleEvent.EventId}", ct));
        await Assert.ThrowsAsync<PostgresException>(() => direct.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM notification_suppression_contexts WHERE event_id = {lifecycleEvent.EventId}", ct));
    }

    [Fact]
    public async Task St04aPersistenceEnforcesResolvedEvidenceShapesAndAppendOnlyHandoff()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var postgres = await PostgresTestDatabase.StartAsync(ct);
        var options = new DbContextOptionsBuilder<EePulseDbContext>().UseNpgsql(postgres.ConnectionString).Options;
        await using (var migration = new EePulseDbContext(options)) await migration.Database.MigrateAsync(ct);

        var seed = await SeedAsync(options, ct);
        var policy = new ProbeStatusPolicySnapshot(Guid.NewGuid(), 1, 3, 2, 500, null, seed.Now);
        var unrelatedPolicy = new ProbeStatusPolicySnapshot(Guid.NewGuid(), 2, 3, 2, 500, null, seed.Now);
        await using (var write = new EePulseDbContext(options))
        {
            write.AddRange(policy, unrelatedPolicy);
            await write.SaveChangesAsync(ct);
        }

        var up = await AddLifecycleSourceAsync(options, seed, policy, ProbeStatus.Recovering, ProbeStatus.Up, "recovery-threshold-met", ct);
        var degraded = await AddLifecycleSourceAsync(options, seed, policy, ProbeStatus.Recovering, ProbeStatus.Degraded, "recovery-threshold-met", ct);
        var opened = await AddLifecycleSourceAsync(options, seed, policy, ProbeStatus.Up, ProbeStatus.Down, "failure-threshold-met", ct);
        var wrongFrom = await AddLifecycleSourceAsync(options, seed, policy, ProbeStatus.Down, ProbeStatus.Up, "recovery-threshold-met", ct);
        var wrongTo = await AddLifecycleSourceAsync(options, seed, policy, ProbeStatus.Recovering, ProbeStatus.Down, "recovery-threshold-met", ct);
        var wrongReason = await AddLifecycleSourceAsync(options, seed, policy, ProbeStatus.Recovering, ProbeStatus.Up, "failure-threshold-met", ct);
        var nonStateDriving = await AddHistoricalLifecycleSourceAsync(options, seed, ct);
        var mismatchPolicy = await AddLifecycleSourceAsync(options, seed, policy, ProbeStatus.Recovering, ProbeStatus.Up, "recovery-threshold-met", ct);
        var invalidPair = await AddLifecycleSourceAsync(options, seed, policy, ProbeStatus.Recovering, ProbeStatus.Up, "recovery-threshold-met", ct);
        var suppressionPair = await AddLifecycleSourceAsync(options, seed, policy, ProbeStatus.Recovering, ProbeStatus.Up, "recovery-threshold-met", ct);

        await using var direct = new EePulseDbContext(options);
        AssertSt04aModel(direct);
        var upEventId = Guid.NewGuid();
        await InsertLifecycleEventAsync(direct, upEventId, up, policy, "Resolved", "resolved", ProbeResultProcessingDispositionKind.StateDriving, ct);
        await InsertSuppressionContextAsync(direct, upEventId, up.IncidentId, "resolved", policy.PolicyVersion, "Eligible", "confirmed-recovery", ct);
        var degradedEventId = Guid.NewGuid();
        await InsertLifecycleEventAsync(direct, degradedEventId, degraded, policy, "Resolved", "resolved", ProbeResultProcessingDispositionKind.StateDriving, ct);
        await InsertSuppressionContextAsync(direct, degradedEventId, degraded.IncidentId, "resolved", policy.PolicyVersion, "Eligible", "confirmed-recovery", ct);
        var openedEventId = Guid.NewGuid();
        await InsertLifecycleEventAsync(direct, openedEventId, opened, policy, "Opened", "opened", ProbeResultProcessingDispositionKind.StateDriving, ct);
        await InsertSuppressionContextAsync(direct, openedEventId, opened.IncidentId, "opened", policy.PolicyVersion, "Eligible", "availability-down", ct);
        var suppressionEventId = Guid.NewGuid();
        await InsertLifecycleEventAsync(direct, suppressionEventId, suppressionPair, policy, "Resolved", "resolved", ProbeResultProcessingDispositionKind.StateDriving, ct);

        await AssertCheckViolationAsync(() => InsertLifecycleEventAsync(direct, Guid.NewGuid(), wrongFrom, policy, "Resolved", "resolved", ProbeResultProcessingDispositionKind.StateDriving, ct), "ck_incident_lifecycle_events_source");
        await AssertCheckViolationAsync(() => InsertLifecycleEventAsync(direct, Guid.NewGuid(), wrongTo, policy, "Resolved", "resolved", ProbeResultProcessingDispositionKind.StateDriving, ct), "ck_incident_lifecycle_events_source");
        await AssertCheckViolationAsync(() => InsertLifecycleEventAsync(direct, Guid.NewGuid(), wrongReason, policy, "Resolved", "resolved", ProbeResultProcessingDispositionKind.StateDriving, ct), "ck_incident_lifecycle_events_source");
        await AssertCheckViolationAsync(() => InsertLifecycleEventAsync(direct, Guid.NewGuid(), invalidPair, policy, "Opened", "resolved", ProbeResultProcessingDispositionKind.StateDriving, ct), "ck_incident_lifecycle_events_source");
        // The policy-lineage BEFORE INSERT trigger rejects this shape before PostgreSQL evaluates the defense-in-depth check.
        await AssertPolicyLineageViolationAsync(() => InsertLifecycleEventAsync(direct, Guid.NewGuid(), nonStateDriving, policy, "Resolved", "resolved", ProbeResultProcessingDispositionKind.HistoricalOther, ct));
        await AssertPolicyLineageViolationAsync(() => InsertLifecycleEventAsync(direct, Guid.NewGuid(), mismatchPolicy, unrelatedPolicy, "Resolved", "resolved", ProbeResultProcessingDispositionKind.StateDriving, ct));
        await AssertCheckViolationAsync(() => InsertSuppressionContextAsync(direct, suppressionEventId, suppressionPair.IncidentId, "resolved", policy.PolicyVersion, "Eligible", "availability-down", ct), "ck_notification_suppression_contexts_reason");
        await AssertAppendOnlyViolationAsync(() => direct.Database.ExecuteSqlInterpolatedAsync($"UPDATE incident_lifecycle_events SET occurred_at = {seed.Now.AddSeconds(1)} WHERE event_id = {upEventId}", ct), "incident_lifecycle_events");
        await AssertAppendOnlyViolationAsync(() => direct.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM incident_lifecycle_events WHERE event_id = {upEventId}", ct), "incident_lifecycle_events");
        await AssertAppendOnlyViolationAsync(() => direct.Database.ExecuteSqlInterpolatedAsync($"UPDATE notification_suppression_contexts SET reason_code = {"tampered"} WHERE event_id = {upEventId}", ct), "notification_suppression_contexts");
        await AssertAppendOnlyViolationAsync(() => direct.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM notification_suppression_contexts WHERE event_id = {upEventId}", ct), "notification_suppression_contexts");
    }

    [Fact]
    public async Task St05bPersistenceEnforcesRecoveryFailedOccurrencesWithoutWeakeningExistingHandoffs()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var postgres = await PostgresTestDatabase.StartAsync(ct);
        var options = new DbContextOptionsBuilder<EePulseDbContext>().UseNpgsql(postgres.ConnectionString).Options;
        await using (var migration = new EePulseDbContext(options)) await migration.Database.MigrateAsync(ct);

        var seed = await SeedAsync(options, ct);
        var policy = new ProbeStatusPolicySnapshot(Guid.NewGuid(), 1, 3, 2, 500, null, seed.Now);
        await using (var write = new EePulseDbContext(options))
        {
            write.Add(policy);
            await write.SaveChangesAsync(ct);
        }

        var occurrence = await AddLifecycleSourceAsync(options, seed, policy, ProbeStatus.Recovering, ProbeStatus.Down, "recovery-failed", ct);
        var wrongStatus = await AddLifecycleSourceAsync(options, seed, policy, ProbeStatus.Recovering, ProbeStatus.Up, "recovery-threshold-met", ct);
        var wrongReason = await AddLifecycleSourceAsync(options, seed, policy, ProbeStatus.Recovering, ProbeStatus.Down, "failure-threshold-met", ct);
        var opened = await AddLifecycleSourceAsync(options, seed, policy, ProbeStatus.Up, ProbeStatus.Down, "failure-threshold-met", ct);
        var resolved = await AddLifecycleSourceAsync(options, seed, policy, ProbeStatus.Recovering, ProbeStatus.Up, "recovery-threshold-met", ct);
        var nonStateDriving = await AddHistoricalLifecycleSourceAsync(options, seed, ct);

        await using var direct = new EePulseDbContext(options);
        var occurrenceKey = $"occurrence:{occurrence.ResultId:D}".ToLowerInvariant();
        var occurrenceEventId = Guid.NewGuid();
        await InsertLifecycleEventAsync(direct, occurrenceEventId, occurrence, policy, "Occurrence", occurrenceKey,
            ProbeResultProcessingDispositionKind.StateDriving, ct);
        await InsertSuppressionContextAsync(direct, occurrenceEventId, occurrence.IncidentId, occurrenceKey,
            policy.PolicyVersion, "Suppressed", "recovery-failed", ct, seed.Now);
        await InsertLifecycleEventAsync(direct, Guid.NewGuid(), opened, policy, "Opened", "opened",
            ProbeResultProcessingDispositionKind.StateDriving, ct);
        await InsertLifecycleEventAsync(direct, Guid.NewGuid(), resolved, policy, "Resolved", "resolved",
            ProbeResultProcessingDispositionKind.StateDriving, ct);

        var migratedIncident = await direct.AvailabilityIncidents.SingleAsync(row => row.Id == occurrence.IncidentId, ct);
        Assert.Equal(1, migratedIncident.OccurrenceCount);
        await using (var read = new EePulseDbContext(options))
        {
            var persistedEvent = await read.IncidentLifecycleEvents.AsNoTracking().SingleAsync(row => row.EventId == occurrenceEventId, ct);
            var persistedContext = await read.NotificationSuppressionContexts.AsNoTracking().SingleAsync(row => row.EventId == occurrenceEventId, ct);
            var sourceLedger = await read.ProbeResultLedgerEntries.AsNoTracking().SingleAsync(row => row.AgentId == occurrence.AgentId && row.ResultId == occurrence.ResultId, ct);
            Assert.Equal((occurrence.IncidentId, occurrence.ProbeId, occurrence.AgentId, occurrence.ResultId,
                    ProbeResultProcessingDispositionKind.StateDriving, policy.Id, policy.PolicyVersion, ProbeStatus.Recovering,
                    ProbeStatus.Down, "recovery-failed", IncidentLifecycleEventType.Occurrence, occurrenceKey, sourceLedger.EndedAt),
                (persistedEvent.IncidentId, persistedEvent.ProbeId, persistedEvent.SourceAgentId, persistedEvent.SourceResultId,
                    persistedEvent.ProcessingDisposition, persistedEvent.PolicySnapshotId, persistedEvent.PolicyVersion,
                    persistedEvent.SourceFromStatus, persistedEvent.SourceToStatus, persistedEvent.SourceReasonCode,
                    persistedEvent.LifecycleEventType, persistedEvent.LifecycleEventKey, persistedEvent.OccurredAt));
            Assert.Equal((occurrenceEventId, occurrence.IncidentId, occurrenceKey, policy.PolicyVersion,
                    NotificationSuppressionEligibility.Suppressed, "recovery-failed", sourceLedger.ReceivedAt),
                (persistedContext.EventId, persistedContext.IncidentId, persistedContext.LifecycleEventKey,
                    persistedContext.PolicyVersion, persistedContext.Eligibility, persistedContext.ReasonCode, persistedContext.EvaluatedAt));
        }
        await direct.Database.ExecuteSqlInterpolatedAsync($"UPDATE availability_incidents SET occurrence_count = {2} WHERE id = {occurrence.IncidentId}", ct);
        Assert.Equal(2, await direct.AvailabilityIncidents.Where(row => row.Id == occurrence.IncidentId).Select(row => row.OccurrenceCount).SingleAsync(ct));
        await AssertCheckViolationAsync(() => direct.Database.ExecuteSqlInterpolatedAsync($"UPDATE availability_incidents SET occurrence_count = {0} WHERE id = {occurrence.IncidentId}", ct), "ck_availability_incidents_occurrence_count");

        var badKeySource = await AddLifecycleSourceAsync(options, seed, policy, ProbeStatus.Recovering, ProbeStatus.Down, "recovery-failed", ct);
        await AssertCheckViolationAsync(() => InsertLifecycleEventAsync(direct, Guid.NewGuid(), badKeySource, policy, "Occurrence", $"occurrence:{Guid.NewGuid():D}", ProbeResultProcessingDispositionKind.StateDriving, ct), "ck_incident_lifecycle_events_key");
        await AssertCheckViolationAsync(() => InsertLifecycleEventAsync(direct, Guid.NewGuid(), wrongStatus, policy, "Occurrence", $"occurrence:{wrongStatus.ResultId:D}", ProbeResultProcessingDispositionKind.StateDriving, ct), "ck_incident_lifecycle_events_source");
        await AssertCheckViolationAsync(() => InsertLifecycleEventAsync(direct, Guid.NewGuid(), wrongReason, policy, "Occurrence", $"occurrence:{wrongReason.ResultId:D}", ProbeResultProcessingDispositionKind.StateDriving, ct), "ck_incident_lifecycle_events_source");
        // The policy-lineage BEFORE INSERT trigger runs before the defense-in-depth StateDriving check.
        await AssertPolicyLineageViolationAsync(() => InsertLifecycleEventAsync(direct, Guid.NewGuid(), nonStateDriving, policy, "Occurrence", $"occurrence:{nonStateDriving.ResultId:D}", ProbeResultProcessingDispositionKind.HistoricalOther, ct));
        var invalidDispositionSource = await AddLifecycleSourceAsync(options, seed, policy, ProbeStatus.Recovering, ProbeStatus.Down, "recovery-failed", ct);
        await AssertCheckViolationAsync(() => InsertLifecycleEventAsync(direct, Guid.NewGuid(), invalidDispositionSource, policy, "Occurrence", $"occurrence:{invalidDispositionSource.ResultId:D}", ProbeResultProcessingDispositionKind.HistoricalOther, ct), "ck_incident_lifecycle_events_disposition");

        var eligibilitySource = await AddLifecycleSourceAsync(options, seed, policy, ProbeStatus.Recovering, ProbeStatus.Down, "recovery-failed", ct);
        var eligibilityEventId = Guid.NewGuid();
        var eligibilityKey = $"occurrence:{eligibilitySource.ResultId:D}".ToLowerInvariant();
        await InsertLifecycleEventAsync(direct, eligibilityEventId, eligibilitySource, policy, "Occurrence", eligibilityKey, ProbeResultProcessingDispositionKind.StateDriving, ct);
        await AssertCheckViolationAsync(() => InsertSuppressionContextAsync(direct, eligibilityEventId, eligibilitySource.IncidentId, eligibilityKey, policy.PolicyVersion, "Eligible", "recovery-failed", ct), "ck_notification_suppression_contexts_eligibility");

        var reasonSource = await AddLifecycleSourceAsync(options, seed, policy, ProbeStatus.Recovering, ProbeStatus.Down, "recovery-failed", ct);
        var reasonEventId = Guid.NewGuid();
        var reasonKey = $"occurrence:{reasonSource.ResultId:D}".ToLowerInvariant();
        await InsertLifecycleEventAsync(direct, reasonEventId, reasonSource, policy, "Occurrence", reasonKey, ProbeResultProcessingDispositionKind.StateDriving, ct);
        await AssertCheckViolationAsync(() => InsertSuppressionContextAsync(direct, reasonEventId, reasonSource.IncidentId, reasonKey, policy.PolicyVersion, "Suppressed", "availability-down", ct), "ck_notification_suppression_contexts_reason");

        var duplicateSourceIncidentId = Guid.NewGuid();
        await direct.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO availability_incidents (id, probe_id, rule_key, status, opened_at, resolved_at, resolved_by, resolution_note) VALUES ({duplicateSourceIncidentId}, {occurrence.ProbeId}, {"availability-down"}, {"Resolved"}, {occurrence.EventAt}, {occurrence.EventAt}, {"system-policy"}, {"confirmed-recovery"})", ct);
        var duplicateSourceWithDistinctIncident = occurrence with { IncidentId = duplicateSourceIncidentId };
        // The distinct incident makes the incident/key/policy alternate key valid while retaining the duplicated source identity.
        await AssertUniqueViolationAsync(() => InsertLifecycleEventAsync(direct, Guid.NewGuid(), duplicateSourceWithDistinctIncident, policy, "Occurrence", occurrenceKey, ProbeResultProcessingDispositionKind.StateDriving, ct), "ux_incident_lifecycle_events_opening_source");
        await AssertAppendOnlyViolationAsync(() => direct.Database.ExecuteSqlInterpolatedAsync($"UPDATE incident_lifecycle_events SET occurred_at = {seed.Now.AddSeconds(1)} WHERE event_id = {occurrenceEventId}", ct), "incident_lifecycle_events");
        await AssertAppendOnlyViolationAsync(() => direct.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM incident_lifecycle_events WHERE event_id = {occurrenceEventId}", ct), "incident_lifecycle_events");
        await AssertAppendOnlyViolationAsync(() => direct.Database.ExecuteSqlInterpolatedAsync($"UPDATE notification_suppression_contexts SET reason_code = {"tampered"} WHERE event_id = {occurrenceEventId}", ct), "notification_suppression_contexts");
        await AssertAppendOnlyViolationAsync(() => direct.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM notification_suppression_contexts WHERE event_id = {occurrenceEventId}", ct), "notification_suppression_contexts");
    }

    [Fact]
    public async Task St05bMigrationBackfillsPreExistingIncidentsToOne()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var postgres = await PostgresTestDatabase.StartAsync(ct);
        var options = new DbContextOptionsBuilder<EePulseDbContext>().UseNpgsql(postgres.ConnectionString).Options;
        await using var migrationContext = new EePulseDbContext(options);
        var migrator = migrationContext.GetService<IMigrator>();
        await migrator.MigrateAsync("20260826075019_WP06St04aIncidentResolution", ct);

        var seed = await SeedAsync(options, ct);
        var incidentId = Guid.NewGuid();
        await using (var beforeSt05b = new EePulseDbContext(options))
        {
            await beforeSt05b.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO availability_incidents (id, probe_id, rule_key, status, opened_at) VALUES ({incidentId}, {seed.ProbeId}, {"availability-down"}, {"Open"}, {seed.Now})", ct);
        }

        await migrator.MigrateAsync("20260826090858_WP06St05bRecoveryFailedOccurrences", ct);
        await using var afterSt05b = new EePulseDbContext(options);
        var incident = await afterSt05b.AvailabilityIncidents.AsNoTracking().SingleAsync(row => row.Id == incidentId, ct);
        Assert.Equal(1, incident.OccurrenceCount);
    }

    private static void AssertSt03aModel(EePulseDbContext db)
    {
        var model = db.GetService<IDesignTimeModel>().Model;
        var incident = model.FindEntityType(typeof(AvailabilityIncident))!;
        Assert.Contains(incident.GetIndexes(), index => index.IsUnique && index.GetDatabaseName() == "ux_availability_incidents_active_probe_rule" && index.GetFilter() == "status IN ('Open', 'Acknowledged')");

        var lifecycleEvent = model.FindEntityType(typeof(IncidentLifecycleEvent))!;
        Assert.Contains(lifecycleEvent.GetForeignKeys(), foreignKey =>
            foreignKey.Properties.Select(property => property.Name).SequenceEqual([nameof(IncidentLifecycleEvent.SourceAgentId), nameof(IncidentLifecycleEvent.SourceResultId), nameof(IncidentLifecycleEvent.ProbeId), nameof(IncidentLifecycleEvent.SourceFromStatus), nameof(IncidentLifecycleEvent.SourceToStatus), nameof(IncidentLifecycleEvent.SourceReasonCode), nameof(IncidentLifecycleEvent.ProcessingDisposition)]) &&
            foreignKey.PrincipalEntityType.ClrType == typeof(ProbeResultStatusTransition) && foreignKey.DeleteBehavior == DeleteBehavior.Restrict);
        Assert.Contains(lifecycleEvent.GetForeignKeys(), foreignKey =>
            foreignKey.Properties.Select(property => property.Name).SequenceEqual([nameof(IncidentLifecycleEvent.SourceAgentId), nameof(IncidentLifecycleEvent.SourceResultId), nameof(IncidentLifecycleEvent.ProcessingDisposition)]) &&
            foreignKey.PrincipalEntityType.ClrType == typeof(ProbeResultProcessingDisposition) && foreignKey.DeleteBehavior == DeleteBehavior.Restrict);

        var context = model.FindEntityType(typeof(NotificationSuppressionContext))!;
        Assert.Contains(context.GetForeignKeys(), foreignKey => foreignKey.PrincipalEntityType.ClrType == typeof(IncidentLifecycleEvent) && foreignKey.DeleteBehavior == DeleteBehavior.Restrict);
    }

    private static void AssertSt04aModel(EePulseDbContext db)
    {
        var model = db.GetService<IDesignTimeModel>().Model;
        var lifecycleEvent = model.FindEntityType(typeof(IncidentLifecycleEvent))!;
        Assert.Contains(lifecycleEvent.GetCheckConstraints(), constraint =>
            constraint.Name == "ck_incident_lifecycle_events_disposition" &&
            constraint.Sql == "processing_disposition = 'StateDriving'");
        Assert.Contains(lifecycleEvent.GetCheckConstraints(), constraint =>
            constraint.Name == "ck_incident_lifecycle_events_source" &&
            constraint.Sql!.Contains("lifecycle_event_type = 'Occurrence'", StringComparison.Ordinal));

        var incident = model.FindEntityType(typeof(AvailabilityIncident))!;
        Assert.Contains(incident.GetCheckConstraints(), constraint =>
            constraint.Name == "ck_availability_incidents_occurrence_count" && constraint.Sql == "occurrence_count >= 1");
    }

    private static async Task<LifecycleSource> AddLifecycleSourceAsync(
        DbContextOptions<EePulseDbContext> options,
        Seed seed,
        ProbeStatusPolicySnapshot policy,
        ProbeStatus fromStatus,
        ProbeStatus toStatus,
        string reasonCode,
        CancellationToken ct)
    {
        var resultId = Guid.NewGuid();
        var eventAt = seed.Now;
        var ledger = new ProbeResultLedgerEntry(seed.AgentId, resultId, seed.ProbeId, 1, eventAt.AddSeconds(-1), eventAt,
            1, 1, 0m, 1m, 1m, 1m, null, new byte[32], seed.Now);
        var disposition = new ProbeResultProcessingDisposition(seed.AgentId, resultId, seed.ProbeId, eventAt,
            ProbeResultProcessingDispositionKind.StateDriving, "state-driving", policy.Id, policy.PolicyVersion, seed.Now);
        var transition = new ProbeResultStatusTransition(seed.AgentId, resultId, seed.ProbeId, fromStatus, toStatus, reasonCode,
            eventAt, seed.Now, ProbeResultProcessingDispositionKind.StateDriving);
        var incident = new AvailabilityIncident(Guid.NewGuid(), seed.ProbeId, eventAt);
        incident.ResolveForConfirmedRecovery(eventAt);
        await using var write = new EePulseDbContext(options);
        write.AddRange(ledger, disposition, transition, incident);
        await write.SaveChangesAsync(ct);
        return new LifecycleSource(incident.Id, seed.ProbeId, seed.AgentId, resultId, fromStatus, toStatus, reasonCode, eventAt);
    }

    private static async Task<LifecycleSource> AddHistoricalLifecycleSourceAsync(
        DbContextOptions<EePulseDbContext> options,
        Seed seed,
        CancellationToken ct)
    {
        var resultId = Guid.NewGuid();
        var ledger = new ProbeResultLedgerEntry(seed.AgentId, resultId, seed.ProbeId, 1, seed.Now.AddSeconds(-1), seed.Now,
            1, 1, 0m, 1m, 1m, 1m, null, new byte[32], seed.Now);
        var disposition = new ProbeResultProcessingDisposition(seed.AgentId, resultId, seed.ProbeId, seed.Now,
            ProbeResultProcessingDispositionKind.HistoricalOther, "policy-lineage-unresolved", null, null, seed.Now);
        var incident = new AvailabilityIncident(Guid.NewGuid(), seed.ProbeId, seed.Now);
        incident.ResolveForConfirmedRecovery(seed.Now);
        await using var write = new EePulseDbContext(options);
        write.AddRange(ledger, disposition, incident);
        await write.SaveChangesAsync(ct);
        return new LifecycleSource(incident.Id, seed.ProbeId, seed.AgentId, resultId, ProbeStatus.Recovering, ProbeStatus.Up,
            "recovery-threshold-met", seed.Now);
    }

    private static async Task AssertCheckViolationAsync(Func<Task> action, string constraintName)
    {
        var exception = await Assert.ThrowsAsync<PostgresException>(action);
        Assert.Equal(PostgresErrorCodes.CheckViolation, exception.SqlState);
        Assert.Equal(constraintName, exception.ConstraintName);
    }

    private static async Task AssertPolicyLineageViolationAsync(Func<Task> action)
    {
        var exception = await Assert.ThrowsAsync<PostgresException>(action);
        Assert.Equal(PostgresErrorCodes.RaiseException, exception.SqlState);
        Assert.Equal("WP-06 incident lifecycle event policy lineage is invalid", exception.MessageText);
        Assert.Null(exception.ConstraintName);
    }

    private static async Task AssertUniqueViolationAsync(Func<Task> action, string constraintName)
    {
        var exception = await Assert.ThrowsAsync<PostgresException>(action);
        Assert.Equal(PostgresErrorCodes.UniqueViolation, exception.SqlState);
        Assert.Equal(constraintName, exception.ConstraintName);
    }

    private static async Task AssertAppendOnlyViolationAsync(Func<Task> action, string tableName)
    {
        var exception = await Assert.ThrowsAsync<PostgresException>(action);
        Assert.Equal(PostgresErrorCodes.RaiseException, exception.SqlState);
        Assert.Equal($"WP-06 append-only table {tableName} cannot be modified or deleted", exception.MessageText);
        Assert.Null(exception.ConstraintName);
    }

    private static async Task AssertTriggerViolationAsync(Func<Task> action, string message = "WP-06 freshness expiry cause source is invalid")
    {
        var exception = await Assert.ThrowsAsync<PostgresException>(action);
        Assert.Equal(PostgresErrorCodes.RaiseException, exception.SqlState);
        Assert.Equal(message, exception.MessageText);
        Assert.Null(exception.ConstraintName);
    }

    private static Task RollbackIfActiveAsync(EePulseDbContext db, IDbContextTransaction transaction) =>
        db.Database.CurrentTransaction is not null && db.Database.GetDbConnection().State == System.Data.ConnectionState.Open
            ? transaction.RollbackAsync(CancellationToken.None)
            : Task.CompletedTask;

    private static Task<int> InsertFreshnessCauseAsync(EePulseDbContext db, FreshnessCause cause, DateTimeOffset requestedAt, CancellationToken ct) =>
        db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO probe_freshness_expiry_causes (cause_id, probe_id, cause_type, source_agent_id, source_result_id, source_cursor_event_at, source_last_fresh_event_at, source_configuration_version, source_agent_group_id, source_disposition, policy_snapshot_id, policy_version, freshness_interval_seconds, freshness_grace_seconds, due_at, requested_at) VALUES ({cause.CauseId}, {cause.ProbeId}, {"ResultFreshnessExpiry"}, {cause.SourceAgentId}, {cause.SourceResultId}, {cause.SourceCursorEventAt}, {cause.SourceLastFreshEventAt}, {cause.SourceConfigurationVersion}, {cause.SourceAgentGroupId}, {"StateDriving"}, {cause.PolicySnapshotId}, {cause.PolicyVersion}, {cause.FreshnessIntervalSeconds}, {cause.FreshnessGraceSeconds}, {cause.DueAt}, {requestedAt})", ct);

    private static Task<int> InsertExpiryDispositionAsync(EePulseDbContext db, Guid causeId, Guid probeId,
        Guid policySnapshotId, int policyVersion, string outcome, string reasonCode,
        DateTimeOffset expiryCutoffReceivedAt, DateTimeOffset? appliedAt, CancellationToken ct) =>
        db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO probe_freshness_expiry_cause_dispositions (cause_id, probe_id, policy_snapshot_id, policy_version, outcome, reason_code, expiry_cutoff_received_at, applied_at) VALUES ({causeId}, {probeId}, {policySnapshotId}, {policyVersion}, {outcome}, {reasonCode}, {expiryCutoffReceivedAt}, {appliedAt})", ct);

    private static Task<int> InsertExpiryTransitionAsync(EePulseDbContext db, Guid causeId, Guid probeId,
        Guid policySnapshotId, int policyVersion, string dispositionOutcome, string fromVisibleStatus,
        string toVisibleStatus, string reasonCode, DateTimeOffset appliedAt, CancellationToken ct) =>
        db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO probe_freshness_expiry_cause_transitions (cause_id, probe_id, policy_snapshot_id, policy_version, disposition_outcome, from_visible_status, to_visible_status, reason_code, applied_at) VALUES ({causeId}, {probeId}, {policySnapshotId}, {policyVersion}, {dispositionOutcome}, {fromVisibleStatus}, {toVisibleStatus}, {reasonCode}, {appliedAt})", ct);

    private static async Task<FreshnessCause> AddAdditionalFreshnessCauseAsync(DbContextOptions<EePulseDbContext> options,
        Seed seed, ProbeStatusPolicySnapshot policy, int secondsAfterSource, CancellationToken ct)
    {
        var resultId = Guid.NewGuid();
        var eventAt = seed.EventAt.AddSeconds(secondsAfterSource);
        await using (var write = new EePulseDbContext(options))
        {
            write.Add(new ProbeResultLedgerEntry(seed.AgentId, resultId, seed.ProbeId, 1, eventAt.AddSeconds(-1), eventAt,
                1, 1, 0m, 1m, 1m, 1m, null, new byte[32], seed.Now));
            write.Add(new ProbeResultProcessingDisposition(seed.AgentId, resultId, seed.ProbeId, eventAt,
                ProbeResultProcessingDispositionKind.StateDriving, "state-driving", policy.Id, policy.PolicyVersion, seed.Now));
            await write.SaveChangesAsync(ct);
            await write.Database.ExecuteSqlInterpolatedAsync($"UPDATE probe_status_projections SET watermark_event_at = {eventAt}, watermark_agent_id = {seed.AgentId}, watermark_result_id = {resultId}, last_fresh_event_at = {eventAt} WHERE probe_id = {seed.ProbeId}", ct);
        }

        var cause = new FreshnessCause(Guid.NewGuid(), seed.ProbeId, seed.AgentId, resultId, eventAt, eventAt,
            1, seed.AgentGroupId, policy.Id, policy.PolicyVersion, 30, 60, eventAt.AddSeconds(60));
        await using var direct = new EePulseDbContext(options);
        await InsertFreshnessCauseAsync(direct, cause, DateTimeOffset.UnixEpoch, ct);
        return cause;
    }

    private static async Task<FreshnessSource> AddFreshnessCauseSourceAsync(DbContextOptions<EePulseDbContext> options, Seed seed, CancellationToken ct)
    {
        var policy = new ProbeStatusPolicySnapshot(Guid.NewGuid(), 1, 3, 2, 500, null, seed.Now);
        var otherPolicy = new ProbeStatusPolicySnapshot(Guid.NewGuid(), 1, 3, 2, 500, null, seed.Now);
        var otherVersionPolicy = new ProbeStatusPolicySnapshot(Guid.NewGuid(), 2, 3, 2, 500, null, seed.Now);
        var disposition = new ProbeResultProcessingDisposition(seed.AgentId, seed.ResultId, seed.ProbeId, seed.EventAt,
            ProbeResultProcessingDispositionKind.StateDriving, "state-driving", policy.Id, policy.PolicyVersion, seed.Now);
        var projection = new ProbeStatusProjection(seed.ProbeId, ProbeStatus.Up, 0, 1, seed.EventAt, seed.EventAt, seed.AgentId, seed.ResultId, null);
        await using var write = new EePulseDbContext(options);
        write.AddRange(policy, otherPolicy, otherVersionPolicy, disposition, projection);
        await write.SaveChangesAsync(ct);
        var cause = new FreshnessCause(Guid.NewGuid(), seed.ProbeId, seed.AgentId, seed.ResultId, seed.EventAt, seed.EventAt,
            1, seed.AgentGroupId, policy.Id, policy.PolicyVersion, 30, 60, seed.EventAt.AddSeconds(60));
        return new FreshnessSource(cause, policy, otherPolicy, otherVersionPolicy);
    }

    private static string FreshnessPayload(Guid probeId, int intervalSeconds) =>
        $$"""{"probes":[{"probeId":"{{probeId:D}}","intervalSeconds":{{intervalSeconds}}}]}""";

    private static async Task WaitForCauseAdvisoryLockAsync(NpgsqlConnection observer, int backendProcessId, Guid probeId, Task insert, CancellationToken ct)
    {
        var canonicalProbeId = probeId.ToString("D");
        while (true)
        {
            await using var command = new NpgsqlCommand("""
                SELECT EXISTS (SELECT 1 FROM pg_locks
                WHERE locktype = 'advisory' AND pid = @backendProcessId AND NOT granted
                  AND (lpad(to_hex(classid::bigint), 8, '0') || lpad(to_hex(objid::bigint), 8, '0')) = lpad(to_hex(hashtextextended(@probeId, 0)), 16, '0'))
                """, observer);
            command.Parameters.AddWithValue("backendProcessId", backendProcessId);
            command.Parameters.AddWithValue("probeId", canonicalProbeId);
            if ((bool)(await command.ExecuteScalarAsync(ct))!) return;
            if (insert.IsCompleted)
            {
                await insert;
                throw new Xunit.Sdk.XunitException("Cause insert did not wait for the canonical Probe advisory lock.");
            }
            await Task.Delay(TimeSpan.FromMilliseconds(20), ct);
        }
    }

    private static Task<int> InsertLifecycleEventAsync(EePulseDbContext db, Guid eventId, LifecycleSource source,
        ProbeStatusPolicySnapshot policy, string lifecycleEventType, string lifecycleEventKey,
        ProbeResultProcessingDispositionKind disposition, CancellationToken ct) =>
        db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO incident_lifecycle_events (event_id, incident_id, probe_id, source_agent_id, source_result_id, source_from_status, source_to_status, source_reason_code, policy_snapshot_id, policy_version, lifecycle_event_type, lifecycle_event_key, processing_disposition, occurred_at) VALUES ({eventId}, {source.IncidentId}, {source.ProbeId}, {source.AgentId}, {source.ResultId}, {source.FromStatus.ToString()}, {source.ToStatus.ToString()}, {source.ReasonCode}, {policy.Id}, {policy.PolicyVersion}, {lifecycleEventType}, {lifecycleEventKey}, {disposition.ToString()}, {source.EventAt})", ct);

    private static Task<int> InsertSuppressionContextAsync(EePulseDbContext db, Guid eventId, Guid incidentId,
        string lifecycleEventKey, int policyVersion, string eligibility, string reasonCode, CancellationToken ct, DateTimeOffset? evaluatedAt = null)
    {
        var observedAt = evaluatedAt ?? DateTimeOffset.UnixEpoch;
        return db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO notification_suppression_contexts (event_id, incident_id, lifecycle_event_key, policy_version, eligibility, reason_code, evaluated_at) VALUES ({eventId}, {incidentId}, {lifecycleEventKey}, {policyVersion}, {eligibility}, {reasonCode}, {observedAt})", ct);
    }

    private static async Task AssertProjectionConstraintsAsync(EePulseDbContext db, Seed seed, CancellationToken ct)
    {
        await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO probe_status_projections (probe_id, underlying_status, consecutive_failure_count, consecutive_success_count, state_version) VALUES ({seed.OtherProbeId}, {"Invalid"}, {0}, {0}, {0L})", ct));
        await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO probe_status_projections (probe_id, underlying_status, consecutive_failure_count, consecutive_success_count, state_version) VALUES ({seed.OtherProbeId}, {"Up"}, {-1}, {0}, {0L})", ct));
        await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO probe_status_projections (probe_id, underlying_status, consecutive_failure_count, consecutive_success_count, state_version) VALUES ({seed.OtherProbeId}, {"Up"}, {1}, {1}, {0L})", ct));
        await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO probe_status_projections (probe_id, underlying_status, consecutive_failure_count, consecutive_success_count, state_version) VALUES ({seed.OtherProbeId}, {"Down"}, {0}, {0}, {0L})", ct));
        await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO probe_status_projections (probe_id, underlying_status, consecutive_failure_count, consecutive_success_count, state_version) VALUES ({seed.OtherProbeId}, {"Recovering"}, {1}, {0}, {0L})", ct));
        await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO probe_status_projections (probe_id, underlying_status, consecutive_failure_count, consecutive_success_count, state_version) VALUES ({seed.OtherProbeId}, {"Up"}, {0}, {0}, {-1L})", ct));
        await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO probe_status_projections (probe_id, underlying_status, consecutive_failure_count, consecutive_success_count, watermark_event_at, watermark_agent_id, state_version) VALUES ({seed.OtherProbeId}, {"Up"}, {0}, {0}, {seed.Now}, {seed.AgentId}, {0L})", ct));
    }

    private static async Task AssertPolicyConstraintsAsync(EePulseDbContext db, DateTimeOffset now, CancellationToken ct)
    {
        await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO probe_status_policy_snapshots (id, policy_version, failure_threshold, recovery_threshold, approved_lateness_seconds, approved_future_skew_seconds, created_at) VALUES ({Guid.NewGuid()}, {1}, {0}, {2}, {300}, {60}, {now})", ct));
        await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO probe_status_policy_snapshots (id, policy_version, failure_threshold, recovery_threshold, warning_rtt_milliseconds, approved_lateness_seconds, approved_future_skew_seconds, created_at) VALUES ({Guid.NewGuid()}, {1}, {3}, {2}, {0}, {300}, {60}, {now})", ct));
        await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO probe_status_policy_snapshots (id, policy_version, failure_threshold, recovery_threshold, warning_packet_loss_ratio, approved_lateness_seconds, approved_future_skew_seconds, created_at) VALUES ({Guid.NewGuid()}, {1}, {3}, {2}, {1.01m}, {300}, {60}, {now})", ct));
        await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO probe_status_policy_snapshots (id, policy_version, failure_threshold, recovery_threshold, approved_lateness_seconds, approved_future_skew_seconds, created_at) VALUES ({Guid.NewGuid()}, {1}, {3}, {2}, {299}, {60}, {now})", ct));
    }

    private static async Task AssertBindingConstraintsAsync(EePulseDbContext db, Seed seed, Guid policyId, CancellationToken ct)
    {
        await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO probe_status_policy_bindings (probe_id, configuration_version, agent_group_id, policy_snapshot_id) VALUES ({seed.ProbeId}, {1L}, {seed.AgentGroupId}, {policyId})", ct));
        await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO probe_status_policy_bindings (probe_id, configuration_version, agent_group_id, policy_snapshot_id) VALUES ({seed.OtherProbeId}, {2L}, {seed.AgentGroupId}, {policyId})", ct));
    }

    private static async Task AssertEffectiveBoundaryConstraintsAsync(EePulseDbContext db, Seed seed, CancellationToken ct)
    {
        await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO agent_configuration_effective_boundaries (agent_id, configuration_version, source_acknowledgement_id, source_acknowledgement_status, applied_acknowledgement_received_at) VALUES ({seed.SecondaryAgentId}, {1L}, {seed.AcknowledgementId}, {"Applied"}, {seed.AcknowledgementReceivedAt})", ct));
        await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO agent_configuration_effective_boundaries (agent_id, configuration_version, source_acknowledgement_id, source_acknowledgement_status, applied_acknowledgement_received_at) VALUES ({seed.SecondaryAgentId}, {2L}, {seed.SecondaryAcknowledgementId}, {"Applied"}, {seed.SecondaryAcknowledgementReceivedAt})", ct));
        await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO agent_configuration_effective_boundaries (agent_id, configuration_version, source_acknowledgement_id, source_acknowledgement_status, applied_acknowledgement_received_at) VALUES ({seed.SecondaryAgentId}, {1L}, {seed.SecondaryAcknowledgementId}, {"Rejected"}, {seed.SecondaryAcknowledgementReceivedAt})", ct));
        await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO agent_configuration_effective_boundaries (agent_id, configuration_version, source_acknowledgement_id, source_acknowledgement_status, applied_acknowledgement_received_at) VALUES ({seed.SecondaryAgentId}, {1L}, {seed.SecondaryAcknowledgementId}, {"Applied"}, {seed.SecondaryAcknowledgementReceivedAt.AddTicks(10)})", ct));
    }

    private static async Task AssertDispositionConstraintsAsync(EePulseDbContext db, Seed seed, Guid policyId, CancellationToken ct)
    {
        await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO probe_result_processing_dispositions (agent_id, result_id, probe_id, event_at, disposition, reason_code, decided_at) VALUES ({seed.AgentId}, {seed.SecondResultId}, {seed.ProbeId}, {seed.SecondEventAt.AddSeconds(1)}, {"HistoricalOther"}, {"late-order"}, {seed.Now})", ct));
        await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO probe_result_processing_dispositions (agent_id, result_id, probe_id, event_at, disposition, reason_code, decided_at) VALUES ({seed.AgentId}, {seed.SecondResultId}, {seed.OtherProbeId}, {seed.SecondEventAt}, {"HistoricalOther"}, {"late-order"}, {seed.Now})", ct));
        await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO probe_result_processing_dispositions (agent_id, result_id, probe_id, event_at, disposition, reason_code, decided_at) VALUES ({seed.AgentId}, {seed.SecondResultId}, {seed.ProbeId}, {seed.SecondEventAt}, {"HistoricalOther"}, {""}, {seed.Now})", ct));
        await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO probe_result_processing_dispositions (agent_id, result_id, probe_id, event_at, disposition, reason_code, decided_at) VALUES ({seed.AgentId}, {seed.SecondResultId}, {seed.ProbeId}, {seed.SecondEventAt}, {"StateDriving"}, {"state-driving"}, {seed.Now})", ct));
        await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO probe_result_processing_dispositions (agent_id, result_id, probe_id, event_at, disposition, reason_code, resolved_policy_snapshot_id, decided_at) VALUES ({seed.AgentId}, {seed.SecondResultId}, {seed.ProbeId}, {seed.SecondEventAt}, {"HistoricalOther"}, {"policy-lineage-unresolved"}, {policyId}, {seed.Now})", ct));
        await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO probe_result_processing_dispositions (agent_id, result_id, probe_id, event_at, disposition, reason_code, resolved_policy_snapshot_id, resolved_policy_version, decided_at) VALUES ({seed.AgentId}, {seed.SecondResultId}, {seed.ProbeId}, {seed.SecondEventAt}, {"StateDriving"}, {"state-driving"}, {policyId}, {2}, {seed.Now})", ct));
    }

    private static async Task AssertTransitionConstraintsAsync(EePulseDbContext db, Seed seed, CancellationToken ct)
    {
        await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO probe_result_status_transitions (agent_id, result_id, probe_id, from_status, to_status, reason_code, event_at, received_at) VALUES ({seed.AgentId}, {Guid.NewGuid()}, {seed.ProbeId}, {"Invalid"}, {"Up"}, {"bootstrap-success"}, {seed.SecondEventAt}, {seed.Now})", ct));
        await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO probe_result_status_transitions (agent_id, result_id, probe_id, from_status, to_status, reason_code, event_at, received_at) VALUES ({seed.AgentId}, {Guid.NewGuid()}, {seed.ProbeId}, {"Up"}, {"Up"}, {"bootstrap-success"}, {seed.SecondEventAt}, {seed.Now})", ct));
        await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO probe_result_status_transitions (agent_id, result_id, probe_id, from_status, to_status, reason_code, event_at, received_at) VALUES ({seed.AgentId}, {Guid.NewGuid()}, {seed.ProbeId}, {"Up"}, {"Down"}, {""}, {seed.SecondEventAt}, {seed.Now})", ct));
        await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO probe_result_status_transitions (agent_id, result_id, probe_id, from_status, to_status, reason_code, event_at, received_at) VALUES ({seed.AgentId}, {Guid.NewGuid()}, {seed.ProbeId}, {"Up"}, {"Down"}, {"unsupported-reason"}, {seed.SecondEventAt}, {seed.Now})", ct));
        await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO probe_result_status_transitions (agent_id, result_id, probe_id, from_status, to_status, reason_code, event_at, received_at) VALUES ({seed.AgentId}, {seed.SecondResultId}, {seed.OtherProbeId}, {"Up"}, {"Down"}, {"failure-threshold-met"}, {seed.SecondEventAt}, {seed.Now})", ct));
        var violation = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO probe_result_status_transitions (agent_id, result_id, probe_id, from_status, to_status, reason_code, event_at, received_at, processing_disposition) VALUES ({seed.AgentId}, {seed.ResultId}, {seed.ProbeId}, {"Unknown"}, {"Up"}, {"bootstrap-success"}, {seed.EventAt}, {seed.Now}, {"StateDriving"})", ct));
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, violation.SqlState);
    }

    private static void AssertTransitionDispositionModel(EePulseDbContext db)
    {
        var model = db.GetService<IDesignTimeModel>().Model;
        var disposition = model.FindEntityType(typeof(ProbeResultProcessingDisposition))!;
        Assert.Contains(disposition.GetKeys(), key => key.Properties.Select(property => property.Name)
            .SequenceEqual([nameof(ProbeResultProcessingDisposition.AgentId), nameof(ProbeResultProcessingDisposition.ResultId), nameof(ProbeResultProcessingDisposition.Disposition)]));

        var transition = model.FindEntityType(typeof(ProbeResultStatusTransition))!;
        Assert.Contains(transition.GetCheckConstraints(), check => check.Name == "ck_probe_result_status_transitions_processing_disposition" && check.Sql == "processing_disposition = 'StateDriving'");
        Assert.Contains(transition.GetForeignKeys(), foreignKey =>
            foreignKey.Properties.Select(property => property.Name).SequenceEqual([nameof(ProbeResultStatusTransition.AgentId), nameof(ProbeResultStatusTransition.ResultId), nameof(ProbeResultStatusTransition.ProcessingDisposition)]) &&
            foreignKey.PrincipalKey.Properties.Select(property => property.Name).SequenceEqual([nameof(ProbeResultProcessingDisposition.AgentId), nameof(ProbeResultProcessingDisposition.ResultId), nameof(ProbeResultProcessingDisposition.Disposition)]) &&
            foreignKey.DeleteBehavior == DeleteBehavior.Restrict);
    }

    [Fact]
    public async Task St09bPostgreSqlEnforcesFreshnessExpiryCauseSourceContractAndAppendOnlyRows()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var postgres = await PostgresTestDatabase.StartAsync(ct);
        var options = new DbContextOptionsBuilder<EePulseDbContext>().UseNpgsql(postgres.ConnectionString).Options;
        await using (var migration = new EePulseDbContext(options)) await migration.Database.MigrateAsync(ct);

        var seed = await SeedAsync(options, ct);
        var source = await AddFreshnessCauseSourceAsync(options, seed, ct);
        await using var direct = new EePulseDbContext(options);
        var before = await direct.Database.SqlQueryRaw<DateTimeOffset>("SELECT clock_timestamp() AS \"Value\"").SingleAsync(ct);
        await InsertFreshnessCauseAsync(direct, source.Cause, DateTimeOffset.UnixEpoch, ct);
        var requestedAt = await direct.Database.SqlQuery<DateTimeOffset>($"SELECT requested_at AS \"Value\" FROM probe_freshness_expiry_causes WHERE cause_id = {source.Cause.CauseId}").SingleAsync(ct);
        var after = await direct.Database.SqlQueryRaw<DateTimeOffset>("SELECT clock_timestamp() AS \"Value\"").SingleAsync(ct);
        Assert.InRange(requestedAt, before, after);
        Assert.NotEqual(DateTimeOffset.UnixEpoch, requestedAt);
        Assert.Equal(0, requestedAt.Ticks % 10);

        await AssertUniqueViolationAsync(() => InsertFreshnessCauseAsync(direct, source.Cause with { CauseId = Guid.NewGuid() }, DateTimeOffset.UnixEpoch, ct), "ak_probe_freshness_expiry_causes_source");
        await direct.Database.ExecuteSqlRawAsync("ALTER TABLE probe_freshness_expiry_causes DISABLE TRIGGER tr_probe_freshness_expiry_causes_validate_source", ct);
        try
        {
            var shiftedCursor = source.Cause.SourceCursorEventAt.AddTicks(10);
            var missingSource = await Assert.ThrowsAsync<PostgresException>(() => InsertFreshnessCauseAsync(direct, source.Cause with
            {
                CauseId = Guid.NewGuid(),
                SourceCursorEventAt = shiftedCursor,
                SourceLastFreshEventAt = shiftedCursor,
                DueAt = source.Cause.DueAt.AddTicks(10),
            }, DateTimeOffset.UnixEpoch, ct));
            Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, missingSource.SqlState);
            Assert.Equal("FK_probe_freshness_expiry_causes_probe_result_ledger_source_ag~", missingSource.ConstraintName);
        }
        finally
        {
            await direct.Database.ExecuteSqlRawAsync("ALTER TABLE probe_freshness_expiry_causes ENABLE TRIGGER tr_probe_freshness_expiry_causes_validate_source", CancellationToken.None);
        }
        await AssertTriggerViolationAsync(() => InsertFreshnessCauseAsync(direct, source.Cause with { PolicySnapshotId = source.OtherPolicy.Id, PolicyVersion = source.OtherPolicy.PolicyVersion }, DateTimeOffset.UnixEpoch, ct), "WP-06 freshness expiry cause policy identity is invalid");
        await AssertTriggerViolationAsync(() => InsertFreshnessCauseAsync(direct, source.Cause with { PolicySnapshotId = source.OtherVersionPolicy.Id, PolicyVersion = source.OtherVersionPolicy.PolicyVersion }, DateTimeOffset.UnixEpoch, ct), "WP-06 freshness expiry cause policy version is invalid");
        await AssertTriggerViolationAsync(() => InsertFreshnessCauseAsync(direct, source.Cause with { FreshnessGraceSeconds = source.Cause.FreshnessGraceSeconds + 1 }, DateTimeOffset.UnixEpoch, ct));
        await AssertTriggerViolationAsync(() => InsertFreshnessCauseAsync(direct, source.Cause with { DueAt = source.Cause.DueAt.AddSeconds(1) }, DateTimeOffset.UnixEpoch, ct));

        await direct.Database.ExecuteSqlInterpolatedAsync($"UPDATE probe_status_projections SET watermark_result_id = {Guid.NewGuid()} WHERE probe_id = {seed.ProbeId}", ct);
        await AssertTriggerViolationAsync(() => InsertFreshnessCauseAsync(direct, source.Cause with { CauseId = Guid.NewGuid() }, DateTimeOffset.UnixEpoch, ct));
        await direct.Database.ExecuteSqlInterpolatedAsync($"UPDATE probe_status_projections SET watermark_result_id = {seed.ResultId}, last_fresh_event_at = {seed.EventAt.AddSeconds(1)} WHERE probe_id = {seed.ProbeId}", ct);
        await AssertTriggerViolationAsync(() => InsertFreshnessCauseAsync(direct, source.Cause with { CauseId = Guid.NewGuid() }, DateTimeOffset.UnixEpoch, ct));
        await direct.Database.ExecuteSqlInterpolatedAsync($"UPDATE probe_status_projections SET last_fresh_event_at = {seed.EventAt} WHERE probe_id = {seed.ProbeId}", ct);

        const string emptyProbesPayload = """{"probes":[]}""";
        await direct.Database.ExecuteSqlInterpolatedAsync(
    $"UPDATE agent_configuration_snapshots SET payload = {emptyProbesPayload}::jsonb WHERE agent_group_id = {seed.AgentGroupId} AND version = {1L}",
    ct);
        await AssertTriggerViolationAsync(() => InsertFreshnessCauseAsync(direct, source.Cause with { CauseId = Guid.NewGuid() }, DateTimeOffset.UnixEpoch, ct));
        await direct.Database.ExecuteSqlInterpolatedAsync($"UPDATE agent_configuration_snapshots SET payload = {FreshnessPayload(seed.ProbeId, source.Cause.FreshnessIntervalSeconds + 1)}::jsonb WHERE agent_group_id = {seed.AgentGroupId} AND version = {1L}", ct);
        await AssertTriggerViolationAsync(() => InsertFreshnessCauseAsync(direct, source.Cause with { CauseId = Guid.NewGuid() }, DateTimeOffset.UnixEpoch, ct));
        await direct.Database.ExecuteSqlInterpolatedAsync($"UPDATE agent_configuration_snapshots SET payload = {FreshnessPayload(seed.ProbeId, source.Cause.FreshnessIntervalSeconds)}::jsonb WHERE agent_group_id = {seed.AgentGroupId} AND version = {1L}", ct);

        var otherGroup = new AgentGroup(Guid.NewGuid(), "WP06 Other Group", null, seed.Now);
        await using (var write = new EePulseDbContext(options))
        {
            write.AddRange(otherGroup, new AgentConfigurationSnapshot(otherGroup.Id, 1, FreshnessPayload(seed.ProbeId, 30), new byte[32], seed.Now, null));
            await write.SaveChangesAsync(ct);
        }
        await AssertTriggerViolationAsync(() => InsertFreshnessCauseAsync(direct, source.Cause with { CauseId = Guid.NewGuid(), SourceAgentGroupId = otherGroup.Id }, DateTimeOffset.UnixEpoch, ct));

        var historicalResultId = Guid.NewGuid();
        await using (var write = new EePulseDbContext(options))
        {
            write.Add(new ProbeResultLedgerEntry(seed.AgentId, historicalResultId, seed.ProbeId, 1, seed.EventAt.AddSeconds(-1), seed.EventAt, 1, 1, 0m, 1m, 1m, 1m, null, new byte[32], seed.Now));
            write.Add(new ProbeResultProcessingDisposition(seed.AgentId, historicalResultId, seed.ProbeId, seed.EventAt, ProbeResultProcessingDispositionKind.HistoricalOther, "late-order", null, null, seed.Now));
            await write.SaveChangesAsync(ct);
        }
        await direct.Database.ExecuteSqlRawAsync("ALTER TABLE probe_freshness_expiry_causes DISABLE TRIGGER tr_probe_freshness_expiry_causes_validate_source", ct);
        try
        {
            var historicalSource = await Assert.ThrowsAsync<PostgresException>(() => InsertFreshnessCauseAsync(direct, source.Cause with { CauseId = Guid.NewGuid(), SourceResultId = historicalResultId }, DateTimeOffset.UnixEpoch, ct));
            Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, historicalSource.SqlState);
            Assert.Equal("FK_probe_freshness_expiry_causes_probe_result_processing_dispo~", historicalSource.ConstraintName);
        }
        finally
        {
            await direct.Database.ExecuteSqlRawAsync("ALTER TABLE probe_freshness_expiry_causes ENABLE TRIGGER tr_probe_freshness_expiry_causes_validate_source", CancellationToken.None);
        }

        await AssertAppendOnlyViolationAsync(() => direct.Database.ExecuteSqlInterpolatedAsync($"UPDATE probe_freshness_expiry_causes SET due_at = {source.Cause.DueAt} WHERE cause_id = {source.Cause.CauseId}", ct), "probe_freshness_expiry_causes");
        await AssertAppendOnlyViolationAsync(() => direct.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM probe_freshness_expiry_causes WHERE cause_id = {source.Cause.CauseId}", ct), "probe_freshness_expiry_causes");
        var restrictiveDelete = await Assert.ThrowsAsync<PostgresException>(() => direct.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM probe_result_ledger WHERE agent_id = {seed.AgentId} AND result_id = {seed.ResultId}", ct));
        Assert.Equal(PostgresErrorCodes.RestrictViolation, restrictiveDelete.SqlState);

        var nextResultId = Guid.NewGuid();
        var nextEventAt = seed.EventAt.AddSeconds(1);
        await using (var write = new EePulseDbContext(options))
        {
            write.Add(new ProbeResultLedgerEntry(seed.AgentId, nextResultId, seed.ProbeId, 1, nextEventAt.AddSeconds(-1), nextEventAt, 1, 1, 0m, 1m, 1m, 1m, null, new byte[32], seed.Now));
            write.Add(new ProbeResultProcessingDisposition(seed.AgentId, nextResultId, seed.ProbeId, nextEventAt, ProbeResultProcessingDispositionKind.StateDriving, "state-driving", source.Policy.Id, source.Policy.PolicyVersion, seed.Now));
            await write.SaveChangesAsync(ct);
        }
        await direct.Database.ExecuteSqlInterpolatedAsync($"UPDATE probe_status_projections SET watermark_event_at = {nextEventAt}, watermark_agent_id = {seed.AgentId}, watermark_result_id = {nextResultId}, last_fresh_event_at = {nextEventAt} WHERE probe_id = {seed.ProbeId}", ct);
        var nextCause = source.Cause with { CauseId = Guid.NewGuid(), SourceResultId = nextResultId, SourceCursorEventAt = nextEventAt, SourceLastFreshEventAt = nextEventAt, DueAt = nextEventAt.AddSeconds(60) };
        await InsertFreshnessCauseAsync(direct, nextCause, DateTimeOffset.UnixEpoch, ct);
    }

    [Fact]
    public async Task St09bPostgreSqlCauseInsertWaitsForCanonicalProbeAdvisoryLock()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken, timeout.Token);
        var ct = cancellation.Token;
        await using var postgres = await PostgresTestDatabase.StartAsync(ct);
        var options = new DbContextOptionsBuilder<EePulseDbContext>().UseNpgsql(postgres.ConnectionString).Options;
        await using (var migration = new EePulseDbContext(options)) await migration.Database.MigrateAsync(ct);
        var seed = await SeedAsync(options, ct);
        var source = await AddFreshnessCauseSourceAsync(options, seed, ct);

        await using var holder = new EePulseDbContext(options);
        await using var holderTransaction = await holder.Database.BeginTransactionAsync(ct);
        await ProbeTransactionLock.AcquireAsync(holder, seed.ProbeId, ct);
        await using var blocked = new EePulseDbContext(options);
        await using var blockedTransaction = await blocked.Database.BeginTransactionAsync(ct);
        var blockedPid = await blocked.Database.SqlQueryRaw<int>("SELECT pg_backend_pid() AS \"Value\"").SingleAsync(ct);
        var blockedInsert = InsertFreshnessCauseAsync(blocked, source.Cause, DateTimeOffset.UnixEpoch, ct);
        Exception? testFailure = null;
        try
        {
            await using var observer = new NpgsqlConnection(postgres.ConnectionString);
            await observer.OpenAsync(ct);
            await WaitForCauseAdvisoryLockAsync(observer, blockedPid, seed.ProbeId, blockedInsert, ct);
            await holderTransaction.CommitAsync(ct);
            await blockedInsert;
            await blockedTransaction.CommitAsync(ct);
        }
        catch (Exception exception)
        {
            testFailure = exception;
        }
        finally
        {
            Exception? cleanupFailure = null;
            try
            {
                await RollbackIfActiveAsync(holder, holderTransaction);
            }
            catch (Exception exception)
            {
                cleanupFailure = exception;
            }
            try
            {
                if (!blockedInsert.IsCompleted)
                    cancellation.Cancel();
                await blockedInsert.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            }
            catch (Exception exception)
            {
                cleanupFailure ??= exception;
            }
            try
            {
                await RollbackIfActiveAsync(blocked, blockedTransaction);
            }
            catch (Exception exception)
            {
                cleanupFailure ??= exception;
            }
            if (testFailure is null && cleanupFailure is not null) ExceptionDispatchInfo.Capture(cleanupFailure).Throw();
        }
        if (testFailure is not null) ExceptionDispatchInfo.Capture(testFailure).Throw();
    }

    [Fact]
    public async Task St09bSourceFirstModelDefinesImmutableResultFreshnessCauseContract()
    {
        var options = new DbContextOptionsBuilder<EePulseDbContext>()
            .UseNpgsql("Host=localhost;Database=ee_pulse_st09b_contract;Username=unused;Password=unused")
            .Options;
        await using var db = new EePulseDbContext(options);
        var model = db.GetService<IDesignTimeModel>().Model;
        var cause = model.FindEntityType(typeof(ProbeFreshnessExpiryCause))!;

        Assert.Contains(cause.GetKeys(), key => key.Properties.Select(property => property.Name)
            .SequenceEqual([nameof(ProbeFreshnessExpiryCause.ProbeId), nameof(ProbeFreshnessExpiryCause.SourceAgentId),
                nameof(ProbeFreshnessExpiryCause.SourceResultId), nameof(ProbeFreshnessExpiryCause.SourceCursorEventAt)]));
        Assert.Contains(cause.GetIndexes(), index => index.Properties.Select(property => property.Name)
            .SequenceEqual([nameof(ProbeFreshnessExpiryCause.DueAt), nameof(ProbeFreshnessExpiryCause.ProbeId)]) &&
            index.GetDatabaseName() == "ix_probe_freshness_expiry_causes_due_probe");
        Assert.Equal(ValueGenerated.OnAdd, cause.FindProperty(nameof(ProbeFreshnessExpiryCause.RequestedAt))!.ValueGenerated);
        Assert.Equal("clock_timestamp()", cause.FindProperty(nameof(ProbeFreshnessExpiryCause.RequestedAt))!.GetDefaultValueSql());
        Assert.Contains(cause.GetCheckConstraints(), check => check.Name == "ck_probe_freshness_expiry_causes_type" && check.Sql == "cause_type = 'ResultFreshnessExpiry'");
        Assert.Contains(cause.GetCheckConstraints(), check => check.Name == "ck_probe_freshness_expiry_causes_source_disposition" && check.Sql == "source_disposition = 'StateDriving'");
        Assert.Contains(cause.GetCheckConstraints(), check => check.Name == "ck_probe_freshness_expiry_causes_versions" && check.Sql == "source_configuration_version >= 1 AND policy_version >= 1");
        Assert.Contains(cause.GetCheckConstraints(), check => check.Name == "ck_probe_freshness_expiry_causes_inputs" && check.Sql == "freshness_interval_seconds >= 1 AND freshness_grace_seconds >= 1");
        Assert.Contains(cause.GetCheckConstraints(), check => check.Name == "ck_probe_freshness_expiry_causes_source_freshness" && check.Sql == "source_cursor_event_at = source_last_fresh_event_at");
        Assert.Contains(cause.GetCheckConstraints(), check => check.Name == "ck_probe_freshness_expiry_causes_due_at" && check.Sql == "due_at >= source_last_fresh_event_at");

        AssertRestrictiveForeignKey(cause, [nameof(ProbeFreshnessExpiryCause.ProbeId)], typeof(Probe), [nameof(Probe.Id)]);
        AssertRestrictiveForeignKey(cause, [nameof(ProbeFreshnessExpiryCause.SourceAgentId)], typeof(EePulse.Domain.Agents.Agent), [nameof(EePulse.Domain.Agents.Agent.Id)]);
        AssertRestrictiveForeignKey(cause, [nameof(ProbeFreshnessExpiryCause.SourceAgentId), nameof(ProbeFreshnessExpiryCause.SourceResultId), nameof(ProbeFreshnessExpiryCause.ProbeId), nameof(ProbeFreshnessExpiryCause.SourceCursorEventAt)], typeof(ProbeResultLedgerEntry), [nameof(ProbeResultLedgerEntry.AgentId), nameof(ProbeResultLedgerEntry.ResultId), nameof(ProbeResultLedgerEntry.ProbeId), nameof(ProbeResultLedgerEntry.EndedAt)]);
        AssertRestrictiveForeignKey(cause, [nameof(ProbeFreshnessExpiryCause.SourceAgentId), nameof(ProbeFreshnessExpiryCause.SourceResultId), nameof(ProbeFreshnessExpiryCause.SourceDisposition)], typeof(ProbeResultProcessingDisposition), [nameof(ProbeResultProcessingDisposition.AgentId), nameof(ProbeResultProcessingDisposition.ResultId), nameof(ProbeResultProcessingDisposition.Disposition)]);
        AssertRestrictiveForeignKey(cause, [nameof(ProbeFreshnessExpiryCause.SourceAgentGroupId), nameof(ProbeFreshnessExpiryCause.SourceConfigurationVersion)], typeof(AgentConfigurationSnapshot), [nameof(AgentConfigurationSnapshot.AgentGroupId), nameof(AgentConfigurationSnapshot.Version)]);
        AssertRestrictiveForeignKey(cause, [nameof(ProbeFreshnessExpiryCause.PolicySnapshotId), nameof(ProbeFreshnessExpiryCause.PolicyVersion)], typeof(ProbeStatusPolicySnapshot), [nameof(ProbeStatusPolicySnapshot.Id), nameof(ProbeStatusPolicySnapshot.PolicyVersion)]);

        var expiryCause = new ProbeFreshnessExpiryCause(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            new DateTimeOffset(2026, 8, 27, 0, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 8, 27, 0, 0, 0, TimeSpan.Zero),
            1, Guid.NewGuid(), Guid.NewGuid(), 1, 30, 60, new DateTimeOffset(2026, 8, 27, 0, 1, 0, TimeSpan.Zero));
        foreach (var state in new[] { EntityState.Modified, EntityState.Deleted })
        {
            db.Entry(expiryCause).State = state;
            await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync(TestContext.Current.CancellationToken));
            db.Entry(expiryCause).State = EntityState.Detached;
        }

        // Phase 2 PostgreSQL contract: trigger-validate exact source/cursor/policy/configuration interval/source-Agent grace/DueAt;
        // enforce alternate-key uniqueness and restrictive deletes; and reject direct SQL UPDATE/DELETE.
    }

    [Fact]
    public async Task St10MigrationBackfillsVisibleStatusWithoutChangingProjectionState()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var postgres = await PostgresTestDatabase.StartAsync(ct);
        var options = new DbContextOptionsBuilder<EePulseDbContext>().UseNpgsql(postgres.ConnectionString).Options;
        await using var migration = new EePulseDbContext(options);
        var migrator = migration.GetService<IMigrator>();
        await migrator.MigrateAsync("20260827023012_WP06St09bResultFreshnessExpiryCause", ct);
        var seed = await SeedAsync(options, ct);
        var thirdProbe = new Probe(Guid.NewGuid(), (await migration.Probes.SingleAsync(x => x.Id == seed.ProbeId, ct)).DeviceId,
            seed.AgentGroupId, 30, 2_000, 3, 500, null, 3, 2);
        migration.Add(thirdProbe);
        await migration.SaveChangesAsync(ct);
        var watermarkAgentId = Guid.NewGuid();
        var watermarkResultId = Guid.NewGuid();
        await migration.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO probe_status_projections (probe_id, underlying_status, consecutive_failure_count, consecutive_success_count, last_fresh_event_at, watermark_event_at, watermark_agent_id, watermark_result_id, state_version) VALUES ({seed.ProbeId}, {"Up"}, {0}, {1}, {seed.Now}, {seed.Now}, {watermarkAgentId}, {watermarkResultId}, {7L}), ({seed.OtherProbeId}, {"Down"}, {2}, {0}, {seed.Now.AddSeconds(1)}, {seed.Now.AddSeconds(1)}, {Guid.NewGuid()}, {Guid.NewGuid()}, {8L}), ({thirdProbe.Id}, {"Recovering"}, {0}, {2}, {seed.Now.AddSeconds(2)}, {seed.Now.AddSeconds(2)}, {Guid.NewGuid()}, {Guid.NewGuid()}, {9L})", ct);

        await migrator.MigrateAsync("20260827132616_WP06St10FreshnessExpiryApplication", ct);
        await using var verify = new EePulseDbContext(options);
        var projections = await verify.ProbeStatusProjections.AsNoTracking().OrderBy(x => x.StateVersion).ToListAsync(ct);
        Assert.Collection(projections,
            row =>
            {
                Assert.Equal(ProbeStatus.Up, row.UnderlyingStatus); Assert.Equal(ProbeStatus.Up, row.VisibleStatus);
                Assert.Equal(0, row.ConsecutiveFailureCount); Assert.Equal(1, row.ConsecutiveSuccessCount);
                Assert.Equal(seed.Now, row.LastFreshEventAt); Assert.Equal(seed.Now, row.WatermarkEventAt);
                Assert.Equal(watermarkAgentId, row.WatermarkAgentId); Assert.Equal(watermarkResultId, row.WatermarkResultId);
                Assert.Null(row.OpenIncidentId); Assert.Equal(7L, row.StateVersion);
            },
            row =>
            {
                Assert.Equal(ProbeStatus.Down, row.UnderlyingStatus); Assert.Equal(ProbeStatus.Down, row.VisibleStatus);
                Assert.Equal(2, row.ConsecutiveFailureCount); Assert.Equal(0, row.ConsecutiveSuccessCount);
                Assert.Equal(seed.Now.AddSeconds(1), row.LastFreshEventAt); Assert.Equal(seed.Now.AddSeconds(1), row.WatermarkEventAt);
                Assert.Null(row.OpenIncidentId); Assert.Equal(8L, row.StateVersion);
            },
            row =>
            {
                Assert.Equal(ProbeStatus.Recovering, row.UnderlyingStatus); Assert.Equal(ProbeStatus.Recovering, row.VisibleStatus);
                Assert.Equal(0, row.ConsecutiveFailureCount); Assert.Equal(2, row.ConsecutiveSuccessCount);
                Assert.Equal(seed.Now.AddSeconds(2), row.LastFreshEventAt); Assert.Equal(seed.Now.AddSeconds(2), row.WatermarkEventAt);
                Assert.Null(row.OpenIncidentId); Assert.Equal(9L, row.StateVersion);
            });
    }

    [Fact]
    public async Task St10PostgreSqlEnforcesFreshnessExpiryApplicationPersistenceContract()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var postgres = await PostgresTestDatabase.StartAsync(ct);
        var options = new DbContextOptionsBuilder<EePulseDbContext>().UseNpgsql(postgres.ConnectionString).Options;
        await using (var migration = new EePulseDbContext(options)) await migration.Database.MigrateAsync(ct);
        var seed = await SeedAsync(options, ct);
        var source = await AddFreshnessCauseSourceAsync(options, seed, ct);
        await using var direct = new EePulseDbContext(options);
        await InsertFreshnessCauseAsync(direct, source.Cause, DateTimeOffset.UnixEpoch, ct);
        var noOpProjectionMissing = await AddAdditionalFreshnessCauseAsync(options, seed, source.Policy, 1, ct);
        var noOpSuperseded = await AddAdditionalFreshnessCauseAsync(options, seed, source.Policy, 2, ct);
        var noOpAlreadyUnknown = await AddAdditionalFreshnessCauseAsync(options, seed, source.Policy, 3, ct);
        var invalidTransitionCause = await AddAdditionalFreshnessCauseAsync(options, seed, source.Policy, 4, ct);
        var noOpTransitionCause = await AddAdditionalFreshnessCauseAsync(options, seed, source.Policy, 5, ct);
        var mismatchedProbeCause = await AddAdditionalFreshnessCauseAsync(options, seed, source.Policy, 6, ct);
        var mismatchedPolicyCause = await AddAdditionalFreshnessCauseAsync(options, seed, source.Policy, 7, ct);
        var cutoff = seed.Now.AddSeconds(30);

        await InsertExpiryDispositionAsync(direct, source.Cause.CauseId, source.Cause.ProbeId, source.Policy.Id, source.Policy.PolicyVersion, "Applied", "result-freshness-expired", cutoff, cutoff, ct);
        await InsertExpiryTransitionAsync(direct, source.Cause.CauseId, source.Cause.ProbeId, source.Policy.Id, source.Policy.PolicyVersion, "Applied", "Down", "Unknown", "result-freshness-expired", cutoff, ct);
        await InsertExpiryDispositionAsync(direct, noOpSuperseded.CauseId, noOpSuperseded.ProbeId, source.Policy.Id, source.Policy.PolicyVersion, "NoOp", "freshness-source-superseded", cutoff, null, ct);
        await InsertExpiryDispositionAsync(direct, noOpAlreadyUnknown.CauseId, noOpAlreadyUnknown.ProbeId, source.Policy.Id, source.Policy.PolicyVersion, "NoOp", "visible-already-unknown", cutoff, null, ct);
        await InsertExpiryDispositionAsync(direct, noOpTransitionCause.CauseId, noOpTransitionCause.ProbeId, source.Policy.Id, source.Policy.PolicyVersion, "NoOp", "freshness-source-superseded", cutoff, null, ct);
        await direct.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM probe_status_projections WHERE probe_id = {seed.ProbeId}", ct);
        await InsertExpiryDispositionAsync(direct, noOpProjectionMissing.CauseId, noOpProjectionMissing.ProbeId, source.Policy.Id, source.Policy.PolicyVersion, "NoOp", "projection-missing", cutoff, null, ct);

        await using (var read = new EePulseDbContext(options))
        {
            var disposition = await read.ProbeFreshnessExpiryCauseDispositions.AsNoTracking().SingleAsync(x => x.CauseId == source.Cause.CauseId, ct);
            var transition = await read.ProbeFreshnessExpiryCauseTransitions.AsNoTracking().SingleAsync(x => x.CauseId == source.Cause.CauseId, ct);
            var projectionMissing = await read.ProbeFreshnessExpiryCauseDispositions.AsNoTracking().SingleAsync(x => x.CauseId == noOpProjectionMissing.CauseId, ct);
            var superseded = await read.ProbeFreshnessExpiryCauseDispositions.AsNoTracking().SingleAsync(x => x.CauseId == noOpSuperseded.CauseId, ct);
            var alreadyUnknown = await read.ProbeFreshnessExpiryCauseDispositions.AsNoTracking().SingleAsync(x => x.CauseId == noOpAlreadyUnknown.CauseId, ct);
            Assert.Equal((source.Cause.ProbeId, source.Policy.Id, source.Policy.PolicyVersion, ProbeFreshnessExpiryCauseDispositionOutcome.Applied, "result-freshness-expired", cutoff, (DateTimeOffset?)cutoff),
                (disposition.ProbeId, disposition.PolicySnapshotId, disposition.PolicyVersion, disposition.Outcome, disposition.ReasonCode, disposition.ExpiryCutoffReceivedAt, disposition.AppliedAt));
            Assert.Equal((source.Cause.ProbeId, source.Policy.Id, source.Policy.PolicyVersion, ProbeFreshnessExpiryCauseDispositionOutcome.Applied, ProbeStatus.Down, ProbeStatus.Unknown, "result-freshness-expired", cutoff),
                (transition.ProbeId, transition.PolicySnapshotId, transition.PolicyVersion, transition.DispositionOutcome, transition.FromVisibleStatus, transition.ToVisibleStatus, transition.ReasonCode, transition.AppliedAt));
            AssertNoOpDisposition(projectionMissing, noOpProjectionMissing, source.Policy, "projection-missing", cutoff);
            AssertNoOpDisposition(superseded, noOpSuperseded, source.Policy, "freshness-source-superseded", cutoff);
            AssertNoOpDisposition(alreadyUnknown, noOpAlreadyUnknown, source.Policy, "visible-already-unknown", cutoff);
        }

        await AssertUniqueViolationAsync(() => InsertExpiryDispositionAsync(direct, source.Cause.CauseId, source.Cause.ProbeId, source.Policy.Id, source.Policy.PolicyVersion, "Applied", "result-freshness-expired", cutoff, cutoff, ct), "PK_probe_freshness_expiry_cause_dispositions");
        await AssertUniqueViolationAsync(() => InsertExpiryTransitionAsync(direct, source.Cause.CauseId, source.Cause.ProbeId, source.Policy.Id, source.Policy.PolicyVersion, "Applied", "Down", "Unknown", "result-freshness-expired", cutoff, ct), "PK_probe_freshness_expiry_cause_transitions");
        await AssertCheckViolationAsync(() => InsertExpiryDispositionAsync(direct, invalidTransitionCause.CauseId, invalidTransitionCause.ProbeId, source.Policy.Id, source.Policy.PolicyVersion, "Invalid", "result-freshness-expired", cutoff, cutoff, ct), "ck_probe_freshness_expiry_cause_dispositions_outcome");
        await AssertCheckViolationAsync(() => InsertExpiryDispositionAsync(direct, invalidTransitionCause.CauseId, invalidTransitionCause.ProbeId, source.Policy.Id, source.Policy.PolicyVersion, "Applied", "wrong", cutoff, cutoff, ct), "ck_probe_freshness_expiry_cause_dispositions_shape");
        await AssertCheckViolationAsync(() => InsertExpiryDispositionAsync(direct, invalidTransitionCause.CauseId, invalidTransitionCause.ProbeId, source.Policy.Id, source.Policy.PolicyVersion, "Applied", "result-freshness-expired", cutoff, cutoff.AddTicks(10), ct), "ck_probe_freshness_expiry_cause_dispositions_shape");
        await AssertCheckViolationAsync(() => InsertExpiryDispositionAsync(direct, invalidTransitionCause.CauseId, invalidTransitionCause.ProbeId, source.Policy.Id, source.Policy.PolicyVersion, "NoOp", "projection-missing", cutoff, cutoff, ct), "ck_probe_freshness_expiry_cause_dispositions_shape");

        await InsertExpiryDispositionAsync(direct, invalidTransitionCause.CauseId, invalidTransitionCause.ProbeId, source.Policy.Id, source.Policy.PolicyVersion, "Applied", "result-freshness-expired", cutoff, cutoff, ct);
        await AssertCheckViolationAsync(() => InsertExpiryTransitionAsync(direct, noOpTransitionCause.CauseId, noOpTransitionCause.ProbeId, source.Policy.Id, source.Policy.PolicyVersion, "NoOp", "Up", "Unknown", "result-freshness-expired", cutoff, ct), "ck_probe_freshness_expiry_cause_transitions_disposition_outcome");
        await AssertCheckViolationAsync(() => InsertExpiryTransitionAsync(direct, invalidTransitionCause.CauseId, invalidTransitionCause.ProbeId, source.Policy.Id, source.Policy.PolicyVersion, "Applied", "Unknown", "Unknown", "result-freshness-expired", cutoff, ct), "ck_probe_freshness_expiry_cause_transitions_from_visible_status");
        await AssertCheckViolationAsync(() => InsertExpiryTransitionAsync(direct, invalidTransitionCause.CauseId, invalidTransitionCause.ProbeId, source.Policy.Id, source.Policy.PolicyVersion, "Applied", "Up", "Up", "result-freshness-expired", cutoff, ct), "ck_probe_freshness_expiry_cause_transitions_to_visible_status");
        await AssertCheckViolationAsync(() => InsertExpiryTransitionAsync(direct, invalidTransitionCause.CauseId, invalidTransitionCause.ProbeId, source.Policy.Id, source.Policy.PolicyVersion, "Applied", "Up", "Unknown", "wrong", cutoff, ct), "ck_probe_freshness_expiry_cause_transitions_reason_code");
        await AssertTriggerViolationAsync(() => InsertExpiryTransitionAsync(direct, invalidTransitionCause.CauseId, invalidTransitionCause.ProbeId, source.Policy.Id, source.Policy.PolicyVersion, "Applied", "Up", "Unknown", "result-freshness-expired", cutoff.AddTicks(10), ct), "WP-06 freshness expiry transition disposition timestamp mismatch");
        await AssertTriggerViolationAsync(() => InsertExpiryTransitionAsync(direct, noOpSuperseded.CauseId, noOpSuperseded.ProbeId, source.Policy.Id, source.Policy.PolicyVersion, "Applied", "Up", "Unknown", "result-freshness-expired", cutoff, ct), "WP-06 freshness expiry transition disposition timestamp mismatch");

        // This validation trigger precedes FK checks on INSERT. Disable it only to assert its defense-in-depth FK identities.
        await direct.Database.ExecuteSqlRawAsync("ALTER TABLE probe_freshness_expiry_cause_transitions DISABLE TRIGGER tr_probe_freshness_expiry_cause_transitions_validate_disposition_timestamp", ct);
        try
        {
            var missingDisposition = await Assert.ThrowsAsync<PostgresException>(() => InsertExpiryTransitionAsync(direct, noOpSuperseded.CauseId, noOpSuperseded.ProbeId, source.Policy.Id, source.Policy.PolicyVersion, "Applied", "Up", "Unknown", "result-freshness-expired", cutoff, ct));
            Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, missingDisposition.SqlState);
            Assert.Equal("FK_probe_freshness_expiry_cause_transitions_probe_freshness_ex~", missingDisposition.ConstraintName);
            var missingCause = await Assert.ThrowsAsync<PostgresException>(() => InsertExpiryDispositionAsync(direct, Guid.NewGuid(), seed.ProbeId, source.Policy.Id, source.Policy.PolicyVersion, "NoOp", "projection-missing", cutoff, null, ct));
            Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, missingCause.SqlState);
            Assert.Equal("FK_probe_freshness_expiry_cause_dispositions_probe_freshness_e~", missingCause.ConstraintName);
            var mismatchedProbe = await Assert.ThrowsAsync<PostgresException>(() => InsertExpiryDispositionAsync(direct, mismatchedProbeCause.CauseId, seed.OtherProbeId, source.Policy.Id, source.Policy.PolicyVersion, "NoOp", "projection-missing", cutoff, null, ct));
            Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, mismatchedProbe.SqlState);
            Assert.Equal("FK_probe_freshness_expiry_cause_dispositions_probe_freshness_e~", mismatchedProbe.ConstraintName);
            var mismatchedPolicy = await Assert.ThrowsAsync<PostgresException>(() => InsertExpiryDispositionAsync(direct, mismatchedPolicyCause.CauseId, mismatchedPolicyCause.ProbeId, source.OtherPolicy.Id, source.OtherPolicy.PolicyVersion, "NoOp", "projection-missing", cutoff, null, ct));
            Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, mismatchedPolicy.SqlState);
            Assert.Equal("FK_probe_freshness_expiry_cause_dispositions_probe_freshness_e~", mismatchedPolicy.ConstraintName);
        }
        finally
        {
            await direct.Database.ExecuteSqlRawAsync("ALTER TABLE probe_freshness_expiry_cause_transitions ENABLE TRIGGER tr_probe_freshness_expiry_cause_transitions_validate_disposition_timestamp", CancellationToken.None);
        }

        await AssertAppendOnlyViolationAsync(() => direct.Database.ExecuteSqlInterpolatedAsync($"UPDATE probe_freshness_expiry_cause_dispositions SET reason_code = {"result-freshness-expired"} WHERE cause_id = {source.Cause.CauseId}", ct), "probe_freshness_expiry_cause_dispositions");
        await AssertAppendOnlyViolationAsync(() => direct.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM probe_freshness_expiry_cause_dispositions WHERE cause_id = {source.Cause.CauseId}", ct), "probe_freshness_expiry_cause_dispositions");
        await AssertAppendOnlyViolationAsync(() => direct.Database.ExecuteSqlInterpolatedAsync($"UPDATE probe_freshness_expiry_cause_transitions SET applied_at = {cutoff} WHERE cause_id = {source.Cause.CauseId}", ct), "probe_freshness_expiry_cause_transitions");
        await AssertAppendOnlyViolationAsync(() => direct.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM probe_freshness_expiry_cause_transitions WHERE cause_id = {source.Cause.CauseId}", ct), "probe_freshness_expiry_cause_transitions");
        await direct.Database.ExecuteSqlRawAsync("ALTER TABLE probe_freshness_expiry_causes DISABLE TRIGGER tr_probe_freshness_expiry_causes_append_only", ct);
        try
        {
            var restrictiveDelete = await Assert.ThrowsAsync<PostgresException>(() => direct.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM probe_freshness_expiry_causes WHERE cause_id = {source.Cause.CauseId}", ct));
            Assert.Equal(PostgresErrorCodes.RestrictViolation, restrictiveDelete.SqlState);
        }
        finally
        {
            await direct.Database.ExecuteSqlRawAsync("ALTER TABLE probe_freshness_expiry_causes ENABLE TRIGGER tr_probe_freshness_expiry_causes_append_only", CancellationToken.None);
        }
    }

    private static void AssertNoOpDisposition(ProbeFreshnessExpiryCauseDisposition disposition, FreshnessCause cause,
        ProbeStatusPolicySnapshot policy, string reasonCode, DateTimeOffset expiryCutoffReceivedAt)
    {
        Assert.Equal(cause.CauseId, disposition.CauseId);
        Assert.Equal(cause.ProbeId, disposition.ProbeId);
        Assert.Equal(policy.Id, disposition.PolicySnapshotId);
        Assert.Equal(policy.PolicyVersion, disposition.PolicyVersion);
        Assert.Equal(ProbeFreshnessExpiryCauseDispositionOutcome.NoOp, disposition.Outcome);
        Assert.Equal(reasonCode, disposition.ReasonCode);
        Assert.Equal(expiryCutoffReceivedAt, disposition.ExpiryCutoffReceivedAt);
        Assert.Null(disposition.AppliedAt);
    }

    [Fact]
    public async Task St10ModelDefinesResultFreshnessExpiryDispositionAndTransitionContracts()
    {
        var options = new DbContextOptionsBuilder<EePulseDbContext>()
            .UseNpgsql("Host=localhost;Database=ee_pulse_st10_contract;Username=unused;Password=unused")
            .Options;
        await using var db = new EePulseDbContext(options);
        var model = db.GetService<IDesignTimeModel>().Model;
        var projection = model.FindEntityType(typeof(ProbeStatusProjection))!;
        var disposition = model.FindEntityType(typeof(ProbeFreshnessExpiryCauseDisposition))!;
        var transition = model.FindEntityType(typeof(ProbeFreshnessExpiryCauseTransition))!;

        Assert.Equal("probe_status_projections", projection.GetTableName());
        var visibleStatus = projection.FindProperty(nameof(ProbeStatusProjection.VisibleStatus))!;
        Assert.Equal("visible_status", visibleStatus.GetColumnName());
        Assert.False(visibleStatus.IsNullable);
        Assert.Equal(20, visibleStatus.GetMaxLength());
        Assert.Equal("varchar(20)", visibleStatus.GetColumnType());
        Assert.NotNull(visibleStatus.GetTypeMapping().Converter);
        Assert.Contains(projection.GetCheckConstraints(), check => check.Name == "ck_probe_status_projections_visible_status" &&
            check.Sql == "visible_status IN ('Unknown', 'Up', 'Degraded', 'Down', 'Recovering')");

        Assert.Equal("probe_freshness_expiry_cause_dispositions", disposition.GetTableName());
        Assert.True(disposition.FindPrimaryKey()!.Properties.Select(x => x.Name)
            .SequenceEqual([nameof(ProbeFreshnessExpiryCauseDisposition.CauseId)]));
        Assert.Contains(disposition.GetKeys(), key => key.Properties.Select(x => x.Name)
            .SequenceEqual([nameof(ProbeFreshnessExpiryCauseDisposition.CauseId), nameof(ProbeFreshnessExpiryCauseDisposition.Outcome)]));
        AssertRequiredColumn(disposition, nameof(ProbeFreshnessExpiryCauseDisposition.CauseId), "cause_id", null, false);
        AssertRequiredColumn(disposition, nameof(ProbeFreshnessExpiryCauseDisposition.ProbeId), "probe_id", null, false);
        AssertRequiredColumn(disposition, nameof(ProbeFreshnessExpiryCauseDisposition.PolicySnapshotId), "policy_snapshot_id", null, false);
        AssertRequiredColumn(disposition, nameof(ProbeFreshnessExpiryCauseDisposition.PolicyVersion), "policy_version", null, false);
        AssertRequiredColumn(disposition, nameof(ProbeFreshnessExpiryCauseDisposition.Outcome), "outcome", 16, true, "varchar(16)");
        AssertRequiredColumn(disposition, nameof(ProbeFreshnessExpiryCauseDisposition.ReasonCode), "reason_code", 64, false);
        AssertRequiredColumn(disposition, nameof(ProbeFreshnessExpiryCauseDisposition.ExpiryCutoffReceivedAt), "expiry_cutoff_received_at", null, false);
        Assert.Equal("applied_at", disposition.FindProperty(nameof(ProbeFreshnessExpiryCauseDisposition.AppliedAt))!.GetColumnName());
        Assert.True(disposition.FindProperty(nameof(ProbeFreshnessExpiryCauseDisposition.AppliedAt))!.IsNullable);
        Assert.Contains(disposition.GetCheckConstraints(), check => check.Name == "ck_probe_freshness_expiry_cause_dispositions_outcome" && check.Sql == "outcome IN ('Applied', 'NoOp')");
        Assert.Contains(disposition.GetCheckConstraints(), check => check.Name == "ck_probe_freshness_expiry_cause_dispositions_shape" && check.Sql == "(outcome = 'Applied' AND reason_code = 'result-freshness-expired' AND applied_at = expiry_cutoff_received_at) OR (outcome = 'NoOp' AND reason_code IN ('projection-missing', 'freshness-source-superseded', 'visible-already-unknown') AND applied_at IS NULL)");
        AssertRestrictiveForeignKey(disposition, [nameof(ProbeFreshnessExpiryCauseDisposition.CauseId), nameof(ProbeFreshnessExpiryCauseDisposition.ProbeId), nameof(ProbeFreshnessExpiryCauseDisposition.PolicySnapshotId), nameof(ProbeFreshnessExpiryCauseDisposition.PolicyVersion)], typeof(ProbeFreshnessExpiryCause), [nameof(ProbeFreshnessExpiryCause.CauseId), nameof(ProbeFreshnessExpiryCause.ProbeId), nameof(ProbeFreshnessExpiryCause.PolicySnapshotId), nameof(ProbeFreshnessExpiryCause.PolicyVersion)]);
        AssertRestrictiveForeignKey(disposition, [nameof(ProbeFreshnessExpiryCauseDisposition.ProbeId)], typeof(Probe), [nameof(Probe.Id)]);
        AssertRestrictiveForeignKey(disposition, [nameof(ProbeFreshnessExpiryCauseDisposition.PolicySnapshotId), nameof(ProbeFreshnessExpiryCauseDisposition.PolicyVersion)], typeof(ProbeStatusPolicySnapshot), [nameof(ProbeStatusPolicySnapshot.Id), nameof(ProbeStatusPolicySnapshot.PolicyVersion)]);

        Assert.Equal("probe_freshness_expiry_cause_transitions", transition.GetTableName());
        Assert.True(transition.FindPrimaryKey()!.Properties.Select(x => x.Name)
            .SequenceEqual([nameof(ProbeFreshnessExpiryCauseTransition.CauseId)]));
        AssertRequiredColumn(transition, nameof(ProbeFreshnessExpiryCauseTransition.CauseId), "cause_id", null, false);
        AssertRequiredColumn(transition, nameof(ProbeFreshnessExpiryCauseTransition.ProbeId), "probe_id", null, false);
        AssertRequiredColumn(transition, nameof(ProbeFreshnessExpiryCauseTransition.PolicySnapshotId), "policy_snapshot_id", null, false);
        AssertRequiredColumn(transition, nameof(ProbeFreshnessExpiryCauseTransition.PolicyVersion), "policy_version", null, false);
        AssertRequiredColumn(transition, nameof(ProbeFreshnessExpiryCauseTransition.DispositionOutcome), "disposition_outcome", 16, true, "varchar(16)");
        AssertRequiredColumn(transition, nameof(ProbeFreshnessExpiryCauseTransition.FromVisibleStatus), "from_visible_status", 20, true);
        AssertRequiredColumn(transition, nameof(ProbeFreshnessExpiryCauseTransition.ToVisibleStatus), "to_visible_status", 20, true);
        AssertRequiredColumn(transition, nameof(ProbeFreshnessExpiryCauseTransition.ReasonCode), "reason_code", 64, false);
        AssertRequiredColumn(transition, nameof(ProbeFreshnessExpiryCauseTransition.AppliedAt), "applied_at", null, false);
        Assert.Contains(transition.GetCheckConstraints(), check => check.Name == "ck_probe_freshness_expiry_cause_transitions_disposition_outcome" && check.Sql == "disposition_outcome = 'Applied'");
        Assert.Contains(transition.GetCheckConstraints(), check => check.Name == "ck_probe_freshness_expiry_cause_transitions_from_visible_status" && check.Sql == "from_visible_status IN ('Up', 'Degraded', 'Down', 'Recovering')");
        Assert.Contains(transition.GetCheckConstraints(), check => check.Name == "ck_probe_freshness_expiry_cause_transitions_to_visible_status" && check.Sql == "to_visible_status = 'Unknown'");
        Assert.Contains(transition.GetCheckConstraints(), check => check.Name == "ck_probe_freshness_expiry_cause_transitions_reason_code" && check.Sql == "reason_code = 'result-freshness-expired'");
        AssertRestrictiveForeignKey(transition, [nameof(ProbeFreshnessExpiryCauseTransition.CauseId), nameof(ProbeFreshnessExpiryCauseTransition.ProbeId), nameof(ProbeFreshnessExpiryCauseTransition.PolicySnapshotId), nameof(ProbeFreshnessExpiryCauseTransition.PolicyVersion)], typeof(ProbeFreshnessExpiryCause), [nameof(ProbeFreshnessExpiryCause.CauseId), nameof(ProbeFreshnessExpiryCause.ProbeId), nameof(ProbeFreshnessExpiryCause.PolicySnapshotId), nameof(ProbeFreshnessExpiryCause.PolicyVersion)]);
        AssertRestrictiveForeignKey(transition, [nameof(ProbeFreshnessExpiryCauseTransition.CauseId), nameof(ProbeFreshnessExpiryCauseTransition.DispositionOutcome)], typeof(ProbeFreshnessExpiryCauseDisposition), [nameof(ProbeFreshnessExpiryCauseDisposition.CauseId), nameof(ProbeFreshnessExpiryCauseDisposition.Outcome)]);
        AssertRestrictiveForeignKey(transition, [nameof(ProbeFreshnessExpiryCauseTransition.ProbeId)], typeof(Probe), [nameof(Probe.Id)]);
        AssertRestrictiveForeignKey(transition, [nameof(ProbeFreshnessExpiryCauseTransition.PolicySnapshotId), nameof(ProbeFreshnessExpiryCauseTransition.PolicyVersion)], typeof(ProbeStatusPolicySnapshot), [nameof(ProbeStatusPolicySnapshot.Id), nameof(ProbeStatusPolicySnapshot.PolicyVersion)]);

        foreach (var entity in new object[]
                 {
                     ProbeFreshnessExpiryCauseDisposition.Applied(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1, new DateTimeOffset(2026, 8, 27, 0, 0, 0, TimeSpan.Zero)),
                     new ProbeFreshnessExpiryCauseTransition(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1, ProbeStatus.Up, new DateTimeOffset(2026, 8, 27, 0, 0, 0, TimeSpan.Zero)),
                 })
        {
            foreach (var state in new[] { EntityState.Modified, EntityState.Deleted })
            {
                db.Entry(entity).State = state;
                await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync(TestContext.Current.CancellationToken));
                db.Entry(entity).State = EntityState.Detached;
            }
        }
    }

    private static void AssertRequiredColumn(IEntityType entity, string propertyName, string columnName, int? maxLength, bool requiresConversion, string? columnType = null)
    {
        var property = entity.FindProperty(propertyName)!;
        Assert.Equal(columnName, property.GetColumnName());
        Assert.False(property.IsNullable);
        Assert.Equal(maxLength, property.GetMaxLength());
        if (columnType is not null) Assert.Equal(columnType, property.GetColumnType());
        if (requiresConversion) Assert.NotNull(property.GetTypeMapping().Converter);
    }

    private static void AssertRestrictiveForeignKey(IEntityType dependent, IReadOnlyList<string> propertyNames, Type principalType, IReadOnlyList<string> principalKeyPropertyNames) =>
        Assert.Contains(dependent.GetForeignKeys(), foreignKey =>
            foreignKey.Properties.Select(property => property.Name).SequenceEqual(propertyNames) &&
            foreignKey.PrincipalEntityType.ClrType == principalType &&
            foreignKey.PrincipalKey.Properties.Select(property => property.Name).SequenceEqual(principalKeyPropertyNames) &&
            foreignKey.DeleteBehavior == DeleteBehavior.Restrict);

    private static async Task<Seed> SeedAsync(DbContextOptions<EePulseDbContext> options, CancellationToken ct)
    {
        var now = new DateTimeOffset(2026, 8, 25, 12, 0, 0, TimeSpan.Zero);
        var eventAt = now.AddSeconds(-5);
        var site = new Site(Guid.NewGuid(), "WP06", "WP06 Site", "UTC", now);
        var group = new AgentGroup(Guid.NewGuid(), "WP06 Group", null, now);
        var device = new Device(Guid.NewGuid(), site.Id, "WP06 Device", "192.0.2.60", null, "PLC", null, null, Criticality.Normal, [], now);
        var probe = new Probe(Guid.NewGuid(), device.Id, group.Id, 30, 2_000, 3, 500, null, 3, 2);
        var otherProbe = new Probe(Guid.NewGuid(), device.Id, group.Id, 30, 2_000, 3, 500, null, 3, 2);
        var agent = new EePulse.Domain.Agents.Agent(Guid.NewGuid(), group.Id, Guid.NewGuid(), "wp06-agent", "1.0.0", 20, now);
        var acknowledgementId = Guid.NewGuid();
        var acknowledgement = new AgentConfigurationAcknowledgement(acknowledgementId, agent.Id, 1,
            AgentAcknowledgementStatus.Applied, now, now, now, null, 1, 1);
        var secondaryAgent = new EePulse.Domain.Agents.Agent(Guid.NewGuid(), group.Id, Guid.NewGuid(), "wp06-agent-secondary", "1.0.0", 20, now);
        var secondaryAcknowledgementId = Guid.NewGuid();
        var secondaryAcknowledgementReceivedAt = now.AddSeconds(1);
        var secondaryAcknowledgement = new AgentConfigurationAcknowledgement(secondaryAcknowledgementId, secondaryAgent.Id, 1,
            AgentAcknowledgementStatus.Applied, secondaryAcknowledgementReceivedAt, secondaryAcknowledgementReceivedAt,
            secondaryAcknowledgementReceivedAt, null, 1, 1);
        var configuration = new AgentConfigurationSnapshot(group.Id, 1, FreshnessPayload(probe.Id, probe.IntervalSeconds), new byte[32], now, null);
        var resultId = Guid.NewGuid();
        var ledger = new ProbeResultLedgerEntry(agent.Id, resultId, probe.Id, 1, eventAt.AddSeconds(-1), eventAt,
            1, 1, 0m, 1m, 1m, 1m, null, new byte[32], now);
        var secondResultId = Guid.NewGuid();
        var secondEventAt = eventAt.AddSeconds(1);
        var secondLedger = new ProbeResultLedgerEntry(agent.Id, secondResultId, probe.Id, 1, secondEventAt.AddSeconds(-1), secondEventAt,
            1, 1, 0m, 1m, 1m, 1m, null, new byte[32], now);

        await using var db = new EePulseDbContext(options);
        db.AddRange(site, group, device, probe, otherProbe, agent, configuration, acknowledgement, secondaryAgent,
            secondaryAcknowledgement, ledger, secondLedger);
        await db.SaveChangesAsync(ct);
        return new Seed(now, eventAt, secondEventAt, group.Id, probe.Id, otherProbe.Id, agent.Id, acknowledgementId,
            now, resultId, secondResultId, secondaryAgent.Id, secondaryAcknowledgementId, secondaryAcknowledgementReceivedAt);
    }

    private static async Task AssertDbContextRejectsAppendOnlyMutationsAsync(
        DbContextOptions<EePulseDbContext> options, Seed seed, Guid policyId, CancellationToken ct)
    {
        foreach (var state in new[] { EntityState.Modified, EntityState.Deleted })
        {
            await AssertDbContextRejectsMutationAsync(options, state,
                (db, token) => db.ProbeStatusPolicySnapshots.SingleAsync(x => x.Id == policyId, token), ct);
            await AssertDbContextRejectsMutationAsync(options, state,
                (db, token) => db.ProbeStatusPolicyBindings.SingleAsync(x => x.ProbeId == seed.ProbeId && x.ConfigurationVersion == 1, token), ct);
            await AssertDbContextRejectsMutationAsync(options, state,
                (db, token) => db.AgentConfigurationEffectiveBoundaries.SingleAsync(x => x.AgentId == seed.AgentId && x.ConfigurationVersion == 1, token), ct);
            await AssertDbContextRejectsMutationAsync(options, state,
                (db, token) => db.ProbeResultProcessingDispositions.SingleAsync(x => x.AgentId == seed.AgentId && x.ResultId == seed.ResultId, token), ct);
            await AssertDbContextRejectsMutationAsync(options, state,
                (db, token) => db.ProbeResultStatusTransitions.SingleAsync(x => x.AgentId == seed.AgentId && x.ResultId == seed.SecondResultId, token), ct);
        }
    }

    private static async Task AssertDbContextRejectsMutationAsync<TEntity>(DbContextOptions<EePulseDbContext> options,
        EntityState state, Func<EePulseDbContext, CancellationToken, Task<TEntity>> load, CancellationToken ct) where TEntity : class
    {
        await using (var db = new EePulseDbContext(options))
        {
            db.Entry(await load(db, ct)).State = state;
            await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync(ct));
        }
    }

    private sealed record Seed(DateTimeOffset Now, DateTimeOffset EventAt, DateTimeOffset SecondEventAt,
        Guid AgentGroupId, Guid ProbeId, Guid OtherProbeId, Guid AgentId, Guid AcknowledgementId,
        DateTimeOffset AcknowledgementReceivedAt, Guid ResultId, Guid SecondResultId, Guid SecondaryAgentId,
        Guid SecondaryAcknowledgementId, DateTimeOffset SecondaryAcknowledgementReceivedAt);

    private sealed record LifecycleSource(Guid IncidentId, Guid ProbeId, Guid AgentId, Guid ResultId,
        ProbeStatus FromStatus, ProbeStatus ToStatus, string ReasonCode, DateTimeOffset EventAt);

    private sealed record FreshnessCause(Guid CauseId, Guid ProbeId, Guid SourceAgentId, Guid SourceResultId,
        DateTimeOffset SourceCursorEventAt, DateTimeOffset SourceLastFreshEventAt, long SourceConfigurationVersion,
        Guid SourceAgentGroupId, Guid PolicySnapshotId, int PolicyVersion, int FreshnessIntervalSeconds,
        int FreshnessGraceSeconds, DateTimeOffset DueAt);

    private sealed record FreshnessSource(FreshnessCause Cause, ProbeStatusPolicySnapshot Policy,
        ProbeStatusPolicySnapshot OtherPolicy, ProbeStatusPolicySnapshot OtherVersionPolicy);
}
