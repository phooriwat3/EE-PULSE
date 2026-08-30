using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EePulse.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class WP06St04aIncidentResolution : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_notification_suppression_contexts_reason",
                table: "notification_suppression_contexts");

            migrationBuilder.DropCheckConstraint(
                name: "ck_incident_lifecycle_events_key",
                table: "incident_lifecycle_events");

            migrationBuilder.DropCheckConstraint(
                name: "ck_incident_lifecycle_events_opening_source",
                table: "incident_lifecycle_events");

            migrationBuilder.DropCheckConstraint(
                name: "ck_incident_lifecycle_events_type",
                table: "incident_lifecycle_events");

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

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
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

            migrationBuilder.AddCheckConstraint(
                name: "ck_notification_suppression_contexts_reason",
                table: "notification_suppression_contexts",
                sql: "reason_code = 'availability-down'");

            migrationBuilder.AddCheckConstraint(
                name: "ck_incident_lifecycle_events_key",
                table: "incident_lifecycle_events",
                sql: "lifecycle_event_key = 'opened'");

            migrationBuilder.AddCheckConstraint(
                name: "ck_incident_lifecycle_events_opening_source",
                table: "incident_lifecycle_events",
                sql: "source_from_status <> 'Down' AND source_to_status = 'Down' AND source_reason_code = 'failure-threshold-met'");

            migrationBuilder.AddCheckConstraint(
                name: "ck_incident_lifecycle_events_type",
                table: "incident_lifecycle_events",
                sql: "lifecycle_event_type = 'Opened'");
        }
    }
}
