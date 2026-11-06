using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RemoteSupport.Platform.Data.Migrations
{
    /// <inheritdoc />
    public partial class MoreSeatsOnRequest : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "MostSeats",
                table: "Tenants",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "SeatsAskedBy",
                table: "Tenants",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<int>(
                name: "SeatsAskedFor",
                table: "Tenants",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "SeatsAskedNote",
                table: "Tenants",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<DateTime>(
                name: "SeatsAskedUtc",
                table: "Tenants",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "MostSeats",
                table: "Tenants");

            migrationBuilder.DropColumn(
                name: "SeatsAskedBy",
                table: "Tenants");

            migrationBuilder.DropColumn(
                name: "SeatsAskedFor",
                table: "Tenants");

            migrationBuilder.DropColumn(
                name: "SeatsAskedNote",
                table: "Tenants");

            migrationBuilder.DropColumn(
                name: "SeatsAskedUtc",
                table: "Tenants");
        }
    }
}
