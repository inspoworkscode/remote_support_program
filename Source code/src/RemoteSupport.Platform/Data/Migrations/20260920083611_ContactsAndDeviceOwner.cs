using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RemoteSupport.Platform.Data.Migrations
{
    /// <inheritdoc />
    public partial class ContactsAndDeviceOwner : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ContactId",
                table: "Tickets",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ContactId",
                table: "Devices",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "Contacts",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    TenantId = table.Column<int>(type: "INTEGER", nullable: false),
                    ClientId = table.Column<int>(type: "INTEGER", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    Phone = table.Column<string>(type: "TEXT", nullable: false),
                    Email = table.Column<string>(type: "TEXT", nullable: false),
                    Role = table.Column<string>(type: "TEXT", nullable: false),
                    Notes = table.Column<string>(type: "TEXT", nullable: false),
                    Archived = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Contacts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Contacts_Clients_ClientId",
                        column: x => x.ClientId,
                        principalTable: "Clients",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Tickets_ContactId",
                table: "Tickets",
                column: "ContactId");

            migrationBuilder.CreateIndex(
                name: "IX_Devices_ContactId",
                table: "Devices",
                column: "ContactId");

            migrationBuilder.CreateIndex(
                name: "IX_Contacts_ClientId",
                table: "Contacts",
                column: "ClientId");

            migrationBuilder.CreateIndex(
                name: "IX_Contacts_TenantId_ClientId",
                table: "Contacts",
                columns: new[] { "TenantId", "ClientId" });

            migrationBuilder.CreateIndex(
                name: "IX_Contacts_TenantId_Name",
                table: "Contacts",
                columns: new[] { "TenantId", "Name" });

            migrationBuilder.AddForeignKey(
                name: "FK_Devices_Contacts_ContactId",
                table: "Devices",
                column: "ContactId",
                principalTable: "Contacts",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_Tickets_Contacts_ContactId",
                table: "Tickets",
                column: "ContactId",
                principalTable: "Contacts",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            // ---- the data step ----
            // Every client that already had a contact person gets that person as their first
            // Contact. COPIED, not moved: Clients.ContactName stays and stays populated, because
            // the client type-ahead still searches it and anything reading it must keep working
            // while the portal moves across. Dropping it here would be a second job in one
            // migration and would take the search down with it.
            //
            // NOT EXISTS makes this idempotent. The migration history already stops it running
            // twice, but a migration that quietly duplicates every contact when somebody replays
            // it by hand is a bad thing to leave lying in a repository - and `contactcheck`
            // asserts exactly this.
            migrationBuilder.Sql(@"
                INSERT INTO Contacts (TenantId, ClientId, Name, Phone, Email, Role, Notes, Archived, CreatedUtc)
                SELECT c.TenantId, c.Id, TRIM(c.ContactName), COALESCE(c.Phone, ''), COALESCE(c.Email, ''), '', '', 0, c.CreatedUtc
                FROM Clients c
                WHERE TRIM(COALESCE(c.ContactName, '')) <> ''
                  AND NOT EXISTS (
                      SELECT 1 FROM Contacts x
                      WHERE x.ClientId = c.Id AND LOWER(x.Name) = LOWER(TRIM(c.ContactName)));");

            // Devices are deliberately NOT pointed at the contact this creates. A client's one
            // contact person was never a statement about who sits at which machine, and a company
            // with eight PCs would come out of this claiming all eight belong to whoever answered
            // the phone. An unlinked machine still shows its logged-on user, so nothing is blank
            // in the meantime - see ContactService.OwnerName.
            //
            // Tickets are not backfilled either. Ticket.ContactName is free text that a technician
            // typed; matching it to a Contact by name would be a guess written into a permanent
            // record, and the string is already there for anyone reading the old ticket.
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Devices_Contacts_ContactId",
                table: "Devices");

            migrationBuilder.DropForeignKey(
                name: "FK_Tickets_Contacts_ContactId",
                table: "Tickets");

            migrationBuilder.DropTable(
                name: "Contacts");

            migrationBuilder.DropIndex(
                name: "IX_Tickets_ContactId",
                table: "Tickets");

            migrationBuilder.DropIndex(
                name: "IX_Devices_ContactId",
                table: "Devices");

            migrationBuilder.DropColumn(
                name: "ContactId",
                table: "Tickets");

            migrationBuilder.DropColumn(
                name: "ContactId",
                table: "Devices");
        }
    }
}
