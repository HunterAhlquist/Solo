using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Solo.Mcp.Migrations
{
    /// <inheritdoc />
    public partial class AddAuditAttributions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AuditAttributions",
                schema: "ext_mcp",
                columns: table => new
                {
                    AuditEntryId = table.Column<long>(type: "bigint", nullable: false),
                    TokenId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AuditAttributions", x => x.AuditEntryId);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AuditAttributions",
                schema: "ext_mcp");
        }
    }
}
