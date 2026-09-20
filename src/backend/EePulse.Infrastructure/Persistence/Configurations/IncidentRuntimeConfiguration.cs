using EePulse.Domain.Identity;
using EePulse.Domain.Status;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EePulse.Infrastructure.Persistence.Configurations;

internal sealed class AvailabilityIncidentRuntimeConfiguration : IEntityTypeConfiguration<AvailabilityIncident>
{
    public void Configure(EntityTypeBuilder<AvailabilityIncident> b)
    {
        b.ToTable("availability_incidents", t =>
        {
            t.HasCheckConstraint("ck_availability_incidents_acknowledgement_comment_text", "acknowledgement_comment IS NULL OR public.wp07_incident_text_is_valid(acknowledgement_comment)");
            t.HasCheckConstraint("ck_availability_incidents_resolution_note_text", "resolution_note IS NULL OR public.wp07_incident_text_is_valid(resolution_note)");
        });
    }
}

internal sealed class IncidentCommentConfiguration : IEntityTypeConfiguration<IncidentComment>
{
    public void Configure(EntityTypeBuilder<IncidentComment> b)
    {
        b.ToTable("incident_comments", t =>
        {
            t.HasCheckConstraint("ck_incident_comments_comment", "public.wp07_incident_text_is_valid(comment)");
        });
        b.HasKey(x => x.Id).HasName("pk_incident_comments");
        b.HasAlternateKey(x => x.IdempotencyKey).HasName("uq_incident_comments_idempotency_key");
        b.HasAlternateKey(x => new { x.IdempotencyKey, x.Id, x.IncidentId, x.AuthorId })
            .HasName("uq_incident_comments_receipt_outcome");
        Property(b.Property(x => x.Id).HasColumnName("id").HasColumnType("uuid").ValueGeneratedNever());
        Property(b.Property(x => x.IncidentId).HasColumnName("incident_id").HasColumnType("uuid").IsRequired());
        Property(b.Property(x => x.ProbeId).HasColumnName("probe_id").HasColumnType("uuid").IsRequired());
        Property(b.Property(x => x.AuthorId).HasColumnName("author_id").HasColumnType("uuid").IsRequired());
        Property(b.Property(x => x.Comment).HasColumnName("comment").HasColumnType("text").IsRequired());
        Property(b.Property(x => x.CreatedAt).HasColumnName("created_at").HasColumnType("timestamp with time zone").IsRequired());
        Property(b.Property(x => x.IdempotencyKey).HasColumnName("idempotency_key").HasColumnType("character varying(36)")
            .HasMaxLength(36).UseCollation("C").IsRequired());
        b.HasOne<AvailabilityIncident>().WithMany().HasForeignKey(x => new { x.IncidentId, x.ProbeId })
            .HasPrincipalKey(x => new { x.Id, x.ProbeId })
            .HasConstraintName("fk_incident_comments_availability_incident").OnDelete(DeleteBehavior.NoAction);
        b.HasOne<HumanPrincipal>().WithMany().HasForeignKey(x => x.AuthorId)
            .HasConstraintName("fk_incident_comments_human_principal").OnDelete(DeleteBehavior.NoAction);
        b.HasOne<IdempotencyReceipt>().WithMany().HasForeignKey(x => x.IdempotencyKey)
            .HasPrincipalKey(x => x.IdempotencyKey)
            .HasConstraintName("fk_incident_comments_idempotency_receipt").OnDelete(DeleteBehavior.NoAction);
    }

    private static void Property(PropertyBuilder property) => property.Metadata.SetAfterSaveBehavior(PropertySaveBehavior.Throw);
}

