using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RemoteSupport.Platform.Data.Migrations
{
    /// <inheritdoc />
    public partial class WorkshopPhotos : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "TicketPhotos",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    TenantId = table.Column<int>(type: "INTEGER", nullable: false),
                    TicketId = table.Column<int>(type: "INTEGER", nullable: true),
                    Kind = table.Column<int>(type: "INTEGER", nullable: false),
                    FileName = table.Column<string>(type: "TEXT", nullable: false),
                    OriginalName = table.Column<string>(type: "TEXT", nullable: false),
                    Bytes = table.Column<long>(type: "INTEGER", nullable: false),
                    UploadedBy = table.Column<string>(type: "TEXT", nullable: false),
                    UploadedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    StageId = table.Column<string>(type: "TEXT", nullable: false),
                    StagedBy = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TicketPhotos", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TicketPhotos_Tickets_TicketId",
                        column: x => x.TicketId,
                        principalTable: "Tickets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TicketPhotos_TenantId_StageId",
                table: "TicketPhotos",
                columns: new[] { "TenantId", "StageId" });

            migrationBuilder.CreateIndex(
                name: "IX_TicketPhotos_TenantId_TicketId",
                table: "TicketPhotos",
                columns: new[] { "TenantId", "TicketId" });

            migrationBuilder.CreateIndex(
                name: "IX_TicketPhotos_TicketId",
                table: "TicketPhotos",
                column: "TicketId");

            migrationBuilder.CreateIndex(
                name: "IX_TicketPhotos_UploadedUtc",
                table: "TicketPhotos",
                column: "UploadedUtc");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TicketPhotos");
        }
    }
}
