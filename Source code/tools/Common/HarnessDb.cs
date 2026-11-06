using System.Text.RegularExpressions;
using Npgsql;

namespace RemoteSupport.Tools;

/// <summary>
/// Which database a harness's throwaway platform runs on (2026-10-08, HANDOVER §118, PostgreSQL step 2).
///
/// <para><b>SQLite unless <c>RSP_TEST_DB=postgres</c>.</b> Then every throwaway platform gets a fresh PostgreSQL
/// database of its own — the same one again for the same data directory, so a restart finds its data — and
/// every one is dropped when the harness ends (<c>RSP_TEST_KEEP=1</c> keeps them). Any left by a harness that
/// was killed are swept away the next day.</para>
///
/// <para>A harness calls <see cref="Use(Dictionary{string,string},string)"/> where it sets
/// <c>RSP_Platform__DataDirectory</c>; reads through <c>UseHarnessDb</c> (HarnessDb.Ef.cs) in place of
/// <c>UseSqlite</c>, or <c>HarnessDb.Connect</c> (HarnessDb.Sql.cs) in place of <c>new SqliteConnection</c>.
/// Hand-written SQL must quote its names (<c>"Tickets"</c>, which both understand; PostgreSQL folds an unquoted
/// name to lower case), use <c>@name</c> parameters and write booleans as TRUE/FALSE.</para>
/// </summary>
internal static partial class HarnessDb
{
    public static readonly bool Postgres =
        string.Equals(Environment.GetEnvironmentVariable("RSP_TEST_DB"), "postgres", StringComparison.OrdinalIgnoreCase);

    private const string Prefix = "rsp_h";
    private static readonly Dictionary<string, string> _databases = new();
    private static string? _admin;

    /// <summary>"PostgreSQL" or "SQLite", for a harness's opening line.</summary>
    public static string Name => Postgres ? "PostgreSQL" : "SQLite";

    /// <summary>The platform about to start on this data directory gets its own PostgreSQL database. Nothing on SQLite.</summary>
    public static void Use(Dictionary<string, string> env, string dataDir) { if (Postgres) env["RSP_ConnectionStrings__Postgres"] = For(dataDir); }

    /// <summary>The same, for <c>ProcessStartInfo.Environment</c>.</summary>
    public static void Use(IDictionary<string, string?> env, string dataDir) { if (Postgres) env["RSP_ConnectionStrings__Postgres"] = For(dataDir); }

    /// <summary>The PostgreSQL connection string for the platform's database in this data directory (its platform.db).</summary>
    public static string For(string dataDir) => ForFile(Path.Combine(dataDir, "platform.db"));

    /// <summary>The PostgreSQL database standing in for this SQLite FILE, made the first time it is asked. By file, not
    /// folder: a harness may keep two databases side by side (clockcheck's old.db and us.db).</summary>
    private static string ForFile(string sqliteFile)
    {
        var key = Path.GetFullPath(sqliteFile).ToLowerInvariant();
        var dataDir = sqliteFile;
        lock (_databases)
        {
            if (_databases.TryGetValue(key, out var known)) return known;
            if (_admin == null)
            {
                _admin = TestPostgres.Admin() ?? throw new InvalidOperationException(
                    "RSP_TEST_DB=postgres, but no PostgreSQL is set up on this PC: run tools\\Setup-TestPostgres.ps1 once.");
                SweepOld(_admin);
                AppDomain.CurrentDomain.ProcessExit += (_, _) => DropAll();
                Console.CancelKeyPress += (_, _) => DropAll();
            }
            // On a pool thread: a harness on a Windows Forms thread (signincheck) deadlocks waiting on async work here.
            var admin = _admin;
            var cs = Task.Run(() => TestPostgres.CreateAsync(admin, Prefix)).GetAwaiter().GetResult();
            _databases[key] = cs;
            Console.WriteLine($"  (PostgreSQL database {new NpgsqlConnectionStringBuilder(cs).Database} for {dataDir})");
            return cs;
        }
    }

