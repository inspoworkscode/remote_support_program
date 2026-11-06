using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RemoteSupport.Platform.Data.Migrations
{
    /// <inheritdoc />
    public partial class FreeVersionRemovals : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "DeviceRemovals",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    TenantId = table.Column<int>(type: "INTEGER", nullable: false),
                    DeviceId = table.Column<string>(type: "TEXT", nullable: false),
                    MachineName = table.Column<string>(type: "TEXT", nullable: false),
                    RemovedBy = table.Column<string>(type: "TEXT", nullable: false),
                    RemovedUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DeviceRemovals", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DeviceRemovals_TenantId_DeviceId",
                table: "DeviceRemovals",
                columns: new[] { "TenantId", "DeviceId" });

            migrationBuilder.CreateIndex(
                name: "IX_DeviceRemovals_TenantId_RemovedUtc",
                table: "DeviceRemovals",
                columns: new[] { "TenantId", "RemovedUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DeviceRemovals");
        }
    }
}
