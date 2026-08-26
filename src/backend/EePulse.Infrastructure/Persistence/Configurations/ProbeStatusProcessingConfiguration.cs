using EePulse.Domain.Agents;
using EePulse.Domain.Inventory;
using EePulse.Domain.Status;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EePulse.Infrastructure.Persistence.Configurations;

// UUID-zero database checks are intentionally deferred: ADR-012 does not require them,
// and adding them needs a repository-wide identity-parity decision.
internal sealed class ProbeStatusProjectionConfiguration : IEntityTypeConfiguration<ProbeStatusProjection>
{
    public void Configure(EntityTypeBuilder<ProbeStatusProjection> b)
    {
        b.ToTable("probe_status_projections", t =>
        {
            t.HasCheckConstraint("ck_probe_status_projections_watermark", "(watermark_event_at IS NULL AND watermark_agent_id IS NULL AND watermark_result_id IS NULL) OR (watermark_event_at IS NOT NULL AND watermark_agent_id IS NOT NULL AND watermark_result_id IS NOT NULL)");
            t.HasCheckConstraint("ck_probe_status_projections_status", "underlying_status IN ('Unknown', 'Up', 'Degraded', 'Down', 'Recovering')");
            t.HasCheckConstraint("ck_probe_status_projections_counters", "consecutive_failure_count >= 0 AND consecutive_success_count >= 0 AND NOT (consecutive_failure_count > 0 AND consecutive_success_count > 0)");
            t.HasCheckConstraint("ck_probe_status_projections_state_version", "state_version >= 0");
            t.HasCheckConstraint("ck_probe_status_projections_down", "underlying_status <> 'Down' OR (consecutive_failure_count >= 1 AND consecutive_success_count = 0)");
            t.HasCheckConstraint("ck_probe_status_projections_recovering", "underlying_status <> 'Recovering' OR (consecutive_failure_count = 0 AND consecutive_success_count >= 1)");
        });
        b.HasKey(x => x.ProbeId);
        b.Property(x => x.ProbeId).HasColumnName("probe_id");
        b.Property(x => x.UnderlyingStatus).HasColumnName("underlying_status").HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.ConsecutiveFailureCount).HasColumnName("consecutive_failure_count");
        b.Property(x => x.ConsecutiveSuccessCount).HasColumnName("consecutive_success_count");
        b.Property(x => x.LastFreshEventAt).HasColumnName("last_fresh_event_at");
        b.Property(x => x.WatermarkEventAt).HasColumnName("watermark_event_at");
        b.Property(x => x.WatermarkAgentId).HasColumnName("watermark_agent_id");
        b.Property(x => x.WatermarkResultId).HasColumnName("watermark_result_id");
        b.Property(x => x.StateVersion).HasColumnName("state_version").IsConcurrencyToken();
        b.HasOne<Probe>().WithMany().HasForeignKey(x => x.ProbeId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class ProbeStatusPolicySnapshotConfiguration : IEntityTypeConfiguration<ProbeStatusPolicySnapshot>
{
    public void Configure(EntityTypeBuilder<ProbeStatusPolicySnapshot> b)
    {
        b.ToTable("probe_status_policy_snapshots", t =>
        {
            t.HasCheckConstraint("ck_probe_status_policy_snapshots_thresholds", "failure_threshold BETWEEN 1 AND 100 AND recovery_threshold BETWEEN 1 AND 100");
            t.HasCheckConstraint("ck_probe_status_policy_snapshots_warning_rtt", "warning_rtt_milliseconds IS NULL OR warning_rtt_milliseconds > 0");
            t.HasCheckConstraint("ck_probe_status_policy_snapshots_packet_loss", "warning_packet_loss_ratio IS NULL OR (warning_packet_loss_ratio > 0 AND warning_packet_loss_ratio <= 1)");
            t.HasCheckConstraint("ck_probe_status_policy_snapshots_lateness", "approved_lateness_seconds = 300 AND approved_future_skew_seconds = 60");
            t.HasCheckConstraint("ck_probe_status_policy_snapshots_version", "policy_version >= 1");
        });
        b.HasKey(x => x.Id);
        b.HasAlternateKey(x => new { x.Id, x.PolicyVersion }).HasName("ak_probe_status_policy_snapshots_id_version");
        b.Property(x => x.Id).HasColumnName("id");
        b.Property(x => x.PolicyVersion).HasColumnName("policy_version");
        b.Property(x => x.FailureThreshold).HasColumnName("failure_threshold");
        b.Property(x => x.RecoveryThreshold).HasColumnName("recovery_threshold");
        b.Property(x => x.WarningRttMilliseconds).HasColumnName("warning_rtt_milliseconds");
        b.Property(x => x.WarningPacketLossRatio).HasColumnName("warning_packet_loss_ratio").HasPrecision(18, 9);
        b.Property(x => x.ApprovedLatenessSeconds).HasColumnName("approved_lateness_seconds");
        b.Property(x => x.ApprovedFutureSkewSeconds).HasColumnName("approved_future_skew_seconds");
        b.Property(x => x.CreatedAt).HasColumnName("created_at");
    }
}

internal sealed class ProbeStatusPolicyBindingConfiguration : IEntityTypeConfiguration<ProbeStatusPolicyBinding>
{
    public void Configure(EntityTypeBuilder<ProbeStatusPolicyBinding> b)
    {
        b.ToTable("probe_status_policy_bindings", t => t.HasCheckConstraint("ck_probe_status_policy_bindings_version", "configuration_version >= 1"));
        b.HasKey(x => new { x.ProbeId, x.ConfigurationVersion });
        b.Property(x => x.ProbeId).HasColumnName("probe_id");
        b.Property(x => x.ConfigurationVersion).HasColumnName("configuration_version");
        b.Property(x => x.AgentGroupId).HasColumnName("agent_group_id");
        b.Property(x => x.PolicySnapshotId).HasColumnName("policy_snapshot_id");
        b.HasIndex(x => x.PolicySnapshotId).HasDatabaseName("ix_probe_status_policy_bindings_snapshot");
        b.HasOne<Probe>().WithMany().HasForeignKey(x => x.ProbeId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<ProbeStatusPolicySnapshot>().WithMany().HasForeignKey(x => x.PolicySnapshotId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<AgentConfigurationSnapshot>().WithMany()
            .HasForeignKey(x => new { x.AgentGroupId, x.ConfigurationVersion })
            .HasPrincipalKey(x => new { x.AgentGroupId, x.Version })
            .OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class AgentConfigurationEffectiveBoundaryConfiguration : IEntityTypeConfiguration<AgentConfigurationEffectiveBoundary>
{
    public void Configure(EntityTypeBuilder<AgentConfigurationEffectiveBoundary> b)
    {
        b.ToTable("agent_configuration_effective_boundaries", t =>
        {
            t.HasCheckConstraint("ck_agent_configuration_effective_boundaries_version", "configuration_version >= 1");
            t.HasCheckConstraint("ck_agent_configuration_effective_boundaries_applied", "source_acknowledgement_status = 'Applied'");
        });
        b.HasKey(x => new { x.AgentId, x.ConfigurationVersion });
        b.Property(x => x.AgentId).HasColumnName("agent_id");
        b.Property(x => x.ConfigurationVersion).HasColumnName("configuration_version");
        b.Property(x => x.SourceAcknowledgementId).HasColumnName("source_acknowledgement_id");
        b.Property(x => x.SourceAcknowledgementStatus).HasColumnName("source_acknowledgement_status").HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.AppliedAcknowledgementReceivedAt).HasColumnName("applied_acknowledgement_received_at");
        b.HasOne<AgentConfigurationAcknowledgement>().WithMany()
            .HasForeignKey(x => new { x.AgentId, x.SourceAcknowledgementId, x.ConfigurationVersion, x.SourceAcknowledgementStatus, x.AppliedAcknowledgementReceivedAt })
            .HasPrincipalKey(x => new { x.AgentId, x.Id, x.ConfigurationVersion, x.Status, x.ReceivedAt })
            .OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class ProbeResultProcessingDispositionConfiguration : IEntityTypeConfiguration<ProbeResultProcessingDisposition>
{
    public void Configure(EntityTypeBuilder<ProbeResultProcessingDisposition> b)
    {
        b.ToTable("probe_result_processing_dispositions", t =>
        {
            t.HasCheckConstraint("ck_probe_result_processing_dispositions_kind", "disposition IN ('StateDriving', 'LateOrder', 'FutureOrSkewSuspect', 'BeyondApprovedLateness', 'Disabled', 'HistoricalOther')");
            t.HasCheckConstraint("ck_probe_result_processing_dispositions_reason_code", "char_length(reason_code) BETWEEN 1 AND 64");
            t.HasCheckConstraint("ck_probe_result_processing_dispositions_lineage", "(resolved_policy_snapshot_id IS NULL AND resolved_policy_version IS NULL) OR (resolved_policy_snapshot_id IS NOT NULL AND resolved_policy_version IS NOT NULL)");
            t.HasCheckConstraint("ck_probe_result_processing_dispositions_state_driving", "disposition <> 'StateDriving' OR (resolved_policy_snapshot_id IS NOT NULL AND resolved_policy_version IS NOT NULL)");
        });
        b.HasKey(x => new { x.AgentId, x.ResultId });
        b.HasAlternateKey(x => new { x.AgentId, x.ResultId, x.Disposition })
            .HasName("ak_probe_result_processing_dispositions_identity_kind");
        b.Property(x => x.AgentId).HasColumnName("agent_id");
        b.Property(x => x.ResultId).HasColumnName("result_id");
        b.Property(x => x.ProbeId).HasColumnName("probe_id");
        b.Property(x => x.EventAt).HasColumnName("event_at");
        b.Property(x => x.Disposition).HasColumnName("disposition").HasConversion<string>().HasMaxLength(32);
        b.Property(x => x.ReasonCode).HasColumnName("reason_code").HasMaxLength(64);
        b.Property(x => x.ResolvedPolicySnapshotId).HasColumnName("resolved_policy_snapshot_id");
        b.Property(x => x.ResolvedPolicyVersion).HasColumnName("resolved_policy_version");
        b.Property(x => x.DecidedAt).HasColumnName("decided_at");
        b.HasIndex(x => new { x.ProbeId, x.DecidedAt }).HasDatabaseName("ix_probe_result_processing_dispositions_probe_decided");
        b.HasOne<ProbeResultLedgerEntry>().WithMany()
            .HasForeignKey(x => new { x.AgentId, x.ResultId, x.ProbeId, x.EventAt })
            .HasPrincipalKey(x => new { x.AgentId, x.ResultId, x.ProbeId, x.EndedAt })
            .OnDelete(DeleteBehavior.Restrict);
        b.HasOne<ProbeStatusPolicySnapshot>().WithMany()
            .HasForeignKey(x => new { x.ResolvedPolicySnapshotId, x.ResolvedPolicyVersion })
            .HasPrincipalKey(x => new { x.Id, x.PolicyVersion })
            .OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class ProbeResultStatusTransitionConfiguration : IEntityTypeConfiguration<ProbeResultStatusTransition>
{
    public void Configure(EntityTypeBuilder<ProbeResultStatusTransition> b)
    {
        b.ToTable("probe_result_status_transitions", t =>
        {
            t.HasCheckConstraint("ck_probe_result_status_transitions_from_status", "from_status IN ('Unknown', 'Up', 'Degraded', 'Down', 'Recovering')");
            t.HasCheckConstraint("ck_probe_result_status_transitions_to_status", "to_status IN ('Unknown', 'Up', 'Degraded', 'Down', 'Recovering')");
            t.HasCheckConstraint("ck_probe_result_status_transitions_status_change", "from_status <> to_status");
            t.HasCheckConstraint("ck_probe_result_status_transitions_reason_code", "char_length(reason_code) BETWEEN 1 AND 64");
            t.HasCheckConstraint("ck_probe_result_status_transitions_reason_code_value", "reason_code IN ('bootstrap-success', 'quality-degraded', 'quality-restored', 'failure-threshold-met', 'recovery-pending', 'recovery-threshold-met', 'recovery-failed')");
            t.HasCheckConstraint("ck_probe_result_status_transitions_processing_disposition", "processing_disposition = 'StateDriving'");
        });
        b.HasKey(x => new { x.AgentId, x.ResultId });
        b.Property(x => x.AgentId).HasColumnName("agent_id");
        b.Property(x => x.ResultId).HasColumnName("result_id");
        b.Property(x => x.ProbeId).HasColumnName("probe_id");
        b.Property(x => x.FromStatus).HasColumnName("from_status").HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.ToStatus).HasColumnName("to_status").HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.ReasonCode).HasColumnName("reason_code").HasMaxLength(64);
        b.Property(x => x.EventAt).HasColumnName("event_at");
        b.Property(x => x.ReceivedAt).HasColumnName("received_at");
        b.Property(x => x.ProcessingDisposition).HasColumnName("processing_disposition").HasConversion<string>().HasMaxLength(32);
        b.HasIndex(x => new { x.ProbeId, x.EventAt, x.AgentId, x.ResultId }).HasDatabaseName("ix_probe_result_status_transitions_probe_event");
        b.HasOne<ProbeResultLedgerEntry>().WithMany()
            .HasForeignKey(x => new { x.AgentId, x.ResultId, x.ProbeId, x.EventAt })
            .HasPrincipalKey(x => new { x.AgentId, x.ResultId, x.ProbeId, x.EndedAt })
            .OnDelete(DeleteBehavior.Restrict);
        b.HasOne<ProbeResultProcessingDisposition>().WithMany()
            .HasForeignKey(x => new { x.AgentId, x.ResultId, x.ProcessingDisposition })
            .HasPrincipalKey(x => new { x.AgentId, x.ResultId, x.Disposition })
            .OnDelete(DeleteBehavior.Restrict);
    }
}
