using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Solo.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddTaskDeadlines : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Backfill pre-existing tasks with "now": their true creation instant is
            // unknown, and a year-0001 sentinel would render any future deadline as
            // fully elapsed (solid red) the moment one is set.
            migrationBuilder.AddColumn<DateTime>(
                name: "CreatedAt",
                table: "Tasks",
                type: "timestamp with time zone",
                nullable: false,
                defaultValueSql: "now()");

            migrationBuilder.AddColumn<DateOnly>(
                name: "DeadlineAt",
                table: "Tasks",
                type: "date",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CreatedAt",
                table: "Tasks");

            migrationBuilder.DropColumn(
                name: "DeadlineAt",
                table: "Tasks");
        }
    }
}
