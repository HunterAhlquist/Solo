using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Solo.Extension.Fixture.Migrations
{
    /// <inheritdoc />
    public partial class InitialFixture : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "ext_fixture");

            migrationBuilder.CreateTable(
                name: "Milestones",
                schema: "ext_fixture",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    TaskId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Milestones", x => x.Id);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Milestones",
                schema: "ext_fixture");
        }
    }
}