    /// <summary>
    /// A harness's hand-written SQL made to suit both databases: every PascalCase word (<c>Tickets</c>,
    /// <c>DeviceId</c> — our table and column names; SQL's own words are written in capitals) is quoted, which
    /// both understand and PostgreSQL needs, since it folds an unquoted name to lower case; and a <c>$name</c>
    /// parameter becomes <c>@name</c>, the form both accept. Text in single quotes is left exactly as it is.
    /// Booleans are NOT translated: write <c>CAST(x AS INTEGER)</c> to read one as 0/1 on both.
    /// </summary>
    public static string Portable(string sql)
    {
        var sb = new System.Text.StringBuilder(sql.Length + 32);
        for (int i = 0; i < sql.Length;)
        {
            char ch = sql[i];
            if (ch == '\'')
            {
                int end = i + 1;
                while (end < sql.Length && !(sql[end] == '\'' && (end + 1 >= sql.Length || sql[end + 1] != '\''))) end += sql[end] == '\'' ? 2 : 1;
                end = Math.Min(end + 1, sql.Length);
                sb.Append(sql, i, end - i); i = end; continue;
            }
            if (ch == '"') { int end = sql.IndexOf('"', i + 1); end = end < 0 ? sql.Length : end + 1; sb.Append(sql, i, end - i); i = end; continue; }
            if (ch == '$' && i + 1 < sql.Length && char.IsLetter(sql[i + 1])) { sb.Append('@'); i++; continue; }
            if (char.IsLetter(ch) || ch == '_')
            {
                int end = i;
                while (end < sql.Length && (char.IsLetterOrDigit(sql[end]) || sql[end] == '_')) end++;
                var word = sql[i..end];
                bool afterParam = i > 0 && (sql[i - 1] == '@' || sql[i - 1] == '$');
                bool pascal = char.IsUpper(word[0]) && word.Any(char.IsLower);
                sb.Append(pascal && !afterParam ? "\"" + word + "\"" : word);
                i = end; continue;
            }
            sb.Append(ch); i++;
        }
        return sb.ToString();
    }

    /// <summary>The file a SQLite connection string names — the key <see cref="ForFile"/> uses.</summary>
    private static string FileOf(string sqliteConnectionString)
    {
        var m = Regex.Match(sqliteConnectionString, @"Data Source\s*=\s*([^;]+)", RegexOptions.IgnoreCase);
        return Path.GetFullPath(m.Success ? m.Groups[1].Value.Trim() : sqliteConnectionString);
    }

    private static void DropAll()
    {
        if (_admin == null) return;
        string[] all;
        lock (_databases) { all = _databases.Values.ToArray(); _databases.Clear(); }
        if (Environment.GetEnvironmentVariable("RSP_TEST_KEEP") == "1")
        {
            foreach (var cs in all) Console.WriteLine($"  (PostgreSQL database kept: {new NpgsqlConnectionStringBuilder(cs).Database})");
            return;
        }
        var admin = _admin;
        foreach (var cs in all) { try { Task.Run(() => TestPostgres.DropAsync(admin, cs)).GetAwaiter().GetResult(); } catch { } }
    }

    /// <summary>Databases left by a harness that never reached its end, from before yesterday. Today's are left alone:
    /// another session may be running a harness right now.</summary>
    private static void SweepOld(string admin)
    {
        try
        {
            var cutoff = DateTime.Now.AddDays(-1).ToString("yyyyMMdd");
            var old = new List<string>();
            using (var c = new NpgsqlConnection(admin))
            {
                c.Open();
                using var q = new NpgsqlCommand($"SELECT datname FROM pg_database WHERE datname LIKE '{Prefix}\\_%'", c);
                using var r = q.ExecuteReader();
                while (r.Read())
                {
                    var name = r.GetString(0);
                    var parts = name.Split('_');
                    if (parts.Length >= 3 && parts[2].Length == 8 && string.CompareOrdinal(parts[2], cutoff) < 0) old.Add(name);
                }
            }
            using var d = new NpgsqlConnection(admin);
            d.Open();
            foreach (var name in old) { using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{name}\" WITH (FORCE)", d); drop.ExecuteNonQuery(); }
        }
        catch { }
    }
}
