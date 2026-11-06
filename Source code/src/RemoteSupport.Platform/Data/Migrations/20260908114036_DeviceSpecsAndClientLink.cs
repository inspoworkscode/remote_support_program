using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RemoteSupport.Platform.Data.Migrations
{
    /// <inheritdoc />
    public partial class DeviceSpecsAndClientLink : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ClientId",
                table: "Devices",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Cores",
                table: "Devices",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "Cpu",
                table: "Devices",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<int>(
                name: "DiskFreeGb",
                table: "Devices",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "DiskTotalGb",
                table: "Devices",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "RamMb",
                table: "Devices",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateIndex(
                name: "IX_Devices_ClientId",
                table: "Devices",
                column: "ClientId");

            migrationBuilder.AddForeignKey(
                name: "FK_Devices_Clients_ClientId",
                table: "Devices",
                column: "ClientId",
                principalTable: "Clients",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Devices_Clients_ClientId",
                table: "Devices");

            migrationBuilder.DropIndex(
                name: "IX_Devices_ClientId",
                table: "Devices");

            migrationBuilder.DropColumn(
                name: "ClientId",
                table: "Devices");

            migrationBuilder.DropColumn(
                name: "Cores",
                table: "Devices");

            migrationBuilder.DropColumn(
                name: "Cpu",
                table: "Devices");

            migrationBuilder.DropColumn(
                name: "DiskFreeGb",
                table: "Devices");

            migrationBuilder.DropColumn(
                name: "DiskTotalGb",
                table: "Devices");

            migrationBuilder.DropColumn(
                name: "RamMb",
                table: "Devices");
        }
    }
}
