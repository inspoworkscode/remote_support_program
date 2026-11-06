// =====================================================================================
// guestcheck - proves outside help, step 3 (roadmap 3.9, 2026-10-06): a GUEST — somebody with no
// account, Clint's accounting firm — connects with the guest console and the customer's code.
//
// The download: a guest console recognised by where it came from (/guest/console.exe) or its name,
// never taken for a company's console. The page, public. Then a real guest on /ws/guest against a
// platform of its own: a name is required; who they are is what they TYPED and the customer's prompt
// and the company's pop-up say "a guest — not checked"; their token opens ONLY the session their code
// started and dies when their guest connection closes; the allow-lists hold; no record is written for a
// company that does not exist; their leaving ends it; and guessing is limited by their address.
//
//   dotnet run -c Debug --project tools/guestcheck/guestcheck.csproj
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
var dataDir = Path.Combine(Path.GetTempPath(), "rs-guestcheck-" + Guid.NewGuid().ToString("N")[..8]);
Directory.CreateDirectory(dataDir);
const string Pw = "guest-check-2026", Sue = "Sue Tech", Sam = "Sam Supervisor", ACo = "Anywhere Test IT";
const string Jane = "Jane Smith", JaneCo = "Smith & Co Accountants";
string JaneLabel = $"{Jane} ({JaneCo}, a guest — not checked)";
Process? server = null;
try
{
    // =========================================================== the download
    Console.WriteLine("guestcheck - a guest with a code");
    Console.WriteLine();
    Console.WriteLine("== the download ==");
    Check("a guest console is known by where it was downloaded from",
        DownloadIdentity.GuestServerFromZoneIdentifier("[ZoneTransfer]\nZoneId=3\nHostUrl=https://theremotesupportprogram.com/guest/console.exe\n") == "https://theremotesupportprogram.com");
    Check("...and a company's console is NOT taken for one",
        DownloadIdentity.GuestServerFromZoneIdentifier("[ZoneTransfer]\nHostUrl=https://theremotesupportprogram.com/downloads/anywhere-it/technician.exe\n") == null);
    Check("...nor a file from anywhere else", DownloadIdentity.GuestServerFromZoneIdentifier("HostUrl=https://evil.example/guest/console.exe.zip") == null);
    Check("a guest console is known by its name, a browser's \" (2)\" allowed for",
        DownloadIdentity.IsGuestFileName(@"C:\Downloads\remote-support-guest.exe") && DownloadIdentity.IsGuestFileName("Remote-Support-Guest (2).exe"));
    Check("...and a company's console by name is not", !DownloadIdentity.IsGuestFileName("anywhere-it-console.exe") && !DownloadIdentity.IsGuestFileName("guest.exe"));
    Check("a guest must give a name; a company is optional", GuestRules.Problem("", "").Length > 0 && GuestRules.Problem("Jo", "") == "");
    Check("...and is described as not checked, either way",
        GuestRules.CompanyLabel(JaneCo) == "Smith & Co Accountants, a guest — not checked" && GuestRules.CompanyLabel(" ") == "a guest — not checked");

    // =========================================================== the platform
    var options = new DbContextOptionsBuilder<PlatformDb>().UseHarnessDb($"Data Source={Path.Combine(dataDir, "platform.db")};Pooling=False").Options;
    await using (var db = new PlatformDb(options)) await HarnessDb.MigrateAsync(db);
    var tenants = new TenantService(new Factory(options), new ConfigurationBuilder().Build(), dataDir, DataProtectionProvider.Create("guestcheck"));
    var (a, _) = await tenants.CreateAsync(ACo, "a-co", 7);
    await tenants.AddTechnicianAsync(a!.Id, "sue@a.test", Pw, Sue, TenantRole.Technician);
    await tenants.AddTechnicianAsync(a.Id, "sam@a.test", Pw, Sam, TenantRole.Admin);
    await tenants.UpdateBrandingAsync(a.Id, t => t.OutsidersAllowed = true);

    var port = FreePort();
    var psi = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
    foreach (var arg in new[] { "run", "-c", "Debug", "--project", Path.Combine(root, "src", "RemoteSupport.Platform", "RemoteSupport.Platform.csproj"), "--", "run" })
        psi.ArgumentList.Add(arg);
    psi.Environment["RSP_Platform__DataDirectory"] = dataDir;
    HarnessDb.Use(psi.Environment, dataDir);
    psi.Environment["RSP_Security__RequireTwoFactor"] = "false";   // a throwaway platform (HANDOVER §72)
    psi.Environment["RSP_Urls"] = $"http://127.0.0.1:{port}";
    psi.Environment["ASPNETCORE_URLS"] = $"http://127.0.0.1:{port}";
    server = Process.Start(psi)!;
    server.OutputDataReceived += (_, _) => { }; server.ErrorDataReceived += (_, _) => { };
    server.BeginOutputReadLine(); server.BeginErrorReadLine();
    if (!await WaitForPort(port, 90000)) { Console.WriteLine("The platform never started listening."); return 1; }

    Console.WriteLine();
    Console.WriteLine($"== the page — a platform of its own on port {port} ==");
    using (var web = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") })
    {
        var page = await web.GetAsync("/guest");
        var html = await page.Content.ReadAsStringAsync();
        Check("/guest is public — nobody signed in, the page itself (its heading asserted, §34)",
            page.IsSuccessStatusCode && html.Contains("Connect to a computer with a code"), $"{(int)page.StatusCode}");
        // Step 4: the browser viewer — public, linked from /guest, its script served.
        Check("/guest offers BOTH ways in: this browser, and the download", html.Contains("href=\"/guest/web\"") && html.Contains("Connect in this browser"));
        var webPage = await web.GetAsync("/guest/web");
        var webHtml = await webPage.Content.ReadAsStringAsync();
        Check("/guest/web is public: the form for name, company and code, and the picture",
            webPage.IsSuccessStatusCode && webHtml.Contains("id=\"gvForm\"") && webHtml.Contains("id=\"gvCode\"") && webHtml.Contains("id=\"gvScreen\""), $"{(int)webPage.StatusCode}");
        var js = await web.GetAsync("/guestview.js");
        var jsText = await js.Content.ReadAsStringAsync();
        Check("...and its script is served, speaking the guest socket and asking for H.264 or tiles",
            js.IsSuccessStatusCode && jsText.Contains("/ws/guest") && jsText.Contains("'h264', 'jpeg-tiles'") && jsText.Contains("preventScroll"));
        var exe = await web.GetAsync("/guest/console.exe");
        Check("/guest/console.exe is public too: here, with no console built, a plain 404 saying so — never a sign-in",
            exe.StatusCode == HttpStatusCode.NotFound && (await exe.Content.ReadAsStringAsync()).Contains("not available"), $"{(int)exe.StatusCode}");
    }

    var (sue, sueTok) = await Console_(port, "sue@a.test");
    var (sam, _) = await Console_(port, "sam@a.test");
    var ann = Id("ann"); var tray = await Device(port, ann, a.TenantKey, "ANN-PC");
    var bob = Id("bob"); var tray2 = await Device(port, bob, a.TenantKey, "BOB-PC");
    await Task.Delay(800);
    async Task<string> Code(Peer t, string forWhom)
    {
        t.Ws.Send(new OutsiderCodeRequestMsg { For = forWhom });
        return (await t.NextWhere<OutsiderCodeMsg>(m => m.Code.Length > 0))?.Code ?? "";
    }

    Console.WriteLine();
    Console.WriteLine("== a guest connects ==");
    var guest = await Peer.Open($"ws://127.0.0.1:{port}/ws/guest");
    var code1 = await Code(tray, "Smith & Co Accountants");
    guest.Ws.Send(new GuestConnectMsg { Code = code1, Name = " ", Company = JaneCo });
    Check("without a name: refused, in words", (await guest.Next<ErrorMsg>())?.Message.Contains("name") == true);
    Check("...and the code is not spent", (await CodeRow(options, ann.DeviceId))?.UsedUtc == null);
    tray.Clear(); sam.Clear();
    guest.Ws.Send(new GuestConnectMsg { Code = OutsiderAccess.Format(code1), Name = Jane, Company = JaneCo });
    var ticket = await guest.Next<GuestTicketMsg>();
    Check("with a name: the guest is given a token", ticket is { Token.Length: > 20 });
    var start = await tray.NextWhere<StartSessionMsg>(m => m.HostToken.Length > 0);
    Check("ANN-PC's app is told to host and ASK, naming the guest as NOT CHECKED",
        start is { AskConsent: true } && start.TechnicianName == JaneLabel && start.VisitorCompany == "Smith & Co Accountants, a guest — not checked",
        start == null ? "nothing" : $"'{start.TechnicianName}' / '{start.VisitorCompany}'");
    var pop = await sam.Next<OutsiderVisitMsg>();
    Check("A's consoles get the pop-up, saying the same", pop?.Visitor == JaneLabel, pop?.Visitor ?? "nothing");
    var mine = await guest.NextWhere<SessionReadyMsg>(m => m.Session.SessionId == start!.SessionId);
    Check("the guest's console hears of it as theirs: their bare name, visiting", mine is { Session: { StartedBy: Jane, Visiting: true } });
    var spent = await CodeRow(options, ann.DeviceId);
    Check("the code is spent, by the guest as described", spent?.UsedBy == JaneLabel, spent?.UsedBy ?? "none");

    var sid = start!.SessionId;
    var host = await Peer.Open($"ws://127.0.0.1:{port}/ws/session?role=host&id={sid}&token={start.HostToken}&consent=asking&secs=60");
    await Task.Delay(300);
    host.Ws.Send(new ConsentGrantedMsg());
    Check("the person allows it: the guest's console is told", await guest.NextWhere<SessionReadyMsg>(m => m.Session.HostConnected && !m.Session.AwaitingConsent) != null);
    host.Ws.Send(new MonitorsMsg { Monitors = new List<MonitorInfo> { new() { Index = 0, Width = 1920, Height = 1080, Primary = true } } });
    await Task.Delay(300);

    Console.WriteLine();
    Console.WriteLine("== what the guest's token opens ==");
    var view = await Peer.TryOpen($"ws://127.0.0.1:{port}/ws/session?role=viewer&id={sid}&token={ticket!.Token}");
    Check("the guest's token opens the session their code started", view != null && await view.Next<MonitorsMsg>() != null);
    Check("a made-up token opens nothing", await Peer.TryOpen($"ws://127.0.0.1:{port}/ws/session?role=viewer&id={sid}&token={new string('A', 48)}") == null);
    // A second guest, with a code of their own for another machine: their token is for THEIR session only.
    var guest2 = await Peer.Open($"ws://127.0.0.1:{port}/ws/guest");
    var code2 = await Code(tray2, "Other firm");
    tray2.Clear();
    guest2.Ws.Send(new GuestConnectMsg { Code = code2, Name = "Gary Guest", Company = "" });
    var ticket2 = await guest2.Next<GuestTicketMsg>();
    var start2 = await tray2.NextWhere<StartSessionMsg>(m => m.HostToken.Length > 0);
    Check("a second guest's token does NOT open the first guest's session", ticket2 != null && await Peer.TryOpen($"ws://127.0.0.1:{port}/ws/session?role=viewer&id={sid}&token={ticket2.Token}") == null);
    Check("...and a guest with no company is still described as not checked", start2?.VisitorCompany == "a guest — not checked", start2?.VisitorCompany ?? "nothing");
    var sueView = await Peer.TryOpen($"ws://127.0.0.1:{port}/ws/session?role=viewer&id={sid}&token={sueTok}");
    Check("A's technician can Watch", sueView != null);
    if (view == null) throw new Exception("cannot go on without the guest's viewer");

    Console.WriteLine();
    Console.WriteLine("== the same limits as another company's technician ==");
    host.Clear(); view.Clear();
    view.Ws.Send(new MouseMsg { Monitor = 0 });
    view.Ws.Send(new ScriptRunMsg { RunId = "x", Script = "whoami" });
    view.Ws.Send(new ClipboardMsg { Text = "x" });
    await Task.Delay(1000);
    Check("the guest's mouse reaches the machine; a script and the clipboard do not",
        host.Count<MouseMsg>() == 1 && host.Count<ScriptRunMsg>() == 0 && host.Count<ClipboardMsg>() == 0);
    host.Ws.Send(new ClipboardMsg { Text = "the customer's clipboard" });
    host.Ws.SendBinary(Bin.FileChunk("f", 0, new byte[] { 1 }));
    await Task.Delay(800);
    Check("...and the guest is not sent the customer's clipboard or a file", view.Count<ClipboardMsg>() == 0 && !view.BinaryKinds.Contains(BinKind.FileChunk));

    Console.WriteLine();
    Console.WriteLine("== the records ==");
    await using (var db = new PlatformDb(options))
    {
        var t = await db.Tickets.Where(x => x.TenantId == a.Id && x.DeviceId == ann.DeviceId).OrderBy(x => x.Id).LastOrDefaultAsync();
        Check("A's ticket names the guest as not checked, and does not put them on it",
            t != null && t.Subject.Contains("a guest — not checked") && !t.AssignedTo.Contains(Jane), t?.Subject ?? "none");
        Check("no record is written for a company that does not exist", !await db.Sessions.AnyAsync(x => x.TenantId == 0));
    }

    Console.WriteLine();
    Console.WriteLine("== closing the guest console takes the token with it ==");
    await guest.Ws.CloseAsync("guest console closed");
    await Task.Delay(600);
    Check("a NEW socket with the token is refused once the guest connection has closed",
        await Peer.TryOpen($"ws://127.0.0.1:{port}/ws/session?role=viewer&id={sid}&token={ticket.Token}") == null);
    sam.Clear();
    view.Ws.Send(new EndSessionMsg { SessionId = sid });
    var ended = await sueView!.Next<SessionEndedMsg>();
    Check("the guest ends it: it ends for A's watching technician too", ended?.Reason.Contains(Jane) == true, ended?.Reason ?? "nothing");

    Console.WriteLine();
    Console.WriteLine("== guessing ==");
    var guesser = await Peer.Open($"ws://127.0.0.1:{port}/ws/guest");
    var real = await Code(tray, "Real");
    var wrong = real == "000000000" ? "000000001" : "000000000";
    for (int i = 0; i < OutsiderCodeService.MaxWrong; i++)
    {
        guesser.Ws.Send(new GuestConnectMsg { Code = wrong, Name = "Guesser" });
        await guesser.Next<ErrorMsg>();
    }
    guesser.Ws.Send(new GuestConnectMsg { Code = real, Name = "Guesser" });
    var locked = await guesser.Next<ErrorMsg>();
    Check("five wrong codes from one address lock it — the right code included", locked?.Message.Contains("Too many") == true, locked?.Message ?? "nothing");
    Check("...and the real code is still unspent for whoever it was given to", (await CodeRow(options, ann.DeviceId))?.UsedUtc == null);
}
catch (Exception ex) { fail++; Console.WriteLine("  FAIL  threw: " + ex); }
finally
{
    try { if (server is { HasExited: false }) server.Kill(true); } catch { }
    await Task.Delay(500);
    try { Directory.Delete(dataDir, true); } catch { }
}

Console.WriteLine();
Console.WriteLine($"{pass} of {pass + fail} checks passed.");
return fail == 0 ? 0 : 1;

static async Task<OutsiderCode?> CodeRow(DbContextOptions<PlatformDb> o, string deviceId)
{
    await using var db = new PlatformDb(o);
    return await db.OutsiderCodes.Where(x => x.DeviceId == deviceId).OrderBy(x => x.Id).LastOrDefaultAsync();
}

static DeviceIdentity Id(string name) => new() { DeviceId = name + "-" + Guid.NewGuid().ToString("N")[..6], DeviceKey = Guid.NewGuid().ToString("N") };

static async Task<(Peer, string)> Console_(int port, string email)
{
    using var hc = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
    var lr = await hc.PostAsJsonAsync($"http://127.0.0.1:{port}/api/login", new { email, password = "guest-check-2026" });
    lr.EnsureSuccessStatusCode();
    var tok = JsonDocument.Parse(await lr.Content.ReadAsStringAsync()).RootElement.GetProperty("token").GetString()!;
    var p = await Peer.Open($"ws://127.0.0.1:{port}/ws/tech?token={Uri.EscapeDataString(tok)}");
    await Task.Delay(400); p.Clear();
    return (p, Uri.EscapeDataString(tok));
}

static async Task<Peer> Device(int port, DeviceIdentity id, string tenantKey, string machine)
{
    var p = await Peer.Open($"ws://127.0.0.1:{port}/ws/device");
    p.Ws.Send(new HelloMsg
    {
        Role = DeviceRole.Client, DeviceId = id.DeviceId, DeviceKey = id.DeviceKey, MachineName = machine,
        UserName = "probe", Os = "probe", Version = "1.0", TenantKey = tenantKey, Can = new List<string> { DeviceCan.AlwaysAsk }
    });
    return p;
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

/// <summary>One socket and everything it has been sent; each Next takes the oldest match.</summary>
file sealed class Peer
{
    public WsConnection Ws { get; private set; } = null!;
    private readonly List<Msg> _got = new();
    private readonly List<byte> _kinds = new();
    public void Clear() { lock (_got) { _got.Clear(); _kinds.Clear(); } }
    public int Count<T>() where T : Msg { lock (_got) return _got.OfType<T>().Count(); }
    public List<byte> BinaryKinds { get { lock (_got) return _kinds.ToList(); } }

    public static async Task<Peer> Open(string url) => await TryOpen(url) ?? throw new InvalidOperationException("could not open " + url);

    public static async Task<Peer?> TryOpen(string url)
    {
        try
        {
            var cws = new ClientWebSocket();
            await cws.ConnectAsync(new Uri(url), CancellationToken.None);
            var p = new Peer { Ws = new WsConnection(cws) };
            p.Ws.OnMessage += m => { lock (p._got) p._got.Add(m); };
            p.Ws.OnBinary += b => { lock (p._got) p._kinds.Add(b.Length > 0 ? b.Span[0] : (byte)0); };
            _ = p.Ws.RunAsync(CancellationToken.None);
            return p;
        }
        catch { return null; }
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
                if (m != null) { _got.Remove(m); return m; }
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
