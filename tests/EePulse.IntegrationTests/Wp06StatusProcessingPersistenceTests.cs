using EePulse.Domain.Agents;
using EePulse.Domain.Inventory;
using EePulse.Domain.Status;
using EePulse.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Npgsql;

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
        var configuration = new AgentConfigurationSnapshot(group.Id, 1, "{}", new byte[32], now, null);
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
}
