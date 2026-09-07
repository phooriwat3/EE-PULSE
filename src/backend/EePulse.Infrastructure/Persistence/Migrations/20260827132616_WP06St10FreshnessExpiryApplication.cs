using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1861 // Avoid constant arrays as arguments

namespace EePulse.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class WP06St10FreshnessExpiryApplication : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "visible_status",
                table: "probe_status_projections",
                type: "varchar(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.Sql("UPDATE probe_status_projections SET visible_status = underlying_status;");

            migrationBuilder.AlterColumn<string>(
                name: "visible_status",
                table: "probe_status_projections",
                type: "varchar(20)",
                maxLength: 20,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "varchar(20)",
                oldMaxLength: 20,
                oldNullable: true);

            migrationBuilder.AddUniqueConstraint(
                name: "ak_probe_freshness_expiry_causes_lineage",
                table: "probe_freshness_expiry_causes",
                columns: new[] { "cause_id", "probe_id", "policy_snapshot_id", "policy_version" });

            migrationBuilder.CreateTable(
                name: "probe_freshness_expiry_cause_dispositions",
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
                    table.PrimaryKey("PK_probe_freshness_expiry_cause_dispositions", x => x.cause_id);
                    table.UniqueConstraint("ak_probe_freshness_expiry_cause_dispositions_cause_outcome", x => new { x.cause_id, x.outcome });
                    table.CheckConstraint("ck_probe_freshness_expiry_cause_dispositions_outcome", "outcome IN ('Applied', 'NoOp')");
                    table.CheckConstraint("ck_probe_freshness_expiry_cause_dispositions_shape", "(outcome = 'Applied' AND reason_code = 'result-freshness-expired' AND applied_at = expiry_cutoff_received_at) OR (outcome = 'NoOp' AND reason_code IN ('projection-missing', 'freshness-source-superseded', 'visible-already-unknown') AND applied_at IS NULL)");
                    table.ForeignKey(
                        name: "FK_probe_freshness_expiry_cause_dispositions_probe_freshness_e~",
                        columns: x => new { x.cause_id, x.probe_id, x.policy_snapshot_id, x.policy_version },
                        principalTable: "probe_freshness_expiry_causes",
                        principalColumns: new[] { "cause_id", "probe_id", "policy_snapshot_id", "policy_version" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_probe_freshness_expiry_cause_dispositions_probe_status_poli~",
                        columns: x => new { x.policy_snapshot_id, x.policy_version },
                        principalTable: "probe_status_policy_snapshots",
                        principalColumns: new[] { "id", "policy_version" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_probe_freshness_expiry_cause_dispositions_probes_probe_id",
                        column: x => x.probe_id,
                        principalTable: "probes",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "probe_freshness_expiry_cause_transitions",
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
                    table.PrimaryKey("PK_probe_freshness_expiry_cause_transitions", x => x.cause_id);
                    table.CheckConstraint("ck_probe_freshness_expiry_cause_transitions_disposition_outcome", "disposition_outcome = 'Applied'");
                    table.CheckConstraint("ck_probe_freshness_expiry_cause_transitions_from_visible_status", "from_visible_status IN ('Up', 'Degraded', 'Down', 'Recovering')");
                    table.CheckConstraint("ck_probe_freshness_expiry_cause_transitions_reason_code", "reason_code = 'result-freshness-expired'");
                    table.CheckConstraint("ck_probe_freshness_expiry_cause_transitions_to_visible_status", "to_visible_status = 'Unknown'");
                    table.ForeignKey(
                        name: "FK_probe_freshness_expiry_cause_transitions_probe_freshness_ex~",
                        columns: x => new { x.cause_id, x.disposition_outcome },
                        principalTable: "probe_freshness_expiry_cause_dispositions",
                        principalColumns: new[] { "cause_id", "outcome" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_probe_freshness_expiry_cause_transitions_probe_freshness_e~1",
                        columns: x => new { x.cause_id, x.probe_id, x.policy_snapshot_id, x.policy_version },
                        principalTable: "probe_freshness_expiry_causes",
                        principalColumns: new[] { "cause_id", "probe_id", "policy_snapshot_id", "policy_version" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_probe_freshness_expiry_cause_transitions_probe_status_polic~",
                        columns: x => new { x.policy_snapshot_id, x.policy_version },
                        principalTable: "probe_status_policy_snapshots",
                        principalColumns: new[] { "id", "policy_version" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_probe_freshness_expiry_cause_transitions_probes_probe_id",
                        column: x => x.probe_id,
                        principalTable: "probes",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.AddCheckConstraint(
                name: "ck_probe_status_projections_visible_status",
                table: "probe_status_projections",
                sql: "visible_status IN ('Unknown', 'Up', 'Degraded', 'Down', 'Recovering')");

            migrationBuilder.Sql("""
                CREATE FUNCTION wp06_probe_freshness_expiry_cause_transition_validate_disposition_timestamp()
                RETURNS trigger
                LANGUAGE plpgsql
                AS $$
                DECLARE
                    disposition_expiry_cutoff_received_at timestamp with time zone;
                BEGIN
                    SELECT expiry_cutoff_received_at
                    INTO disposition_expiry_cutoff_received_at
                    FROM probe_freshness_expiry_cause_dispositions
                    WHERE cause_id = NEW.cause_id
                      AND outcome = NEW.disposition_outcome;

                    IF NOT FOUND OR disposition_expiry_cutoff_received_at <> NEW.applied_at THEN
                        RAISE EXCEPTION 'WP-06 freshness expiry transition disposition timestamp mismatch' USING ERRCODE = 'P0001';
                    END IF;

                    RETURN NEW;
                END;
                $$;

                CREATE TRIGGER tr_probe_freshness_expiry_cause_transitions_validate_disposition_timestamp
                BEFORE INSERT ON probe_freshness_expiry_cause_transitions
                FOR EACH ROW EXECUTE FUNCTION wp06_probe_freshness_expiry_cause_transition_validate_disposition_timestamp();

                CREATE TRIGGER tr_probe_freshness_expiry_cause_dispositions_append_only
                BEFORE UPDATE OR DELETE ON probe_freshness_expiry_cause_dispositions
                FOR EACH ROW EXECUTE FUNCTION wp06_status_processing_reject_append_only_mutation();

                CREATE TRIGGER tr_probe_freshness_expiry_cause_transitions_append_only
                BEFORE UPDATE OR DELETE ON probe_freshness_expiry_cause_transitions
                FOR EACH ROW EXECUTE FUNCTION wp06_status_processing_reject_append_only_mutation();
                """);

            migrationBuilder.CreateIndex(
                name: "IX_probe_freshness_expiry_cause_dispositions_cause_id_probe_id~",
                table: "probe_freshness_expiry_cause_dispositions",
                columns: new[] { "cause_id", "probe_id", "policy_snapshot_id", "policy_version" });

            migrationBuilder.CreateIndex(
                name: "IX_probe_freshness_expiry_cause_dispositions_policy_snapshot_i~",
                table: "probe_freshness_expiry_cause_dispositions",
                columns: new[] { "policy_snapshot_id", "policy_version" });

            migrationBuilder.CreateIndex(
                name: "IX_probe_freshness_expiry_cause_dispositions_probe_id",
                table: "probe_freshness_expiry_cause_dispositions",
                column: "probe_id");

            migrationBuilder.CreateIndex(
                name: "IX_probe_freshness_expiry_cause_transitions_cause_id_dispositi~",
                table: "probe_freshness_expiry_cause_transitions",
                columns: new[] { "cause_id", "disposition_outcome" });

            migrationBuilder.CreateIndex(
                name: "IX_probe_freshness_expiry_cause_transitions_cause_id_probe_id_~",
                table: "probe_freshness_expiry_cause_transitions",
                columns: new[] { "cause_id", "probe_id", "policy_snapshot_id", "policy_version" });

            migrationBuilder.CreateIndex(
                name: "IX_probe_freshness_expiry_cause_transitions_policy_snapshot_id~",
                table: "probe_freshness_expiry_cause_transitions",
                columns: new[] { "policy_snapshot_id", "policy_version" });

            migrationBuilder.CreateIndex(
                name: "IX_probe_freshness_expiry_cause_transitions_probe_id",
                table: "probe_freshness_expiry_cause_transitions",
                column: "probe_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP TRIGGER IF EXISTS tr_probe_freshness_expiry_cause_transitions_append_only ON probe_freshness_expiry_cause_transitions;
                DROP TRIGGER IF EXISTS tr_probe_freshness_expiry_cause_dispositions_append_only ON probe_freshness_expiry_cause_dispositions;
                DROP TRIGGER IF EXISTS tr_probe_freshness_expiry_cause_transitions_validate_disposition_timestamp ON probe_freshness_expiry_cause_transitions;
                DROP FUNCTION IF EXISTS wp06_probe_freshness_expiry_cause_transition_validate_disposition_timestamp();
                """);

            migrationBuilder.DropTable(
                name: "probe_freshness_expiry_cause_transitions");

            migrationBuilder.DropTable(
                name: "probe_freshness_expiry_cause_dispositions");

            migrationBuilder.DropCheckConstraint(
                name: "ck_probe_status_projections_visible_status",
                table: "probe_status_projections");

            migrationBuilder.DropUniqueConstraint(
                name: "ak_probe_freshness_expiry_causes_lineage",
                table: "probe_freshness_expiry_causes");

            migrationBuilder.DropColumn(
                name: "visible_status",
                table: "probe_status_projections");
        }
    }
}

#pragma warning restore CA1861 // Avoid constant arrays as arguments
