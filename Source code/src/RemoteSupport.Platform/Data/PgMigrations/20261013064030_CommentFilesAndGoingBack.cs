using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace RemoteSupport.Platform.Data.PgMigrations
{
    /// <inheritdoc />
    public partial class CommentFilesAndGoingBack : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Before",
                table: "TicketNotes",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "UndoneBy",
                table: "TicketNotes",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<DateTime>(
                name: "UndoneUtc",
                table: "TicketNotes",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "WentBack",
                table: "TicketNotes",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "TicketAttachments",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    TenantId = table.Column<int>(type: "integer", nullable: false),
                    TicketId = table.Column<int>(type: "integer", nullable: false),
                    NoteId = table.Column<long>(type: "bigint", nullable: true),
                    FileName = table.Column<string>(type: "text", nullable: false),
                    OriginalName = table.Column<string>(type: "text", nullable: false),
                    ContentType = table.Column<string>(type: "text", nullable: false),
                    Bytes = table.Column<long>(type: "bigint", nullable: false),
                    UploadedBy = table.Column<string>(type: "text", nullable: false),
                    UploadedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TicketAttachments", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TicketAttachments_NoteId",
                table: "TicketAttachments",
                column: "NoteId");

            migrationBuilder.CreateIndex(
                name: "IX_TicketAttachments_TenantId_TicketId",
                table: "TicketAttachments",
                columns: new[] { "TenantId", "TicketId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TicketAttachments");

            migrationBuilder.DropColumn(
                name: "Before",
                table: "TicketNotes");

            migrationBuilder.DropColumn(
                name: "UndoneBy",
                table: "TicketNotes");

            migrationBuilder.DropColumn(
                name: "UndoneUtc",
                table: "TicketNotes");

            migrationBuilder.DropColumn(
                name: "WentBack",
                table: "TicketNotes");
        }
    }
}
