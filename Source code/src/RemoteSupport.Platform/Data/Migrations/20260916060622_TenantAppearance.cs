using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RemoteSupport.Platform.Data.Migrations
{
    /// <inheritdoc />
    public partial class TenantAppearance : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ClientTheme",
                table: "Tenants",
                type: "TEXT",
                nullable: false,
                defaultValue: "light");   // every existing tenant keeps the look they have today

            migrationBuilder.AddColumn<string>(
                name: "TechnicianTheme",
                table: "Tenants",
                type: "TEXT",
                nullable: false,
                defaultValue: "light");   // every existing tenant keeps the look they have today
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ClientTheme",
                table: "Tenants");

            migrationBuilder.DropColumn(
                name: "TechnicianTheme",
                table: "Tenants");
        }
    }
}
