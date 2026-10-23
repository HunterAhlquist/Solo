using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Solo.DesignDocs.Migrations
{
    /// <inheritdoc />
    public partial class InitialDesignDocs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "ext_design_docs");

            migrationBuilder.CreateTable(
                name: "MemberColors",
                schema: "ext_design_docs",
                columns: table => new
                {
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Slot = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MemberColors", x => new { x.ProjectId, x.UserId });
                });

            migrationBuilder.CreateTable(
                name: "Vaults",
                schema: "ext_design_docs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    IsClosed = table.Column<bool>(type: "boolean", nullable: false),
                    ClosedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uuid", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Vaults", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "DocUpdates",
                schema: "ext_design_docs",
                columns: table => new
                {
                    Seq = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    DocumentId = table.Column<Guid>(type: "uuid", nullable: false),
                    VaultId = table.Column<Guid>(type: "uuid", nullable: false),
                    IsSnapshot = table.Column<bool>(type: "boolean", nullable: false),
                    Data = table.Column<byte[]>(type: "bytea", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    At = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DocUpdates", x => x.Seq);
                    table.ForeignKey(
                        name: "FK_DocUpdates_Vaults_VaultId",
                        column: x => x.VaultId,
                        principalSchema: "ext_design_docs",
                        principalTable: "Vaults",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Folders",
                schema: "ext_design_docs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    VaultId = table.Column<Guid>(type: "uuid", nullable: false),
                    ParentId = table.Column<Guid>(type: "uuid", nullable: true),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Folders", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Folders_Folders_ParentId",
                        column: x => x.ParentId,
                        principalSchema: "ext_design_docs",
                        principalTable: "Folders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_Folders_Vaults_VaultId",
                        column: x => x.VaultId,
                        principalSchema: "ext_design_docs",
                        principalTable: "Vaults",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Images",
                schema: "ext_design_docs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    VaultId = table.Column<Guid>(type: "uuid", nullable: false),
                    Path = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                    ContentType = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Data = table.Column<byte[]>(type: "bytea", nullable: false),
                    Width = table.Column<int>(type: "integer", nullable: false),
                    Height = table.Column<int>(type: "integer", nullable: false),
                    Bytes = table.Column<long>(type: "bigint", nullable: false),
                    UploadedBy = table.Column<Guid>(type: "uuid", nullable: false),
                    UploadedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Images", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Images_Vaults_VaultId",
                        column: x => x.VaultId,
                        principalSchema: "ext_design_docs",
                        principalTable: "Vaults",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Notes",
                schema: "ext_design_docs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    VaultId = table.Column<Guid>(type: "uuid", nullable: false),
                    FolderId = table.Column<Guid>(type: "uuid", nullable: true),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Markdown = table.Column<string>(type: "text", nullable: false),
                    MarkdownSeq = table.Column<long>(type: "bigint", nullable: false),
                    Headings = table.Column<string>(type: "jsonb", nullable: false),
                    SeedMarkdown = table.Column<string>(type: "text", nullable: true),
                    LastEditedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    LastEditedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Notes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Notes_Folders_FolderId",
                        column: x => x.FolderId,
                        principalSchema: "ext_design_docs",
                        principalTable: "Folders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_Notes_Vaults_VaultId",
                        column: x => x.VaultId,
                        principalSchema: "ext_design_docs",
                        principalTable: "Vaults",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Sketches",
                schema: "ext_design_docs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    VaultId = table.Column<Guid>(type: "uuid", nullable: false),
                    NoteId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    PageSize = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Orientation = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Pattern = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    LastEditedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    LastEditedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Sketches", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Sketches_Notes_NoteId",
                        column: x => x.NoteId,
                        principalSchema: "ext_design_docs",
                        principalTable: "Notes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_Sketches_Vaults_VaultId",
                        column: x => x.VaultId,
                        principalSchema: "ext_design_docs",
                        principalTable: "Vaults",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DocUpdates_DocumentId_Seq",
                schema: "ext_design_docs",
                table: "DocUpdates",
                columns: new[] { "DocumentId", "Seq" });

            migrationBuilder.CreateIndex(
                name: "IX_DocUpdates_VaultId",
                schema: "ext_design_docs",
                table: "DocUpdates",
                column: "VaultId");

            migrationBuilder.CreateIndex(
                name: "IX_Folders_ParentId",
                schema: "ext_design_docs",
                table: "Folders",
                column: "ParentId");

            migrationBuilder.CreateIndex(
                name: "IX_Folders_VaultId_ParentId_Name",
                schema: "ext_design_docs",
                table: "Folders",
                columns: new[] { "VaultId", "ParentId", "Name" },
                unique: true)
                .Annotation("Npgsql:NullsDistinct", false);

            migrationBuilder.CreateIndex(
                name: "IX_Images_VaultId_Path",
                schema: "ext_design_docs",
                table: "Images",
                columns: new[] { "VaultId", "Path" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MemberColors_ProjectId_Slot",
                schema: "ext_design_docs",
                table: "MemberColors",
                columns: new[] { "ProjectId", "Slot" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Notes_FolderId",
                schema: "ext_design_docs",
                table: "Notes",
                column: "FolderId");

            migrationBuilder.CreateIndex(
                name: "IX_Notes_VaultId_FolderId_Name",
                schema: "ext_design_docs",
                table: "Notes",
                columns: new[] { "VaultId", "FolderId", "Name" },
                unique: true)
                .Annotation("Npgsql:NullsDistinct", false);

            migrationBuilder.CreateIndex(
                name: "IX_Sketches_NoteId",
                schema: "ext_design_docs",
                table: "Sketches",
                column: "NoteId");

            migrationBuilder.CreateIndex(
                name: "IX_Sketches_VaultId_Name",
                schema: "ext_design_docs",
                table: "Sketches",
                columns: new[] { "VaultId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Vaults_ProjectId_Name",
                schema: "ext_design_docs",
                table: "Vaults",
                columns: new[] { "ProjectId", "Name" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DocUpdates",
                schema: "ext_design_docs");

            migrationBuilder.DropTable(
                name: "Images",
                schema: "ext_design_docs");

            migrationBuilder.DropTable(
                name: "MemberColors",
                schema: "ext_design_docs");

            migrationBuilder.DropTable(
                name: "Sketches",
                schema: "ext_design_docs");

            migrationBuilder.DropTable(
                name: "Notes",
                schema: "ext_design_docs");

            migrationBuilder.DropTable(
                name: "Folders",
                schema: "ext_design_docs");

            migrationBuilder.DropTable(
                name: "Vaults",
                schema: "ext_design_docs");
        }
    }
}
