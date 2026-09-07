using EePulse.Domain.Agents;
using EePulse.Domain.Inventory;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using System.Net;

namespace EePulse.Infrastructure.Persistence.Configurations;

internal sealed class AgentConfiguration : IEntityTypeConfiguration<Agent>
{
    public void Configure(EntityTypeBuilder<Agent> b)
    {
        b.ToTable("agents"); b.HasKey(x => x.Id);
        b.Property(x => x.Id).HasColumnName("id"); b.Property(x => x.AgentGroupId).HasColumnName("agent_group_id");
        b.Property(x => x.ClientInstanceId).HasColumnName("client_instance_id"); b.Property(x => x.Name).HasColumnName("name").HasMaxLength(255);
        b.Property(x => x.MachineName).HasColumnName("machine_name").HasMaxLength(255); b.Property(x => x.AgentVersion).HasColumnName("agent_version").HasMaxLength(64);
        b.Property(x => x.SelfHealth).HasColumnName("self_health").HasConversion<string>().HasMaxLength(20); b.Property(x => x.QueueDepth).HasColumnName("queue_depth");
        b.Property(x => x.Status).HasColumnName("status").HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.LastHeartbeatAt).HasColumnName("last_heartbeat_at"); b.Property(x => x.LastReportedAt).HasColumnName("last_reported_at");
        b.Property(x => x.HeartbeatIntervalSeconds).HasColumnName("heartbeat_interval_seconds"); b.Property(x => x.DesiredConfigurationVersion).HasColumnName("desired_configuration_version");
        b.Property(x => x.LastAppliedConfigurationVersion).HasColumnName("last_applied_configuration_version"); b.Property(x => x.LastConfigurationAcknowledgedAt).HasColumnName("last_configuration_acknowledged_at");
        b.Property(x => x.ClockSkewSuspected).HasColumnName("clock_skew_suspected"); b.Property(x => x.CredentialExpiresAt).HasColumnName("credential_expires_at");
        b.Property(x => x.CreatedAt).HasColumnName("created_at"); b.Property(x => x.RevokedAt).HasColumnName("revoked_at"); b.Property(x => x.RevocationReason).HasColumnName("revocation_reason").HasMaxLength(50);
        b.Property(x => x.RowVersion).HasColumnName("row_version").IsConcurrencyToken();
        b.HasOne<AgentGroup>().WithMany().HasForeignKey(x => x.AgentGroupId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => x.ClientInstanceId).IsUnique().HasFilter("revoked_at IS NULL").HasDatabaseName("ux_agents_active_client_instance");
        b.HasIndex(x => new { x.AgentGroupId, x.Status }).HasDatabaseName("ix_agents_group_status"); b.HasIndex(x => new { x.Status, x.LastHeartbeatAt }).HasDatabaseName("ix_agents_status_heartbeat");
    }
}

