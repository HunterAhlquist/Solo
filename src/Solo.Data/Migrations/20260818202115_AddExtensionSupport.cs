using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Solo.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddExtensionSupport : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Extensions",
                table: "Tasks",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Extensions",
                table: "Epics",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Extensions",
                table: "Boards",
                type: "jsonb",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ExtensionRegistrations",
                columns: table => new
                {
                    ExtensionId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    SchemaName = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    DisplayName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    LastSeenVersion = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    InstalledAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LastLoadedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    State = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    FailureReason = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ExtensionRegistrations", x => x.ExtensionId);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ExtensionRegistrations");

            migrationBuilder.DropColumn(
                name: "Extensions",
                table: "Tasks");

            migrationBuilder.DropColumn(
                name: "Extensions",
                table: "Epics");

            migrationBuilder.DropColumn(
                name: "Extensions",
                table: "Boards");
        }
    }
}
