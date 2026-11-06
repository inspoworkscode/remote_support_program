using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RemoteSupport.Platform.Data.Migrations
{
    /// <inheritdoc />
    public partial class TechnicianOutsideHelp : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "TechnicianInvitesAllowed",
                table: "Tenants",
                type: "INTEGER",
                nullable: false,
                defaultValue: true);   // on for every company already here (§155): the default the model gives a new one

            migrationBuilder.AddColumn<int>(
                name: "InvitedById",
                table: "OutsiderCodes",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "SessionId",
                table: "OutsiderCodes",
                type: "TEXT",
                nullable: false,
                defaultValue: "");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "TechnicianInvitesAllowed",
                table: "Tenants");

            migrationBuilder.DropColumn(
                name: "InvitedById",
                table: "OutsiderCodes");

            migrationBuilder.DropColumn(
                name: "SessionId",
                table: "OutsiderCodes");
        }
    }
}
