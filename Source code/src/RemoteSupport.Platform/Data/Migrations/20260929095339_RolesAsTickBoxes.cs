using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RemoteSupport.Platform.Data.Migrations
{
    /// <summary>
    /// Roles become tick boxes (2026-09-29, roadmap 3.6.8). No schema change — the role is still one
    /// integer — but its MEANING changes from "which one of five" to "which boxes", and a set of
    /// flags cannot have a box worth zero, so every stored number moves:
    ///
    ///   0 Technician        → 1
    ///   1 Supervisor        → 2
    ///   2 Office admin      → 4
    ///   3 Owner             → 8
    ///   4 Owner/Supervisor  → 10 (Owner + Supervisor)
    ///
    /// Written as one CASE so no row is moved twice — a chain of UPDATEs would carry 1 to 2 and then
    /// the new 2 on to 4. The portal's cookie carried the old number too; <c>PortalIdentity.RoleOf</c>
    /// reads an old cookie by the old numbers, for the same reason.
    /// </summary>
    public partial class RolesAsTickBoxes : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                UPDATE Technicians SET Role = CASE Role
                    WHEN 0 THEN 1
                    WHEN 1 THEN 2
                    WHEN 2 THEN 4
                    WHEN 3 THEN 8
                    WHEN 4 THEN 10
                    ELSE 1 END;
                """);
        }

        /// <summary>
        /// Back to one role each. A combination has no old number, so it keeps the widest one it
        /// held — a technician who was also the office administrator goes back as a technician.
        /// </summary>
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                UPDATE Technicians SET Role = CASE
                    WHEN (Role & 8) <> 0 AND (Role & 2) <> 0 THEN 4
                    WHEN (Role & 8) <> 0 THEN 3
                    WHEN (Role & 2) <> 0 THEN 1
                    WHEN (Role & 1) <> 0 THEN 0
                    WHEN (Role & 4) <> 0 THEN 2
                    ELSE 0 END;
                """);
        }
    }
}
