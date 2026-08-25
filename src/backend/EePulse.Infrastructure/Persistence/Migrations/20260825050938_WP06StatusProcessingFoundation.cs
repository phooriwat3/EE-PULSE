using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1861

namespace EePulse.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class WP06StatusProcessingFoundation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddUniqueConstraint(
                name: "ak_probe_result_ledger_processing_identity",
                table: "probe_result_ledger",
                columns: new[] { "agent_id", "result_id", "probe_id", "ended_at" });

            migrationBuilder.AddUniqueConstraint(
                name: "ak_agent_configuration_acknowledgements_boundary_source",
                table: "agent_configuration_acknowledgements",
                columns: new[] { "agent_id", "acknowledgement_id", "configuration_version", "status", "received_at" });

            migrationBuilder.CreateTable(
                name: "agent_configuration_effective_boundaries",
                columns: table => new
                {
                    agent_id = table.Column<Guid>(type: "uuid", nullable: false),
                    configuration_version = table.Column<long>(type: "bigint", nullable: false),
                    source_acknowledgement_id = table.Column<Guid>(type: "uuid", nullable: false),
                    source_acknowledgement_status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    applied_acknowledgement_received_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_agent_configuration_effective_boundaries", x => new { x.agent_id, x.configuration_version });
                    table.CheckConstraint("ck_agent_configuration_effective_boundaries_applied", "source_acknowledgement_status = 'Applied'");
                    table.CheckConstraint("ck_agent_configuration_effective_boundaries_version", "configuration_version >= 1");
                    table.ForeignKey(
                        name: "FK_agent_configuration_effective_boundaries_agent_configuratio~",
                        columns: x => new { x.agent_id, x.source_acknowledgement_id, x.configuration_version, x.source_acknowledgement_status, x.applied_acknowledgement_received_at },
                        principalTable: "agent_configuration_acknowledgements",
                        principalColumns: new[] { "agent_id", "acknowledgement_id", "configuration_version", "status", "received_at" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "probe_status_policy_snapshots",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    policy_version = table.Column<int>(type: "integer", nullable: false),
                    failure_threshold = table.Column<int>(type: "integer", nullable: false),
                    recovery_threshold = table.Column<int>(type: "integer", nullable: false),
                    warning_rtt_milliseconds = table.Column<int>(type: "integer", nullable: true),
                    warning_packet_loss_ratio = table.Column<decimal>(type: "numeric(18,9)", precision: 18, scale: 9, nullable: true),
                    approved_lateness_seconds = table.Column<int>(type: "integer", nullable: false),
                    approved_future_skew_seconds = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_probe_status_policy_snapshots", x => x.id);
                    table.UniqueConstraint("ak_probe_status_policy_snapshots_id_version", x => new { x.id, x.policy_version });
                    table.CheckConstraint("ck_probe_status_policy_snapshots_lateness", "approved_lateness_seconds = 300 AND approved_future_skew_seconds = 60");
                    table.CheckConstraint("ck_probe_status_policy_snapshots_packet_loss", "warning_packet_loss_ratio IS NULL OR (warning_packet_loss_ratio > 0 AND warning_packet_loss_ratio <= 1)");
                    table.CheckConstraint("ck_probe_status_policy_snapshots_thresholds", "failure_threshold BETWEEN 1 AND 100 AND recovery_threshold BETWEEN 1 AND 100");
                    table.CheckConstraint("ck_probe_status_policy_snapshots_version", "policy_version >= 1");
                    table.CheckConstraint("ck_probe_status_policy_snapshots_warning_rtt", "warning_rtt_milliseconds IS NULL OR warning_rtt_milliseconds > 0");
                });

            migrationBuilder.CreateTable(
                name: "probe_status_projections",
                columns: table => new
                {
                    probe_id = table.Column<Guid>(type: "uuid", nullable: false),
                    underlying_status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    consecutive_failure_count = table.Column<int>(type: "integer", nullable: false),
                    consecutive_success_count = table.Column<int>(type: "integer", nullable: false),
                    last_fresh_event_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    watermark_event_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    watermark_agent_id = table.Column<Guid>(type: "uuid", nullable: true),
                    watermark_result_id = table.Column<Guid>(type: "uuid", nullable: true),
                    state_version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_probe_status_projections", x => x.probe_id);
                    table.CheckConstraint("ck_probe_status_projections_counters", "consecutive_failure_count >= 0 AND consecutive_success_count >= 0 AND NOT (consecutive_failure_count > 0 AND consecutive_success_count > 0)");
                    table.CheckConstraint("ck_probe_status_projections_down", "underlying_status <> 'Down' OR (consecutive_failure_count >= 1 AND consecutive_success_count = 0)");
                    table.CheckConstraint("ck_probe_status_projections_recovering", "underlying_status <> 'Recovering' OR (consecutive_failure_count = 0 AND consecutive_success_count >= 1)");
                    table.CheckConstraint("ck_probe_status_projections_state_version", "state_version >= 0");
                    table.CheckConstraint("ck_probe_status_projections_status", "underlying_status IN ('Unknown', 'Up', 'Degraded', 'Down', 'Recovering')");
                    table.CheckConstraint("ck_probe_status_projections_watermark", "(watermark_event_at IS NULL AND watermark_agent_id IS NULL AND watermark_result_id IS NULL) OR (watermark_event_at IS NOT NULL AND watermark_agent_id IS NOT NULL AND watermark_result_id IS NOT NULL)");
                    table.ForeignKey(
                        name: "FK_probe_status_projections_probes_probe_id",
                        column: x => x.probe_id,
                        principalTable: "probes",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "probe_result_processing_dispositions",
                columns: table => new
                {
                    agent_id = table.Column<Guid>(type: "uuid", nullable: false),
                    result_id = table.Column<Guid>(type: "uuid", nullable: false),
                    probe_id = table.Column<Guid>(type: "uuid", nullable: false),
                    event_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    disposition = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    reason_code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    resolved_policy_snapshot_id = table.Column<Guid>(type: "uuid", nullable: true),
                    resolved_policy_version = table.Column<int>(type: "integer", nullable: true),
                    decided_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_probe_result_processing_dispositions", x => new { x.agent_id, x.result_id });
                    table.CheckConstraint("ck_probe_result_processing_dispositions_kind", "disposition IN ('StateDriving', 'LateOrder', 'FutureOrSkewSuspect', 'BeyondApprovedLateness', 'Disabled', 'HistoricalOther')");
                    table.CheckConstraint("ck_probe_result_processing_dispositions_lineage", "(resolved_policy_snapshot_id IS NULL AND resolved_policy_version IS NULL) OR (resolved_policy_snapshot_id IS NOT NULL AND resolved_policy_version IS NOT NULL)");
                    table.CheckConstraint("ck_probe_result_processing_dispositions_reason_code", "char_length(reason_code) BETWEEN 1 AND 64");
                    table.CheckConstraint("ck_probe_result_processing_dispositions_state_driving", "disposition <> 'StateDriving' OR (resolved_policy_snapshot_id IS NOT NULL AND resolved_policy_version IS NOT NULL)");
                    table.ForeignKey(
                        name: "FK_probe_result_processing_dispositions_probe_result_ledger_ag~",
                        columns: x => new { x.agent_id, x.result_id, x.probe_id, x.event_at },
                        principalTable: "probe_result_ledger",
                        principalColumns: new[] { "agent_id", "result_id", "probe_id", "ended_at" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_probe_result_processing_dispositions_probe_status_policy_sn~",
                        columns: x => new { x.resolved_policy_snapshot_id, x.resolved_policy_version },
                        principalTable: "probe_status_policy_snapshots",
                        principalColumns: new[] { "id", "policy_version" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "probe_status_policy_bindings",
                columns: table => new
                {
                    probe_id = table.Column<Guid>(type: "uuid", nullable: false),
                    configuration_version = table.Column<long>(type: "bigint", nullable: false),
                    agent_group_id = table.Column<Guid>(type: "uuid", nullable: false),
                    policy_snapshot_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_probe_status_policy_bindings", x => new { x.probe_id, x.configuration_version });
                    table.CheckConstraint("ck_probe_status_policy_bindings_version", "configuration_version >= 1");
                    table.ForeignKey(
                        name: "FK_probe_status_policy_bindings_agent_configuration_snapshots_~",
                        columns: x => new { x.agent_group_id, x.configuration_version },
                        principalTable: "agent_configuration_snapshots",
                        principalColumns: new[] { "agent_group_id", "version" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_probe_status_policy_bindings_probe_status_policy_snapshots_~",
                        column: x => x.policy_snapshot_id,
                        principalTable: "probe_status_policy_snapshots",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_probe_status_policy_bindings_probes_probe_id",
                        column: x => x.probe_id,
                        principalTable: "probes",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.Sql("""
                CREATE FUNCTION wp06_status_processing_reject_append_only_mutation()
                RETURNS trigger
                LANGUAGE plpgsql
                AS $$
                BEGIN
                    RAISE EXCEPTION 'WP-06 append-only table % cannot be modified or deleted', TG_TABLE_NAME;
                END;
                $$;

                CREATE TRIGGER tr_probe_status_policy_snapshots_append_only
                BEFORE UPDATE OR DELETE ON probe_status_policy_snapshots
                FOR EACH ROW EXECUTE FUNCTION wp06_status_processing_reject_append_only_mutation();

                CREATE TRIGGER tr_probe_status_policy_bindings_append_only
                BEFORE UPDATE OR DELETE ON probe_status_policy_bindings
                FOR EACH ROW EXECUTE FUNCTION wp06_status_processing_reject_append_only_mutation();

                CREATE TRIGGER tr_agent_configuration_effective_boundaries_append_only
                BEFORE UPDATE OR DELETE ON agent_configuration_effective_boundaries
                FOR EACH ROW EXECUTE FUNCTION wp06_status_processing_reject_append_only_mutation();

                CREATE TRIGGER tr_probe_result_processing_dispositions_append_only
                BEFORE UPDATE OR DELETE ON probe_result_processing_dispositions
                FOR EACH ROW EXECUTE FUNCTION wp06_status_processing_reject_append_only_mutation();
                """);

            migrationBuilder.CreateIndex(
                name: "ix_probe_result_ledger_state_order",
                table: "probe_result_ledger",
                columns: new[] { "probe_id", "ended_at", "agent_id", "result_id" });

            migrationBuilder.CreateIndex(
                name: "IX_agent_configuration_effective_boundaries_agent_id_source_ac~",
                table: "agent_configuration_effective_boundaries",
                columns: new[] { "agent_id", "source_acknowledgement_id", "configuration_version", "source_acknowledgement_status", "applied_acknowledgement_received_at" });

            migrationBuilder.CreateIndex(
                name: "IX_probe_result_processing_dispositions_agent_id_result_id_pro~",
                table: "probe_result_processing_dispositions",
                columns: new[] { "agent_id", "result_id", "probe_id", "event_at" });

            migrationBuilder.CreateIndex(
                name: "ix_probe_result_processing_dispositions_probe_decided",
                table: "probe_result_processing_dispositions",
                columns: new[] { "probe_id", "decided_at" });

            migrationBuilder.CreateIndex(
                name: "IX_probe_result_processing_dispositions_resolved_policy_snapsh~",
                table: "probe_result_processing_dispositions",
                columns: new[] { "resolved_policy_snapshot_id", "resolved_policy_version" });

            migrationBuilder.CreateIndex(
                name: "IX_probe_status_policy_bindings_agent_group_id_configuration_v~",
                table: "probe_status_policy_bindings",
                columns: new[] { "agent_group_id", "configuration_version" });

            migrationBuilder.CreateIndex(
                name: "ix_probe_status_policy_bindings_snapshot",
                table: "probe_status_policy_bindings",
                column: "policy_snapshot_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP TRIGGER IF EXISTS tr_probe_result_processing_dispositions_append_only ON probe_result_processing_dispositions;
                DROP TRIGGER IF EXISTS tr_agent_configuration_effective_boundaries_append_only ON agent_configuration_effective_boundaries;
                DROP TRIGGER IF EXISTS tr_probe_status_policy_bindings_append_only ON probe_status_policy_bindings;
                DROP TRIGGER IF EXISTS tr_probe_status_policy_snapshots_append_only ON probe_status_policy_snapshots;
                DROP FUNCTION IF EXISTS wp06_status_processing_reject_append_only_mutation();
                """);

            migrationBuilder.DropTable(
                name: "agent_configuration_effective_boundaries");

            migrationBuilder.DropTable(
                name: "probe_result_processing_dispositions");

            migrationBuilder.DropTable(
                name: "probe_status_policy_bindings");

            migrationBuilder.DropTable(
                name: "probe_status_projections");

            migrationBuilder.DropTable(
                name: "probe_status_policy_snapshots");

            migrationBuilder.DropUniqueConstraint(
                name: "ak_probe_result_ledger_processing_identity",
                table: "probe_result_ledger");

            migrationBuilder.DropIndex(
                name: "ix_probe_result_ledger_state_order",
                table: "probe_result_ledger");

            migrationBuilder.DropUniqueConstraint(
                name: "ak_agent_configuration_acknowledgements_boundary_source",
                table: "agent_configuration_acknowledgements");
        }
    }
}

#pragma warning restore CA1861
