using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RemoteSupport.Platform.Data.Migrations
{
    /// <inheritdoc />
    public partial class OutsideHelp : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "OutsidersAllowed",
                table: "Tenants",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "OutsiderMode",
                table: "Devices",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "OutsiderCodes",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    TenantId = table.Column<int>(type: "INTEGER", nullable: false),
                    DeviceId = table.Column<string>(type: "TEXT", nullable: false),
                    CodeHash = table.Column<string>(type: "TEXT", nullable: false),
                    ForWhom = table.Column<string>(type: "TEXT", nullable: false),
                    MadeBy = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ExpiresUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UsedUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    UsedBy = table.Column<string>(type: "TEXT", nullable: false),
                    EndedUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    EndedWhy = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OutsiderCodes", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_OutsiderCodes_CodeHash",
                table: "OutsiderCodes",
                column: "CodeHash");

            migrationBuilder.CreateIndex(
                name: "IX_OutsiderCodes_TenantId_DeviceId_CreatedUtc",
                table: "OutsiderCodes",
                columns: new[] { "TenantId", "DeviceId", "CreatedUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "OutsiderCodes");

            migrationBuilder.DropColumn(
                name: "OutsidersAllowed",
                table: "Tenants");

            migrationBuilder.DropColumn(
                name: "OutsiderMode",
                table: "Devices");
        }
    }
}
