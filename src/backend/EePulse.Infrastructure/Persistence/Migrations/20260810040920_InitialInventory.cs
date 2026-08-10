using System;
using System.Net;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable
#pragma warning disable CA1861

namespace EePulse.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialInventory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "agent_groups",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    description = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    enabled = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    row_version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_agent_groups", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "audit_events",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    actor_id = table.Column<Guid>(type: "uuid", nullable: true),
                    action = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    entity_type = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    entity_id = table.Column<Guid>(type: "uuid", nullable: true),
                    before_json = table.Column<string>(type: "jsonb", nullable: true),
                    after_json = table.Column<string>(type: "jsonb", nullable: true),
                    correlation_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    source_ip = table.Column<IPAddress>(type: "inet", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_audit_events", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "sites",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    code = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    timezone = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    enabled = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    row_version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_sites", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "devices",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    site_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    address = table.Column<IPAddress>(type: "inet", nullable: false),
                    hostname = table.Column<string>(type: "character varying(253)", maxLength: 253, nullable: true),
                    device_type = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    area = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    owner = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    criticality = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    enabled = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    row_version = table.Column<long>(type: "bigint", nullable: false),
                    tags = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_devices", x => x.id);
                    table.ForeignKey(
                        name: "FK_devices_sites_site_id",
                        column: x => x.site_id,
                        principalTable: "sites",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "probes",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    device_id = table.Column<Guid>(type: "uuid", nullable: false),
                    agent_group_id = table.Column<Guid>(type: "uuid", nullable: false),
                    type = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    interval_seconds = table.Column<int>(type: "integer", nullable: false),
                    timeout_milliseconds = table.Column<int>(type: "integer", nullable: false),
                    attempt_count = table.Column<int>(type: "integer", nullable: false),
                    warning_rtt_milliseconds = table.Column<int>(type: "integer", nullable: true),
                    critical_rtt_milliseconds = table.Column<int>(type: "integer", nullable: true),
                    failure_threshold = table.Column<int>(type: "integer", nullable: false),
                    recovery_threshold = table.Column<int>(type: "integer", nullable: false),
                    enabled = table.Column<bool>(type: "boolean", nullable: false),
                    config_version = table.Column<long>(type: "bigint", nullable: false),
                    row_version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_probes", x => x.id);
                    table.ForeignKey(
                        name: "FK_probes_agent_groups_agent_group_id",
                        column: x => x.agent_group_id,
                        principalTable: "agent_groups",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_probes_devices_device_id",
                        column: x => x.device_id,
                        principalTable: "devices",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "maintenance_windows",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    starts_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ends_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    timezone = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    site_id = table.Column<Guid>(type: "uuid", nullable: true),
                    device_id = table.Column<Guid>(type: "uuid", nullable: true),
                    probe_id = table.Column<Guid>(type: "uuid", nullable: true),
                    enabled = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    row_version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_maintenance_windows", x => x.id);
                    table.CheckConstraint("ck_maintenance_windows_one_scope", "num_nonnulls(site_id, device_id, probe_id) = 1");
                    table.ForeignKey(
                        name: "FK_maintenance_windows_devices_device_id",
                        column: x => x.device_id,
                        principalTable: "devices",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_maintenance_windows_probes_probe_id",
                        column: x => x.probe_id,
                        principalTable: "probes",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_maintenance_windows_sites_site_id",
                        column: x => x.site_id,
                        principalTable: "sites",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ux_agent_groups_name",
                table: "agent_groups",
                column: "name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_audit_events_entity_time",
                table: "audit_events",
                columns: new[] { "entity_type", "entity_id", "occurred_at" });

            migrationBuilder.CreateIndex(
                name: "ix_audit_events_occurred_at",
                table: "audit_events",
                column: "occurred_at");

            migrationBuilder.CreateIndex(
                name: "ix_devices_site_enabled",
                table: "devices",
                columns: new[] { "site_id", "enabled" });

            migrationBuilder.CreateIndex(
                name: "ux_devices_site_address",
                table: "devices",
                columns: new[] { "site_id", "address" },
                unique: true,
                filter: "\"enabled\"");

            migrationBuilder.CreateIndex(
                name: "IX_maintenance_windows_device_id",
                table: "maintenance_windows",
                column: "device_id");

            migrationBuilder.CreateIndex(
                name: "IX_maintenance_windows_probe_id",
                table: "maintenance_windows",
                column: "probe_id");

            migrationBuilder.CreateIndex(
                name: "ix_maintenance_windows_range",
                table: "maintenance_windows",
                columns: new[] { "starts_at", "ends_at" });

            migrationBuilder.CreateIndex(
                name: "IX_maintenance_windows_site_id",
                table: "maintenance_windows",
                column: "site_id");

            migrationBuilder.CreateIndex(
                name: "IX_probes_agent_group_id",
                table: "probes",
                column: "agent_group_id");

            migrationBuilder.CreateIndex(
                name: "ix_probes_device_type",
                table: "probes",
                columns: new[] { "device_id", "type" });

            migrationBuilder.CreateIndex(
                name: "ux_sites_code",
                table: "sites",
                column: "code",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "audit_events");

            migrationBuilder.DropTable(
                name: "maintenance_windows");

            migrationBuilder.DropTable(
                name: "probes");

            migrationBuilder.DropTable(
                name: "agent_groups");

            migrationBuilder.DropTable(
                name: "devices");

            migrationBuilder.DropTable(
                name: "sites");
        }
    }
}
#pragma warning restore CA1861
