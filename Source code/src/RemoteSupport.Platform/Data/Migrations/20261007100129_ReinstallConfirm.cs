using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RemoteSupport.Platform.Data.Migrations
{
    /// <inheritdoc />
    public partial class ReinstallConfirm : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ReinstallConfirmedBy",
                table: "Devices",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<DateTime>(
                name: "ReinstallConfirmedUtc",
                table: "Devices",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "ReinstallUnconfirmed",
                table: "Devices",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ReinstallConfirmedBy",
                table: "Devices");

            migrationBuilder.DropColumn(
                name: "ReinstallConfirmedUtc",
                table: "Devices");

            migrationBuilder.DropColumn(
                name: "ReinstallUnconfirmed",
                table: "Devices");
        }
    }
}
