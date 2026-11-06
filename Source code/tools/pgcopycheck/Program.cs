using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Npgsql;
using RemoteSupport.Platform.Data;
using RemoteSupport.Shared;
using RemoteSupport.Tools;

// ---------------------------------------------------------------------------------------------
// pgcopycheck - the live server's data copy, SQLite to PostgreSQL (2026-10-09, step 4 of HANDOVER §118).
//
// Proves `RemoteSupport.Platform copy-to-postgres` on three real-shaped SQLite files before it ever meets
// the live one:
//   A. every table filled by THIS harness with awkward values: times below a microsecond, DateTime.MinValue
//      and MaxValue, nulls, empty text, accents and emoji, extreme numbers, a ticket with a parent;
//   B. a scratch copy of the local test server's own platform.db (refused while it is behind on migrations,
//      then migrated as a deploy would and copied) — skipped if this PC has none;
//   C. the load test's seed (seed-loadtest), whose password is known, so a COPIED technician signs in.
// For each: the copy says OK; the SQLite file's SHA-256 is unchanged (it is the way back); every table has
// the same rows by plain SQL on both sides, independent of the copy's own check; a second copy into the same
// database is refused; new rows made on the copy do not collide; the platform starts on it and signs in.
// Then the copy's check is shown to CATCH damage — one character, one microsecond, a lost parent, a missing
// row — and to put each one right again; and text holding a NUL is refused before anything is copied.
//
//   dotnet run -c Release --project tools/pgcopycheck/pgcopycheck.csproj [-- --keep]
// ---------------------------------------------------------------------------------------------

const int Port = 8161;
string http = $"http://127.0.0.1:{Port}", ws = $"ws://127.0.0.1:{Port}";
int pass = 0, fail = 0;
void Check(string what, bool ok, string detail = "")
{
    if (ok) pass++; else fail++;
    Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}  {what}{(detail.Length > 0 ? " - " + detail : "")}");
}
var keep = args.Contains("--keep");
var repo = FindRepoRoot();
var project = Path.Combine(repo, "src", "RemoteSupport.Platform", "RemoteSupport.Platform.csproj");
var admin = TestPostgres.Admin();
Console.WriteLine("pgcopycheck - the SQLite to PostgreSQL data copy");
if (admin == null) { Console.WriteLine("No PostgreSQL set up on this PC (run tools\\Setup-TestPostgres.ps1 once)."); return 2; }

