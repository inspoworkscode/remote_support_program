using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace RemoteSupport.Platform.Data.PgMigrations
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
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "PayfastMerchantKeyEnc",
                table: "PlatformSettings",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "PayfastPassphraseEnc",
                table: "PlatformSettings",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<bool>(
                name: "PayfastSandbox",
                table: "PlatformSettings",
                type: "boolean",
                nullable: false,
                defaultValue: true);   // sandbox until go-live: an existing settings row must never start out LIVE

            migrationBuilder.AddColumn<DateTime>(
                name: "PayfastUpdatedUtc",
                table: "PlatformSettings",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "PayfastPayments",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    TenantId = table.Column<int>(type: "integer", nullable: false),
                    SubscriptionId = table.Column<int>(type: "integer", nullable: false),
                    PfPaymentId = table.Column<string>(type: "text", nullable: false),
                    Status = table.Column<string>(type: "text", nullable: false),
                    AmountGrossCents = table.Column<long>(type: "bigint", nullable: false),
                    AmountFeeCents = table.Column<long>(type: "bigint", nullable: false),
                    AmountNetCents = table.Column<long>(type: "bigint", nullable: false),
                    ExpectedCents = table.Column<long>(type: "bigint", nullable: false),
                    Applied = table.Column<bool>(type: "boolean", nullable: false),
                    Problem = table.Column<string>(type: "text", nullable: false),
                    BillingDate = table.Column<string>(type: "text", nullable: false),
                    ItemName = table.Column<string>(type: "text", nullable: false),
                    Sandbox = table.Column<bool>(type: "boolean", nullable: false),
                    ReceivedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PayfastPayments", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PayfastSubscriptions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    TenantId = table.Column<int>(type: "integer", nullable: false),
                    Reference = table.Column<string>(type: "text", nullable: false),
                    Purpose = table.Column<string>(type: "text", nullable: false),
                    Sandbox = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedBy = table.Column<string>(type: "text", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ItemName = table.Column<string>(type: "text", nullable: false),
                    InitialCents = table.Column<long>(type: "bigint", nullable: false),
                    AmountCents = table.Column<long>(type: "bigint", nullable: false),
                    Frequency = table.Column<int>(type: "integer", nullable: false),
                    Cycles = table.Column<int>(type: "integer", nullable: false),
                    BillingDate = table.Column<string>(type: "text", nullable: false),
                    Token = table.Column<string>(type: "text", nullable: true),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    ActivatedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastPaymentUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    NextRunDate = table.Column<string>(type: "text", nullable: false),
                    CancelledUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
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
