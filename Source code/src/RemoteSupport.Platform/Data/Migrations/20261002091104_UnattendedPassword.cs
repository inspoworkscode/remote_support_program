using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RemoteSupport.Platform.Data.Migrations
{
    /// <inheritdoc />
    public partial class UnattendedPassword : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "UnattendedPasswordChangedBy",
                table: "Devices",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<DateTime>(
                name: "UnattendedPasswordChangedUtc",
                table: "Devices",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "UnattendedPasswordHash",
                table: "Devices",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "UnattendedPasswordSalt",
                table: "Devices",
                type: "TEXT",
                nullable: false,
                defaultValue: "");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "UnattendedPasswordChangedBy",
                table: "Devices");

            migrationBuilder.DropColumn(
                name: "UnattendedPasswordChangedUtc",
                table: "Devices");

            migrationBuilder.DropColumn(
                name: "UnattendedPasswordHash",
                table: "Devices");

            migrationBuilder.DropColumn(
                name: "UnattendedPasswordSalt",
                table: "Devices");
        }
    }
}
