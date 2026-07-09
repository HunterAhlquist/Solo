using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Solo.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddIconsAndImages : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "ImageId",
                table: "Projects",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Icon",
                table: "Labels",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ImageId",
                table: "Labels",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ImageId",
                table: "Boards",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ImageAssets",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ContentType = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Data = table.Column<byte[]>(type: "bytea", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ImageAssets", x => x.Id);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ImageAssets");

            migrationBuilder.DropColumn(
                name: "ImageId",
                table: "Projects");

            migrationBuilder.DropColumn(
                name: "Icon",
                table: "Labels");

            migrationBuilder.DropColumn(
                name: "ImageId",
                table: "Labels");

            migrationBuilder.DropColumn(
                name: "ImageId",
                table: "Boards");
        }
    }
}
