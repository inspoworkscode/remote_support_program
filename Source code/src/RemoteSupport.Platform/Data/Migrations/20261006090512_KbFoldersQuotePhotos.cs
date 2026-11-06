using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RemoteSupport.Platform.Data.Migrations
{
    /// <inheritdoc />
    public partial class KbFoldersQuotePhotos : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "QuoteLineId",
                table: "TicketPhotos",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "FolderId",
                table: "KbRevisions",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "FolderId",
                table: "KbArticles",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "KbFolders",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    TenantId = table.Column<int>(type: "INTEGER", nullable: false),
                    ClientId = table.Column<int>(type: "INTEGER", nullable: true),
                    ParentId = table.Column<int>(type: "INTEGER", nullable: true),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedBy = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_KbFolders", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TicketPhotos_TenantId_QuoteLineId",
                table: "TicketPhotos",
                columns: new[] { "TenantId", "QuoteLineId" });

            migrationBuilder.CreateIndex(
                name: "IX_KbArticles_TenantId_FolderId",
                table: "KbArticles",
                columns: new[] { "TenantId", "FolderId" });

            migrationBuilder.CreateIndex(
                name: "IX_KbFolders_TenantId_ClientId",
                table: "KbFolders",
                columns: new[] { "TenantId", "ClientId" });

            // Every quote line's one photo, carried across: a line's photos are now the TicketPhotos
            // that name it. Only Part photos (Kind 2) on the same ticket, as AddLineAsync always checked.
            migrationBuilder.Sql(@"
UPDATE ""TicketPhotos"" SET ""QuoteLineId"" = (
    SELECT q.""Id"" FROM ""QuoteLines"" q
    WHERE q.""PhotoId"" = ""TicketPhotos"".""Id"" AND q.""TicketId"" = ""TicketPhotos"".""TicketId"" AND q.""TenantId"" = ""TicketPhotos"".""TenantId""
    ORDER BY q.""Id"" LIMIT 1)
WHERE ""Kind"" = 2 AND EXISTS (SELECT 1 FROM ""QuoteLines"" q2 WHERE q2.""PhotoId"" = ""TicketPhotos"".""Id"");");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "KbFolders");

            migrationBuilder.DropIndex(
                name: "IX_TicketPhotos_TenantId_QuoteLineId",
                table: "TicketPhotos");

            migrationBuilder.DropIndex(
                name: "IX_KbArticles_TenantId_FolderId",
                table: "KbArticles");

            migrationBuilder.DropColumn(
                name: "QuoteLineId",
                table: "TicketPhotos");

            migrationBuilder.DropColumn(
                name: "FolderId",
                table: "KbRevisions");

            migrationBuilder.DropColumn(
                name: "FolderId",
                table: "KbArticles");
        }
    }
}
