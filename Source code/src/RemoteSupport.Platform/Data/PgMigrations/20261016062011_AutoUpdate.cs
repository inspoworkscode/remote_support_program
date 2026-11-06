using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace RemoteSupport.Platform.Data.PgMigrations
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
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "DeviceUpdateEvents",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    TenantId = table.Column<int>(type: "integer", nullable: false),
                    DeviceId = table.Column<string>(type: "text", nullable: false),
                    MachineName = table.Column<string>(type: "text", nullable: false),
                    Version = table.Column<string>(type: "text", nullable: false),
                    Step = table.Column<string>(type: "text", nullable: false),
                    Detail = table.Column<string>(type: "text", nullable: false),
                    AtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DeviceUpdateEvents", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "UpdateRollouts",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Version = table.Column<string>(type: "text", nullable: false),
                    Ring = table.Column<int>(type: "integer", nullable: false),
                    Paused = table.Column<bool>(type: "boolean", nullable: false),
                    PausedWhy = table.Column<string>(type: "text", nullable: false),
                    StartedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ChangedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ChangedBy = table.Column<string>(type: "text", nullable: false)
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
