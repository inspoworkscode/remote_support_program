using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using RemoteSupport.Platform.Data;
using RemoteSupport.Platform.Services;
using RemoteSupport.Shared;
using RemoteSupport.Tools;

// ---------------------------------------------------------------------------------------------
// requestedname - the name given at a support request becomes the machine's name in the lists (2026-10-18).
//
// Clint: "techs and staff are not immediately linking machines to clients, so very quickly the system can become
// messy... as soon as a client puts their name in when requesting support, this is the user name that shows in the
// console and portal for that machine. It then changes when the contact or client is created and the machine gets
// manually linked to them." His answers: the LATEST name wins; it is MARKED as only what they typed; linking SUGGESTS
// people on file by that name and offers it as a new contact; machines that asked before take it from past tickets.
//
// The rule itself; then pretend tray apps and a pretend console against a real platform (the name arriving, the
// latest winning, a blank changing nothing, a first install asking at once, a restart, linking as a new contact,
// unlinking); the backfill's SQL run on this database; and the portal's Devices page and dashboard.
//
// Its own platform on port 8211; the live server is never touched. RSP_TEST_DB=postgres runs it on PostgreSQL.
//
//   dotnet run -c Release --project tools/requestedname/requestedname.csproj
// ---------------------------------------------------------------------------------------------

const int Port = 8211;
string http = $"http://127.0.0.1:{Port}", ws = $"ws://127.0.0.1:{Port}";

int pass = 0, fail = 0;
void Check(string what, bool ok, string detail = "")
{
    if (ok) { pass++; Console.WriteLine($"  PASS  {what}{(detail.Length > 0 ? " — " + detail : "")}"); }
    else { fail++; Console.WriteLine($"  FAIL  {what}{(detail.Length > 0 ? " — " + detail : "")}"); }
}
void Log(string s) => Console.WriteLine($"{DateTime.Now:HH:mm:ss.fff}  {s}");

Console.WriteLine($"requestedname - the name they gave, until somebody links the machine ({HarnessDb.Name})");
Console.WriteLine();

// ================================================================ the rule
Console.WriteLine("== the rule ==");
Check("a person on file wins", OwnerNames.For("Sue Jones", "Zenith", "John Smith", "frontdesk") == "Sue Jones");
Check("then the company — linking to a client alone replaces the given name", OwnerNames.For("", "Zenith", "John Smith", "frontdesk") == "Zenith");
Check("then the name given at a support request", OwnerNames.For("", "", "John Smith", "frontdesk") == "John Smith");
Check("then Windows' own sign-in, as before", OwnerNames.For("", "", "  ", "frontdesk") == "frontdesk");
Check("marked only while nobody has linked it", OwnerNames.IsRequested("", "", "John Smith")
    && !OwnerNames.IsRequested("", "Zenith", "John Smith") && !OwnerNames.IsRequested("Sue", "Zenith", "John Smith")
    && !OwnerNames.IsRequested("", "", " "));
Check("the server's rule IS the console's", ContactService.OwnerName("", "", "John Smith", "x")
    == new DeviceInfo { RequestedBy = "John Smith", UserName = "x" }.OwnerName);
Check("spaces tidied", OwnerNames.Clean("  John    Smith ") == "John Smith", OwnerNames.Clean("  John    Smith "));
Check("a name, not an essay: at most 100 characters", OwnerNames.Clean(new string('a', 150)).Length == OwnerNames.MaxLength);
Check("same name, any case: the best fit", OwnerNames.Likeness("john smith", "John  Smith") == 2);
Check("every word of one in the other: a fit", OwnerNames.Likeness("John", "John Smith") == 1 && OwnerNames.Likeness("Smith John", "John Smith") == 1);
Check("a different person: no fit", OwnerNames.Likeness("John Smith", "Johnny Smith") == 0 && OwnerNames.Likeness("John Smith", "") == 0);
var people = new List<Contact>
{
    new() { Id = 1, Name = "Johnny Smith" }, new() { Id = 2, Name = "John" }, new() { Id = 3, Name = "john smith" },
    new() { Id = 4, Name = "John Smith", Archived = true }, new() { Id = 5, Name = "Mary Jones" },
};
var sugg = ContactService.Suggestions(people, "John Smith");
Check("suggestions: the exact name first, a partial after, nobody else, nobody archived",
    sugg.Select(c => c.Id).SequenceEqual(new[] { 3, 2 }), string.Join(",", sugg.Select(c => c.Id)));

