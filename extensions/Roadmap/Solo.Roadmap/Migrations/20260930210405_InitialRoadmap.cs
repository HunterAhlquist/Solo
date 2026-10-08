using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Solo.Roadmap.Migrations
{
    /// <inheritdoc />
    public partial class InitialRoadmap : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "ext_roadmap");

            migrationBuilder.CreateTable(
                name: "Roadmaps",
                schema: "ext_roadmap",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    Title = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Roadmaps", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Settings",
                schema: "ext_roadmap",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false),
                    PastDays = table.Column<int>(type: "integer", nullable: false),
                    FutureDays = table.Column<int>(type: "integer", nullable: false),
                    StripeAnchor = table.Column<DateOnly>(type: "date", nullable: false),
                    StripeIntervalDays = table.Column<int>(type: "integer", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Settings", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Entries",
                schema: "ext_roadmap",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    RoadmapId = table.Column<Guid>(type: "uuid", nullable: false),
                    TaskId = table.Column<Guid>(type: "uuid", nullable: false),
                    Rank = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: false),
                    AnchorDay = table.Column<DateOnly>(type: "date", nullable: false),
                    AddedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Entries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Entries_Roadmaps_RoadmapId",
                        column: x => x.RoadmapId,
                        principalSchema: "ext_roadmap",
                        principalTable: "Roadmaps",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Entries_RoadmapId_TaskId",
                schema: "ext_roadmap",
                table: "Entries",
                columns: new[] { "RoadmapId", "TaskId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Entries_TaskId",
                schema: "ext_roadmap",
                table: "Entries",
                column: "TaskId");

            migrationBuilder.CreateIndex(
                name: "IX_Roadmaps_ProjectId",
                schema: "ext_roadmap",
                table: "Roadmaps",
                column: "ProjectId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Entries",
                schema: "ext_roadmap");

            migrationBuilder.DropTable(
                name: "Settings",
                schema: "ext_roadmap");

            migrationBuilder.DropTable(
                name: "Roadmaps",
                schema: "ext_roadmap");
        }
    }
}
