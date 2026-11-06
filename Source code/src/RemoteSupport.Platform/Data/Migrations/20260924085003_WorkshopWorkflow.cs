using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RemoteSupport.Platform.Data.Migrations
{
    /// <inheritdoc />
    public partial class WorkshopWorkflow : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AbandonedReason",
                table: "Tickets",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "CollectedBy",
                table: "Tickets",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<DateTime>(
                name: "CollectedUtc",
                table: "Tickets",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DoNotInvoiceReason",
                table: "Tickets",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<decimal>(
                name: "InvoiceAmount",
                table: "Tickets",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "InvoiceNumber",
                table: "Tickets",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<int>(
                name: "Lane",
                table: "Tickets",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "Outcome",
                table: "Tickets",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<decimal>(
                name: "QuoteAmount",
                table: "Tickets",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "QuoteReference",
                table: "Tickets",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "QuoteSupplierEmail",
                table: "Tickets",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<bool>(
                name: "Repairable",
                table: "Tickets",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Stage",
                table: "Tickets",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "StageReturn",
                table: "Tickets",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<int>(
                name: "TimeSpentMinutes",
                table: "Tickets",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "WorkedBy",
                table: "Tickets",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<bool>(
                name: "IsWorkDone",
                table: "TicketNotes",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "Stage",
                table: "TicketNotes",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateTable(
                name: "CatalogItems",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    TenantId = table.Column<int>(type: "INTEGER", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    MatchKey = table.Column<string>(type: "TEXT", nullable: false),
                    LastPartNumber = table.Column<string>(type: "TEXT", nullable: false),
                    MergedIntoId = table.Column<int>(type: "INTEGER", nullable: true),
                    CreatedBy = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CatalogItems", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "QuoteLines",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    TenantId = table.Column<int>(type: "INTEGER", nullable: false),
                    TicketId = table.Column<int>(type: "INTEGER", nullable: false),
                    ItemId = table.Column<int>(type: "INTEGER", nullable: false),
                    ItemName = table.Column<string>(type: "TEXT", nullable: false),
                    PartNumber = table.Column<string>(type: "TEXT", nullable: false),
                    Quantity = table.Column<int>(type: "INTEGER", nullable: false),
                    PhotoId = table.Column<long>(type: "INTEGER", nullable: true),
                    AddedBy = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_QuoteLines", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Tickets_TenantId_Lane",
                table: "Tickets",
                columns: new[] { "TenantId", "Lane" });

            migrationBuilder.CreateIndex(
                name: "IX_CatalogItems_TenantId_MatchKey",
                table: "CatalogItems",
                columns: new[] { "TenantId", "MatchKey" });

            migrationBuilder.CreateIndex(
                name: "IX_QuoteLines_TenantId_TicketId",
                table: "QuoteLines",
                columns: new[] { "TenantId", "TicketId" });

            // Workshop jobs already on file join the route where their status says they are —
            // Clint, answer 21. Open is Awaiting diagnosis; On hold is Waiting on customer (and
            // returns to the bench); In progress and Resolved are On the bench, since a resolved
            // job has not been through the decision yet and must be; Closed is closed.
            // Category 2 is Workshop; Status 0 Open, 2 On hold, 4 Closed; Lane 0 bench, 3 done.
            migrationBuilder.Sql("""
                UPDATE Tickets SET
                    Stage = CASE Status WHEN 0 THEN 'awaiting-diagnosis' WHEN 2 THEN 'waiting-customer' WHEN 4 THEN 'closed' ELSE 'on-bench' END,
                    StageReturn = CASE Status WHEN 2 THEN 'on-bench' ELSE '' END,
                    Lane = CASE Status WHEN 4 THEN 3 ELSE 0 END,
                    Status = CASE Status WHEN 3 THEN 1 ELSE Status END,
                    ClosedUtc = CASE Status WHEN 3 THEN NULL ELSE ClosedUtc END
                WHERE Category = 2 AND Stage = '';
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CatalogItems");

            migrationBuilder.DropTable(
                name: "QuoteLines");

            migrationBuilder.DropIndex(
                name: "IX_Tickets_TenantId_Lane",
                table: "Tickets");

            migrationBuilder.DropColumn(
                name: "AbandonedReason",
                table: "Tickets");

            migrationBuilder.DropColumn(
                name: "CollectedBy",
                table: "Tickets");

            migrationBuilder.DropColumn(
                name: "CollectedUtc",
                table: "Tickets");

            migrationBuilder.DropColumn(
                name: "DoNotInvoiceReason",
                table: "Tickets");

            migrationBuilder.DropColumn(
                name: "InvoiceAmount",
                table: "Tickets");

            migrationBuilder.DropColumn(
                name: "InvoiceNumber",
                table: "Tickets");

            migrationBuilder.DropColumn(
                name: "Lane",
                table: "Tickets");

            migrationBuilder.DropColumn(
                name: "Outcome",
                table: "Tickets");

            migrationBuilder.DropColumn(
                name: "QuoteAmount",
                table: "Tickets");

            migrationBuilder.DropColumn(
                name: "QuoteReference",
                table: "Tickets");

            migrationBuilder.DropColumn(
                name: "QuoteSupplierEmail",
                table: "Tickets");

            migrationBuilder.DropColumn(
                name: "Repairable",
                table: "Tickets");

            migrationBuilder.DropColumn(
                name: "Stage",
                table: "Tickets");

            migrationBuilder.DropColumn(
                name: "StageReturn",
                table: "Tickets");

            migrationBuilder.DropColumn(
                name: "TimeSpentMinutes",
                table: "Tickets");

            migrationBuilder.DropColumn(
                name: "WorkedBy",
                table: "Tickets");

            migrationBuilder.DropColumn(
                name: "IsWorkDone",
                table: "TicketNotes");

            migrationBuilder.DropColumn(
                name: "Stage",
                table: "TicketNotes");
        }
    }
}
