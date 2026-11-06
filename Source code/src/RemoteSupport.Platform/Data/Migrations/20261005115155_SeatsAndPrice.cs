using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RemoteSupport.Platform.Data.Migrations
{
    /// <inheritdoc />
    public partial class SeatsAndPrice : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "NotBilled",
                table: "Tenants",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "Region",
                table: "Tenants",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateTable(
                name: "SeatAgreements",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    TenantId = table.Column<int>(type: "INTEGER", nullable: false),
                    People = table.Column<int>(type: "INTEGER", nullable: false),
                    Included = table.Column<int>(type: "INTEGER", nullable: false),
                    Region = table.Column<string>(type: "TEXT", nullable: false),
                    Monthly = table.Column<decimal>(type: "TEXT", nullable: false),
                    MonthlyBefore = table.Column<decimal>(type: "TEXT", nullable: false),
                    What = table.Column<string>(type: "TEXT", nullable: false),
                    AgreedBy = table.Column<string>(type: "TEXT", nullable: false),
                    AgreedUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SeatAgreements", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SeatAgreements_TenantId_AgreedUtc",
                table: "SeatAgreements",
                columns: new[] { "TenantId", "AgreedUtc" });

            // Every company's region from the country it has on file — Pricing.RegionOfCountry, in SQL.
            migrationBuilder.Sql(@"
UPDATE Tenants SET Region = CASE
    WHEN trim(Country) = '' OR lower(trim(Country)) IN ('south africa', 'za') THEN 'ZA'
    WHEN lower(trim(Country)) = 'canada' THEN 'CA'
    WHEN lower(trim(Country)) IN ('united kingdom', 'uk') THEN 'UK'
    WHEN lower(trim(Country)) IN ('europe', 'ireland', 'germany', 'netherlands', 'france', 'spain', 'italy', 'portugal', 'belgium',
        'switzerland', 'austria', 'sweden', 'norway', 'denmark', 'finland', 'poland', 'luxembourg', 'greece', 'czech republic', 'czechia') THEN 'EU'
    ELSE 'US' END;");
            // The package is seven seats now (PRICING.md); a company given more by the platform owner keeps them.
            migrationBuilder.Sql("UPDATE Tenants SET Seats = 7 WHERE Seats < 7;");
            // The platform's own company is not billed (Clint, 2026-10-05).
            migrationBuilder.Sql("UPDATE Tenants SET NotBilled = 1 WHERE Slug = 'anywhereit';");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SeatAgreements");

            migrationBuilder.DropColumn(
                name: "NotBilled",
                table: "Tenants");

            migrationBuilder.DropColumn(
                name: "Region",
                table: "Tenants");
        }
    }
}
