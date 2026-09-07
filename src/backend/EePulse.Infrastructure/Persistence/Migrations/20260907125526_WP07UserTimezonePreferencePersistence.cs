using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EePulse.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class WP07UserTimezonePreferencePersistence : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "user_timezone_preferences",
                columns: table => new
                {
                    issuer = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    subject = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    timezone = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    version = table.Column<long>(type: "bigint", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_user_timezone_preferences", x => new { x.issuer, x.subject });
                    table.CheckConstraint("ck_user_timezone_preferences_issuer", "char_length(issuer) BETWEEN 1 AND 512");
                    table.CheckConstraint("ck_user_timezone_preferences_subject", "char_length(subject) BETWEEN 1 AND 512");
                    table.CheckConstraint("ck_user_timezone_preferences_version", "version >= 1");
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "user_timezone_preferences");
        }
    }
}
