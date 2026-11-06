using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RemoteSupport.Platform.Data.Migrations
{
    /// <inheritdoc />
    public partial class RecurringTasks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "DueOn",
                table: "Tickets",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "RecurringTaskId",
                table: "Tickets",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "RecurringKinds",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    TenantId = table.Column<int>(type: "INTEGER", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    MatchKey = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedBy = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RecurringKinds", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "RecurringTasks",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    TenantId = table.Column<int>(type: "INTEGER", nullable: false),
                    Title = table.Column<string>(type: "TEXT", nullable: false),
                    Details = table.Column<string>(type: "TEXT", nullable: false),
                    Kind = table.Column<string>(type: "TEXT", nullable: false),
                    ClientId = table.Column<int>(type: "INTEGER", nullable: true),
                    AssignedTo = table.Column<string>(type: "TEXT", nullable: false),
                    Priority = table.Column<int>(type: "INTEGER", nullable: false),
                    Repeats = table.Column<int>(type: "INTEGER", nullable: false),
                    WeekdaysOnly = table.Column<bool>(type: "INTEGER", nullable: false),
                    Days = table.Column<int>(type: "INTEGER", nullable: false),
                    MonthDay = table.Column<int>(type: "INTEGER", nullable: false),
                    NextDue = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastOpenedFor = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CreatedBy = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CancelledUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CancelledBy = table.Column<string>(type: "TEXT", nullable: false),
                    CancelReason = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RecurringTasks", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Tickets_TenantId_RecurringTaskId_DueOn",
                table: "Tickets",
                columns: new[] { "TenantId", "RecurringTaskId", "DueOn" });

            migrationBuilder.CreateIndex(
                name: "IX_RecurringKinds_TenantId_MatchKey",
                table: "RecurringKinds",
                columns: new[] { "TenantId", "MatchKey" });

            migrationBuilder.CreateIndex(
                name: "IX_RecurringTasks_TenantId_CancelledUtc_NextDue",
                table: "RecurringTasks",
                columns: new[] { "TenantId", "CancelledUtc", "NextDue" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "RecurringKinds");

            migrationBuilder.DropTable(
                name: "RecurringTasks");

            migrationBuilder.DropIndex(
                name: "IX_Tickets_TenantId_RecurringTaskId_DueOn",
                table: "Tickets");

            migrationBuilder.DropColumn(
                name: "DueOn",
                table: "Tickets");

            migrationBuilder.DropColumn(
                name: "RecurringTaskId",
                table: "Tickets");
        }
    }
}