internal sealed class IncidentLifecycleActionConfiguration : IEntityTypeConfiguration<IncidentLifecycleAction>
{
    public void Configure(EntityTypeBuilder<IncidentLifecycleAction> b)
    {
        b.ToTable("incident_lifecycle_actions", t =>
        {
            t.HasCheckConstraint("ck_incident_lifecycle_actions_kind_reason", "(action_kind = 'acknowledgement' AND reason_code = 'operator-acknowledgement') OR (action_kind = 'manual_resolution' AND reason_code = 'manual-resolution')");
            t.HasCheckConstraint("ck_incident_lifecycle_actions_action_note", "public.wp07_incident_text_is_valid(action_note)");
        });
        b.HasKey(x => x.EventId).HasName("pk_incident_lifecycle_actions");
        b.HasAlternateKey(x => x.IdempotencyKey).HasName("uq_incident_lifecycle_actions_idempotency_key");
        b.HasAlternateKey(x => new { x.IdempotencyKey, x.EventId, x.IncidentId, x.ActorId, x.ActionKind })
            .HasName("uq_incident_lifecycle_actions_receipt_outcome");
        Property(b.Property(x => x.EventId).HasColumnName("event_id").HasColumnType("uuid").ValueGeneratedNever());
        Property(b.Property(x => x.IncidentId).HasColumnName("incident_id").HasColumnType("uuid").IsRequired());
        Property(b.Property(x => x.ProbeId).HasColumnName("probe_id").HasColumnType("uuid").IsRequired());
        Property(b.Property(x => x.ActorId).HasColumnName("actor_id").HasColumnType("uuid").IsRequired());
        Property(b.Property(x => x.ActionKind).HasColumnName("action_kind").HasColumnType("character varying(32)")
            .HasMaxLength(32).UseCollation("C").IsRequired());
        Property(b.Property(x => x.ReasonCode).HasColumnName("reason_code").HasColumnType("character varying(64)")
            .HasMaxLength(64).UseCollation("C").IsRequired());
        Property(b.Property(x => x.OccurredAt).HasColumnName("occurred_at").HasColumnType("timestamp with time zone").IsRequired());
        Property(b.Property(x => x.ActionNote).HasColumnName("action_note").HasColumnType("text").IsRequired());
        Property(b.Property(x => x.IdempotencyKey).HasColumnName("idempotency_key").HasColumnType("character varying(36)")
            .HasMaxLength(36).UseCollation("C").IsRequired());
        b.HasOne<AvailabilityIncident>().WithMany().HasForeignKey(x => new { x.IncidentId, x.ProbeId })
            .HasPrincipalKey(x => new { x.Id, x.ProbeId })
            .HasConstraintName("fk_incident_lifecycle_actions_availability_incident").OnDelete(DeleteBehavior.NoAction);
        b.HasOne<HumanPrincipal>().WithMany().HasForeignKey(x => x.ActorId)
            .HasConstraintName("fk_incident_lifecycle_actions_human_principal").OnDelete(DeleteBehavior.NoAction);
        b.HasOne<IdempotencyReceipt>().WithMany().HasForeignKey(x => x.IdempotencyKey)
            .HasPrincipalKey(x => x.IdempotencyKey)
            .HasConstraintName("fk_incident_lifecycle_actions_idempotency_receipt").OnDelete(DeleteBehavior.NoAction);
    }

    private static void Property(PropertyBuilder property) => property.Metadata.SetAfterSaveBehavior(PropertySaveBehavior.Throw);
}

