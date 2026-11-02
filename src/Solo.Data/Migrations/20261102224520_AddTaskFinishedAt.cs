using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Solo.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddTaskFinishedAt : Migration
    {
        /// <summary>
        /// Backfills already-finished tasks from the audit log: the newest change
        /// to a finished task's status is the one that finished it. A task whose
        /// audit rows were pruned falls back to its creation time, which can only
        /// make it look older than it is, and pruning only happens to old rows.
        /// </summary>
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "FinishedAt",
                table: "Tasks",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.Sql("""
                UPDATE "Tasks" t
                SET "FinishedAt" = COALESCE(
                    (SELECT max(a."Timestamp")
                     FROM "AuditEntries" a
                     WHERE a."TableName" = 'Tasks'
                       AND a."ColumnName" = 'Status'
                       AND a."RowId" = t."Id"::text),
                    t."CreatedAt")
                WHERE t."Status" = 'Finished';
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "FinishedAt",
                table: "Tasks");
        }
    }
}
