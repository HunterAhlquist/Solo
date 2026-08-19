using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Solo.Mcp.Migrations
{
    /// <inheritdoc />
    public partial class InitialMcp : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "ext_mcp");

            migrationBuilder.CreateTable(
                name: "Tokens",
                schema: "ext_mcp",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TokenHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Issuer = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    Subject = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    Email = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true),
                    DisplayName = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    Description = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LastUsedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    RevokedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    RevokedReason = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Tokens", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "TokenUsage",
                schema: "ext_mcp",
                columns: table => new
                {
                    TokenId = table.Column<Guid>(type: "uuid", nullable: false),
                    Hour = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Count = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TokenUsage", x => new { x.TokenId, x.Hour });
                    table.ForeignKey(
                        name: "FK_TokenUsage_Tokens_TokenId",
                        column: x => x.TokenId,
                        principalSchema: "ext_mcp",
                        principalTable: "Tokens",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Tokens_Issuer_Subject",
                schema: "ext_mcp",
                table: "Tokens",
                columns: new[] { "Issuer", "Subject" });

            migrationBuilder.CreateIndex(
                name: "IX_Tokens_TokenHash",
                schema: "ext_mcp",
                table: "Tokens",
                column: "TokenHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TokenUsage_Hour",
                schema: "ext_mcp",
                table: "TokenUsage",
                column: "Hour");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TokenUsage",
                schema: "ext_mcp");

            migrationBuilder.DropTable(
                name: "Tokens",
                schema: "ext_mcp");
        }
    }
}
