using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RemoteSupport.Platform.Data.Migrations
{
    /// <inheritdoc />
    public partial class TrialNoticesAndPlatformMail : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "LastTrialNoticeUtc",
                table: "Tenants",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TrialNoticesSent",
                table: "Tenants",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateTable(
                name: "PlatformSettings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    SmtpHost = table.Column<string>(type: "TEXT", nullable: false),
                    SmtpPort = table.Column<int>(type: "INTEGER", nullable: false),
                    SmtpUser = table.Column<string>(type: "TEXT", nullable: false),
                    SmtpPasswordEnc = table.Column<string>(type: "TEXT", nullable: false),
                    SmtpFrom = table.Column<string>(type: "TEXT", nullable: false),
                    SmtpFromName = table.Column<string>(type: "TEXT", nullable: false),
                    SmtpUseSsl = table.Column<bool>(type: "INTEGER", nullable: false),
                    NoticeCopyTo = table.Column<string>(type: "TEXT", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PlatformSettings", x => x.Id);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PlatformSettings");

            migrationBuilder.DropColumn(
                name: "LastTrialNoticeUtc",
                table: "Tenants");

            migrationBuilder.DropColumn(
                name: "TrialNoticesSent",
                table: "Tenants");
        }
    }
}
