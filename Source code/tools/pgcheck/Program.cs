using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RemoteSupport.Platform.Data;
using RemoteSupport.Shared;
using RemoteSupport.Tools;

// ---------------------------------------------------------------------------------------------
// pgcheck - the platform on PostgreSQL (2026-10-08, HANDOVER §118).
//
// Part 1, always: both migration sets still match the model — a session that adds a SQLite migration
// may not know a PostgreSQL one is needed beside it, so this is what notices — and PgInitial was never
// made again (frozen 2026-10-09: PostgreSQL servers hold data now).
//
// Part 2, when a PostgreSQL is set up (tools/Setup-TestPostgres.ps1): a fresh database of its own, the
// platform started on it, and the whole path a real day takes — the command line, a technician signing
// in, a machine saying hello, a session that writes its ticket — read back through PgPlatformDb, then a
// restart to prove migrations run once and data stays. The database is dropped after (--keep to keep it).
//
//   dotnet run -c Release --project tools/pgcheck/pgcheck.csproj
// ---------------------------------------------------------------------------------------------

const int Port = 8141;
string http = $"http://127.0.0.1:{Port}", ws = $"ws://127.0.0.1:{Port}";
int pass = 0, fail = 0;
void Check(string what, bool ok, string detail = "")
{
    if (ok) pass++; else fail++;
    Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}  {what}{(detail.Length > 0 ? " — " + detail : "")}");
}
var repo = FindRepoRoot();
var project = Path.Combine(repo, "src", "RemoteSupport.Platform", "RemoteSupport.Platform.csproj");

Console.WriteLine("pgcheck - the platform on PostgreSQL");
Console.WriteLine();
Console.WriteLine("== part 1: both migration sets match the model ==");
var built = Run("dotnet", $"build \"{project}\" -v q --nologo", repo, 600000);
if (built.code != 0) { Console.WriteLine(built.output); return 2; }
foreach (var ctx in new[] { "PgPlatformDb", "PlatformDb" })
{
    var r = Run("dotnet", $"ef migrations has-pending-model-changes --no-build --context {ctx} --project \"{project}\"", repo, 300000);
    var why = ctx == "PgPlatformDb"
        ? "a model change has no PostgreSQL migration: dotnet ef migrations add <the SQLite one's name> --context PgPlatformDb --output-dir Data/PgMigrations"
        : "a model change has no SQLite migration: dotnet ef migrations add <Name> --context PlatformDb";
    Check($"{ctx}: no model change without a migration", r.code == 0, r.code == 0 ? "" : why);
}
// PgInitial is FROZEN (2026-10-09, step 4 of §118): once a server holds data in PostgreSQL, a PgInitial made
// again has a new id, and that server would try to create every table a second time and refuse to start.
// Every model change gets a PostgreSQL migration of its own beside the SQLite one instead.
var pgFirst = Directory.GetFiles(Path.Combine(repo, "src", "RemoteSupport.Platform", "Data", "PgMigrations"), "*.Designer.cs")
    .Select(f => Path.GetFileName(f).Replace(".Designer.cs", "")).OrderBy(n => n, StringComparer.Ordinal).FirstOrDefault();
Check("the PostgreSQL migrations still begin with 20261009072435_PgInitial (never made again)", pgFirst == "20261009072435_PgInitial",
    pgFirst == "20261009072435_PgInitial" ? "" : $"they begin with {pgFirst ?? "nothing"} - put PgInitial back from git and add a migration instead");

var admin = TestPostgres.Admin();
if (admin == null)
{
    Console.WriteLine();
    Console.WriteLine("Part 2 skipped: no PostgreSQL set up on this PC (run tools\\Setup-TestPostgres.ps1 once).");
    Console.WriteLine($"{pass} of {pass + fail} checks passed.");
    return fail == 0 ? 0 : 1;
}

