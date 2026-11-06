// =====================================================================================
// outsidercode - proves outside help, step 1 (roadmap 3.9, 2026-10-06): the switches and the
// customer's code.
//
//   "have the client generate a code, with the other company using a generic technician installer
//   that links to that code" ... "it should be able to be switched on and off, with off being
//   default, but can be toggled per machine"
//
// Three halves. The RULE: the company's switch is the default, a machine's own choice overrides it
// either way. The CODES, in-process: nine digits, ten minutes, once; a new one stops the old; only the
// hash is stored; five wrong guesses lock the GUESSER (a wrong code names no machine); two people
// typing the same code at once get one machine between them. The WIRE, on a platform of its own:
// the tray app told whether it may make a code; only a supervisor or the owner may change a machine;
// only the tray app may make or stop a code — never the agent; switching it off, for the machine or
// the company, stops the live code at once; the portal page is the owner's and supervisors'.
//
// Nothing here lets anybody connect — that is step 2.
//
//   dotnet run -c Debug --project tools/outsidercode/outsidercode.csproj
// =====================================================================================

using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using RemoteSupport.Platform.Data;
using RemoteSupport.Platform.Services;
using RemoteSupport.Shared;
using RemoteSupport.Tools;

int pass = 0, fail = 0;
void Check(string what, bool ok, string detail = "")
{
    if (ok) { pass++; Console.WriteLine($"  PASS  {what}{(detail.Length > 0 ? " — " + detail : "")}"); }
    else { fail++; Console.WriteLine($"  FAIL  {what}{(detail.Length > 0 ? " — " + detail : "")}"); }
}

