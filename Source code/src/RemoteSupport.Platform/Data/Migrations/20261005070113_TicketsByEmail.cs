using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RemoteSupport.Platform.Data.Migrations
{
    /// <inheritdoc />
    public partial class TicketsByEmail : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "RequesterEmail",
                table: "Tickets",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "IntakeAddress",
                table: "PlatformSettings",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<int>(
                name: "IntakeFailures",
                table: "PlatformSettings",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "IntakeImapHost",
                table: "PlatformSettings",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "IntakeImapPasswordEnc",
                table: "PlatformSettings",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<int>(
                name: "IntakeImapPort",
                table: "PlatformSettings",
                type: "INTEGER",
                nullable: false,
                defaultValue: 993);

            migrationBuilder.AddColumn<string>(
                name: "IntakeImapSecurity",
                table: "PlatformSettings",
                type: "TEXT",
                nullable: false,
                defaultValue: "ssl");

            migrationBuilder.AddColumn<string>(
                name: "IntakeImapUser",
                table: "PlatformSettings",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<DateTime>(
                name: "IntakeLastCheckUtc",
                table: "PlatformSettings",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "IntakeLastProblem",
                table: "PlatformSettings",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<long>(
                name: "IntakeLastUid",
                table: "PlatformSettings",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<DateTime>(
                name: "IntakeNextTryUtc",
                table: "PlatformSettings",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IntakePaused",
                table: "PlatformSettings",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<long>(
                name: "IntakeUidValidity",
                table: "PlatformSettings",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.CreateTable(
                name: "InboundMail",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    TenantId = table.Column<int>(type: "INTEGER", nullable: false),
                    Channel = table.Column<string>(type: "TEXT", nullable: false),
                    MessageId = table.Column<string>(type: "TEXT", nullable: false),
                    FromAddress = table.Column<string>(type: "TEXT", nullable: false),
                    FromName = table.Column<string>(type: "TEXT", nullable: false),
                    Subject = table.Column<string>(type: "TEXT", nullable: false),
                    ReceivedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Outcome = table.Column<string>(type: "TEXT", nullable: false),
                    Reason = table.Column<string>(type: "TEXT", nullable: false),
                    TicketId = table.Column<int>(type: "INTEGER", nullable: true),
                    TicketNumber = table.Column<int>(type: "INTEGER", nullable: false),
                    ReplyResult = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InboundMail", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "TicketIntake",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    TenantId = table.Column<int>(type: "INTEGER", nullable: false),
                    EmailEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    Way = table.Column<string>(type: "TEXT", nullable: false),
                    Address = table.Column<string>(type: "TEXT", nullable: false),
                    AddressConfirmed = table.Column<bool>(type: "INTEGER", nullable: false),
                    ConfirmTokenHash = table.Column<string>(type: "TEXT", nullable: false),
                    ConfirmSentUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ImapHost = table.Column<string>(type: "TEXT", nullable: false),
                    ImapPort = table.Column<int>(type: "INTEGER", nullable: false),
                    ImapSecurity = table.Column<string>(type: "TEXT", nullable: false),
                    ImapUser = table.Column<string>(type: "TEXT", nullable: false),
                    ImapPasswordEnc = table.Column<string>(type: "TEXT", nullable: false),
                    Folder = table.Column<string>(type: "TEXT", nullable: false),
                    SendVia = table.Column<string>(type: "TEXT", nullable: false),
                    SmtpSameLogin = table.Column<bool>(type: "INTEGER", nullable: false),
                    SmtpHost = table.Column<string>(type: "TEXT", nullable: false),
                    SmtpPort = table.Column<int>(type: "INTEGER", nullable: false),
                    SmtpSecurity = table.Column<string>(type: "TEXT", nullable: false),
                    SmtpUser = table.Column<string>(type: "TEXT", nullable: false),
                    SmtpPasswordEnc = table.Column<string>(type: "TEXT", nullable: false),
                    AutoReply = table.Column<bool>(type: "INTEGER", nullable: false),
                    AutoReplyText = table.Column<string>(type: "TEXT", nullable: false),
                    UidValidity = table.Column<long>(type: "INTEGER", nullable: false),
                    LastUid = table.Column<long>(type: "INTEGER", nullable: false),
                    LastCheckUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastOkUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastProblem = table.Column<string>(type: "TEXT", nullable: false),
                    Paused = table.Column<bool>(type: "INTEGER", nullable: false),
                    Failures = table.Column<int>(type: "INTEGER", nullable: false),
                    NextTryUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    GmailConfirmation = table.Column<string>(type: "TEXT", nullable: false),
                    GmailConfirmationUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    FormEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    FormHeading = table.Column<string>(type: "TEXT", nullable: false),
                    FormIntro = table.Column<string>(type: "TEXT", nullable: false),
                    FormPhone = table.Column<string>(type: "TEXT", nullable: false),
                    FormCompany = table.Column<bool>(type: "INTEGER", nullable: false),
                    FormThanks = table.Column<string>(type: "TEXT", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TicketIntake", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Tickets_TenantId_RequesterEmail",
                table: "Tickets",
                columns: new[] { "TenantId", "RequesterEmail" });

            migrationBuilder.CreateIndex(
                name: "IX_InboundMail_TenantId_FromAddress_ReceivedUtc",
                table: "InboundMail",
                columns: new[] { "TenantId", "FromAddress", "ReceivedUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_InboundMail_TenantId_Id",
                table: "InboundMail",
                columns: new[] { "TenantId", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_InboundMail_TenantId_MessageId",
                table: "InboundMail",
                columns: new[] { "TenantId", "MessageId" });

            migrationBuilder.CreateIndex(
                name: "IX_TicketIntake_TenantId",
                table: "TicketIntake",
                column: "TenantId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "InboundMail");

            migrationBuilder.DropTable(
                name: "TicketIntake");

            migrationBuilder.DropIndex(
                name: "IX_Tickets_TenantId_RequesterEmail",
                table: "Tickets");

            migrationBuilder.DropColumn(
                name: "RequesterEmail",
                table: "Tickets");

            migrationBuilder.DropColumn(
                name: "IntakeAddress",
                table: "PlatformSettings");

            migrationBuilder.DropColumn(
                name: "IntakeFailures",
                table: "PlatformSettings");

            migrationBuilder.DropColumn(
                name: "IntakeImapHost",
                table: "PlatformSettings");

            migrationBuilder.DropColumn(
                name: "IntakeImapPasswordEnc",
                table: "PlatformSettings");

            migrationBuilder.DropColumn(
                name: "IntakeImapPort",
                table: "PlatformSettings");

            migrationBuilder.DropColumn(
                name: "IntakeImapSecurity",
                table: "PlatformSettings");

            migrationBuilder.DropColumn(
                name: "IntakeImapUser",
                table: "PlatformSettings");

            migrationBuilder.DropColumn(
                name: "IntakeLastCheckUtc",
                table: "PlatformSettings");

            migrationBuilder.DropColumn(
                name: "IntakeLastProblem",
                table: "PlatformSettings");

            migrationBuilder.DropColumn(
                name: "IntakeLastUid",
                table: "PlatformSettings");

            migrationBuilder.DropColumn(
                name: "IntakeNextTryUtc",
                table: "PlatformSettings");

            migrationBuilder.DropColumn(
                name: "IntakePaused",
                table: "PlatformSettings");

            migrationBuilder.DropColumn(
                name: "IntakeUidValidity",
                table: "PlatformSettings");
        }
    }
}
