using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RemoteSupport.Platform.Data.Migrations
{
    /// <inheritdoc />
    public partial class QuoteSuppliers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "QuoteMachineMake",
                table: "Tickets",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "QuoteMachineModel",
                table: "Tickets",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<bool>(
                name: "QuoteMachineSaved",
                table: "Tickets",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "QuoteMachineSerial",
                table: "Tickets",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "QuoteMachineType",
                table: "Tickets",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "QuoteSupplierNames",
                table: "Tickets",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "QuoteDeliveryAddress",
                table: "Tenants",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "QuoteToAddress",
                table: "Tenants",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateTable(
                name: "Suppliers",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    TenantId = table.Column<int>(type: "INTEGER", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    MatchKey = table.Column<string>(type: "TEXT", nullable: false),
                    Email = table.Column<string>(type: "TEXT", nullable: false),
                    ContactPerson = table.Column<string>(type: "TEXT", nullable: false),
                    Phone = table.Column<string>(type: "TEXT", nullable: false),
                    RemovedUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CreatedBy = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Suppliers", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Suppliers_TenantId_MatchKey",
                table: "Suppliers",
                columns: new[] { "TenantId", "MatchKey" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Suppliers");

            migrationBuilder.DropColumn(
                name: "QuoteMachineMake",
                table: "Tickets");

            migrationBuilder.DropColumn(
                name: "QuoteMachineModel",
                table: "Tickets");

            migrationBuilder.DropColumn(
                name: "QuoteMachineSaved",
                table: "Tickets");

            migrationBuilder.DropColumn(
                name: "QuoteMachineSerial",
                table: "Tickets");

            migrationBuilder.DropColumn(
                name: "QuoteMachineType",
                table: "Tickets");

            migrationBuilder.DropColumn(
                name: "QuoteSupplierNames",
                table: "Tickets");

            migrationBuilder.DropColumn(
                name: "QuoteDeliveryAddress",
                table: "Tenants");

            migrationBuilder.DropColumn(
                name: "QuoteToAddress",
                table: "Tenants");
        }
    }
}
