using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Solo.Data.Migrations
{
    /// <summary>
    /// Moves labels from the project to the board. A null <c>BoardId</c> used to
    /// mean "project-global, visible on every board"; it now means "project
    /// default", a template no existing board follows. So every board gets its
    /// own copy of the project's labels — tagged with the default it came from,
    /// which is what a later reset restores in place — and the items that
    /// carried the shared label are repointed at their own board's copy.
    /// </summary>
    public partial class BoardScopedLabels : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "SourceId",
                table: "Labels",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Labels_SourceId",
                table: "Labels",
                column: "SourceId");

            migrationBuilder.AddForeignKey(
                name: "FK_Labels_Labels_SourceId",
                table: "Labels",
                column: "SourceId",
                principalTable: "Labels",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.Sql("""
                CREATE TEMP TABLE label_copies (old_id uuid, new_id uuid, board_id uuid);

                INSERT INTO label_copies (old_id, new_id, board_id)
                SELECT l."Id", gen_random_uuid(), b."Id"
                FROM "Labels" l
                JOIN "Boards" b ON b."ProjectId" = l."ProjectId"
                WHERE l."BoardId" IS NULL;

                INSERT INTO "Labels" ("Id", "ProjectId", "BoardId", "SourceId", "Kind", "Name", "Color", "Icon", "ImageId")
                SELECT c.new_id, l."ProjectId", c.board_id, l."Id", l."Kind", l."Name", l."Color", l."Icon", l."ImageId"
                FROM label_copies c
                JOIN "Labels" l ON l."Id" = c.old_id;

                UPDATE "Tasks" t
                SET "LabelId" = c.new_id
                FROM label_copies c
                WHERE t."LabelId" = c.old_id AND t."BoardId" = c.board_id;

                UPDATE "SubTasks" s
                SET "LabelId" = c.new_id
                FROM label_copies c, "Tasks" t
                WHERE s."TaskId" = t."Id" AND s."LabelId" = c.old_id AND t."BoardId" = c.board_id;

                DROP TABLE label_copies;
                """);
        }

        /// <summary>
        /// Sends items back to the project-level label of the same kind and name,
        /// then drops every board-owned label. Lossy by nature: a label a board
        /// added for itself has no project-level equivalent to return to.
        /// </summary>
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                CREATE TEMP TABLE label_origins (board_label_id uuid, project_label_id uuid);

                INSERT INTO label_origins (board_label_id, project_label_id)
                SELECT b."Id", COALESCE(b."SourceId", p."Id")
                FROM "Labels" b
                LEFT JOIN "Labels" p
                  ON p."ProjectId" = b."ProjectId"
                 AND p."BoardId" IS NULL
                 AND p."Kind" = b."Kind"
                 AND p."Name" = b."Name"
                WHERE b."BoardId" IS NOT NULL;

                UPDATE "Tasks" t
                SET "LabelId" = o.project_label_id
                FROM label_origins o
                WHERE t."LabelId" = o.board_label_id;

                UPDATE "SubTasks" s
                SET "LabelId" = o.project_label_id
                FROM label_origins o
                WHERE s."LabelId" = o.board_label_id;

                DELETE FROM "Labels" WHERE "BoardId" IS NOT NULL;

                DROP TABLE label_origins;
                """);

            migrationBuilder.DropForeignKey(
                name: "FK_Labels_Labels_SourceId",
                table: "Labels");

            migrationBuilder.DropIndex(
                name: "IX_Labels_SourceId",
                table: "Labels");

            migrationBuilder.DropColumn(
                name: "SourceId",
                table: "Labels");
        }
    }
}
