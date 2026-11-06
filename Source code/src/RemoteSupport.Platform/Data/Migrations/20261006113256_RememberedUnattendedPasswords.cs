using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RemoteSupport.Platform.Data.Migrations
{
    /// <inheritdoc />
    public partial class RememberedUnattendedPasswords : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SavedUnattendedPasswords",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    TenantId = table.Column<int>(type: "INTEGER", nullable: false),
                    DeviceRowId = table.Column<int>(type: "INTEGER", nullable: false),
                    TechnicianId = table.Column<int>(type: "INTEGER", nullable: false),
                    PasswordSalt = table.Column<string>(type: "TEXT", nullable: false),
                    SavedUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SavedUnattendedPasswords", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SavedUnattendedPasswords_Devices_DeviceRowId",
                        column: x => x.DeviceRowId,
                        principalTable: "Devices",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_SavedUnattendedPasswords_Technicians_TechnicianId",
                        column: x => x.TechnicianId,
                        principalTable: "Technicians",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SavedUnattendedPasswords_DeviceRowId_TechnicianId",
                table: "SavedUnattendedPasswords",
                columns: new[] { "DeviceRowId", "TechnicianId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SavedUnattendedPasswords_TechnicianId",
                table: "SavedUnattendedPasswords",
                column: "TechnicianId");

            migrationBuilder.CreateIndex(
                name: "IX_SavedUnattendedPasswords_TenantId",
                table: "SavedUnattendedPasswords",
                column: "TenantId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SavedUnattendedPasswords");
        }
    }
}
