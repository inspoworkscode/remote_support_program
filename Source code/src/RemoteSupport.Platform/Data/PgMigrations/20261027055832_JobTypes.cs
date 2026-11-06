using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RemoteSupport.Platform.Data.PgMigrations
{
    /// <inheritdoc />
    public partial class JobTypes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "RouteOff",
                table: "Tickets",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "JobTypesChosenBy",
                table: "Tenants",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<DateTime>(
                name: "JobTypesChosenUtc",
                table: "Tenants",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "JobTypesOff",
                table: "Tenants",
                type: "text",
                nullable: false,
                defaultValue: "");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "RouteOff",
                table: "Tickets");

            migrationBuilder.DropColumn(
                name: "JobTypesChosenBy",
                table: "Tenants");

            migrationBuilder.DropColumn(
                name: "JobTypesChosenUtc",
                table: "Tenants");

            migrationBuilder.DropColumn(
                name: "JobTypesOff",
                table: "Tenants");
        }
    }
}
