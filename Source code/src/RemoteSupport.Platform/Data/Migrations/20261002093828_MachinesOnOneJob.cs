using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RemoteSupport.Platform.Data.Migrations
{
    /// <inheritdoc />
    public partial class MachinesOnOneJob : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "MachineId",
                table: "TicketPhotos",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "TicketMachines",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    TenantId = table.Column<int>(type: "INTEGER", nullable: false),
                    TicketId = table.Column<int>(type: "INTEGER", nullable: false),
                    Purpose = table.Column<string>(type: "TEXT", nullable: false),
                    Make = table.Column<string>(type: "TEXT", nullable: false),
                    Model = table.Column<string>(type: "TEXT", nullable: false),
                    Serial = table.Column<string>(type: "TEXT", nullable: false),
                    Accessories = table.Column<string>(type: "TEXT", nullable: false),
                    PasswordProtected = table.Column<bool>(type: "INTEGER", nullable: false),
                    PasswordEnc = table.Column<string>(type: "TEXT", nullable: false),
                    AddedBy = table.Column<string>(type: "TEXT", nullable: false),
                    AddedUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TicketMachines", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TicketMachines_Tickets_TicketId",
                        column: x => x.TicketId,
                        principalTable: "Tickets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TicketMachines_TenantId_TicketId",
                table: "TicketMachines",
                columns: new[] { "TenantId", "TicketId" });

            migrationBuilder.CreateIndex(
                name: "IX_TicketMachines_TicketId",
                table: "TicketMachines",
                column: "TicketId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TicketMachines");

            migrationBuilder.DropColumn(
                name: "MachineId",
                table: "TicketPhotos");
        }
    }
}
