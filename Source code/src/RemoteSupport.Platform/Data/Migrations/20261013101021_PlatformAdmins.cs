using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RemoteSupport.Platform.Data.Migrations
{
    /// <inheritdoc />
    public partial class PlatformAdmins : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AdminLog",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    AtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Who = table.Column<string>(type: "TEXT", nullable: false),
                    What = table.Column<string>(type: "TEXT", nullable: false),
                    Company = table.Column<string>(type: "TEXT", nullable: false),
                    Server = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AdminLog", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PlatformAdmins",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    Email = table.Column<string>(type: "TEXT", nullable: false),
                    Level = table.Column<int>(type: "INTEGER", nullable: false),
                    PasswordSalt = table.Column<string>(type: "TEXT", nullable: false),
                    PasswordHash = table.Column<string>(type: "TEXT", nullable: false),
                    TotpSecretEnc = table.Column<string>(type: "TEXT", nullable: false),
                    TotpLastStep = table.Column<long>(type: "INTEGER", nullable: false),
                    SecurityStamp = table.Column<string>(type: "TEXT", nullable: false),
                    LinkHash = table.Column<string>(type: "TEXT", nullable: false),
                    LinkExpiresUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CreatedBy = table.Column<string>(type: "TEXT", nullable: false),
                    LastSignInUtc = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PlatformAdmins", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AdminLog_AtUtc",
                table: "AdminLog",
                column: "AtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_PlatformAdmins_Email",
                table: "PlatformAdmins",
                column: "Email",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PlatformAdmins_LinkHash",
                table: "PlatformAdmins",
                column: "LinkHash");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AdminLog");

            migrationBuilder.DropTable(
                name: "PlatformAdmins");
        }
    }
}
