using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RemoteSupport.Platform.Data.Migrations
{
    /// <inheritdoc />
    public partial class TwoFactorSignIn : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "RecoveryCodeHashes",
                table: "Technicians",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "SecurityStamp",
                table: "Technicians",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "SignInPlaces",
                table: "Technicians",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<DateTime>(
                name: "TotpEnabledUtc",
                table: "Technicians",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "TotpLastStep",
                table: "Technicians",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<string>(
                name: "TotpSecretEnc",
                table: "Technicians",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<long>(
                name: "OwnerTotpLastStep",
                table: "PlatformSettings",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<string>(
                name: "OwnerTotpSecretEnc",
                table: "PlatformSettings",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            // Everybody gets a stamp of their own now. Every cookie issued before today carries none,
            // so this signs every browser out once — and the next sign-in is the one that asks them
            // to set up their authenticator. Without it, a cookie from last week would go on working
            // on the password alone for as long as it kept being used.
            migrationBuilder.Sql("UPDATE Technicians SET SecurityStamp = lower(hex(randomblob(16)));");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "RecoveryCodeHashes",
                table: "Technicians");

            migrationBuilder.DropColumn(
                name: "SecurityStamp",
                table: "Technicians");

            migrationBuilder.DropColumn(
                name: "SignInPlaces",
                table: "Technicians");

            migrationBuilder.DropColumn(
                name: "TotpEnabledUtc",
                table: "Technicians");

            migrationBuilder.DropColumn(
                name: "TotpLastStep",
                table: "Technicians");

            migrationBuilder.DropColumn(
                name: "TotpSecretEnc",
                table: "Technicians");

            migrationBuilder.DropColumn(
                name: "OwnerTotpLastStep",
                table: "PlatformSettings");

            migrationBuilder.DropColumn(
                name: "OwnerTotpSecretEnc",
                table: "PlatformSettings");
        }
    }
}
