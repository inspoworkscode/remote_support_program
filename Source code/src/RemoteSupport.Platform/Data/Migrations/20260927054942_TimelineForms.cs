using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RemoteSupport.Platform.Data.Migrations
{
    /// <inheritdoc />
    public partial class TimelineForms : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "FormTitle",
                table: "TicketNotes",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "MovedTo",
                table: "TicketNotes",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            // "On the bench" was retired on 2026-09-27 for Inspection findings → Repairable / Not
            // repairable → Work done. A job already there has been looked at, so it lands on
            // Inspected, where the next question is Repairable or not. Anything waiting on the
            // customer comes back there too.
            migrationBuilder.Sql("UPDATE Tickets SET Stage = 'inspected' WHERE Stage = 'on-bench';");
            migrationBuilder.Sql("UPDATE Tickets SET StageReturn = 'inspected' WHERE StageReturn = 'on-bench';");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "FormTitle",
                table: "TicketNotes");

            migrationBuilder.DropColumn(
                name: "MovedTo",
                table: "TicketNotes");
        }
    }
}
