using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RemoteSupport.Platform.Data.Migrations
{
    /// <inheritdoc />
    public partial class Mentions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "TicketMentions",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    TenantId = table.Column<int>(type: "INTEGER", nullable: false),
                    TicketId = table.Column<int>(type: "INTEGER", nullable: false),
                    NoteId = table.Column<long>(type: "INTEGER", nullable: false),
                    Person = table.Column<string>(type: "TEXT", nullable: false),
                    MentionedBy = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    AnsweredUtc = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TicketMentions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TicketMentions_TicketNotes_NoteId",
                        column: x => x.NoteId,
                        principalTable: "TicketNotes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_TicketMentions_Tickets_TicketId",
                        column: x => x.TicketId,
                        principalTable: "Tickets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TicketMentions_NoteId",
                table: "TicketMentions",
                column: "NoteId");

            migrationBuilder.CreateIndex(
                name: "IX_TicketMentions_TenantId_Person_AnsweredUtc",
                table: "TicketMentions",
                columns: new[] { "TenantId", "Person", "AnsweredUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_TicketMentions_TicketId",
                table: "TicketMentions",
                column: "TicketId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TicketMentions");
        }
    }
}
