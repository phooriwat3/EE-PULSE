using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EePulse.Infrastructure.Persistence.Migrations;

public partial class WP07Phase2B2IncidentRuntime : Migration
{
    private static readonly string[] AvailabilityIncidentPrincipalKeyColumns = ["id", "probe_id"];
    private static readonly string[] ReceiptToCommentForeignKeyColumns = ["idempotency_key", "incident_comment_id", "incident_id", "actor_id"];
    private static readonly string[] ReceiptToLifecycleActionForeignKeyColumns = ["idempotency_key", "incident_lifecycle_action_id", "incident_id", "actor_id", "outcome_kind"];
    private static readonly string[] ChildIncidentProbeForeignKeyColumns = ["incident_id", "probe_id"];

    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            DO LANGUAGE plpgsql $wp07_require_utf8$
            BEGIN
                IF pg_catalog.current_setting('server_encoding') COLLATE "C"
                   <> 'UTF8' COLLATE "C" THEN
                    RAISE EXCEPTION USING
                        ERRCODE = '55000',
                        MESSAGE = 'WP07 incident runtime requires UTF8 server encoding.',
                        CONSTRAINT = 'ck_wp07_incident_runtime_server_encoding_utf8';
                END IF;
            END;
            $wp07_require_utf8$;
            """);
        migrationBuilder.Sql("""
            CREATE FUNCTION public.wp07_dotnet_trim(value text)
            RETURNS text LANGUAGE plpgsql STRICT IMMUTABLE PARALLEL SAFE
            SET search_path = pg_catalog
            AS $function$
            DECLARE
                first_character integer := 1;
                last_character integer := pg_catalog.char_length(value);
                trim_set text :=
                    pg_catalog.chr(9) || pg_catalog.chr(10) || pg_catalog.chr(11) ||
                    pg_catalog.chr(12) || pg_catalog.chr(13) || pg_catalog.chr(32) ||
                    pg_catalog.chr(133) || pg_catalog.chr(160) || pg_catalog.chr(5760) ||
                    pg_catalog.chr(8192) || pg_catalog.chr(8193) || pg_catalog.chr(8194) ||
                    pg_catalog.chr(8195) || pg_catalog.chr(8196) || pg_catalog.chr(8197) ||
                    pg_catalog.chr(8198) || pg_catalog.chr(8199) || pg_catalog.chr(8200) ||
                    pg_catalog.chr(8201) || pg_catalog.chr(8202) || pg_catalog.chr(8232) ||
                    pg_catalog.chr(8233) || pg_catalog.chr(8239) || pg_catalog.chr(8287) ||
                    pg_catalog.chr(12288);
            BEGIN
                WHILE first_character <= last_character AND pg_catalog.strpos(
                    trim_set COLLATE "C",
                    pg_catalog.substr(value, first_character, 1) COLLATE "C") > 0 LOOP
                    first_character := first_character + 1;
                END LOOP;
                WHILE last_character >= first_character AND pg_catalog.strpos(
                    trim_set COLLATE "C",
                    pg_catalog.substr(value, last_character, 1) COLLATE "C") > 0 LOOP
                    last_character := last_character - 1;
                END LOOP;
                IF first_character > last_character THEN RETURN ''; END IF;
                RETURN pg_catalog.substr(value, first_character, last_character - first_character + 1);
            END;
            $function$;
            """);
        migrationBuilder.Sql("""
            CREATE FUNCTION public.wp07_dotnet_utf16_code_units(value text)
            RETURNS integer LANGUAGE sql STRICT IMMUTABLE PARALLEL SAFE
            SET search_path = pg_catalog
            AS $function$
                SELECT pg_catalog.char_length(value) + (
                    SELECT pg_catalog.count(*)::integer
                    FROM pg_catalog.generate_series(1, pg_catalog.char_length(value)) AS characters(ordinal)
                    WHERE pg_catalog.octet_length(pg_catalog.substr(value, characters.ordinal, 1)) = 4);
            $function$;
            """);
        migrationBuilder.Sql("""
            CREATE FUNCTION public.wp07_incident_text_is_valid(value text)
            RETURNS boolean LANGUAGE sql STRICT IMMUTABLE PARALLEL SAFE
            SET search_path = pg_catalog
            AS $function$
                SELECT value COLLATE "C" = public.wp07_dotnet_trim(value) COLLATE "C"
                   AND public.wp07_dotnet_utf16_code_units(value) BETWEEN 1 AND 2000
                   AND NOT EXISTS (
                       SELECT 1
                       FROM pg_catalog.generate_series(1, pg_catalog.char_length(value)) AS characters(ordinal)
                       WHERE pg_catalog.ascii(pg_catalog.substr(value, characters.ordinal, 1)) BETWEEN 0 AND 31
                          OR pg_catalog.ascii(pg_catalog.substr(value, characters.ordinal, 1)) BETWEEN 127 AND 159
                          OR pg_catalog.ascii(pg_catalog.substr(value, characters.ordinal, 1)) IN (8232, 8233));
            $function$;
            """);

        migrationBuilder.CreateTable(name: "idempotency_receipts", columns: table => new
        {
            idempotency_key = table.Column<string>(type: "character varying(36)", maxLength: 36, nullable: false, collation: "C"),
            route_template = table.Column<string>(type: "text", nullable: false, collation: "C"),
            incident_id = table.Column<Guid>(type: "uuid", nullable: false),
            actor_id = table.Column<Guid>(type: "uuid", nullable: false),
            request_fingerprint_version = table.Column<short>(type: "smallint", nullable: false),
            request_digest = table.Column<byte[]>(type: "bytea", nullable: false),
            response_status = table.Column<int>(type: "integer", nullable: false),
            response_body = table.Column<byte[]>(type: "bytea", nullable: false),
            response_content_type = table.Column<string>(type: "text", nullable: false),
            response_etag = table.Column<string>(type: "text", nullable: false),
            outcome_kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false, collation: "C"),
            incident_comment_id = table.Column<Guid>(type: "uuid", nullable: true),
            incident_lifecycle_action_id = table.Column<Guid>(type: "uuid", nullable: true),
            created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
            completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
        }, constraints: table =>
        {
            table.PrimaryKey("pk_idempotency_receipts", x => x.idempotency_key);
            table.CheckConstraint("ck_idempotency_receipts_request_fingerprint_version", "request_fingerprint_version = 1");
            table.CheckConstraint("ck_idempotency_receipts_request_digest", "pg_catalog.octet_length(request_digest) = 32");
            table.CheckConstraint("ck_idempotency_receipts_response_status_valid", "response_status BETWEEN 100 AND 599");
            table.CheckConstraint("ck_idempotency_receipts_response_status_success", "response_status IN (200, 201)");
            table.CheckConstraint("ck_idempotency_receipts_outcome_status", "(outcome_kind = 'general_comment' AND response_status = 201) OR (outcome_kind IN ('acknowledgement', 'manual_resolution') AND response_status = 200)");
            table.CheckConstraint("ck_idempotency_receipts_content_type_nonempty", "response_content_type COLLATE \"C\" <> ''");
            table.CheckConstraint("ck_idempotency_receipts_response_etag_strong_quoted", "response_etag COLLATE \"C\" ~ '^\"[!#-~]+\"$'");
            table.CheckConstraint("ck_idempotency_receipts_route_nonempty", "route_template COLLATE \"C\" <> ''");
            table.CheckConstraint("ck_idempotency_receipts_outcome_kind", "outcome_kind IN ('acknowledgement', 'general_comment', 'manual_resolution')");
            table.CheckConstraint("ck_idempotency_receipts_outcome_references", "(outcome_kind = 'general_comment' AND incident_comment_id IS NOT NULL AND incident_lifecycle_action_id IS NULL) OR (outcome_kind = 'acknowledgement' AND incident_comment_id IS NULL AND incident_lifecycle_action_id IS NOT NULL) OR (outcome_kind = 'manual_resolution' AND incident_comment_id IS NULL AND incident_lifecycle_action_id IS NOT NULL)");
            table.CheckConstraint("ck_idempotency_receipts_completed_at", "completed_at >= created_at");
        });
        migrationBuilder.CreateTable(name: "incident_comments", columns: table => new
        {
            id = table.Column<Guid>(type: "uuid", nullable: false), incident_id = table.Column<Guid>(type: "uuid", nullable: false),
            probe_id = table.Column<Guid>(type: "uuid", nullable: false), author_id = table.Column<Guid>(type: "uuid", nullable: false),
            comment = table.Column<string>(type: "text", nullable: false), created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
            idempotency_key = table.Column<string>(type: "character varying(36)", maxLength: 36, nullable: false, collation: "C")
        }, constraints: table =>
        {
            table.PrimaryKey("pk_incident_comments", x => x.id);
            table.UniqueConstraint("uq_incident_comments_idempotency_key", x => x.idempotency_key);
            table.UniqueConstraint("uq_incident_comments_receipt_outcome", x => new { x.idempotency_key, x.id, x.incident_id, x.author_id });
            table.CheckConstraint("ck_incident_comments_comment", "public.wp07_incident_text_is_valid(comment)");
            table.ForeignKey("fk_incident_comments_availability_incident", x => new { x.incident_id, x.probe_id }, "availability_incidents", AvailabilityIncidentPrincipalKeyColumns);
            table.ForeignKey("fk_incident_comments_human_principal", x => x.author_id, "human_principals", "id");
        });
        migrationBuilder.CreateTable(name: "incident_lifecycle_actions", columns: table => new
        {
            event_id = table.Column<Guid>(type: "uuid", nullable: false), incident_id = table.Column<Guid>(type: "uuid", nullable: false),
            probe_id = table.Column<Guid>(type: "uuid", nullable: false), actor_id = table.Column<Guid>(type: "uuid", nullable: false),
            action_kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false, collation: "C"),
            reason_code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false, collation: "C"),
            occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
            action_note = table.Column<string>(type: "text", nullable: false),
            idempotency_key = table.Column<string>(type: "character varying(36)", maxLength: 36, nullable: false, collation: "C")
        }, constraints: table =>
        {
            table.PrimaryKey("pk_incident_lifecycle_actions", x => x.event_id);
            table.UniqueConstraint("uq_incident_lifecycle_actions_idempotency_key", x => x.idempotency_key);
            table.UniqueConstraint("uq_incident_lifecycle_actions_receipt_outcome", x => new { x.idempotency_key, x.event_id, x.incident_id, x.actor_id, x.action_kind });
            table.CheckConstraint("ck_incident_lifecycle_actions_kind_reason", "(action_kind = 'acknowledgement' AND reason_code = 'operator-acknowledgement') OR (action_kind = 'manual_resolution' AND reason_code = 'manual-resolution')");
            table.CheckConstraint("ck_incident_lifecycle_actions_action_note", "public.wp07_incident_text_is_valid(action_note)");
            table.ForeignKey("fk_incident_lifecycle_actions_availability_incident", x => new { x.incident_id, x.probe_id }, "availability_incidents", AvailabilityIncidentPrincipalKeyColumns);
            table.ForeignKey("fk_incident_lifecycle_actions_human_principal", x => x.actor_id, "human_principals", "id");
        });
        migrationBuilder.Sql("""
            ALTER TABLE incident_comments ADD CONSTRAINT fk_incident_comments_idempotency_receipt
            FOREIGN KEY (idempotency_key) REFERENCES idempotency_receipts(idempotency_key)
            ON DELETE NO ACTION DEFERRABLE INITIALLY DEFERRED;
            ALTER TABLE incident_lifecycle_actions ADD CONSTRAINT fk_incident_lifecycle_actions_idempotency_receipt
            FOREIGN KEY (idempotency_key) REFERENCES idempotency_receipts(idempotency_key)
            ON DELETE NO ACTION DEFERRABLE INITIALLY DEFERRED;
            ALTER TABLE idempotency_receipts ADD CONSTRAINT fk_idempotency_receipts_incident_comment_outcome
            FOREIGN KEY (idempotency_key, incident_comment_id, incident_id, actor_id)
            REFERENCES incident_comments (idempotency_key, id, incident_id, author_id)
            ON DELETE NO ACTION DEFERRABLE INITIALLY DEFERRED;
            ALTER TABLE idempotency_receipts ADD CONSTRAINT fk_idempotency_receipts_lifecycle_action_outcome
            FOREIGN KEY (idempotency_key, incident_lifecycle_action_id, incident_id, actor_id, outcome_kind)
            REFERENCES incident_lifecycle_actions (idempotency_key, event_id, incident_id, actor_id, action_kind)
            ON DELETE NO ACTION DEFERRABLE INITIALLY DEFERRED;
            """);
        migrationBuilder.AddCheckConstraint("ck_availability_incidents_acknowledgement_comment_text", "availability_incidents", "acknowledgement_comment IS NULL OR public.wp07_incident_text_is_valid(acknowledgement_comment)");
        migrationBuilder.AddCheckConstraint("ck_availability_incidents_resolution_note_text", "availability_incidents", "resolution_note IS NULL OR public.wp07_incident_text_is_valid(resolution_note)");
        migrationBuilder.CreateIndex("IX_idempotency_receipts_idempotency_key_incident_comment_id_in~", "idempotency_receipts", ReceiptToCommentForeignKeyColumns);
        migrationBuilder.CreateIndex("IX_idempotency_receipts_idempotency_key_incident_lifecycle_act~", "idempotency_receipts", ReceiptToLifecycleActionForeignKeyColumns);
        migrationBuilder.CreateIndex("uq_idempotency_receipts_incident_comment", "idempotency_receipts", "incident_comment_id", unique: true);
        migrationBuilder.CreateIndex("uq_idempotency_receipts_lifecycle_action", "idempotency_receipts", "incident_lifecycle_action_id", unique: true);
        migrationBuilder.CreateIndex("IX_incident_comments_author_id", "incident_comments", "author_id");
        migrationBuilder.CreateIndex("IX_incident_comments_incident_id_probe_id", "incident_comments", ChildIncidentProbeForeignKeyColumns);
        migrationBuilder.CreateIndex("IX_incident_lifecycle_actions_actor_id", "incident_lifecycle_actions", "actor_id");
        migrationBuilder.CreateIndex("IX_incident_lifecycle_actions_incident_id_probe_id", "incident_lifecycle_actions", ChildIncidentProbeForeignKeyColumns);
        migrationBuilder.Sql("""
            CREATE FUNCTION public.fn_wp07_incident_runtime_reject_mutation()
            RETURNS trigger LANGUAGE plpgsql SET search_path = pg_catalog
            AS $wp07_incident_runtime_immutable$
            BEGIN
                RAISE EXCEPTION USING ERRCODE = '23514',
                    MESSAGE = 'WP07 incident runtime records are immutable.', CONSTRAINT = TG_NAME;
                RETURN OLD;
            END;
            $wp07_incident_runtime_immutable$;
            CREATE TRIGGER tr_incident_comments_immutable BEFORE UPDATE OR DELETE ON public.incident_comments FOR EACH ROW EXECUTE FUNCTION public.fn_wp07_incident_runtime_reject_mutation();
            CREATE TRIGGER tr_incident_lifecycle_actions_immutable BEFORE UPDATE OR DELETE ON public.incident_lifecycle_actions FOR EACH ROW EXECUTE FUNCTION public.fn_wp07_incident_runtime_reject_mutation();
            CREATE TRIGGER tr_idempotency_receipts_immutable BEFORE UPDATE OR DELETE ON public.idempotency_receipts FOR EACH ROW EXECUTE FUNCTION public.fn_wp07_incident_runtime_reject_mutation();
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            LOCK TABLE availability_incidents IN ACCESS EXCLUSIVE MODE NOWAIT;
            LOCK TABLE human_principals IN ACCESS EXCLUSIVE MODE NOWAIT;
            LOCK TABLE idempotency_receipts IN ACCESS EXCLUSIVE MODE NOWAIT;
            LOCK TABLE incident_comments IN ACCESS EXCLUSIVE MODE NOWAIT;
            LOCK TABLE incident_lifecycle_actions IN ACCESS EXCLUSIVE MODE NOWAIT;
            """);
        migrationBuilder.Sql("""
            DROP TRIGGER tr_incident_comments_immutable ON public.incident_comments;
            DROP TRIGGER tr_incident_lifecycle_actions_immutable ON public.incident_lifecycle_actions;
            DROP TRIGGER tr_idempotency_receipts_immutable ON public.idempotency_receipts;
            DROP FUNCTION public.fn_wp07_incident_runtime_reject_mutation();
            ALTER TABLE idempotency_receipts DROP CONSTRAINT fk_idempotency_receipts_incident_comment_outcome;
            ALTER TABLE idempotency_receipts DROP CONSTRAINT fk_idempotency_receipts_lifecycle_action_outcome;
            ALTER TABLE incident_comments DROP CONSTRAINT fk_incident_comments_idempotency_receipt;
            ALTER TABLE incident_lifecycle_actions DROP CONSTRAINT fk_incident_lifecycle_actions_idempotency_receipt;
            """);
        migrationBuilder.DropTable(name: "incident_comments");
        migrationBuilder.DropTable(name: "incident_lifecycle_actions");
        migrationBuilder.DropTable(name: "idempotency_receipts");
        migrationBuilder.DropCheckConstraint("ck_availability_incidents_acknowledgement_comment_text", "availability_incidents");
        migrationBuilder.DropCheckConstraint("ck_availability_incidents_resolution_note_text", "availability_incidents");
        migrationBuilder.Sql("DROP FUNCTION public.wp07_incident_text_is_valid(text);");
        migrationBuilder.Sql("DROP FUNCTION public.wp07_dotnet_utf16_code_units(text);");
        migrationBuilder.Sql("DROP FUNCTION public.wp07_dotnet_trim(text);");
    }
}
