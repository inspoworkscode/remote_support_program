using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RemoteSupport.Platform.Data.Migrations
{
    /// <inheritdoc />
    public partial class Payfast : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "PayfastMerchantId",
                table: "PlatformSettings",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "PayfastMerchantKeyEnc",
                table: "PlatformSettings",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "PayfastPassphraseEnc",
                table: "PlatformSettings",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<bool>(
                name: "PayfastSandbox",
                table: "PlatformSettings",
                type: "INTEGER",
                nullable: false,
                defaultValue: true);   // sandbox until go-live: an existing settings row must never start out LIVE

            migrationBuilder.AddColumn<DateTime>(
                name: "PayfastUpdatedUtc",
                table: "PlatformSettings",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "PayfastPayments",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    TenantId = table.Column<int>(type: "INTEGER", nullable: false),
                    SubscriptionId = table.Column<int>(type: "INTEGER", nullable: false),
                    PfPaymentId = table.Column<string>(type: "TEXT", nullable: false),
                    Status = table.Column<string>(type: "TEXT", nullable: false),
                    AmountGrossCents = table.Column<long>(type: "INTEGER", nullable: false),
                    AmountFeeCents = table.Column<long>(type: "INTEGER", nullable: false),
                    AmountNetCents = table.Column<long>(type: "INTEGER", nullable: false),
                    ExpectedCents = table.Column<long>(type: "INTEGER", nullable: false),
                    Applied = table.Column<bool>(type: "INTEGER", nullable: false),
                    Problem = table.Column<string>(type: "TEXT", nullable: false),
                    BillingDate = table.Column<string>(type: "TEXT", nullable: false),
                    ItemName = table.Column<string>(type: "TEXT", nullable: false),
                    Sandbox = table.Column<bool>(type: "INTEGER", nullable: false),
                    ReceivedUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PayfastPayments", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PayfastSubscriptions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    TenantId = table.Column<int>(type: "INTEGER", nullable: false),
                    Reference = table.Column<string>(type: "TEXT", nullable: false),
                    Purpose = table.Column<string>(type: "TEXT", nullable: false),
                    Sandbox = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedBy = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ItemName = table.Column<string>(type: "TEXT", nullable: false),
                    InitialCents = table.Column<long>(type: "INTEGER", nullable: false),
                    AmountCents = table.Column<long>(type: "INTEGER", nullable: false),
                    Frequency = table.Column<int>(type: "INTEGER", nullable: false),
                    Cycles = table.Column<int>(type: "INTEGER", nullable: false),
                    BillingDate = table.Column<string>(type: "TEXT", nullable: false),
                    Token = table.Column<string>(type: "TEXT", nullable: true),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    ActivatedUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastPaymentUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    NextRunDate = table.Column<string>(type: "TEXT", nullable: false),
                    CancelledUtc = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PayfastSubscriptions", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PayfastPayments_PfPaymentId_Status",
                table: "PayfastPayments",
                columns: new[] { "PfPaymentId", "Status" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PayfastPayments_SubscriptionId_ReceivedUtc",
                table: "PayfastPayments",
                columns: new[] { "SubscriptionId", "ReceivedUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_PayfastSubscriptions_Reference",
                table: "PayfastSubscriptions",
                column: "Reference",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PayfastSubscriptions_TenantId_CreatedUtc",
                table: "PayfastSubscriptions",
                columns: new[] { "TenantId", "CreatedUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_PayfastSubscriptions_Token",
                table: "PayfastSubscriptions",
                column: "Token");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PayfastPayments");

            migrationBuilder.DropTable(
                name: "PayfastSubscriptions");

            migrationBuilder.DropColumn(
                name: "PayfastMerchantId",
                table: "PlatformSettings");

            migrationBuilder.DropColumn(
                name: "PayfastMerchantKeyEnc",
                table: "PlatformSettings");

            migrationBuilder.DropColumn(
                name: "PayfastPassphraseEnc",
                table: "PlatformSettings");

            migrationBuilder.DropColumn(
                name: "PayfastSandbox",
                table: "PlatformSettings");

            migrationBuilder.DropColumn(
                name: "PayfastUpdatedUtc",
                table: "PlatformSettings");
        }
    }
}
