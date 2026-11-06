using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RemoteSupport.Platform.Data;
using RemoteSupport.Shared;
using RemoteSupport.Tools;

// ---------------------------------------------------------------------------------------------
// wakecheck - Wake-on-LAN through another machine on the same network (roadmap 1.8, 2026-10-06).
//
// Clint's choice: right-click an offline machine, Wake up; the server asks ANOTHER of the same
// customer's machines on that network to send the wake-up — "via another of their PCs", nothing to
// set up on the customer's router.
//
// Part 1 is this PC: the magic packet's bytes, MACs and networks written the one way, this PC's own
// network cards against Get-NetAdapter, and the sender's packets — to every network it is on, both
// broadcasts, ports 9 and 7 — captured before they leave, then one real send.
//
// Part 2 runs its own platform on port 8131 against a throwaway database, with pretend machines that
// each claim an internet address through X-Forwarded-For (as Caddy sets it in production; the
// platform trusts it from loopback only). It asks: who is chosen to send, who is NEVER chosen (another
// customer's PC, another site, another network, an older client), what the sender is handed (MACs,
// nothing to aim at), who hears the answer, and what survives a server restart. The live server is
// never touched.
//
//   dotnet run -c Release --project tools/wakecheck/wakecheck.csproj
//   ... -- --local      part 1 only
// ---------------------------------------------------------------------------------------------

const int Port = 8131;
string http = $"http://127.0.0.1:{Port}", ws = $"ws://127.0.0.1:{Port}";

int pass = 0, fail = 0;
void Check(string what, bool ok, string detail = "")
{
    if (ok) pass++; else fail++;
    Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}  {what}{(detail.Length > 0 ? " — " + detail : "")}");
}
void Log(string s) => Console.WriteLine("        " + s);

Console.WriteLine("wakecheck — Wake-on-LAN through another machine on the same network");
Console.WriteLine();

