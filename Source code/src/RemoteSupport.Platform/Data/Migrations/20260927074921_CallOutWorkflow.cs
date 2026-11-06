using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RemoteSupport.Platform.Data.Migrations
{
    /// <inheritdoc />
    public partial class CallOutWorkflow : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "PaymentMethod",
                table: "Tickets",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "PaymentReference",
                table: "Tickets",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            // Call outs already on file join the route (Clint, 2026-09-27). Closed stays closed;
            // everything else starts at Call out booked, because none of them has had its Work done
            // written — a Resolved one included, as a Resolved workshop job was put back before its
            // decision. The status follows the stage, as it does for every workflow ticket.
            // Category 3 is Call out; Status 0 Open, 4 Closed; Lane 0 bench, 3 done.
            migrationBuilder.Sql("""
                UPDATE Tickets SET
                    Stage = CASE Status WHEN 4 THEN 'closed' ELSE 'callout-booked' END,
                    StageReturn = '',
                    Lane = CASE Status WHEN 4 THEN 3 ELSE 0 END,
                    ClosedUtc = CASE Status WHEN 4 THEN ClosedUtc ELSE NULL END,
                    Status = CASE Status WHEN 4 THEN 4 ELSE 0 END
                WHERE Category = 3 AND Stage = '';
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "PaymentMethod",
                table: "Tickets");

            migrationBuilder.DropColumn(
                name: "PaymentReference",
                table: "Tickets");
        }
    }
}
