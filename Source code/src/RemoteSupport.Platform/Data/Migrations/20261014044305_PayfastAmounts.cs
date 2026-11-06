using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RemoteSupport.Platform.Data.Migrations
{
    /// <inheritdoc />
    public partial class PayfastAmounts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "AmountChangedUtc",
                table: "PayfastSubscriptions",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AmountProblem",
                table: "PayfastSubscriptions",
                type: "TEXT",
                nullable: false,
                defaultValue: "");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AmountChangedUtc",
                table: "PayfastSubscriptions");

            migrationBuilder.DropColumn(
                name: "AmountProblem",
                table: "PayfastSubscriptions");
        }
    }
}
