using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RemoteSupport.Platform.Data.Migrations
{
    /// <summary>
    /// Owner and Supervisor, separated (2026-09-24). No schema change — the role is an integer — but
    /// every company needs exactly one owner from the moment this runs, and Clint's rule is that
    /// "the person who created the company becomes Owner + Supervisor; every other administrator
    /// becomes a Supervisor". Administrator (1) already IS supervisor, so only the creator moves.
    ///
    /// The creator is the lowest-numbered administrator: signup writes the company and its first
    /// administrator together, and nothing before this made another. An enabled one is preferred,
    /// so a creator who has since been switched off does not come back as the owner.
    /// </summary>
    public partial class OwnerRole : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                UPDATE Technicians SET Role = 4
                WHERE Id IN (
                    SELECT COALESCE(
                        (SELECT MIN(a.Id) FROM Technicians a WHERE a.TenantId = t.TenantId AND a.Role = 1 AND a.Disabled = 0),
                        (SELECT MIN(a.Id) FROM Technicians a WHERE a.TenantId = t.TenantId AND a.Role = 1))
                    FROM (SELECT DISTINCT TenantId FROM Technicians) t)
                AND NOT EXISTS (SELECT 1 FROM Technicians o WHERE o.TenantId = Technicians.TenantId AND o.Role IN (3, 4));
                """);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("UPDATE Technicians SET Role = 1 WHERE Role IN (3, 4);");
        }
    }
}
