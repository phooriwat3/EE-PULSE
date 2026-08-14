using System;
using System.Net;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable
#pragma warning disable CA1861

namespace EePulse.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class WP03AgentEnrollmentConfiguration : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "configuration_version",
                table: "agent_groups",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.CreateTable(
                name: "agent_configuration_snapshots",
                columns: table => new
                {
                    agent_group_id = table.Column<Guid>(type: "uuid", nullable: false),
                    version = table.Column<long>(type: "bigint", nullable: false),
                    payload = table.Column<string>(type: "jsonb", nullable: false),
                    payload_digest = table.Column<byte[]>(type: "bytea", nullable: false),
                    generated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    rollback_of_version = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_agent_configuration_snapshots", x => new { x.agent_group_id, x.version });
                    table.CheckConstraint("ck_agent_snapshot_digest", "octet_length(payload_digest) = 32");
                    table.ForeignKey(
                        name: "FK_agent_configuration_snapshots_agent_groups_agent_group_id",
                        column: x => x.agent_group_id,
                        principalTable: "agent_groups",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "agent_enrollment_tokens",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    agent_group_id = table.Column<Guid>(type: "uuid", nullable: false),
                    digest = table.Column<byte[]>(type: "bytea", nullable: false),
                    label = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    expected_machine_name = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    used_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    used_by_agent_id = table.Column<Guid>(type: "uuid", nullable: true),
                    revoked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_by = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    row_version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_agent_enrollment_tokens", x => x.id);
                    table.CheckConstraint("ck_agent_enrollment_token_digest", "octet_length(digest) = 32");
                    table.ForeignKey(
                        name: "FK_agent_enrollment_tokens_agent_groups_agent_group_id",
                        column: x => x.agent_group_id,
                        principalTable: "agent_groups",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "agent_group_allowed_networks",
                columns: table => new
                {
                    agent_group_id = table.Column<Guid>(type: "uuid", nullable: false),
                    network = table.Column<IPNetwork>(type: "cidr", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_agent_group_allowed_networks", x => new { x.agent_group_id, x.network });
                    table.ForeignKey(
                        name: "FK_agent_group_allowed_networks_agent_groups_agent_group_id",
                        column: x => x.agent_group_id,
                        principalTable: "agent_groups",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "agents",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    agent_group_id = table.Column<Guid>(type: "uuid", nullable: false),
                    client_instance_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    machine_name = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    agent_version = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    self_health = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    queue_depth = table.Column<long>(type: "bigint", nullable: false),
                    last_heartbeat_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_reported_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    heartbeat_interval_seconds = table.Column<int>(type: "integer", nullable: false),
                    desired_configuration_version = table.Column<long>(type: "bigint", nullable: false),
                    last_applied_configuration_version = table.Column<long>(type: "bigint", nullable: false),
                    last_configuration_acknowledged_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    clock_skew_suspected = table.Column<bool>(type: "boolean", nullable: false),
                    credential_expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    revoked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    revocation_reason = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    row_version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_agents", x => x.id);
                    table.ForeignKey(
                        name: "FK_agents_agent_groups_agent_group_id",
                        column: x => x.agent_group_id,
                        principalTable: "agent_groups",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "agent_enrollment_token_allowed_networks",
                columns: table => new
                {
                    token_id = table.Column<Guid>(type: "uuid", nullable: false),
                    network = table.Column<IPNetwork>(type: "cidr", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_agent_enrollment_token_allowed_networks", x => new { x.token_id, x.network });
                    table.ForeignKey(
                        name: "FK_agent_enrollment_token_allowed_networks_agent_enrollment_to~",
                        column: x => x.token_id,
                        principalTable: "agent_enrollment_tokens",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "agent_allowed_networks",
                columns: table => new
                {
                    agent_id = table.Column<Guid>(type: "uuid", nullable: false),
                    network = table.Column<IPNetwork>(type: "cidr", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_agent_allowed_networks", x => new { x.agent_id, x.network });
                    table.ForeignKey(
                        name: "FK_agent_allowed_networks_agents_agent_id",
                        column: x => x.agent_id,
                        principalTable: "agents",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "agent_configuration_acknowledgements",
                columns: table => new
                {
                    acknowledgement_id = table.Column<Guid>(type: "uuid", nullable: false),
                    agent_id = table.Column<Guid>(type: "uuid", nullable: false),
                    configuration_version = table.Column<long>(type: "bigint", nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    applied_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    sent_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    received_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    error_code = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    central_effective_configuration_version = table.Column<long>(type: "bigint", nullable: false),
                    desired_configuration_version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_agent_configuration_acknowledgements", x => new { x.agent_id, x.acknowledgement_id });
                    table.ForeignKey(
                        name: "FK_agent_configuration_acknowledgements_agents_agent_id",
                        column: x => x.agent_id,
                        principalTable: "agents",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "agent_credentials",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    agent_id = table.Column<Guid>(type: "uuid", nullable: false),
                    digest = table.Column<byte[]>(type: "bytea", nullable: false),
                    state = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    rotate_after = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    pending_expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    first_used_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    revoked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_agent_credentials", x => x.id);
                    table.CheckConstraint("ck_agent_credential_digest", "octet_length(digest) = 32");
                    table.ForeignKey(
                        name: "FK_agent_credentials_agents_agent_id",
                        column: x => x.agent_id,
                        principalTable: "agents",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "agent_heartbeat_receipts",
                columns: table => new
                {
                    agent_id = table.Column<Guid>(type: "uuid", nullable: false),
                    heartbeat_id = table.Column<Guid>(type: "uuid", nullable: false),
                    received_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    response_json = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_agent_heartbeat_receipts", x => new { x.agent_id, x.heartbeat_id });
                    table.ForeignKey(
                        name: "FK_agent_heartbeat_receipts_agents_agent_id",
                        column: x => x.agent_id,
                        principalTable: "agents",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "agent_policy_allowed_networks",
                columns: table => new
                {
                    agent_id = table.Column<Guid>(type: "uuid", nullable: false),
                    network = table.Column<IPNetwork>(type: "cidr", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_agent_policy_allowed_networks", x => new { x.agent_id, x.network });
                    table.ForeignKey(
                        name: "FK_agent_policy_allowed_networks_agents_agent_id",
                        column: x => x.agent_id,
                        principalTable: "agents",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ux_agent_credentials_active",
                table: "agent_credentials",
                column: "agent_id",
                unique: true,
                filter: "state = 'Active'");

            migrationBuilder.CreateIndex(
                name: "ux_agent_credentials_pending",
                table: "agent_credentials",
                column: "agent_id",
                unique: true,
                filter: "state = 'Pending'");

            migrationBuilder.CreateIndex(
                name: "IX_agent_enrollment_tokens_agent_group_id",
                table: "agent_enrollment_tokens",
                column: "agent_group_id");

            migrationBuilder.CreateIndex(
                name: "ix_agent_heartbeat_receipts_received",
                table: "agent_heartbeat_receipts",
                column: "received_at");

            migrationBuilder.CreateIndex(
                name: "ix_agents_group_status",
                table: "agents",
                columns: new[] { "agent_group_id", "status" });

            migrationBuilder.CreateIndex(
                name: "ix_agents_status_heartbeat",
                table: "agents",
                columns: new[] { "status", "last_heartbeat_at" });

            migrationBuilder.CreateIndex(
                name: "ux_agents_active_client_instance",
                table: "agents",
                column: "client_instance_id",
                unique: true,
                filter: "revoked_at IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "agent_allowed_networks");

            migrationBuilder.DropTable(
                name: "agent_configuration_acknowledgements");

            migrationBuilder.DropTable(
                name: "agent_configuration_snapshots");

            migrationBuilder.DropTable(
                name: "agent_credentials");

            migrationBuilder.DropTable(
                name: "agent_enrollment_token_allowed_networks");

            migrationBuilder.DropTable(
                name: "agent_group_allowed_networks");

            migrationBuilder.DropTable(
                name: "agent_heartbeat_receipts");

            migrationBuilder.DropTable(
                name: "agent_policy_allowed_networks");

            migrationBuilder.DropTable(
                name: "agent_enrollment_tokens");

            migrationBuilder.DropTable(
                name: "agents");

            migrationBuilder.DropColumn(
                name: "configuration_version",
                table: "agent_groups");
        }
    }
}
#pragma warning restore CA1861
