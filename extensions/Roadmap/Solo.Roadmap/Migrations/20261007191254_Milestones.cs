using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Solo.Roadmap.Migrations
{
    /// <inheritdoc />
    public partial class Milestones : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Milestones",
                schema: "ext_roadmap",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    RoadmapId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Day = table.Column<DateOnly>(type: "date", nullable: false),
                    Color = table.Column<string>(type: "character varying(7)", maxLength: 7, nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Milestones", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Milestones_Roadmaps_RoadmapId",
                        column: x => x.RoadmapId,
                        principalSchema: "ext_roadmap",
                        principalTable: "Roadmaps",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Milestones_RoadmapId",
                schema: "ext_roadmap",
                table: "Milestones",
                column: "RoadmapId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Milestones",
                schema: "ext_roadmap");
        }
    }
}