// =============================================================================== part 1
Console.WriteLine("== part 1: this PC ==");
{
    var p = WakeOnLan.MagicPacket("00-11-22-AA-bb-cc");
    Check("the magic packet is 102 bytes", p.Length == 102, $"{p.Length}");
    Check("...six bytes of FF first", p.Take(6).All(b => b == 0xFF));
    var mac = new byte[] { 0x00, 0x11, 0x22, 0xAA, 0xBB, 0xCC };
    Check("...then the MAC sixteen times", Enumerable.Range(0, 16).All(r => p.Skip(6 + r * 6).Take(6).SequenceEqual(mac)));
    Check("a MAC is written one way", WakeOnLan.NormaliseMac("aa:bb:cc:dd:ee:ff") == "AABBCCDDEEFF" && WakeOnLan.NormaliseMac("AA-BB-CC-DD-EE-FF") == "AABBCCDDEEFF");
    Check("...and nonsense is not a MAC", WakeOnLan.NormaliseMac("hello") == null && WakeOnLan.NormaliseMac("00-00-00-00-00-00") == null
        && WakeOnLan.NormaliseMac("FF-FF-FF-FF-FF-FF") == null && WakeOnLan.NormaliseMac("AABBCCDDEE") == null);
    Check("a network is written one way", WakeOnLan.NetworkOf(IPAddress.Parse("192.168.1.37"), 24) == "192.168.1.0/24"
        && WakeOnLan.NetworkOf(IPAddress.Parse("10.1.2.3"), 8) == "10.0.0.0/8"
        && WakeOnLan.NetworkOf(IPAddress.Parse("172.16.25.4"), 20) == "172.16.16.0/20",
        WakeOnLan.NetworkOf(IPAddress.Parse("172.16.25.4"), 20));

    // This PC's cards against Windows' own answer, asked a different way.
    var cards = WakeOnLan.Cards();
    foreach (var c in cards) Log($"card {c.Mac} {(c.Wired ? "cable" : "Wi-Fi")} {string.Join(", ", c.Networks)}");
    var ps = Run("powershell", "-NoProfile -Command \"Get-NetAdapter -Physical | Where-Object { $_.InterfaceDescription -notmatch 'Bluetooth' } | ForEach-Object { ($_.MacAddress -replace '-','') + ' ' + $_.Status }\"", ".", 30000);
    var physical = ps.output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(l => l.Split(' ')[0].ToUpperInvariant()).Where(m => m.Length == 12).ToList();
    Log("Get-NetAdapter -Physical: " + string.Join(", ", physical));
    Check("every physical network card Windows lists is reported", physical.Count > 0 && physical.All(m => cards.Any(c => c.Mac == m)),
        $"{physical.Count(m => cards.Any(c => c.Mac == m))} of {physical.Count}");
    Check("...and nothing that is not one", cards.All(c => physical.Contains(c.Mac)), string.Join(", ", cards.Select(c => c.Mac).Except(physical)));
    var ipPs = Run("powershell", "-NoProfile -Command \"Get-NetIPAddress -AddressFamily IPv4 | Where-Object { $_.PrefixOrigin -ne 'WellKnown' -and $_.IPAddress -notlike '169.254*' } | ForEach-Object { $_.IPAddress + '/' + $_.PrefixLength }\"", ".", 30000);
    var connectedNets = ipPs.output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(l => l.Split('/')).Where(a => a.Length == 2 && IPAddress.TryParse(a[0], out _))
        .Select(a => WakeOnLan.NetworkOf(IPAddress.Parse(a[0]), int.Parse(a[1]))).ToList();
    var reported = cards.SelectMany(c => c.Networks).ToList();
    Check("the network this PC is on is reported with its card", reported.Count > 0 && reported.All(connectedNets.Contains),
        $"reported {string.Join(", ", reported)}; Windows: {string.Join(", ", connectedNets)}");

    // The sender, with its sockets replaced by a recorder.
    var sent = new List<(IPEndPoint From, IPEndPoint To, byte[] Packet)>();
    int n = WakeOnLan.Send(new[] { "00-11-22-33-44-55", "rubbish", "66:77:88:99:AA:BB" }, (f, t, pk) => sent.Add((f, t, pk)));
    var nets = WakeOnLan.LocalNetworks();
    foreach (var (local, bc) in nets) Log($"this PC is on {local}, broadcast {bc}");
    Check("a wake-up goes out of every network this PC is on", nets.All(x => sent.Any(s => s.From.Address.Equals(x.Local))), $"{sent.Select(s => s.From.Address).Distinct().Count()} of {nets.Count}");
    Check("...to the limited broadcast AND that network's own", nets.All(x => sent.Any(s => s.From.Address.Equals(x.Local) && s.To.Address.Equals(IPAddress.Broadcast))
        && sent.Any(s => s.From.Address.Equals(x.Local) && s.To.Address.Equals(x.Broadcast))));
    Check("...on ports 9 and 7", sent.Select(s => s.To.Port).Distinct().OrderBy(x => x).SequenceEqual(new[] { 7, 9 }));
    Check("...and only to broadcast addresses — never anywhere else", sent.All(s => s.To.Address.Equals(IPAddress.Broadcast) || nets.Any(x => x.Broadcast.Equals(s.To.Address))));
    Check("the rubbish MAC is dropped; both real ones are sent", sent.Select(s => Convert.ToHexString(s.Packet.Skip(6).Take(6).ToArray())).Distinct().OrderBy(x => x)
        .SequenceEqual(new[] { "001122334455", "66778899AABB" }), $"{n} packets");
    bool threw = false; try { WakeOnLan.Send(new[] { "nonsense" }, (_, _, _) => { }); } catch { threw = true; }
    Check("with no usable MAC it refuses rather than sending nothing quietly", threw);
    string realErr = ""; int real = 0;
    try { real = WakeOnLan.Send(new[] { "02-00-00-00-00-01" }); } catch (Exception ex) { realErr = ex.Message; }
    Check("a real send leaves this PC (a made-up, locally administered MAC — nothing here will wake)", real > 0, realErr.Length > 0 ? realErr : $"{real} packets");
}

