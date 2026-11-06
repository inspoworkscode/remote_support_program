using Npgsql;

namespace RemoteSupport.Tools;

/// <summary>
/// The PostgreSQL the harnesses use (2026-10-08, HANDOVER §118): a server on this PC, reached as the test-only
/// account <c>rsp_test</c> that <c>tools/Setup-TestPostgres.ps1</c> makes (it may create and drop databases and
/// nothing else). Every harness gets a database of its OWN, made fresh and dropped after, exactly as each gets
/// its own throwaway SQLite file today.
///
/// <para>Order: the RSP_TEST_POSTGRES environment variable, then <c>tools/rs-postgres.txt</c> (git-ignored; the
/// setup script writes it). Neither holds Clint's administrator password — only the test account's, which the
/// script made up.</para>
/// </summary>
internal static class TestPostgres
{
    /// <summary>The connection string to the server's <c>postgres</c> database, or null when none is set up.</summary>
    public static string? Admin()
    {
        var env = Environment.GetEnvironmentVariable("RSP_TEST_POSTGRES");
        if (!string.IsNullOrWhiteSpace(env)) return env.Trim();
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (int i = 0; i < 8 && dir != null; i++, dir = dir.Parent)
        {
            var f = Path.Combine(dir.FullName, "tools", "rs-postgres.txt");
            if (File.Exists(f)) return File.ReadAllText(f).Trim();
        }
        return null;
    }

    /// <summary>A new, empty database for one harness run; its connection string. Dropped by <see cref="DropAsync"/>.</summary>
    public static async Task<string> CreateAsync(string admin, string prefix)
    {
        var name = $"{prefix}_{DateTime.Now:yyyyMMdd_HHmmss}_{Random.Shared.Next(1000, 9999)}".ToLowerInvariant();
        await using (var c = new NpgsqlConnection(admin))
        {
            await c.OpenAsync();
            await using var cmd = new NpgsqlCommand($"CREATE DATABASE \"{name}\"", c);
            await cmd.ExecuteNonQueryAsync();
        }
        return new NpgsqlConnectionStringBuilder(admin) { Database = name }.ConnectionString;
    }

    public static async Task DropAsync(string admin, string connection)
    {
        var name = new NpgsqlConnectionStringBuilder(connection).Database;
        NpgsqlConnection.ClearAllPools();
        await using var c = new NpgsqlConnection(admin);
        await c.OpenAsync();
        await using var cmd = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{name}\" WITH (FORCE)", c);
        await cmd.ExecuteNonQueryAsync();
    }
}
