using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Solo.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddEmail : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Defaults match the entity's, so people who already have a row get
            // the same daily digest as everyone else.
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "EmailedAt",
                table: "Notifications",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DigestFrequency",
                table: "NotificationPreferences",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "Daily");

            migrationBuilder.AddColumn<int>(
                name: "DigestHour",
                table: "NotificationPreferences",
                type: "integer",
                nullable: false,
                defaultValue: 8);

            migrationBuilder.AddColumn<string>(
                name: "DigestWeekday",
                table: "NotificationPreferences",
                type: "character varying(10)",
                maxLength: 10,
                nullable: false,
                defaultValue: "Monday");

            migrationBuilder.AddColumn<bool>(
                name: "EmailOnAssigned",
                table: "NotificationPreferences",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<bool>(
                name: "EmailOnDeadline",
                table: "NotificationPreferences",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LastDigestAt",
                table: "NotificationPreferences",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "DataProtectionKeys",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    FriendlyName = table.Column<string>(type: "text", nullable: true),
                    Xml = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DataProtectionKeys", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "EmailMessages",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    To = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                    Subject = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    HtmlBody = table.Column<string>(type: "text", nullable: false),
                    TextBody = table.Column<string>(type: "text", nullable: false),
                    Kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    UnsubscribeUrl = table.Column<string>(type: "character varying(3048)", maxLength: 3048, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    NextAttemptAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Attempts = table.Column<int>(type: "integer", nullable: false),
                    SentAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    FailedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastError = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EmailMessages", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "EmailSettings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    PublicAddress = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    FromName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    FromAddress = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true),
                    SmtpHost = table.Column<string>(type: "character varying(253)", maxLength: 253, nullable: true),
                    SmtpPort = table.Column<int>(type: "integer", nullable: false),
                    Security = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Username = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    ProtectedPassword = table.Column<string>(type: "text", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EmailSettings", x => x.Id);
                    table.CheckConstraint("CK_EmailSettings_Singleton", "\"Id\" = 1");
                });

            migrationBuilder.CreateIndex(
                name: "IX_EmailMessages_CreatedAt",
                table: "EmailMessages",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_EmailMessages_NextAttemptAt",
                table: "EmailMessages",
                column: "NextAttemptAt",
                filter: "\"SentAt\" IS NULL AND \"FailedAt\" IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DataProtectionKeys");

            migrationBuilder.DropTable(
                name: "EmailMessages");

            migrationBuilder.DropTable(
                name: "EmailSettings");

            migrationBuilder.DropColumn(
                name: "EmailedAt",
                table: "Notifications");

            migrationBuilder.DropColumn(
                name: "DigestFrequency",
                table: "NotificationPreferences");

            migrationBuilder.DropColumn(
                name: "DigestHour",
                table: "NotificationPreferences");

            migrationBuilder.DropColumn(
                name: "DigestWeekday",
                table: "NotificationPreferences");

            migrationBuilder.DropColumn(
                name: "EmailOnAssigned",
                table: "NotificationPreferences");

            migrationBuilder.DropColumn(
                name: "EmailOnDeadline",
                table: "NotificationPreferences");

            migrationBuilder.DropColumn(
                name: "LastDigestAt",
                table: "NotificationPreferences");
        }
    }
}
