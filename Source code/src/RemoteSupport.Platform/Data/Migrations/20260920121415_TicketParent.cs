using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RemoteSupport.Platform.Data.Migrations
{
    /// <inheritdoc />
    public partial class TicketParent : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ParentTicketId",
                table: "Tickets",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Tickets_ParentTicketId",
                table: "Tickets",
                column: "ParentTicketId");

            migrationBuilder.CreateIndex(
                name: "IX_Tickets_TenantId_DeviceId_CreatedUtc",
                table: "Tickets",
                columns: new[] { "TenantId", "DeviceId", "CreatedUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_Tickets_TenantId_ParentTicketId",
                table: "Tickets",
                columns: new[] { "TenantId", "ParentTicketId" });

            migrationBuilder.AddForeignKey(
                name: "FK_Tickets_Tickets_ParentTicketId",
                table: "Tickets",
                column: "ParentTicketId",
                principalTable: "Tickets",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Tickets_Tickets_ParentTicketId",
                table: "Tickets");

            migrationBuilder.DropIndex(
                name: "IX_Tickets_ParentTicketId",
                table: "Tickets");

            migrationBuilder.DropIndex(
                name: "IX_Tickets_TenantId_DeviceId_CreatedUtc",
                table: "Tickets");

            migrationBuilder.DropIndex(
                name: "IX_Tickets_TenantId_ParentTicketId",
                table: "Tickets");

            migrationBuilder.DropColumn(
                name: "ParentTicketId",
                table: "Tickets");
        }
    }
}
