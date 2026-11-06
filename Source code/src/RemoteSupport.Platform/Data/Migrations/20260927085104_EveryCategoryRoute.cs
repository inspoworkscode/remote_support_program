using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RemoteSupport.Platform.Data.Migrations
{
    /// <inheritdoc />
    public partial class EveryCategoryRoute : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Walk in, Remote support and General join their routes (Clint, 2026-09-27), the way
            // Workshop and Call out did: Closed — and, for these three, Resolved, which is how a
            // finished remote session has always been left — stays finished; everything else starts
            // at its route's first stage. Nothing here has been through the new choices yet.
            // Category 0 Remote support, 1 Walk in, 4 General; Status 3 Resolved, 4 Closed.
            migrationBuilder.Sql("""
                UPDATE Tickets SET
                    Stage = CASE WHEN Status IN (3, 4) THEN 'closed'
                                 WHEN Category = 0 THEN 'remote-session'
                                 WHEN Category = 1 THEN 'walkin-booked'
                                 ELSE 'general-open' END,
                    StageReturn = '',
                    Lane = CASE WHEN Status IN (3, 4) THEN 3 ELSE 0 END,
                    ClosedUtc = CASE WHEN Status IN (3, 4) THEN COALESCE(ClosedUtc, UpdatedUtc) ELSE NULL END,
                    Status = CASE WHEN Status IN (3, 4) THEN 4 ELSE 0 END
                WHERE Category IN (0, 1, 4) AND Stage = '';
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {

        }
    }
}
