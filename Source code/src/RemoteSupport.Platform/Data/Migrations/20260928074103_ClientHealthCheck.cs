using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RemoteSupport.Platform.Data.Migrations
{
    /// <inheritdoc />
    public partial class ClientHealthCheck : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "HealthCheckInstalled",
                table: "Devices",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "HealthCheckMode",
                table: "Devices",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "HealthCheckNote",
                table: "Devices",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<DateTime>(
                name: "HealthCheckReportedUtc",
                table: "Devices",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "HealthCheck",
                table: "Clients",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "HealthCheckInstalled",
                table: "Devices");

            migrationBuilder.DropColumn(
                name: "HealthCheckMode",
                table: "Devices");

            migrationBuilder.DropColumn(
                name: "HealthCheckNote",
                table: "Devices");

            migrationBuilder.DropColumn(
                name: "HealthCheckReportedUtc",
                table: "Devices");

            migrationBuilder.DropColumn(
                name: "HealthCheck",
                table: "Clients");
        }
    }
}
