using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace RemoteSupport.Platform.Data.PgMigrations
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
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    AtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Who = table.Column<string>(type: "text", nullable: false),
                    What = table.Column<string>(type: "text", nullable: false),
                    Company = table.Column<string>(type: "text", nullable: false),
                    Server = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AdminLog", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PlatformAdmins",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Name = table.Column<string>(type: "text", nullable: false),
                    Email = table.Column<string>(type: "text", nullable: false),
                    Level = table.Column<int>(type: "integer", nullable: false),
                    PasswordSalt = table.Column<string>(type: "text", nullable: false),
                    PasswordHash = table.Column<string>(type: "text", nullable: false),
                    TotpSecretEnc = table.Column<string>(type: "text", nullable: false),
                    TotpLastStep = table.Column<long>(type: "bigint", nullable: false),
                    SecurityStamp = table.Column<string>(type: "text", nullable: false),
                    LinkHash = table.Column<string>(type: "text", nullable: false),
                    LinkExpiresUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedBy = table.Column<string>(type: "text", nullable: false),
                    LastSignInUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
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
