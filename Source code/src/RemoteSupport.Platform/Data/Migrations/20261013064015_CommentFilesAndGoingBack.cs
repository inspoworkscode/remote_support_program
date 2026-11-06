using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RemoteSupport.Platform.Data.Migrations
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
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "UndoneBy",
                table: "TicketNotes",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<DateTime>(
                name: "UndoneUtc",
                table: "TicketNotes",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "WentBack",
                table: "TicketNotes",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "TicketAttachments",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    TenantId = table.Column<int>(type: "INTEGER", nullable: false),
                    TicketId = table.Column<int>(type: "INTEGER", nullable: false),
                    NoteId = table.Column<long>(type: "INTEGER", nullable: true),
                    FileName = table.Column<string>(type: "TEXT", nullable: false),
                    OriginalName = table.Column<string>(type: "TEXT", nullable: false),
                    ContentType = table.Column<string>(type: "TEXT", nullable: false),
                    Bytes = table.Column<long>(type: "INTEGER", nullable: false),
                    UploadedBy = table.Column<string>(type: "TEXT", nullable: false),
                    UploadedUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
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