var root = AppContext.BaseDirectory;
while (root != null && !File.Exists(Path.Combine(root, "CLAUDE.md"))) root = Path.GetDirectoryName(root);
if (root == null) { Console.WriteLine("Could not find the repository root."); return 1; }
var work = Path.Combine(Path.GetTempPath(), "rs-outsidercode-" + Guid.NewGuid().ToString("N")[..8]);
Directory.CreateDirectory(work);
const string Pw = "outsider-code-2026", Sue = "Sue Tech", Sam = "Sam Supervisor";
Process? server = null;
try
{
    // =========================================================== the rule
    Console.WriteLine("outsidercode - outside help, step 1: the switches and the customer's code");
    Console.WriteLine();
    Console.WriteLine("== the rule ==");
    Check("the company off, the machine as the company: NOT allowed — off is the default", !OutsiderAccess.Allowed(false, OutsiderAccess.AsCompany));
    Check("the company on, the machine as the company: allowed", OutsiderAccess.Allowed(true, OutsiderAccess.AsCompany));
    Check("the company off, but ON for this machine: allowed — \"toggled per machine\"", OutsiderAccess.Allowed(false, OutsiderAccess.On));
    Check("the company on, but OFF for this machine: not allowed", !OutsiderAccess.Allowed(true, OutsiderAccess.Off));
    Check("an unknown mode is read as \"as the company\", never as on", !OutsiderAccess.Allowed(false, 7) && OutsiderAccess.Normalise(-3) == OutsiderAccess.AsCompany);
    Check("a code reads in threes", OutsiderAccess.Format("012345678") == "012 345 678");
    Check("what is typed is read as digits only", OutsiderAccess.Clean(" 012 345-678 ") == "012345678");
    Check("who it is for must be said", OutsiderAccess.ForProblem("  ").Length > 0 && OutsiderAccess.ForProblem("Smith & Co") == "");
    Check("only a supervisor or the owner may switch it", Permissions.CanAllowOutsiders(TenantRole.Admin) && Permissions.CanAllowOutsiders(TenantRole.Owner)
        && !Permissions.CanAllowOutsiders(TenantRole.Technician) && !Permissions.CanAllowOutsiders(TenantRole.Billing));

    // =========================================================== the codes, in-process
    Console.WriteLine();
    Console.WriteLine("== the codes ==");
    var svcDir = Path.Combine(work, "service"); Directory.CreateDirectory(svcDir);
    var svcOpts = new DbContextOptionsBuilder<PlatformDb>().UseHarnessDb($"Data Source={Path.Combine(svcDir, "platform.db")};Pooling=False").Options;
    await using (var db = new PlatformDb(svcOpts)) await HarnessDb.MigrateAsync(db);
    var codes = new OutsiderCodeService(new Factory(svcOpts));
    async Task<OutsiderCode> Row(int id) { await using var db = new PlatformDb(svcOpts); return await db.OutsiderCodes.FirstAsync(c => c.Id == id); }
    async Task<List<OutsiderCode>> Rows() { await using var db = new PlatformDb(svcOpts); return await db.OutsiderCodes.OrderBy(c => c.Id).ToListAsync(); }

    Check("without saying who it is for, no code", (await codes.CreateAsync(1, "pc1", " ", "Ann at PC1")).Error.Length > 0);
    var (c1, exp1, e1) = await codes.CreateAsync(1, "pc1", " Smith & Co Accountants ", "Ann at PC1");
    Check("a code is nine digits", e1 == "" && c1.Length == 9 && c1.All(char.IsDigit), c1);
    Check("...and lasts ten minutes", Math.Abs((exp1 - DateTime.UtcNow - TimeSpan.FromMinutes(10)).TotalSeconds) < 5);
    var stored = (await Rows()).Last();
    Check("only its HASH is kept — the digits are nowhere in the row",
        stored.CodeHash == OutsiderCodeService.Hash(c1) && !JsonSerializer.Serialize(stored).Contains(c1) && stored.ForWhom == "Smith & Co Accountants");

    var (c2, _, _) = await codes.CreateAsync(1, "pc1", "Smith & Co Accountants", "Ann at PC1");
    var first = (await Rows()).First();
    Check("a new code stops the old one, and says why", first.EndedUtc != null && first.EndedWhy.Contains("replaced"), first.EndedWhy);
    Check("...the old one no longer works", (await codes.RedeemAsync(c1, "caller-a", "probe")).Hit == null);
    var (hit, err) = await codes.RedeemAsync(OutsiderAccess.Format(c2), "caller-a", "Jane at Smith & Co");
    Check("the new one works, typed with spaces, and names the machine and who it is for",
        hit is { TenantId: 1, DeviceId: "pc1", ForWhom: "Smith & Co Accountants" }, err);
    Check("...it is recorded as used, and by whom", (await Row(hit!.CodeId)) is { UsedBy: "Jane at Smith & Co" } r && r.UsedUtc != null);
    var again = await codes.RedeemAsync(c2, "caller-b", "probe");
    Check("it works ONCE", again.Hit == null, again.Error);
    Check("...with the same words as a code that never existed — a guesser learns nothing",
        again.Error == (await codes.RedeemAsync("111111111", "caller-b", "probe")).Error);

    var (c3, _, _) = await codes.CreateAsync(1, "pc2", "Other IT", "Bob at PC2");
    Check("stopping it works", await codes.CancelAsync(1, "pc2", "stopped at the machine") && (await codes.RedeemAsync(c3, "caller-c", "probe")).Hit == null);
    Check("...and nothing is left to stop", !await codes.CancelAsync(1, "pc2", "again") && await codes.LiveAsync(1, "pc2") == null);

    var (c4, _, _) = await codes.CreateAsync(1, "pc3", "Late Co", "Cy at PC3");
    await using (var db = new PlatformDb(svcOpts))
    {
        var row = await db.OutsiderCodes.OrderBy(c => c.Id).LastAsync();
        row.ExpiresUtc = DateTime.UtcNow.AddSeconds(-1);
        await db.SaveChangesAsync();
    }
    Check("after ten minutes it does not work", (await codes.RedeemAsync(c4, "caller-d", "probe")).Hit == null);

    var (c5, _, _) = await codes.CreateAsync(1, "pc4", "Guess Co", "Di at PC4");
    var wrong = c5 == "000000000" ? "000000001" : "000000000";
    for (int i = 0; i < OutsiderCodeService.MaxWrong; i++) await codes.RedeemAsync(wrong, "guesser", "probe");
    var locked = await codes.RedeemAsync(c5, "guesser", "probe");
    Check("five wrong codes lock the GUESSER — the right one included", locked.Hit == null && locked.Error.Contains("Too many"), locked.Error);
    for (int i = 0; i < 10; i++) await codes.RedeemAsync("", "blank-typer", "probe");
    Check("ten blanks are not guesses: someone else, and the blank-typer, still get in",
        (await codes.RedeemAsync(c5, "blank-typer", "probe")).Hit != null);

    var (c6, _, _) = await codes.CreateAsync(1, "pc5", "Race Co", "Ed at PC5");
    var race = await Task.WhenAll(Enumerable.Range(0, 6).Select(i => codes.RedeemAsync(c6, "racer-" + i, "racer " + i)));
    Check("six people typing the same code at once: exactly ONE gets the machine", race.Count(x => x.Hit != null) == 1,
        string.Join(", ", race.Select(x => x.Hit != null ? "in" : "no")));

    // =========================================================== the wire
    var dataDir = Path.Combine(work, "platform"); Directory.CreateDirectory(dataDir);
    var options = new DbContextOptionsBuilder<PlatformDb>().UseHarnessDb($"Data Source={Path.Combine(dataDir, "platform.db")};Pooling=False").Options;
    await using (var db = new PlatformDb(options)) await HarnessDb.MigrateAsync(db);
    var tenants = new TenantService(new Factory(options), new ConfigurationBuilder().Build(), dataDir, DataProtectionProvider.Create("outsidercode"));
    var (tenant, _) = await tenants.CreateAsync("Outside Help Co", "outside-help", 7);
    await tenants.AddTechnicianAsync(tenant!.Id, "sue@oh.test", Pw, Sue, TenantRole.Technician);
    await tenants.AddTechnicianAsync(tenant.Id, "sam@oh.test", Pw, Sam, TenantRole.Admin);
    var platformCodes = new OutsiderCodeService(new Factory(options));
    async Task<Device> Dev(string id) { await using var db = new PlatformDb(options); return await db.Devices.FirstAsync(d => d.DeviceId == id); }
    async Task<OutsiderCode> LastCode(string id) { await using var db = new PlatformDb(options); return await db.OutsiderCodes.Where(c => c.DeviceId == id).OrderBy(c => c.Id).LastAsync(); }

    var port = FreePort();
    var psi = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
    foreach (var a in new[] { "run", "-c", "Debug", "--project", Path.Combine(root, "src", "RemoteSupport.Platform", "RemoteSupport.Platform.csproj"), "--", "run" })
        psi.ArgumentList.Add(a);
    psi.Environment["RSP_Platform__DataDirectory"] = dataDir;
    HarnessDb.Use(psi.Environment, dataDir);
    psi.Environment["RSP_Security__RequireTwoFactor"] = "false";   // a throwaway platform: the password alone (HANDOVER §72)
    psi.Environment["RSP_Urls"] = $"http://127.0.0.1:{port}";
    psi.Environment["ASPNETCORE_URLS"] = $"http://127.0.0.1:{port}";
    server = Process.Start(psi)!;
    server.OutputDataReceived += (_, _) => { }; server.ErrorDataReceived += (_, _) => { };
    server.BeginOutputReadLine(); server.BeginErrorReadLine();
    if (!await WaitForPort(port, 90000)) { Console.WriteLine("The platform never started listening."); return 1; }

    Console.WriteLine();
    Console.WriteLine($"== the wire — a platform of its own on port {port} ==");
    var sue = await Console_(port, "sue@oh.test");
    var sam = await Console_(port, "sam@oh.test");
    var pc1 = new DeviceIdentity { DeviceId = "pc1-" + Guid.NewGuid().ToString("N")[..6], DeviceKey = Guid.NewGuid().ToString("N") };
    var pc2 = new DeviceIdentity { DeviceId = "pc2-" + Guid.NewGuid().ToString("N")[..6], DeviceKey = Guid.NewGuid().ToString("N") };
    var tray1 = await Device(port, pc1, DeviceRole.Client, tenant.TenantKey, "ANN-PC");
    var agent1 = await Device(port, pc1, DeviceRole.Agent, tenant.TenantKey, "ANN-PC");
    var tray2 = await Device(port, pc2, DeviceRole.Client, tenant.TenantKey, "BOB-PC");

    var st = await tray1.Next<OutsiderStateMsg>();
    Check("the tray app is told at once: NOT allowed — the company has it off by default", st is { Allowed: false });
    tray1.Ws.Send(new OutsiderCodeRequestMsg { For = "Smith & Co" });
    var refused = await tray1.Next<OutsiderCodeMsg>();
    Check("asking anyway: refused, in words, and no code", refused is { Code: "" } && refused.Error.Contains("switched off"), refused?.Error ?? "no answer");

    sue.Ws.Send(new DeviceOutsiderModeMsg { DeviceId = pc1.DeviceId, Mode = OutsiderAccess.On });
    var sueRefused = await sue.Next<ErrorMsg>();
    Check("a TECHNICIAN cannot switch it on for a machine — refused in words", sueRefused?.Message.Contains("supervisor") == true, sueRefused?.Message ?? "no answer");
    // The tray app is told its state twice when it connects, on purpose (§33: on registration, and again
    // once the stored record is read), so "nothing new" means never told it is ALLOWED.
    Check("...and the tray app is never told it is allowed", await tray1.NextWhere<OutsiderStateMsg>(s => s.Allowed, 1500) == null && (await Dev(pc1.DeviceId)).OutsiderMode == 0);

    sam.Ws.Send(new DeviceOutsiderModeMsg { DeviceId = pc1.DeviceId, Mode = OutsiderAccess.On });
    var on1 = await tray1.NextWhere<OutsiderStateMsg>(s => s.Allowed);
    Check("the SUPERVISOR switches it on for ANN-PC: its tray app is told at once", on1 is { Allowed: true, LiveFor: "" });
    var upd = await sam.NextWhere<DeviceUpdateMsg>(u => u.Device.DeviceId == pc1.DeviceId && u.Device.OutsiderMode == OutsiderAccess.On);
    Check("...every console's list says so", upd is { Device: { OutsiderAllowed: true, OutsiderCompanyOn: false } });
    Check("...it is stored", (await Dev(pc1.DeviceId)).OutsiderMode == OutsiderAccess.On);
    Check("...and BOB-PC, following the company, is still not allowed", await tray2.Next<OutsiderStateMsg>(1000) is null or { Allowed: false });

    agent1.Ws.Send(new OutsiderCodeRequestMsg { For = "Agent Co" });
    Check("the AGENT cannot make a code — only the person at the machine", await agent1.Next<OutsiderCodeMsg>(1500) == null && await platformCodes.LiveAsync(tenant.Id, pc1.DeviceId) == null);

    tray1.Ws.Send(new OutsiderCodeRequestMsg { For = "" });
    var noFor = await tray1.Next<OutsiderCodeMsg>();
    Check("without saying who it is for: refused", noFor is { Code: "" } && noFor.Error.Length > 0, noFor?.Error ?? "no answer");

    tray1.Ws.Send(new OutsiderCodeRequestMsg { For = "Smith & Co Accountants" });
    var made = await tray1.Next<OutsiderCodeMsg>();
    Check("the person at ANN-PC makes a code: nine digits, for Smith & Co, ten minutes",
        made is { Error: "", For: "Smith & Co Accountants" } && made.Code.Length == 9 && made.ExpiresUtc > DateTime.UtcNow.AddMinutes(9), made?.Error ?? "no answer");
    var live = await tray1.NextWhere<OutsiderStateMsg>(s => s.LiveFor.Length > 0);
    Check("...and the tray app is told one is live, for whom — never the digits again", live is { LiveFor: "Smith & Co Accountants" } && !JsonSerializer.Serialize(live).Contains(made!.Code));
    Check("...the record says who made it, at which machine", (await LastCode(pc1.DeviceId)).MadeBy.Contains("ANN-PC"));

    tray1.Ws.Send(new OutsiderCodeCancelMsg());
    Check("Stop this code: the tray app is told none is live", (await tray1.NextWhere<OutsiderStateMsg>(s => s.LiveFor == "")) != null);
    Check("...and it no longer works", (await platformCodes.RedeemAsync(made.Code, "probe-1", "probe")).Hit == null && (await LastCode(pc1.DeviceId)).EndedWhy.Contains("stopped"));

    tray1.Ws.Send(new OutsiderCodeRequestMsg { For = "Smith & Co Accountants" });
    var made2 = await tray1.Next<OutsiderCodeMsg>();
    sam.Ws.Send(new DeviceOutsiderModeMsg { DeviceId = pc1.DeviceId, Mode = OutsiderAccess.Off });
    var off1 = await tray1.NextWhere<OutsiderStateMsg>(s => !s.Allowed);
    Check("switched OFF for the machine while a code is live: the tray app is told", off1 is { Allowed: false, LiveFor: "" });
    Check("...and the code stopped at once, saying who switched it off",
        (await platformCodes.RedeemAsync(made2!.Code, "probe-2", "probe")).Hit == null && (await LastCode(pc1.DeviceId)).EndedWhy.Contains(Sam), (await LastCode(pc1.DeviceId)).EndedWhy);

    // ---- the company's switch, from the portal ----
    var samWeb = await Portal(port, "sam@oh.test");
    var page = await samWeb.GetStringAsync("/portal/outsiders");
    Check("the supervisor's portal has the Outside help page", page.Contains("<h1>Outside help</h1>") && page.Contains("ANN-PC"));
    var sueWeb = await Portal(port, "sue@oh.test");
    var suePage = await sueWeb.GetStringAsync("/portal/outsiders");
    Check("a technician's does not (the page's heading is asserted, not the status code — §34)", !suePage.Contains("<h1>Outside help</h1>"));

    var on = await PostForm(samWeb, "/portal/outsiders", "handler=Company", ("enabled", "true"));
    Check("the supervisor switches it on for the company", on.Contains("Switched on"));
    var on2 = await tray2.NextWhere<OutsiderStateMsg>(s => s.Allowed);
    Check("...BOB-PC, following the company, is told at once", on2 != null);
    Check("...ANN-PC, set OFF for itself, stays off", await tray1.NextWhere<OutsiderStateMsg>(s => s.Allowed, 1500) == null);
    Check("...and it is stored", (await tenants.ByIdAsync(tenant.Id))!.OutsidersAllowed);

    tray2.Ws.Send(new OutsiderCodeRequestMsg { For = "Ledger IT" });
    var made3 = await tray2.Next<OutsiderCodeMsg>();
    Check("the person at BOB-PC makes a code", made3 is { Error: "" } && made3.Code.Length == 9);
    var off = await PostForm(samWeb, "/portal/outsiders", "handler=Company");   // the tick unticked: nothing posted for it
    Check("the supervisor switches the company OFF", off.Contains("Switched off"));
    Check("...BOB-PC is told at once", await tray2.NextWhere<OutsiderStateMsg>(s => !s.Allowed) != null);
    Check("...and its live code stopped", (await platformCodes.RedeemAsync(made3!.Code, "probe-3", "probe")).Hit == null);
    var after = await samWeb.GetStringAsync("/portal/outsiders");
    Check("the page lists the codes made, and what became of each",
        after.Contains("Ledger IT") && after.Contains("Smith &amp; Co Accountants") && after.Contains("Stopped"));
}
catch (Exception ex) { fail++; Console.WriteLine("  FAIL  threw: " + ex); }
finally
{
    try { if (server is { HasExited: false }) server.Kill(true); } catch { }
    await Task.Delay(500);
    try { Directory.Delete(work, true); } catch { }
}

