using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RemoteSupport.Platform.Data.Migrations
{
    /// <inheritdoc />
    public partial class PerCompanyAddresses : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Token",
                table: "TicketPhotos",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "Token",
                table: "TicketAttachments",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "Token",
                table: "SessionRecordings",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<int>(
                name: "Number",
                table: "RecurringTasks",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "Token",
                table: "KbImages",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<int>(
                name: "Number",
                table: "KbArticles",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "Number",
                table: "Clients",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            // The backfill (HANDOVER §159): each company's existing clients, articles and recurring tasks numbered
            // 1, 2, 3 in the order they were made (by id), BEFORE the unique indexes; every stored file a random
            // address of the same shape FileToken.New() gives (24 hex characters).
            foreach (var table in new[] { "Clients", "KbArticles", "RecurringTasks" })
                migrationBuilder.Sql($@"UPDATE ""{table}"" SET ""Number"" = (SELECT COUNT(*) FROM ""{table}"" o
    WHERE o.""TenantId"" = ""{table}"".""TenantId"" AND o.""Id"" <= ""{table}"".""Id"");");
            foreach (var table in new[] { "TicketPhotos", "TicketAttachments", "KbImages", "SessionRecordings" })
                migrationBuilder.Sql($@"UPDATE ""{table}"" SET ""Token"" = lower(hex(randomblob(12))) WHERE ""Token"" = '';");

            migrationBuilder.CreateIndex(
                name: "IX_TicketPhotos_TenantId_Token",
                table: "TicketPhotos",
                columns: new[] { "TenantId", "Token" });

            migrationBuilder.CreateIndex(
                name: "IX_TicketAttachments_TenantId_Token",
                table: "TicketAttachments",
                columns: new[] { "TenantId", "Token" });

            migrationBuilder.CreateIndex(
                name: "IX_SessionRecordings_TenantId_Token",
                table: "SessionRecordings",
                columns: new[] { "TenantId", "Token" });

            migrationBuilder.CreateIndex(
                name: "IX_RecurringTasks_TenantId_Number",
                table: "RecurringTasks",
                columns: new[] { "TenantId", "Number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_KbImages_TenantId_Token",
                table: "KbImages",
                columns: new[] { "TenantId", "Token" });

            migrationBuilder.CreateIndex(
                name: "IX_KbArticles_TenantId_Number",
                table: "KbArticles",
                columns: new[] { "TenantId", "Number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Clients_TenantId_Number",
                table: "Clients",
                columns: new[] { "TenantId", "Number" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_TicketPhotos_TenantId_Token",
                table: "TicketPhotos");

            migrationBuilder.DropIndex(
                name: "IX_TicketAttachments_TenantId_Token",
                table: "TicketAttachments");

            migrationBuilder.DropIndex(
                name: "IX_SessionRecordings_TenantId_Token",
                table: "SessionRecordings");

            migrationBuilder.DropIndex(
                name: "IX_RecurringTasks_TenantId_Number",
                table: "RecurringTasks");

            migrationBuilder.DropIndex(
                name: "IX_KbImages_TenantId_Token",
                table: "KbImages");

            migrationBuilder.DropIndex(
                name: "IX_KbArticles_TenantId_Number",
                table: "KbArticles");

            migrationBuilder.DropIndex(
                name: "IX_Clients_TenantId_Number",
                table: "Clients");

            migrationBuilder.DropColumn(
                name: "Token",
                table: "TicketPhotos");

            migrationBuilder.DropColumn(
                name: "Token",
                table: "TicketAttachments");

            migrationBuilder.DropColumn(
                name: "Token",
                table: "SessionRecordings");

            migrationBuilder.DropColumn(
                name: "Number",
                table: "RecurringTasks");

            migrationBuilder.DropColumn(
                name: "Token",
                table: "KbImages");

            migrationBuilder.DropColumn(
                name: "Number",
                table: "KbArticles");

            migrationBuilder.DropColumn(
                name: "Number",
                table: "Clients");
        }
    }
}
