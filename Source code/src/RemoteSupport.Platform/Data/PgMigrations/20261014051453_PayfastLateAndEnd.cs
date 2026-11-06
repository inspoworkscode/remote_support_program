using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RemoteSupport.Platform.Data.PgMigrations
{
    /// <inheritdoc />
    public partial class PayfastLateAndEnd : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "EndedUtc",
                table: "PayfastSubscriptions",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "EndsOn",
                table: "PayfastSubscriptions",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "LateDue",
                table: "PayfastSubscriptions",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<int>(
                name: "LateNotices",
                table: "PayfastSubscriptions",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTime>(
                name: "SuspendedUtc",
                table: "PayfastSubscriptions",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "EndedUtc",
                table: "PayfastSubscriptions");

            migrationBuilder.DropColumn(
                name: "EndsOn",
                table: "PayfastSubscriptions");

            migrationBuilder.DropColumn(
                name: "LateDue",
                table: "PayfastSubscriptions");

            migrationBuilder.DropColumn(
                name: "LateNotices",
                table: "PayfastSubscriptions");

            migrationBuilder.DropColumn(
                name: "SuspendedUtc",
                table: "PayfastSubscriptions");
        }
    }
}
