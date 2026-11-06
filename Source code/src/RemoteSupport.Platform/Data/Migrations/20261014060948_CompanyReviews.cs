using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RemoteSupport.Platform.Data.Migrations
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
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ReviewLaters",
                table: "Tenants",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<bool>(
                name: "ReviewOptedOut",
                table: "Tenants",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "CompanyReviews",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    TenantId = table.Column<int>(type: "INTEGER", nullable: false),
                    Stars = table.Column<int>(type: "INTEGER", nullable: false),
                    Words = table.Column<string>(type: "TEXT", nullable: false),
                    ShownName = table.Column<string>(type: "TEXT", nullable: false),
                    BusinessName = table.Column<string>(type: "TEXT", nullable: false),
                    BusinessUrl = table.Column<string>(type: "TEXT", nullable: false),
                    ConsentToPublish = table.Column<bool>(type: "INTEGER", nullable: false),
                    WrittenBy = table.Column<string>(type: "TEXT", nullable: false),
                    WrittenByEmail = table.Column<string>(type: "TEXT", nullable: false),
                    WrittenUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    DecidedBy = table.Column<string>(type: "TEXT", nullable: false),
                    DecidedUtc = table.Column<DateTime>(type: "TEXT", nullable: true)
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
