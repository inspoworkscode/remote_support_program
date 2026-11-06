using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RemoteSupport.Platform.Data.Migrations
{
    /// <inheritdoc />
    public partial class StaffChat : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "StaffChatReads",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    TenantId = table.Column<int>(type: "INTEGER", nullable: false),
                    Person = table.Column<string>(type: "TEXT", nullable: false),
                    With = table.Column<string>(type: "TEXT", nullable: false),
                    LastReadId = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StaffChatReads", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "StaffMessages",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    TenantId = table.Column<int>(type: "INTEGER", nullable: false),
                    From = table.Column<string>(type: "TEXT", nullable: false),
                    To = table.Column<string>(type: "TEXT", nullable: false),
                    Text = table.Column<string>(type: "TEXT", nullable: false),
                    SentUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StaffMessages", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_StaffChatReads_TenantId_Person_With",
                table: "StaffChatReads",
                columns: new[] { "TenantId", "Person", "With" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_StaffMessages_TenantId_From_Id",
                table: "StaffMessages",
                columns: new[] { "TenantId", "From", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_StaffMessages_TenantId_To_Id",
                table: "StaffMessages",
                columns: new[] { "TenantId", "To", "Id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "StaffChatReads");

            migrationBuilder.DropTable(
                name: "StaffMessages");
        }
    }
}