Console.WriteLine();
Console.WriteLine($"{pass} of {pass + fail} checks passed.");
return fail == 0 ? 0 : 1;

static async Task<Peer> Console_(int port, string email)
{
    using var hc = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
    var lr = await hc.PostAsJsonAsync($"http://127.0.0.1:{port}/api/login", new { email, password = "outsider-code-2026" });
    lr.EnsureSuccessStatusCode();
    var tok = JsonDocument.Parse(await lr.Content.ReadAsStringAsync()).RootElement.GetProperty("token").GetString()!;
    var p = await Peer.Open($"ws://127.0.0.1:{port}/ws/tech?token={Uri.EscapeDataString(tok)}");
    await Task.Delay(500); p.Clear();
    return p;
}

static async Task<Peer> Device(int port, DeviceIdentity id, DeviceRole role, string tenantKey, string machine)
{
    var p = await Peer.Open($"ws://127.0.0.1:{port}/ws/device");
    p.Ws.Send(new HelloMsg
    {
        Role = role, DeviceId = id.DeviceId, DeviceKey = id.DeviceKey, MachineName = machine,
        UserName = "probe", Os = "probe", Version = "1.0", TenantKey = tenantKey, Can = new List<string>()
    });
    await Task.Delay(400);
    return p;
}

/// <summary>Signed in through the portal's own form, antiforgery token and all.</summary>
static async Task<HttpClient> Portal(int port, string email)
{
    var c = new HttpClient(new HttpClientHandler { CookieContainer = new CookieContainer(), AllowAutoRedirect = true })
        { BaseAddress = new Uri($"http://127.0.0.1:{port}"), Timeout = TimeSpan.FromSeconds(30) };
    var page = await c.GetStringAsync("/login");
    await c.PostAsync("/login", new FormUrlEncodedContent(new Dictionary<string, string>
        { ["Email"] = email, ["Password"] = "outsider-code-2026", ["__RequestVerificationToken"] = Token(page) }));
    return c;
}

