using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RemoteSupport.Platform.Data.Migrations
{
    /// <inheritdoc />
    public partial class QuoteRequestSignatures : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "QuoteSignatureId",
                table: "Tickets",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "LastQuoteSignatureId",
                table: "Technicians",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "QuoteSignatures",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    TenantId = table.Column<int>(type: "INTEGER", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    LinesJson = table.Column<string>(type: "TEXT", nullable: false),
                    Image = table.Column<byte[]>(type: "BLOB", nullable: true),
                    ImageType = table.Column<string>(type: "TEXT", nullable: false),
                    ImageToken = table.Column<string>(type: "TEXT", nullable: false),
                    ImageWidth = table.Column<int>(type: "INTEGER", nullable: false),
                    ImageFirst = table.Column<bool>(type: "INTEGER", nullable: false),
                    RemovedUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CreatedBy = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_QuoteSignatures", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_QuoteSignatures_ImageToken",
                table: "QuoteSignatures",
                column: "ImageToken");

            migrationBuilder.CreateIndex(
                name: "IX_QuoteSignatures_TenantId",
                table: "QuoteSignatures",
                column: "TenantId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "QuoteSignatures");

            migrationBuilder.DropColumn(
                name: "QuoteSignatureId",
                table: "Tickets");

            migrationBuilder.DropColumn(
                name: "LastQuoteSignatureId",
                table: "Technicians");
        }
    }
}
