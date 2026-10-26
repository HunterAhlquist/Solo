using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Solo.DesignDocs.Migrations
{
    /// <inheritdoc />
    public partial class OfflineCommandReceipts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "OfflineReceipts",
                schema: "ext_design_docs",
                columns: table => new
                {
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    VaultId = table.Column<Guid>(type: "uuid", nullable: false),
                    Hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Result = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OfflineReceipts", x => new { x.UserId, x.Id });
                });

            migrationBuilder.CreateIndex(
                name: "IX_OfflineReceipts_ProjectId",
                schema: "ext_design_docs",
                table: "OfflineReceipts",
                column: "ProjectId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "OfflineReceipts",
                schema: "ext_design_docs");
        }
    }
}
