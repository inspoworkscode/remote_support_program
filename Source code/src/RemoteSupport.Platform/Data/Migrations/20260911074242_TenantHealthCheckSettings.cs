using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RemoteSupport.Platform.Data.Migrations
{
    /// <inheritdoc />
    public partial class TenantHealthCheckSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "HealthCheckDayOfMonth",
                table: "Tenants",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "HealthCheckEmailTo",
                table: "Tenants",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<bool>(
                name: "HealthCheckEnabled",
                table: "Tenants",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "HealthCheckRunMaintenance",
                table: "Tenants",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "SmtpFrom",
                table: "Tenants",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "SmtpHost",
                table: "Tenants",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "SmtpPasswordEnc",
                table: "Tenants",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<int>(
                name: "SmtpPort",
                table: "Tenants",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<bool>(
                name: "SmtpUseSsl",
                table: "Tenants",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "SmtpUser",
                table: "Tenants",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            // A C# property initialiser is not a SQL default, so AddColumn backfilled every
            // EXISTING tenant with 0 / false while any tenant created afterwards gets 587 / true
            // from the model. That inconsistency would surface much later as "SMTP port 0" on the
            // one tenant that predates this migration - which is Anywhere IT, the only one that
            // matters right now. Backfill the intended values explicitly instead.
            //
            // Safe to apply to every row: HealthCheckEnabled stays false, so nothing runs until an
            // IT company turns it on and fills in a mail server.
            migrationBuilder.Sql(
                "update Tenants set SmtpPort = 587, SmtpUseSsl = 1, HealthCheckRunMaintenance = 1;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "HealthCheckDayOfMonth",
                table: "Tenants");

            migrationBuilder.DropColumn(
                name: "HealthCheckEmailTo",
                table: "Tenants");

            migrationBuilder.DropColumn(
                name: "HealthCheckEnabled",
                table: "Tenants");

            migrationBuilder.DropColumn(
                name: "HealthCheckRunMaintenance",
                table: "Tenants");

            migrationBuilder.DropColumn(
                name: "SmtpFrom",
                table: "Tenants");

            migrationBuilder.DropColumn(
                name: "SmtpHost",
                table: "Tenants");

            migrationBuilder.DropColumn(
                name: "SmtpPasswordEnc",
                table: "Tenants");

            migrationBuilder.DropColumn(
                name: "SmtpPort",
                table: "Tenants");

            migrationBuilder.DropColumn(
                name: "SmtpUseSsl",
                table: "Tenants");

            migrationBuilder.DropColumn(
                name: "SmtpUser",
                table: "Tenants");
        }
    }
}
