using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RemoteSupport.Platform.Data.Migrations
{
    /// <inheritdoc />
    public partial class ClientUpdates : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "FromCustomer",
                table: "TicketNotes",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "TicketClientUpdates",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    TenantId = table.Column<int>(type: "INTEGER", nullable: false),
                    TicketId = table.Column<int>(type: "INTEGER", nullable: false),
                    NoteId = table.Column<long>(type: "INTEGER", nullable: false),
                    ToAddress = table.Column<string>(type: "TEXT", nullable: false),
                    ToName = table.Column<string>(type: "TEXT", nullable: false),
                    SentBy = table.Column<string>(type: "TEXT", nullable: false),
                    SentUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Sent = table.Column<bool>(type: "INTEGER", nullable: false),
                    Result = table.Column<string>(type: "TEXT", nullable: false),
                    MessageId = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TicketClientUpdates", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TicketClientUpdates_TenantId_TicketId",
                table: "TicketClientUpdates",
                columns: new[] { "TenantId", "TicketId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TicketClientUpdates");

            migrationBuilder.DropColumn(
                name: "FromCustomer",
                table: "TicketNotes");
        }
    }
}
