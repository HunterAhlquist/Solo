using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Solo.Roadmap.Migrations
{
    /// <inheritdoc />
    public partial class SprintIndication : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "SprintStartDay",
                schema: "ext_roadmap",
                table: "Settings",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<int>(
                name: "SprintLengthWeeks",
                schema: "ext_roadmap",
                table: "Settings",
                type: "integer",
                nullable: false,
                defaultValue: 2);

            // Keep what an admin chose: the anchor's weekday, and the interval in
            // whole weeks. An anchor on 1 January is the old default rather than a
            // choice, and its weekday is an accident of the year, so it becomes
            // Monday. Postgres's DOW and .NET's DayOfWeek both count Sunday as 0.
            migrationBuilder.Sql("""
                UPDATE ext_roadmap."Settings" SET
                    "SprintStartDay" = CASE WHEN EXTRACT(DOY FROM "StripeAnchor") = 1 THEN 1
                                            ELSE EXTRACT(DOW FROM "StripeAnchor")::integer END,
                    "SprintLengthWeeks" = GREATEST(1, LEAST(52, ROUND("StripeIntervalDays" / 7.0)::integer));
                """);

            migrationBuilder.DropColumn(
                name: "StripeAnchor",
                schema: "ext_roadmap",
                table: "Settings");

            migrationBuilder.DropColumn(
                name: "StripeIntervalDays",
                schema: "ext_roadmap",
                table: "Settings");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateOnly>(
                name: "StripeAnchor",
                schema: "ext_roadmap",
                table: "Settings",
                type: "date",
                nullable: false,
                defaultValue: new DateOnly(1, 1, 1));

            migrationBuilder.AddColumn<int>(
                name: "StripeIntervalDays",
                schema: "ext_roadmap",
                table: "Settings",
                type: "integer",
                nullable: false,
                defaultValue: 14);

            // The first sprint start day of the current year, every so many days.
            migrationBuilder.Sql("""
                UPDATE ext_roadmap."Settings" SET
                    "StripeAnchor" = date_trunc('year', now())::date
                        + (("SprintStartDay" - EXTRACT(DOW FROM date_trunc('year', now()))::integer + 7) % 7),
                    "StripeIntervalDays" = "SprintLengthWeeks" * 7;
                """);

            migrationBuilder.DropColumn(
                name: "SprintStartDay",
                schema: "ext_roadmap",
                table: "Settings");

            migrationBuilder.DropColumn(
                name: "SprintLengthWeeks",
                schema: "ext_roadmap",
                table: "Settings");
        }
    }
}
