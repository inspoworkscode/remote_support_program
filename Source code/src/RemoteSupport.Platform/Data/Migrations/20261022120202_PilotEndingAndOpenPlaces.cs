using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RemoteSupport.Platform.Data.Migrations
{
    /// <inheritdoc />
    public partial class PilotEndingAndOpenPlaces : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "Full",
                table: "WaitingList",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTime>(
                name: "PlaceExpiresUtc",
                table: "WaitingList",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "PlaceOfferedUtc",
                table: "WaitingList",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PlaceTokenHash",
                table: "WaitingList",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<DateTime>(
                name: "PlaceUsedUtc",
                table: "WaitingList",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "TenantId",
                table: "WaitingList",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PilotAfter",
                table: "Tenants",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<DateTime>(
                name: "PilotEndedUtc",
                table: "Tenants",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "PilotEndsUtc",
                table: "Tenants",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "OpenPlaces",
                table: "PlatformSettings",
                type: "INTEGER",
                nullable: false,
                defaultValue: 5); // Five companies at a time (2026-10-22, OpenPlaces) — 0 would mean no limit on every server already running.
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Full",
                table: "WaitingList");

            migrationBuilder.DropColumn(
                name: "PlaceExpiresUtc",
                table: "WaitingList");

            migrationBuilder.DropColumn(
                name: "PlaceOfferedUtc",
                table: "WaitingList");

            migrationBuilder.DropColumn(
                name: "PlaceTokenHash",
                table: "WaitingList");

            migrationBuilder.DropColumn(
                name: "PlaceUsedUtc",
                table: "WaitingList");

            migrationBuilder.DropColumn(
                name: "TenantId",
                table: "WaitingList");

            migrationBuilder.DropColumn(
                name: "PilotAfter",
                table: "Tenants");

            migrationBuilder.DropColumn(
                name: "PilotEndedUtc",
                table: "Tenants");

            migrationBuilder.DropColumn(
                name: "PilotEndsUtc",
                table: "Tenants");

            migrationBuilder.DropColumn(
                name: "OpenPlaces",
                table: "PlatformSettings");
        }
    }
}
