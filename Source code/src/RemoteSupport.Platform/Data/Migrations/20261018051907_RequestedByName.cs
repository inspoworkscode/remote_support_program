using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RemoteSupport.Platform.Data.Migrations
{
    /// <inheritdoc />
    public partial class RequestedByName : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "RequestedBy",
                table: "Devices",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<DateTime>(
                name: "RequestedByUtc",
                table: "Devices",
                type: "TEXT",
                nullable: true);

            // Every machine takes the name from its most recent support request (Source 1, a call) that gave one, so the
            // device lists tidy up the day this deploys rather than at each machine's next request (2026-10-18). Blanks only.
            migrationBuilder.Sql(@"
UPDATE ""Devices"" SET
    ""RequestedBy"" = (SELECT SUBSTR(TRIM(t.""ContactName""), 1, 100) FROM ""Tickets"" t
        WHERE t.""TenantId"" = ""Devices"".""TenantId"" AND t.""DeviceId"" = ""Devices"".""DeviceId"" AND t.""Source"" = 1 AND TRIM(t.""ContactName"") <> ''
        ORDER BY t.""CreatedUtc"" DESC, t.""Id"" DESC LIMIT 1),
    ""RequestedByUtc"" = (SELECT t.""CreatedUtc"" FROM ""Tickets"" t
        WHERE t.""TenantId"" = ""Devices"".""TenantId"" AND t.""DeviceId"" = ""Devices"".""DeviceId"" AND t.""Source"" = 1 AND TRIM(t.""ContactName"") <> ''
        ORDER BY t.""CreatedUtc"" DESC, t.""Id"" DESC LIMIT 1)
WHERE ""RequestedBy"" = '' AND EXISTS (SELECT 1 FROM ""Tickets"" t
    WHERE t.""TenantId"" = ""Devices"".""TenantId"" AND t.""DeviceId"" = ""Devices"".""DeviceId"" AND t.""Source"" = 1 AND TRIM(t.""ContactName"") <> '');");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "RequestedBy",
                table: "Devices");

            migrationBuilder.DropColumn(
                name: "RequestedByUtc",
                table: "Devices");
        }
    }
}
