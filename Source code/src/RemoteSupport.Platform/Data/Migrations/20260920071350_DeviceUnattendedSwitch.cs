using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RemoteSupport.Platform.Data.Migrations
{
    /// <inheritdoc />
    public partial class DeviceUnattendedSwitch : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "UnattendedChangedBy",
                table: "Devices",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<DateTime>(
                name: "UnattendedChangedUtc",
                table: "Devices",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "UnattendedEnabled",
                table: "Devices",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "UnattendedChangedBy",
                table: "Devices");

            migrationBuilder.DropColumn(
                name: "UnattendedChangedUtc",
                table: "Devices");

            migrationBuilder.DropColumn(
                name: "UnattendedEnabled",
                table: "Devices");
        }
    }
}