internal sealed class AgentAllowedNetworkConfiguration : IEntityTypeConfiguration<AgentAllowedNetwork>
{
    public void Configure(EntityTypeBuilder<AgentAllowedNetwork> b) { b.ToTable("agent_allowed_networks"); b.HasKey(x => new { x.AgentId, x.Network }); b.Property(x => x.AgentId).HasColumnName("agent_id"); b.Property(x => x.Network).HasColumnName("network").HasColumnType("cidr").HasConversion(value => IPNetwork.Parse(value), value => value.ToString()); b.HasOne<Agent>().WithMany().HasForeignKey(x => x.AgentId).OnDelete(DeleteBehavior.Restrict); }
}
internal sealed class AgentPolicyAllowedNetworkConfiguration : IEntityTypeConfiguration<AgentPolicyAllowedNetwork>
{
    public void Configure(EntityTypeBuilder<AgentPolicyAllowedNetwork> b) { b.ToTable("agent_policy_allowed_networks"); b.HasKey(x => new { x.AgentId, x.Network }); b.Property(x => x.AgentId).HasColumnName("agent_id"); b.Property(x => x.Network).HasColumnName("network").HasColumnType("cidr").HasConversion(value => IPNetwork.Parse(value), value => value.ToString()); b.HasOne<Agent>().WithMany().HasForeignKey(x => x.AgentId).OnDelete(DeleteBehavior.Restrict); }
}
internal sealed class AgentGroupAllowedNetworkConfiguration : IEntityTypeConfiguration<AgentGroupAllowedNetwork>
{
    public void Configure(EntityTypeBuilder<AgentGroupAllowedNetwork> b) { b.ToTable("agent_group_allowed_networks"); b.HasKey(x => new { x.AgentGroupId, x.Network }); b.Property(x => x.AgentGroupId).HasColumnName("agent_group_id"); b.Property(x => x.Network).HasColumnName("network").HasColumnType("cidr").HasConversion(value => IPNetwork.Parse(value), value => value.ToString()); b.HasOne<AgentGroup>().WithMany().HasForeignKey(x => x.AgentGroupId).OnDelete(DeleteBehavior.Restrict); }
}
internal sealed class AgentEnrollmentTokenConfiguration : IEntityTypeConfiguration<AgentEnrollmentToken>
{
    public void Configure(EntityTypeBuilder<AgentEnrollmentToken> b) { b.ToTable("agent_enrollment_tokens", t => t.HasCheckConstraint("ck_agent_enrollment_token_digest", "octet_length(digest) = 32")); b.HasKey(x => x.Id); b.Property(x => x.Id).HasColumnName("id"); b.Property(x => x.AgentGroupId).HasColumnName("agent_group_id"); b.Property(x => x.Digest).HasColumnName("digest").HasColumnType("bytea"); b.Property(x => x.Label).HasColumnName("label").HasMaxLength(200); b.Property(x => x.ExpectedMachineName).HasColumnName("expected_machine_name").HasMaxLength(255); b.Property(x => x.ExpiresAt).HasColumnName("expires_at"); b.Property(x => x.UsedAt).HasColumnName("used_at"); b.Property(x => x.UsedByAgentId).HasColumnName("used_by_agent_id"); b.Property(x => x.RevokedAt).HasColumnName("revoked_at"); b.Property(x => x.CreatedBy).HasColumnName("created_by"); b.Property(x => x.CreatedAt).HasColumnName("created_at"); b.Property(x => x.RowVersion).HasColumnName("row_version").IsConcurrencyToken(); b.HasOne<AgentGroup>().WithMany().HasForeignKey(x => x.AgentGroupId).OnDelete(DeleteBehavior.Restrict); }
}
internal sealed class AgentEnrollmentTokenAllowedNetworkConfiguration : IEntityTypeConfiguration<AgentEnrollmentTokenAllowedNetwork>
{
    public void Configure(EntityTypeBuilder<AgentEnrollmentTokenAllowedNetwork> b) { b.ToTable("agent_enrollment_token_allowed_networks"); b.HasKey(x => new { x.TokenId, x.Network }); b.Property(x => x.TokenId).HasColumnName("token_id"); b.Property(x => x.Network).HasColumnName("network").HasColumnType("cidr").HasConversion(value => IPNetwork.Parse(value), value => value.ToString()); b.HasOne<AgentEnrollmentToken>().WithMany().HasForeignKey(x => x.TokenId).OnDelete(DeleteBehavior.Restrict); }
}
internal sealed class AgentCredentialConfiguration : IEntityTypeConfiguration<AgentCredential>
{
    public void Configure(EntityTypeBuilder<AgentCredential> b)
    {
        b.ToTable("agent_credentials", t => t.HasCheckConstraint("ck_agent_credential_digest", "octet_length(digest) = 32"));
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).HasColumnName("id");
        b.Property(x => x.AgentId).HasColumnName("agent_id");
        b.Property(x => x.Digest).HasColumnName("digest").HasColumnType("bytea");
        b.Property(x => x.State).HasColumnName("state").HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.ExpiresAt).HasColumnName("expires_at");
        b.Property(x => x.RotateAfter).HasColumnName("rotate_after");
        b.Property(x => x.CreatedAt).HasColumnName("created_at");
        b.Property(x => x.PendingExpiresAt).HasColumnName("pending_expires_at");
        b.Property(x => x.FirstUsedAt).HasColumnName("first_used_at");
        b.Property(x => x.RevokedAt).HasColumnName("revoked_at");
        b.HasOne<Agent>().WithMany().HasForeignKey(x => x.AgentId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex([nameof(AgentCredential.AgentId)], "UX_AgentCredential_Active_Model")
            .IsUnique().HasFilter("state = 'Active'").HasDatabaseName("ux_agent_credentials_active");
        b.HasIndex([nameof(AgentCredential.AgentId)], "UX_AgentCredential_Pending_Model")
            .IsUnique().HasFilter("state = 'Pending'").HasDatabaseName("ux_agent_credentials_pending");
    }
}
internal sealed class AgentConfigurationSnapshotConfiguration : IEntityTypeConfiguration<AgentConfigurationSnapshot>
{
    public void Configure(EntityTypeBuilder<AgentConfigurationSnapshot> b) { b.ToTable("agent_configuration_snapshots", t => t.HasCheckConstraint("ck_agent_snapshot_digest", "octet_length(payload_digest) = 32")); b.HasKey(x => new { x.AgentGroupId, x.Version }); b.Property(x => x.AgentGroupId).HasColumnName("agent_group_id"); b.Property(x => x.Version).HasColumnName("version"); b.Property(x => x.Payload).HasColumnName("payload").HasColumnType("jsonb"); b.Property(x => x.PayloadDigest).HasColumnName("payload_digest").HasColumnType("bytea"); b.Property(x => x.GeneratedAt).HasColumnName("generated_at"); b.Property(x => x.RollbackOfVersion).HasColumnName("rollback_of_version"); b.HasOne<AgentGroup>().WithMany().HasForeignKey(x => x.AgentGroupId).OnDelete(DeleteBehavior.Restrict); }
}
internal sealed class AgentConfigurationAcknowledgementConfiguration : IEntityTypeConfiguration<AgentConfigurationAcknowledgement>
{
    public void Configure(EntityTypeBuilder<AgentConfigurationAcknowledgement> b) { b.ToTable("agent_configuration_acknowledgements"); b.HasKey(x => new { x.AgentId, x.Id }); b.HasAlternateKey(x => new { x.AgentId, x.Id, x.ConfigurationVersion, x.Status, x.ReceivedAt }).HasName("ak_agent_configuration_acknowledgements_boundary_source"); b.Property(x => x.Id).HasColumnName("acknowledgement_id"); b.Property(x => x.AgentId).HasColumnName("agent_id"); b.Property(x => x.ConfigurationVersion).HasColumnName("configuration_version"); b.Property(x => x.Status).HasColumnName("status").HasConversion<string>().HasMaxLength(20); b.Property(x => x.AppliedAt).HasColumnName("applied_at"); b.Property(x => x.SentAt).HasColumnName("sent_at"); b.Property(x => x.ReceivedAt).HasColumnName("received_at"); b.Property(x => x.ErrorCode).HasColumnName("error_code").HasMaxLength(100); b.Property(x => x.CentralEffectiveConfigurationVersion).HasColumnName("central_effective_configuration_version"); b.Property(x => x.DesiredConfigurationVersion).HasColumnName("desired_configuration_version"); b.HasOne<Agent>().WithMany().HasForeignKey(x => x.AgentId).OnDelete(DeleteBehavior.Restrict); }
}
internal sealed class AgentHeartbeatReceiptConfiguration : IEntityTypeConfiguration<AgentHeartbeatReceipt>
{
    public void Configure(EntityTypeBuilder<AgentHeartbeatReceipt> b) { b.ToTable("agent_heartbeat_receipts"); b.HasKey(x => new { x.AgentId, x.HeartbeatId }); b.Property(x => x.AgentId).HasColumnName("agent_id"); b.Property(x => x.HeartbeatId).HasColumnName("heartbeat_id"); b.Property(x => x.ReceivedAt).HasColumnName("received_at"); b.Property(x => x.ResponseJson).HasColumnName("response_json").HasColumnType("jsonb"); b.HasIndex(x => x.ReceivedAt).HasDatabaseName("ix_agent_heartbeat_receipts_received"); b.HasOne<Agent>().WithMany().HasForeignKey(x => x.AgentId).OnDelete(DeleteBehavior.Restrict); }
}