/// <summary>A form posted the way the page posts it: the token from THAT form (§80's lesson).</summary>
static async Task<string> PostForm(HttpClient c, string url, string formMarker, params (string k, string v)[] fields)
{
    var page = await c.GetStringAsync(url);
    var at = page.IndexOf(formMarker, StringComparison.Ordinal);
    var start = at < 0 ? -1 : page.LastIndexOf("<form", at, StringComparison.Ordinal);
    var end = at < 0 ? -1 : page.IndexOf("</form>", at, StringComparison.Ordinal);
    var form = start < 0 || end < 0 ? "" : page[start..end];
    var all = fields.Select(f => new KeyValuePair<string, string>(f.k, f.v)).Append(new("__RequestVerificationToken", Token(form)));
    var r = await c.PostAsync(url + "?" + formMarker, new FormUrlEncodedContent(all));
    return WebUtility.HtmlDecode(await r.Content.ReadAsStringAsync());
}

static string Token(string page)
{
    foreach (var a in new[] { "name=\"__RequestVerificationToken\" type=\"hidden\" value=\"", "name=\"__RequestVerificationToken\" value=\"" })
    {
        var i = page.IndexOf(a, StringComparison.Ordinal);
        if (i < 0) continue;
        i += a.Length;
        var j = page.IndexOf('"', i);
        if (j > i) return page[i..j];
    }
    return "";
}

