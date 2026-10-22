using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Solo.DesignDocs.Migrations
{
    /// <inheritdoc />
    public partial class SeedLeaseIdentity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "SeededByToken",
                schema: "ext_design_docs",
                table: "Notes",
                type: "uuid",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SeededByToken",
                schema: "ext_design_docs",
                table: "Notes");
        }
    }
}
