using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EePulse.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class WP07Phase2B2IncidentFoundation : Migration
    {
        private static readonly string[] HumanPrincipalIssuerSubjectColumns = ["issuer", "subject"];

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                DO $wp07_incident_actor_guard$
                BEGIN
                    IF EXISTS (
                        SELECT 1
                        FROM availability_incidents
                        WHERE acknowledged_by IS NOT NULL
                           OR (resolved_by IS NOT NULL AND resolved_by <> 'system-policy')
                           OR (resolved_by = 'system-policy' AND
                               (status <> 'Resolved' OR resolved_at IS NULL OR resolution_note IS DISTINCT FROM 'confirmed-recovery'))
                    ) THEN
                        RAISE EXCEPTION 'WP07 incident identity migration found unsupported legacy actor data.'
                            USING ERRCODE = 'check_violation';
                    END IF;
                END
                $wp07_incident_actor_guard$;
                """);

            migrationBuilder.DropCheckConstraint(
                name: "ck_availability_incidents_lifecycle",
                table: "availability_incidents");

            migrationBuilder.DropCheckConstraint(
                name: "ck_availability_incidents_resolution",
                table: "availability_incidents");

            migrationBuilder.DropCheckConstraint(
                name: "ck_availability_incidents_status_lifecycle",
                table: "availability_incidents");

            migrationBuilder.Sql(
                "UPDATE availability_incidents SET resolved_by = NULL WHERE resolved_by = 'system-policy';");

            migrationBuilder.Sql(
                "ALTER TABLE availability_incidents ALTER COLUMN acknowledged_by TYPE uuid USING acknowledged_by::uuid; " +
                "ALTER TABLE availability_incidents ALTER COLUMN resolved_by TYPE uuid USING resolved_by::uuid;");

            migrationBuilder.AlterColumn<string>(
                name: "resolution_note",
                table: "availability_incidents",
                type: "character varying(2000)",
                maxLength: 2000,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(1000)",
                oldMaxLength: 1000,
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "acknowledgement_comment",
                table: "availability_incidents",
                type: "character varying(2000)",
                maxLength: 2000,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(1000)",
                oldMaxLength: 1000,
                oldNullable: true);

            migrationBuilder.AddColumn<long>(
                name: "row_version",
                table: "availability_incidents",
                type: "bigint",
                nullable: false,
                defaultValue: 1L);

            migrationBuilder.CreateTable(
                name: "human_principals",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    issuer = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false, collation: "C"),
                    subject = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false, collation: "C"),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_human_principals", x => x.id);
                    table.CheckConstraint("ck_human_principals_id_nonempty", "id <> '00000000-0000-0000-0000-000000000000'::uuid");
                    table.CheckConstraint("ck_human_principals_issuer_shape", "char_length(issuer) BETWEEN 1 AND 512 AND issuer = btrim(issuer, ' ' || chr(9) || chr(10) || chr(11) || chr(12) || chr(13) || chr(160) || chr(5760) || chr(8192) || chr(8193) || chr(8194) || chr(8195) || chr(8196) || chr(8197) || chr(8198) || chr(8199) || chr(8200) || chr(8201) || chr(8202) || chr(8232) || chr(8233) || chr(8239) || chr(8287) || chr(12288)) AND position(chr(8232) in issuer) = 0 AND position(chr(8233) in issuer) = 0 AND issuer !~ '[[:cntrl:]]' AND translate(issuer, chr(128) || chr(129) || chr(130) || chr(131) || chr(132) || chr(133) || chr(134) || chr(135) || chr(136) || chr(137) || chr(138) || chr(139) || chr(140) || chr(141) || chr(142) || chr(143) || chr(144) || chr(145) || chr(146) || chr(147) || chr(148) || chr(149) || chr(150) || chr(151) || chr(152) || chr(153) || chr(154) || chr(155) || chr(156) || chr(157) || chr(158) || chr(159), '') = issuer");
                    table.CheckConstraint("ck_human_principals_subject_shape", "char_length(subject) BETWEEN 1 AND 512 AND subject = btrim(subject, ' ' || chr(9) || chr(10) || chr(11) || chr(12) || chr(13) || chr(160) || chr(5760) || chr(8192) || chr(8193) || chr(8194) || chr(8195) || chr(8196) || chr(8197) || chr(8198) || chr(8199) || chr(8200) || chr(8201) || chr(8202) || chr(8232) || chr(8233) || chr(8239) || chr(8287) || chr(12288)) AND position(chr(8232) in subject) = 0 AND position(chr(8233) in subject) = 0 AND subject !~ '[[:cntrl:]]' AND translate(subject, chr(128) || chr(129) || chr(130) || chr(131) || chr(132) || chr(133) || chr(134) || chr(135) || chr(136) || chr(137) || chr(138) || chr(139) || chr(140) || chr(141) || chr(142) || chr(143) || chr(144) || chr(145) || chr(146) || chr(147) || chr(148) || chr(149) || chr(150) || chr(151) || chr(152) || chr(153) || chr(154) || chr(155) || chr(156) || chr(157) || chr(158) || chr(159), '') = subject");
                });

            migrationBuilder.CreateIndex(
                name: "IX_availability_incidents_acknowledged_by",
                table: "availability_incidents",
                column: "acknowledged_by");

            migrationBuilder.CreateIndex(
                name: "IX_availability_incidents_resolved_by",
                table: "availability_incidents",
                column: "resolved_by");

            migrationBuilder.AddCheckConstraint(
                name: "ck_availability_incidents_lifecycle",
                table: "availability_incidents",
                sql: "(acknowledged_at IS NULL AND acknowledged_by IS NULL AND acknowledgement_comment IS NULL) OR (acknowledged_at IS NOT NULL AND acknowledged_by IS NOT NULL AND char_length(COALESCE(acknowledgement_comment, '')) BETWEEN 1 AND 2000)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_availability_incidents_resolution",
                table: "availability_incidents",
                sql: "(resolved_at IS NULL AND resolved_by IS NULL AND resolution_note IS NULL) OR (resolved_at IS NOT NULL AND char_length(COALESCE(resolution_note, '')) BETWEEN 1 AND 2000 AND (resolved_by IS NOT NULL OR resolution_note = 'confirmed-recovery'))");

            migrationBuilder.AddCheckConstraint(
                name: "ck_availability_incidents_row_version",
                table: "availability_incidents",
                sql: "row_version > 0");

            migrationBuilder.AddCheckConstraint(
                name: "ck_availability_incidents_status_lifecycle",
                table: "availability_incidents",
                sql: "(status = 'Open' AND acknowledged_at IS NULL AND acknowledged_by IS NULL AND acknowledgement_comment IS NULL AND resolved_at IS NULL AND resolved_by IS NULL AND resolution_note IS NULL) OR (status = 'Acknowledged' AND acknowledged_at IS NOT NULL AND acknowledged_by IS NOT NULL AND char_length(COALESCE(acknowledgement_comment, '')) BETWEEN 1 AND 2000 AND resolved_at IS NULL AND resolved_by IS NULL AND resolution_note IS NULL) OR (status = 'Resolved' AND resolved_at IS NOT NULL AND char_length(COALESCE(resolution_note, '')) BETWEEN 1 AND 2000 AND (resolved_by IS NOT NULL OR resolution_note = 'confirmed-recovery') AND ((acknowledged_at IS NULL AND acknowledged_by IS NULL AND acknowledgement_comment IS NULL) OR (acknowledged_at IS NOT NULL AND acknowledged_by IS NOT NULL AND char_length(COALESCE(acknowledgement_comment, '')) BETWEEN 1 AND 2000)))");

            migrationBuilder.CreateIndex(
                name: "ux_human_principals_issuer_subject",
                table: "human_principals",
                columns: HumanPrincipalIssuerSubjectColumns,
                unique: true);

            migrationBuilder.Sql(
                """
                CREATE FUNCTION public.fn_wp07_human_principals_reject_mutation()
                RETURNS trigger
                LANGUAGE plpgsql
                AS $wp07_human_principals_immutable$
                BEGIN
                    RAISE EXCEPTION USING
                        ERRCODE = '23514',
                        MESSAGE = 'WP07 human principals are immutable.',
                        CONSTRAINT = 'tr_human_principals_immutable';
                    RETURN OLD;
                END;
                $wp07_human_principals_immutable$;

                CREATE TRIGGER tr_human_principals_immutable
                BEFORE UPDATE OR DELETE ON public.human_principals
                FOR EACH ROW
                EXECUTE FUNCTION public.fn_wp07_human_principals_reject_mutation();
                """);

            migrationBuilder.AddForeignKey(
                name: "fk_availability_incidents_human_principals_acknowledged_by",
                table: "availability_incidents",
                column: "acknowledged_by",
                principalTable: "human_principals",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_availability_incidents_human_principals_resolved_by",
                table: "availability_incidents",
                column: "resolved_by",
                principalTable: "human_principals",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                LOCK TABLE availability_incidents IN ACCESS EXCLUSIVE MODE NOWAIT;
                LOCK TABLE human_principals IN ACCESS EXCLUSIVE MODE NOWAIT;

                DO $wp07_incident_downgrade_guard$
                BEGIN
                    IF EXISTS (
                        SELECT 1
                        FROM availability_incidents
                        WHERE (acknowledgement_comment IS NOT NULL
                               AND char_length(acknowledgement_comment) > 1000)
                           OR (resolution_note IS NOT NULL
                               AND char_length(resolution_note) > 1000)
                    ) THEN
                        RAISE EXCEPTION 'WP07 incident text downgrade requires acknowledgement_comment and resolution_note to be at most 1000 characters.'
                            USING ERRCODE = 'check_violation';
                    END IF;

                    IF EXISTS (SELECT 1 FROM human_principals)
                       OR EXISTS (
                           SELECT 1
                           FROM availability_incidents
                           WHERE acknowledged_by IS NOT NULL OR resolved_by IS NOT NULL
                       ) THEN
                        RAISE EXCEPTION 'WP07 incident identity downgrade requires empty principal and actor data.'
                            USING ERRCODE = 'check_violation';
                    END IF;

                    IF EXISTS (
                        SELECT 1
                        FROM availability_incidents
                        WHERE row_version <> 1
                    ) THEN
                        RAISE EXCEPTION 'WP07 incident row-version downgrade requires every incident row_version to equal 1.'
                            USING ERRCODE = 'check_violation';
                    END IF;
                END
                $wp07_incident_downgrade_guard$;
                """);

            migrationBuilder.DropForeignKey(
                name: "fk_availability_incidents_human_principals_acknowledged_by",
                table: "availability_incidents");

            migrationBuilder.DropForeignKey(
                name: "fk_availability_incidents_human_principals_resolved_by",
                table: "availability_incidents");

            migrationBuilder.DropIndex(
                name: "IX_availability_incidents_acknowledged_by",
                table: "availability_incidents");

            migrationBuilder.DropIndex(
                name: "IX_availability_incidents_resolved_by",
                table: "availability_incidents");

            migrationBuilder.DropCheckConstraint(
                name: "ck_availability_incidents_lifecycle",
                table: "availability_incidents");

            migrationBuilder.DropCheckConstraint(
                name: "ck_availability_incidents_resolution",
                table: "availability_incidents");

            migrationBuilder.DropCheckConstraint(
                name: "ck_availability_incidents_row_version",
                table: "availability_incidents");

            migrationBuilder.DropCheckConstraint(
                name: "ck_availability_incidents_status_lifecycle",
                table: "availability_incidents");

            migrationBuilder.DropColumn(
                name: "row_version",
                table: "availability_incidents");

            migrationBuilder.Sql(
                "ALTER TABLE availability_incidents ALTER COLUMN acknowledged_by TYPE character varying(128) USING acknowledged_by::text; " +
                "ALTER TABLE availability_incidents ALTER COLUMN resolved_by TYPE character varying(128) USING resolved_by::text; " +
                "UPDATE availability_incidents SET resolved_by = 'system-policy' " +
                "WHERE status = 'Resolved' AND resolved_by IS NULL AND resolution_note = 'confirmed-recovery';");

            migrationBuilder.Sql(
                """
                DROP TRIGGER tr_human_principals_immutable ON public.human_principals;
                DROP FUNCTION public.fn_wp07_human_principals_reject_mutation();
                """);

            migrationBuilder.DropTable(
                name: "human_principals");

            migrationBuilder.AlterColumn<string>(
                name: "resolved_by",
                table: "availability_incidents",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(128)",
                oldMaxLength: 128,
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "resolution_note",
                table: "availability_incidents",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(2000)",
                oldMaxLength: 2000,
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "acknowledgement_comment",
                table: "availability_incidents",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(2000)",
                oldMaxLength: 2000,
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "acknowledged_by",
                table: "availability_incidents",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(128)",
                oldMaxLength: 128,
                oldNullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "ck_availability_incidents_lifecycle",
                table: "availability_incidents",
                sql: "(acknowledged_at IS NULL AND acknowledged_by IS NULL AND acknowledgement_comment IS NULL) OR (acknowledged_at IS NOT NULL AND acknowledged_by IS NOT NULL AND acknowledgement_comment IS NOT NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_availability_incidents_resolution",
                table: "availability_incidents",
                sql: "(resolved_at IS NULL AND resolved_by IS NULL AND resolution_note IS NULL) OR (resolved_at IS NOT NULL AND resolved_by IS NOT NULL AND resolution_note IS NOT NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_availability_incidents_status_lifecycle",
                table: "availability_incidents",
                sql: "(status = 'Open' AND acknowledged_at IS NULL AND resolved_at IS NULL) OR (status = 'Acknowledged' AND acknowledged_at IS NOT NULL AND resolved_at IS NULL) OR (status = 'Resolved' AND resolved_at IS NOT NULL)");
        }
    }
}