if (args.Contains("--local"))
{
    Console.WriteLine();
    Console.WriteLine($"{pass} of {pass + fail} checks passed.");
    return fail == 0 ? 0 : 1;
}

// =============================================================================== part 2
Console.WriteLine();
Console.WriteLine("== part 2: the server, on a throwaway platform ==");
var repo = FindRepoRoot();
var work = Path.Combine(Path.GetTempPath(), "rs-wakecheck-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
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
};
HarnessDb.Use(env, dataDir);
string tenantKey = "";
foreach (var cmd in new[]
{
    "create-tenant \"Wake Check\" wake-check 5", "approve wake-check",
    "add-tech wake-check one@local.test localtest \"Tech One\" --admin",
    "add-tech wake-check two@local.test localtest \"Tech Two\"",
})
{
    var r = Run(exe, cmd, serverDir, 60000, env);
    if (r.code != 0) { Console.WriteLine(r.output); Console.WriteLine("CLI step failed: " + cmd); return 2; }
    var line = r.output.Split('\n').FirstOrDefault(l => l.StartsWith("Tenant key:"));
    if (line != null) tenantKey = line["Tenant key:".Length..].Trim();
}
if (tenantKey.Length == 0) { Console.WriteLine("could not read the tenant key"); return 2; }

// Two customers, so "the same customer's machine" can be told from another customer's in one building.
int acme, bravo;
var dbo = new DbContextOptionsBuilder<PlatformDb>().UseHarnessDb($"Data Source={Path.Combine(dataDir, "platform.db")};Pooling=False").Options;
await using (var db = new PlatformDb(dbo))
{
    var tid = db.Tenants.First().Id;
    var a = new Client { TenantId = tid, Name = "Acme Dental" }; var b2 = new Client { TenantId = tid, Name = "Bravo Books" };
    db.Clients.AddRange(a, b2); await db.SaveChangesAsync(); acme = a.Id; bravo = b2.Id;
}

