using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RemoteSupport.Platform.Data.PgMigrations
{
    /// <inheritdoc />
    public partial class TicketDueDate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "DueDate",
                table: "Tickets",
                type: "timestamp with time zone",
                nullable: true);

            // A recurring task's ticket is due on its day (HANDOVER §165); every other ticket starts with none.
            migrationBuilder.Sql(@"UPDATE ""Tickets"" SET ""DueDate"" = ""DueOn"" WHERE ""RecurringTaskId"" IS NOT NULL AND ""DueOn"" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DueDate",
                table: "Tickets");
        }
    }
}
