using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RemoteSupport.Platform.Data.Migrations
{
    /// <inheritdoc />
    public partial class AutoUpdate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "SelfUpdates",
                table: "Devices",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "DeviceUpdateEvents",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    TenantId = table.Column<int>(type: "INTEGER", nullable: false),
                    DeviceId = table.Column<string>(type: "TEXT", nullable: false),
                    MachineName = table.Column<string>(type: "TEXT", nullable: false),
                    Version = table.Column<string>(type: "TEXT", nullable: false),
                    Step = table.Column<string>(type: "TEXT", nullable: false),
                    Detail = table.Column<string>(type: "TEXT", nullable: false),
                    AtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DeviceUpdateEvents", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "UpdateRollouts",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Version = table.Column<string>(type: "TEXT", nullable: false),
                    Ring = table.Column<int>(type: "INTEGER", nullable: false),
                    Paused = table.Column<bool>(type: "INTEGER", nullable: false),
                    PausedWhy = table.Column<string>(type: "TEXT", nullable: false),
                    StartedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ChangedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ChangedBy = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UpdateRollouts", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DeviceUpdateEvents_TenantId_DeviceId_AtUtc",
                table: "DeviceUpdateEvents",
                columns: new[] { "TenantId", "DeviceId", "AtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_DeviceUpdateEvents_Version_Step",
                table: "DeviceUpdateEvents",
                columns: new[] { "Version", "Step" });

            migrationBuilder.CreateIndex(
                name: "IX_UpdateRollouts_Version",
                table: "UpdateRollouts",
                column: "Version",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DeviceUpdateEvents");

            migrationBuilder.DropTable(
                name: "UpdateRollouts");

            migrationBuilder.DropColumn(
                name: "SelfUpdates",
                table: "Devices");
        }
    }
}
