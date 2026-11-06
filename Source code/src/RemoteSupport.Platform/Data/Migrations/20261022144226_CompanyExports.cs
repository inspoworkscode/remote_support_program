using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RemoteSupport.Platform.Data.Migrations
{
    /// <inheritdoc />
    public partial class CompanyExports : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CompanyExports",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    TenantId = table.Column<int>(type: "INTEGER", nullable: false),
                    State = table.Column<int>(type: "INTEGER", nullable: false),
                    IncludeRecordings = table.Column<bool>(type: "INTEGER", nullable: false),
                    RequestedBy = table.Column<string>(type: "TEXT", nullable: false),
                    RequestedById = table.Column<int>(type: "INTEGER", nullable: true),
                    ByUs = table.Column<bool>(type: "INTEGER", nullable: false),
                    Progress = table.Column<string>(type: "TEXT", nullable: false),
                    Summary = table.Column<string>(type: "TEXT", nullable: false),
                    FileName = table.Column<string>(type: "TEXT", nullable: false),
                    Bytes = table.Column<long>(type: "INTEGER", nullable: false),
                    LinkHash = table.Column<string>(type: "TEXT", nullable: false),
                    Error = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    StartedUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    FinishedUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ExpiresUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    Downloads = table.Column<int>(type: "INTEGER", nullable: false),
                    DownloadedUtc = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CompanyExports", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CompanyExports_LinkHash",
                table: "CompanyExports",
                column: "LinkHash");

            migrationBuilder.CreateIndex(
                name: "IX_CompanyExports_TenantId_CreatedUtc",
                table: "CompanyExports",
                columns: new[] { "TenantId", "CreatedUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CompanyExports");
        }
    }
}
