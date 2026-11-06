using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RemoteSupport.Platform.Data.Migrations
{
    /// <inheritdoc />
    public partial class DeviceHardwareIdentity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "HardwareId",
                table: "Devices",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<DateTime>(
                name: "LastReinstallUtc",
                table: "Devices",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ReinstallCount",
                table: "Devices",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateIndex(
                name: "IX_Devices_TenantId_HardwareId",
                table: "Devices",
                columns: new[] { "TenantId", "HardwareId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Devices_TenantId_HardwareId",
                table: "Devices");

            migrationBuilder.DropColumn(
                name: "HardwareId",
                table: "Devices");

            migrationBuilder.DropColumn(
                name: "LastReinstallUtc",
                table: "Devices");

            migrationBuilder.DropColumn(
                name: "ReinstallCount",
                table: "Devices");
        }
    }
}
