using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace RemoteSupport.Platform.Data.PgMigrations
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
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    TenantId = table.Column<int>(type: "integer", nullable: false),
                    State = table.Column<int>(type: "integer", nullable: false),
                    IncludeRecordings = table.Column<bool>(type: "boolean", nullable: false),
                    RequestedBy = table.Column<string>(type: "text", nullable: false),
                    RequestedById = table.Column<int>(type: "integer", nullable: true),
                    ByUs = table.Column<bool>(type: "boolean", nullable: false),
                    Progress = table.Column<string>(type: "text", nullable: false),
                    Summary = table.Column<string>(type: "text", nullable: false),
                    FileName = table.Column<string>(type: "text", nullable: false),
                    Bytes = table.Column<long>(type: "bigint", nullable: false),
                    LinkHash = table.Column<string>(type: "text", nullable: false),
                    Error = table.Column<string>(type: "text", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    StartedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    FinishedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ExpiresUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Downloads = table.Column<int>(type: "integer", nullable: false),
                    DownloadedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
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