internal sealed class ProbeResultLedgerEntryConfiguration : IEntityTypeConfiguration<ProbeResultLedgerEntry>
{
    public void Configure(EntityTypeBuilder<ProbeResultLedgerEntry> b)
    {
        b.ToTable("probe_result_ledger", t => t.HasCheckConstraint("ck_probe_result_ledger_payload_digest", "octet_length(immutable_payload_digest) = 32"));
        b.HasKey(x => new { x.AgentId, x.ResultId });
        b.Property(x => x.AgentId).HasColumnName("agent_id"); b.Property(x => x.ResultId).HasColumnName("result_id");
        b.Property(x => x.ProbeId).HasColumnName("probe_id"); b.Property(x => x.ConfigurationVersion).HasColumnName("configuration_version");
        b.Property(x => x.StartedAt).HasColumnName("started_at"); b.Property(x => x.EndedAt).HasColumnName("ended_at");
        b.Property(x => x.AttemptCount).HasColumnName("attempt_count"); b.Property(x => x.SuccessfulAttemptCount).HasColumnName("successful_attempt_count");
        b.Property(x => x.PacketLossRatio).HasColumnName("packet_loss_ratio").HasPrecision(18, 9);
        b.Property(x => x.MinRttMilliseconds).HasColumnName("min_rtt_milliseconds").HasPrecision(18, 6);
        b.Property(x => x.AverageRttMilliseconds).HasColumnName("average_rtt_milliseconds").HasPrecision(18, 6);
        b.Property(x => x.MaxRttMilliseconds).HasColumnName("max_rtt_milliseconds").HasPrecision(18, 6);
        b.Property(x => x.ErrorCategory).HasColumnName("error_category").HasMaxLength(32);
        b.Property(x => x.ImmutablePayloadDigest).HasColumnName("immutable_payload_digest").HasColumnType("bytea"); b.Property(x => x.ReceivedAt).HasColumnName("received_at");
        b.HasIndex(x => x.ProbeId).HasDatabaseName("IX_probe_result_ledger_probe_id");
        b.HasIndex(x => x.ReceivedAt).HasDatabaseName("ix_probe_result_ledger_received");
        b.HasAlternateKey(x => new { x.AgentId, x.ResultId, x.ProbeId, x.EndedAt }).HasName("ak_probe_result_ledger_processing_identity");
        b.HasIndex(x => new { x.ProbeId, x.EndedAt, x.AgentId, x.ResultId }).HasDatabaseName("ix_probe_result_ledger_state_order");
        b.HasOne<Agent>().WithMany().HasForeignKey(x => x.AgentId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Probe>().WithMany().HasForeignKey(x => x.ProbeId).OnDelete(DeleteBehavior.Restrict);
    }
}
