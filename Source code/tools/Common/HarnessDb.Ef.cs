using Microsoft.EntityFrameworkCore;

namespace RemoteSupport.Tools;

/// <summary>The Entity Framework half of <see cref="HarnessDb"/>, for harnesses that read the platform's database
/// through <c>PlatformDb</c>. A <c>PlatformDb</c> on Npgsql reads and writes exactly as on SQLite; only migrations
/// differ by context type, and a harness never runs those — its platform does.</summary>
internal static partial class HarnessDb
{
    /// <summary>In place of <c>UseSqlite(connectionString)</c>: the same SQLite file, or that data directory's PostgreSQL database.</summary>
    public static DbContextOptionsBuilder<T> UseHarnessDb<T>(this DbContextOptionsBuilder<T> o, string sqliteConnectionString) where T : DbContext =>
        Postgres ? o.UseNpgsql(ForFile(FileOf(sqliteConnectionString))) : o.UseSqlite(sqliteConnectionString);

    public static DbContextOptionsBuilder UseHarnessDb(this DbContextOptionsBuilder o, string sqliteConnectionString) =>
        Postgres ? o.UseNpgsql(ForFile(FileOf(sqliteConnectionString))) : o.UseSqlite(sqliteConnectionString);

    /// <summary>In place of <c>db.Database.MigrateAsync()</c>. On PostgreSQL the database is built by
    /// <c>PgPlatformDb</c>'s own migrations — a <c>PlatformDb</c> on Npgsql would try SQLite's.</summary>
    public static async Task MigrateAsync(DbContext db)
    {
        if (!Postgres) { await db.Database.MigrateAsync(); return; }
        var cs = db.Database.GetConnectionString() ?? throw new InvalidOperationException("no connection string to migrate");
        await using var pg = new RemoteSupport.Platform.Data.PgPlatformDb(
            new DbContextOptionsBuilder<RemoteSupport.Platform.Data.PgPlatformDb>().UseNpgsql(cs).Options);
        await pg.Database.MigrateAsync();
    }

    /// <summary>
    /// After rows were written with ids of the harness's choosing: every PostgreSQL id counter moved past the
    /// highest id in its table, or the next row the service adds would collide with one (an identity column
    /// does not notice ids it did not hand out). Nothing to do on SQLite, which always takes the highest + 1.
    /// </summary>
    public static async Task ResetSequencesAsync(DbContext db)
    {
        if (!Postgres) return;
        await db.Database.ExecuteSqlRawAsync("""
            DO $body$
            DECLARE r record;
            BEGIN
              FOR r IN SELECT table_name, column_name FROM information_schema.columns
                       WHERE table_schema = 'public' AND is_identity = 'YES' LOOP
                EXECUTE format('SELECT setval(pg_get_serial_sequence(%L, %L), COALESCE((SELECT MAX(%I) FROM %I), 0) + 1, false)',
                               '"' || r.table_name || '"', r.column_name, r.column_name, r.table_name);
              END LOOP;
            END
            $body$;
            """);
    }

    /// <summary>For a harness's part that tests upgrading an OLDER SQLite database (seeded before a migration, then
    /// migrated): PostgreSQL has no older migrations to start from. Prints why it is skipped; true when it runs.</summary>
    public static bool SqliteUpgradePart(string what)
    {
        if (!Postgres) return true;
        Console.WriteLine($"  (skipped on PostgreSQL: {what} — it tests an upgrade of an older SQLite database)");
        return false;
    }
}
