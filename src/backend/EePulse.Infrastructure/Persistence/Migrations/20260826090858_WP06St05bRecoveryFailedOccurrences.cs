using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EePulse.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class WP06St05bRecoveryFailedOccurrences : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_notification_suppression_contexts_eligibility",
                table: "notification_suppression_contexts");

            migrationBuilder.DropCheckConstraint(
                name: "ck_notification_suppression_contexts_reason",
                table: "notification_suppression_contexts");

            migrationBuilder.DropCheckConstraint(
                name: "ck_incident_lifecycle_events_key",
                table: "incident_lifecycle_events");

            migrationBuilder.DropCheckConstraint(
                name: "ck_incident_lifecycle_events_source",
                table: "incident_lifecycle_events");

            migrationBuilder.DropCheckConstraint(
                name: "ck_incident_lifecycle_events_type",
                table: "incident_lifecycle_events");

            migrationBuilder.AddColumn<int>(
                name: "occurrence_count",
                table: "availability_incidents",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddCheckConstraint(
                name: "ck_notification_suppression_contexts_eligibility",
                table: "notification_suppression_contexts",
                sql: "(lifecycle_event_key IN ('opened', 'resolved') AND eligibility = 'Eligible') OR (lifecycle_event_key LIKE 'occurrence:%' AND eligibility = 'Suppressed')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_notification_suppression_contexts_reason",
                table: "notification_suppression_contexts",
                sql: "(lifecycle_event_key = 'opened' AND reason_code = 'availability-down') OR (lifecycle_event_key = 'resolved' AND reason_code = 'confirmed-recovery') OR (lifecycle_event_key LIKE 'occurrence:%' AND reason_code = 'recovery-failed')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_incident_lifecycle_events_key",
                table: "incident_lifecycle_events",
                sql: "lifecycle_event_key IN ('opened', 'resolved') OR lifecycle_event_key = ('occurrence:' || lower(source_result_id::text))");

            migrationBuilder.AddCheckConstraint(
                name: "ck_incident_lifecycle_events_source",
                table: "incident_lifecycle_events",
                sql: "(lifecycle_event_type = 'Opened' AND lifecycle_event_key = 'opened' AND source_from_status <> 'Down' AND source_to_status = 'Down' AND source_reason_code = 'failure-threshold-met') OR (lifecycle_event_type = 'Resolved' AND lifecycle_event_key = 'resolved' AND source_from_status = 'Recovering' AND source_to_status IN ('Up', 'Degraded') AND source_reason_code = 'recovery-threshold-met') OR (lifecycle_event_type = 'Occurrence' AND lifecycle_event_key = ('occurrence:' || lower(source_result_id::text)) AND source_from_status = 'Recovering' AND source_to_status = 'Down' AND source_reason_code = 'recovery-failed')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_incident_lifecycle_events_type",
                table: "incident_lifecycle_events",
                sql: "lifecycle_event_type IN ('Opened', 'Resolved', 'Occurrence')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_availability_incidents_occurrence_count",
                table: "availability_incidents",
                sql: "occurrence_count >= 1");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_notification_suppression_contexts_eligibility",
                table: "notification_suppression_contexts");

            migrationBuilder.DropCheckConstraint(
                name: "ck_notification_suppression_contexts_reason",
                table: "notification_suppression_contexts");

            migrationBuilder.DropCheckConstraint(
                name: "ck_incident_lifecycle_events_key",
                table: "incident_lifecycle_events");

            migrationBuilder.DropCheckConstraint(
                name: "ck_incident_lifecycle_events_source",
                table: "incident_lifecycle_events");

            migrationBuilder.DropCheckConstraint(
                name: "ck_incident_lifecycle_events_type",
                table: "incident_lifecycle_events");

            migrationBuilder.DropCheckConstraint(
                name: "ck_availability_incidents_occurrence_count",
                table: "availability_incidents");

            migrationBuilder.DropColumn(
                name: "occurrence_count",
                table: "availability_incidents");

            migrationBuilder.AddCheckConstraint(
                name: "ck_notification_suppression_contexts_eligibility",
                table: "notification_suppression_contexts",
                sql: "eligibility = 'Eligible'");

            migrationBuilder.AddCheckConstraint(
                name: "ck_notification_suppression_contexts_reason",
                table: "notification_suppression_contexts",
                sql: "(lifecycle_event_key = 'opened' AND reason_code = 'availability-down') OR (lifecycle_event_key = 'resolved' AND reason_code = 'confirmed-recovery')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_incident_lifecycle_events_key",
                table: "incident_lifecycle_events",
                sql: "lifecycle_event_key IN ('opened', 'resolved')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_incident_lifecycle_events_source",
                table: "incident_lifecycle_events",
                sql: "(lifecycle_event_type = 'Opened' AND lifecycle_event_key = 'opened' AND source_from_status <> 'Down' AND source_to_status = 'Down' AND source_reason_code = 'failure-threshold-met') OR (lifecycle_event_type = 'Resolved' AND lifecycle_event_key = 'resolved' AND source_from_status = 'Recovering' AND source_to_status IN ('Up', 'Degraded') AND source_reason_code = 'recovery-threshold-met')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_incident_lifecycle_events_type",
                table: "incident_lifecycle_events",
                sql: "lifecycle_event_type IN ('Opened', 'Resolved')");
        }
    }
}
