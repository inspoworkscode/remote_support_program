using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RemoteSupport.Platform.Data.Migrations
{
    /// <inheritdoc />
    public partial class MicrosoftSignIn : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "MsConnectedBy",
                table: "TicketIntake",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<DateTime>(
                name: "MsConnectedUtc",
                table: "TicketIntake",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MsRefreshTokenEnc",
                table: "TicketIntake",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "MsTenant",
                table: "TicketIntake",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "MsUser",
                table: "TicketIntake",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "MsClientId",
                table: "PlatformSettings",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "MsClientSecretEnc",
                table: "PlatformSettings",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<DateTime>(
                name: "MsSecretExpiresUtc",
                table: "PlatformSettings",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "MsUpdatedUtc",
                table: "PlatformSettings",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "MsConnectedBy",
                table: "TicketIntake");

            migrationBuilder.DropColumn(
                name: "MsConnectedUtc",
                table: "TicketIntake");

            migrationBuilder.DropColumn(
                name: "MsRefreshTokenEnc",
                table: "TicketIntake");

            migrationBuilder.DropColumn(
                name: "MsTenant",
                table: "TicketIntake");

            migrationBuilder.DropColumn(
                name: "MsUser",
                table: "TicketIntake");

            migrationBuilder.DropColumn(
                name: "MsClientId",
                table: "PlatformSettings");

            migrationBuilder.DropColumn(
                name: "MsClientSecretEnc",
                table: "PlatformSettings");

            migrationBuilder.DropColumn(
                name: "MsSecretExpiresUtc",
                table: "PlatformSettings");

            migrationBuilder.DropColumn(
                name: "MsUpdatedUtc",
                table: "PlatformSettings");
        }
    }
}
