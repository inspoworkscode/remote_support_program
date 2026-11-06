using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RemoteSupport.Platform.Data.Migrations
{
    /// <summary>
    /// The package is five seats now, and no company has more than ten (Clint, 2026-10-08; Pricing.cs,
    /// PRICING.md). Every company is moved to five: signups were still off and the platform's own company
    /// is not billed, so nobody paying is on seven. Nobody is removed — a company with more people than
    /// the package simply pays the extra seats, agreed by its owner from here on.
    /// </summary>
    public partial class FiveSeatPackage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("UPDATE Tenants SET Seats = 5;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("UPDATE Tenants SET Seats = 7 WHERE Seats = 5;");
        }
    }
}
