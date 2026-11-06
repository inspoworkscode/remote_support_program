using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RemoteSupport.Platform.Data.PgMigrations
{
    /// <inheritdoc />
    public partial class FreeVersionBack : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // The Free version is back beside the Basic version (2026-10-25, HANDOVER §163). Until now both were stored "free":
            // the old free accounts, and the Basic companies made since the morning of 2026-10-25. A Basic company is one that
            // was given a trial (signups, Owner -> Basic version, a notice), paid for extra computers, or has a card at PayFast;
            // the free accounts from before have none of these, and stay "free" — the Free version again.
            migrationBuilder.Sql(@"UPDATE ""Tenants"" SET ""Plan"" = 'basic'
                WHERE ""Plan"" = 'free'
                  AND (""TrialEndsUtc"" IS NOT NULL OR ""BasicMachines"" > 0
                       OR ""Id"" IN (SELECT ""TenantId"" FROM ""PayfastSubscriptions"" WHERE ""Purpose"" <> 'test'))");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Back to one stored name for both, as before.
            migrationBuilder.Sql(@"UPDATE ""Tenants"" SET ""Plan"" = 'free' WHERE ""Plan"" = 'basic'");
        }
    }
}