static int FreePort() { var l = new TcpListener(IPAddress.Loopback, 0); l.Start(); var p = ((IPEndPoint)l.LocalEndpoint).Port; l.Stop(); return p; }

static async Task<bool> WaitForPort(int port, int ms)
{
    var until = DateTime.UtcNow.AddMilliseconds(ms);
    while (DateTime.UtcNow < until)
    {
        try { using var c = new TcpClient(); await c.ConnectAsync("127.0.0.1", port); return true; }
        catch { await Task.Delay(400); }
    }
    return false;
}

/// <summary>One socket and everything it has been sent, in order; each Next takes the oldest match.</summary>
file sealed class Peer
{
    public WsConnection Ws { get; private set; } = null!;
    private readonly List<Msg> _got = new();
    public void Clear() { lock (_got) _got.Clear(); }

    public static async Task<Peer> Open(string url)
    {
        var cws = new ClientWebSocket();
        await cws.ConnectAsync(new Uri(url), CancellationToken.None);
        var p = new Peer { Ws = new WsConnection(cws) };
        p.Ws.OnMessage += m => { lock (p._got) p._got.Add(m); };
        _ = p.Ws.RunAsync(CancellationToken.None);
        return p;
    }

    public Task<T?> Next<T>(int ms = 8000) where T : Msg => NextWhere<T>(_ => true, ms);

    public async Task<T?> NextWhere<T>(Func<T, bool> ok, int ms = 8000) where T : Msg
    {
        var until = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < until)
        {
            lock (_got)
            {
                var m = _got.OfType<T>().FirstOrDefault(ok);
                if (m != null) { _got.RemoveAll(x => ReferenceEquals(x, m) || (x is T t && !ok(t) && _got.IndexOf(x) < _got.IndexOf(m))); return m; }
            }
            await Task.Delay(40);
        }
        return null;
    }
}

file sealed class Factory : IDbContextFactory<PlatformDb>
{
    private readonly DbContextOptions<PlatformDb> _o;
    public Factory(DbContextOptions<PlatformDb> o) => _o = o;
    public PlatformDb CreateDbContext() => new(_o);
}
