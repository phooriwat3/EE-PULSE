using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1861 // Avoid constant arrays as arguments

namespace EePulse.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class WP06St10bHeartbeatExpiryPersistence : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "probe_heartbeat_expiry_causes",
                columns: table => new
                {
                    cause_id = table.Column<Guid>(type: "uuid", nullable: false),
                    probe_id = table.Column<Guid>(type: "uuid", nullable: false),
                    cause_type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    authority_agent_id = table.Column<Guid>(type: "uuid", nullable: false),
                    source_result_id = table.Column<Guid>(type: "uuid", nullable: false),
                    source_cursor_event_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    source_last_heartbeat_received_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    source_heartbeat_interval_seconds = table.Column<int>(type: "integer", nullable: false),
                    source_configuration_version = table.Column<long>(type: "bigint", nullable: false),
                    source_agent_group_id = table.Column<Guid>(type: "uuid", nullable: false),
                    source_disposition = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    policy_snapshot_id = table.Column<Guid>(type: "uuid", nullable: false),
                    policy_version = table.Column<int>(type: "integer", nullable: false),
                    due_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    requested_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "clock_timestamp()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_probe_heartbeat_expiry_causes", x => x.cause_id);
                    table.UniqueConstraint("ak_probe_heartbeat_expiry_causes_lineage", x => new { x.cause_id, x.probe_id, x.policy_snapshot_id, x.policy_version });
                    table.UniqueConstraint("ak_probe_heartbeat_expiry_causes_source", x => new { x.probe_id, x.authority_agent_id, x.source_result_id, x.source_cursor_event_at, x.source_last_heartbeat_received_at, x.source_heartbeat_interval_seconds });
                    table.CheckConstraint("ck_probe_heartbeat_expiry_causes_due_at", "due_at >= source_last_heartbeat_received_at");
                    table.CheckConstraint("ck_probe_heartbeat_expiry_causes_heartbeat_interval", "source_heartbeat_interval_seconds BETWEEN 15 AND 30");
                    table.CheckConstraint("ck_probe_heartbeat_expiry_causes_source_disposition", "source_disposition = 'StateDriving'");
                    table.CheckConstraint("ck_probe_heartbeat_expiry_causes_type", "cause_type = 'AgentHeartbeatExpiry'");
                    table.CheckConstraint("ck_probe_heartbeat_expiry_causes_versions", "source_configuration_version >= 1 AND policy_version >= 1");
                    table.ForeignKey(
                        name: "FK_probe_heartbeat_expiry_causes_agent_configuration_snapshots~",
                        columns: x => new { x.source_agent_group_id, x.source_configuration_version },
                        principalTable: "agent_configuration_snapshots",
                        principalColumns: new[] { "agent_group_id", "version" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_probe_heartbeat_expiry_causes_agents_authority_agent_id",
                        column: x => x.authority_agent_id,
                        principalTable: "agents",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_probe_heartbeat_expiry_causes_probe_result_ledger_authority~",
                        columns: x => new { x.authority_agent_id, x.source_result_id, x.probe_id, x.source_cursor_event_at },
                        principalTable: "probe_result_ledger",
                        principalColumns: new[] { "agent_id", "result_id", "probe_id", "ended_at" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_probe_heartbeat_expiry_causes_probe_result_processing_dispo~",
                        columns: x => new { x.authority_agent_id, x.source_result_id, x.source_disposition },
                        principalTable: "probe_result_processing_dispositions",
                        principalColumns: new[] { "agent_id", "result_id", "disposition" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_probe_heartbeat_expiry_causes_probe_status_policy_snapshots~",
                        columns: x => new { x.policy_snapshot_id, x.policy_version },
                        principalTable: "probe_status_policy_snapshots",
                        principalColumns: new[] { "id", "policy_version" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_probe_heartbeat_expiry_causes_probes_probe_id",
                        column: x => x.probe_id,
                        principalTable: "probes",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "probe_heartbeat_expiry_cause_dispositions",
                columns: table => new
                {
                    cause_id = table.Column<Guid>(type: "uuid", nullable: false),
                    probe_id = table.Column<Guid>(type: "uuid", nullable: false),
                    policy_snapshot_id = table.Column<Guid>(type: "uuid", nullable: false),
                    policy_version = table.Column<int>(type: "integer", nullable: false),
                    outcome = table.Column<string>(type: "varchar(16)", maxLength: 16, nullable: false),
                    reason_code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    expiry_cutoff_received_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    applied_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_probe_heartbeat_expiry_cause_dispositions", x => x.cause_id);
                    table.UniqueConstraint("ak_probe_heartbeat_expiry_cause_dispositions_cause_outcome", x => new { x.cause_id, x.outcome });
                    table.CheckConstraint("ck_probe_heartbeat_expiry_cause_dispositions_outcome", "outcome IN ('Applied', 'NoOp')");
                    table.CheckConstraint("ck_probe_heartbeat_expiry_cause_dispositions_shape", "(outcome = 'Applied' AND reason_code = 'agent-heartbeat-expired' AND applied_at = expiry_cutoff_received_at) OR (outcome = 'NoOp' AND reason_code IN ('projection-missing', 'authority-watermark-superseded', 'authority-heartbeat-advanced', 'visible-already-unknown') AND applied_at IS NULL)");
                    table.ForeignKey(
                        name: "FK_probe_heartbeat_expiry_cause_dispositions_probe_heartbeat_e~",
                        columns: x => new { x.cause_id, x.probe_id, x.policy_snapshot_id, x.policy_version },
                        principalTable: "probe_heartbeat_expiry_causes",
                        principalColumns: new[] { "cause_id", "probe_id", "policy_snapshot_id", "policy_version" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_probe_heartbeat_expiry_cause_dispositions_probe_status_poli~",
                        columns: x => new { x.policy_snapshot_id, x.policy_version },
                        principalTable: "probe_status_policy_snapshots",
                        principalColumns: new[] { "id", "policy_version" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_probe_heartbeat_expiry_cause_dispositions_probes_probe_id",
                        column: x => x.probe_id,
                        principalTable: "probes",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "probe_heartbeat_expiry_cause_transitions",
                columns: table => new
                {
                    cause_id = table.Column<Guid>(type: "uuid", nullable: false),
                    probe_id = table.Column<Guid>(type: "uuid", nullable: false),
                    policy_snapshot_id = table.Column<Guid>(type: "uuid", nullable: false),
                    policy_version = table.Column<int>(type: "integer", nullable: false),
                    disposition_outcome = table.Column<string>(type: "varchar(16)", maxLength: 16, nullable: false),
                    from_visible_status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    to_visible_status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    reason_code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    applied_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_probe_heartbeat_expiry_cause_transitions", x => x.cause_id);
                    table.CheckConstraint("ck_probe_heartbeat_expiry_cause_transitions_disposition_outcome", "disposition_outcome = 'Applied'");
                    table.CheckConstraint("ck_probe_heartbeat_expiry_cause_transitions_from_visible_status", "from_visible_status IN ('Up', 'Degraded', 'Down', 'Recovering')");
                    table.CheckConstraint("ck_probe_heartbeat_expiry_cause_transitions_reason_code", "reason_code = 'agent-heartbeat-expired'");
                    table.CheckConstraint("ck_probe_heartbeat_expiry_cause_transitions_to_visible_status", "to_visible_status = 'Unknown'");
                    table.ForeignKey(
                        name: "FK_probe_heartbeat_expiry_cause_transitions_probe_heartbeat_ex~",
                        columns: x => new { x.cause_id, x.disposition_outcome },
                        principalTable: "probe_heartbeat_expiry_cause_dispositions",
                        principalColumns: new[] { "cause_id", "outcome" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_probe_heartbeat_expiry_cause_transitions_probe_heartbeat_e~1",
                        columns: x => new { x.cause_id, x.probe_id, x.policy_snapshot_id, x.policy_version },
                        principalTable: "probe_heartbeat_expiry_causes",
                        principalColumns: new[] { "cause_id", "probe_id", "policy_snapshot_id", "policy_version" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_probe_heartbeat_expiry_cause_transitions_probe_status_polic~",
                        columns: x => new { x.policy_snapshot_id, x.policy_version },
                        principalTable: "probe_status_policy_snapshots",
                        principalColumns: new[] { "id", "policy_version" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_probe_heartbeat_expiry_cause_transitions_probes_probe_id",
                        column: x => x.probe_id,
                        principalTable: "probes",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_probe_heartbeat_expiry_cause_dispositions_cause_id_probe_id~",
                table: "probe_heartbeat_expiry_cause_dispositions",
                columns: new[] { "cause_id", "probe_id", "policy_snapshot_id", "policy_version" });

            migrationBuilder.CreateIndex(
                name: "IX_probe_heartbeat_expiry_cause_dispositions_policy_snapshot_i~",
                table: "probe_heartbeat_expiry_cause_dispositions",
                columns: new[] { "policy_snapshot_id", "policy_version" });

            migrationBuilder.CreateIndex(
                name: "IX_probe_heartbeat_expiry_cause_dispositions_probe_id",
                table: "probe_heartbeat_expiry_cause_dispositions",
                column: "probe_id");

            migrationBuilder.CreateIndex(
                name: "IX_probe_heartbeat_expiry_cause_transitions_cause_id_dispositi~",
                table: "probe_heartbeat_expiry_cause_transitions",
                columns: new[] { "cause_id", "disposition_outcome" });

            migrationBuilder.CreateIndex(
                name: "IX_probe_heartbeat_expiry_cause_transitions_cause_id_probe_id_~",
                table: "probe_heartbeat_expiry_cause_transitions",
                columns: new[] { "cause_id", "probe_id", "policy_snapshot_id", "policy_version" });

            migrationBuilder.CreateIndex(
                name: "IX_probe_heartbeat_expiry_cause_transitions_policy_snapshot_id~",
                table: "probe_heartbeat_expiry_cause_transitions",
                columns: new[] { "policy_snapshot_id", "policy_version" });

            migrationBuilder.CreateIndex(
                name: "IX_probe_heartbeat_expiry_cause_transitions_probe_id",
                table: "probe_heartbeat_expiry_cause_transitions",
                column: "probe_id");

            migrationBuilder.CreateIndex(
                name: "IX_probe_heartbeat_expiry_causes_authority_agent_id_source_re~1",
                table: "probe_heartbeat_expiry_causes",
                columns: new[] { "authority_agent_id", "source_result_id", "probe_id", "source_cursor_event_at" });

            migrationBuilder.CreateIndex(
                name: "IX_probe_heartbeat_expiry_causes_authority_agent_id_source_res~",
                table: "probe_heartbeat_expiry_causes",
                columns: new[] { "authority_agent_id", "source_result_id", "source_disposition" });

            migrationBuilder.CreateIndex(
                name: "ix_probe_heartbeat_expiry_causes_due_probe",
                table: "probe_heartbeat_expiry_causes",
                columns: new[] { "due_at", "probe_id" });

            migrationBuilder.CreateIndex(
                name: "IX_probe_heartbeat_expiry_causes_policy_snapshot_id_policy_ver~",
                table: "probe_heartbeat_expiry_causes",
                columns: new[] { "policy_snapshot_id", "policy_version" });

            migrationBuilder.CreateIndex(
                name: "IX_probe_heartbeat_expiry_causes_source_agent_group_id_source_~",
                table: "probe_heartbeat_expiry_causes",
                columns: new[] { "source_agent_group_id", "source_configuration_version" });

            migrationBuilder.Sql("""
                CREATE FUNCTION fn_validate_probe_heartbeat_expiry_cause()
                RETURNS trigger
                LANGUAGE plpgsql
                AS $$
                DECLARE
                    authority_agent_group_id uuid;
                    authority_last_heartbeat_at timestamptz;
                    authority_heartbeat_interval_seconds integer;
                    projection_watermark_event_at timestamptz;
                    projection_watermark_agent_id uuid;
                    projection_watermark_result_id uuid;
                    ledger_configuration_version bigint;
                    disposition_probe_id uuid;
                    disposition_event_at timestamptz;
                    disposition_policy_snapshot_id uuid;
                    disposition_policy_version integer;
                    expected_due_at timestamptz;
                BEGIN
                    SELECT agent_group_id, last_heartbeat_at, heartbeat_interval_seconds
                    INTO authority_agent_group_id, authority_last_heartbeat_at, authority_heartbeat_interval_seconds
                    FROM agents
                    WHERE id = NEW.authority_agent_id
                    FOR SHARE;
                    IF NOT FOUND
                       OR authority_last_heartbeat_at IS NULL
                       OR authority_last_heartbeat_at IS DISTINCT FROM NEW.source_last_heartbeat_received_at
                       OR authority_heartbeat_interval_seconds IS DISTINCT FROM NEW.source_heartbeat_interval_seconds
                       OR authority_heartbeat_interval_seconds NOT BETWEEN 15 AND 30
                       OR authority_agent_group_id IS DISTINCT FROM NEW.source_agent_group_id THEN
                        RAISE EXCEPTION 'WP-06 heartbeat expiry cause source is invalid' USING ERRCODE = 'P0001';
                    END IF;

                    PERFORM pg_advisory_xact_lock(hashtextextended(NEW.probe_id::text, 0));

                    SELECT watermark_event_at, watermark_agent_id, watermark_result_id
                    INTO projection_watermark_event_at, projection_watermark_agent_id, projection_watermark_result_id
                    FROM probe_status_projections
                    WHERE probe_id = NEW.probe_id
                    FOR UPDATE;
                    IF NOT FOUND
                       OR projection_watermark_event_at IS DISTINCT FROM NEW.source_cursor_event_at
                       OR projection_watermark_agent_id IS DISTINCT FROM NEW.authority_agent_id
                       OR projection_watermark_result_id IS DISTINCT FROM NEW.source_result_id THEN
                        RAISE EXCEPTION 'WP-06 heartbeat expiry cause source is invalid' USING ERRCODE = 'P0001';
                    END IF;

                    SELECT configuration_version
                    INTO ledger_configuration_version
                    FROM probe_result_ledger
                    WHERE agent_id = NEW.authority_agent_id
                      AND result_id = NEW.source_result_id
                      AND probe_id = NEW.probe_id
                      AND ended_at = NEW.source_cursor_event_at;
                    IF NOT FOUND OR ledger_configuration_version IS DISTINCT FROM NEW.source_configuration_version THEN
                        RAISE EXCEPTION 'WP-06 heartbeat expiry cause source is invalid' USING ERRCODE = 'P0001';
                    END IF;

                    SELECT probe_id, event_at, resolved_policy_snapshot_id, resolved_policy_version
                    INTO disposition_probe_id, disposition_event_at, disposition_policy_snapshot_id, disposition_policy_version
                    FROM probe_result_processing_dispositions
                    WHERE agent_id = NEW.authority_agent_id
                      AND result_id = NEW.source_result_id
                      AND disposition = 'StateDriving';
                    IF NOT FOUND
                       OR disposition_probe_id IS DISTINCT FROM NEW.probe_id
                       OR disposition_event_at IS DISTINCT FROM NEW.source_cursor_event_at
                       OR disposition_policy_snapshot_id IS NULL
                       OR disposition_policy_version IS NULL
                       OR disposition_policy_snapshot_id IS DISTINCT FROM NEW.policy_snapshot_id
                       OR disposition_policy_version IS DISTINCT FROM NEW.policy_version THEN
                        RAISE EXCEPTION 'WP-06 heartbeat expiry cause source is invalid' USING ERRCODE = 'P0001';
                    END IF;

                    PERFORM 1
                    FROM agent_configuration_snapshots
                    WHERE agent_group_id = NEW.source_agent_group_id
                      AND version = NEW.source_configuration_version;
                    IF NOT FOUND
                       OR NEW.cause_type IS DISTINCT FROM 'AgentHeartbeatExpiry'
                       OR NEW.source_disposition IS DISTINCT FROM 'StateDriving'
                       OR NEW.source_heartbeat_interval_seconds NOT BETWEEN 15 AND 30 THEN
                        RAISE EXCEPTION 'WP-06 heartbeat expiry cause source is invalid' USING ERRCODE = 'P0001';
                    END IF;

                    BEGIN
                        expected_due_at := NEW.source_last_heartbeat_received_at + make_interval(secs => greatest(60, 3 * NEW.source_heartbeat_interval_seconds));
                    EXCEPTION WHEN datetime_field_overflow THEN
                        RAISE EXCEPTION 'WP-06 heartbeat expiry cause source is invalid' USING ERRCODE = 'P0001';
                    END;
                    IF expected_due_at IS DISTINCT FROM NEW.due_at THEN
                        RAISE EXCEPTION 'WP-06 heartbeat expiry cause source is invalid' USING ERRCODE = 'P0001';
                    END IF;

                    NEW.requested_at := date_trunc('microseconds', clock_timestamp());
                    RETURN NEW;
                END;
                $$;

                CREATE TRIGGER tr_probe_heartbeat_expiry_causes_validate
                BEFORE INSERT ON probe_heartbeat_expiry_causes
                FOR EACH ROW EXECUTE FUNCTION fn_validate_probe_heartbeat_expiry_cause();

                CREATE TRIGGER tr_probe_heartbeat_expiry_causes_append_only
                BEFORE UPDATE OR DELETE ON probe_heartbeat_expiry_causes
                FOR EACH ROW EXECUTE FUNCTION wp06_status_processing_reject_append_only_mutation();

                CREATE TRIGGER tr_probe_heartbeat_expiry_cause_dispositions_append_only
                BEFORE UPDATE OR DELETE ON probe_heartbeat_expiry_cause_dispositions
                FOR EACH ROW EXECUTE FUNCTION wp06_status_processing_reject_append_only_mutation();

                CREATE TRIGGER tr_probe_heartbeat_expiry_cause_transitions_append_only
                BEFORE UPDATE OR DELETE ON probe_heartbeat_expiry_cause_transitions
                FOR EACH ROW EXECUTE FUNCTION wp06_status_processing_reject_append_only_mutation();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP TRIGGER IF EXISTS tr_probe_heartbeat_expiry_cause_transitions_append_only ON probe_heartbeat_expiry_cause_transitions;
                DROP TRIGGER IF EXISTS tr_probe_heartbeat_expiry_cause_dispositions_append_only ON probe_heartbeat_expiry_cause_dispositions;
                DROP TRIGGER IF EXISTS tr_probe_heartbeat_expiry_causes_append_only ON probe_heartbeat_expiry_causes;
                DROP TRIGGER IF EXISTS tr_probe_heartbeat_expiry_causes_validate ON probe_heartbeat_expiry_causes;
                DROP FUNCTION IF EXISTS fn_validate_probe_heartbeat_expiry_cause();
                """);
            migrationBuilder.DropTable(
                name: "probe_heartbeat_expiry_cause_transitions");

            migrationBuilder.DropTable(
                name: "probe_heartbeat_expiry_cause_dispositions");

            migrationBuilder.DropTable(
                name: "probe_heartbeat_expiry_causes");
        }
    }
}

#pragma warning restore CA1861 // Avoid constant arrays as arguments
