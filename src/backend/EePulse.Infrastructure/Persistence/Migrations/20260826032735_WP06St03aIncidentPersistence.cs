using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable
#pragma warning disable CA1861

namespace EePulse.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class WP06St03aIncidentPersistence : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "open_incident_id",
                table: "probe_status_projections",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddUniqueConstraint(
                name: "ak_probe_result_status_transitions_opening_source",
                table: "probe_result_status_transitions",
                columns: new[] { "agent_id", "result_id", "probe_id", "from_status", "to_status", "reason_code", "processing_disposition" });

            migrationBuilder.CreateTable(
                name: "availability_incidents",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    probe_id = table.Column<Guid>(type: "uuid", nullable: false),
                    rule_key = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    opened_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    acknowledged_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    acknowledged_by = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    acknowledgement_comment = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    resolved_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    resolved_by = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    resolution_note = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_availability_incidents", x => x.id);
                    table.UniqueConstraint("ak_availability_incidents_id_probe", x => new { x.id, x.probe_id });
                    table.CheckConstraint("ck_availability_incidents_lifecycle", "(acknowledged_at IS NULL AND acknowledged_by IS NULL AND acknowledgement_comment IS NULL) OR (acknowledged_at IS NOT NULL AND acknowledged_by IS NOT NULL AND acknowledgement_comment IS NOT NULL)");
                    table.CheckConstraint("ck_availability_incidents_resolution", "(resolved_at IS NULL AND resolved_by IS NULL AND resolution_note IS NULL) OR (resolved_at IS NOT NULL AND resolved_by IS NOT NULL AND resolution_note IS NOT NULL)");
                    table.CheckConstraint("ck_availability_incidents_rule_key", "rule_key = 'availability-down'");
                    table.CheckConstraint("ck_availability_incidents_status", "status IN ('Open', 'Acknowledged', 'Resolved')");
                    table.CheckConstraint("ck_availability_incidents_status_lifecycle", "(status = 'Open' AND acknowledged_at IS NULL AND resolved_at IS NULL) OR (status = 'Acknowledged' AND acknowledged_at IS NOT NULL AND resolved_at IS NULL) OR (status = 'Resolved' AND resolved_at IS NOT NULL)");
                    table.CheckConstraint("ck_availability_incidents_timestamps", "(acknowledged_at IS NULL OR opened_at <= acknowledged_at) AND (resolved_at IS NULL OR opened_at <= resolved_at) AND (acknowledged_at IS NULL OR resolved_at IS NULL OR acknowledged_at <= resolved_at)");
                    table.ForeignKey(
                        name: "FK_availability_incidents_probes_probe_id",
                        column: x => x.probe_id,
                        principalTable: "probes",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "incident_lifecycle_events",
                columns: table => new
                {
                    event_id = table.Column<Guid>(type: "uuid", nullable: false),
                    incident_id = table.Column<Guid>(type: "uuid", nullable: false),
                    probe_id = table.Column<Guid>(type: "uuid", nullable: false),
                    source_agent_id = table.Column<Guid>(type: "uuid", nullable: false),
                    source_result_id = table.Column<Guid>(type: "uuid", nullable: false),
                    source_from_status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    source_to_status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    source_reason_code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    policy_snapshot_id = table.Column<Guid>(type: "uuid", nullable: false),
                    policy_version = table.Column<int>(type: "integer", nullable: false),
                    lifecycle_event_type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    lifecycle_event_key = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    processing_disposition = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_incident_lifecycle_events", x => x.event_id);
                    table.UniqueConstraint("ak_incident_lifecycle_events_incident_key_policy", x => new { x.incident_id, x.lifecycle_event_key, x.policy_version });
                    table.UniqueConstraint("ak_incident_lifecycle_events_pairing", x => new { x.event_id, x.incident_id, x.lifecycle_event_key, x.policy_version });
                    table.CheckConstraint("ck_incident_lifecycle_events_disposition", "processing_disposition = 'StateDriving'");
                    table.CheckConstraint("ck_incident_lifecycle_events_key", "lifecycle_event_key = 'opened'");
                    table.CheckConstraint("ck_incident_lifecycle_events_opening_source", "source_from_status <> 'Down' AND source_to_status = 'Down' AND source_reason_code = 'failure-threshold-met'");
                    table.CheckConstraint("ck_incident_lifecycle_events_type", "lifecycle_event_type = 'Opened'");
                    table.ForeignKey(
                        name: "FK_incident_lifecycle_events_availability_incidents_incident_i~",
                        columns: x => new { x.incident_id, x.probe_id },
                        principalTable: "availability_incidents",
                        principalColumns: new[] { "id", "probe_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_incident_lifecycle_events_probe_result_processing_dispositi~",
                        columns: x => new { x.source_agent_id, x.source_result_id, x.processing_disposition },
                        principalTable: "probe_result_processing_dispositions",
                        principalColumns: new[] { "agent_id", "result_id", "disposition" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_incident_lifecycle_events_probe_result_status_transitions_s~",
                        columns: x => new { x.source_agent_id, x.source_result_id, x.probe_id, x.source_from_status, x.source_to_status, x.source_reason_code, x.processing_disposition },
                        principalTable: "probe_result_status_transitions",
                        principalColumns: new[] { "agent_id", "result_id", "probe_id", "from_status", "to_status", "reason_code", "processing_disposition" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_incident_lifecycle_events_probe_status_policy_snapshots_pol~",
                        columns: x => new { x.policy_snapshot_id, x.policy_version },
                        principalTable: "probe_status_policy_snapshots",
                        principalColumns: new[] { "id", "policy_version" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "notification_suppression_contexts",
                columns: table => new
                {
                    event_id = table.Column<Guid>(type: "uuid", nullable: false),
                    incident_id = table.Column<Guid>(type: "uuid", nullable: false),
                    lifecycle_event_key = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    policy_version = table.Column<int>(type: "integer", nullable: false),
                    eligibility = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    reason_code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    evaluated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_notification_suppression_contexts", x => x.event_id);
                    table.CheckConstraint("ck_notification_suppression_contexts_eligibility", "eligibility = 'Eligible'");
                    table.CheckConstraint("ck_notification_suppression_contexts_reason", "reason_code = 'availability-down'");
                    table.ForeignKey(
                        name: "FK_notification_suppression_contexts_incident_lifecycle_events~",
                        columns: x => new { x.event_id, x.incident_id, x.lifecycle_event_key, x.policy_version },
                        principalTable: "incident_lifecycle_events",
                        principalColumns: new[] { "event_id", "incident_id", "lifecycle_event_key", "policy_version" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_probe_status_projections_open_incident_id_probe_id",
                table: "probe_status_projections",
                columns: new[] { "open_incident_id", "probe_id" });

            migrationBuilder.CreateIndex(
                name: "ux_availability_incidents_active_probe_rule",
                table: "availability_incidents",
                columns: new[] { "probe_id", "rule_key" },
                unique: true,
                filter: "status IN ('Open', 'Acknowledged')");

            migrationBuilder.CreateIndex(
                name: "IX_incident_lifecycle_events_incident_id_probe_id",
                table: "incident_lifecycle_events",
                columns: new[] { "incident_id", "probe_id" });

            migrationBuilder.CreateIndex(
                name: "IX_incident_lifecycle_events_policy_snapshot_id_policy_version",
                table: "incident_lifecycle_events",
                columns: new[] { "policy_snapshot_id", "policy_version" });

            migrationBuilder.CreateIndex(
                name: "IX_incident_lifecycle_events_source_agent_id_source_result_id_~",
                table: "incident_lifecycle_events",
                columns: new[] { "source_agent_id", "source_result_id", "processing_disposition" });

            migrationBuilder.CreateIndex(
                name: "IX_incident_lifecycle_events_source_agent_id_source_result_id~1",
                table: "incident_lifecycle_events",
                columns: new[] { "source_agent_id", "source_result_id", "probe_id", "source_from_status", "source_to_status", "source_reason_code", "processing_disposition" });

            migrationBuilder.CreateIndex(
                name: "ux_incident_lifecycle_events_opening_source",
                table: "incident_lifecycle_events",
                columns: new[] { "source_agent_id", "source_result_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_notification_suppression_contexts_event_id_incident_id_life~",
                table: "notification_suppression_contexts",
                columns: new[] { "event_id", "incident_id", "lifecycle_event_key", "policy_version" });

            migrationBuilder.CreateIndex(
                name: "ux_notification_suppression_contexts_event_key",
                table: "notification_suppression_contexts",
                columns: new[] { "incident_id", "lifecycle_event_key", "policy_version" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_probe_status_projections_availability_incidents_open_incide~",
                table: "probe_status_projections",
                columns: new[] { "open_incident_id", "probe_id" },
                principalTable: "availability_incidents",
                principalColumns: new[] { "id", "probe_id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.Sql("""
                CREATE FUNCTION wp06_incident_lifecycle_event_validate_policy_lineage()
                RETURNS trigger
                LANGUAGE plpgsql
                AS $$
                DECLARE
                    source_policy_snapshot_id uuid;
                    source_policy_version integer;
                BEGIN
                    SELECT resolved_policy_snapshot_id, resolved_policy_version
                    INTO source_policy_snapshot_id, source_policy_version
                    FROM probe_result_processing_dispositions
                    WHERE agent_id = NEW.source_agent_id
                      AND result_id = NEW.source_result_id
                      AND disposition = 'StateDriving';

                    IF NOT FOUND
                       OR source_policy_snapshot_id IS NULL
                       OR source_policy_version IS NULL
                       OR source_policy_snapshot_id IS DISTINCT FROM NEW.policy_snapshot_id
                       OR source_policy_version IS DISTINCT FROM NEW.policy_version THEN
                        RAISE EXCEPTION 'WP-06 incident lifecycle event policy lineage is invalid';
                    END IF;

                    RETURN NEW;
                END;
                $$;
                """);

            migrationBuilder.Sql("""
                CREATE TRIGGER tr_incident_lifecycle_events_policy_lineage
                BEFORE INSERT ON incident_lifecycle_events
                FOR EACH ROW EXECUTE FUNCTION wp06_incident_lifecycle_event_validate_policy_lineage();
                """);

            migrationBuilder.Sql("""
                CREATE TRIGGER tr_incident_lifecycle_events_append_only
                BEFORE UPDATE OR DELETE ON incident_lifecycle_events
                FOR EACH ROW EXECUTE FUNCTION wp06_status_processing_reject_append_only_mutation();
                """);

            migrationBuilder.Sql("""
                CREATE TRIGGER tr_notification_suppression_contexts_append_only
                BEFORE UPDATE OR DELETE ON notification_suppression_contexts
                FOR EACH ROW EXECUTE FUNCTION wp06_status_processing_reject_append_only_mutation();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS tr_incident_lifecycle_events_policy_lineage ON incident_lifecycle_events;");
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS tr_incident_lifecycle_events_append_only ON incident_lifecycle_events;");
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS tr_notification_suppression_contexts_append_only ON notification_suppression_contexts;");
            migrationBuilder.Sql("DROP FUNCTION IF EXISTS wp06_incident_lifecycle_event_validate_policy_lineage();");

            migrationBuilder.DropForeignKey(
                name: "FK_probe_status_projections_availability_incidents_open_incide~",
                table: "probe_status_projections");

            migrationBuilder.DropTable(
                name: "notification_suppression_contexts");

            migrationBuilder.DropTable(
                name: "incident_lifecycle_events");

            migrationBuilder.DropTable(
                name: "availability_incidents");

            migrationBuilder.DropIndex(
                name: "IX_probe_status_projections_open_incident_id_probe_id",
                table: "probe_status_projections");

            migrationBuilder.DropUniqueConstraint(
                name: "ak_probe_result_status_transitions_opening_source",
                table: "probe_result_status_transitions");

            migrationBuilder.DropColumn(
                name: "open_incident_id",
                table: "probe_status_projections");
        }
    }
}
#pragma warning restore CA1861
