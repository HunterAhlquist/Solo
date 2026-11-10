using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Solo.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddExtensionUpdateFeed : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "UpdateFeed",
                table: "ExtensionRegistrations",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "UpdateFeed",
                table: "ExtensionRegistrations");
        }
    }
}
