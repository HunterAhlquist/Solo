using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Solo.DesignDocs.Migrations
{
    /// <inheritdoc />
    public partial class NoteScopedSketchNames : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Sketches_VaultId_Name",
                schema: "ext_design_docs",
                table: "Sketches");

            migrationBuilder.CreateIndex(
                name: "IX_Sketches_NoteId_Name",
                schema: "ext_design_docs",
                table: "Sketches",
                columns: new[] { "NoteId", "Name" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Sketches_NoteId_Name",
                schema: "ext_design_docs",
                table: "Sketches");

            migrationBuilder.CreateIndex(
                name: "IX_Sketches_VaultId_Name",
                schema: "ext_design_docs",
                table: "Sketches",
                columns: new[] { "VaultId", "Name" },
                unique: true);
        }
    }
}
