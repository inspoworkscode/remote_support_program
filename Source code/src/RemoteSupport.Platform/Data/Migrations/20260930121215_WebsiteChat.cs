using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RemoteSupport.Platform.Data.Migrations
{
    /// <inheritdoc />
    public partial class WebsiteChat : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "WebChatEnabled",
                table: "Tenants",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "WebChatHours",
                table: "Tenants",
                type: "TEXT",
                nullable: false,
                defaultValue: "08:00-17:00;08:00-17:00;08:00-17:00;08:00-17:00;08:00-17:00;;");   // Monday to Friday, as a new company gets

            migrationBuilder.AddColumn<string>(
                name: "WebChatTimeZone",
                table: "Tenants",
                type: "TEXT",
                nullable: false,
                defaultValue: "Africa/Johannesburg");

            migrationBuilder.CreateTable(
                name: "WebChatLines",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    TenantId = table.Column<int>(type: "INTEGER", nullable: false),
                    WebChatId = table.Column<int>(type: "INTEGER", nullable: false),
                    FromVisitor = table.Column<bool>(type: "INTEGER", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    Text = table.Column<string>(type: "TEXT", nullable: false),
                    SentUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WebChatLines", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "WebChats",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    TenantId = table.Column<int>(type: "INTEGER", nullable: false),
                    TokenHash = table.Column<string>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    Email = table.Column<string>(type: "TEXT", nullable: false),
                    Phone = table.Column<string>(type: "TEXT", nullable: false),
                    Page = table.Column<string>(type: "TEXT", nullable: false),
                    Ip = table.Column<string>(type: "TEXT", nullable: false),
                    ClientId = table.Column<int>(type: "INTEGER", nullable: true),
                    ContactId = table.Column<int>(type: "INTEGER", nullable: true),
                    AnsweredBy = table.Column<string>(type: "TEXT", nullable: false),
                    StartedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    EndedUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    EndedBy = table.Column<string>(type: "TEXT", nullable: false),
                    LeftMessage = table.Column<bool>(type: "INTEGER", nullable: false),
                    TicketId = table.Column<int>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WebChats", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_WebChatLines_WebChatId_Id",
                table: "WebChatLines",
                columns: new[] { "WebChatId", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_WebChats_TenantId_EndedUtc",
                table: "WebChats",
                columns: new[] { "TenantId", "EndedUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "WebChatLines");

            migrationBuilder.DropTable(
                name: "WebChats");

            migrationBuilder.DropColumn(
                name: "WebChatEnabled",
                table: "Tenants");

            migrationBuilder.DropColumn(
                name: "WebChatHours",
                table: "Tenants");

            migrationBuilder.DropColumn(
                name: "WebChatTimeZone",
                table: "Tenants");
        }
    }
}