Console.WriteLine();
Console.WriteLine("== part 2: the platform on a fresh PostgreSQL database ==");
var conn = await TestPostgres.CreateAsync(admin, "rsp_pgcheck");
Console.WriteLine($"  database {new Npgsql.NpgsqlConnectionStringBuilder(conn).Database}");
var work = Path.Combine(Path.GetTempPath(), "rs-pgcheck-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
var dataDir = Path.Combine(work, "data"); Directory.CreateDirectory(dataDir);
var serverDir = Path.Combine(work, "server");
try
{
    var b = Run("dotnet", $"build -c Release \"{project}\" -v q --nologo -p:OutDir=\"{serverDir}\\\"", repo, 600000);
    if (b.code != 0) { Console.WriteLine(b.output); return 2; }
    var exe = Path.Combine(serverDir, "RemoteSupport.Platform.exe");
    var env = new Dictionary<string, string>
    {
        ["RSP_Platform__DataDirectory"] = dataDir, ["RSP_Security__RequireTwoFactor"] = "false", ["RSP_Platform__PublicUrl"] = http,
        ["RSP_ConnectionStrings__Postgres"] = conn, ["ASPNETCORE_URLS"] = http, ["ASPNETCORE_ENVIRONMENT"] = "Development",
    };

    // ---- the command line, on PostgreSQL ----
    string tenantKey = "";
    foreach (var cmd in new[] { "create-tenant \"Pg Check\" pg-check 7", "add-tech pg-check one@local.test localtest-pg \"Tech One\" --admin" })
    {
        var r = Run(exe, cmd, serverDir, 120000, env);
        Check($"command line: {cmd.Split(' ')[0]}", r.code == 0, r.code == 0 ? "" : r.output.Trim());
        var line = r.output.Split('\n').FirstOrDefault(l => l.StartsWith("Tenant key:"));
        if (line != null) tenantKey = line["Tenant key:".Length..].Trim();
    }
    Check("SQLite was never touched (no platform.db made)", !File.Exists(Path.Combine(dataDir, "platform.db")));

    // ---- the server ----
    var (server, log) = Start(exe, serverDir, env, http);
    Check("the platform starts on PostgreSQL and says so", await Up(http) && await Wait(() => log.Any(l => l.Contains("DB: PostgreSQL")), 5000),
        log.FirstOrDefault(l => l.Contains("DB:"))?.Trim() ?? "no start-up line");

    using var hc = new HttpClient();
    var lr = await hc.PostAsJsonAsync($"{http}/api/login", new { email = "one@local.test", password = "localtest-pg" });
    var body = await lr.Content.ReadAsStringAsync();
    var token = lr.IsSuccessStatusCode && JsonDocument.Parse(body).RootElement.TryGetProperty("token", out var t) ? t.GetString() ?? "" : "";
    Check("a technician signs in (the password hash round-trips)", token.Length > 0, token.Length > 0 ? "" : body);

    var techQ = new ConcurrentQueue<Msg>();
    var techRaw = new ClientWebSocket();
    await techRaw.ConnectAsync(new Uri($"{ws}/ws/tech?token={token}"), default);
    var tech = new WsConnection(techRaw); tech.OnMessage += m => techQ.Enqueue(m); _ = tech.RunAsync();
    Check("their console gets its machine list", await Wait(() => techQ.OfType<StateMsg>().Any(), 5000));

    // ---- a machine, and a session that writes its ticket ----
    var hostConnected = false;
    var devRaw = new ClientWebSocket();
    await devRaw.ConnectAsync(new Uri($"{ws}/ws/device"), default);
    var dev = new WsConnection(devRaw);
    dev.OnMessage += m =>
    {
        if (m is StartSessionMsg s && s.HostToken.Length > 0) _ = Task.Run(async () =>
        {
            var hr = new ClientWebSocket();
            await hr.ConnectAsync(new Uri($"{ws}/ws/session?role=host&id={s.SessionId}&token={s.HostToken}"), default);
            var h = new WsConnection(hr); _ = h.RunAsync(); hostConnected = true;
            h.Send(new MonitorsMsg { Monitors = new List<MonitorInfo> { new() { Index = 0, Width = 256, Height = 256, Primary = true } } });
        });
    };
    _ = dev.RunAsync();
    dev.Send(new HelloMsg
    {
        Role = DeviceRole.Client, DeviceId = "pg-machine-1", DeviceKey = "k-pg-1", MachineName = "PG-PC", UserName = "PgUser",
        Os = "Windows 11 (pretend)", Version = "0.0-test", TenantKey = tenantKey,
        Spec = new SystemSpec { Cpu = "Pretend", Cores = 1, RamMb = 1024, DiskTotalGb = 10, DiskFreeGb = 5, Os = "Windows 11 (pretend)" },
    });
    Check("a machine connects and shows online", await Wait(() => techQ.OfType<DeviceUpdateMsg>().Any(u => u.Device.DeviceId == "pg-machine-1" && u.Device.Online), 5000));

    tech.Send(new ConnectDeviceMsg { DeviceId = "pg-machine-1", Reason = "pgcheck: a session on PostgreSQL" });
    var ready = await WaitFor<SessionReadyMsg>(techQ, m => m.Session.DeviceId == "pg-machine-1", 8000);
    Check("a session starts", ready != null && await Wait(() => hostConnected, 5000));
    if (ready != null) tech.Send(new EndSessionMsg { SessionId = ready.Session.SessionId });
    await Task.Delay(4500); // EndSession gives the host its last words, then writes "Session ended"

    // ---- read back through PgPlatformDb ----
    var opts = new DbContextOptionsBuilder<PgPlatformDb>().UseNpgsql(conn).Options;
    await using (var db = new PgPlatformDb(opts))
    {
        var device = await db.Devices.FirstOrDefaultAsync(d => d.DeviceId == "pg-machine-1");
        Check("the machine is stored", device != null, device == null ? "" : $"{device.MachineName}, first seen {device.FirstSeenUtc:u}");
        Check("its time comes back as UTC, and as now", device != null && device.FirstSeenUtc.Kind == DateTimeKind.Utc && Math.Abs((DateTime.UtcNow - device.FirstSeenUtc).TotalMinutes) < 2,
            device == null ? "" : $"{device.FirstSeenUtc.Kind}, {(DateTime.UtcNow - device.FirstSeenUtc).TotalSeconds:0} s ago");
        var ticket = await db.Tickets.Where(x => x.DeviceId == "pg-machine-1").OrderByDescending(x => x.Id).FirstOrDefaultAsync();
        Check("the session wrote its ticket", ticket != null, ticket == null ? "" : $"#{ticket.Number}: {ticket.Subject}");
        Check("the session is logged", await db.Sessions.AnyAsync(s => s.DeviceId == "pg-machine-1"));

        // The case rule every client and contact search uses (ToLower on both sides): Smith matches smith.
        var tenantId = (await db.Tenants.FirstAsync(x => x.Slug == "pg-check")).Id;
        db.Clients.Add(new Client { TenantId = tenantId, Name = "Smith Plumbing" });
        await db.SaveChangesAsync();
        var s = "smith";
        Check("a search in lower case finds a name in capitals", await db.Clients.AnyAsync(c => c.TenantId == tenantId && c.Name.ToLower().Contains(s)));
    }
    Stop(server);

    // ---- a restart: the migration runs once, the data stays ----
    // Read from the database's own history table, not the log: the platform logs EF at Warning, so a log
    // line saying "Applying migration" would never be printed either way (the first version of this check).
    async Task<(long history, long tickets)> Counts()
    {
        await using var c = new Npgsql.NpgsqlConnection(conn);
        await c.OpenAsync();
        await using var h = new Npgsql.NpgsqlCommand("SELECT count(*) FROM \"__EFMigrationsHistory\"", c);
        await using var t = new Npgsql.NpgsqlCommand("SELECT count(*) FROM \"Tickets\"", c);
        return ((long)(await h.ExecuteScalarAsync())!, (long)(await t.ExecuteScalarAsync())!);
    }
    var before = await Counts();
    int pgMigrations = Directory.GetFiles(Path.Combine(repo, "src", "RemoteSupport.Platform", "Data", "PgMigrations"), "*.Designer.cs").Length;
    Check("the database records exactly the PostgreSQL migrations there are", before.history == pgMigrations, $"{before.history} recorded, {pgMigrations} in Data/PgMigrations");
    var (again, _) = Start(exe, serverDir, env, http);
    Check("it starts again on the same database", await Up(http));
    var after = await Counts();
    Check("...without applying any migration a second time, and with its tickets", after == before, $"history {before.history} → {after.history}, tickets {before.tickets} → {after.tickets}");
    var lr2 = await hc.PostAsJsonAsync($"{http}/api/login", new { email = "one@local.test", password = "localtest-pg" });
    Check("...and the technician can still sign in", lr2.IsSuccessStatusCode);
    Stop(again);
}
catch (Exception ex) { fail++; Console.WriteLine("  FAIL  threw: " + ex); }
finally
{
    if (args.Contains("--keep")) Console.WriteLine($"  database kept: {new Npgsql.NpgsqlConnectionStringBuilder(conn).Database}");
    else { try { await TestPostgres.DropAsync(admin, conn); } catch (Exception ex) { Console.WriteLine("  (could not drop the database: " + ex.Message + ")"); } }
}

Console.WriteLine();
Console.WriteLine($"{pass} of {pass + fail} checks passed.");
return fail == 0 ? 0 : 1;

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
static async Task<T?> WaitFor<T>(ConcurrentQueue<Msg> q, Func<T, bool> f, int ms) where T : Msg
{ var e = DateTime.UtcNow.AddMilliseconds(ms); while (DateTime.UtcNow < e) { foreach (var m in q) if (m is T t && f(t)) return t; await Task.Delay(50); } return null; }
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