// ================================================================ a real platform
var repo = FindRepoRoot();
var work = Path.Combine(Path.GetTempPath(), "rs-requestedname-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
var dataDir = Path.Combine(work, "data");
Directory.CreateDirectory(dataDir);
var serverDir = Path.Combine(work, "server");
Log($"building the platform into {serverDir}");
var b = Run("dotnet", $"build -c Release \"{Path.Combine(repo, "src", "RemoteSupport.Platform", "RemoteSupport.Platform.csproj")}\" -v q --nologo -p:OutDir=\"{serverDir}\\\"", repo, 300000);
if (b.code != 0) { Console.WriteLine(b.output); Console.WriteLine("the platform would not build"); return 2; }
var exe = Path.Combine(serverDir, "RemoteSupport.Platform.exe");
var env = new Dictionary<string, string>
{
    ["RSP_Platform__DataDirectory"] = dataDir, ["RSP_Security__RequireTwoFactor"] = "false", ["RSP_Platform__PublicUrl"] = http,
    ["ASPNETCORE_URLS"] = http, ["ASPNETCORE_ENVIRONMENT"] = "Development",
    // Holds a machine's first hello between looking its row up and saving it, so a request sent at once meets an
    // unsaved record on SQLite too — the first-install case below (tools/firsthello's switch).
    ["RSP_Testing__PersistDeviceGapMs"] = "800",
};
HarnessDb.Use(env, dataDir);
string tenantKey = "";
foreach (var cmd in new[] { "create-tenant \"Requested Name\" requested-name 5", "approve requested-name", "add-tech requested-name sue@local.test localtest \"Sue Tech\" --admin" })
{
    var r = Run(exe, cmd, serverDir, 60000, env);
    if (r.code != 0) { Console.WriteLine(r.output); Console.WriteLine("CLI step failed: " + cmd); return 2; }
    var line = r.output.Split('\n').FirstOrDefault(l => l.StartsWith("Tenant key:"));
    if (line != null) tenantKey = line["Tenant key:".Length..].Trim();
}
if (tenantKey.Length == 0) { Console.WriteLine("could not read the tenant key"); return 2; }

Process? server = null;
async Task<bool> StartAsync()
{
    var psi = new ProcessStartInfo(exe, $"run --urls {http}") { WorkingDirectory = serverDir, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
    foreach (var kv in env) psi.Environment[kv.Key] = kv.Value;
    server = Process.Start(psi)!;
    // The server's own words kept beside its database, for when a check fails.
    var log = Path.Combine(work, "server.log");
    void Keep(string? line) { if (line != null) lock (work) File.AppendAllText(log, line + Environment.NewLine); }
    server.OutputDataReceived += (_, e) => Keep(e.Data); server.ErrorDataReceived += (_, e) => Keep(e.Data);
    server.BeginOutputReadLine(); server.BeginErrorReadLine();
    using var probe = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
    for (int i = 0; i < 60; i++) { try { if ((await probe.GetAsync($"{http}/health")).IsSuccessStatusCode) return true; } catch { } await Task.Delay(500); }
    return false;
}
void Kill() { try { if (server != null && !server.HasExited) server.Kill(true); server?.WaitForExit(5000); } catch { } }
if (!await StartAsync()) { Console.WriteLine("the platform did not come up"); Kill(); return 2; }

try { await ChecksAsync(); }
catch (Exception ex) { fail++; Console.WriteLine("  FAIL  threw: " + ex); }
finally { Kill(); }

Console.WriteLine();
Console.WriteLine($"{pass} of {pass + fail} checks passed.");
Console.WriteLine($"server and database kept at {work}");
return fail == 0 ? 0 : 1;

async Task ChecksAsync()
{
    int tenantId;
    await using (var db = Db()) tenantId = (await db.Tenants.FirstAsync(t => t.Slug == "requested-name")).Id;
    var sue = await Tech.OpenAsync(http, ws, "sue@local.test");

    // ================================================================ the name arrives
    Console.WriteLine();
    Console.WriteLine("== a support request names the machine ==");
    var pc = new FakeMachine(ws, "dsk-1", "DESKTOP123", tenantKey);
    await pc.ConnectAsync();
    await Task.Delay(1200);
    var before = sue.Latest(pc.Id);
    Check("before anybody asks for help: Windows' sign-in, unmarked", before?.OwnerName == "FakeUser" && before.NamedByRequest == false,
        before == null ? "no device" : $"{before.OwnerName} marked={before.NamedByRequest}");

    var asked = DateTime.UtcNow;
    pc.Send(new RequestCallMsg { ContactName = "  John    Smith ", Issue = "Printer offline" });
    var named = await sue.WaitForDevice(pc.Id, d => d.RequestedBy == "John Smith", 5000);
    Check("every open console is told at once: the name they gave", named?.OwnerName == "John Smith", named?.OwnerName ?? "no update");
    Check("...marked as only what they typed", named?.NamedByRequest == true);
    Check("...with when", named?.RequestedByUtc is { } when && Math.Abs((when.UtcDateTime - asked).TotalMinutes) < 2, named?.RequestedByUtc?.ToString("o") ?? "none");
    var row = await DeviceAsync(pc.Id);
    Check("kept on the machine's record", row?.RequestedBy == "John Smith", row?.RequestedBy ?? "no row");
    Check("...its time in UTC, not shifted by the server's zone (§108)",
        row?.RequestedByUtc is { } stored && Math.Abs((DateTime.SpecifyKind(stored, DateTimeKind.Utc) - asked).TotalMinutes) < 2,
        row?.RequestedByUtc?.ToString("o") ?? "none");

    pc.Send(new RequestCallMsg { ContactName = "Mary Jones", Issue = "Outlook" });
    var later = await sue.WaitForDevice(pc.Id, d => d.RequestedBy == "Mary Jones", 5000);
    Check("asked again by somebody else: the LATEST name wins", later?.OwnerName == "Mary Jones", later?.OwnerName ?? "no update");

    pc.Send(new RequestCallMsg { ContactName = "   ", Issue = "Nobody said" });
    await Task.Delay(1500);
    Check("a blank name changes nothing", (await DeviceAsync(pc.Id))?.RequestedBy == "Mary Jones" && sue.Latest(pc.Id)?.RequestedBy == "Mary Jones",
        (await DeviceAsync(pc.Id))?.RequestedBy ?? "");

    // ================================================================ a first install asks at once
    Console.WriteLine();
    Console.WriteLine("== a brand-new install asks for help before its record is saved ==");
    var fresh = new FakeMachine(ws, "dsk-2", "DESKTOP456", tenantKey);
    await fresh.ConnectAsync(settle: false);
    // As soon as the hub has it — the consoles are told it is online — and well inside the 800 ms its record takes to
    // be saved. Not before: a message sent in the first milliseconds behind a hello is dropped by /ws/device while it
    // looks the company up (BUGS #13), which no real tray app does — the person has a form to fill in first.
    var online = await sue.WaitForDevice(fresh.Id, _ => true, 3000);
    fresh.Send(new RequestCallMsg { ContactName = "Peter Pan", Issue = "First day" });
    int rowsThen; await using (var db = Db()) rowsThen = await db.Devices.CountAsync(d => d.DeviceId == fresh.Id);
    Check("the request really did arrive before the machine's record was saved", online != null && rowsThen == 0,
        online == null ? "never came online" : $"{rowsThen} row(s) when it was sent");
    var first = await sue.WaitForDevice(fresh.Id, d => d.RequestedBy == "Peter Pan", 6000);
    await Task.Delay(1000);
    Check("the name is not lost to the record being saved under it", (await DeviceAsync(fresh.Id))?.RequestedBy == "Peter Pan",
        (await DeviceAsync(fresh.Id))?.RequestedBy ?? "no row");
    Check("...and the consoles show it", first?.OwnerName == "Peter Pan" && sue.Latest(fresh.Id)?.RequestedBy == "Peter Pan",
        sue.Latest(fresh.Id)?.OwnerName ?? "");

    // ================================================================ a restart
    Console.WriteLine();
    Console.WriteLine("== the server restarts ==");
    sue.Dispose(); pc.Drop(); fresh.Drop();
    Kill();
    if (!await StartAsync()) { Check("the platform comes back", false); return; }
    sue = await Tech.OpenAsync(http, ws, "sue@local.test");
    var state = await sue.WaitFor<StateMsg>(_ => true, 5000);
    var afterRestart = state?.Devices.FirstOrDefault(d => d.DeviceId == "dsk-1");
    Check("a machine that is OFF still shows the name it gave, marked", afterRestart?.OwnerName == "Mary Jones" && afterRestart.NamedByRequest,
        afterRestart == null ? "not in the list" : $"{afterRestart.OwnerName} marked={afterRestart.NamedByRequest}");
    pc = new FakeMachine(ws, "dsk-1", "DESKTOP123", tenantKey);
    await pc.ConnectAsync();
    await Task.Delay(1500);
    Check("...and still does once it is back", sue.Latest(pc.Id)?.OwnerName == "Mary Jones", sue.Latest(pc.Id)?.OwnerName ?? "");

    // ================================================================ linking it
    Console.WriteLine();
    Console.WriteLine("== linking it, as a new contact by that name ==");
    int acme, zenith;
    await using (var db = Db())
    {
        var a = new Client { TenantId = tenantId, Name = "Acme Ltd" }; var z = new Client { TenantId = tenantId, Name = "Zenith Motors" };
        db.Clients.AddRange(a, z);
        await db.SaveChangesAsync();
        acme = a.Id; zenith = z.Id;
        db.Contacts.Add(new Contact { TenantId = tenantId, ClientId = acme, Name = "Peter Pan" });
        await db.SaveChangesAsync();
    }
    sue.Send(new DeviceLinkMsg { DeviceId = pc.Id, ClientId = acme, NewContactName = "Mary Jones" });
    var linked = await sue.WaitForDevice(pc.Id, d => d.ContactName == "Mary Jones", 5000);
    Check("added as a contact at the client chosen, and the machine linked to her", linked?.ClientName == "Acme Ltd" && linked.ContactId > 0,
        linked == null ? "no update" : $"{linked.ContactName} at {linked.ClientName}");
    Check("...no longer marked: she is a contact now", linked?.NamedByRequest == false && linked.OwnerName == "Mary Jones");
    var other = new FakeMachine(ws, "dsk-3", "DESKTOP789", tenantKey);
    await other.ConnectAsync();
    other.Send(new RequestCallMsg { ContactName = "mary  jones", Issue = "Her laptop" });
    await sue.WaitForDevice(other.Id, d => d.RequestedBy == "mary jones", 5000);
    sue.Send(new DeviceLinkMsg { DeviceId = other.Id, ClientId = acme, NewContactName = "mary jones" });
    var second = await sue.WaitForDevice(other.Id, d => d.ContactId > 0, 5000);
    int marys; await using (var db = Db()) marys = await db.Contacts.CountAsync(c => c.TenantId == tenantId && c.ClientId == acme && c.Name.ToLower() == "mary jones");
    Check("the same name at the same client again: the SAME contact, not a second", marys == 1 && second?.ContactId == linked?.ContactId,
        $"{marys} contacts, ids {linked?.ContactId} / {second?.ContactId}");
    sue.Send(new DeviceLinkMsg { DeviceId = pc.Id, ClientId = 0, NewContactName = "Mary Jones" });
    var refused = await sue.WaitFor<ErrorMsg>(e => e.Message.Contains("client"), 4000);
    Check("a new contact with no client is refused, in words", refused != null, refused?.Message ?? "no error");
    sue.Send(new DeviceLinkMsg { DeviceId = pc.Id, ClientId = 0, ContactId = 0 });
    var unlinked = await sue.WaitForDevice(pc.Id, d => d.ClientId == 0 && d.ContactId == 0, 5000);
    Check("unlinked again: back to the name it gave, marked again", unlinked?.OwnerName == "Mary Jones" && unlinked.NamedByRequest,
        unlinked == null ? "no update" : $"{unlinked.OwnerName} marked={unlinked.NamedByRequest}");

    // ================================================================ the portal
    Console.WriteLine();
    Console.WriteLine("== the portal ==");
    var portal = await PortalAsync("sue@local.test");
    var page = await portal.GetStringAsync("/portal/devices");
    Check("Devices shows the name they gave, marked", page.Contains("requested-by") && page.Contains(">Peter Pan</em>"));
    // The stylesheet sets every <em> upright, so the mark is the class's own rule — found walking the page.
    var css = await portal.GetStringAsync("/site.css");
    Check("...and the stylesheet makes it italic: <em> alone is upright on the portal", css.Contains(".requested-by { font-style: italic"));
    Check("...offers the person on file by that name, with their client", page.Contains("Peter Pan — Acme Ltd"));
    Check("...and adding that name as a new contact", page.Contains("add-requested") && page.Contains("name=\"name\" value=\"Peter Pan\""));
    var form = FormFor(page, "dsk-2", "add-requested");
    var token = Between(form ?? "", "name=\"__RequestVerificationToken\" type=\"hidden\" value=\"", "\"") ?? "";
    Check("...whose form carries its own antiforgery token (§80)", token.Length > 0);
    var post = await portal.PostAsync("/portal/devices?handler=AddContact", new FormUrlEncodedContent(new Dictionary<string, string>
        { ["deviceId"] = "dsk-2", ["clientId"] = zenith.ToString(), ["name"] = "Peter Pan", ["__RequestVerificationToken"] = token }));
    var answer = await post.Content.ReadAsStringAsync();
    Check("Add and link says what it did", answer.Contains("DESKTOP456 linked to Peter Pan at Zenith Motors"), Between(answer, "id=\"devicesMessage\">", "<") ?? Between(answer, "id=\"devicesError\">", "<") ?? "no message");
    var viaPortal = await sue.WaitForDevice("dsk-2", d => d.ClientName == "Zenith Motors" && d.ContactName == "Peter Pan", 5000);
    Check("...a NEW Peter Pan at Zenith, not Acme's — and the consoles are told", viaPortal != null && viaPortal.NamedByRequest == false);
    int pans; await using (var db = Db()) pans = await db.Contacts.CountAsync(c => c.TenantId == tenantId && c.Name == "Peter Pan");
    Check("...so there are two Peter Pans, one at each", pans == 2, $"{pans}");
    var home = await portal.GetStringAsync("/portal");
    Check("the dashboard's unassigned machines show the name too", home.Contains("requested-by") && home.Contains(">Mary Jones</em>"));

    // ================================================================ the backfill
    Console.WriteLine();
    Console.WriteLine("== machines that asked before: the migration's backfill ==");
    await using (var db = Db())
    {
        db.Devices.AddRange(new Device { TenantId = tenantId, DeviceId = "bf-1", MachineName = "OLD-1" }, new Device { TenantId = tenantId, DeviceId = "bf-2", MachineName = "OLD-2" });
        Ticket T(int n, string device, TicketSource src, string contact, int day) => new()
        {
            TenantId = tenantId, Number = 9000 + n, Subject = "old " + n, DeviceId = device, MachineName = device, Source = src, ContactName = contact,
            CreatedUtc = new DateTime(2026, 10, day, 10, 0, 0, DateTimeKind.Utc), UpdatedUtc = new DateTime(2026, 10, day, 10, 0, 0, DateTimeKind.Utc),
        };
        db.Tickets.AddRange(T(1, "bf-1", TicketSource.Call, "Old Name", 1), T(2, "bf-1", TicketSource.Call, " Newer Name ", 5),
            T(3, "bf-1", TicketSource.Manual, "Typed By Staff", 10), T(4, "bf-1", TicketSource.Call, "  ", 11),
            T(5, "bf-2", TicketSource.Manual, "Staff Only", 3));
        await db.SaveChangesAsync();
        Migration mig = HarnessDb.Postgres ? new RemoteSupport.Platform.Data.PgMigrations.RequestedByName() : new RemoteSupport.Platform.Data.Migrations.RequestedByName();
        mig.ActiveProvider = db.Database.ProviderName!;
        var sql = mig.UpOperations.OfType<SqlOperation>().Single().Sql;
        await db.Database.ExecuteSqlRawAsync(sql);
    }
    var bf1 = await DeviceAsync("bf-1"); var bf2 = await DeviceAsync("bf-2");
    Check("the latest support request that gave a name — not staff's ticket, not a blank one", bf1?.RequestedBy == "Newer Name", bf1?.RequestedBy ?? "no row");
    Check("...with that request's time", bf1?.RequestedByUtc is { } t5 && DateTime.SpecifyKind(t5, DateTimeKind.Utc) == new DateTime(2026, 10, 5, 10, 0, 0, DateTimeKind.Utc),
        bf1?.RequestedByUtc?.ToString("o") ?? "none");
    Check("a machine that never asked keeps nothing", bf2?.RequestedBy == "" && bf2.RequestedByUtc == null, bf2?.RequestedBy ?? "no row");
    Check("a machine that asked AFTER keeps its own newer name", (await DeviceAsync("dsk-1"))?.RequestedBy == "Mary Jones");

    sue.Dispose(); pc.Drop(); other.Drop(); portal.Dispose();
}

PlatformDb Db() => new(new DbContextOptionsBuilder<PlatformDb>().UseHarnessDb($"Data Source={Path.Combine(dataDir, "platform.db")};Pooling=False").Options);

async Task<Device?> DeviceAsync(string deviceId) { await using var db = Db(); return await db.Devices.AsNoTracking().FirstOrDefaultAsync(d => d.DeviceId == deviceId); }

async Task<HttpClient> PortalAsync(string email)
{
    var c = new HttpClient(new HttpClientHandler { CookieContainer = new System.Net.CookieContainer(), AllowAutoRedirect = true }) { BaseAddress = new Uri(http) };
    var login = await c.GetStringAsync("/login");
    var token = Between(login, "name=\"__RequestVerificationToken\" type=\"hidden\" value=\"", "\"") ?? "";
    await c.PostAsync("/login", new FormUrlEncodedContent(new Dictionary<string, string> { ["Email"] = email, ["Password"] = "localtest", ["__RequestVerificationToken"] = token }));
    return c;
}

/// <summary>The form of this class for this machine, from its opening tag to its close.</summary>
static string? FormFor(string page, string deviceId, string cls)
{
    int at = 0;
    while ((at = page.IndexOf($"class=\"cpick-form {cls}\"", at, StringComparison.Ordinal)) >= 0)
    {
        var start = page.LastIndexOf("<form", at, StringComparison.Ordinal);
        var end = page.IndexOf("</form>", at, StringComparison.Ordinal);
        if (start < 0 || end < 0) return null;
        var f = page[start..end];
        if (f.Contains($"name=\"deviceId\" value=\"{deviceId}\"")) return f;
        at = end;
    }
    return null;
}

static string? Between(string s, string a, string b)
{
    var i = s.IndexOf(a, StringComparison.Ordinal);
    if (i < 0) return null;
    i += a.Length;
    var j = s.IndexOf(b, i, StringComparison.Ordinal);
    return j < 0 ? null : s[i..j];
}

static (int code, string output) Run(string file, string argLine, string cwd, int ms, Dictionary<string, string>? env = null)
{
    var psi = new ProcessStartInfo(file, argLine) { WorkingDirectory = cwd, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
    if (env != null) foreach (var kv in env) psi.Environment[kv.Key] = kv.Value;
    using var p = Process.Start(psi)!;
    var o = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
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

/// <summary>A technician's console connection.</summary>
sealed class Tech : IDisposable
{
    private readonly ClientWebSocket _raw = new();
    private WsConnection _c = null!;
    public readonly ConcurrentQueue<Msg> Seen = new();

    public static async Task<Tech> OpenAsync(string http, string ws, string email)
    {
        using var hc = new HttpClient();
        var lr = await hc.PostAsJsonAsync($"{http}/api/login", new { email, password = "localtest" });
        var tok = JsonDocument.Parse(await lr.Content.ReadAsStringAsync()).RootElement.GetProperty("token").GetString()!;
        var t = new Tech();
        await t._raw.ConnectAsync(new Uri($"{ws}/ws/tech?token={tok}"), default);
        t._c = new WsConnection(t._raw);
        t._c.OnMessage += m => t.Seen.Enqueue(m);
        _ = t._c.RunAsync();
        await Task.Delay(600);
        return t;
    }

    public void Send(Msg m) => _c.Send(m);

    public async Task<T?> WaitFor<T>(Func<T, bool> f, int ms) where T : Msg
    { var e = DateTime.UtcNow.AddMilliseconds(ms); while (DateTime.UtcNow < e) { foreach (var m in Seen) if (m is T t && f(t)) return t; await Task.Delay(50); } return null; }

    /// <summary>A device as this console last heard of it — from its state or an update.</summary>
    public DeviceInfo? Latest(string id) => Seen.Reverse().Select(m => m switch
    {
        DeviceUpdateMsg u when u.Device.DeviceId == id => u.Device,
        StateMsg s => s.Devices.FirstOrDefault(d => d.DeviceId == id),
        _ => null
    }).FirstOrDefault(d => d != null);

    public async Task<DeviceInfo?> WaitForDevice(string id, Func<DeviceInfo, bool> f, int ms)
    {
        var e = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < e)
        {
            foreach (var m in Seen) if (m is DeviceUpdateMsg u && u.Device.DeviceId == id && f(u.Device)) return u.Device;
            await Task.Delay(50);
        }
        return null;
    }

    public void Dispose() { try { _raw.Abort(); } catch { } }
}

/// <summary>A machine's tray app connection.</summary>
sealed class FakeMachine
{
    private readonly string _ws, _tenantKey;
    public readonly string Id, Name;
    private ClientWebSocket? _raw;
    private WsConnection? _c;

    public FakeMachine(string ws, string id, string name, string tenantKey) { _ws = ws; Id = id; Name = name; _tenantKey = tenantKey; }

    /// <summary>Says hello; <paramref name="settle"/> false sends the next message at once, as a first install asking for
    /// help the moment it is up would.</summary>
    public async Task ConnectAsync(bool settle = true)
    {
        _raw = new ClientWebSocket();
        await _raw.ConnectAsync(new Uri($"{_ws}/ws/device"), default);
        _c = new WsConnection(_raw);
        _ = _c.RunAsync();
        _c.Send(new HelloMsg
        {
            Role = DeviceRole.Client, DeviceId = Id, DeviceKey = Id + "-key", MachineName = Name, UserName = "FakeUser",
            Os = "Windows 11 (pretend)", Version = "0.0-test", TenantKey = _tenantKey,
            Spec = new SystemSpec { Cpu = "Pretend CPU", Cores = 1, RamMb = 1024, DiskTotalGb = 10, DiskFreeGb = 5, Os = "Windows 11 (pretend)" }
        });
        if (settle) await Task.Delay(500);
    }

    public void Send(Msg m) => _c?.Send(m);
    public void Drop() { try { _raw?.Abort(); } catch { } }
}
