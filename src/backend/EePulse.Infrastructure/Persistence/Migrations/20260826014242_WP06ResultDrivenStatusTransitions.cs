using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable
#pragma warning disable CA1861

namespace EePulse.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class WP06ResultDrivenStatusTransitions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddUniqueConstraint(
                name: "ak_probe_result_processing_dispositions_identity_kind",
                table: "probe_result_processing_dispositions",
                columns: new[] { "agent_id", "result_id", "disposition" });

            migrationBuilder.CreateTable(
                name: "probe_result_status_transitions",
                columns: table => new
                {
                    agent_id = table.Column<Guid>(type: "uuid", nullable: false),
                    result_id = table.Column<Guid>(type: "uuid", nullable: false),
                    probe_id = table.Column<Guid>(type: "uuid", nullable: false),
                    from_status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    to_status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    reason_code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    event_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    received_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    processing_disposition = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_probe_result_status_transitions", x => new { x.agent_id, x.result_id });
                    table.CheckConstraint("ck_probe_result_status_transitions_from_status", "from_status IN ('Unknown', 'Up', 'Degraded', 'Down', 'Recovering')");
                    table.CheckConstraint("ck_probe_result_status_transitions_processing_disposition", "processing_disposition = 'StateDriving'");
                    table.CheckConstraint("ck_probe_result_status_transitions_reason_code", "char_length(reason_code) BETWEEN 1 AND 64");
                    table.CheckConstraint("ck_probe_result_status_transitions_reason_code_value", "reason_code IN ('bootstrap-success', 'quality-degraded', 'quality-restored', 'failure-threshold-met', 'recovery-pending', 'recovery-threshold-met', 'recovery-failed')");
                    table.CheckConstraint("ck_probe_result_status_transitions_status_change", "from_status <> to_status");
                    table.CheckConstraint("ck_probe_result_status_transitions_to_status", "to_status IN ('Unknown', 'Up', 'Degraded', 'Down', 'Recovering')");
                    table.ForeignKey(
                        name: "FK_probe_result_status_transitions_probe_result_ledger_agent_i~",
                        columns: x => new { x.agent_id, x.result_id, x.probe_id, x.event_at },
                        principalTable: "probe_result_ledger",
                        principalColumns: new[] { "agent_id", "result_id", "probe_id", "ended_at" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_probe_result_status_transitions_probe_result_processing_dis~",
                        columns: x => new { x.agent_id, x.result_id, x.processing_disposition },
                        principalTable: "probe_result_processing_dispositions",
                        principalColumns: new[] { "agent_id", "result_id", "disposition" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.Sql("""
                CREATE TRIGGER tr_probe_result_status_transitions_append_only
                BEFORE UPDATE OR DELETE ON probe_result_status_transitions
                FOR EACH ROW
                EXECUTE FUNCTION wp06_status_processing_reject_append_only_mutation();
                """);

            migrationBuilder.CreateIndex(
                name: "IX_probe_result_status_transitions_agent_id_result_id_probe_id~",
                table: "probe_result_status_transitions",
                columns: new[] { "agent_id", "result_id", "probe_id", "event_at" });

            migrationBuilder.CreateIndex(
                name: "IX_probe_result_status_transitions_agent_id_result_id_processi~",
                table: "probe_result_status_transitions",
                columns: new[] { "agent_id", "result_id", "processing_disposition" });

            migrationBuilder.CreateIndex(
                name: "ix_probe_result_status_transitions_probe_event",
                table: "probe_result_status_transitions",
                columns: new[] { "probe_id", "event_at", "agent_id", "result_id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS tr_probe_result_status_transitions_append_only ON probe_result_status_transitions;");

            migrationBuilder.DropTable(
                name: "probe_result_status_transitions");

            migrationBuilder.DropUniqueConstraint(
                name: "ak_probe_result_processing_dispositions_identity_kind",
                table: "probe_result_processing_dispositions");
        }
    }
}
#pragma warning restore CA1861
