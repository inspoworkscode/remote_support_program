using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RemoteSupport.Platform.Data.Migrations
{
    /// <inheritdoc />
    public partial class ClientsAndTicketCategories : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Accessories",
                table: "Tickets",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<DateTime>(
                name: "AppointmentUtc",
                table: "Tickets",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AssetMake",
                table: "Tickets",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "AssetModel",
                table: "Tickets",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "AssetSerial",
                table: "Tickets",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<int>(
                name: "Category",
                table: "Tickets",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "ClientId",
                table: "Tickets",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DevicePasswordEnc",
                table: "Tickets",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<bool>(
                name: "DevicePasswordProtected",
                table: "Tickets",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "SiteAddress",
                table: "Tickets",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateTable(
                name: "Clients",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    TenantId = table.Column<int>(type: "INTEGER", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    ContactName = table.Column<string>(type: "TEXT", nullable: false),
                    Phone = table.Column<string>(type: "TEXT", nullable: false),
                    Email = table.Column<string>(type: "TEXT", nullable: false),
                    Address = table.Column<string>(type: "TEXT", nullable: false),
                    Notes = table.Column<string>(type: "TEXT", nullable: false),
                    Archived = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Clients", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Tickets_ClientId",
                table: "Tickets",
                column: "ClientId");

            migrationBuilder.CreateIndex(
                name: "IX_Tickets_TenantId_Category",
                table: "Tickets",
                columns: new[] { "TenantId", "Category" });

            migrationBuilder.CreateIndex(
                name: "IX_Tickets_TenantId_ClientId",
                table: "Tickets",
                columns: new[] { "TenantId", "ClientId" });

            migrationBuilder.CreateIndex(
                name: "IX_Clients_TenantId_Name",
                table: "Clients",
                columns: new[] { "TenantId", "Name" });

            migrationBuilder.AddForeignKey(
                name: "FK_Tickets_Clients_ClientId",
                table: "Tickets",
                column: "ClientId",
                principalTable: "Clients",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Tickets_Clients_ClientId",
                table: "Tickets");

            migrationBuilder.DropTable(
                name: "Clients");

            migrationBuilder.DropIndex(
                name: "IX_Tickets_ClientId",
                table: "Tickets");

            migrationBuilder.DropIndex(
                name: "IX_Tickets_TenantId_Category",
                table: "Tickets");

            migrationBuilder.DropIndex(
                name: "IX_Tickets_TenantId_ClientId",
                table: "Tickets");

            migrationBuilder.DropColumn(
                name: "Accessories",
                table: "Tickets");

            migrationBuilder.DropColumn(
                name: "AppointmentUtc",
                table: "Tickets");

            migrationBuilder.DropColumn(
                name: "AssetMake",
                table: "Tickets");

            migrationBuilder.DropColumn(
                name: "AssetModel",
                table: "Tickets");

            migrationBuilder.DropColumn(
                name: "AssetSerial",
                table: "Tickets");

            migrationBuilder.DropColumn(
                name: "Category",
                table: "Tickets");

            migrationBuilder.DropColumn(
                name: "ClientId",
                table: "Tickets");

            migrationBuilder.DropColumn(
                name: "DevicePasswordEnc",
                table: "Tickets");

            migrationBuilder.DropColumn(
                name: "DevicePasswordProtected",
                table: "Tickets");

            migrationBuilder.DropColumn(
                name: "SiteAddress",
                table: "Tickets");
        }
    }
}