internal sealed class IdempotencyReceiptConfiguration : IEntityTypeConfiguration<IdempotencyReceipt>
{
    public void Configure(EntityTypeBuilder<IdempotencyReceipt> b)
    {
        b.ToTable("idempotency_receipts", t =>
        {
            t.HasCheckConstraint("ck_idempotency_receipts_request_fingerprint_version", "request_fingerprint_version = 1");
            t.HasCheckConstraint("ck_idempotency_receipts_request_digest", "pg_catalog.octet_length(request_digest) = 32");
            t.HasCheckConstraint("ck_idempotency_receipts_response_status_valid", "response_status BETWEEN 100 AND 599");
            t.HasCheckConstraint("ck_idempotency_receipts_response_status_success", "response_status IN (200, 201)");
            t.HasCheckConstraint("ck_idempotency_receipts_outcome_status", "(outcome_kind = 'general_comment' AND response_status = 201) OR (outcome_kind IN ('acknowledgement', 'manual_resolution') AND response_status = 200)");
            t.HasCheckConstraint("ck_idempotency_receipts_content_type_nonempty", "response_content_type COLLATE \"C\" <> ''");
            t.HasCheckConstraint("ck_idempotency_receipts_response_etag_strong_quoted", "response_etag COLLATE \"C\" ~ '^\"[!#-~]+\"$'");
            t.HasCheckConstraint("ck_idempotency_receipts_route_nonempty", "route_template COLLATE \"C\" <> ''");
            t.HasCheckConstraint("ck_idempotency_receipts_outcome_kind", "outcome_kind IN ('acknowledgement', 'general_comment', 'manual_resolution')");
            t.HasCheckConstraint("ck_idempotency_receipts_outcome_references", "(outcome_kind = 'general_comment' AND incident_comment_id IS NOT NULL AND incident_lifecycle_action_id IS NULL) OR (outcome_kind = 'acknowledgement' AND incident_comment_id IS NULL AND incident_lifecycle_action_id IS NOT NULL) OR (outcome_kind = 'manual_resolution' AND incident_comment_id IS NULL AND incident_lifecycle_action_id IS NOT NULL)");
            t.HasCheckConstraint("ck_idempotency_receipts_completed_at", "completed_at >= created_at");
        });
        b.HasKey(x => x.IdempotencyKey).HasName("pk_idempotency_receipts");
        b.HasIndex(x => x.IncidentCommentId).IsUnique()
            .HasDatabaseName("uq_idempotency_receipts_incident_comment");
        b.HasIndex(x => x.IncidentLifecycleActionId).IsUnique()
            .HasDatabaseName("uq_idempotency_receipts_lifecycle_action");
        Property(b.Property(x => x.IdempotencyKey).HasColumnName("idempotency_key").HasColumnType("character varying(36)").HasMaxLength(36).UseCollation("C").IsRequired());
        Property(b.Property(x => x.RouteTemplate).HasColumnName("route_template").HasColumnType("text").UseCollation("C").IsRequired());
        Property(b.Property(x => x.IncidentId).HasColumnName("incident_id").HasColumnType("uuid").IsRequired());
        Property(b.Property(x => x.ActorId).HasColumnName("actor_id").HasColumnType("uuid").IsRequired());
        Property(b.Property(x => x.RequestFingerprintVersion).HasColumnName("request_fingerprint_version").HasColumnType("smallint").IsRequired());
        Property(b.Property(x => x.RequestDigest).HasColumnName("request_digest").HasColumnType("bytea").IsRequired());
        Property(b.Property(x => x.ResponseStatus).HasColumnName("response_status").HasColumnType("integer").IsRequired());
        Property(b.Property(x => x.ResponseBody).HasColumnName("response_body").HasColumnType("bytea").IsRequired());
        Property(b.Property(x => x.ResponseContentType).HasColumnName("response_content_type").HasColumnType("text").IsRequired());
        Property(b.Property(x => x.ResponseEtag).HasColumnName("response_etag").HasColumnType("text").IsRequired());
        Property(b.Property(x => x.OutcomeKind).HasColumnName("outcome_kind").HasColumnType("character varying(32)").HasMaxLength(32).UseCollation("C").IsRequired());
        Property(b.Property(x => x.IncidentCommentId).HasColumnName("incident_comment_id").HasColumnType("uuid").IsRequired(false));
        Property(b.Property(x => x.IncidentLifecycleActionId).HasColumnName("incident_lifecycle_action_id").HasColumnType("uuid").IsRequired(false));
        Property(b.Property(x => x.CreatedAt).HasColumnName("created_at").HasColumnType("timestamp with time zone").IsRequired());
        Property(b.Property(x => x.CompletedAt).HasColumnName("completed_at").HasColumnType("timestamp with time zone").IsRequired());
        b.HasOne<IncidentComment>().WithMany()
            .HasForeignKey(x => new { x.IdempotencyKey, x.IncidentCommentId, x.IncidentId, x.ActorId })
            .HasPrincipalKey(x => new { x.IdempotencyKey, x.Id, x.IncidentId, x.AuthorId })
            .HasConstraintName("fk_idempotency_receipts_incident_comment_outcome").OnDelete(DeleteBehavior.NoAction);
        b.HasOne<IncidentLifecycleAction>().WithMany()
            .HasForeignKey(x => new { x.IdempotencyKey, x.IncidentLifecycleActionId, x.IncidentId, x.ActorId, x.OutcomeKind })
            .HasPrincipalKey(x => new { x.IdempotencyKey, x.EventId, x.IncidentId, x.ActorId, x.ActionKind })
            .HasConstraintName("fk_idempotency_receipts_lifecycle_action_outcome").OnDelete(DeleteBehavior.NoAction);
    }

    private static void Property(PropertyBuilder property) => property.Metadata.SetAfterSaveBehavior(PropertySaveBehavior.Throw);
}
