using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Solo.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddEpicGroups : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "GroupId",
                table: "Epics",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "EpicGroups",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    BoardId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    Rank = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EpicGroups", x => x.Id);
                    table.ForeignKey(
                        name: "FK_EpicGroups_Boards_BoardId",
                        column: x => x.BoardId,
                        principalTable: "Boards",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Epics_GroupId",
                table: "Epics",
                column: "GroupId");

            migrationBuilder.CreateIndex(
                name: "IX_EpicGroups_BoardId",
                table: "EpicGroups",
                column: "BoardId");

            migrationBuilder.AddForeignKey(
                name: "FK_Epics_EpicGroups_GroupId",
                table: "Epics",
                column: "GroupId",
                principalTable: "EpicGroups",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Epics_EpicGroups_GroupId",
                table: "Epics");

            migrationBuilder.DropTable(
                name: "EpicGroups");

            migrationBuilder.DropIndex(
                name: "IX_Epics_GroupId",
                table: "Epics");

            migrationBuilder.DropColumn(
                name: "GroupId",
                table: "Epics");
        }
    }
}
