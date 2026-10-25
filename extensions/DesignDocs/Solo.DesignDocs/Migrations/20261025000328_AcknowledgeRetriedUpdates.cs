using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Solo.DesignDocs.Migrations
{
    /// <inheritdoc />
    public partial class AcknowledgeRetriedUpdates : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "ClientUpdateId",
                schema: "ext_design_docs",
                table: "DocUpdates",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_DocUpdates_DocumentId_ClientUpdateId",
                schema: "ext_design_docs",
                table: "DocUpdates",
                columns: new[] { "DocumentId", "ClientUpdateId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_DocUpdates_DocumentId_ClientUpdateId",
                schema: "ext_design_docs",
                table: "DocUpdates");

            migrationBuilder.DropColumn(
                name: "ClientUpdateId",
                schema: "ext_design_docs",
                table: "DocUpdates");
        }
    }
}
