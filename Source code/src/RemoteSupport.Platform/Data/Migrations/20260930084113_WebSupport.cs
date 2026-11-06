using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RemoteSupport.Platform.Data.Migrations
{
    /// <inheritdoc />
    public partial class WebSupport : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "WebSupportEnabled",
                table: "Tenants",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "WebSupportHours",
                table: "Tenants",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "WebSupportTimeZone",
                table: "Tenants",
                type: "TEXT",
                nullable: false,
                defaultValue: "");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "WebSupportEnabled",
                table: "Tenants");

            migrationBuilder.DropColumn(
                name: "WebSupportHours",
                table: "Tenants");

            migrationBuilder.DropColumn(
                name: "WebSupportTimeZone",
                table: "Tenants");
        }
    }
}
