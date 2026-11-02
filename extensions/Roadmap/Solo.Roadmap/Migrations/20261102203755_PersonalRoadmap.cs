using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Solo.Roadmap.Migrations
{
    /// <inheritdoc />
    public partial class PersonalRoadmap : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PersonalEntries",
                schema: "ext_roadmap",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    TaskId = table.Column<Guid>(type: "uuid", nullable: false),
                    Rank = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: false),
                    AnchorDay = table.Column<DateOnly>(type: "date", nullable: false),
                    StartAt = table.Column<DateOnly>(type: "date", nullable: true),
                    EndAt = table.Column<DateOnly>(type: "date", nullable: true),
                    AddedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PersonalEntries", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PersonalEntries_TaskId",
                schema: "ext_roadmap",
                table: "PersonalEntries",
                column: "TaskId");

            migrationBuilder.CreateIndex(
                name: "IX_PersonalEntries_UserId_TaskId",
                schema: "ext_roadmap",
                table: "PersonalEntries",
                columns: new[] { "UserId", "TaskId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PersonalEntries",
                schema: "ext_roadmap");
        }
    }
}
