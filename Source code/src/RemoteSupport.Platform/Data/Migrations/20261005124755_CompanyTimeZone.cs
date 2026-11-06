using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RemoteSupport.Platform.Data.Migrations
{
    /// <inheritdoc />
    public partial class CompanyTimeZone : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "TimeZone",
                table: "Tenants",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            // Every company's zone from its region — CompanyClock.DefaultFor, in SQL. Anywhere IT, and
            // every company made on the command line, is South Africa.
            migrationBuilder.Sql(@"
UPDATE Tenants SET TimeZone = CASE Region
    WHEN 'UK' THEN 'Europe/London'
    WHEN 'US' THEN 'America/New_York'
    WHEN 'CA' THEN 'America/Toronto'
    WHEN 'EU' THEN CASE trim(Country)
        WHEN 'Ireland' THEN 'Europe/Dublin'
        WHEN 'Portugal' THEN 'Europe/Lisbon'
        WHEN 'Finland' THEN 'Europe/Helsinki'
        WHEN 'Greece' THEN 'Europe/Helsinki'
        ELSE 'Europe/Amsterdam' END
    ELSE 'Africa/Johannesburg' END;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "TimeZone",
                table: "Tenants");
        }
    }
}
