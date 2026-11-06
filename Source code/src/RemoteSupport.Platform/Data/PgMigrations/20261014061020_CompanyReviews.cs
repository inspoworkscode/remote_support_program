using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace RemoteSupport.Platform.Data.PgMigrations
{
    /// <inheritdoc />
    public partial class CompanyReviews : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "ReviewAskAfterUtc",
                table: "Tenants",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ReviewLaters",
                table: "Tenants",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<bool>(
                name: "ReviewOptedOut",
                table: "Tenants",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "CompanyReviews",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    TenantId = table.Column<int>(type: "integer", nullable: false),
                    Stars = table.Column<int>(type: "integer", nullable: false),
                    Words = table.Column<string>(type: "text", nullable: false),
                    ShownName = table.Column<string>(type: "text", nullable: false),
                    BusinessName = table.Column<string>(type: "text", nullable: false),
                    BusinessUrl = table.Column<string>(type: "text", nullable: false),
                    ConsentToPublish = table.Column<bool>(type: "boolean", nullable: false),
                    WrittenBy = table.Column<string>(type: "text", nullable: false),
                    WrittenByEmail = table.Column<string>(type: "text", nullable: false),
                    WrittenUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    DecidedBy = table.Column<string>(type: "text", nullable: false),
                    DecidedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CompanyReviews", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CompanyReviews_Status_DecidedUtc",
                table: "CompanyReviews",
                columns: new[] { "Status", "DecidedUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_CompanyReviews_TenantId",
                table: "CompanyReviews",
                column: "TenantId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CompanyReviews");

            migrationBuilder.DropColumn(
                name: "ReviewAskAfterUtc",
                table: "Tenants");

            migrationBuilder.DropColumn(
                name: "ReviewLaters",
                table: "Tenants");

            migrationBuilder.DropColumn(
                name: "ReviewOptedOut",
                table: "Tenants");
        }
    }
}
