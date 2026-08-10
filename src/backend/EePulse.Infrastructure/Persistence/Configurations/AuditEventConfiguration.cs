using EePulse.Domain.Auditing;
using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EePulse.Infrastructure.Persistence.Configurations;

internal sealed class AuditEventConfiguration : IEntityTypeConfiguration<AuditEvent>
{
    public void Configure(EntityTypeBuilder<AuditEvent> builder)
    {
        builder.ToTable("audit_events");
        builder.HasKey(audit => audit.Id);
        builder.Property(audit => audit.Id).HasColumnName("id");
        builder.Property(audit => audit.ActorId).HasColumnName("actor_id");
        builder.Property(audit => audit.Action).HasColumnName("action").HasMaxLength(100).IsRequired();
        builder.Property(audit => audit.EntityType).HasColumnName("entity_type").HasMaxLength(100).IsRequired();
        builder.Property(audit => audit.EntityId).HasColumnName("entity_id");
        builder.Property(audit => audit.BeforeJson).HasColumnName("before_json").HasColumnType("jsonb");
        builder.Property(audit => audit.AfterJson).HasColumnName("after_json").HasColumnType("jsonb");
        builder.Property(audit => audit.CorrelationId).HasColumnName("correlation_id").HasMaxLength(128).IsRequired();
        builder.Property(audit => audit.OccurredAt).HasColumnName("occurred_at");
        builder.Property(audit => audit.SourceIp)
            .HasColumnName("source_ip")
            .HasColumnType("inet")
            .HasConversion(
                address => address == null ? null : IPAddress.Parse(address),
                address => address == null ? null : address.ToString());
        builder.HasIndex(audit => new { audit.EntityType, audit.EntityId, audit.OccurredAt })
            .HasDatabaseName("ix_audit_events_entity_time");
        builder.HasIndex(audit => audit.OccurredAt).HasDatabaseName("ix_audit_events_occurred_at");
    }
}
