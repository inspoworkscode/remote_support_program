using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RemoteSupport.Platform.Data.Migrations
{
    /// <inheritdoc />
    public partial class HealthCheckRuns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "HealthCheckRuns",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    TenantId = table.Column<int>(type: "INTEGER", nullable: false),
                    DeviceId = table.Column<string>(type: "TEXT", nullable: false),
                    MachineName = table.Column<string>(type: "TEXT", nullable: false),
                    ClientId = table.Column<int>(type: "INTEGER", nullable: true),
                    ReceivedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    RunUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Critical = table.Column<int>(type: "INTEGER", nullable: false),
                    Warning = table.Column<int>(type: "INTEGER", nullable: false),
                    Info = table.Column<int>(type: "INTEGER", nullable: false),
                    Ok = table.Column<int>(type: "INTEGER", nullable: false),
                    MaintenanceRan = table.Column<bool>(type: "INTEGER", nullable: false),
                    RestorePointMade = table.Column<bool>(type: "INTEGER", nullable: false),
                    JsonFile = table.Column<string>(type: "TEXT", nullable: false),
                    PdfFile = table.Column<string>(type: "TEXT", nullable: false),
                    MailStatus = table.Column<string>(type: "TEXT", nullable: false),
                    MailedUtc = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HealthCheckRuns", x => x.Id);
                    table.ForeignKey(
                        name: "FK_HealthCheckRuns_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_HealthCheckRuns_TenantId_DeviceId_RunUtc",
                table: "HealthCheckRuns",
                columns: new[] { "TenantId", "DeviceId", "RunUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_HealthCheckRuns_TenantId_ReceivedUtc",
                table: "HealthCheckRuns",
                columns: new[] { "TenantId", "ReceivedUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "HealthCheckRuns");
        }
    }
}
