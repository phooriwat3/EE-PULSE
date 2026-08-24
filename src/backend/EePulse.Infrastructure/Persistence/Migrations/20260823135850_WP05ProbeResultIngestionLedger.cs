using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EePulse.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class WP05ProbeResultIngestionLedger : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "probe_result_ledger",
                columns: table => new
                {
                    agent_id = table.Column<Guid>(type: "uuid", nullable: false),
                    result_id = table.Column<Guid>(type: "uuid", nullable: false),
                    probe_id = table.Column<Guid>(type: "uuid", nullable: false),
                    configuration_version = table.Column<long>(type: "bigint", nullable: false),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ended_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    attempt_count = table.Column<int>(type: "integer", nullable: false),
                    successful_attempt_count = table.Column<int>(type: "integer", nullable: false),
                    packet_loss_ratio = table.Column<decimal>(type: "numeric(18,9)", precision: 18, scale: 9, nullable: false),
                    min_rtt_milliseconds = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: true),
                    average_rtt_milliseconds = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: true),
                    max_rtt_milliseconds = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: true),
                    error_category = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    immutable_payload_digest = table.Column<byte[]>(type: "bytea", nullable: false),
                    received_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_probe_result_ledger", x => new { x.agent_id, x.result_id });
                    table.CheckConstraint("ck_probe_result_ledger_payload_digest", "octet_length(immutable_payload_digest) = 32");
                    table.ForeignKey(
                        name: "FK_probe_result_ledger_agents_agent_id",
                        column: x => x.agent_id,
                        principalTable: "agents",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_probe_result_ledger_probes_probe_id",
                        column: x => x.probe_id,
                        principalTable: "probes",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_probe_result_ledger_probe_id",
                table: "probe_result_ledger",
                column: "probe_id");

            migrationBuilder.CreateIndex(
                name: "ix_probe_result_ledger_received",
                table: "probe_result_ledger",
                column: "received_at");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "probe_result_ledger");
        }
    }
}