var work = Path.Combine(Path.GetTempPath(), "rs-pgcopycheck-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
var serverDir = Path.Combine(work, "server");
var made = new List<string>();
var built = Run("dotnet", $"build -c Release \"{project}\" -v q --nologo -p:OutDir=\"{serverDir}\\\"", repo, 600000);
if (built.code != 0) { Console.WriteLine(built.output); return 2; }
var exe = Path.Combine(serverDir, "RemoteSupport.Platform.exe");

IModel model;
using (var m = new PlatformDb(new DbContextOptionsBuilder<PlatformDb>().UseSqlite("Data Source=:memory:").Options)) model = m.Model;
var tables = model.GetEntityTypes().Where(t => t.GetTableName() != null).Select(t => t.GetTableName()!).Distinct().OrderBy(x => x).ToList();
string T<E>() => model.FindEntityType(typeof(E))!.GetTableName()!;

try
{
    // ================= A. every table, awkward values =================
    Console.WriteLine();
    Console.WriteLine($"== A: every one of the {tables.Count} tables filled with awkward values ==");
    var genDir = Path.Combine(work, "gen"); Directory.CreateDirectory(genDir);
    var genFile = Path.Combine(genDir, "platform.db");
    await Fill(genFile, model);
    var genCounts = SqliteCounts(genFile, tables);
    var empty = genCounts.Where(kv => kv.Value < 4).Select(kv => kv.Key).ToList();
    Check($"the generated file has rows in every table", empty.Count == 0, empty.Count == 0 ? $"{genCounts.Values.Sum()} rows" : "empty: " + string.Join(", ", empty));
    var genSelf = SqliteScalar(genFile, $"SELECT COUNT(*) FROM \"{T<Ticket>()}\" WHERE \"ParentTicketId\" IS NOT NULL");
    Check("...including tickets with a parent (the table that points at itself)", genSelf > 0, $"{genSelf}");
    await ProveCopy("A", genFile, genDir, null);

    // ================= B. the local test server's own database =================
    Console.WriteLine();
    Console.WriteLine("== B: a scratch copy of the local test server's platform.db ==");
    var realSrc = Path.Combine(repo, "src", "RemoteSupport.Platform", "bin", "Release", "net10.0", "data", "platform.db");
    if (!File.Exists(realSrc)) Console.WriteLine("  (skipped: this PC has no local test server database)");
    else
    {
        var realDir = Path.Combine(work, "real"); Directory.CreateDirectory(realDir);
        var realFile = Path.Combine(realDir, "platform.db");
        File.Copy(realSrc, realFile);
        int behind;
        await using (var s = Sqlite(realFile, readOnly: true)) behind = (await s.Database.GetPendingMigrationsAsync()).Count();
        if (behind > 0)
        {
            var before = Sha(realFile);
            var conn = await NewDb();
            var r = Run(exe, $"copy-to-postgres --from \"{realFile}\" --to \"{conn}\"", serverDir, 300000);
            Check($"B: a file {behind} migration(s) behind is refused", r.code != 0 && r.output.Contains("REFUSED") && r.output.Contains("behind"), Last(r.output));
            Check("B: ...and left exactly as it was", Sha(realFile) == before);
            Check("B: ...and nothing was made in PostgreSQL", PgScalar(conn, "SELECT COUNT(*) FROM information_schema.tables WHERE table_schema = 'public'") == 0);
            await using (var s = Sqlite(realFile, readOnly: false)) await s.Database.MigrateAsync(); // what a deploy does
            Console.WriteLine($"  (migrated the scratch copy, as a deploy would)");
        }
        else Console.WriteLine("  (it is up to date, so the behind-on-migrations refusal is not shown here)");
        Console.WriteLine($"  {SqliteCounts(realFile, tables).Values.Sum()} rows: " + string.Join(", ",
            SqliteCounts(realFile, tables).Where(kv => kv.Value > 0).OrderByDescending(kv => kv.Value).Take(8).Select(kv => $"{kv.Key} {kv.Value}")));
        await ProveCopy("B", realFile, realDir, null);
    }

    // ================= C. the load test's seed =================
    Console.WriteLine();
    Console.WriteLine("== C: the load test's seed (30 companies, 4 people each) ==");
    var loadDir = Path.Combine(work, "load"); Directory.CreateDirectory(loadDir);
    var seedFile = Path.Combine(work, "seed.txt");
    var seeded = Run(exe, $"seed-loadtest 30 4 \"{seedFile}\"", serverDir, 300000, new() { ["RSP_Platform__DataDirectory"] = loadDir });
    var password = File.Exists(seedFile) ? File.ReadAllLines(seedFile).FirstOrDefault(l => l.StartsWith("password\t"))?.Split('\t')[1] : null;
    Check("C: the seed is made", seeded.code == 0 && password != null, seeded.code == 0 ? "" : Last(seeded.output));
    if (password != null) await ProveCopy("C", Path.Combine(loadDir, "platform.db"), loadDir, ("t1@load-00001.test", password));

    // ================= D. damage the copy: its check must say so =================
    Console.WriteLine();
    Console.WriteLine("== D: damage made to a good copy - the check must call it DIFFERENT, and same once put right ==");
    {
        var conn = await NewDb();
        var r = Run(exe, $"copy-to-postgres --from \"{genFile}\" --to \"{conn}\"", serverDir, 300000);
        Check("D: a good copy to start from", r.code == 0, Last(r.output));
        string tk = T<Ticket>(), dv = T<Device>();
        var firstTicket = PgScalar(conn, $"SELECT MIN(\"Id\") FROM \"{tk}\"");
        var withParent = PgScalar(conn, $"SELECT MIN(\"Id\") FROM \"{tk}\" WHERE \"ParentTicketId\" IS NOT NULL");
        var parent = PgScalar(conn, $"SELECT \"ParentTicketId\" FROM \"{tk}\" WHERE \"Id\" = {withParent}");
        var firstDevice = PgScalar(conn, $"SELECT MIN(\"Id\") FROM \"{dv}\"");
        var damages = new (string what, string table, string damage, string repair)[]
        {
            ("one character of a ticket's subject", tk,
                $"UPDATE \"{tk}\" SET \"Subject\" = \"Subject\" || 'x' WHERE \"Id\" = {firstTicket}",
                $"UPDATE \"{tk}\" SET \"Subject\" = left(\"Subject\", length(\"Subject\") - 1) WHERE \"Id\" = {firstTicket}"),
            ("a machine's first-seen time, one microsecond later", dv,
                $"UPDATE \"{dv}\" SET \"FirstSeenUtc\" = \"FirstSeenUtc\" + interval '1 microsecond' WHERE \"Id\" = {firstDevice}",
                $"UPDATE \"{dv}\" SET \"FirstSeenUtc\" = \"FirstSeenUtc\" - interval '1 microsecond' WHERE \"Id\" = {firstDevice}"),
            ("a ticket's parent lost", tk,
                $"UPDATE \"{tk}\" SET \"ParentTicketId\" = NULL WHERE \"Id\" = {withParent}",
                $"UPDATE \"{tk}\" SET \"ParentTicketId\" = {parent} WHERE \"Id\" = {withParent}"),
        };
        foreach (var (what, table, damage, repair) in damages)
        {
            PgExec(conn, damage);
            var v = Run(exe, $"copy-to-postgres --from \"{genFile}\" --to \"{conn}\" --verify-only", serverDir, 300000);
            var line = v.output.Split('\n').FirstOrDefault(l => l.Contains($" {table} ")) ?? "";
            int differ = v.output.Split('\n').Count(l => l.TrimStart().StartsWith("DIFFERENT"));
            Check($"D: {what} - DIFFERENT, in {table} alone", v.code != 0 && line.Contains("DIFFERENT") && differ == 1 && v.output.Contains("must NOT be switched"), line.Trim());
            PgExec(conn, repair);
            var ok = Run(exe, $"copy-to-postgres --from \"{genFile}\" --to \"{conn}\" --verify-only", serverDir, 300000);
            Check($"D: ...put right, it is the same again", ok.code == 0 && ok.output.Contains("OK: all"), Last(ok.output));
        }
        // A row gone, from a table nothing points at (so no foreign key stops the delete).
        var leaf = model.GetEntityTypes().Where(t => t.GetTableName() != null && HasId(t.GetTableName()!)
            && !model.GetEntityTypes().SelectMany(o => o.GetForeignKeys()).Any(fk => fk.PrincipalEntityType == t))
            .Select(t => t.GetTableName()!).OrderBy(x => x == T<ChatLine>() ? 0 : 1).ThenBy(x => x).First();
        PgExec(conn, $"DELETE FROM \"{leaf}\" WHERE \"Id\" = (SELECT MAX(\"Id\") FROM \"{leaf}\")");
        var gone = Run(exe, $"copy-to-postgres --from \"{genFile}\" --to \"{conn}\" --verify-only", serverDir, 300000);
        var leafLine = gone.output.Split('\n').FirstOrDefault(l => l.Contains($" {leaf} ")) ?? "";
        Check($"D: a missing row in {leaf} - DIFFERENT, and says how many PostgreSQL has", gone.code != 0 && leafLine.Contains("DIFFERENT") && leafLine.Contains("PostgreSQL has"), leafLine.Trim());

        // The id counters, undone for one table: the "new ids past the copied ones" check above must be able to fail.
        PgExec(conn, $"SELECT setval(pg_get_serial_sequence('\"{T<Tenant>()}\"', 'Id'), 1, false)");
        var env = new Dictionary<string, string> { ["RSP_Platform__DataDirectory"] = Path.Combine(work, "gen"), ["RSP_ConnectionStrings__Postgres"] = conn };
        var clash = Run(exe, "create-tenant \"Clash Check\" clash-check 7", serverDir, 120000, env);
        Check("D: with the company id counter NOT moved, adding a company fails (so the id check can catch it)", clash.code != 0, Last(clash.output));
    }

    // ================= E. text PostgreSQL cannot hold =================
    Console.WriteLine();
    Console.WriteLine("== E: a NUL character in text is refused, named, and nothing copied ==");
    {
        var nulFile = Path.Combine(work, "nul.db");
        File.Copy(genFile, nulFile);
        using (var c = new SqliteConnection($"Data Source={nulFile};Pooling=False"))
        {
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = $"UPDATE \"{T<Client>()}\" SET \"Notes\" = 'before' || char(0) || 'after' WHERE \"Id\" = (SELECT MIN(\"Id\") FROM \"{T<Client>()}\")";
            cmd.ExecuteNonQuery();
        }
        var conn = await NewDb();
        var r = Run(exe, $"copy-to-postgres --from \"{nulFile}\" --to \"{conn}\"", serverDir, 300000);
        Check("E: refused, naming the table and column", r.code != 0 && r.output.Contains("NUL") && r.output.Contains($"{T<Client>()}.Notes"), Last(r.output));
        Check("E: ...and not one row copied", PgScalar(conn, $"SELECT COUNT(*) FROM \"{T<Tenant>()}\"") == 0);
    }
}
catch (Exception ex) { fail++; Console.WriteLine("  FAIL  threw: " + ex); }
finally
{
    NpgsqlConnection.ClearAllPools();
    foreach (var c in made)
    {
        if (keep) Console.WriteLine($"  database kept: {new NpgsqlConnectionStringBuilder(c).Database}");
        else try { await TestPostgres.DropAsync(admin, c); } catch (Exception ex) { Console.WriteLine("  (could not drop a database: " + ex.Message + ")"); }
    }
    if (!keep) try { Directory.Delete(work, true); } catch { }
}

Console.WriteLine();
Console.WriteLine($"{pass} of {pass + fail} checks passed.");
return fail == 0 ? 0 : 1;

// ---------------------------------------------------------------------------------------------
// One SQLite file, copied and proved.
async Task ProveCopy(string label, string file, string dataDir, (string email, string password)? copied)
{
    var before = Sha(file);
    var conn = await NewDb();
    var r = Run(exe, $"copy-to-postgres --from \"{file}\" --to \"{conn}\"", serverDir, 600000);
    Check($"{label}: the copy says OK", r.code == 0 && r.output.Contains("OK: all"), Last(r.output));
    if (r.code != 0) { Console.WriteLine(r.output); return; }
    Check($"{label}: the SQLite file is byte for byte as it was", Sha(file) == before);

    // Independent of the copy's own check: plain SQL on both sides, every table's rows and highest id.
    var s = SqliteCounts(file, tables);
    var wrong = new List<string>();
    foreach (var t in tables)
    {
        var pgN = PgScalar(conn, $"SELECT COUNT(*) FROM \"{t}\"");
        if (pgN != s[t]) wrong.Add($"{t} {s[t]} vs {pgN}");
        if (HasId(t) && SqliteScalar(file, $"SELECT COALESCE(MAX(\"Id\"), 0) FROM \"{t}\"") != PgScalar(conn, $"SELECT COALESCE(MAX(\"Id\"), 0) FROM \"{t}\""))
            wrong.Add($"{t} highest id");
    }
    Check($"{label}: every table has the same rows and highest id, by plain SQL on both sides", wrong.Count == 0,
        wrong.Count == 0 ? $"{tables.Count} tables, {s.Values.Sum()} rows" : string.Join("; ", wrong));

    var again = Run(exe, $"copy-to-postgres --from \"{file}\" --to \"{conn}\"", serverDir, 300000);
    Check($"{label}: a second copy into the same database is refused", again.code != 0 && again.output.Contains("already has rows"), Last(again.output));

    // New rows on the copy: the id counters must have moved past the copied ids.
    var env = new Dictionary<string, string>
    {
        ["RSP_Platform__DataDirectory"] = dataDir, ["RSP_Security__RequireTwoFactor"] = "false", ["RSP_Platform__PublicUrl"] = http,
        ["RSP_ConnectionStrings__Postgres"] = conn, ["ASPNETCORE_URLS"] = http, ["ASPNETCORE_ENVIRONMENT"] = "Development",
    };
    var (maxTenant, maxTech) = (PgScalar(conn, $"SELECT COALESCE(MAX(\"Id\"), 0) FROM \"{T<Tenant>()}\""), PgScalar(conn, $"SELECT COALESCE(MAX(\"Id\"), 0) FROM \"{T<Technician>()}\""));
    var ct = Run(exe, "create-tenant \"Copy Check\" copy-check 7", serverDir, 120000, env);
    var at = Run(exe, "add-tech copy-check after@copy.test copycheck-pw \"After Copy\" --admin", serverDir, 120000, env);
    var newTenant = PgScalar(conn, $"SELECT \"Id\" FROM \"{T<Tenant>()}\" WHERE \"Slug\" = 'copy-check'");
    var newTech = PgScalar(conn, $"SELECT \"Id\" FROM \"{T<Technician>()}\" WHERE lower(\"Email\") = 'after@copy.test'");
    Check($"{label}: a company and a person added on the copy take new ids, past the copied ones",
        ct.code == 0 && at.code == 0 && newTenant > maxTenant && newTech > maxTech,
        ct.code != 0 ? Last(ct.output) : at.code != 0 ? Last(at.output) : $"company {newTenant} (copied up to {maxTenant}), person {newTech} (up to {maxTech})");

    var (server, log) = Start(exe, serverDir, env, http);
    try
    {
        Check($"{label}: the platform starts on the copy", await Up(http) && await Wait(() => log.Any(l => l.Contains("DB: PostgreSQL")), 5000),
            log.FirstOrDefault(l => l.Contains("DB:"))?.Trim() ?? "no start-up line");
        var who = new List<(string email, string password, string what)> { ("after@copy.test", "copycheck-pw", "the person added on the copy") };
        if (copied is { } c) who.Add((c.email, c.password, "a COPIED person (their password hash came across)"));
        using var hc = new HttpClient();
        foreach (var (email, pw, what) in who)
        {
            var lr = await hc.PostAsJsonAsync($"{http}/api/login", new { email, password = pw });
            var body = await lr.Content.ReadAsStringAsync();
            var token = lr.IsSuccessStatusCode && JsonDocument.Parse(body).RootElement.TryGetProperty("token", out var tt) ? tt.GetString() ?? "" : "";
            var q = new ConcurrentQueue<Msg>();
            if (token.Length > 0)
            {
                var raw = new ClientWebSocket();
                await raw.ConnectAsync(new Uri($"{ws}/ws/tech?token={token}"), default);
                var tech = new WsConnection(raw); tech.OnMessage += m => q.Enqueue(m); _ = tech.RunAsync();
                await Wait(() => q.OfType<StateMsg>().Any(), 5000);
            }
            Check($"{label}: {what} signs in and gets their console's state", token.Length > 0 && q.OfType<StateMsg>().Any(), token.Length > 0 ? "" : body);
        }
    }
    finally { Stop(server); }
    Check($"{label}: ...and the SQLite file is still untouched after the platform ran on PostgreSQL", Sha(file) == before);
}

async Task<string> NewDb() { var c = await TestPostgres.CreateAsync(admin!, "rsp_pgcopy"); made.Add(c); return c; }

bool HasId(string table) => model.GetEntityTypes().First(t => t.GetTableName() == table).FindPrimaryKey()!.Properties is { Count: 1 } k && k[0].Name == "Id";

static PlatformDb Sqlite(string file, bool readOnly) =>
    new(new DbContextOptionsBuilder<PlatformDb>().UseSqlite($"Data Source={file};Pooling=False{(readOnly ? ";Mode=ReadOnly" : "")}").Options);

static Dictionary<string, long> SqliteCounts(string file, List<string> tables) => tables.ToDictionary(t => t, t => SqliteScalar(file, $"SELECT COUNT(*) FROM \"{t}\""));
static long SqliteScalar(string file, string sql)
{
    using var c = new SqliteConnection($"Data Source={file};Mode=ReadOnly;Pooling=False");
    c.Open();
    using var cmd = c.CreateCommand(); cmd.CommandText = sql;
    return Convert.ToInt64(cmd.ExecuteScalar() ?? 0L);
}
static long PgScalar(string conn, string sql)
{
    using var c = new NpgsqlConnection(conn); c.Open();
    using var cmd = new NpgsqlCommand(sql, c);
    var v = cmd.ExecuteScalar();
    return v is null or DBNull ? 0 : Convert.ToInt64(v);
}
static void PgExec(string conn, string sql)
{
    using var c = new NpgsqlConnection(conn); c.Open();
    using var cmd = new NpgsqlCommand(sql, c);
    cmd.ExecuteNonQuery();
}
static string Sha(string file) { using var f = File.OpenRead(file); return Convert.ToHexString(SHA256.HashData(f)); }
static string Last(string output) => output.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.Length > 0) ?? "";