Process? server = null;
async Task<bool> StartServer()
{
    var psi = new ProcessStartInfo(exe, $"run --urls {http}") { WorkingDirectory = serverDir, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
    foreach (var kv in env) psi.Environment[kv.Key] = kv.Value;
    server = Process.Start(psi)!;
    server.OutputDataReceived += (_, _) => { }; server.ErrorDataReceived += (_, _) => { };
    server.BeginOutputReadLine(); server.BeginErrorReadLine();
    using var probe = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
    for (int i = 0; i < 60; i++) { try { if ((await probe.GetAsync($"{http}/health")).IsSuccessStatusCode) return true; } catch { } await Task.Delay(500); }
    return false;
}
if (!await StartServer()) { Console.WriteLine("the platform did not come up"); try { server?.Kill(true); } catch { } return 2; }

try { await ChecksAsync(); }
catch (Exception ex) { fail++; Console.WriteLine("  FAIL  threw: " + ex); }
finally { try { server?.Kill(true); server?.WaitForExit(5000); } catch { } }

Console.WriteLine();
Console.WriteLine($"{pass} of {pass + fail} checks passed.");
Console.WriteLine($"server and database kept at {work}");
return fail == 0 ? 0 : 1;

async Task ChecksAsync()
{
    var one = await Tech.OpenAsync(http, ws, "one@local.test");
    var two = await Tech.OpenAsync(http, ws, "two@local.test");
    const string Office = "203.0.113.10", Elsewhere = "198.51.100.7";
    const string Lan = "192.168.1.0/24", OtherLan = "10.0.0.0/24";
    const string SleeperMac = "00D0B7AABB01";

    var sleeper = new Fake(ws, "w-sleeper", "SLEEPER-PC", tenantKey, DeviceRole.Client, Office,
        new() { new NetworkCard { Mac = "00-D0-B7-AA-BB-01", Wired = true, Networks = { Lan } }, new NetworkCard { Mac = "nonsense", Networks = { Lan } } }, wake: true);
    var oldPc = new Fake(ws, "w-old", "OLD-PC", tenantKey, DeviceRole.Client, Office, null, wake: false);
    await sleeper.ConnectAsync(); await oldPc.ConnectAsync();
    one.Link("w-sleeper", acme); one.Link("w-old", acme);
    await Task.Delay(800);

    Console.WriteLine();
    Console.WriteLine("== what a machine reports ==");
    Check("a machine that reported its card can be woken", await one.WaitDevice("w-sleeper", d => d.CanWake, 3000), one.Describe("w-sleeper"));
    Check("an older client that reported nothing cannot (the menu says so)", await one.WaitDevice("w-old", d => !d.CanWake, 3000), one.Describe("w-old"));
    var res = await one.WakeAsync("w-sleeper");
    Check("waking a machine that is on says it is on", res is { Sent: false } && res.Message.Contains("already on"), res?.Message ?? "no answer");

    sleeper.Drop(); oldPc.Drop();
    Check("it goes offline", await one.WaitDevice("w-sleeper", d => !d.Online, 3000));
    Check("...and can still be woken: the card is remembered", one.Latest("w-sleeper")?.CanWake == true);

    Console.WriteLine();
    Console.WriteLine("== nobody to send it ==");
    res = await one.WakeAsync("w-sleeper");
    Check("with no other machine there, it says nothing there can wake it", res is { Sent: false } && res.Message.Contains("nothing there can wake it"), res?.Message ?? "no answer");
    Check("...naming the customer", res?.Message.Contains("Acme Dental") == true);
    res = await one.WakeAsync("w-old");
    Check("an older client's machine: it says why it cannot be woken", res is { Sent: false } && res.Message.Contains("never told the server its network card"), res?.Message ?? "");

    Console.WriteLine();
    Console.WriteLine("== machines that must NOT be asked ==");
    var otherCustomer = new Fake(ws, "w-bravo", "BRAVO-PC", tenantKey, DeviceRole.Client, Office, new() { new NetworkCard { Mac = "00D0B7CC0001", Networks = { Lan } } }, wake: true);
    var otherSite = new Fake(ws, "w-far", "FAR-PC", tenantKey, DeviceRole.Client, Elsewhere, new() { new NetworkCard { Mac = "00D0B7CC0002", Networks = { Lan } } }, wake: true);
    var otherLan = new Fake(ws, "w-lan2", "LAN2-PC", tenantKey, DeviceRole.Client, Office, new() { new NetworkCard { Mac = "00D0B7CC0003", Networks = { OtherLan } } }, wake: true);
    var tooOld = new Fake(ws, "w-tooold", "TOOOLD-PC", tenantKey, DeviceRole.Client, Office, new() { new NetworkCard { Mac = "00D0B7CC0004", Networks = { Lan } } }, wake: false);
    foreach (var f in new[] { otherCustomer, otherSite, otherLan, tooOld }) await f.ConnectAsync();
    one.Link("w-bravo", bravo); one.Link("w-far", acme); one.Link("w-lan2", acme); one.Link("w-tooold", acme);
    await Task.Delay(800);
    res = await one.WakeAsync("w-sleeper");
    Check("still refused", res is { Sent: false }, res?.Message ?? "no answer");
    Check("another customer's PC on the same network was not asked (\"another of THEIR PCs\")", otherCustomer.WakeSends.Count == 0);
    Check("the customer's PC at another site (another internet address) was not asked", otherSite.WakeSends.Count == 0);
    Check("the customer's PC on another network at the same site was not asked", otherLan.WakeSends.Count == 0);
    Check("an older client that cannot send it was not asked", tooOld.WakeSends.Count == 0);

    Console.WriteLine();
    Console.WriteLine("== the customer's PC on the same network ==");
    var helper = new Fake(ws, "w-helper", "HELPER-PC", tenantKey, DeviceRole.Client, Office,
        new() { new NetworkCard { Mac = "00D0B7DD0001", Networks = { Lan, OtherLan } } }, wake: true) { Answer = (true, 4, "") };
    await helper.ConnectAsync(); one.Link("w-helper", acme); await Task.Delay(800);
    res = await one.WakeAsync("w-sleeper");
    Check("it is asked", helper.WakeSends.Count == 1, $"{helper.WakeSends.Count}");
    var asked = helper.WakeSends.LastOrDefault();
    Check("...with the sleeping machine's MAC and nothing else to aim at", asked != null && asked.Macs.SequenceEqual(new[] { SleeperMac }), asked == null ? "" : string.Join(",", asked.Macs));
    Check("...the nonsense card the machine once claimed is not passed on", asked?.Macs.Count == 1);
    Check("the technician is told it was sent, and from which machine", res is { Sent: true } && res.Message.Contains("HELPER-PC") && res.Message.Contains("SLEEPER-PC"), res?.Message ?? "no answer");
    Check("...and what to check if it does not wake (BIOS, Wi-Fi)", res?.Message.Contains("BIOS") == true);
    Check("the other technician hears nothing of it", await two.WaitFor<WakeResultMsg>(_ => true, 800) == null);
    foreach (var f in new[] { otherCustomer, otherSite, otherLan, tooOld })
        Check($"{f.Name} was still not asked", f.WakeSends.Count == 0);

    Console.WriteLine();
    Console.WriteLine("== the agent first ==");
    var helperAgent = new Fake(ws, "w-helper", "HELPER-PC", tenantKey, DeviceRole.Agent, Office,
        new() { new NetworkCard { Mac = "00D0B7DD0001", Networks = { Lan } } }, wake: true) { Answer = (true, 4, "") };
    await helperAgent.ConnectAsync(); await Task.Delay(500);
    int trayBefore = helper.WakeSends.Count;
    res = await one.WakeAsync("w-sleeper");
    Check("with the agent and the tray app both there, the agent sends it", helperAgent.WakeSends.Count == 1 && helper.WakeSends.Count == trayBefore,
        $"agent {helperAgent.WakeSends.Count}, tray app {helper.WakeSends.Count - trayBefore}");
    helperAgent.Drop(); await Task.Delay(500);

    Console.WriteLine();
    Console.WriteLine("== answers ==");
    helper.Answer = (false, 0, "this machine is not on any network it could send from");
    res = await one.WakeAsync("w-sleeper");
    Check("a sender that could not send: the technician is told why, in its words", res is { Sent: false } && res.Message.Contains("not on any network"), res?.Message ?? "");
    helper.Answer = null;   // says nothing
    var stray = new Fake(ws, "w-stray", "STRAY-PC", tenantKey, DeviceRole.Client, Office, null, wake: true);
    await stray.ConnectAsync();
    var mark = one.Seen.Count;
    one.Wake("w-sleeper");
    await WaitAsync(() => helper.WakeSends.Count > 0 && helper.WakeSends.Last().RequestId.Length > 0, 3000);
    var pending = helper.WakeSends.Last().RequestId;
    stray.Send(new WakeSentMsg { RequestId = pending, Sent = true, Packets = 99 });
    helper.Send(new WakeSentMsg { RequestId = "made-up", Sent = true });
    await Task.Delay(1000);
    Check("an answer from a machine that was NOT asked is ignored", !one.Seen.Skip(mark).OfType<WakeResultMsg>().Any(), "");
    var late = await one.WaitFor<WakeResultMsg>(r => !r.Sent && r.Message.Contains("did not answer"), 17000, mark);
    Check("a sender that never answers: after 15 s the technician is told so, not left waiting", late != null, late?.Message ?? "nothing");
    helper.Send(new WakeSentMsg { RequestId = pending, Sent = true });
    await Task.Delay(800);
    Check("...and its answer after that is dropped, not shown twice", one.Seen.Skip(mark).OfType<WakeResultMsg>().Count() == 1);
    helper.Answer = (true, 4, "");

    Console.WriteLine();
    Console.WriteLine("== a machine linked to nobody ==");
    var stray2 = new Fake(ws, "w-nobodys", "NOBODYS-PC", tenantKey, DeviceRole.Client, Office, new() { new NetworkCard { Mac = "00D0B7EE0001", Networks = { Lan } } }, wake: true);
    await stray2.ConnectAsync(); stray2.Drop(); await Task.Delay(500);
    int helperBefore = helper.WakeSends.Count;
    otherCustomer.Answer = (true, 4, "");
    res = await one.WakeAsync("w-nobodys");
    Check("may be woken by any of the company's machines at that place", res is { Sent: true }
        && (helper.WakeSends.Count == helperBefore + 1 || otherCustomer.WakeSends.Count == 1), res?.Message ?? "");

    Console.WriteLine();
    Console.WriteLine("== after a server restart ==");
    foreach (var f in new[] { otherCustomer, otherSite, otherLan, tooOld, helper, stray }) f.Drop();
    one.Dispose(); two.Dispose();
    try { server!.Kill(true); server.WaitForExit(5000); } catch { }
    if (!await StartServer()) { Check("the platform came back", false); return; }
    var again = await Tech.OpenAsync(http, ws, "one@local.test");
    Check("the sleeping machine can still be woken: its card was kept in the database", again.Latest("w-sleeper")?.CanWake == true, again.Describe("w-sleeper"));
    var helper2 = new Fake(ws, "w-helper", "HELPER-PC", tenantKey, DeviceRole.Client, Office,
        new() { new NetworkCard { Mac = "00D0B7DD0001", Networks = { Lan } } }, wake: true) { Answer = (true, 4, "") };
    await helper2.ConnectAsync(); await Task.Delay(500);
    res = await again.WakeAsync("w-sleeper");
    Check("...and is, through the same customer's PC", res is { Sent: true } && helper2.WakeSends.LastOrDefault()?.Macs.SequenceEqual(new[] { SleeperMac }) == true, res?.Message ?? "");
    helper2.Drop();

    Console.WriteLine();
    Console.WriteLine("== nothing logged ==");
    await using var db = new PlatformDb(dbo);
    Check("no ticket was opened by any of it", await db.Tickets.CountAsync() == 0);
    Check("no session was recorded", await db.Sessions.CountAsync() == 0);
    var row = await db.Devices.FirstAsync(d => d.DeviceId == "w-sleeper");
    Check("the database holds the clean card and the site's address", row.PublicAddress == Office && row.NetworkCardsJson.Contains(SleeperMac) && !row.NetworkCardsJson.Contains("nonsense"),
        $"{row.PublicAddress} {row.NetworkCardsJson}");
    again.Dispose();
}

static async Task<bool> WaitAsync(Func<bool> f, int ms)
{ var e = DateTime.UtcNow.AddMilliseconds(ms); while (DateTime.UtcNow < e) { if (f()) return true; await Task.Delay(50); } return f(); }

static (int code, string output) Run(string file, string argLine, string cwd, int ms, Dictionary<string, string>? env = null)
{
    var p0 = new ProcessStartInfo(file, argLine) { WorkingDirectory = cwd, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
    if (env != null) foreach (var kv in env) p0.Environment[kv.Key] = kv.Value;
    using var p = Process.Start(p0)!;
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
    private readonly ConcurrentDictionary<string, DeviceInfo> _devices = new();
    public readonly List<Msg> Seen = new();

    public static async Task<Tech> OpenAsync(string http, string ws, string email)
    {
        using var hc = new HttpClient();
        var lr = await hc.PostAsJsonAsync($"{http}/api/login", new { email, password = "localtest" });
        var tok = JsonDocument.Parse(await lr.Content.ReadAsStringAsync()).RootElement.GetProperty("token").GetString()!;
        var t = new Tech();
        await t._raw.ConnectAsync(new Uri($"{ws}/ws/tech?token={tok}"), default);
        t._c = new WsConnection(t._raw);
        t._c.OnMessage += m =>
        {
            lock (t.Seen) t.Seen.Add(m);
            if (m is StateMsg st) foreach (var d in st.Devices) t._devices[d.DeviceId] = d;
            if (m is DeviceUpdateMsg du) t._devices[du.Device.DeviceId] = du.Device;
        };
        _ = t._c.RunAsync();
        await Task.Delay(600);
        return t;
    }

    public DeviceInfo? Latest(string id) => _devices.TryGetValue(id, out var d) ? d : null;
    public string Describe(string id) => Latest(id) is { } d ? $"online={d.Online} canWake={d.CanWake}" : "not in the list";
    public void Link(string deviceId, int clientId) => _c.Send(new DeviceLinkMsg { DeviceId = deviceId, ClientId = clientId });
    public void Wake(string deviceId) => _c.Send(new WakeRequestMsg { DeviceId = deviceId });

    public async Task<WakeResultMsg?> WakeAsync(string deviceId)
    {
        int mark; lock (Seen) mark = Seen.Count;
        Wake(deviceId);
        return await WaitFor<WakeResultMsg>(r => r.DeviceId == deviceId, 5000, mark);
    }

    public async Task<bool> WaitDevice(string id, Func<DeviceInfo, bool> f, int ms)
    { var e = DateTime.UtcNow.AddMilliseconds(ms); while (DateTime.UtcNow < e) { if (Latest(id) is { } d && f(d)) return true; await Task.Delay(50); } return false; }

    public async Task<T?> WaitFor<T>(Func<T, bool> f, int ms, int from = 0) where T : Msg
    {
        var e = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < e)
        {
            List<Msg> now; lock (Seen) now = Seen.Skip(from).ToList();
            foreach (var m in now) if (m is T t && f(t)) return t;
            await Task.Delay(50);
        }
        return null;
    }

    public void Dispose() { try { _raw.Abort(); } catch { } }
}

/// <summary>
/// One of a machine's connections, claiming an internet address the way Caddy reports one
/// (X-Forwarded-For) and the cards it is told to. Asked to send a wake-up, it records what it was
/// handed and answers as told — or not at all, when <see cref="Answer"/> is null.
/// </summary>
sealed class Fake
{
    private readonly string _ws, _id, _tenantKey, _publicAddress;
    public readonly string Name;
    private readonly DeviceRole _role;
    private readonly List<NetworkCard>? _cards;
    private readonly bool _wake;
    private ClientWebSocket? _raw;
    private WsConnection? _c;
    public readonly List<WakeSendMsg> WakeSends = new();
    public (bool Sent, int Packets, string Error)? Answer;

    public Fake(string ws, string id, string name, string tenantKey, DeviceRole role, string publicAddress, List<NetworkCard>? cards, bool wake)
    { _ws = ws; _id = id; Name = name; _tenantKey = tenantKey; _role = role; _publicAddress = publicAddress; _cards = cards; _wake = wake; }

    public async Task ConnectAsync()
    {
        _raw = new ClientWebSocket();
        _raw.Options.SetRequestHeader("X-Forwarded-For", _publicAddress);
        await _raw.ConnectAsync(new Uri($"{_ws}/ws/device"), default);
        _c = new WsConnection(_raw);
        _c.OnMessage += m =>
        {
            if (m is WakeSendMsg w)
            {
                lock (WakeSends) WakeSends.Add(w);
                if (Answer is { } a) _c.Send(new WakeSentMsg { RequestId = w.RequestId, Sent = a.Sent, Packets = a.Packets, Error = a.Error });
            }
        };
        _ = _c.RunAsync();
        _c.Send(new HelloMsg
        {
            Role = _role, DeviceId = _id, DeviceKey = "k-" + _id, MachineName = Name, UserName = "FakeUser",
            Os = "Windows 11 (pretend)", Version = "0.0-test", TenantKey = _tenantKey,
            Spec = new SystemSpec { Cpu = "Pretend CPU", Cores = 1, RamMb = 1024, DiskTotalGb = 10, DiskFreeGb = 5, Os = "Windows 11 (pretend)" },
            Can = _wake ? new List<string> { DeviceCan.Wake } : new List<string>(),
            Cards = _cards
        });
        await Task.Delay(500);
    }

    public void Send(Msg m) => _c?.Send(m);
    public void Drop() { try { _raw?.Abort(); } catch { } }
}
