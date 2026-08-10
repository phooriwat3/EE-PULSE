using EePulse.Domain.Inventory;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EePulse.Infrastructure.Persistence.Configurations;

internal sealed class ProbeConfiguration : IEntityTypeConfiguration<Probe>
{
    public void Configure(EntityTypeBuilder<Probe> builder)
    {
        builder.ToTable("probes");
        builder.HasKey(probe => probe.Id);
        builder.Property(probe => probe.Id).HasColumnName("id");
        builder.Property(probe => probe.DeviceId).HasColumnName("device_id");
        builder.Property(probe => probe.AgentGroupId).HasColumnName("agent_group_id");
        builder.Property(probe => probe.Type).HasColumnName("type").HasConversion<string>().HasMaxLength(20);
        builder.Property(probe => probe.IntervalSeconds).HasColumnName("interval_seconds");
        builder.Property(probe => probe.TimeoutMilliseconds).HasColumnName("timeout_milliseconds");
        builder.Property(probe => probe.AttemptCount).HasColumnName("attempt_count");
        builder.Property(probe => probe.WarningRttMilliseconds).HasColumnName("warning_rtt_milliseconds");
        builder.Property(probe => probe.CriticalRttMilliseconds).HasColumnName("critical_rtt_milliseconds");
        builder.Property(probe => probe.FailureThreshold).HasColumnName("failure_threshold");
        builder.Property(probe => probe.RecoveryThreshold).HasColumnName("recovery_threshold");
        builder.Property(probe => probe.Enabled).HasColumnName("enabled");
        builder.Property(probe => probe.ConfigVersion).HasColumnName("config_version");
        builder.Property(probe => probe.RowVersion).HasColumnName("row_version").IsConcurrencyToken();
        builder.HasOne<Device>().WithMany().HasForeignKey(probe => probe.DeviceId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<AgentGroup>().WithMany().HasForeignKey(probe => probe.AgentGroupId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(probe => new { probe.DeviceId, probe.Type }).HasDatabaseName("ix_probes_device_type");
    }
}