// ---------------------------------------------------------------------------------------------
// Four rows in every table, each column given a value chosen to break a careless copy.
static async Task Fill(string file, IModel model)
{
    await using var db = Sqlite(file, readOnly: false);
    await db.Database.MigrateAsync();
    var rows = new Dictionary<IEntityType, List<object>>();
    var order = new List<IEntityType>();
    var all = db.Model.GetEntityTypes().Where(t => t.GetTableName() != null).ToList();
    while (order.Count < all.Count)
        order.AddRange(all.Where(t => !order.Contains(t) && t.GetForeignKeys().All(fk => fk.PrincipalEntityType == t || order.Contains(fk.PrincipalEntityType))).ToList() is { Count: > 0 } next
            ? next : throw new InvalidOperationException("foreign keys in a circle"));
    foreach (var t in order)
    {
        rows[t] = new List<object>();
        for (int n = 0; n < 4; n++)
        {
            var o = Activator.CreateInstance(t.ClrType)!;
            var e = db.Entry(o);
            foreach (var p in t.GetProperties())
            {
                var fk = p.GetContainingForeignKeys().FirstOrDefault();
                if (fk != null)
                {
                    var principal = fk.PrincipalEntityType == t ? rows[t] : rows[fk.PrincipalEntityType];
                    var nullable = p.IsNullable;
                    // n 0: a first row (in its own table, with no parent); then each row points at a different principal.
                    if (fk.PrincipalEntityType == t && n == 0 || principal.Count == 0 || nullable && n == 3) { e.Property(p.Name).CurrentValue = null; continue; }
                    var target = fk.PrincipalEntityType == t ? principal[0] : principal[n % principal.Count];
                    var pk = fk.PrincipalKey.Properties[fk.Properties.ToList().IndexOf(p)];
                    e.Property(p.Name).CurrentValue = db.Entry(target).Property(pk.Name).CurrentValue;
                    continue;
                }
                if (p.IsPrimaryKey() && p.ValueGenerated != Microsoft.EntityFrameworkCore.Metadata.ValueGenerated.Never && IsNumber(p.ClrType)) continue;
                e.Property(p.Name).CurrentValue = Awkward(p, n);
            }
            db.Add(o);
            await db.SaveChangesAsync();
            rows[t].Add(o);
        }
    }

    static bool IsNumber(Type t) => t == typeof(int) || t == typeof(long) || t == typeof(short);
    static object? Awkward(IProperty p, int n)
    {
        var type = Nullable.GetUnderlyingType(p.ClrType) ?? p.ClrType;
        if (p.IsNullable && n == 3) return null;
        var tag = $"{p.DeclaringType.ClrType.Name}.{p.Name}.{n}";
        if (type == typeof(string))
        {
            var s = n switch
            {
                0 => $"Ångström café - naïve 😀 'single' \"double\" back\\slash {tag}",
                1 => $"line one\r\nline two\ttab {tag}",
                2 => "",
                _ => tag,
            };
            var max = p.GetMaxLength();
            return max is { } m && s.Length > m ? $"{n}{Guid.NewGuid():N}"[..m] : s;
        }
        if (type == typeof(DateTime)) return n switch
        {
            0 => new DateTime(2026, 10, 9, 12, 34, 56, DateTimeKind.Utc).AddTicks(1234567), // below a microsecond
            1 => DateTime.SpecifyKind(DateTime.MinValue, DateTimeKind.Utc),
            2 => DateTime.SpecifyKind(DateTime.MaxValue, DateTimeKind.Utc),
            _ => new DateTime(1999, 12, 31, 23, 59, 59, DateTimeKind.Utc).AddTicks(9999999),
        };
        if (type == typeof(int)) return n switch { 0 => int.MaxValue - 3, 1 => -1, 2 => 0, _ => 42 + p.Name.Length };
        if (type == typeof(long)) return n switch { 0 => long.MaxValue - 3, 1 => -1L, 2 => 0L, _ => 4_000_000_000L };
        if (type == typeof(short)) return (short)(n * 7);
        if (type == typeof(byte)) return (byte)(n * 60);
        if (type == typeof(double)) return n switch { 0 => 0.1 + 0.2, 1 => 1e-300, 2 => 0.0, _ => Math.PI * 1e10 };
        if (type == typeof(float)) return n switch { 0 => 0.1f, 1 => 1e-30f, 2 => 0f, _ => 3.14159f };
        if (type == typeof(decimal)) return n switch { 0 => 123456789.123456789m, 1 => 0.10m, 2 => -1m, _ => 0.000001m };
        if (type == typeof(bool)) return n % 2 == 0;
        if (type == typeof(Guid)) return Guid.NewGuid();
        if (type == typeof(byte[])) return n == 2 ? Array.Empty<byte>() : RandomNumberGenerator.GetBytes(40 + n);
        if (type.IsEnum) { var v = Enum.GetValues(type); return v.GetValue(n % v.Length); }
        if (type == typeof(TimeSpan)) return TimeSpan.FromTicks(123456789 + n);
        if (type == typeof(DateOnly)) return new DateOnly(2026, 10, 9).AddDays(n);
        if (type == typeof(TimeOnly)) return new TimeOnly(6, 0).Add(TimeSpan.FromMinutes(n));
        if (type == typeof(List<string>)) return n == 2 ? new List<string>() : new List<string> { "a", tag, "ü" };
        if (type == typeof(List<int>)) return n == 2 ? new List<int>() : new List<int> { n, -n, int.MaxValue };
        throw new InvalidOperationException($"pgcopycheck does not know how to fill {tag} ({p.ClrType.Name}) - teach Awkward()");
    }
}

