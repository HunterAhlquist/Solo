using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Solo.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddAliasesAndTaskNumbers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "Number",
                table: "Tasks",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "Alias",
                table: "Projects",
                type: "character varying(8)",
                maxLength: 8,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "Alias",
                table: "Boards",
                type: "character varying(8)",
                maxLength: 8,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<int>(
                name: "NextTaskNumber",
                table: "Boards",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            // Backfill before the unique indexes go on. Existing projects and
            // boards get a placeholder alias from their id's first hex digits
            // (unique in practice; the owner renames it to something meaningful
            // in settings). Existing tasks are numbered in creation order — ids
            // are v7 GUIDs, so ordering by id is ordering by time — and each
            // board's counter continues from its highest number.
            migrationBuilder.Sql(
                """
                UPDATE "Projects" SET "Alias" = UPPER(LEFT(REPLACE("Id"::text, '-', ''), 8));
                UPDATE "Boards" SET "Alias" = UPPER(LEFT(REPLACE("Id"::text, '-', ''), 8));
                UPDATE "Tasks" AS t SET "Number" = numbered.rn
                FROM (
                    SELECT "Id", ROW_NUMBER() OVER (PARTITION BY "BoardId" ORDER BY "Id") AS rn
                    FROM "Tasks"
                ) AS numbered
                WHERE t."Id" = numbered."Id";
                UPDATE "Boards" AS b SET "NextTaskNumber" =
                    COALESCE((SELECT MAX("Number") FROM "Tasks" WHERE "BoardId" = b."Id"), 0) + 1;
                """);

            migrationBuilder.CreateIndex(
                name: "IX_Tasks_BoardId_Number",
                table: "Tasks",
                columns: new[] { "BoardId", "Number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Projects_Alias",
                table: "Projects",
                column: "Alias",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Boards_ProjectId_Alias",
                table: "Boards",
                columns: new[] { "ProjectId", "Alias" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Tasks_BoardId_Number",
                table: "Tasks");

            migrationBuilder.DropIndex(
                name: "IX_Projects_Alias",
                table: "Projects");

            migrationBuilder.DropIndex(
                name: "IX_Boards_ProjectId_Alias",
                table: "Boards");

            migrationBuilder.DropColumn(
                name: "Number",
                table: "Tasks");

            migrationBuilder.DropColumn(
                name: "Alias",
                table: "Projects");

            migrationBuilder.DropColumn(
                name: "Alias",
                table: "Boards");

            migrationBuilder.DropColumn(
                name: "NextTaskNumber",
                table: "Boards");
        }
    }
}
