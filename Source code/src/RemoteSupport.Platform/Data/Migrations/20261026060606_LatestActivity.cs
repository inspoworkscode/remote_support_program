using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RemoteSupport.Platform.Data.Migrations
{
    /// <inheritdoc />
    public partial class LatestActivity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "Automatic",
                table: "TicketNotes",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "ActivityMine",
                table: "Technicians",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTime>(
                name: "ActivitySeenUtc",
                table: "Technicians",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Tickets_TenantId_CreatedUtc",
                table: "Tickets",
                columns: new[] { "TenantId", "CreatedUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_TicketNotes_TenantId_CreatedUtc",
                table: "TicketNotes",
                columns: new[] { "TenantId", "CreatedUtc" });

            // Latest activity (2026-10-26, HANDOVER §164) leaves out what a ticket writes by itself while something else happens.
            // From today each writer says so; for everything before, every automatic-looking line counts as automatic — the list
            // shows less of the past rather than a session's every line.
            migrationBuilder.Sql(@"UPDATE ""TicketNotes"" SET ""Automatic"" = ""IsSystem""");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Tickets_TenantId_CreatedUtc",
                table: "Tickets");

            migrationBuilder.DropIndex(
                name: "IX_TicketNotes_TenantId_CreatedUtc",
                table: "TicketNotes");

            migrationBuilder.DropColumn(
                name: "Automatic",
                table: "TicketNotes");

            migrationBuilder.DropColumn(
                name: "ActivityMine",
                table: "Technicians");

            migrationBuilder.DropColumn(
                name: "ActivitySeenUtc",
                table: "Technicians");
        }
    }
}