// ---------------------------------------------------------------------------------------------
static (Process p, ConcurrentQueue<string> log) Start(string exe, string dir, Dictionary<string, string> env, string http)
{
    var psi = new ProcessStartInfo(exe, $"run --urls {http}") { WorkingDirectory = dir, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
    foreach (var kv in env) psi.Environment[kv.Key] = kv.Value;
    var p = Process.Start(psi)!;
    var log = new ConcurrentQueue<string>();
    p.OutputDataReceived += (_, e) => { if (e.Data != null) log.Enqueue(e.Data); };
    p.ErrorDataReceived += (_, e) => { if (e.Data != null) log.Enqueue(e.Data); };
    p.BeginOutputReadLine(); p.BeginErrorReadLine();
    return (p, log);
}
static void Stop(Process p) { try { p.Kill(true); p.WaitForExit(5000); } catch { } }
static async Task<bool> Up(string http)
{
    using var probe = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
    for (int i = 0; i < 60; i++) { try { if ((await probe.GetAsync($"{http}/health")).IsSuccessStatusCode) return true; } catch { } await Task.Delay(500); }
    return false;
}
static async Task<bool> Wait(Func<bool> f, int ms)
{ var e = DateTime.UtcNow.AddMilliseconds(ms); while (DateTime.UtcNow < e) { if (f()) return true; await Task.Delay(50); } return f(); }
static (int code, string output) Run(string file, string argLine, string cwd, int ms, Dictionary<string, string>? env = null)
{
    var p0 = new ProcessStartInfo(file, argLine) { WorkingDirectory = cwd, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
    if (env != null) foreach (var kv in env) p0.Environment[kv.Key] = kv.Value;
    using var p = Process.Start(p0)!;
    var err = p.StandardError.ReadToEndAsync();
    var o = p.StandardOutput.ReadToEnd() + err.Result;
    p.WaitForExit(ms);
    return (p.HasExited ? p.ExitCode : -1, o);
}
static string FindRepoRoot()
{
    var d = new DirectoryInfo(AppContext.BaseDirectory);
    for (int i = 0; i < 8 && d != null; i++, d = d.Parent)
        if (Directory.Exists(Path.Combine(d.FullName, "src", "RemoteSupport.Platform"))) return d.FullName;
    throw new InvalidOperationException("could not find the repository root");
}
