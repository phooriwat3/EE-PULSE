using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable
#pragma warning disable CA1861

namespace EePulse.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class WP06St09bResultFreshnessExpiryCause : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "probe_freshness_expiry_causes",
                columns: table => new
                {
                    cause_id = table.Column<Guid>(type: "uuid", nullable: false),
                    probe_id = table.Column<Guid>(type: "uuid", nullable: false),
                    cause_type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    source_agent_id = table.Column<Guid>(type: "uuid", nullable: false),
                    source_result_id = table.Column<Guid>(type: "uuid", nullable: false),
                    source_cursor_event_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    source_last_fresh_event_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    source_configuration_version = table.Column<long>(type: "bigint", nullable: false),
                    source_agent_group_id = table.Column<Guid>(type: "uuid", nullable: false),
                    source_disposition = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    policy_snapshot_id = table.Column<Guid>(type: "uuid", nullable: false),
                    policy_version = table.Column<int>(type: "integer", nullable: false),
                    freshness_interval_seconds = table.Column<int>(type: "integer", nullable: false),
                    freshness_grace_seconds = table.Column<int>(type: "integer", nullable: false),
                    due_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    requested_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "clock_timestamp()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_probe_freshness_expiry_causes", x => x.cause_id);
                    table.UniqueConstraint("ak_probe_freshness_expiry_causes_source", x => new { x.probe_id, x.source_agent_id, x.source_result_id, x.source_cursor_event_at });
                    table.CheckConstraint("ck_probe_freshness_expiry_causes_due_at", "due_at >= source_last_fresh_event_at");
                    table.CheckConstraint("ck_probe_freshness_expiry_causes_inputs", "freshness_interval_seconds >= 1 AND freshness_grace_seconds >= 1");
                    table.CheckConstraint("ck_probe_freshness_expiry_causes_source_disposition", "source_disposition = 'StateDriving'");
                    table.CheckConstraint("ck_probe_freshness_expiry_causes_source_freshness", "source_cursor_event_at = source_last_fresh_event_at");
                    table.CheckConstraint("ck_probe_freshness_expiry_causes_type", "cause_type = 'ResultFreshnessExpiry'");
                    table.CheckConstraint("ck_probe_freshness_expiry_causes_versions", "source_configuration_version >= 1 AND policy_version >= 1");
                    table.ForeignKey(
                        name: "FK_probe_freshness_expiry_causes_agent_configuration_snapshots~",
                        columns: x => new { x.source_agent_group_id, x.source_configuration_version },
                        principalTable: "agent_configuration_snapshots",
                        principalColumns: new[] { "agent_group_id", "version" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_probe_freshness_expiry_causes_agents_source_agent_id",
                        column: x => x.source_agent_id,
                        principalTable: "agents",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_probe_freshness_expiry_causes_probe_result_ledger_source_ag~",
                        columns: x => new { x.source_agent_id, x.source_result_id, x.probe_id, x.source_cursor_event_at },
                        principalTable: "probe_result_ledger",
                        principalColumns: new[] { "agent_id", "result_id", "probe_id", "ended_at" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_probe_freshness_expiry_causes_probe_result_processing_dispo~",
                        columns: x => new { x.source_agent_id, x.source_result_id, x.source_disposition },
                        principalTable: "probe_result_processing_dispositions",
                        principalColumns: new[] { "agent_id", "result_id", "disposition" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_probe_freshness_expiry_causes_probe_status_policy_snapshots~",
                        columns: x => new { x.policy_snapshot_id, x.policy_version },
                        principalTable: "probe_status_policy_snapshots",
                        principalColumns: new[] { "id", "policy_version" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_probe_freshness_expiry_causes_probes_probe_id",
                        column: x => x.probe_id,
                        principalTable: "probes",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_probe_freshness_expiry_causes_due_probe",
                table: "probe_freshness_expiry_causes",
                columns: new[] { "due_at", "probe_id" });

            migrationBuilder.CreateIndex(
                name: "IX_probe_freshness_expiry_causes_policy_snapshot_id_policy_ver~",
                table: "probe_freshness_expiry_causes",
                columns: new[] { "policy_snapshot_id", "policy_version" });

            migrationBuilder.CreateIndex(
                name: "IX_probe_freshness_expiry_causes_source_agent_group_id_source_~",
                table: "probe_freshness_expiry_causes",
                columns: new[] { "source_agent_group_id", "source_configuration_version" });

            migrationBuilder.CreateIndex(
                name: "IX_probe_freshness_expiry_causes_source_agent_id_source_resul~1",
                table: "probe_freshness_expiry_causes",
                columns: new[] { "source_agent_id", "source_result_id", "probe_id", "source_cursor_event_at" });

            migrationBuilder.CreateIndex(
                name: "IX_probe_freshness_expiry_causes_source_agent_id_source_result~",
                table: "probe_freshness_expiry_causes",
                columns: new[] { "source_agent_id", "source_result_id", "source_disposition" });

            migrationBuilder.Sql("""
                CREATE FUNCTION wp06_probe_freshness_expiry_cause_validate_source()
                RETURNS trigger
                LANGUAGE plpgsql
                AS $$
                DECLARE
                    source_configuration_version bigint;
                    source_probe_id uuid;
                    source_event_at timestamptz;
                    source_policy_snapshot_id uuid;
                    source_policy_version integer;
                    projection_cursor_event_at timestamptz;
                    projection_cursor_agent_id uuid;
                    projection_cursor_result_id uuid;
                    projection_last_fresh_event_at timestamptz;
                    source_agent_group_id uuid;
                    source_heartbeat_interval_seconds integer;
                    configuration_payload jsonb;
                    matching_probe_count integer;
                    matching_interval_seconds integer;
                BEGIN
                    PERFORM pg_advisory_xact_lock(hashtextextended(NEW.probe_id::text, 0));

                    SELECT configuration_version, probe_id, ended_at
                    INTO source_configuration_version, source_probe_id, source_event_at
                    FROM probe_result_ledger
                    WHERE agent_id = NEW.source_agent_id
                      AND result_id = NEW.source_result_id
                      AND probe_id = NEW.probe_id
                      AND ended_at = NEW.source_cursor_event_at;
                    IF NOT FOUND
                       OR source_configuration_version IS DISTINCT FROM NEW.source_configuration_version
                       OR source_probe_id IS DISTINCT FROM NEW.probe_id
                       OR source_event_at IS DISTINCT FROM NEW.source_cursor_event_at THEN
                        RAISE EXCEPTION 'WP-06 freshness expiry cause source is invalid';
                    END IF;

                    SELECT probe_id, event_at, resolved_policy_snapshot_id, resolved_policy_version
                    INTO source_probe_id, source_event_at, source_policy_snapshot_id, source_policy_version
                    FROM probe_result_processing_dispositions
                    WHERE agent_id = NEW.source_agent_id
                      AND result_id = NEW.source_result_id
                      AND disposition = 'StateDriving';
                    IF NOT FOUND
                       OR source_probe_id IS DISTINCT FROM NEW.probe_id
                       OR source_event_at IS DISTINCT FROM NEW.source_cursor_event_at THEN
                        RAISE EXCEPTION 'WP-06 freshness expiry cause source is invalid';
                    END IF;
                    IF source_policy_snapshot_id IS NULL OR source_policy_version IS NULL THEN
                        RAISE EXCEPTION 'WP-06 freshness expiry cause policy lineage is invalid';
                    END IF;
                    IF source_policy_version IS DISTINCT FROM NEW.policy_version THEN
                        RAISE EXCEPTION 'WP-06 freshness expiry cause policy version is invalid';
                    END IF;
                    IF source_policy_snapshot_id IS DISTINCT FROM NEW.policy_snapshot_id THEN
                        RAISE EXCEPTION 'WP-06 freshness expiry cause policy identity is invalid';
                    END IF;

                    SELECT watermark_event_at, watermark_agent_id, watermark_result_id, last_fresh_event_at
                    INTO projection_cursor_event_at, projection_cursor_agent_id, projection_cursor_result_id, projection_last_fresh_event_at
                    FROM probe_status_projections
                    WHERE probe_id = NEW.probe_id;
                    IF NOT FOUND
                       OR projection_cursor_event_at IS DISTINCT FROM NEW.source_cursor_event_at
                       OR projection_cursor_agent_id IS DISTINCT FROM NEW.source_agent_id
                       OR projection_cursor_result_id IS DISTINCT FROM NEW.source_result_id
                       OR projection_last_fresh_event_at IS DISTINCT FROM NEW.source_last_fresh_event_at THEN
                        RAISE EXCEPTION 'WP-06 freshness expiry cause source is invalid';
                    END IF;

                    SELECT agent_group_id, heartbeat_interval_seconds
                    INTO source_agent_group_id, source_heartbeat_interval_seconds
                    FROM agents
                    WHERE id = NEW.source_agent_id;
                    IF NOT FOUND
                       OR source_agent_group_id IS DISTINCT FROM NEW.source_agent_group_id
                       OR NEW.freshness_grace_seconds IS DISTINCT FROM greatest(60, 3 * source_heartbeat_interval_seconds) THEN
                        RAISE EXCEPTION 'WP-06 freshness expiry cause source is invalid';
                    END IF;

                    SELECT payload INTO configuration_payload
                    FROM agent_configuration_snapshots
                    WHERE agent_group_id = NEW.source_agent_group_id
                      AND version = NEW.source_configuration_version;
                    IF NOT FOUND OR jsonb_typeof(configuration_payload -> 'probes') IS DISTINCT FROM 'array' THEN
                        RAISE EXCEPTION 'WP-06 freshness expiry cause source is invalid';
                    END IF;

                    SELECT count(*), min(CASE
                        WHEN jsonb_typeof(probe_entry -> 'intervalSeconds') = 'number'
                         AND probe_entry ->> 'intervalSeconds' ~ '^[0-9]+$'
                        THEN (probe_entry ->> 'intervalSeconds')::integer
                    END)
                    INTO matching_probe_count, matching_interval_seconds
                    FROM jsonb_array_elements(configuration_payload -> 'probes') AS probe_entry
                    WHERE probe_entry ->> 'probeId' = NEW.probe_id::text;
                    IF matching_probe_count <> 1
                       OR matching_interval_seconds IS DISTINCT FROM NEW.freshness_interval_seconds
                       OR NEW.due_at IS DISTINCT FROM NEW.source_last_fresh_event_at + make_interval(secs => greatest(2 * NEW.freshness_interval_seconds, NEW.freshness_grace_seconds)) THEN
                        RAISE EXCEPTION 'WP-06 freshness expiry cause source is invalid';
                    END IF;

                    NEW.requested_at := date_trunc('microseconds', clock_timestamp());
                    RETURN NEW;
                END;
                $$;

                CREATE TRIGGER tr_probe_freshness_expiry_causes_validate_source
                BEFORE INSERT ON probe_freshness_expiry_causes
                FOR EACH ROW EXECUTE FUNCTION wp06_probe_freshness_expiry_cause_validate_source();

                CREATE TRIGGER tr_probe_freshness_expiry_causes_append_only
                BEFORE UPDATE OR DELETE ON probe_freshness_expiry_causes
                FOR EACH ROW EXECUTE FUNCTION wp06_status_processing_reject_append_only_mutation();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP TRIGGER IF EXISTS tr_probe_freshness_expiry_causes_append_only ON probe_freshness_expiry_causes;
                DROP TRIGGER IF EXISTS tr_probe_freshness_expiry_causes_validate_source ON probe_freshness_expiry_causes;
                DROP FUNCTION IF EXISTS wp06_probe_freshness_expiry_cause_validate_source();
                """);
            migrationBuilder.DropTable(
                name: "probe_freshness_expiry_causes");
        }
    }
}
#pragma warning restore CA1861
